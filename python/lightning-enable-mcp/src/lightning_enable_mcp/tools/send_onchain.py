"""
Send On-Chain Tool

Send an on-chain Bitcoin payment to a Bitcoin address.
Supports Strike and LND wallets.
"""

import json
import logging
from . import sanitize_error
from typing import TYPE_CHECKING, Union

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..lnd_wallet import LndWallet
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.send_onchain")


async def send_onchain(
    address: str,
    amount_sats: int,
    confirmed: bool = False,
    wallet: "Union[StrikeWallet, LndWallet, None]" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Send an on-chain Bitcoin payment to a Bitcoin address.

    Supports Strike and LND wallets. The payment is sent from your
    wallet balance.

    Args:
        address: Bitcoin address to send to (e.g., bc1q...)
        amount_sats: Amount to send in satoshis
        confirmed: Set to True to confirm this irreversible on-chain send.
            On-chain payments cannot be undone, so the first call always returns
            requiresConfirmation; call again with confirmed=True to proceed.
        wallet: Strike or LND wallet instance
        budget_service: BudgetService for spending limits

    Returns:
        JSON with payment result including transaction details
    """
    if not address or not address.strip():
        return json.dumps({
            "success": False,
            "error": "Bitcoin address is required"
        })

    # PY-C2: validate the address before anything else. On-chain sends are
    # irreversible, so a typo'd, garbage, or wrong-network address must be
    # rejected here rather than risk broadcasting funds to an unrecoverable
    # destination. Only valid mainnet addresses pass.
    from ..bitcoin_address import is_valid_mainnet
    if not is_valid_mainnet(address):
        return json.dumps({
            "success": False,
            "error": "Invalid Bitcoin address. Provide a valid mainnet Bitcoin address "
                     "(starts with bc1, 1, or 3). The address failed validation and was "
                     "NOT sent — on-chain payments are irreversible."
        })

    if amount_sats <= 0:
        return json.dumps({
            "success": False,
            "error": "Amount must be greater than 0 sats"
        })

    if not wallet:
        return json.dumps({
            "success": False,
            "error": "Wallet not configured. Set STRIKE_API_KEY or LND_REST_HOST+LND_MACAROON_HEX for on-chain payments."
        })

    # Verify it's a supported wallet type
    from ..strike_wallet import StrikeWallet
    from ..lnd_wallet import LndWallet
    if not isinstance(wallet, (StrikeWallet, LndWallet)):
        provider_name = type(wallet).__name__.replace("Wallet", "")
        return json.dumps({
            "success": False,
            "error": f"{provider_name} does not support on-chain payments. Use Strike or LND wallet.",
            "errorCode": "NOT_SUPPORTED",
            "hint": "Set STRIKE_API_KEY or LND_REST_HOST+LND_MACAROON_HEX for on-chain payments."
        })

    # Budget check — FAIL CLOSED (PY-FAILOPEN fix). Previously an exception here
    # was swallowed and the payment proceeded with NO budget enforcement; for an
    # irreversible on-chain send that is unacceptable, so a budget-check error
    # now REFUSES the send.
    if budget_service:
        try:
            budget_result = await budget_service.check_approval_level(amount_sats)
        except Exception as e:
            logger.warning(f"Budget check error; refusing on-chain send (fail-closed): {e}")
            return json.dumps({
                "success": False,
                "error": "Could not verify spending budget, so the on-chain send was refused "
                         "(fail-closed). Check the wallet / price service and try again.",
            })

        from ..config import ApprovalLevel
        if budget_result.level == ApprovalLevel.DENY:
            return json.dumps({
                "success": False,
                "error": f"Budget check failed: {budget_result.denial_reason}",
            })

    # PY-C1: on-chain sends are irreversible, so ALWAYS require explicit
    # confirmation regardless of amount/tier. (Previously the requires_confirmation
    # tiers fell through and paid with no confirmation at all.)
    if not confirmed:
        return json.dumps({
            "success": False,
            "requiresConfirmation": True,
            "error": "On-chain sends are irreversible and require explicit confirmation.",
            "message": f"Confirm sending {amount_sats:,} sats to {address}? This cannot be undone.",
            "howToConfirm": f'Call: send_onchain(address="{address}", amount_sats={amount_sats}, confirmed=True)',
        })

    try:
        result = await wallet.send_onchain(address.strip(), amount_sats)

        if not result.success:
            return json.dumps({
                "success": False,
                "error": result.error_message,
                "errorCode": result.error_code,
            })

        # Record spend if budget service available
        if budget_service:
            try:
                total_sats = amount_sats + (result.fee_sats or 0)
                budget_service.record_spend(total_sats)
                budget_service.record_payment_time()
            except Exception:
                pass

        provider_name = "LND" if isinstance(wallet, LndWallet) else "Strike"

        if result.state == "COMPLETED":
            message = f"On-chain payment of {amount_sats} sats sent to {address}"
        else:
            message = f"On-chain payment initiated (status: {result.state})"

        return json.dumps({
            "success": True,
            "provider": provider_name,
            "payment": {
                "id": result.payment_id,
                "txId": result.txid,
                "state": result.state,
                "amountSats": result.amount_sats,
                "feeSats": result.fee_sats,
            },
            "message": message,
        }, indent=2)

    except Exception as e:
        logger.exception("Error sending on-chain payment")
        return json.dumps({
            "success": False,
            "error": sanitize_error(str(e))
        })
