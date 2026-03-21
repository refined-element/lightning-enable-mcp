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
        """Return amount in satoshis (ceiling division to avoid sub-sat amounts rounding to 0)."""
        if self.amount_msat is not None:
            return -(-self.amount_msat // 1000)  # ceil division
        return None


@dataclass
class MppChallenge:
    """Parsed MPP (Machine Payments Protocol) challenge from WWW-Authenticate header.
    Per IETF draft-ryan-httpauth-payment. No macaroon — just invoice + preimage."""

    invoice: str
    amount: str | None = None
    realm: str | None = None
    amount_msat: int | None = None

    @property
    def amount_sats(self) -> int | None:
        """Return amount in satoshis (ceiling division to avoid sub-sat amounts rounding to 0)."""
        if self.amount_msat is not None:
            return -(-self.amount_msat // 1000)  # ceil division
        return None


@dataclass
class L402Token:
    """L402 authorization token (macaroon + preimage)."""

    macaroon: str
    preimage: str

    def to_header(self) -> str:
        """Format as Authorization header value."""
        return f"L402 {self.macaroon}:{self.preimage}"


@dataclass
class MppToken:
    """MPP authorization token (preimage only, no macaroon)."""

    preimage: str

    def to_header(self) -> str:
        """Format as Authorization header value."""
        return f'Payment method="lightning", preimage="{self.preimage}"'


class L402Client:
    """HTTP client with L402 payment support."""

    def __init__(self, wallet: "NWCWallet") -> None:
        """
        Initialize L402 client.

        Args:
            wallet: NWC wallet for paying invoices
        """
        self.wallet = wallet
        self._http_client = httpx.AsyncClient(
            timeout=30.0,
            headers={"Accept-Encoding": "identity"},
        )

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
        # Handle both L402 and legacy LSAT (case-insensitive per HTTP spec),
        # allowing any valid HTTP whitespace (SP / HTAB) and multiple characters.
        scheme_match = re.match(r'^\s*(L402|LSAT)\s+', www_authenticate, re.IGNORECASE)
        if not scheme_match:
            raise L402Error(f"Invalid L402 challenge: {www_authenticate[:50]}")

        # Extract macaroon (allow optional whitespace around '=' per HTTP auth-param OWS rules)
        macaroon_match = re.search(r'macaroon\s*=\s*"([^"]+)"', www_authenticate)
        if not macaroon_match:
            raise L402Error("Missing macaroon in L402 challenge")
        macaroon = macaroon_match.group(1)

        # Extract invoice (allow optional whitespace around '=' per HTTP auth-param OWS rules)
        invoice_match = re.search(r'invoice\s*=\s*"([^"]+)"', www_authenticate)
        if not invoice_match:
            raise L402Error("Missing invoice in L402 challenge")
        invoice = invoice_match.group(1)

        # Parse invoice to get amount
        amount_msat = self._get_invoice_amount_msat(invoice)

        return L402Challenge(macaroon=macaroon, invoice=invoice, amount_msat=amount_msat)

    def parse_mpp_challenge(self, www_authenticate: str) -> MppChallenge:
        """
        Parse WWW-Authenticate header for MPP (Payment) challenge.

        The header format is:
        Payment realm="<realm>", method="lightning", invoice="<bolt11>", amount="<amount>", currency="sat"

        Args:
            www_authenticate: WWW-Authenticate header value

        Returns:
            Parsed MppChallenge

        Raises:
            L402Error: If header cannot be parsed
        """
        www_authenticate = www_authenticate.strip()
        parts = www_authenticate.split(None, 1)
        if not parts or parts[0].lower() != "payment":
            raise L402Error(f"Invalid MPP challenge: {www_authenticate[:50]}")

        params_str = parts[1] if len(parts) > 1 else ""

        method_match = re.search(r'method\s*=\s*"([^"]+)"', params_str, re.IGNORECASE)
        if not method_match or method_match.group(1).lower() != "lightning":
            raise L402Error("MPP challenge method must be 'lightning'")

        invoice_match = re.search(r'invoice\s*=\s*"([^"]+)"', params_str, re.IGNORECASE)
        if not invoice_match:
            raise L402Error("Missing invoice in MPP challenge")
        invoice = invoice_match.group(1)

        amount_match = re.search(r'amount\s*=\s*"([^"]+)"', params_str, re.IGNORECASE)
        amount = amount_match.group(1) if amount_match else None

        realm_match = re.search(r'realm\s*=\s*"([^"]+)"', params_str, re.IGNORECASE)
        realm = realm_match.group(1) if realm_match else None

        amount_msat = self._get_invoice_amount_msat(invoice)

        return MppChallenge(invoice=invoice, amount=amount, realm=realm, amount_msat=amount_msat)

    def parse_best_challenge(self, www_authenticate: str) -> L402Challenge | MppChallenge:
        """
        Parse WWW-Authenticate header, trying L402 first then MPP.

        Prefers L402 when available (caveats, no cache dependency).
        Falls back to MPP only when L402 is not available.
        Handles comma-separated challenges in a single header value
        (e.g., "Payment ..., L402 ...") by delegating to _select_best_challenge.

        Args:
            www_authenticate: WWW-Authenticate header value

        Returns:
            Parsed L402Challenge or MppChallenge

        Raises:
            L402Error: If neither L402 nor MPP can be parsed
        """
        return self._select_best_challenge([www_authenticate])

    @staticmethod
    def _expand_challenges(www_auth_values: list[str]) -> list[str]:
        """
        Expand a list of WWW-Authenticate header values into individual challenges.

        A single header value may contain multiple challenges comma-separated, e.g.:
            'Payment method="lightning", invoice="...", L402 macaroon="...", invoice="..."'
        This splits on known auth scheme boundaries so each challenge is parsed
        individually.

        Args:
            www_auth_values: Raw WWW-Authenticate header values (may be comma-joined)

        Returns:
            List of individual challenge strings
        """
        # Pattern matches the start of a known auth scheme (case-insensitive).
        # We look for scheme names at the start of a segment or after a comma.
        scheme_boundary = re.compile(r"(?i)(?:^|,\s*)(?=(?:l402|lsat|payment)\s)", re.IGNORECASE)

        expanded: list[str] = []
        for value in www_auth_values:
            if not value or not value.strip():
                continue
            matches = list(scheme_boundary.finditer(value))
            if len(matches) <= 1:
                # Single challenge (or no recognized scheme) — keep as-is
                expanded.append(value.strip())
                continue
            # Multiple scheme boundaries found — split into segments
            for i, match in enumerate(matches):
                start = match.start()
                # Skip any leading comma/whitespace at the boundary
                while start < len(value) and value[start] in ", ":
                    start += 1
                end = matches[i + 1].start() if i + 1 < len(matches) else len(value)
                segment = value[start:end].strip().rstrip(",").strip()
                if segment:
                    expanded.append(segment)

        return expanded

    def _select_best_challenge(self, www_auth_values: list[str]) -> "L402Challenge | MppChallenge":
        """
        Select the best challenge from a list of WWW-Authenticate header values.

        Handles comma-separated challenges within a single header value by expanding
        them first. Prefers L402/LSAT over MPP.

        Args:
            www_auth_values: List of WWW-Authenticate header values

        Returns:
            Best available challenge (L402 preferred, MPP fallback)

        Raises:
            L402Error: If no valid challenge is found
        """
        # Expand comma-joined header values into individual challenges
        expanded = self._expand_challenges(www_auth_values)

        l402_challenge = None
        mpp_challenge = None

        for value in expanded:
            value = value.strip()
            if not value:
                continue
            # Try L402 first
            try:
                l402_challenge = self.parse_l402_challenge(value)
                # L402 is preferred — return immediately
                return l402_challenge
            except L402Error:
                pass
            # Try MPP
            try:
                if mpp_challenge is None:
                    mpp_challenge = self.parse_mpp_challenge(value)
            except L402Error:
                pass

        if mpp_challenge is not None:
            return mpp_challenge

        combined = "; ".join(v[:40] for v in www_auth_values)
        raise L402Error(f"No valid L402 or MPP challenge found in headers: {combined}")

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
            # Use get_list to properly handle multiple WWW-Authenticate headers
            # (httpx may comma-join them into a single string otherwise)
            www_auth_values = response.headers.get_list("WWW-Authenticate")
            if not www_auth_values:
                raise L402Error("402 response without WWW-Authenticate header")

            # Parse each header value separately, preferring L402 over MPP
            challenge = self._select_best_challenge(www_auth_values)

            # Reject no-amount invoices (security: could bypass budget checks)
            if challenge.amount_sats is None or challenge.amount_sats <= 0:
                raise L402Error(
                    "Invoice has no amount specified. For security, only invoices with explicit amounts are supported."
                )

            # Check budget
            if challenge.amount_sats > max_sats:
                raise L402BudgetExceededError(
                    f"Invoice amount {challenge.amount_sats} sats exceeds maximum {max_sats} sats"
                )

            # Pay invoice
            protocol = "MPP" if isinstance(challenge, MppChallenge) else "L402"
            logger.info(f"Paying {protocol} invoice for {challenge.amount_sats} sats")
            preimage = await self.wallet.pay_invoice(challenge.invoice)

            # Create token
            if isinstance(challenge, MppChallenge):
                token = MppToken(preimage=preimage)
            else:
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
        macaroon: str | None = None,
        max_sats: int = 1000,
    ) -> L402Token | MppToken:
        """
        Pay an L402/MPP invoice and return the authorization token.

        Args:
            invoice: BOLT11 invoice string
            macaroon: Base64-encoded macaroon (optional; if None, returns MPP token)
            max_sats: Maximum satoshis allowed

        Returns:
            L402Token (if macaroon provided) or MppToken (if no macaroon) for authorization

        Raises:
            L402Error: If invoice has no amount specified
            L402BudgetExceededError: If invoice exceeds max_sats
            L402PaymentError: If payment fails
        """
        # Check invoice amount — reject no-amount invoices (security: could bypass budget checks)
        amount_msat = self._get_invoice_amount_msat(invoice)
        if amount_msat is None or amount_msat <= 0:
            raise L402Error(
                "Invoice has no amount specified. For security, only invoices with explicit amounts are supported."
            )

        amount_sats = -(-amount_msat // 1000)  # ceil division: sub-sat amounts round up to 1
        if amount_sats > max_sats:
            raise L402BudgetExceededError(
                f"Invoice amount {amount_sats} sats exceeds maximum {max_sats} sats"
            )

        # Pay invoice
        try:
            preimage = await self.wallet.pay_invoice(invoice)
        except Exception as e:
            raise L402PaymentError(f"Payment failed: {e!s}") from e

        normalized_macaroon = macaroon.strip() if macaroon is not None else None
        if normalized_macaroon:
            return L402Token(macaroon=normalized_macaroon, preimage=preimage)
        return MppToken(preimage=preimage)
