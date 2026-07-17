"""
Tests for the OpenNode wallet client.

Focus: OpenNode does NOT return Lightning preimages. `pay_invoice` is contracted
to return a preimage (the L402 proof of payment), so when no preimage exists the
wallet MUST NOT substitute an internal identifier (withdrawal ID / reference).
"""

import pytest
from unittest.mock import AsyncMock

from lightning_enable_mcp.opennode_wallet import OpenNodeWallet, OpenNodePaymentError
from lightning_enable_mcp.wallet_errors import (
    PaymentPendingError,
    PreimageUnavailableError,
    is_valid_preimage,
)


VALID_PREIMAGE = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"
BOLT11 = "lnbc100n1p3abcdef"


def _wallet(response: dict) -> OpenNodeWallet:
    """An OpenNodeWallet whose HTTP layer returns `response` from /withdrawals."""
    wallet = OpenNodeWallet(api_key="test-key")
    wallet._request = AsyncMock(return_value=response)
    return wallet


class TestPreimageIsNeverFabricated:
    """A withdrawal ID is an internal DB id — never proof of payment."""

    @pytest.mark.asyncio
    async def test_paid_without_preimage_raises_instead_of_returning_withdrawal_id(self):
        wallet = _wallet({"id": "withdrawal-123", "status": "paid"})

        with pytest.raises(PreimageUnavailableError) as exc:
            await wallet.pay_invoice(BOLT11)

        # The withdrawal ID is available for tracking, but NOT as a preimage.
        assert exc.value.tracking_id == "withdrawal-123"
        assert exc.value.provider == "opennode"
        assert exc.value.status == "paid"

    @pytest.mark.asyncio
    async def test_reference_field_is_not_used_as_preimage(self):
        """`reference` is an internal OpenNode value, not a preimage."""
        wallet = _wallet({
            "id": "withdrawal-123",
            "status": "paid",
            "reference": "some-internal-reference",
        })

        with pytest.raises(PreimageUnavailableError):
            await wallet.pay_invoice(BOLT11)

    @pytest.mark.asyncio
    async def test_invoice_returned_in_preimage_field_is_rejected(self):
        """If OpenNode echoes the invoice back, that is not a preimage either."""
        wallet = _wallet({
            "id": "withdrawal-123",
            "status": "paid",
            "preimage": "lnbc100n1p3abcdef",
        })

        with pytest.raises(PreimageUnavailableError):
            await wallet.pay_invoice(BOLT11)

    @pytest.mark.asyncio
    async def test_malformed_preimage_is_rejected(self):
        """Anything that is not a 64-char hex string cannot be a preimage."""
        wallet = _wallet({
            "id": "withdrawal-123",
            "status": "paid",
            "preimage": "not-a-real-preimage",
        })

        with pytest.raises(PreimageUnavailableError):
            await wallet.pay_invoice(BOLT11)

    @pytest.mark.asyncio
    @pytest.mark.parametrize("status", ["paid", "confirmed", "completed"])
    async def test_valid_preimage_is_returned(self, status):
        """The happy path still works for any settled status."""
        wallet = _wallet({"id": "withdrawal-123", "status": status, "preimage": VALID_PREIMAGE})

        assert await wallet.pay_invoice(BOLT11) == VALID_PREIMAGE


class TestPendingIsNotSuccess:
    """An in-flight payment may still fail — it must not read as settled."""

    @pytest.mark.asyncio
    @pytest.mark.parametrize("status", ["pending", "processing"])
    async def test_pending_raises_rather_than_returning_withdrawal_id(self, status):
        wallet = _wallet({"id": "withdrawal-456", "status": status})

        with pytest.raises(PaymentPendingError) as exc:
            await wallet.pay_invoice(BOLT11)

        assert exc.value.tracking_id == "withdrawal-456"
        assert exc.value.status == status

    @pytest.mark.asyncio
    async def test_pending_is_distinguishable_from_settled_without_preimage(self):
        """
        The two states must not be conflated: one is terminal (money gone), the
        other is not (may still fail).
        """
        pending = _wallet({"id": "w-1", "status": "pending"})
        settled = _wallet({"id": "w-2", "status": "paid"})

        with pytest.raises(PaymentPendingError):
            await pending.pay_invoice(BOLT11)

        # A settled-but-unprovable payment is NOT a pending one.
        with pytest.raises(PreimageUnavailableError):
            await settled.pay_invoice(BOLT11)


class TestActualFailures:
    """Real failures must still surface as failures."""

    @pytest.mark.asyncio
    async def test_failed_status_raises_payment_error(self):
        wallet = _wallet({"id": "withdrawal-789", "status": "failed"})

        with pytest.raises(OpenNodePaymentError):
            await wallet.pay_invoice(BOLT11)

    @pytest.mark.asyncio
    async def test_failed_status_is_not_a_proof_error(self):
        """A failure must not masquerade as 'paid but unprovable'."""
        wallet = _wallet({"id": "withdrawal-789", "status": "failed"})

        with pytest.raises(OpenNodePaymentError) as exc:
            await wallet.pay_invoice(BOLT11)

        assert not isinstance(exc.value, PreimageUnavailableError)
        assert not isinstance(exc.value, PaymentPendingError)


class TestIsValidPreimage:
    """The guard that makes fabrication impossible."""

    @pytest.mark.parametrize("value", [
        VALID_PREIMAGE,
        VALID_PREIMAGE.upper(),          # hex is case-insensitive
        f"  {VALID_PREIMAGE}  ",         # tolerate surrounding whitespace
        "0" * 64,
    ])
    def test_accepts_real_preimages(self, value):
        assert is_valid_preimage(value) is True

    @pytest.mark.parametrize("value", [
        None,
        "",
        "withdrawal-123",                 # an OpenNode withdrawal ID
        "b5f9e0c2-1234-4a56-8901-abcdef123456",  # a UUID-ish payment ID
        "lnbc100n1p3abcdef",              # a BOLT11 invoice
        VALID_PREIMAGE[:-1],              # 63 chars
        VALID_PREIMAGE + "0",             # 65 chars
        "z" * 64,                         # right length, not hex
        12345,                            # not a string
        b"\x01" * 32,                     # raw bytes, not hex
        ["deadbeef"],
        {"preimage": VALID_PREIMAGE},
    ])
    def test_rejects_everything_else(self, value):
        assert is_valid_preimage(value) is False
