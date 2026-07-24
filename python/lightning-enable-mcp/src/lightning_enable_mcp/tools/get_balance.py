"""
Get Balance Tool

Unified wallet-balance tool. Supersedes check_wallet_balance and get_all_balances,
returning a single superset shape for every backend:

- ``balance_sats`` / ``balance_btc`` — the scalar balance (from the old
  check_wallet_balance path, or derived from Strike's BTC entry)
- ``wallet_info`` — optional NWC ``get_info`` block (alias/network/block_height),
  present only where the backend provides it (preserved from check_wallet_balance)
- ``balances[]`` — multi-currency for Strike, a single BTC entry otherwise
  (from the old get_all_balances path)
- ``session`` — session spend summary when a budget service is available

Nothing either old tool returned is dropped.
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


async def get_balance(
    wallet: "Union[NWCWallet, OpenNodeWallet, StrikeWallet, None]" = None,
    strike_wallet: "StrikeWallet | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Get the wallet balance as a single superset shape (see module docstring).

    Args:
        wallet: Primary wallet instance
        strike_wallet: Strike wallet instance (may be separate from the primary wallet)
        budget_service: BudgetService for session stats

    Returns:
        JSON with balance information, or an error message.
    """
    # Use strike_wallet if available, otherwise try the primary wallet.
    effective_strike = strike_wallet
    if effective_strike is None:
        from ..strike_wallet import StrikeWallet
        if isinstance(wallet, StrikeWallet):
            effective_strike = wallet

    # Base wallet used for the scalar sats figure + NWC wallet_info enrichment.
    base_wallet = wallet if wallet is not None else effective_strike

    if base_wallet is None:
        return json.dumps({
            "success": False,
            "error": WALLET_NOT_CONFIGURED_FOR_RECEIVING,
            "configured": False,
        })

    if effective_strike is not None:
        # ── Strike multi-currency path (old get_all_balances Strike branch) ──
        try:
            result = await effective_strike.get_all_balances()

            if not result.success:
                return json.dumps({
                    "success": False,
                    "error": result.error_message,
                    "errorCode": result.error_code,
                })

            formatted_balances = []
            for b in result.balances:
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
                formatted_balances.append(entry)

            response: dict = {
                "success": True,
                "wallet_type": "strike",
                "provider": "Strike",
                "balances": formatted_balances,
                "message": f"Retrieved {len(result.balances)} currency balance(s) from Strike",
            }

            # Derive the scalar sats figure from the BTC entry for the superset shape.
            btc = next((b for b in result.balances if b.currency == "BTC"), None)
            if btc is not None:
                response["balance_sats"] = int(btc.available * 100_000_000)
                response["balance_btc"] = float(btc.available)

        except Exception as e:
            logger.exception("Error getting all balances from Strike")
            return json.dumps({
                "success": False,
                "error": sanitize_error(str(e)),
            })
    else:
        # ── Single-wallet path (old check_wallet_balance + get_all_balances fallback) ──
        try:
            balance_sats = await base_wallet.get_balance()
        except Exception as e:
            logger.exception("Error getting balance")
            return json.dumps({
                "success": False,
                "error": sanitize_error(str(e)),
            })

        provider_name = type(base_wallet).__name__.replace("Wallet", "")
        response = {
            "success": True,
            "wallet_type": _wallet_type(base_wallet),
            "provider": provider_name,
            "balance_sats": balance_sats,
            "balance_btc": balance_sats / 100_000_000,
            "balances": [{
                "currency": "BTC",
                "available": balance_sats / 100_000_000,
                "total": balance_sats / 100_000_000,
                "pending": 0,
                "formatted": f"{balance_sats / 100_000_000:.8f} BTC ({balance_sats:,} sats)",
            }],
            "message": f"Retrieved BTC balance from {provider_name}. "
                       "For multi-currency balances, use Strike wallet.",
        }

        # NWC (and some others) expose extra info — preserved from check_wallet_balance.
        try:
            info = await base_wallet.get_info()
            if info and isinstance(info, dict):
                response["wallet_info"] = {
                    "alias": info.get("alias"),
                    "network": info.get("network"),
                    "block_height": info.get("block_height"),
                }
        except Exception:
            # get_info is best-effort; not all wallets support it.
            pass

    await _attach_session(response, budget_service)

    return json.dumps(response, indent=2)
