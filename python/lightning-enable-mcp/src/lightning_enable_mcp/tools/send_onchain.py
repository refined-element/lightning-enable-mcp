"""
Send On-Chain Tool

Send an on-chain Bitcoin payment to a Bitcoin address.
Supports Strike and LND wallets.
"""

import asyncio
import hashlib
import json
import logging
from typing import TYPE_CHECKING, Optional, Union

from mcp.types import Tool

from ..confirmation_channel import ConfirmationRequest
from ..wallet_errors import onchain_signal
from ..operation_ledger import MONEY_MOVING_STATES, OperationLedger, OperationState
from . import sanitize_error

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..lnd_wallet import LndWallet
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.send_onchain")


def _normalize_address(address: str) -> str:
    """bech32 (bc1...) is case-insensitive; base58 (1.../3...) is case-sensitive."""
    a = (address or "").strip()
    return a.lower() if a.lower().startswith("bc1") else a


def onchain_operation_id(address: str, amount_sats: int, intent_id: Optional[str] = None) -> str:
    """Stable, non-secret idempotency key for an on-chain send:
    ``"onchain:" + sha256("onchain:" + normalized_address + ":" + amount_sats
    [+ ":" + intent_id.strip()])``. A None / blank intent_id is the same as omitting it.
    Mirrors the .NET port."""
    raw = f"onchain:{_normalize_address(address)}:{int(amount_sats)}"
    scope = intent_id.strip() if isinstance(intent_id, str) else ""
    if scope:
        raw += ":" + scope
    return "onchain:" + hashlib.sha256(raw.encode("utf-8")).hexdigest()


