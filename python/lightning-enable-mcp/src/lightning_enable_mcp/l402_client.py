"""
L402 Client

Handles L402 protocol for HTTP requests with automatic payment.
"""

import base64
import logging
import re
from dataclasses import dataclass
from typing import TYPE_CHECKING

import httpx
from bolt11 import decode as decode_bolt11

if TYPE_CHECKING:
    from .nwc_wallet import NWCWallet

logger = logging.getLogger("lightning-enable-mcp.l402")


class L402Error(Exception):
    """Exception for L402-related errors."""

    pass


class L402PaymentError(L402Error):
    """Exception for payment failures."""

    pass


class L402BudgetExceededError(L402Error):
    """Exception when payment would exceed budget."""

    pass


@dataclass
class L402Challenge:
    """Parsed L402 challenge from WWW-Authenticate header."""

    macaroon: str
    invoice: str
    amount_msat: int | None = None

    @property
    def amount_sats(self) -> int | None:
        """Return amount in satoshis."""
        if self.amount_msat is not None:
            return self.amount_msat // 1000
        return None


@dataclass
class L402Token:
    """L402 authorization token (macaroon + preimage)."""

    macaroon: str
    preimage: str

    def to_header(self) -> str:
        """Format as Authorization header value."""
        return f"L402 {self.macaroon}:{self.preimage}"


class L402Client:
    """HTTP client with L402 payment support."""

    def __init__(self, wallet: "NWCWallet") -> None:
        """
        Initialize L402 client.

        Args:
            wallet: NWC wallet for paying invoices
        """
        self.wallet = wallet
        self._http_client = httpx.AsyncClient(timeout=30.0)

    async def close(self) -> None:
        """Close the HTTP client."""
        await self._http_client.aclose()

    def parse_l402_challenge(self, www_authenticate: str) -> L402Challenge:
        """
        Parse WWW-Authenticate header for L402 challenge.

        The header format is:
        L402 macaroon="<base64>", invoice="<bolt11>"

        or legacy LSAT format:
        LSAT macaroon="<base64>", invoice="<bolt11>"

        Args:
            www_authenticate: WWW-Authenticate header value

        Returns:
            Parsed L402Challenge

        Raises:
            L402Error: If header cannot be parsed
        """
        # Handle both L402 and legacy LSAT
        if not www_authenticate.startswith(("L402 ", "LSAT ")):
            raise L402Error(f"Invalid L402 challenge: {www_authenticate[:50]}")

        # Extract macaroon
        macaroon_match = re.search(r'macaroon="([^"]+)"', www_authenticate)
        if not macaroon_match:
            raise L402Error("Missing macaroon in L402 challenge")
        macaroon = macaroon_match.group(1)

        # Extract invoice
        invoice_match = re.search(r'invoice="([^"]+)"', www_authenticate)
        if not invoice_match:
            raise L402Error("Missing invoice in L402 challenge")
        invoice = invoice_match.group(1)

        # Parse invoice to get amount
        amount_msat = self._get_invoice_amount_msat(invoice)

        return L402Challenge(macaroon=macaroon, invoice=invoice, amount_msat=amount_msat)

    def _get_invoice_amount_msat(self, bolt11: str) -> int | None:
        """
        Extract amount in millisatoshis from a BOLT11 invoice.

        Args:
            bolt11: BOLT11 invoice string

        Returns:
            Amount in millisatoshis, or None if not specified
        """
        try:
            decoded = decode_bolt11(bolt11)
            if hasattr(decoded, "amount_msat") and decoded.amount_msat:
                return decoded.amount_msat
            # Some libraries use amount in satoshis
            if hasattr(decoded, "amount") and decoded.amount:
                return decoded.amount * 1000
            return None
        except Exception as e:
            logger.warning(f"Failed to decode invoice: {e}")
            return None

    async def fetch(
        self,
        url: str,
        method: str = "GET",
        headers: dict[str, str] | None = None,
        body: str | None = None,
        max_sats: int = 1000,
    ) -> tuple[str, int | None]:
        """
        Fetch a URL with automatic L402 payment handling.

        Args:
            url: URL to fetch
            method: HTTP method
            headers: Additional request headers
            body: Request body
            max_sats: Maximum satoshis to pay

        Returns:
            Tuple of (response text, amount paid in sats or None)

        Raises:
            L402Error: If L402 flow fails
            L402BudgetExceededError: If invoice exceeds max_sats
        """
        headers = headers or {}
        content = body.encode() if body else None

        # Initial request
        response = await self._http_client.request(
            method=method, url=url, headers=headers, content=content
        )

        # Check for L402 challenge
        if response.status_code == 402:
            www_auth = response.headers.get("WWW-Authenticate")
            if not www_auth:
                raise L402Error("402 response without WWW-Authenticate header")

            # Parse challenge
            challenge = self.parse_l402_challenge(www_auth)

            # Check budget
            if challenge.amount_sats is not None and challenge.amount_sats > max_sats:
                raise L402BudgetExceededError(
                    f"Invoice amount {challenge.amount_sats} sats exceeds maximum {max_sats} sats"
                )

            # Pay invoice
            logger.info(f"Paying L402 invoice for {challenge.amount_sats} sats")
            preimage = await self.wallet.pay_invoice(challenge.invoice)

            # Create token
            token = L402Token(macaroon=challenge.macaroon, preimage=preimage)

            # Retry with authorization
            auth_headers = {**headers, "Authorization": token.to_header()}
            response = await self._http_client.request(
                method=method, url=url, headers=auth_headers, content=content
            )

            if response.status_code >= 400:
                raise L402Error(
                    f"Request failed after payment: {response.status_code} {response.text[:200]}"
                )

            return response.text, challenge.amount_sats

        # Handle other error responses
        if response.status_code >= 400:
            raise L402Error(f"Request failed: {response.status_code} {response.text[:200]}")

        return response.text, None

    async def pay_challenge(
        self,
        invoice: str,
        macaroon: str,
        max_sats: int = 1000,
    ) -> L402Token:
        """
        Pay an L402 invoice and return the authorization token.

        Args:
            invoice: BOLT11 invoice string
            macaroon: Base64-encoded macaroon
            max_sats: Maximum satoshis allowed

        Returns:
            L402Token for authorization

        Raises:
            L402BudgetExceededError: If invoice exceeds max_sats
            L402PaymentError: If payment fails
        """
        # Check invoice amount
        amount_msat = self._get_invoice_amount_msat(invoice)
        if amount_msat is not None:
            amount_sats = amount_msat // 1000
            if amount_sats > max_sats:
                raise L402BudgetExceededError(
                    f"Invoice amount {amount_sats} sats exceeds maximum {max_sats} sats"
                )

        # Pay invoice
        try:
            preimage = await self.wallet.pay_invoice(invoice)
        except Exception as e:
            raise L402PaymentError(f"Payment failed: {e!s}") from e

        return L402Token(macaroon=macaroon, preimage=preimage)
