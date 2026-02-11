"""
Wallet Tools

Check wallet balance and status via NWC.
"""

import json
import logging
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..nwc_wallet import NWCWallet

logger = logging.getLogger("lightning-enable-mcp.tools.wallet")


async def check_wallet_balance(
    wallet: "NWCWallet | None" = None,
) -> str:
    """
    Check the connected Lightning wallet balance via NWC.

    Returns:
        JSON with balance information or error message
    """
    if not wallet:
        return json.dumps(
            {"success": False, "error": "Wallet not initialized. Check NWC connection."}
        )

    try:
        # Get balance
        balance_sats = await wallet.get_balance()

        result = {
            "success": True,
            "balance_sats": balance_sats,
            "balance_btc": balance_sats / 100_000_000,
            "message": f"Wallet balance: {balance_sats:,} sats",
        }

        # Try to get additional info
        try:
            info = await wallet.get_info()
            if info:
                result["wallet_info"] = {
                    "alias": info.get("alias"),
                    "network": info.get("network"),
                    "block_height": info.get("block_height"),
                }
        except Exception:
            # get_info might not be supported by all wallets
            pass

        return json.dumps(result, indent=2)

    except Exception as e:
        logger.exception("Error checking wallet balance")
        return json.dumps({"success": False, "error": str(e)})
