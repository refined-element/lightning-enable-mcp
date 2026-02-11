"""
Pay Invoice Tool

Pay a Lightning invoice directly and get the preimage as proof of payment.
Uses the new BudgetService with multi-tier approval logic.
"""

import json
import logging
from typing import TYPE_CHECKING, Union

if TYPE_CHECKING:
    from ..budget import BudgetManager
    from ..budget_service import BudgetService
    from ..nwc_wallet import NWCWallet
    from ..opennode_wallet import OpenNodeWallet

from ..config import ApprovalLevel

logger = logging.getLogger("lightning-enable-mcp.tools.pay_invoice")


async def pay_invoice(
    invoice: str,
    max_sats: int = 1000,
    confirmed: bool = False,
    wallet: "Union[NWCWallet, OpenNodeWallet, None]" = None,
    budget_manager: "BudgetManager | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Pay a Lightning invoice directly and get the preimage as proof of payment.

    This tool allows direct payment of any BOLT11 Lightning invoice without
    the L402 protocol overhead. Useful for tipping, donations, or paying
    for services that accept Lightning directly.

    NOTE: Most MCP clients (including Claude Code) don't support elicitation yet.
    Payments above auto_approve threshold require explicit confirmation
    by calling this tool again with confirmed=True.

    Args:
        invoice: BOLT11 Lightning invoice string to pay
        max_sats: Maximum satoshis allowed to pay. Defaults to 1000
        confirmed: Set to True to confirm a payment above the auto-approve threshold
        wallet: Wallet instance (NWC or OpenNode)
        budget_manager: Legacy budget manager (deprecated, use budget_service)
        budget_service: BudgetService for multi-tier approval logic

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

        # Use new BudgetService if available, otherwise fall back to legacy BudgetManager
        if budget_service:
            # Check approval level using new multi-tier system
            result = await budget_service.check_approval_level(max_sats)

            if result.level == ApprovalLevel.DENY:
                return json.dumps({
                    "success": False,
                    "error": "Payment denied by budget policy",
                    "denialReason": result.denial_reason,
                    "budget": {
                        "requestedSats": max_sats,
                        "requestedUsd": float(result.amount_usd),
                        "remainingSessionUsd": float(result.remaining_session_budget_usd),
                    },
                    "note": "Edit ~/.lightning-enable/config.json to change limits."
                })

            # Check if payment requires confirmation (FORM_CONFIRM or URL_CONFIRM)
            if result.requires_confirmation and not confirmed:
                return json.dumps({
                    "success": False,
                    "requiresConfirmation": True,
                    "approvalLevel": result.level.value,
                    "error": "Payment requires your confirmation",
                    "message": result.confirmation_message or f"Approve payment of ${result.amount_usd:.2f} ({max_sats:,} sats)?",
                    "howToConfirm": 'Call: pay_invoice(invoice="...", confirmed=True)',
                    "amount": {
                        "sats": max_sats,
                        "usd": float(result.amount_usd)
                    },
                    "budget": {
                        "remainingSessionUsd": float(result.remaining_session_budget_usd),
                    }
                })

            # LOG_AND_APPROVE: Log for user awareness but proceed
            if result.level == ApprovalLevel.LOG_AND_APPROVE:
                logger.info(f"Log-and-approve payment: {max_sats} sats (${result.amount_usd:.2f})")

        elif budget_manager:
            # Legacy budget manager fallback
            try:
                budget_manager.check_payment(max_sats)
            except Exception as e:
                return json.dumps({
                    "success": False,
                    "error": str(e),
                    "budget": {
                        "requested_sats": max_sats,
                        "remaining_sats": budget_manager.max_per_session - budget_manager.session_spent
                    }
                })

            # Check if payment requires confirmation (above auto_approve threshold)
            auto_approve_sats = getattr(budget_manager, 'auto_approve_sats', 1000)
            if max_sats > auto_approve_sats and not confirmed:
                # Estimate USD value (~$0.001 per sat at ~$100k/BTC)
                estimated_usd = max_sats * 0.001
                return json.dumps({
                    "success": False,
                    "requiresConfirmation": True,
                    "error": "Payment requires your confirmation",
                    "message": f"This payment of ~${estimated_usd:.2f} ({max_sats:,} sats) exceeds the auto-approve threshold of {auto_approve_sats:,} sats. "
                              "To proceed, call pay_invoice again with confirmed=True.",
                    "howToConfirm": 'Call: pay_invoice(invoice="...", confirmed=True)',
                    "amount": {
                        "sats": max_sats,
                        "estimatedUsd": round(estimated_usd, 2)
                    },
                    "thresholds": {
                        "autoApprove": auto_approve_sats,
                        "note": "Payments above this require confirmation"
                    }
                })

        # Pay the invoice
        logger.info(f"Paying invoice: {normalized_invoice[:30]}...")
        preimage = await wallet.pay_invoice(normalized_invoice)

        if not preimage:
            # Record failed payment
            if budget_manager:
                budget_manager.record_payment(
                    url="direct-invoice",
                    amount_sats=max_sats,
                    invoice=normalized_invoice,
                    preimage="",
                    status="failed",
                )
            return json.dumps({
                "success": False,
                "error": "Payment failed - no preimage returned"
            })

        # Record the payment
        if budget_service:
            budget_service.record_spend(max_sats)
            budget_service.record_payment_time()

            # Get updated session info
            status = budget_service.get_status()
            session_info = {
                "spentSats": status["session"]["spentSats"],
                "spentUsd": status["session"]["spentUsd"],
                "remainingUsd": status["session"]["remainingUsd"],
                "requestCount": status["session"]["requestCount"],
            }
        elif budget_manager:
            budget_manager.record_payment(
                url="direct-invoice",
                amount_sats=max_sats,
                invoice=normalized_invoice,
                preimage=preimage,
                status="success",
            )
            session_info = {
                "spentSats": budget_manager.session_spent,
                "remainingSats": budget_manager.max_per_session - budget_manager.session_spent,
            }
        else:
            session_info = None

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

        # Record failed payment
        if budget_manager:
            budget_manager.record_payment(
                url="direct-invoice",
                amount_sats=0,
                invoice=invoice,
                preimage="",
                status="failed",
            )

        return json.dumps({
            "success": False,
            "error": str(e)
        })
