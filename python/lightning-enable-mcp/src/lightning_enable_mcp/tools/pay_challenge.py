"""
Pay L402 Challenge Tool

Manually pay an L402 invoice and get the authorization token.
"""

import json
import logging
import sys
from . import sanitize_error
from typing import TYPE_CHECKING, Optional

from bolt11 import decode as decode_bolt11

if TYPE_CHECKING:
    from ..budget import BudgetManager
    from ..budget_service import BudgetService
    from ..nwc_wallet import NWCWallet

logger = logging.getLogger("lightning-enable-mcp.tools.pay")


async def pay_l402_challenge(
    invoice: str,
    macaroon: str | None = None,
    max_sats: int = 1000,
    confirmation_nonce: "Optional[str]" = None,
    wallet: "NWCWallet | None" = None,
    budget_manager: "BudgetManager | None" = None,
    budget_service: "BudgetService | None" = None,
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
        confirmation_nonce: The code the human read from the server console (for payments above
            the auto-approve threshold). Omit on the first call to request one.
        wallet: NWC wallet instance
        budget_manager: Legacy budget manager (deprecated, use budget_service)
        budget_service: BudgetService for multi-tier approval + out-of-band confirmation

    Returns:
        JSON with L402/MPP token or error message
    """
    if not wallet:
        return json.dumps(
            {"success": False, "error": "Wallet not initialized. Check NWC connection."}
        )

    if not invoice:
        return json.dumps({"success": False, "error": "Invoice is required"})

    # Normalize invoice: strip whitespace/newlines that could cause decode or payment failures
    invoice = invoice.strip()

    # Normalize macaroon: strip whitespace and treat empty/whitespace-only as None
    if macaroon is not None:
        macaroon = macaroon.strip()
        if not macaroon:
            macaroon = None
    is_mpp = macaroon is None

    try:
        # Parse invoice to get amount
        decoded = decode_bolt11(invoice)
        amount_msat = None
        amount_sats = None

        if hasattr(decoded, "amount_msat") and decoded.amount_msat:
            amount_msat = decoded.amount_msat
            amount_sats = -(-amount_msat // 1000)  # ceil division: sub-sat amounts round up to 1
        elif hasattr(decoded, "amount") and decoded.amount:
            amount_sats = decoded.amount

        # Reject no-amount invoices (security: could bypass budget checks)
        if amount_sats is None or amount_sats <= 0:
            return json.dumps(
                {
                    "success": False,
                    "error": "Invoice has no amount specified. For security, only invoices with explicit amounts are supported.",
                }
            )

        # Check against max_sats
        if amount_sats > max_sats:
            return json.dumps(
                {
                    "success": False,
                    "error": f"Invoice amount {amount_sats} sats exceeds maximum {max_sats} sats",
                    "amount_sats": amount_sats,
                }
            )

        # Budget approval + OUT-OF-BAND confirmation (BudgetService path). Above the
        # auto-approve threshold the code is printed to the server console (stderr) only —
        # never in the result — so the human, not the model, must read it and relay it back.
        if budget_service:
            from ..config import ApprovalLevel
            approval = await budget_service.check_approval_level(amount_sats)
            if approval.level == ApprovalLevel.DENY:
                return json.dumps({
                    "success": False,
                    "error": f"Payment denied by budget policy: {approval.denial_reason}",
                    "amount_sats": amount_sats,
                })
            if approval.requires_confirmation:
                inv_prefix = invoice[:30] + "..."
                if confirmation_nonce:
                    confirmation = budget_service.validate_and_consume_confirmation(
                        confirmation_nonce.strip().upper(), amount_sats, "pay_l402_challenge"
                    )
                    if confirmation is None:
                        return json.dumps({
                            "success": False,
                            "error": (
                                "Confirmation code is invalid, expired, already used, or does not match THIS "
                                "payment's amount and tool. Codes are bound to the exact amount + tool approved."
                            ),
                            "message": (
                                "Ask the human operator for the code shown in the server console, then call "
                                "pay_l402_challenge again with confirmation_nonce set to it."
                            ),
                        })
                    # Human-relayed code validated — fall through and pay.
                else:
                    pending = budget_service.create_pending_confirmation(
                        amount_sats, approval.amount_usd, "pay_l402_challenge", inv_prefix
                    )
                    print(
                        "[Lightning Enable] *** L402 CHALLENGE PAYMENT CONFIRMATION REQUIRED ***\n"
                        f"  pay_l402_challenge — ${approval.amount_usd:.2f} ({amount_sats:,} sats), "
                        f"invoice {inv_prefix}\n"
                        f"  Confirmation code: {pending.nonce}\n"
                        "  To approve, give this code to the agent. Expires in 120s.",
                        file=sys.stderr,
                        flush=True,
                    )
                    return json.dumps({
                        "success": False,
                        "requiresConfirmation": True,
                        "error": "L402 challenge payment requires human confirmation",
                        "message": (
                            f"This payment of ${approval.amount_usd:.2f} ({amount_sats:,} sats) exceeds the "
                            "auto-approve threshold. A confirmation code was printed to the server console/logs "
                            "— visible to the human operator, NOT to you. Ask the human to read that code and "
                            "give it to you."
                        ),
                        "howToConfirm": (
                            "Ask the human operator for the confirmation code shown in the server console, then "
                            'call pay_l402_challenge(invoice="...", confirmation_nonce="<code-from-human>").'
                        ),
                        "amount": {"sats": amount_sats, "usd": float(approval.amount_usd)},
                        "expiresInSeconds": 120,
                    })

        # Legacy budget manager fallback (deprecated, no confirmation flow).
        elif budget_manager and amount_sats:
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
        if budget_service and amount_sats:
            budget_service.record_spend(amount_sats)
            budget_service.record_payment_time()
        elif budget_manager and amount_sats:
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
