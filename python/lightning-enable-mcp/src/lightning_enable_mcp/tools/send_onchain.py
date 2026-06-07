"""
Send On-Chain Tool

Send an on-chain Bitcoin payment to a Bitcoin address.
Supports Strike and LND wallets.
"""

import json
import logging
import sys
from . import sanitize_error
from typing import TYPE_CHECKING, Optional, Union

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..lnd_wallet import LndWallet
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.send_onchain")


async def send_onchain(
    address: str,
    amount_sats: int,
    confirmation_code: "Optional[str]" = None,
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
        confirmation_code: The code the human read from the server console. On-chain sends
            are irreversible and ALWAYS require confirmation: the first call prints a code
            to the server console (never in the result) and returns requiresConfirmation;
            ask the human for the code and call again with confirmation_code set to it.
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

    # On-chain is irreversible, so it ALWAYS requires out-of-band confirmation and must
    # FAIL CLOSED if there is no budget/confirmation service to run that gate through.
    if budget_service is None:
        return json.dumps({
            "success": False,
            "error": "Budget/confirmation service is unavailable, so this on-chain send was refused "
                     "(fail-closed). On-chain payments are irreversible and must go through the "
                     "confirmation gate.",
        })

    # Budget check — FAIL CLOSED. A budget-check error (e.g. price feed down) REFUSES the
    # send rather than proceeding with no enforcement.
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

    # ALWAYS require OUT-OF-BAND confirmation (irreversible). The code is printed to the
    # server console (stderr) only — never in the result — so the human operator, not the
    # model, must read it and relay it back.
    address = address.strip()
    if confirmation_code:
        confirmation = budget_service.validate_and_consume_confirmation(
            confirmation_code.strip().upper(), amount_sats, "send_onchain"
        )
        if confirmation is None:
            return json.dumps({
                "success": False,
                "error": "Confirmation code is invalid, expired, already used, or does not match THIS "
                         "send's amount and tool. Codes are bound to the exact amount + tool approved.",
                "message": "Ask the human operator for the code shown in the server console, then call "
                           "send_onchain again with confirmation_code set to it.",
            })
        # Human-relayed code validated (amount + tool bound) — fall through and send.
    else:
        pending = budget_service.create_pending_confirmation(
            amount_sats, budget_result.amount_usd, "send_onchain", address
        )
        print(
            "[Lightning Enable] *** ON-CHAIN SEND CONFIRMATION REQUIRED (irreversible) ***\n"
            f"  send_onchain — {amount_sats:,} sats to {address}\n"
            f"  Confirmation code: {pending.nonce}\n"
            "  To approve, give this code to the agent. Expires in 120s.",
            file=sys.stderr,
            flush=True,
        )
        return json.dumps({
            "success": False,
            "requiresConfirmation": True,
            "error": "On-chain send requires human confirmation",
            "message": f"On-chain sends are irreversible, so this {amount_sats:,}-sat send to {address} requires "
                       "confirmation. A confirmation code was printed to the server console/logs — visible to the "
                       "human operator, NOT to you. Ask the human to read that code and give it to you.",
            "howToConfirm": "Ask the human operator for the confirmation code shown in the server console, then call "
                            'send_onchain(address="...", amount_sats=..., confirmation_code="<code-from-human>").',
            "amount": {"sats": amount_sats, "usd": float(budget_result.amount_usd)},
            "expiresInSeconds": 120,
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
