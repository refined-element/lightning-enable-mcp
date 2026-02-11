"""
Nostr Wallet Connect (NWC) Client

Implements NIP-47 for Lightning wallet operations via Nostr.
"""

import asyncio
import hashlib
import json
import logging
import secrets
import time
from dataclasses import dataclass
from typing import Any
from urllib.parse import parse_qs, urlparse

import websockets
from websockets.client import WebSocketClientProtocol

logger = logging.getLogger("lightning-enable-mcp.nwc")


class NWCError(Exception):
    """Exception for NWC-related errors."""

    pass


class NWCConnectionError(NWCError):
    """Exception for connection failures."""

    pass


class NWCPaymentError(NWCError):
    """Exception for payment failures."""

    pass


@dataclass
class NWCConfig:
    """Parsed NWC connection configuration."""

    wallet_pubkey: str
    relay_url: str
    secret: str

    @classmethod
    def from_uri(cls, uri: str) -> "NWCConfig":
        """
        Parse a nostr+walletconnect:// URI.

        Format: nostr+walletconnect://<pubkey>?relay=<relay_url>&secret=<secret>
        """
        if not uri.startswith("nostr+walletconnect://"):
            raise ValueError("Invalid NWC URI: must start with nostr+walletconnect://")

        # Parse the URI
        parsed = urlparse(uri)

        # Extract pubkey from netloc
        wallet_pubkey = parsed.netloc
        if not wallet_pubkey:
            raise ValueError("Invalid NWC URI: missing wallet pubkey")

        # Parse query parameters
        params = parse_qs(parsed.query)

        relay_url = params.get("relay", [None])[0]
        if not relay_url:
            raise ValueError("Invalid NWC URI: missing relay parameter")

        secret = params.get("secret", [None])[0]
        if not secret:
            raise ValueError("Invalid NWC URI: missing secret parameter")

        return cls(wallet_pubkey=wallet_pubkey, relay_url=relay_url, secret=secret)


def _sha256(data: bytes) -> bytes:
    """Compute SHA256 hash."""
    return hashlib.sha256(data).digest()


def _compute_event_id(event: dict[str, Any]) -> str:
    """Compute Nostr event ID."""
    serialized = json.dumps(
        [
            0,
            event["pubkey"],
            event["created_at"],
            event["kind"],
            event["tags"],
            event["content"],
        ],
        separators=(",", ":"),
        ensure_ascii=False,
    )
    return _sha256(serialized.encode()).hex()


def _sign_event(event: dict[str, Any], secret_key: bytes) -> str:
    """
    Sign a Nostr event using secp256k1.

    Args:
        event: Event dict with id field set
        secret_key: 32-byte secret key

    Returns:
        Hex-encoded signature
    """
    try:
        from secp256k1 import PrivateKey

        privkey = PrivateKey(secret_key)
        event_id_bytes = bytes.fromhex(event["id"])
        sig = privkey.schnorr_sign(event_id_bytes, None, raw=True)
        return sig.hex()
    except ImportError:
        raise ImportError("secp256k1 library required for signing")


def _get_pubkey(secret_key: bytes) -> str:
    """
    Get public key from secret key.

    Args:
        secret_key: 32-byte secret key

    Returns:
        Hex-encoded public key (x-only, 32 bytes)
    """
    try:
        from secp256k1 import PrivateKey

        privkey = PrivateKey(secret_key)
        pubkey = privkey.pubkey.serialize()
        # Return x-only pubkey (skip the prefix byte)
        return pubkey[1:33].hex() if len(pubkey) == 33 else pubkey[:32].hex()
    except ImportError:
        raise ImportError("secp256k1 library required")


def _encrypt_content(plaintext: str, secret_key: bytes, recipient_pubkey: str) -> str:
    """
    Encrypt content using NIP-04 (shared secret + AES-256-CBC).

    Args:
        plaintext: Content to encrypt
        secret_key: Sender's 32-byte secret key
        recipient_pubkey: Recipient's hex-encoded public key

    Returns:
        Encrypted content in NIP-04 format: base64(ciphertext)?iv=base64(iv)
    """
    import base64
    import os
    from hashlib import sha256

    try:
        from secp256k1 import PrivateKey, PublicKey
        from Crypto.Cipher import AES
        from Crypto.Util.Padding import pad
    except ImportError:
        # Fallback to cryptography library
        from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
        from cryptography.hazmat.backends import default_backend
        from secp256k1 import PrivateKey, PublicKey

        # Compute shared secret
        privkey = PrivateKey(secret_key)
        recipient_bytes = bytes.fromhex(recipient_pubkey)
        # Add prefix for compressed pubkey
        if len(recipient_bytes) == 32:
            recipient_bytes = b"\x02" + recipient_bytes
        pubkey = PublicKey(recipient_bytes, raw=True)
        shared_point = pubkey.tweak_mul(secret_key)
        shared_secret = sha256(shared_point.serialize()[1:33]).digest()

        # Generate IV and encrypt
        iv = os.urandom(16)
        cipher = Cipher(algorithms.AES(shared_secret), modes.CBC(iv), backend=default_backend())
        encryptor = cipher.encryptor()

        # PKCS7 padding
        plaintext_bytes = plaintext.encode("utf-8")
        padding_len = 16 - (len(plaintext_bytes) % 16)
        padded = plaintext_bytes + bytes([padding_len] * padding_len)

        ciphertext = encryptor.update(padded) + encryptor.finalize()

        return f"{base64.b64encode(ciphertext).decode()}?iv={base64.b64encode(iv).decode()}"


