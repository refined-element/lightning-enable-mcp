"""
Get BTC Price Tool

Get the current Bitcoin price in USD.
Only available with Strike wallet.
"""

import json
import logging
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.get_btc_price")


async def get_btc_price(
    wallet: "StrikeWallet | None" = None,
) -> str:
    """
    Get the current Bitcoin price in USD.

    Uses Strike's rate ticker API to get the current BTC/USD exchange rate.
    Only available with Strike wallet.

    Args:
        wallet: Strike wallet instance

    Returns:
        JSON with current BTC price in USD
    """
    if not wallet:
        return json.dumps({
            "success": False,
            "error": "Wallet not configured. Set STRIKE_API_KEY environment variable for price data."
        })

    # Verify it's a Strike wallet
    from ..strike_wallet import StrikeWallet
    if not isinstance(wallet, StrikeWallet):
        provider_name = type(wallet).__name__.replace("Wallet", "")
        return json.dumps({
            "success": False,
            "error": f"Price ticker is only available with Strike wallet. Current wallet: {provider_name}",
            "errorCode": "NOT_SUPPORTED",
            "hint": "Set STRIKE_API_KEY environment variable for price data."
        })

    try:
        result = await wallet.get_btc_price()

        if not result.success:
            return json.dumps({
                "success": False,
                "error": result.error_message,
                "errorCode": result.error_code,
            })

        return json.dumps({
            "success": True,
            "provider": "Strike",
            "ticker": {
                "btcUsd": float(result.btc_usd_price),
            },
            "message": f"Current BTC price: ${result.btc_usd_price:,.2f} USD"
        }, indent=2)

    except Exception as e:
        logger.exception("Error getting BTC price")
        return json.dumps({
            "success": False,
            "error": str(e)
        })
