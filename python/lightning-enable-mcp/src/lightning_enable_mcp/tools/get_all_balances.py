"""
Get All Balances Tool

Get all currency balances from your wallet (USD, BTC, etc.).
Most useful with Strike wallet which supports multiple currencies.
"""

import json
import logging
from typing import TYPE_CHECKING, Union

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..nwc_wallet import NWCWallet
    from ..opennode_wallet import OpenNodeWallet
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.get_all_balances")


async def get_all_balances(
    wallet: "Union[NWCWallet, OpenNodeWallet, StrikeWallet, None]" = None,
    strike_wallet: "StrikeWallet | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Get all currency balances from your wallet (USD, BTC, etc.).

    Most useful with Strike wallet which supports multiple currencies.
    For NWC and OpenNode wallets, returns BTC balance only.

    Args:
        wallet: Primary wallet instance
        strike_wallet: Strike wallet instance (may be separate from primary wallet)
        budget_service: BudgetService for session stats

    Returns:
        JSON with all currency balances and session spending info
    """
    # Use strike_wallet if available, otherwise try primary wallet
    effective_strike = strike_wallet
    if effective_strike is None:
        from ..strike_wallet import StrikeWallet
        if isinstance(wallet, StrikeWallet):
            effective_strike = wallet

    if effective_strike is not None:
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
                "provider": "Strike",
                "balances": formatted_balances,
                "message": f"Retrieved {len(result.balances)} currency balance(s) from Strike",
            }

            # Add session info if budget service available
            if budget_service:
                try:
                    status = budget_service.get_status()
                    session = status.get("session", {})
                    response["session"] = {
                        "spentSats": session.get("spentSats", 0),
                        "remainingBudgetSats": session.get("remainingSats", 0),
                        "requestCount": session.get("requestCount", 0),
                    }
                except Exception:
                    pass

            return json.dumps(response, indent=2)

        except Exception as e:
            logger.exception("Error getting all balances from Strike")
            return json.dumps({
                "success": False,
                "error": str(e)
            })

    # Fallback to regular balance for non-Strike wallets
    if wallet:
        try:
            balance_sats = await wallet.get_balance()
            provider_name = type(wallet).__name__.replace("Wallet", "")

            response = {
                "success": True,
                "provider": provider_name,
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

            if budget_service:
                try:
                    status = budget_service.get_status()
                    session = status.get("session", {})
                    response["session"] = {
                        "spentSats": session.get("spentSats", 0),
                        "remainingBudgetSats": session.get("remainingSats", 0),
                        "requestCount": session.get("requestCount", 0),
                    }
                except Exception:
                    pass

            return json.dumps(response, indent=2)

        except Exception as e:
            logger.exception("Error getting balance")
            return json.dumps({
                "success": False,
                "error": str(e)
            })

    return json.dumps({
        "success": False,
        "error": "Wallet not configured. Set STRIKE_API_KEY, OPENNODE_API_KEY, or NWC_CONNECTION_STRING environment variable.",
        "configured": False,
    })
