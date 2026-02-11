"""
Pay L402 Challenge Tool

Manually pay an L402 invoice and get the authorization token.
"""

import json
import logging
from typing import TYPE_CHECKING

from bolt11 import decode as decode_bolt11

if TYPE_CHECKING:
    from ..budget import BudgetManager
    from ..nwc_wallet import NWCWallet

logger = logging.getLogger("lightning-enable-mcp.tools.pay")


async def pay_l402_challenge(
    invoice: str,
    macaroon: str,
    max_sats: int = 1000,
    wallet: "NWCWallet | None" = None,
    budget_manager: "BudgetManager | None" = None,
) -> str:
    """
    Manually pay an L402 invoice and receive the authorization token.

    This is useful when you want to handle the L402 flow yourself rather than
    using access_l402_resource which does it automatically.

    Args:
        invoice: BOLT11 Lightning invoice string
        macaroon: Base64-encoded macaroon from the L402 challenge
        max_sats: Maximum satoshis allowed for this payment
        wallet: NWC wallet instance
        budget_manager: Budget manager for tracking spending

    Returns:
        JSON with L402 token or error message
    """
    if not wallet:
        return json.dumps(
            {"success": False, "error": "Wallet not initialized. Check NWC connection."}
        )

    if not invoice:
        return json.dumps({"success": False, "error": "Invoice is required"})

    if not macaroon:
        return json.dumps({"success": False, "error": "Macaroon is required"})

    try:
        # Parse invoice to get amount
        decoded = decode_bolt11(invoice)
        amount_msat = None
        amount_sats = None

        if hasattr(decoded, "amount_msat") and decoded.amount_msat:
            amount_msat = decoded.amount_msat
            amount_sats = amount_msat // 1000
        elif hasattr(decoded, "amount") and decoded.amount:
            amount_sats = decoded.amount

        # Check against max_sats
        if amount_sats is not None and amount_sats > max_sats:
            return json.dumps(
                {
                    "success": False,
                    "error": f"Invoice amount {amount_sats} sats exceeds maximum {max_sats} sats",
                    "amount_sats": amount_sats,
                }
            )

        # Check budget
        if budget_manager and amount_sats:
            try:
                budget_manager.check_payment(amount_sats, max_sats)
            except Exception as e:
                return json.dumps(
                    {"success": False, "error": str(e), "amount_sats": amount_sats}
                )

        # Pay the invoice
        logger.info(f"Paying invoice for {amount_sats} sats")
        preimage = await wallet.pay_invoice(invoice)

        # Record payment
        if budget_manager and amount_sats:
            budget_manager.record_payment(
                url="manual_l402_payment",
                amount_sats=amount_sats,
                invoice=invoice,
                preimage=preimage,
                status="success",
            )

        # Construct L402 token
        l402_token = f"{macaroon}:{preimage}"

        result = {
            "success": True,
            "token": l402_token,
            "authorization_header": f"L402 {l402_token}",
            "preimage": preimage,
            "amount_sats": amount_sats,
            "message": (
                f"Payment successful. Use the authorization_header value in your "
                f"Authorization header to access the L402-protected resource."
            ),
        }

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception("Error paying L402 challenge")
        return json.dumps({"success": False, "error": str(e)})