def _decrypt_content(encrypted: str, secret_key: bytes, sender_pubkey: str) -> str:
    """
    Decrypt content using NIP-04.

    Args:
        encrypted: Encrypted content in NIP-04 format
        secret_key: Recipient's 32-byte secret key
        sender_pubkey: Sender's hex-encoded public key

    Returns:
        Decrypted plaintext
    """
    import base64
    from hashlib import sha256

    try:
        from secp256k1 import PrivateKey, PublicKey
        from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
        from cryptography.hazmat.backends import default_backend

        # Parse encrypted content
        parts = encrypted.split("?iv=")
        if len(parts) != 2:
            raise ValueError("Invalid NIP-04 encrypted content")

        ciphertext = base64.b64decode(parts[0])
        iv = base64.b64decode(parts[1])

        # Compute shared secret
        privkey = PrivateKey(secret_key)
        sender_bytes = bytes.fromhex(sender_pubkey)
        if len(sender_bytes) == 32:
            sender_bytes = b"\x02" + sender_bytes
        pubkey = PublicKey(sender_bytes, raw=True)
        shared_point = pubkey.tweak_mul(secret_key)
        shared_secret = sha256(shared_point.serialize()[1:33]).digest()

        # Decrypt
        cipher = Cipher(algorithms.AES(shared_secret), modes.CBC(iv), backend=default_backend())
        decryptor = cipher.decryptor()
        padded = decryptor.update(ciphertext) + decryptor.finalize()

        # Remove PKCS7 padding
        padding_len = padded[-1]
        plaintext = padded[:-padding_len]

        return plaintext.decode("utf-8")
    except ImportError as e:
        raise ImportError(f"Required library not available: {e}")


