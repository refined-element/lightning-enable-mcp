"""
Pay Invoice Tool

Pay a Lightning invoice directly and get the preimage as proof of payment.
Uses the new BudgetService with multi-tier approval logic.
"""

import json
import logging
import sys
from typing import TYPE_CHECKING, Optional, Union

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..payment_history_service import PaymentHistoryService
    from ..nwc_wallet import NWCWallet
    from ..opennode_wallet import OpenNodeWallet

from bolt11 import decode as decode_bolt11

from ..config import ApprovalLevel
from . import sanitize_error

logger = logging.getLogger("lightning-enable-mcp.tools.pay_invoice")


async def pay_invoice(
    invoice: str,
    max_sats: int = 1000,
    confirmation_nonce: "Optional[str]" = None,
    wallet: "Union[NWCWallet, OpenNodeWallet, None]" = None,
    budget_service: "BudgetService | None" = None,
    payment_history_service: "PaymentHistoryService | None" = None,
) -> str:
    """
    Pay a Lightning invoice directly and get the preimage as proof of payment.

    This tool allows direct payment of any BOLT11 Lightning invoice without
    the L402 protocol overhead. Useful for tipping, donations, or paying
    for services that accept Lightning directly.

    Payments above the auto-approve threshold require OUT-OF-BAND confirmation: the
    server prints a code to its console/stderr (the human operator sees it; the model
    does not), and you must ask the human for that code and pass it as confirmation_nonce.
    The code is never returned in a tool result, so a prompt-injected agent cannot
    self-approve.

    Args:
        invoice: BOLT11 Lightning invoice string to pay
        max_sats: Maximum satoshis allowed to pay. Defaults to 1000
        confirmation_nonce: The code the human read from the server console (for payments
            above the auto-approve threshold). Omit on the first call to request one.
        wallet: Wallet instance (NWC or OpenNode)
        budget_service: BudgetService for multi-tier approval logic
        payment_history_service: PaymentHistoryService for the session audit trail

    Returns:
        JSON with payment result including preimage or error message
    """
    # Validate invoice is provided
    if not invoice or not invoice.strip():
        return json.dumps({
            "success": False,
            "error": "Invoice is required"
        })

    if not wallet:
        return json.dumps({
            "success": False,
            "error": "Wallet not configured. Set NWC_CONNECTION_STRING or OPENNODE_API_KEY environment variable."
        })

    try:
        # Normalize invoice to lowercase
        normalized_invoice = invoice.strip().lower()

        # Basic validation - must be a BOLT11 invoice
        if not normalized_invoice.startswith("lnbc") and not normalized_invoice.startswith("lntb"):
            return json.dumps({
                "success": False,
                "error": "Invalid invoice format. Must be a BOLT11 invoice starting with 'lnbc' (mainnet) or 'lntb' (testnet)"
            })

        # Decode the ACTUAL amount encoded in the invoice and enforce the budget against
        # IT, not max_sats. The wallet pays the encoded amount, so checking only max_sats
        # would let a tiny cap auto-approve a large payment (budget bypass + mis-counted
        # spend). Mirrors pay_l402_challenge and the .NET PayInvoiceTool.
        try:
            decoded = decode_bolt11(normalized_invoice)
        except Exception:
            return json.dumps({
                "success": False,
                "error": "Could not decode the BOLT11 invoice."
            })

        amount_sats = None
        if getattr(decoded, "amount_msat", None):
            amount_sats = -(-decoded.amount_msat // 1000)  # ceil; sub-sat rounds up to 1
        elif getattr(decoded, "amount", None):
            amount_sats = decoded.amount

        if amount_sats is None or amount_sats <= 0:
            return json.dumps({
                "success": False,
                "error": "Invoice has no amount specified. For security, only invoices with an explicit amount are supported."
            })

        if amount_sats > max_sats:
            return json.dumps({
                "success": False,
                "error": f"Invoice amount {amount_sats:,} sats exceeds the maximum {max_sats:,} sats you allowed.",
                "amount_sats": amount_sats,
            })

        # BudgetService is the single source of truth for spending limits + the
        # out-of-band confirmation flow.
        if budget_service:
            # Check approval level against the DECODED invoice amount (what will be paid)
            result = await budget_service.check_approval_level(amount_sats)

            if result.level == ApprovalLevel.DENY:
                return json.dumps({
                    "success": False,
                    "error": "Payment denied by budget policy",
                    "denialReason": result.denial_reason,
                    "budget": {
                        "requestedSats": amount_sats,
                        "requestedUsd": float(result.amount_usd),
                        "remainingSessionUsd": float(result.remaining_session_budget_usd),
                    },
                    "note": "Edit ~/.lightning-enable/config.json to change limits."
                })

            # OUT-OF-BAND confirmation for above-threshold payments. The code is printed to
            # the server console (stderr) ONLY — never in this result — so the human operator
            # (not the model) must read it and relay it back. This is what stops a
            # prompt-injected agent from reading its own code and self-approving.
            if result.requires_confirmation:
                if confirmation_nonce:
                    confirmation = budget_service.validate_and_consume_confirmation(
                        confirmation_nonce.strip().upper(), amount_sats, "pay_invoice"
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
                                "pay_invoice again with confirmation_nonce set to it."
                            ),
                        })
                    # Human-relayed code validated (amount + tool bound) — fall through and pay.
                else:
                    pending = budget_service.create_pending_confirmation(
                        amount_sats, result.amount_usd, "pay_invoice", normalized_invoice[:30] + "..."
                    )
                    print(
                        "[Lightning Enable] *** PAYMENT CONFIRMATION REQUIRED ***\n"
                        f"  pay_invoice — ${result.amount_usd:.2f} ({amount_sats:,} sats), "
                        f"invoice {normalized_invoice[:30]}...\n"
                        f"  Confirmation code: {pending.nonce}\n"
                        "  To approve, give this code to the agent. Expires in 120s.",
                        file=sys.stderr,
                        flush=True,
                    )
                    return json.dumps({
                        "success": False,
                        "requiresConfirmation": True,
                        "approvalLevel": result.level.value,
                        "error": "Payment requires human confirmation",
                        "message": (
                            f"This payment of ${result.amount_usd:.2f} ({amount_sats:,} sats) exceeds the "
                            "auto-approve threshold. A confirmation code was printed to the server console/logs "
                            "— visible to the human operator, NOT to you. Ask the human to read that code and "
                            "give it to you."
                        ),
                        "howToConfirm": (
                            "Ask the human operator for the confirmation code shown in the server console, then "
                            'call pay_invoice(invoice="...", confirmation_nonce="<code-from-human>").'
                        ),
                        "amount": {"sats": amount_sats, "usd": float(result.amount_usd)},
                        "expiresInSeconds": 120,
                        "budget": {"remainingSessionUsd": float(result.remaining_session_budget_usd)},
                    })

            # LOG_AND_APPROVE: Log for user awareness but proceed
            if result.level == ApprovalLevel.LOG_AND_APPROVE:
                logger.info(f"Log-and-approve payment: {amount_sats} sats (${result.amount_usd:.2f})")

        # Pay the invoice
        logger.info(f"Paying invoice: {normalized_invoice[:30]}...")
        preimage = await wallet.pay_invoice(normalized_invoice)

        if not preimage:
            # Record failed payment (preimage is never stored in history).
            if payment_history_service:
                payment_history_service.record_payment(
                    url="direct-invoice",
                    amount_sats=amount_sats,
                    status="failed",
                    invoice=normalized_invoice,
                )
            return json.dumps({
                "success": False,
                "error": "Payment failed - no preimage returned"
            })

        # Record the payment
        session_info = None
        if budget_service:
            budget_service.record_spend(amount_sats)
            budget_service.record_payment_time()

            # Get updated session info
            status = budget_service.get_status()
            session_info = {
                "spentSats": status["session"]["spentSats"],
                "spentUsd": status["session"]["spentUsd"],
                "remainingUsd": status["session"]["remainingUsd"],
                "requestCount": status["session"]["requestCount"],
            }

        # Audit trail (separate from limits). Preimage is NEVER stored.
        if payment_history_service:
            payment_history_service.record_payment(
                url="direct-invoice",
                amount_sats=amount_sats,
                status="success",
                invoice=normalized_invoice,
            )

        # Return success with preimage
        response = {
            "success": True,
            "preimage": preimage,
            "message": "Payment successful",
            "invoice": {
                "paid": normalized_invoice[:30] + "..." if len(normalized_invoice) > 30 else normalized_invoice
            }
        }

        if session_info:
            response["session"] = session_info

        return json.dumps(response, indent=2)

    except Exception as e:
        logger.exception("Error paying invoice")

        # Record failed payment (preimage is never stored in history).
        if payment_history_service:
            payment_history_service.record_payment(
                url="direct-invoice",
                amount_sats=0,
                status="failed",
                invoice=invoice,
            )

        return json.dumps({
            "success": False,
            "error": sanitize_error(str(e))
        })
