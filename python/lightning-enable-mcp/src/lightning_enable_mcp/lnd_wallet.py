"""
LND Wallet Client

Implements Lightning wallet operations via LND's REST API.
Connects directly to user's own Lightning node - ALWAYS returns preimage.

This is the recommended wallet for L402 because:
1. User controls their own node
2. LND always returns preimage for payments
3. Direct node access = lowest latency

Configuration (environment variables or config file):
- LND_REST_HOST: LND REST API host (e.g., "localhost:8080" or "127.0.0.1:8080")
- LND_MACAROON_HEX: Admin macaroon in hex format (required for payments)
- LND_TLS_CERT_PATH: Path to the node's tls.cert (PEM or DER). Pins that certificate
  as the only trusted one, which is what LND's self-signed cert needs. Wins over
  LND_SKIP_TLS_VERIFY when both are set.
- LND_SKIP_TLS_VERIFY: Set to "true" to skip TLS verification (dev only)
- LND_PAYMENT_TIMEOUT_SECONDS: How long a payment may stay in flight before it is
  reported as pending (default 25, so a tool call stays well under 30s)
- LND_FEE_LIMIT_SATS: Fixed routing-fee ceiling per payment. Unset = 5% of the
  invoice amount, floored at MIN_FEE_LIMIT_SATS.

To get your macaroon in hex format:
- Linux/Mac: xxd -ps -c 1000 ~/.lnd/data/chain/bitcoin/mainnet/admin.macaroon
- Windows PowerShell: [System.BitConverter]::ToString(
      [System.IO.File]::ReadAllBytes("$env:USERPROFILE\\AppData\\Local\\Lnd\\data\\chain\\bitcoin\\mainnet\\admin.macaroon")
  ) -replace '-',''
"""

import asyncio
import base64
import binascii
import json
import logging
import math
import os
import ssl
from dataclasses import dataclass
from datetime import datetime, timezone
from decimal import Decimal
from typing import Any

from .wallet_errors import (
    PaymentPendingError,
    PaymentProofUnavailableError,
    PreimageUnavailableError,
    is_valid_preimage,
)

try:
    import httpx
except ImportError:
    httpx = None  # type: ignore

logger = logging.getLogger("lightning-enable-mcp.lnd")

# routerrpc SendPaymentV2 — the supported way to pay an invoice. The legacy
# lnrpc.SendPaymentSync route this client used to call
# (POST /v1/channels/transactions) has been REMOVED from LND: a modern node
# answers it with 404 {"code":5,"message":"Not Found"} and never creates a
# payment, so every payment failed with "LND API error (404)" while the node
# recorded no attempt at all. Verified against LND v0.21.3-beta.
ROUTER_SEND_PATH = "/v2/router/send"

# Kept only as a fallback for nodes old enough to lack routerrpc. A 404 proves the
# route was never reached, so nothing was submitted and the fallback cannot double-pay.
LEGACY_SEND_PATH = "channels/transactions"

# LND stops trying after `timeout_seconds`; the client-side bound below is the
# backstop for a stalled stream, so a payment can never block an agent indefinitely.
DEFAULT_PAYMENT_TIMEOUT_SECONDS = 25
STREAM_TIMEOUT_SLACK_SECONDS = 10

# SendPaymentV2 considers ONLY zero-fee routes when fee_limit_sat is left at its
# default of 0. A limit must always be sent. 5% mirrors lncli's default ceiling; the
# floor keeps tiny invoices (where 5% rounds to ~0) payable past a 1-sat base fee.
DEFAULT_FEE_LIMIT_PERCENT = 5
MIN_FEE_LIMIT_SATS = 2

# Payment states that end the SendPaymentV2 stream.
TERMINAL_PAYMENT_STATUSES = frozenset({"SUCCEEDED", "FAILED"})

# LND fills payment_preimage with 32 zero bytes when no proof exists. It is 64 valid
# hex characters, so a length/format check alone accepts it as a preimage.
ZERO_PREIMAGE_HEX = "0" * 64


