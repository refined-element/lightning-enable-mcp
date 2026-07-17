"""
Wallet payment errors for states where NO PREIMAGE EXISTS.

Every ``wallet.pay_invoice()`` implementation is contracted to return a Lightning
payment preimage. The preimage is not a receipt or a tracking number — under L402
(and MPP) it IS the proof of payment: the client sends it back to the server as
``Authorization: L402 <macaroon>:<preimage>``, and the server grants access only
because possessing it proves the invoice was paid.

That contract has exactly two failure modes that are NOT "the payment failed":

1. The payment settled, but the provider does not expose preimages
   (OpenNode never does; Strike occasionally omits it) -> PreimageUnavailableError.
2. The payment was accepted but has not settled yet, so no preimage exists YET —
   and it may still fail -> PaymentPendingError.

In both cases there is a strong temptation to return SOMETHING preimage-shaped —
a withdrawal ID, a payment ID, an internal reference. Doing so is a serious bug:
the caller publishes that value to the agent in the field L402 treats as proof of
payment, the server rejects the resulting Authorization header, and the agent's
own records claim a valid preimage for a payment it cannot prove. Money spent, no
access, and a receipt that lies.

So these states raise instead of returning. An exception cannot be silently passed
off as a preimage the way a fabricated string can — the invariant "if pay_invoice
returns, the return value IS a real preimage" holds for every caller at once.
Each error carries ``tracking_id`` so callers can still tell the human/agent which
provider-side payment to go look at.

Mirrors the .NET port's NwcPaymentResult.HasPreimage / SucceededWithoutPreimage
contract (see dotnet/src/LightningEnable.Mcp/Models/NwcConfig.cs).
"""

from typing import Optional

# A BOLT11 preimage is a 32-byte value, hex-encoded => 64 hex characters.
PREIMAGE_HEX_LENGTH = 64


def is_valid_preimage(value: object) -> bool:
    """
    True only if ``value`` is a syntactically valid Lightning preimage:
    a 64-character hex string (32 bytes).

    This does NOT verify the preimage against a payment hash — it only rejects
    values that cannot possibly be a preimage (withdrawal IDs, payment IDs,
    internal references, BOLT11 invoices, empty strings, non-strings). Anything
    that fails this check must never be handed to a caller as proof of payment.
    """
    if not isinstance(value, str):
        return False

    candidate = value.strip()
    if len(candidate) != PREIMAGE_HEX_LENGTH:
        return False

    try:
        bytes.fromhex(candidate)
    except ValueError:
        return False

    return True


class PaymentProofUnavailableError(Exception):
    """
    Base: funds may have left the wallet, but no preimage is available as proof.

    Callers that only need to fail closed can catch this one type. Callers that
    must distinguish "settled, unprovable" from "still in flight" catch the two
    subclasses below — the difference matters, because one is terminal and the
    other is not.
    """

    def __init__(
        self,
        message: str,
        *,
        provider: str,
        tracking_id: Optional[str] = None,
        status: Optional[str] = None,
    ) -> None:
        super().__init__(message)
        self.provider = provider
        # Provider-side identifier (withdrawal ID, payment ID). NOT a preimage —
        # for looking the payment up with the provider, nothing more.
        self.tracking_id = tracking_id
        # Raw provider status string that led here, for diagnostics.
        self.status = status


class PreimageUnavailableError(PaymentProofUnavailableError):
    """
    TERMINAL: the payment settled and the funds are gone, but the provider will
    never return a preimage for it, so L402/MPP verification is impossible.

    Not a payment failure — do not report it to the agent as one, or the agent
    will retry and pay twice. Report: paid, unprovable, here is the tracking ID.
    """


class PaymentPendingError(PaymentProofUnavailableError):
    """
    NON-TERMINAL: the payment was accepted but has not settled. It may still
    succeed, and it may still FAIL.

    Must not be reported as success (the agent would proceed believing it paid)
    nor as a hard failure (the agent would retry and risk paying twice). Report
    it as pending, with the tracking ID to poll.
    """
