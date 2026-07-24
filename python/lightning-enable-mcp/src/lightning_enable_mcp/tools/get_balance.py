"""
Get Balance Tool

Unified wallet-balance tool. Supersedes check_wallet_balance and get_all_balances,
returning a single superset shape for every backend:

- ``balance_sats`` / ``balance_btc`` — the PRIMARY wallet's scalar balance, ALWAYS
  present (the "strict superset" guarantee). It is the headline, taken from the
  primary wallet regardless of whether a Strike wallet is also configured.
- ``wallet_info`` — optional NWC ``get_info`` block (alias/network/block_height),
  present only where the primary backend provides it (preserved from
  check_wallet_balance).
- ``balances[]`` — the multi-currency array from a configured Strike wallet
  (supplementary), or a single derived BTC entry for a single-currency backend
  (from the old get_all_balances path).
- ``session`` — session spend summary when a budget service is available.

The primary wallet is ALWAYS the headline; a configured Strike wallet ADDS its
multi-currency ``balances[]`` — it never replaces the primary. Nothing either old
tool returned is dropped.
"""

import json
import logging
from . import sanitize_error
from ..wallet_messages import WALLET_NOT_CONFIGURED_FOR_RECEIVING
from typing import TYPE_CHECKING, Union

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..nwc_wallet import NWCWallet
    from ..opennode_wallet import OpenNodeWallet
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.get_balance")


def _wallet_type(wallet: object) -> str:
    """Map a wallet instance to a canonical type string (strike|nwc|lnd|opennode)."""
    name = type(wallet).__name__.lower()
    for key in ("strike", "nwc", "lnd", "opennode"):
        if key in name:
            return key
    return name.replace("wallet", "")


def _provider_name(wallet: object) -> str:
    """Human-facing provider name derived from the wallet class name."""
    return type(wallet).__name__.replace("Wallet", "")


def _balance_unavailable(wallet: object) -> str:
    """Honest error when a provider genuinely can't report a balance.

    Distinct from a zero balance: OpenNode's ``get_balance`` returns -1 when it has no
    balance endpoint, and we must NEVER fabricate that into a negative/phantom balance.
    """
    provider = _provider_name(wallet)
    return json.dumps({
        "success": False,
        "error": (
            f"Balance not available from {provider}. The provider did not report a "
            "balance (this is not a zero balance)."
        ),
        "errorCode": "BALANCE_UNAVAILABLE",
    })


async def _maybe_attach_wallet_info(response: dict, wallet: object) -> None:
    """Attach the NWC-style get_info enrichment block, best-effort.

    Preserved from check_wallet_balance. Not all wallets support get_info; failures are
    swallowed so a missing/unsupported info call never fails a balance read.
    """
    try:
        info = await wallet.get_info()
        if info and isinstance(info, dict):
            response["wallet_info"] = {
                "alias": info.get("alias"),
                "network": info.get("network"),
                "block_height": info.get("block_height"),
            }
    except Exception:
        # get_info is best-effort; not all wallets support it.
        pass


async def _attach_session(response: dict, budget_service: "BudgetService | None") -> None:
    """Attach the session spend summary (from the old get_all_balances logic).

    `get_status()` has no `remainingSats` key — deriving it avoids the historical bug
    of reporting a remaining budget of 0 forever. Unknown renders as null, never 0.
    """
    if not budget_service:
        return
    try:
        status = budget_service.get_status()
        session = status.get("session", {})
        remaining_sats = await budget_service.get_remaining_session_sats()
        response["session"] = {
            "spentSats": session.get("spentSats", 0),
            "remainingBudgetSats": remaining_sats,
            "requestCount": session.get("requestCount", 0),
        }
        if remaining_sats is None:
            response["session"]["remainingBudgetNote"] = (
                "Remaining budget is unknown (no session limit configured, or the "
                "BTC price is unavailable to convert the USD limit)."
            )
    except Exception:
        pass


def _format_strike_balances(balances) -> list:
    """Format Strike's multi-currency balances for display."""
    formatted = []
    for b in balances:
        entry = {
            "currency": b.currency,
            "available": float(b.available),
            "total": float(b.total),
            "pending": float(b.pending),
        }
        if b.currency == "BTC":
            sats = int(b.available * 100_000_000)
            entry["formatted"] = f"{b.available:.8f} BTC ({sats:,} sats)"
        else:
            entry["formatted"] = f"{b.available:,.2f} {b.currency}"
        formatted.append(entry)
    return formatted


