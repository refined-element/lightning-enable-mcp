"""
Tests for the Strike wallet's preimage contract.

Strike normally returns a real preimage via `lightning.preImage`, which is why
Strike supports L402. When it does NOT, the payment ID must never stand in for
the preimage — that would publish an internal identifier to the agent in the
field L402 treats as proof of payment.
"""

import pytest
from unittest.mock import AsyncMock

from lightning_enable_mcp.strike_wallet import StrikeWallet, StrikePaymentError
from lightning_enable_mcp.wallet_errors import PreimageUnavailableError


VALID_PREIMAGE = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"
BOLT11 = "lnbc100n1p3abcdef"


def _wallet(execute_response: dict) -> StrikeWallet:
    """
    A StrikeWallet whose HTTP layer returns a payment quote, then
    `execute_response` from the execute-payment-quote call.
    """
    wallet = StrikeWallet(api_key="test-key")
    wallet._request = AsyncMock(side_effect=[
        {"paymentQuoteId": "quote-1"},   # POST /payment-quotes/lightning
        execute_response,                 # PATCH /payment-quotes/{id}/execute
    ])
    return wallet


class TestStrikePreimageContract:

    @pytest.mark.asyncio
    async def test_preimage_is_returned_when_present(self):
        wallet = _wallet({
            "paymentId": "payment-1",
            "state": "COMPLETED",
            "lightning": {"preImage": VALID_PREIMAGE},
        })

        assert await wallet.pay_invoice(BOLT11) == VALID_PREIMAGE

    @pytest.mark.asyncio
    async def test_completed_without_preimage_does_not_return_payment_id(self):
        wallet = _wallet({
            "paymentId": "payment-1",
            "state": "COMPLETED",
            "lightning": {},
        })

        with pytest.raises(PreimageUnavailableError) as exc:
            await wallet.pay_invoice(BOLT11)

        assert exc.value.tracking_id == "payment-1"
        assert exc.value.provider == "strike"

    @pytest.mark.asyncio
    async def test_completed_with_missing_lightning_block_raises(self):
        wallet = _wallet({"paymentId": "payment-1", "state": "COMPLETED"})

        with pytest.raises(PreimageUnavailableError):
            await wallet.pay_invoice(BOLT11)

    @pytest.mark.asyncio
    async def test_malformed_preimage_is_rejected(self):
        wallet = _wallet({
            "paymentId": "payment-1",
            "state": "COMPLETED",
            "lightning": {"preImage": "payment-1"},
        })

        with pytest.raises(PreimageUnavailableError):
            await wallet.pay_invoice(BOLT11)

    @pytest.mark.asyncio
    async def test_failed_state_still_raises_payment_error(self):
        wallet = _wallet({"paymentId": "payment-1", "state": "FAILED"})

        with pytest.raises(StrikePaymentError) as exc:
            await wallet.pay_invoice(BOLT11)

        # A real failure is not a "paid but unprovable" outcome.
        assert not isinstance(exc.value, PreimageUnavailableError)
