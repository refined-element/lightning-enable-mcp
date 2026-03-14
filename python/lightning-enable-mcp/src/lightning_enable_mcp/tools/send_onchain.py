"""
Send On-Chain Tool

Send an on-chain Bitcoin payment to a Bitcoin address.
Supports Strike and LND wallets.
"""

import json
import logging
from typing import TYPE_CHECKING, Union

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..lnd_wallet import LndWallet
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.send_onchain")


async def send_onchain(
    address: str,
    amount_sats: int,
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

    # Check budget if configured
    if budget_service:
        try:
            budget_result = await budget_service.check_approval_level(amount_sats)
            from ..config import ApprovalLevel
            if budget_result.level == ApprovalLevel.DENY:
                return json.dumps({
                    "success": False,
                    "error": f"Budget check failed: {budget_result.denial_reason}",
                })
        except Exception as e:
            logger.warning(f"Budget check failed: {e}")

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
            "error": str(e)
        })