async def get_balance(
    wallet: "Union[NWCWallet, OpenNodeWallet, StrikeWallet, None]" = None,
    strike_wallet: "StrikeWallet | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Get the wallet balance as a single superset shape (see module docstring).

    The PRIMARY wallet is always the headline (``balance_sats``/``balance_btc``/
    ``wallet_info``). If a Strike wallet is configured — either as the primary or as a
    separate multi-currency wallet — its per-currency ``balances[]`` is ADDED. Strike
    never replaces the primary's scalar balance.

    Args:
        wallet: Primary wallet instance (priority LND > NWC > Strike > OpenNode)
        strike_wallet: Strike wallet instance, when separate from the primary wallet
        budget_service: BudgetService for session stats

    Returns:
        JSON with balance information, or an error message.
    """
    from ..strike_wallet import StrikeWallet

    # A configured Strike wallet: the explicit strike_wallet, or the primary if it IS Strike.
    effective_strike = strike_wallet
    if effective_strike is None and isinstance(wallet, StrikeWallet):
        effective_strike = wallet

    # The primary wallet is the headline. Falls back to Strike only when it is the sole
    # wallet (Strike-only setup, where wallet is None but strike_wallet is set).
    base_wallet = wallet if wallet is not None else effective_strike

    if base_wallet is None:
        # Read-only tool: use the receiving-oriented guidance (any backend can report a
        # balance), not the payment-oriented message that wrongly rules OpenNode out.
        return json.dumps({
            "success": False,
            "error": WALLET_NOT_CONFIGURED_FOR_RECEIVING,
            "configured": False,
        })

    strike_is_primary = effective_strike is not None and base_wallet is effective_strike

    if effective_strike is not None:
        # ── Strike is configured: query it ONCE for the multi-currency balances[]. ──
        try:
            result = await effective_strike.get_all_balances()
        except Exception as e:
            logger.exception("Error getting all balances from Strike")
            return json.dumps({"success": False, "error": sanitize_error(str(e))})

        if not result.success:
            # Genuinely unavailable — surface it honestly, never a phantom balance.
            return json.dumps({
                "success": False,
                "error": result.error_message,
                "errorCode": result.error_code,
            })

        formatted_balances = _format_strike_balances(result.balances)

        if strike_is_primary:
            # Strike is ALSO the primary: take the headline scalar from the BTC entry of
            # the call we already made (matches .NET; no second /balances round-trip). A
            # USD-only account has no BTC entry -> honest 0 sats, never a dropped scalar.
            btc = next((b for b in result.balances if b.currency == "BTC"), None)
            balance_sats = int(btc.available * 100_000_000) if btc is not None else 0
            provider_name = "Strike"
            wallet_type = "strike"
            message = f"Retrieved {len(result.balances)} currency balance(s) from Strike"
            wallet_info_source = None
        else:
            # Dual-wallet (e.g. LND primary + separate Strike): the headline scalar comes
            # from the PRIMARY wallet, and Strike's balances[] is supplementary. This is
            # the fix for the hijack that used to report ONLY Strike and drop the primary.
            try:
                balance_sats = await base_wallet.get_balance()
            except Exception as e:
                logger.exception("Error getting balance")
                return json.dumps({"success": False, "error": sanitize_error(str(e))})
            if balance_sats is not None and balance_sats < 0:
                return _balance_unavailable(base_wallet)
            provider_name = _provider_name(base_wallet)
            wallet_type = _wallet_type(base_wallet)
            message = (
                f"Primary {provider_name} balance: {balance_sats:,} sats. "
                f"Plus {len(result.balances)} currency balance(s) from Strike."
            )
            wallet_info_source = base_wallet
    else:
        # ── Single-currency wallet (no Strike): exactly ONE get_balance round-trip. ──
        try:
            balance_sats = await base_wallet.get_balance()
        except Exception as e:
            logger.exception("Error getting balance")
            return json.dumps({"success": False, "error": sanitize_error(str(e))})

        # -1 is the "balance unavailable" sentinel (e.g. OpenNode has no balance endpoint).
        # Report it honestly instead of emitting a negative/phantom balance.
        if balance_sats is not None and balance_sats < 0:
            return _balance_unavailable(base_wallet)

        provider_name = _provider_name(base_wallet)
        wallet_type = _wallet_type(base_wallet)
        formatted_balances = [{
            "currency": "BTC",
            "available": balance_sats / 100_000_000,
            "total": balance_sats / 100_000_000,
            "pending": 0,
            "formatted": f"{balance_sats / 100_000_000:.8f} BTC ({balance_sats:,} sats)",
        }]
        message = (
            f"Retrieved BTC balance from {provider_name}. "
            "For multi-currency balances, use Strike wallet."
        )
        wallet_info_source = base_wallet

    response: dict = {
        "success": True,
        "wallet_type": wallet_type,
        "provider": provider_name,
        "balance_sats": balance_sats,
        "balance_btc": balance_sats / 100_000_000,
        "balances": formatted_balances,
        "message": message,
    }

    # NWC (and some others) expose extra info — preserved from check_wallet_balance.
    # Only attempted on the primary wallet, and only when it is not Strike itself.
    if wallet_info_source is not None:
        await _maybe_attach_wallet_info(response, wallet_info_source)

    await _attach_session(response, budget_service)

    return json.dumps(response, indent=2)