def _positive_int_env(name: str, default: "int | None") -> "int | None":
    """Read a positive integer from the environment, falling back to ``default``."""
    raw = (os.getenv(name) or "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
    except ValueError:
        logger.warning("Ignoring %s: %r is not an integer", name, raw)
        return default
    if value <= 0:
        logger.warning("Ignoring %s: must be positive", name)
        return default
    return value


def _invoice_amount_sats(bolt11: str) -> int:
    """Best-effort invoice amount, used only to size the routing-fee ceiling."""
    try:
        from bolt11 import decode as _decode

        decoded = _decode((bolt11 or "").strip().lower())
        msat = getattr(decoded, "amount_msat", None)
        if msat:
            return -(-int(msat) // 1000)  # ceil; sub-sat rounds up to 1
        amount = getattr(decoded, "amount", None)
        return int(amount) if amount else 0
    except Exception:
        return 0


def _payment_hash_from_bolt11(bolt11: str) -> "str | None":
    """Public payment hash from the invoice, for reconciling an unresolved payment."""
    try:
        from bolt11 import decode as _decode

        return getattr(_decode((bolt11 or "").strip().lower()), "payment_hash", None)
    except Exception:
        return None


class LndError(Exception):
    """Exception for LND-related errors."""
    pass


class LndPaymentError(LndError):
    """Exception for payment failures."""
    pass


def build_tls_verify(
    skip_tls_verify: bool, tls_cert_path: str | None
) -> "bool | ssl.SSLContext":
    """Server-certificate policy for the LND REST client (the httpx ``verify`` value).

    Mirrors the .NET ``LndWalletService.BuildServerCertificateValidator``:

    - ``LND_TLS_CERT_PATH`` set: an ``SSLContext`` that trusts exactly that certificate
      (PEM or DER) and nothing from the system store, so LND's self-signed ``tls.cert``
      validates. Hostname checking is off because the pin already fixes the identity;
      LND's cert lists only the SANs it was generated with, and a node reached by
      another address would otherwise fail for no security gain.
    - ``LND_SKIP_TLS_VERIFY=true``: ``False`` (any certificate accepted) with a warning.
    - neither: ``True`` (default chain validation).

    Pinning wins over skip when both are set. An unreadable or malformed certificate
    raises ``LndError`` naming the variable, so a typo fails at startup instead of as an
    opaque ``CERTIFICATE_VERIFY_FAILED`` on the first request.
    """
    path = (tls_cert_path or "").strip()
    if path and not path.startswith("${"):
        try:
            with open(path, "rb") as fh:
                raw = fh.read()
            # PROTOCOL_TLS_CLIENT loads NO default CAs: the pinned file is the whole
            # trust store (create_default_context would add the system bundle).
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            context.check_hostname = False
            context.verify_mode = ssl.CERT_REQUIRED
            if raw.lstrip()[:32].startswith(b"-----BEGIN"):
                context.load_verify_locations(cadata=raw.decode("ascii"))
            else:
                context.load_verify_locations(cadata=raw)
        except (OSError, ssl.SSLError, ValueError, UnicodeDecodeError) as e:
            raise LndError(
                f"LND_TLS_CERT_PATH points at '{path}' but the certificate could not be "
                f"read: {e}. Set it to the node's tls.cert (PEM or DER), or unset it to "
                "use default TLS validation."
            ) from e
        # get_ca_certs() lists CA:TRUE anchors only. LND's autogenerated tls.cert is
        # CA:TRUE, so this catches an empty/garbage file; a hand-issued non-CA cert
        # would be reported as "no certificate found" rather than silently trusted.
        if not context.get_ca_certs(binary_form=True):
            raise LndError(
                f"LND_TLS_CERT_PATH points at '{path}' but the certificate could not be "
                "read: no certificate found in file. Set it to the node's tls.cert "
                "(PEM or DER), or unset it to use default TLS validation."
            )
        logger.info("[LND] Pinning TLS certificate from LND_TLS_CERT_PATH")
        return context

    if skip_tls_verify:
        logger.warning(
            "[LND] WARNING: LND_SKIP_TLS_VERIFY=true - TLS certificate verification is OFF"
        )
        return False

    return True


@dataclass
class LndConfig:
    """LND connection configuration."""
    rest_host: str
    macaroon_hex: str
    skip_tls_verify: bool = False
    tls_cert_path: str | None = None


@dataclass
class LndOnChainResult:
    """On-chain payment result from LND."""
    success: bool
    payment_id: str | None = None
    txid: str | None = None
    state: str | None = None
    amount_sats: int | None = None
    fee_sats: int | None = None
    error_code: str | None = None
    error_message: str | None = None

    @classmethod
    def succeeded(
        cls,
        payment_id: str,
        state: str,
        amount_sats: int,
        fee_sats: int = 0,
        txid: str | None = None,
    ) -> "LndOnChainResult":
        return cls(
            success=True,
            payment_id=payment_id,
            txid=txid,
            state=state,
            amount_sats=amount_sats,
            fee_sats=fee_sats,
        )

    @classmethod
    def failed(cls, code: str, message: str) -> "LndOnChainResult":
        return cls(success=False, error_code=code, error_message=message)


class LndWallet:
    """
    LND wallet client for Lightning payments via LND REST API.

    Provides the same core interface as NWCWallet, StrikeWallet, and OpenNodeWallet.
    LND always returns the preimage for outgoing payments, making it ideal for L402.

    LND REST API notes:
    - Numbers are returned as strings in JSON (e.g., "1000" not 1000)
    - Preimage and r_hash are returned as base64, must convert to hex
    - Auth via Grpc-Metadata-macaroon header with hex-encoded macaroon
    """

    def __init__(
        self,
        rest_host: str,
        macaroon_hex: str,
        skip_tls_verify: bool = False,
        tls_cert_path: str | None = None,
    ) -> None:
        """
        Initialize LND wallet.

        Args:
            rest_host: LND REST API host (e.g., "localhost:8080")
            macaroon_hex: Admin macaroon in hex format
            skip_tls_verify: Skip TLS certificate verification (dev only)
            tls_cert_path: Path to the node's tls.cert to pin (wins over skip_tls_verify)
        """
        if httpx is None:
            raise ImportError(
                "httpx is required for LndWallet. Install with: pip install httpx"
            )

        self.config = LndConfig(
            rest_host=rest_host,
            macaroon_hex=macaroon_hex,
            skip_tls_verify=skip_tls_verify,
            tls_cert_path=tls_cert_path,
        )
        self._client: httpx.AsyncClient | None = None
        self._connected = False
        self._payment_timeout_seconds = _positive_int_env(
            "LND_PAYMENT_TIMEOUT_SECONDS", DEFAULT_PAYMENT_TIMEOUT_SECONDS
        )
        # None = derive per invoice (percentage of the amount).
        self._fee_limit_sats_override = _positive_int_env("LND_FEE_LIMIT_SATS", None)

    @property
    def _base_host(self) -> str:
        """Scheme-normalized host, e.g. ``https://127.0.0.1:8080`` (no trailing slash).

        Derived rather than stored so it is correct even when ``connect()`` has not run
        (the client can be injected) and for the ``/v2`` routes, which sit outside the
        client's ``/v1/`` base URL.
        """
        host = self.config.rest_host or ""
        if not host.startswith("https://") and not host.startswith("http://"):
            host = f"https://{host}"
        return host.rstrip("/")

    @property
    def is_configured(self) -> bool:
        """Whether the wallet has valid configuration."""
        return bool(self.config.rest_host) and bool(self.config.macaroon_hex)

    @property
    def provider_name(self) -> str:
        """Return the provider name."""
        return "LND"

    async def connect(self) -> None:
        """Initialize the HTTP client and verify connection."""
        if self._connected:
            return

        # Determine base URL - add scheme if not present
        base_url = f"{self._base_host}/v1/"

        # Resolved here (not in __init__) so a bad LND_TLS_CERT_PATH surfaces as a
        # connection error, before any request, and never leaves a half-built client.
        verify = build_tls_verify(self.config.skip_tls_verify, self.config.tls_cert_path)

        self._client = httpx.AsyncClient(
            base_url=base_url,
            headers={
                "Grpc-Metadata-macaroon": self.config.macaroon_hex,
                "Content-Type": "application/json",
            },
            timeout=60.0,
            verify=verify,
        )

        self._connected = True
        logger.info(f"LND wallet connected to {self.config.rest_host}")

    async def disconnect(self) -> None:
        """Close the HTTP client."""
        if self._client:
            await self._client.aclose()
        self._connected = False

    async def _request(
        self,
        method: str,
        path: str,
        json_data: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        """
        Make an API request to LND REST API.

        Args:
            method: HTTP method (GET, POST)
            path: API path (relative to /v1/)
            json_data: Request body data

        Returns:
            Response data dict

        Raises:
            LndError: If the request fails
        """
        if not self._connected or not self._client:
            await self.connect()

        try:
            response = await self._client.request(
                method=method,
                url=path,
                json=json_data,
            )

            if response.status_code >= 400:
                error_text = response.text
                raise LndError(f"LND API error ({response.status_code}): {error_text}")

            if response.status_code == 204:
                return {}

            return response.json()

        except httpx.RequestError as e:
            raise LndError(f"Failed to connect to LND: {e!s}") from e

    async def pay_invoice(self, bolt11: str, amount_sats: int | None = None) -> str:
        """
        Pay a Lightning invoice via LND.

        LND ALWAYS returns the preimage - this is why it's ideal for L402.

        POST /v2/router/send (routerrpc SendPaymentV2), a server-streaming route whose
        frames are newline-delimited ``{"result": <lnrpc.Payment>}`` objects. The last
        frame carries the terminal status; on SUCCEEDED, ``payment_preimage`` is a hex
        string (NOT base64, unlike the old v1 route).

        The legacy ``POST /v1/channels/transactions`` (lnrpc.SendPaymentSync) has been
        REMOVED from LND and 404s on a modern node, so it is only tried when v2 itself
        is absent — see ROUTER_SEND_PATH.

        Args:
            bolt11: BOLT11 invoice string
            amount_sats: Unused - LND uses the invoice amount

        Returns:
            Payment preimage as hex string

        Raises:
            LndPaymentError: If the payment provably failed (retryable)
            PreimageUnavailableError: Settled, but no usable proof (terminal)
            PaymentPendingError: Accepted/in flight, outcome unknown (do NOT retry)
        """
        if not self.is_configured:
            raise LndPaymentError(
                "LND not configured. Set LND_REST_HOST and LND_MACAROON_HEX environment variables."
            )

        logger.info(f"Paying invoice via LND: {bolt11[:30]}...")

        try:
            payment = await self._router_send_payment(bolt11)

            if payment is None:
                # routerrpc is not served by this node. The 404 proves the request never
                # reached a payment RPC, so nothing was submitted and re-sending on the
                # legacy route cannot double-pay.
                logger.info(
                    "LND does not serve routerrpc SendPaymentV2; falling back to the legacy send route"
                )
                return await self._legacy_send_payment(bolt11)

            status = str(payment.get("status") or "").upper()

            if status == "FAILED":
                reason = payment.get("failure_reason") or "FAILED"
                logger.error("LND payment failed: %s", reason)
                raise LndPaymentError(f"Payment failed: {reason}")

            if status != "SUCCEEDED":
                # IN_FLIGHT / INITIATED / no frame at all. The payment was ACCEPTED, so
                # this is neither success nor failure: reporting it as a failure would
                # invite a retry that pays twice. Non-terminal by contract.
                raise PaymentPendingError(
                    "LND accepted the payment but has not settled it "
                    f"(status: {status or 'unknown'}). Do NOT retry it — check its "
                    "status on your node (lncli listpayments) before doing anything else.",
                    provider="lnd",
                    tracking_id=payment.get("payment_hash") or _payment_hash_from_bolt11(bolt11),
                    status=status or None,
                )

            # SUCCEEDED: v2 returns the preimage as a hex string, already decoded.
            return self._preimage_from_settled_hex(
                payment.get("payment_preimage"), payment.get("payment_hash")
            )

        except LndError:
            raise
        except PaymentProofUnavailableError:
            # Settled-but-unprovable and accepted-but-unsettled are NOT payment failures.
            # Must be re-raised before the generic handler below, which would otherwise
            # rewrap them as an LndPaymentError and tell the agent to retry a payment
            # that may already have taken the money.
            raise
        except Exception as e:
            raise LndPaymentError(f"Payment failed: {e!s}") from e

    async def _router_send_payment(self, bolt11: str) -> dict[str, Any] | None:
        """Pay via routerrpc SendPaymentV2.

        Returns the last ``lnrpc.Payment`` frame (``{}`` if the node sent none), or
        ``None`` when the route itself is absent (404) so the caller can fall back.

        The whole read is bounded: LND gives up after ``timeout_seconds`` and the
        client-side ``wait_for`` covers a stream that stalls without closing, so a
        payment can never block the calling agent indefinitely.
        """
        if not self._connected or not self._client:
            await self.connect()

        body = {
            "payment_request": bolt11,
            "timeout_seconds": self._payment_timeout_seconds,
            # LND REST takes 64-bit fields as strings.
            "fee_limit_sat": str(self._fee_limit_sats(bolt11)),
            # Only the terminal frame is of interest; skip the IN_FLIGHT chatter.
            "no_inflight_updates": True,
        }
        url = f"{self._base_host}{ROUTER_SEND_PATH}"

        try:
            return await asyncio.wait_for(
                self._read_router_send_stream(url, body, bolt11),
                timeout=self._payment_timeout_seconds + STREAM_TIMEOUT_SLACK_SECONDS,
            )
        except asyncio.TimeoutError as timeout_err:
            # The request WAS submitted, so this is pending, not failed. Bounded by
            # construction: the alternative (waiting forever on a stalled read) is the
            # hang this bound exists to prevent.
            raise PaymentPendingError(
                "LND did not report a final payment status within "
                f"{self._payment_timeout_seconds + STREAM_TIMEOUT_SLACK_SECONDS}s. The "
                "payment may still be in flight — do NOT retry it; check its status on "
                "your node (lncli listpayments).",
                provider="lnd",
                tracking_id=_payment_hash_from_bolt11(bolt11),
            ) from timeout_err
        except httpx.RequestError as e:
            raise LndError(f"Failed to connect to LND: {e!s}") from e

    async def _read_router_send_stream(
        self, url: str, body: dict[str, Any], bolt11: str
    ) -> dict[str, Any] | None:
        """Read the NDJSON payment stream. See ``_router_send_payment`` for the contract."""
        assert self._client is not None  # connect() ran in the caller
        last_payment: dict[str, Any] | None = None

        async with self._client.stream("POST", url, json=body) as response:
            if response.status_code == 404:
                await response.aread()
                return None
            if response.status_code >= 400:
                await response.aread()
                # Deliberately NOT retried on the legacy route: the route exists, so the
                # payment may have been submitted before the error.
                raise LndError(f"LND API error ({response.status_code}): {response.text}")

            # From here on the node has answered 2xx, so it may already have accepted
            # the payment. A transport failure while reading the body proves nothing
            # about the outcome: it MUST surface as pending (with the invoice hash for
            # reconciliation), never as the retryable connection error the caller maps
            # to "payment failed" - that invites a retry that pays twice.
            try:
                async for raw_line in response.aiter_lines():
                    line = raw_line.strip()
                    if not line:
                        continue
                    try:
                        frame = json.loads(line)
                    except ValueError:
                        continue  # skip a torn/partial frame rather than failing the payment
                    if not isinstance(frame, dict):
                        continue

                    error = frame.get("error")
                    if error:
                        message = error.get("message") if isinstance(error, dict) else str(error)
                        raise LndError(f"LND payment stream error: {message}")

                    result = frame.get("result")
                    if isinstance(result, dict):
                        last_payment = result
                        if str(result.get("status") or "").upper() in TERMINAL_PAYMENT_STATUSES:
                            break
            except (LndError, PaymentProofUnavailableError):
                raise
            except Exception as read_err:
                tracking_id = (
                    (last_payment or {}).get("payment_hash") or _payment_hash_from_bolt11(bolt11)
                )
                raise PaymentPendingError(
                    "Lost the connection to LND after it accepted the payment request "
                    f"({type(read_err).__name__}: {read_err!s}). The payment may have gone "
                    "through - do NOT retry it; check its status on your node "
                    "(lncli listpayments) before doing anything else.",
                    provider="lnd",
                    tracking_id=tracking_id,
                    status=str((last_payment or {}).get("status") or "") or None,
                ) from read_err

        return last_payment if last_payment is not None else {}

    async def _legacy_send_payment(self, bolt11: str) -> str:
        """Pay via the pre-routerrpc lnrpc.SendPaymentSync route (old nodes only)."""
        result = await self._request("POST", LEGACY_SEND_PATH, {"payment_request": bolt11})

        # Check for payment error
        payment_error = result.get("payment_error")
        if payment_error:
            logger.error(f"LND payment error: {payment_error}")
            raise LndPaymentError(f"Payment failed: {payment_error}")

        # The legacy route returns the preimage as base64 - convert to hex
        payment_preimage_b64 = result.get("payment_preimage")
        if payment_preimage_b64:
            try:
                preimage_bytes = base64.b64decode(payment_preimage_b64)
            except (binascii.Error, ValueError, TypeError) as decode_err:
                # LND reported no payment_error, so the payment SETTLED — the funds
                # are gone. A preimage that is not decodable base64 — or is the wrong
                # type entirely (a JSON number makes base64.b64decode raise TypeError,
                # not binascii.Error) — is settled-but-UNPROVABLE, not a payment
                # failure. Left to fall through, either error hits the generic
                # `except` in pay_invoice and becomes a RETRYABLE LndPaymentError —
                # inviting a double-pay. Raise the terminal PreimageUnavailableError
                # instead, matching the no-preimage and invalid-format cases.
                #
                # Deliberately does not echo the offending value (engineering
                # standard #5: never log preimage-position content).
                logger.error("LND returned a preimage that is not a decodable base64 string")
                raise PreimageUnavailableError(
                    "LND returned a preimage that is not a decodable base64 string. "
                    "The payment settled, but L402/MPP verification is not possible "
                    "without a real preimage.",
                    provider="lnd",
                    tracking_id=result.get("payment_hash"),
                ) from decode_err

            return self._preimage_from_settled_hex(
                preimage_bytes.hex(), result.get("payment_hash")
            )

        # LND reported no payment_error above, so the payment SETTLED — the funds
        # are gone. A missing/empty preimage does NOT mean the payment failed; it
        # means the payment is UNPROVABLE. Raising LndPaymentError here (the old
        # behavior) surfaces a settled payment as a failure, and the caller retries
        # and pays twice. Per the wallet_errors contract this is one of the two
        # states that are NOT "the payment failed" — the terminal, non-retryable
        # PreimageUnavailableError (mirrors the .NET SucceededWithoutPreimage
        # contract in Models/NwcConfig.cs). It is re-raised untouched by
        # pay_invoice's PaymentProofUnavailableError handler, not rewrapped.
        #
        # Deliberately does not echo any response content (engineering standard #5).
        logger.error("LND payment settled but no preimage was returned")
        raise PreimageUnavailableError(
            "The payment settled, but LND returned no preimage, so L402/MPP "
            "verification is not possible without a real preimage.",
            provider="lnd",
            tracking_id=result.get("payment_hash"),
        )

    def _preimage_from_settled_hex(
        self, preimage_hex: object, payment_hash: object
    ) -> str:
        """Gate for the one field L402 treats as proof of payment.

        The payment has SETTLED by the time this runs, so anything that is not a real
        preimage is settled-but-UNPROVABLE (terminal), never a payment failure — a
        failure would invite a retry that pays twice.
        """
        tracking_id = payment_hash if isinstance(payment_hash, str) and payment_hash else None

        # LND "always returns a preimage" in practice, but practice is not a guard:
        # anything that is not 32 bytes hex-encoded cannot be a preimage, and the
        # all-zero value is LND's explicit "no proof here" sentinel — it passes a
        # length/hex check, so it has to be rejected by name.
        candidate = preimage_hex.strip().lower() if isinstance(preimage_hex, str) else preimage_hex
        if not is_valid_preimage(candidate) or candidate == ZERO_PREIMAGE_HEX:
            # Deliberately does not echo the offending value (engineering standard #5:
            # never log preimage-position content).
            logger.error("LND returned a value that is not a valid preimage")
            raise PreimageUnavailableError(
                "LND returned a value that is not a valid 64-character hex preimage. "
                "The payment settled, but L402/MPP verification is not possible "
                "without a real preimage.",
                provider="lnd",
                tracking_id=tracking_id,
            )

        logger.info("LND payment succeeded, preimage received")
        return candidate  # type: ignore[return-value]

    def _fee_limit_sats(self, bolt11: str) -> int:
        """Routing-fee ceiling for one payment.

        Never 0: SendPaymentV2 reads a 0 limit as "consider only zero-fee routes", which
        silently fails most real payments.
        """
        if self._fee_limit_sats_override is not None:
            return self._fee_limit_sats_override

        amount_sats = _invoice_amount_sats(bolt11)
        if not amount_sats:
            return MIN_FEE_LIMIT_SATS
        return max(
            math.ceil(amount_sats * DEFAULT_FEE_LIMIT_PERCENT / 100), MIN_FEE_LIMIT_SATS
        )

    async def get_balance(self) -> int:
        """
        Get wallet Lightning channel balance in satoshis.

        GET /v1/balance/channels
        Response: {"local_balance": {"sat": "12345", "msat": "12345000"}, ...}

        Note: LND returns numbers as strings.

        Returns:
            Balance in satoshis
        """
        if not self.is_configured:
            raise LndError("LND not configured")

        try:
            result = await self._request("GET", "balance/channels")

            # local_balance.sat is spendable Lightning balance
            # LND returns numbers as strings
            local_balance = result.get("local_balance", {})
            balance_str = local_balance.get("sat", "0")
            balance_sats = int(balance_str)

            logger.info(f"LND balance: {balance_sats} sats")
            return balance_sats

        except LndError:
            raise
        except Exception as e:
            raise LndError(f"Failed to get LND balance: {e!s}") from e

    async def create_invoice(
        self,
        amount_sats: int,
        memo: str | None = None,
        expiry_secs: int = 3600,
    ) -> dict[str, Any]:
        """
        Create a Lightning invoice via LND.

        POST /v1/invoices
        Request: {"value": sats, "memo": "...", "expiry": secs}
        Response: {"r_hash": "<base64>", "payment_request": "lnbc..."}

        Args:
            amount_sats: Amount in satoshis
            memo: Optional invoice memo/description
            expiry_secs: Invoice expiry in seconds (default 3600)

        Returns:
            Dict with invoice_id (r_hash hex), bolt11 (payment_request), amount_sats, expires_at
        """
        if not self.is_configured:
            raise LndError(
                "LND not configured. Set LND_REST_HOST and LND_MACAROON_HEX environment variables."
            )

        logger.info(f"Creating LND invoice for {amount_sats} sats...")

        request_body: dict[str, Any] = {
            "value": str(amount_sats),
            "memo": memo or "Lightning payment",
            "expiry": str(expiry_secs),
        }

        result = await self._request("POST", "invoices", request_body)

        payment_request = result.get("payment_request")
        if not payment_request:
            raise LndError("No invoice returned from LND")

        # Convert r_hash from base64 to hex for invoice ID
        r_hash_b64 = result.get("r_hash", "")
        invoice_id = ""
        if r_hash_b64:
            r_hash_bytes = base64.b64decode(r_hash_b64)
            invoice_id = r_hash_bytes.hex()

        logger.info(f"LND invoice created: {invoice_id[:16]}...")

        return {
            "invoice_id": invoice_id,
            "bolt11": payment_request,
            "amount_sats": amount_sats,
            "expires_at": datetime.now(timezone.utc).timestamp() + expiry_secs,
        }

    async def get_invoice_status(self, invoice_id: str) -> dict[str, Any]:
        """
        Check the status of an invoice.

        GET /v1/invoice/{r_hash_hex}
        Response: {"state": "OPEN|SETTLED|CANCELED", "value": "1000", "settle_date": "1234567890"}

        Args:
            invoice_id: Invoice ID (r_hash in hex)

        Returns:
            Dict with id, state (PENDING/PAID/CANCELLED), amount_sats, settled_at
        """
        if not self.is_configured:
            raise LndError("LND not configured")

        result = await self._request("GET", f"invoice/{invoice_id}")

        # Map LND states to standard states
        lnd_state = (result.get("state") or "UNKNOWN").upper()
        state_map = {
            "OPEN": "PENDING",
            "SETTLED": "PAID",
            "CANCELED": "CANCELLED",
            "ACCEPTED": "PENDING",
        }
        state = state_map.get(lnd_state, lnd_state)

        # Parse amount (LND returns as string)
        value_str = result.get("value", "0")
        amount_sats = int(value_str) if value_str else 0

        # Parse settle date (LND returns as string unix timestamp)
        settled_at = None
        settle_date_str = result.get("settle_date")
        if settle_date_str:
            settle_ts = int(settle_date_str)
            if settle_ts > 0:
                settled_at = datetime.fromtimestamp(settle_ts, tz=timezone.utc).isoformat()

        return {
            "id": invoice_id,
            "state": state,
            "is_paid": state == "PAID",
            "is_pending": state == "PENDING",
            "amount_sats": amount_sats,
            "settled_at": settled_at,
        }

    async def send_onchain(self, address: str, amount_sats: int) -> LndOnChainResult:
        """
        Send an on-chain Bitcoin payment via LND.

        POST /v1/transactions
        Request: {"addr": address, "amount": sats, "target_conf": 6}
        Response: {"txid": "..."}

        Args:
            address: Bitcoin address (e.g., bc1q...)
            amount_sats: Amount in satoshis

        Returns:
            LndOnChainResult with payment details
        """
        if not self.is_configured:
            return LndOnChainResult.failed("NOT_CONFIGURED", "LND not configured")

        if not address:
            return LndOnChainResult.failed("INVALID_ADDRESS", "Bitcoin address is required")

        if amount_sats <= 0:
            return LndOnChainResult.failed("INVALID_AMOUNT", "Amount must be positive")

        try:
            logger.info(f"LND sending {amount_sats} sats on-chain to {address}...")

            request_body = {
                "addr": address,
                "amount": str(amount_sats),
                "target_conf": 6,  # Target 6 confirmations (~1 hour)
            }

            result = await self._request("POST", "transactions", request_body)
            txid = result.get("txid", "")

            logger.info(f"LND on-chain tx sent: {txid}")

            return LndOnChainResult.succeeded(
                payment_id=txid or "",
                txid=txid,
                state="PENDING",
                amount_sats=amount_sats,
                fee_sats=0,  # Fee will be in the tx details
            )

        except LndError as e:
            return LndOnChainResult.failed("API_ERROR", str(e))
        except Exception as e:
            return LndOnChainResult.failed("EXCEPTION", str(e))

    async def get_all_balances(self) -> dict[str, Any]:
        """
        Get all balances - LND is BTC-only (Lightning channels).

        Returns a dict with success, balances list, etc. matching the
        pattern used by StrikeWallet.get_all_balances().
        """
        try:
            balance_sats = await self.get_balance()
            balance_btc = Decimal(balance_sats) / Decimal("100000000")

            return {
                "success": True,
                "balances": [
                    {
                        "currency": "BTC",
                        "available": float(balance_btc),
                        "total": float(balance_btc),
                        "pending": 0,
                        "formatted": f"{balance_btc:.8f} BTC ({balance_sats:,} sats)",
                    }
                ],
                "provider": "LND",
                "message": f"Retrieved BTC balance from LND node ({balance_sats:,} sats)",
            }

        except Exception as e:
            return {
                "success": False,
                "error_code": "ERROR",
                "error_message": str(e),
            }

    async def get_info(self) -> dict[str, Any]:
        """
        Get wallet/node info.

        Returns:
            Dict with node info
        """
        try:
            result = await self._request("GET", "getinfo")
            return {
                "type": "lnd",
                "alias": result.get("alias"),
                "identity_pubkey": result.get("identity_pubkey"),
                "num_active_channels": result.get("num_active_channels"),
                "num_peers": result.get("num_peers"),
                "block_height": result.get("block_height"),
                "synced_to_chain": result.get("synced_to_chain"),
                "version": result.get("version"),
                "status": "connected",
                "preimage_support": True,
                "l402_compatible": True,
            }
        except Exception:
            return {
                "type": "lnd",
                "status": "connected" if self._connected else "disconnected",
                "preimage_support": True,
                "l402_compatible": True,
                "note": "LND always returns preimage. L402 fully supported.",
            }
