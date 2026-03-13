"""
Check Invoice Status Tool

Check the payment status of a previously created Lightning invoice.
"""

import json
import logging
from typing import TYPE_CHECKING, Union

if TYPE_CHECKING:
    from ..nwc_wallet import NWCWallet
    from ..opennode_wallet import OpenNodeWallet
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.check_invoice_status")


async def check_invoice_status(
    invoice_id: str,
    wallet: "Union[NWCWallet, OpenNodeWallet, StrikeWallet, None]" = None,
) -> str:
    """
    Check if a Lightning invoice has been paid.

    Use the invoice ID returned from create_invoice to check whether
    the invoice has been paid, is still pending, or has expired.

    Args:
        invoice_id: The invoice ID returned from create_invoice
        wallet: Wallet instance

    Returns:
        JSON with invoice status including whether it has been paid
    """
    if not invoice_id or not invoice_id.strip():
        return json.dumps({
            "success": False,
            "error": "Invoice ID is required"
        })

    if not wallet:
        return json.dumps({
            "success": False,
            "error": "Wallet not configured. Set STRIKE_API_KEY, OPENNODE_API_KEY, or NWC_CONNECTION_STRING environment variable."
        })

    try:
        from ..strike_wallet import StrikeWallet

        if isinstance(wallet, StrikeWallet):
            # Use Strike API to check invoice status
            payment = await wallet._request("GET", f"/invoices/{invoice_id}")
            state = payment.get("state", "UNKNOWN")
            amount = payment.get("amount", {})
            amount_value = amount.get("amount") if isinstance(amount, dict) else None
            paid_at = payment.get("paidAt")

            is_paid = state.upper() in ("PAID", "COMPLETED")
            is_pending = state.upper() in ("UNPAID", "PENDING")

            if is_paid:
                message = f"Invoice {invoice_id} has been PAID!"
            elif is_pending:
                message = f"Invoice {invoice_id} is still pending payment."
            else:
                message = f"Invoice {invoice_id} status: {state}"

            return json.dumps({
                "success": True,
                "provider": "Strike",
                "invoice": {
                    "id": invoice_id,
                    "state": state,
                    "isPaid": is_paid,
                    "isPending": is_pending,
                    "amount": amount_value,
                    "paidAt": paid_at,
                },
                "message": message,
            }, indent=2)
        else:
            provider_name = type(wallet).__name__.replace("Wallet", "")
            return json.dumps({
                "success": False,
                "error": f"Invoice status check is not supported with {provider_name} wallet.",
                "hint": "Use Strike wallet (set STRIKE_API_KEY) for invoice status checking."
            })

    except Exception as e:
        logger.exception("Error checking invoice status")
        return json.dumps({
            "success": False,
            "error": str(e)
        })
