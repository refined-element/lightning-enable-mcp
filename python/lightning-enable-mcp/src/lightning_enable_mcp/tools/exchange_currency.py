"""
Exchange Currency Tool

Exchange currency within your wallet (USD to BTC or BTC to USD).
Currently only available with Strike wallet.
"""

import json
import logging
from . import sanitize_error
from decimal import Decimal
from typing import TYPE_CHECKING, Union

if TYPE_CHECKING:
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.exchange_currency")


async def exchange_currency(
    source_currency: str,
    target_currency: str,
    amount: float,
    wallet: "StrikeWallet | None" = None,
) -> str:
    """
    Exchange currency within your wallet (USD to BTC or BTC to USD).

    Currently only available with Strike wallet which supports
    multi-currency accounts.

    Args:
        source_currency: Currency to convert from: USD or BTC
        target_currency: Currency to convert to: BTC or USD
        amount: Amount in source currency (e.g., 100 for $100 or 0.001 for 0.001 BTC)
        wallet: Strike wallet instance

    Returns:
        JSON with exchange result or error message
    """
    if not source_currency or not source_currency.strip():
        return json.dumps({
            "success": False,
            "error": "Source currency is required (USD or BTC)"
        })

    if not target_currency or not target_currency.strip():
        return json.dumps({
            "success": False,
            "error": "Target currency is required (BTC or USD)"
        })

    if amount <= 0:
        return json.dumps({
            "success": False,
            "error": "Amount must be greater than 0"
        })

    if not wallet:
        return json.dumps({
            "success": False,
            "error": "Currency exchange requires Strike wallet. Set STRIKE_API_KEY environment variable."
        })

    # Verify it's a Strike wallet
    from ..strike_wallet import StrikeWallet
    if not isinstance(wallet, StrikeWallet):
        provider_name = type(wallet).__name__.replace("Wallet", "")
        return json.dumps({
            "success": False,
            "error": f"{provider_name} does not support currency exchange. Use Strike wallet.",
            "errorCode": "NOT_SUPPORTED",
            "hint": "Set STRIKE_API_KEY environment variable for currency exchange."
        })

    try:
        result = await wallet.exchange_currency(
            source_currency=source_currency.strip(),
            target_currency=target_currency.strip(),
            amount=Decimal(str(amount)),
        )

        if not result.success:
            return json.dumps({
                "success": False,
                "error": result.error_message,
                "errorCode": result.error_code,
            })

        # Format amounts for display
        if result.source_currency == "BTC":
            source_formatted = f"{result.source_amount:.8f} BTC"
        else:
            source_formatted = f"${result.source_amount:,.2f} USD"

        if result.target_currency == "BTC":
            target_formatted = f"{result.target_amount:.8f} BTC"
        else:
            target_formatted = f"${result.target_amount:,.2f} USD"

        return json.dumps({
            "success": True,
            "provider": "Strike",
            "exchange": {
                "id": result.exchange_id,
                "sourceCurrency": result.source_currency,
                "targetCurrency": result.target_currency,
                "sourceAmount": float(result.source_amount) if result.source_amount else None,
                "targetAmount": float(result.target_amount) if result.target_amount else None,
                "rate": float(result.rate) if result.rate else None,
                "fee": float(result.fee) if result.fee else None,
                "state": result.state,
            },
            "message": f"Exchanged {source_formatted} for {target_formatted}"
        }, indent=2)

    except Exception as e:
        logger.exception("Error exchanging currency")
        return json.dumps({
            "success": False,
            "error": sanitize_error(str(e))
        })
