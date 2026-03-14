"""
Create Invoice Tool

Create a Lightning invoice to receive a payment.
Returns a BOLT11 invoice string to share with the payer.
"""

import json
import logging
from typing import TYPE_CHECKING, Union

if TYPE_CHECKING:
    from ..lnd_wallet import LndWallet
    from ..nwc_wallet import NWCWallet
    from ..opennode_wallet import OpenNodeWallet
    from ..strike_wallet import StrikeWallet

logger = logging.getLogger("lightning-enable-mcp.tools.create_invoice")


async def create_invoice(
    amount_sats: int,
    memo: str | None = None,
    expiry_secs: int = 3600,
    wallet: "Union[LndWallet, NWCWallet, OpenNodeWallet, StrikeWallet, None]" = None,
) -> str:
    """
    Create a Lightning invoice to receive a payment.

    Returns a BOLT11 invoice string that can be shared with a payer.
    The payer can then use pay_invoice to pay it.

    Args:
        amount_sats: Amount to receive in satoshis
        memo: Optional description/memo for the invoice
        expiry_secs: Invoice expiry time in seconds. Defaults to 3600 (1 hour)
        wallet: Wallet instance

    Returns:
        JSON with invoice details including BOLT11 string to share with payer
    """
    if amount_sats <= 0:
        return json.dumps({
            "success": False,
            "error": "Amount must be greater than 0 sats"
        })

    if not wallet:
        return json.dumps({
            "success": False,
            "error": "Wallet not configured. Set LND_REST_HOST+LND_MACAROON_HEX, STRIKE_API_KEY, OPENNODE_API_KEY, or NWC_CONNECTION_STRING environment variable."
        })

    try:
        from ..lnd_wallet import LndWallet
        from ..strike_wallet import StrikeWallet
        from ..opennode_wallet import OpenNodeWallet

        if isinstance(wallet, LndWallet):
            # Create invoice via LND REST API
            inv_result = await wallet.create_invoice(
                amount_sats=amount_sats,
                memo=memo,
                expiry_secs=expiry_secs,
            )

            return json.dumps({
                "success": True,
                "provider": "LND",
                "invoice": {
                    "id": inv_result["invoice_id"],
                    "bolt11": inv_result["bolt11"],
                    "amountSats": inv_result["amount_sats"],
                },
                "message": f"Invoice created for {amount_sats} sats. Share the bolt11 string with the payer."
            }, indent=2)

        elif isinstance(wallet, StrikeWallet):
            # Create invoice via Strike API
            from decimal import Decimal
            amount_btc = Decimal(amount_sats) / Decimal("100000000")

            invoice_request = {
                "amount": {
                    "currency": "BTC",
                    "amount": str(amount_btc),
                },
            }
            if memo:
                invoice_request["description"] = memo

            result = await wallet._request("POST", "/invoices", invoice_request)
            invoice_id = result.get("invoiceId")
            bolt11 = result.get("quote") or result.get("lnInvoice")

            return json.dumps({
                "success": True,
                "provider": "Strike",
                "invoice": {
                    "id": invoice_id,
                    "bolt11": bolt11,
                    "amountSats": amount_sats,
                    "expiresAt": result.get("expiresAt"),
                },
                "message": f"Invoice created for {amount_sats} sats. Share the bolt11 string with the payer."
            }, indent=2)

        elif isinstance(wallet, OpenNodeWallet):
            # Create charge via OpenNode API
            charge_request = {
                "amount": amount_sats,
                "currency": "satoshis",
            }
            if memo:
                charge_request["description"] = memo

            result = await wallet._request("POST", "/charges", charge_request)
            charge_id = result.get("id")
            bolt11 = (result.get("lightning_invoice") or {}).get("payreq") or result.get("lightning_invoice")

            return json.dumps({
                "success": True,
                "provider": "OpenNode",
                "invoice": {
                    "id": charge_id,
                    "bolt11": bolt11,
                    "amountSats": amount_sats,
                },
                "message": f"Invoice created for {amount_sats} sats. Share the bolt11 string with the payer."
            }, indent=2)

        else:
            # NWC - try make_invoice NIP-47 method
            try:
                params = {
                    "amount": amount_sats * 1000,  # NWC uses millisats
                    "expiry": expiry_secs,
                }
                if memo:
                    params["description"] = memo

                response = await wallet._send_request("make_invoice", params)

                if response.get("error"):
                    error = response["error"]
                    return json.dumps({
                        "success": False,
                        "error": f"Failed to create invoice: {error.get('message', error)}"
                    })

                result = response.get("result", {})
                return json.dumps({
                    "success": True,
                    "provider": "NWC",
                    "invoice": {
                        "id": result.get("payment_hash"),
                        "bolt11": result.get("invoice"),
                        "amountSats": amount_sats,
                    },
                    "message": f"Invoice created for {amount_sats} sats. Share the bolt11 string with the payer."
                }, indent=2)

            except Exception as e:
                return json.dumps({
                    "success": False,
                    "error": f"Invoice creation failed: {str(e)}",
                    "hint": "Not all NWC wallets support invoice creation."
                })

    except Exception as e:
        logger.exception("Error creating invoice")
        return json.dumps({
            "success": False,
            "error": str(e)
        })
