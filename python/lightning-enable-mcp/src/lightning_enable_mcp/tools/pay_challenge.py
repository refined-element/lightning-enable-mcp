"""
Pay L402 Challenge Tool

Manually pay an L402 invoice and get the authorization token.
"""

import json
import logging
from . import sanitize_error
from typing import TYPE_CHECKING

from bolt11 import decode as decode_bolt11

if TYPE_CHECKING:
    from ..budget import BudgetManager
    from ..nwc_wallet import NWCWallet

logger = logging.getLogger("lightning-enable-mcp.tools.pay")


async def pay_l402_challenge(
    invoice: str,
    macaroon: str | None = None,
    max_sats: int = 1000,
    wallet: "NWCWallet | None" = None,
    budget_manager: "BudgetManager | None" = None,
) -> str:
    """
    Manually pay an L402 or MPP invoice and receive the authorization token.

    This is useful when you want to handle the L402/MPP flow yourself rather than
    using access_l402_resource which does it automatically.

    When macaroon is provided, uses L402 protocol.
    When macaroon is omitted, uses MPP (Machine Payments Protocol) — preimage only.

    Args:
        invoice: BOLT11 Lightning invoice string
        macaroon: Base64-encoded macaroon from the L402 challenge (optional; omit for MPP mode)
        max_sats: Maximum satoshis allowed for this payment
        wallet: NWC wallet instance
        budget_manager: Budget manager for tracking spending

    Returns:
        JSON with L402/MPP token or error message
    """
    if not wallet:
        return json.dumps(
            {"success": False, "error": "Wallet not initialized. Check NWC connection."}
        )

    if not invoice:
        return json.dumps({"success": False, "error": "Invoice is required"})

    # Determine protocol: L402 if macaroon provided, MPP otherwise
    is_mpp = not macaroon

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
                    {"success": False, "error": sanitize_error(str(e)), "amount_sats": amount_sats}
                )

        # Pay the invoice
        protocol = "MPP" if is_mpp else "L402"
        logger.info(f"Paying {protocol} invoice for {amount_sats} sats")
        preimage = await wallet.pay_invoice(invoice)

        # Record payment
        if budget_manager and amount_sats:
            budget_manager.record_payment(
                url=f"manual_{protocol.lower()}_payment",
                amount_sats=amount_sats,
                invoice=invoice,
                preimage=preimage,
                status="success",
            )

        # Construct authorization header based on protocol
        if is_mpp:
            authorization_header = f'Payment method="lightning", preimage="{preimage}"'
        else:
            l402_token = f"{macaroon}:{preimage}"
            authorization_header = f"L402 {l402_token}"

        result = {
            "success": True,
            "preimage": preimage,
            "amount_sats": amount_sats,
            "protocol": protocol,
            "usage": {
                "headerName": "Authorization",
                "headerValue": authorization_header,
                "protocol": protocol,
                "description": "Include this header in subsequent requests to the same endpoint",
            },
            "message": (
                f"Payment successful ({protocol}). Use the authorization header value "
                f"to access the protected resource."
            ),
        }

        # Include token and authorization_header for backward compatibility across protocols
        if is_mpp:
            # For MPP, the token is just the preimage
            result["token"] = preimage
        else:
            # For L402, preserve existing macaroon:preimage token format
            result["token"] = f"{macaroon}:{preimage}"

        # Always include the full authorization header
        result["authorization_header"] = authorization_header

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception("Error paying L402/MPP challenge")
        return json.dumps({"success": False, "error": sanitize_error(str(e))})
