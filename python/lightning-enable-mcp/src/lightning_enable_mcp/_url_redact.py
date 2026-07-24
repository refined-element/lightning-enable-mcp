"""
Shared URL redaction.

A single source of truth for turning a request URL into a display-safe form before it is
printed to stderr, logged, or stored in the session payment history. The query string,
fragment, and userinfo can carry secrets (e.g. ``?token=...``), so keep only
``scheme://host[:port]/path`` and mark when anything was dropped (engineering standard #5 —
never log/store secrets). Used by both the tools (console prompts) and the L402 client (the
payment-history record), so the two never diverge.
"""

from urllib.parse import urlsplit, urlunsplit

__all__ = ["redact_url_for_display"]


def redact_url_for_display(url: str, limit: int = 50) -> str:
    """Return a display-safe URL with credentials stripped, capped at ``limit`` chars."""
    try:
        parts = urlsplit(url)
        host = parts.hostname or ""
        if ":" in host:  # IPv6 literal — urlsplit unbrackets it; re-bracket so host:port is unambiguous
            host = f"[{host}]"
        netloc = f"{host}:{parts.port}" if parts.port else host
        dropped = bool(parts.query or parts.fragment or parts.username or parts.password)
        safe = urlunsplit((parts.scheme, netloc, parts.path, "", ""))
        if dropped:
            safe = f"{safe} (redacted)"
    except Exception:
        safe = url.split("?", 1)[0].split("#", 1)[0]
        if "//" in safe:
            scheme_sep, rest = safe.split("//", 1)
            if "@" in rest:
                rest = rest.split("@", 1)[1]
            safe = scheme_sep + "//" + rest
    return safe[:limit] + "..." if len(safe) > limit else safe
