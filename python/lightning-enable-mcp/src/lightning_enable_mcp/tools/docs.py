"""``docs`` tool: search and read the Lightning Enable documentation.

Lets an agent answer "how do I set up NWC?" or "what does error X mean?" without
web access. The corpus is ``llms-full.txt`` (every docs page in one file), fetched
once and cached for an hour; see :mod:`lightning_enable_mcp.docs_service`. No API
key and no wallet are needed. Mirrors .NET ``Tools/DocsTool.cs``.
"""

import json
from typing import Any

from mcp.types import Tool

from ..docs_service import MAX_SEARCH_LIMIT, DocsService

DOCS_ACTIONS = ("search", "read")

DOCS_TOOL = Tool(
    name="docs",
    description=(
        "Search or read the Lightning Enable docs. "
        "search: query -> sections. read: path, optional section -> text."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "action": {"type": "string", "enum": list(DOCS_ACTIONS)},
            "query": {"type": "string"},
            "limit": {"type": "integer", "description": f"default 5, max {MAX_SEARCH_LIMIT}"},
            "path": {"type": "string", "description": "e.g. /nwc-setup/account-setup"},
            "section": {"type": "string", "description": "heading text"},
        },
        "required": ["action"],
    },
)


def _as_limit(value: Any) -> int | None | str:
    """The search limit, or an error message when it is not a whole number."""
    if value is None:
        return None
    if isinstance(value, bool):
        return "limit must be a whole number between 1 and 20."
    if isinstance(value, int):
        return value
    if isinstance(value, float) and value.is_integer():
        return int(value)
    if isinstance(value, str) and value.strip().lstrip("-").isdigit():
        return int(value.strip())
    return "limit must be a whole number between 1 and 20."


async def docs(
    action: str | None,
    query: str | None = None,
    limit: Any = None,
    path: str | None = None,
    section: str | None = None,
    docs_service: DocsService | None = None,
) -> str:
    """Dispatch a ``docs`` call. Always returns JSON text."""
    service = docs_service or DocsService()

    if action == "search":
        parsed = _as_limit(limit)
        if isinstance(parsed, str):
            result: dict[str, Any] = {"success": False, "error": parsed}
        else:
            result = await service.search(query, parsed)
    elif action == "read":
        result = await service.read(path, section)
    else:
        shown = "missing" if action is None else repr(action)
        result = {
            "success": False,
            "error": f"docs: action is {shown}. Set action to one of: search, read.",
            "tool": "docs",
            "valid_actions": list(DOCS_ACTIONS),
        }
    return json.dumps(result, indent=2, ensure_ascii=False)