class NWCWallet:
    """Nostr Wallet Connect client for Lightning payments."""

    def __init__(self, connection_string: str) -> None:
        """
        Initialize NWC wallet.

        Args:
            connection_string: nostr+walletconnect:// URI
        """
        self.config = NWCConfig.from_uri(connection_string)
        self._secret_key = bytes.fromhex(self.config.secret)
        self._pubkey = _get_pubkey(self._secret_key)
        self._ws: WebSocketClientProtocol | None = None
        self._connected = False
        self._pending_requests: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self._response_task: asyncio.Task[None] | None = None

    async def connect(self) -> None:
        """Connect to the relay."""
        if self._connected:
            return

        try:
            self._ws = await websockets.connect(self.config.relay_url)
            self._connected = True
            self._response_task = asyncio.create_task(self._handle_responses())
            logger.info(f"Connected to NWC relay: {self.config.relay_url}")
        except Exception as e:
            raise NWCConnectionError(f"Failed to connect to relay: {e!s}") from e

    async def disconnect(self) -> None:
        """Disconnect from the relay."""
        if self._response_task:
            self._response_task.cancel()
            try:
                await self._response_task
            except asyncio.CancelledError:
                pass

        if self._ws:
            await self._ws.close()

        self._connected = False

    async def _handle_responses(self) -> None:
        """Handle incoming messages from the relay."""
        if not self._ws:
            return

        try:
            async for message in self._ws:
                try:
                    data = json.loads(message)
                    await self._process_message(data)
                except json.JSONDecodeError:
                    logger.warning(f"Invalid JSON from relay: {message[:100]}")
                except Exception as e:
                    logger.exception(f"Error processing message: {e}")
        except websockets.ConnectionClosed:
            logger.info("Relay connection closed")
            self._connected = False

    async def _process_message(self, data: list[Any]) -> None:
        """Process a Nostr message."""
        if not data or data[0] != "EVENT":
            return

        if len(data) < 3:
            return

        event = data[2]
        if event.get("kind") != 23195:  # NIP-47 response kind
            return

        # Check if this is for us
        p_tag = None
        e_tag = None
        for tag in event.get("tags", []):
            if tag[0] == "p" and tag[1] == self._pubkey:
                p_tag = tag[1]
            if tag[0] == "e":
                e_tag = tag[1]

        if not p_tag or not e_tag:
            return

        # Decrypt response
        try:
            content = _decrypt_content(
                event["content"], self._secret_key, self.config.wallet_pubkey
            )
            response = json.loads(content)

            # Resolve pending request
            if e_tag in self._pending_requests:
                self._pending_requests[e_tag].set_result(response)
        except Exception as e:
            logger.exception(f"Error decrypting response: {e}")

    async def _send_request(self, method: str, params: dict[str, Any]) -> dict[str, Any]:
        """
        Send an NWC request and wait for response.

        Args:
            method: NIP-47 method name
            params: Request parameters

        Returns:
            Response dict
        """
        if not self._connected or not self._ws:
            await self.connect()

        # Create request content
        request = {"method": method, "params": params}
        encrypted_content = _encrypt_content(
            json.dumps(request), self._secret_key, self.config.wallet_pubkey
        )

        # Create event
        event = {
            "kind": 23194,  # NIP-47 request kind
            "pubkey": self._pubkey,
            "created_at": int(time.time()),
            "tags": [["p", self.config.wallet_pubkey]],
            "content": encrypted_content,
        }

        # Sign event
        event["id"] = _compute_event_id(event)
        event["sig"] = _sign_event(event, self._secret_key)

        # Create future for response
        future: asyncio.Future[dict[str, Any]] = asyncio.Future()
        self._pending_requests[event["id"]] = future

        # Subscribe to responses
        sub_id = secrets.token_hex(8)
        sub_msg = json.dumps(
            [
                "REQ",
                sub_id,
                {
                    "kinds": [23195],
                    "#e": [event["id"]],
                    "#p": [self._pubkey],
                    "since": int(time.time()) - 10,
                },
            ]
        )
        await self._ws.send(sub_msg)

        # Send request
        await self._ws.send(json.dumps(["EVENT", event]))

        # Wait for response with timeout
        try:
            response = await asyncio.wait_for(future, timeout=60.0)
            return response
        except asyncio.TimeoutError:
            raise NWCError(f"Timeout waiting for {method} response")
        finally:
            del self._pending_requests[event["id"]]
            # Unsubscribe
            await self._ws.send(json.dumps(["CLOSE", sub_id]))

    async def pay_invoice(self, bolt11: str) -> str:
        """
        Pay a Lightning invoice.

        Args:
            bolt11: BOLT11 invoice string

        Returns:
            Payment preimage as hex string

        Raises:
            NWCPaymentError: If payment fails
        """
        response = await self._send_request("pay_invoice", {"invoice": bolt11})

        if response.get("error"):
            error = response["error"]
            raise NWCPaymentError(f"Payment failed: {error.get('message', error)}")

        result = response.get("result", {})
        preimage = result.get("preimage")

        if not preimage:
            raise NWCPaymentError("No preimage in payment response")

        # Validate preimage format - should be 64-char hex string
        # Some wallets may incorrectly return the invoice or other data
        if preimage.startswith(("lnbc", "lntb", "lnurl")):
            logger.error(f"NWC wallet returned invoice instead of preimage: {preimage[:50]}...")
            raise NWCPaymentError(
                "Wallet returned invoice instead of preimage. "
                "This may be a bug in your NWC wallet implementation."
            )

        # Normalize preimage - some wallets may include 0x prefix or spaces
        preimage = preimage.replace("0x", "").replace(" ", "").lower()

        # Validate it looks like hex
        if not all(c in "0123456789abcdef" for c in preimage):
            logger.error(f"Invalid preimage format: {preimage[:50]}...")
            raise NWCPaymentError(
                f"Invalid preimage format. Expected hex string, got: {preimage[:20]}..."
            )

        return preimage

    async def get_balance(self) -> int:
        """
        Get wallet balance.

        Returns:
            Balance in satoshis
        """
        response = await self._send_request("get_balance", {})

        if response.get("error"):
            error = response["error"]
            raise NWCError(f"Failed to get balance: {error.get('message', error)}")

        result = response.get("result", {})
        # Balance is in millisatoshis
        balance_msat = result.get("balance", 0)
        return balance_msat // 1000

    async def get_info(self) -> dict[str, Any]:
        """
        Get wallet info.

        Returns:
            Wallet info dict
        """
        response = await self._send_request("get_info", {})

        if response.get("error"):
            error = response["error"]
            raise NWCError(f"Failed to get info: {error.get('message', error)}")

        return response.get("result", {})
