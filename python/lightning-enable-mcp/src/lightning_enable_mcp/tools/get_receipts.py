"""
Get Receipts Tool

Reads the durable, append-only payment receipt log
(``~/.lightning-enable/receipts.jsonl``) — the human-facing spend + revocation
record. Off the agent's hot path: rapid flows log to the file; this tool is how a
human (or the agent, on request) reviews what was spent and how to revoke.
"""

import json
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..receipt_service import ReceiptService


async def get_receipts(
    limit: int = 20,
    receipt_service: "ReceiptService | None" = None,
) -> str:
    """Return the most recent payment receipts from the durable log."""
    if receipt_service is None:
        return json.dumps({
            "success": False,
            "error": "Receipt logging is not available (no wallet/session initialized).",
        })

    # Clamp the window so a huge request can't be used to dump an unbounded read.
    try:
        limit = max(1, min(int(limit), 200))
    except (TypeError, ValueError):
        limit = 20

    receipts = receipt_service.read_recent(limit)
    # read_recent returns only dicts; still coerce defensively so a stray non-numeric
    # amountSats (hand-edit / format drift) can never crash the whole read.
    total_sats = 0
    for r in receipts:
        v = r.get("amountSats")
        if isinstance(v, bool):
            continue
        if isinstance(v, (int, float)):
            total_sats += int(v)

    return json.dumps({
        "success": True,
        "count": len(receipts),
        "totalSatsInView": total_sats,
        "logFile": str(receipt_service.path),
        "receipts": receipts,
        "note": "Append-only spend log — one payment receipt per line. Each includes how to revoke the wallet.",
    }, indent=2)
