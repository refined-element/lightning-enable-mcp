"""
Best-effort startup version check, mirroring the .NET server's ``Program.Main``.

On start the server asks the Lightning Enable API whether this version is current:
``GET {LIGHTNING_ENABLE_API_URL or https://api.lightningenable.com}/api/mcp/version-check
?currentVersion=<version>``. A required update prints a loud banner; a recommended update
prints one line. Everything goes to stderr, because stdout is the MCP stdio transport and a
stray line there desyncs the client.

The check never blocks serving and never fails the server: it runs as a background task with
a 5-second timeout, and any failure (network, HTTP status, malformed JSON) is swallowed.
"""

from __future__ import annotations

import logging
import os
import sys
from typing import Any, TextIO

import httpx

from ._version import USER_AGENT_PYTHON, __version__

logger = logging.getLogger("lightning-enable-mcp")

DEFAULT_API_URL = "https://api.lightningenable.com"
VERSION_CHECK_PATH = "/api/mcp/version-check"
VERSION_CHECK_TIMEOUT_SECONDS = 5.0

_PREFIX = "[Lightning Enable MCP]"
_RULE = "!" * 54


def version_check_url() -> str:
    """The version-check endpoint, honoring ``LIGHTNING_ENABLE_API_URL`` like the API client."""
    base_url = os.getenv("LIGHTNING_ENABLE_API_URL")
    base_url = base_url.rstrip("/") if base_url else DEFAULT_API_URL
    return f"{base_url}{VERSION_CHECK_PATH}"


def _string_field(payload: dict[str, Any], name: str) -> str | None:
    value = payload.get(name)
    return value if isinstance(value, str) else None


def report_version_check(payload: Any, stream: TextIO) -> None:
    """Print the update notice the response asks for, if any. Prints nothing otherwise."""
    if not isinstance(payload, dict):
        return

    if payload.get("updateRequired") is True:
        message = _string_field(payload, "message")
        notes = _string_field(payload, "releaseNotes")
        lines = ["", _RULE, f"{_PREFIX} CRITICAL UPDATE REQUIRED"]
        if message is not None:
            lines.append(f"{_PREFIX} {message}")
        if notes is not None:
            lines.append(f"{_PREFIX} What's fixed: {notes}")
        lines += [_RULE, ""]
        print("\n".join(lines), file=stream, flush=True)
    elif payload.get("updateRecommended") is True:
        latest = _string_field(payload, "latestVersion") or "unknown"
        print(
            f"{_PREFIX} Update available: v{latest}. "
            "Run: pip install --upgrade lightning-enable-mcp "
            "(or uvx --refresh lightning-enable-mcp)",
            file=stream,
            flush=True,
        )


async def check_for_updates(
    current_version: str = __version__,
    *,
    transport: httpx.AsyncBaseTransport | None = None,
    stream: TextIO | None = None,
) -> None:
    """Ask the API whether ``current_version`` is current and report to stderr. Never raises.

    ``transport`` and ``stream`` exist for tests; production uses the default network
    transport and ``sys.stderr``.
    """
    out = stream if stream is not None else sys.stderr
    try:
        async with httpx.AsyncClient(
            timeout=VERSION_CHECK_TIMEOUT_SECONDS,
            headers={"User-Agent": USER_AGENT_PYTHON, "Accept": "application/json"},
            transport=transport,
        ) as client:
            response = await client.get(
                version_check_url(), params={"currentVersion": current_version}
            )
            response.raise_for_status()
            payload = response.json()
        report_version_check(payload, out)
    except Exception as exc:  # noqa: BLE001 - best effort: never break the server
        # One short line, no traceback, and only at DEBUG (hidden at the default INFO level).
        logger.debug("Version check skipped: %s", type(exc).__name__)
