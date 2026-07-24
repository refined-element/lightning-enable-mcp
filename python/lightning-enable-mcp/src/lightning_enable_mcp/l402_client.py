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

from ._redirect import resolve_redirect_location
from ._url_redact import redact_url_for_display
from .wallet_errors import PaymentProofUnavailableError

if TYPE_CHECKING:
    from .budget_service import BudgetService
    from .nwc_wallet import NWCWallet
    from .payment_history_service import PaymentHistoryService

logger = logging.getLogger("lightning-enable-mcp.l402")


class L402Error(Exception):
    """Exception for L402-related errors."""

    pass


class L402RedirectError(L402Error):
    """Raised when a fetch hits an unfollowed 3xx redirect.

    Redirects are deliberately NOT followed (httpx ``follow_redirects=False``). Parity
    with the .NET port, which reverted an ``AllowAutoRedirect=true`` experiment: following
    a redirect would (1) re-send agent-supplied custom headers (X-Api-Key, Cookie, ...) to
    a cross-origin target, and (2) on the L402 path, pay a provider that host-redirects
    before its 402 and then loses its ``Authorization: L402`` header on the host change —
    pay, receive nothing. Instead the caller surfaces ``location`` as an actionable
    "call this tool again with that URL" result. ``amount_paid`` is attached when a
    payment already settled before the redirect (paid retry), so the caller still records
    the real spend, and ``l402_token`` carries the paid credential so a well-behaved agent
    retries the redirect target WITH the token instead of paying again.

    KNOWN LIMITATION (same-host paid redirect): a redirect on the paid retry is NOT
    followed even to the same host — re-issuing the request could trigger a fresh 402 and a
    second payment, too risky in the money path. L402 providers SHOULD serve the resource
    directly on the paid retry rather than redirecting; when one redirects after payment we
    hand the paid token back (``l402_token``) for the agent to reuse against the target.
    """

    def __init__(self, location: str | None, status_code: int) -> None:
        self.location = location
        self.status_code = status_code
        # Attached by the paid-retry path when a payment already settled before the
        # redirect. Defaulted so callers can read them unconditionally.
        self.amount_paid: int | None = None
        self.l402_token: str | None = None
        if location:
            super().__init__(
                f"Resource redirected to {location}. Call this tool again with that URL."
            )
        else:
            super().__init__(
                f"Resource returned an HTTP {status_code} redirect with no Location header."
            )


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

    def __init__(
        self,
        wallet: "NWCWallet",
        budget_service: "BudgetService | None" = None,
        payment_history_service: "PaymentHistoryService | None" = None,
    ) -> None:
        """
        Initialize L402 client.

        Args:
            wallet: NWC wallet for paying invoices
            budget_service: BudgetService — the client records the spend and arms the
                payment cooldown here (single source of truth), so the consuming tools stay
                passive. Optional (None in unit tests that only exercise parsing / redirects).
            payment_history_service: PaymentHistoryService — the client writes the session
                audit record here on a settled payment. Optional.
        """
        self.wallet = wallet
        self._budget_service = budget_service
        self._payment_history_service = payment_history_service
        self._http_client = httpx.AsyncClient(
            timeout=30.0,
            headers={"Accept-Encoding": "identity"},
            # Do NOT auto-follow redirects (this is httpx's default; pinned explicitly so a
            # future default change can't silently re-enable it). A 3xx is surfaced as an
            # actionable L402RedirectError, never followed — see L402RedirectError for why.
            follow_redirects=False,
        )

    def _record_spend_and_arm_cooldown(self, amount_sats: int) -> None:
        """Record the budget spend and arm the payment cooldown for a payment whose funds
        have left the wallet. This is the funds-safety-critical pair (the session ledger +
        the cross-payment cooldown) and is written in the client so the tools never do — it
        fires EXACTLY ONCE per fetch, at the single point money is known to have moved."""
        if self._budget_service is not None:
            self._budget_service.record_spend(amount_sats)
            self._budget_service.record_payment_time()

    def _record_settled_payment(self, amount_sats: int, url: str) -> None:
        """Record a fully settled (preimage-backed) payment: the spend + cooldown AND the
        session audit history entry, EXACTLY ONCE. Called only once per fetch, right after
        ``pay_invoice`` returns a preimage — so the recording is identical regardless of
        whether the authorized retry then returns 2xx / 3xx-redirect / 4xx / 5xx. This is a
        settled payment, so history status is always ``success`` — the client NEVER records a
        failed payment for a settlement whose invoice was paid."""
        self._record_spend_and_arm_cooldown(amount_sats)
        if self._payment_history_service is not None:
            # Redact before storing — the query/userinfo can carry secrets (standard #5).
            self._payment_history_service.record_payment(
                url=redact_url_for_display(url),
                amount_sats=amount_sats,
                status="success",
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

        # 3xx redirect: NOT followed (follow_redirects=False). Surface it as an actionable
        # error rather than paying/leaking. Checked before the 402/>=400 handling so a
        # provider that host-redirects BEFORE its 402 is never entered into the pay flow.
        redirect = resolve_redirect_location(url, response)
        if redirect is not None:
            raise L402RedirectError(redirect, response.status_code)

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
            try:
                preimage = await self.wallet.pay_invoice(challenge.invoice)
            except PaymentProofUnavailableError as e:
                # The wallet has no preimage for us (it never returns them, or the
                # payment hasn't settled), so L402 cannot be completed — but the
                # funds have left (or are leaving) the wallet. SINGLE SOURCE OF TRUTH:
                # record the real spend + arm the cooldown here (once) so the budget is
                # never under-counted, instead of relying on the tool. No history entry:
                # the payment is unprovable / possibly still pending, so it is not audited
                # as a settled "success" — but the funds-safety ledger is kept accurate.
                # amount_paid is attached so the tool can surface it (do-not-retry).
                e.amount_paid = challenge.amount_sats
                self._record_spend_and_arm_cooldown(challenge.amount_sats)
                raise

            # SINGLE SOURCE OF TRUTH — the invoice is paid (preimage in hand). Record the
            # spend + payment history + cooldown here, EXACTLY ONCE, BEFORE the retry, so the
            # recording is identical whether the retry returns 2xx / 3xx-redirect / 4xx / 5xx.
            # The consuming tools (access_l402_resource, settle_agent_service) are passive and
            # MUST NOT record any of this again.
            self._record_settled_payment(challenge.amount_sats, url)

            # Create token
            if isinstance(challenge, MppChallenge):
                token = MppToken(preimage=preimage)
            else:
                token = L402Token(macaroon=challenge.macaroon, preimage=preimage)

            # Raw credential (macaroon:preimage for L402, preimage for MPP) — parity with the
            # .NET L402Token field — surfaced on both the redirect and the error retry paths
            # so the agent can authenticate a retry against the target instead of re-paying.
            raw_token = (
                token.preimage
                if isinstance(token, MppToken)
                else f"{token.macaroon}:{token.preimage}"
            )

            # Retry with authorization
            auth_headers = {**headers, "Authorization": token.to_header()}
            response = await self._http_client.request(
                method=method, url=url, headers=auth_headers, content=content
            )

            # A 3xx on the paid retry is likewise not followed. The payment already
            # settled (and was recorded above, once), so surface the redirect AND the
            # settled amount + paid token so the agent can re-call the target WITH the token
            # instead of paying again. Same-host targets are also NOT auto-followed here (see
            # L402RedirectError) — re-issuing could re-trigger a 402.
            retry_redirect = resolve_redirect_location(url, response)
            if retry_redirect is not None:
                err = L402RedirectError(retry_redirect, response.status_code)
                err.amount_paid = challenge.amount_sats
                err.l402_token = raw_token
                raise err

            if response.status_code >= 400:
                # The invoice was already paid (preimage obtained, recorded once above) but
                # the authorized retry failed. This is NOT a failed payment — surface the
                # settled amount AND the paid token so the tool can tell the agent it ALREADY
                # PAID and hand back the credential to reuse, never inviting a second payment.
                err = L402Error(
                    f"Request failed after payment: {response.status_code} {response.text[:200]}"
                )
                err.amount_paid = challenge.amount_sats
                err.l402_token = raw_token
                raise err

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
        except PaymentProofUnavailableError as e:
            # Not a payment failure — the funds left (or are leaving) the wallet,
            # there is simply no preimage to authenticate with. Rewrapping this as
            # L402PaymentError("Payment failed") would tell the caller the money is
            # still theirs. Attach the amount so the spend can still be recorded.
            e.amount_paid = amount_sats
            raise
        except Exception as e:
            raise L402PaymentError(f"Payment failed: {e!s}") from e

        normalized_macaroon = macaroon.strip() if macaroon is not None else None
        if normalized_macaroon:
            return L402Token(macaroon=normalized_macaroon, preimage=preimage)
        return MppToken(preimage=preimage)
