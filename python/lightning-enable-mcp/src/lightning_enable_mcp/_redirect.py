"""
Shared redirect resolution.

Single source of truth for detecting an unfollowed 3xx redirect and resolving its
(possibly relative) Location to an absolute URL. Both HTTP paths run with
``follow_redirects=False`` (the L402 client and the discover_api manifest fetch), so
every redirect surfaces in code rather than being followed. This helper is used by BOTH
``l402_client.L402Client.fetch`` (initial fetch + paid retry) and
``tools.discover_api._try_fetch`` — extracted so the resolution logic exists in exactly
one place (the review flagged two divergent copies).
"""

from urllib.parse import urljoin

__all__ = ["resolve_redirect_location"]


def resolve_redirect_location(request_url: str, response: object) -> str | None:
    """Return the resolved (absolute) redirect target for an unfollowed 3xx, else ``None``.

    A 3xx WITHOUT a Location (e.g. a bare 304 Not Modified) is NOT a redirect and returns
    ``None`` so it flows through normal handling. A relative Location is resolved against
    ``request_url`` so the agent receives an absolute URL to re-call with. The target is
    surfaced verbatim (resolved to absolute) and leaks no internal detail — re-calling
    routes back through the SSRF guard, so a redirect pointing at an internal host is
    refused on that next call, not here.

    ``response`` is duck-typed: anything exposing ``status_code`` (int) and
    ``headers.get("location")`` (an ``httpx.Response`` in production, a mock in tests).
    """
    status_code = getattr(response, "status_code", None)
    if status_code is None or not (300 <= status_code < 400):
        return None
    location = response.headers.get("location")
    if not location:
        return None
    try:
        return urljoin(request_url, location)
    except Exception:
        return location