async def send_onchain(
    address: str,
    amount_sats: int,
    confirmation_nonce: "Optional[str]" = None,
    wallet: "Union[StrikeWallet, LndWallet, None]" = None,
    budget_service: "BudgetService | None" = None,
    operation_ledger: "OperationLedger | None" = None,
    intent_id: "Optional[str]" = None,
) -> str:
    """
    Send an on-chain Bitcoin payment to a Bitcoin address.

    Supports Strike and LND wallets. The payment is sent from your
    wallet balance.

    Args:
        address: Bitcoin address to send to (e.g., bc1q...)
        amount_sats: Amount to send in satoshis
        confirmation_nonce: The code the human read from the server console. On-chain sends
            are irreversible and ALWAYS require confirmation: the first call prints a code
            to the server console (never in the result) and returns requiresConfirmation;
            ask the human for the code and call again with confirmation_nonce set to it.
        wallet: Strike or LND wallet instance
        budget_service: BudgetService for spending limits
        operation_ledger: Durable idempotency ledger. When set, the send is keyed by
            ``onchain_operation_id(address, amount_sats)``; a second call for the same
            address+amount while a prior one may have moved money (SUBMITTED / PENDING /
            UNKNOWN / SETTLED) reports that payment's status instead of re-sending. Only a
            recorded FAILED (proven no funds moved) allows a fresh send.
        intent_id: Optional idempotency scope. Omit for normal use. Supply a NEW value only
            to intentionally pay the same address the same amount again; a blank value is
            the same as omitting it. The confirmation code binding (amount, tool, address)
            is unchanged, so a fresh code is still required for any real send.

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

    # Verify it's a supported wallet type. The wallet may arrive wrapped in the
    # receipt seam (ReceiptRecordingWallet) — unwrap for the type check only; the
    # actual send below goes through the WRAPPED wallet so the receipt is written.
    from ..lnd_wallet import LndWallet
    from ..receipt_seam import POLICY_HUMAN_CONFIRMED, PaymentReceiptScope, unwrap_wallet
    from ..strike_wallet import StrikeWallet
    inner_wallet = unwrap_wallet(wallet)
    if not isinstance(inner_wallet, (StrikeWallet, LndWallet)):
        provider_name = type(inner_wallet).__name__.replace("Wallet", "")
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

    # Idempotency: a prior attempt for this exact address+amount that may have moved money
    # is reported (with a provider status refresh when available), never re-sent. This runs
    # BEFORE the confirmation gate: reporting status moves no funds and needs no code.
    address = address.strip()
    operation_id = onchain_operation_id(address, amount_sats, intent_id)
    provider_name = "LND" if isinstance(inner_wallet, LndWallet) else "Strike"
    if operation_ledger is not None:
        existing = operation_ledger.lookup(operation_id)
        if existing is not None and existing.state in MONEY_MOVING_STATES:
            return await _already_submitted_response(
                operation_ledger, operation_id, existing, inner_wallet, provider_name
            )

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
    if confirmation_nonce:
        confirmation = budget_service.validate_and_consume_confirmation(
            confirmation_nonce.strip().upper(), amount_sats, "send_onchain", address
        )
        if confirmation is None:
            return json.dumps({
                "success": False,
                "error": "Confirmation code is invalid, expired, already used, or does not match THIS "
                         "send's amount, tool, and address. Codes are bound to the exact amount, tool, and "
                         "destination approved — a code cannot be redirected to a different address.",
                "message": "Ask the human operator for the code shown in the server console, then call "
                           "send_onchain again with confirmation_nonce set to it.",
            })
        # Human-relayed code validated (amount + tool + address bound) — fall through and send.
    else:
        # Code to the human on the CONFIGURED approval channel — the model never sees it, on
        # any channel. On a "refuse" server there is no code at all and the (irreversible)
        # send is turned down rather than left half-approved.
        dispatch = await budget_service.request_confirmation(
            ConfirmationRequest(
                amount_sats=amount_sats,
                amount_usd=budget_result.amount_usd,
                tool_name="send_onchain",
                description=address,
                destination=address,
                title="ON-CHAIN SEND CONFIRMATION REQUIRED (irreversible)",
                summary=f"send_onchain — {amount_sats:,} sats to {address}",
            )
        )
        if not dispatch.delivered:
            return json.dumps({
                "success": False,
                "requiresConfirmation": False,
                "confirmationChannel": dispatch.channel_name,
                "error": dispatch.refusal_reason,
                "message": "The send was REFUSED, not queued for approval — no human can be asked for a "
                           "code on this server. Retrying will not help until the operator changes the "
                           "configuration.",
                "amount": {"sats": amount_sats, "usd": float(budget_result.amount_usd)},
            })
        return json.dumps({
            "success": False,
            "requiresConfirmation": True,
            "confirmationChannel": dispatch.channel_name,
            "error": "On-chain send requires human confirmation",
            "message": f"On-chain sends are irreversible, so this {amount_sats:,}-sat send to {address} requires "
                       f"confirmation. A confirmation code was {dispatch.operator_hint} — visible to the "
                       "human operator, NOT to you. Ask the human to read that code and give it to you.",
            "howToConfirm": "Ask the human operator for the confirmation code, then call "
                            'send_onchain(address="...", amount_sats=..., confirmation_nonce="<code-from-human>").',
            "amount": {"sats": amount_sats, "usd": float(budget_result.amount_usd)},
            "expiresInSeconds": dispatch.expires_in_seconds,
        })

    # Reserve principal + a fee headroom BEFORE broadcasting. On-chain fees are added by the
    # provider ON TOP of the principal, so reserving (and checking) only the principal would
    # let the final debit (principal + fee) exceed the session cap. Reserve the maximum,
    # commit the actual debit, and the unused headroom is released automatically. Headroom =
    # max(1000 sats, 10% of principal). This also fixes the fee-undercount.
    fee_headroom_sats = max(1000, amount_sats // 10)
    reservation = await budget_service.try_reserve(amount_sats + fee_headroom_sats)
    if not reservation.success:
        return json.dumps({
            "success": False,
            "error": f"Budget check failed: {reservation.denial_reason}",
        })
    reservation_id = reservation.reservation_id

    # Claim the operation in the durable ledger. The lookup + SUBMITTED write below run
    # with NO await between them, so under the single asyncio loop two concurrent calls
    # for the same address+amount cannot both claim it: the loser sees SUBMITTED.
    if operation_ledger is not None:
        existing = operation_ledger.lookup(operation_id)
        if existing is not None and existing.state in MONEY_MOVING_STATES:
            budget_service.release_reservation(reservation_id)
            return await _already_submitted_response(
                operation_ledger, operation_id, existing, inner_wallet, provider_name
            )
        operation_ledger.record_submitted(
            operation_id, amount_sats, provider_name.lower(), kind="onchain"
        )

    def _commit(debit_sats: int) -> None:
        try:
            budget_service.commit_reservation(reservation_id, debit_sats)
            budget_service.record_payment_time()
        except Exception:
            logger.warning("Failed to commit on-chain budget reservation", exc_info=True)

    def _record(state, **ids) -> None:
        if operation_ledger is not None:
            operation_ledger.record_outcome(operation_id, state, None, **ids)

    def _ambiguous_debit(fee) -> int:
        # Principal + known fee, else principal + the reserved headroom (worst case).
        return amount_sats + (fee if isinstance(fee, int) and not isinstance(fee, bool) else fee_headroom_sats)

    # Ambient payment intent: the durable receipt is written at the wallet seam
    # (ReceiptRecordingWallet) when the send succeeds or may have executed. The destination
    # address is public chain data, so it is safe as receipt context; reaching this point
    # requires the human confirmation code, so policy is always "confirm".
    receipt_scope = PaymentReceiptScope(
        "onchain", context=address, policy=POLICY_HUMAN_CONFIRMED
    )
    try:
        with receipt_scope:
            result = await wallet.send_onchain(address, amount_sats)

    except asyncio.CancelledError as e:
        # CancelledError is a BaseException, so the `except Exception` below never sees it.
        # If the wallet PROVED the cancel landed before the execute call, nothing moved:
        # release. Otherwise (execute issued, or unknown) funds may have moved: commit the
        # reservation and record UNKNOWN so a retry reports status instead of re-sending.
        if onchain_signal(e, "onchain_submitted") is False:
            budget_service.release_reservation(reservation_id)
            _record(OperationState.FAILED)
        else:
            _commit(_ambiguous_debit(onchain_signal(e, "onchain_fee_sats")))
            _record(
                OperationState.UNKNOWN,
                payment_id=onchain_signal(e, "onchain_payment_id"),
                quote_id=onchain_signal(e, "onchain_quote_id"),
            )
        raise

    except Exception as e:
        logger.exception("Error sending on-chain payment")
        if onchain_signal(e, "onchain_submitted") is False:
            budget_service.release_reservation(reservation_id)
            _record(OperationState.FAILED)
            return json.dumps({
                "success": False,
                "error": sanitize_error(str(e)),
                "receipt_written": receipt_scope.receipt_written,
                "message": "The send failed before it was submitted to the provider; no funds moved.",
            })
        # The wallet threw without proving the send was never submitted: treat it as
        # ambiguous. Retain the budget and block a blind re-send.
        payment_id = onchain_signal(e, "onchain_payment_id")
        _commit(_ambiguous_debit(onchain_signal(e, "onchain_fee_sats")))
        _record(OperationState.UNKNOWN, payment_id=payment_id,
                quote_id=onchain_signal(e, "onchain_quote_id"))
        return json.dumps({
            "success": False,
            "error": sanitize_error(str(e)),
            "errorCode": "OUTCOME_UNKNOWN",
            "state": "UNKNOWN",
            "paymentId": payment_id,
            "quoteId": onchain_signal(e, "onchain_quote_id"),
            "txId": None,
            "provider": provider_name,
            "receipt_written": receipt_scope.receipt_written,
            "amountSats": amount_sats,
            "warning": AMBIGUOUS_SEND_WARNING,
        })

    success = getattr(result, "success", False) is True
    submitted = getattr(result, "submitted", None)
    if not isinstance(submitted, bool):
        # Legacy result without the signal: only a success proves submission.
        submitted = success
    state = getattr(result, "state", None)
    state = state.upper() if isinstance(state, str) else ""
    payment_id = getattr(result, "payment_id", None)
    quote_id = getattr(result, "quote_id", None)
    txid = getattr(result, "txid", None)
    ids = {
        "payment_id": payment_id if isinstance(payment_id, str) else None,
        "quote_id": quote_id if isinstance(quote_id, str) else None,
        "tx_id": txid if isinstance(txid, str) else None,
    }

    if not success and (not submitted or state == "FAILED"):
        # Proven no funds moved: never submitted, or the provider definitively
        # rejected/failed it. Release the reservation and allow a genuine retry.
        budget_service.release_reservation(reservation_id)
        _record(OperationState.FAILED, **ids)
        return json.dumps({
            "success": False,
            "error": result.error_message,
            "errorCode": result.error_code,
            "state": state or None,
            "receipt_written": receipt_scope.receipt_written,
            "message": (
                "The provider did not execute this send; no funds moved and the budget "
                "reservation was released. It is safe to retry."
                if submitted else
                "The send failed before it was submitted to the provider; no funds moved "
                "and the budget reservation was released."
            ),
        })

    if not success:
        # Submitted, outcome UNKNOWN (timeout / transport error / cancel after execute).
        fee = getattr(result, "fee_sats", None)
        _commit(_ambiguous_debit(fee))
        _record(OperationState.UNKNOWN, **ids)
        return json.dumps({
            "success": False,
            "error": result.error_message,
            "errorCode": "OUTCOME_UNKNOWN",
            "provider": provider_name,
            "state": "UNKNOWN",
            "paymentId": ids["payment_id"],
            "quoteId": ids["quote_id"],
            "txId": ids["tx_id"],
            "receipt_written": receipt_scope.receipt_written,
            "amountSats": amount_sats,
            "warning": AMBIGUOUS_SEND_WARNING,
        })

    # Accepted by the provider. Commit the ACTUAL debit (principal + network fee).
    # Committing less than the reserved maximum automatically releases the unused headroom.
    fee = getattr(result, "fee_sats", None)
    _commit(amount_sats + (fee if isinstance(fee, int) else 0))
    _record(OperationState.SETTLED if state == "COMPLETED" else OperationState.PENDING, **ids)

    if state == "COMPLETED":
        message = f"On-chain payment of {amount_sats} sats sent to {address}"
    else:
        message = (
            f"On-chain payment initiated (status: {result.state}). On-chain confirmation "
            "normally takes ~10+ minutes. Calling send_onchain again with the same address "
            "and amount will report this payment's status rather than re-send."
        )

    response = {
        "success": True,
        "provider": provider_name,
        "receipt_written": receipt_scope.receipt_written or False,
        "state": result.state,
        "paymentId": result.payment_id,
        "payment": {
            "id": result.payment_id,
            "txId": result.txid,
            "state": result.state,
            "amountSats": result.amount_sats,
            "feeSats": result.fee_sats,
        },
        "message": message,
    }
    if state == "PENDING":
        response["note"] = PENDING_NOTE
    return json.dumps(response, indent=2)


AMBIGUOUS_SEND_WARNING = (
    "The send may have executed at the provider even though this call could not confirm "
    "it. The budget for it has been retained (not released). Calling send_onchain again "
    "with the same address and amount will report this payment's status rather than "
    "re-send. Check the provider dashboard / get_balance BEFORE retrying with any other "
    "parameters — on-chain payments are irreversible."
)

PENDING_NOTE = (
    "On-chain payments normally stay PENDING for ~10 minutes until confirmed. Do NOT send "
    "again: calling send_onchain again with the same address and amount will report this "
    "payment's status rather than re-send."
)

PLAIN_FAILURE_WARNING = (
    "If this failure was a network/timeout error, the send may still have executed at "
    "the provider. Check the provider dashboard / get_balance BEFORE retrying — on-chain "
    "payments are irreversible."
)


def _blocked_retry_message(payment_id: Optional[str], provider: str) -> str:
    return (
        "Nothing was sent by this call: an on-chain send for the same address and amount was "
        f"already submitted (payment id {payment_id or 'not recorded'}). On-chain payments are "
        "irreversible, so it will not be sent again while that payment may have moved funds. "
        f"Verify its status at the provider ({provider}) before doing anything else. To "
        "intentionally pay this address this amount again, supply a new intent_id."
    )


_PROVIDER_STATE_TO_LEDGER = {
    "COMPLETED": OperationState.SETTLED,
    "PENDING": OperationState.PENDING,
    "FAILED": OperationState.FAILED,
}


async def _already_submitted_response(
    ledger, operation_id: str, record, inner_wallet, provider_name: str
) -> str:
    """A prior attempt for this exact address+amount may have moved money. Never send
    again: refresh the provider status when the wallet supports it, else refuse, naming
    the recorded payment id so the agent/operator can verify at the provider."""
    payment_id = record.payment_id
    state = record.state.value.upper()
    tx_id = record.tx_id
    lookup = getattr(inner_wallet, "get_onchain_payment_status", None)
    attempted = False
    refreshed = False

    if payment_id and callable(lookup):
        attempted = True
        try:
            status = await lookup(payment_id)
        except Exception:
            logger.warning("On-chain status lookup failed for a recorded operation", exc_info=True)
            status = None
        if status is not None and getattr(status, "success", False) is True:
            refreshed = True
            provider_state = str(getattr(status, "state", "") or "UNKNOWN").upper()
            new_tx = getattr(status, "txid", None) or tx_id
            ledger_state = _PROVIDER_STATE_TO_LEDGER.get(provider_state, record.state)
            if ledger_state != record.state or new_tx != tx_id:
                ledger.record_outcome(operation_id, ledger_state, None, tx_id=new_tx)
            state, tx_id = provider_state, new_tx

    # No confirmation code minted or consumed, no budget reserved, no wallet send.
    return json.dumps({
        "success": False,
        "errorCode": "ALREADY_SUBMITTED",
        "duplicate": True,
        "error": "This on-chain send was already submitted; it was not sent again.",
        "state": state,
        "paymentId": payment_id,
        "quoteId": record.quote_id,
        "txId": tx_id,
        "provider": provider_name,
        "statusLookup": {"attempted": attempted, "succeeded": refreshed},
        "receipt_written": False,
        "message": _blocked_retry_message(payment_id, provider_name),
    })


# MCP tool schema (lives beside its handler; registered in tools/registry.py).
SEND_ONCHAIN_TOOL = Tool(
    name="send_onchain",
    description=(
        "Send an on-chain Bitcoin payment to a Bitcoin address. "
        "Currently only available with Strike wallet."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "address": {
                "type": "string",
                "description": "Bitcoin address to send to (e.g., bc1q...)",
            },
            "amount_sats": {
                "type": "integer",
                "description": "Amount to send in satoshis",
            },
            "confirmation_nonce": {
                "type": "string",
                "description": (
                    "Confirmation code the human operator read from the server console. "
                    "On-chain sends always require it: the first call prints a code to the "
                    "console (never in the result) and returns requiresConfirmation; ask the "
                    "human and call again with confirmation_nonce set to it."
                ),
            },
            "intent_id": {
                "type": "string",
                "description": (
                    "Optional idempotency scope. Omit for normal use. Supply a NEW value only "
                    "when you intentionally need to pay the same address the same amount "
                    "again; a repeat with the same address, amount and intent_id is reported "
                    "as status, never re-sent."
                ),
            },
        },
        "required": ["address", "amount_sats"],
    },
)
