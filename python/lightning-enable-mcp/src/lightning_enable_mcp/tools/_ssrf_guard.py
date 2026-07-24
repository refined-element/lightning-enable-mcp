"""
SSRF guard for MCP tools that fetch attacker-influenceable URLs.

Both ``access_l402_resource`` and ``discover_api`` fetch URLs chosen by the agent
(or by a third-party registry entry). Without a guard, a target of
``http://127.0.0.1/…``, ``http://169.254.169.254/…`` (cloud metadata), or any
RFC1918 / link-local host would be fetched on the operator's behalf — the classic
SSRF pivot into the internal network.

This module centralizes the check (F-10e). It:

* Rejects non-HTTP(S) schemes.
* Rejects obvious internal hostnames (``localhost``, ``*.internal``, the well-known
  cloud-metadata names) up front.
* Resolves the host and rejects if ANY resolved address is private, loopback,
  link-local, reserved, multicast, or unspecified — **fail closed** (one bad
  address in the set blocks the whole request). IPv4-mapped IPv6 addresses
  (``::ffff:169.254.169.254``) are unwrapped and re-checked.
* Allows the request when the host does not resolve, matching the sibling API
  repo's validator: the name may be configured later, and nothing is fetched
  until a real connection succeeds.

Error messages are deliberately generic — they never echo the resolved internal
host or IP, so a probe cannot use the tool as an internal-network oracle.

DNS-rebind (TOCTOU) residual window
-----------------------------------
This is a **resolve-then-validate** guard: it resolves and validates the host,
then hands the URL to ``httpx``, which resolves the host AGAIN when it opens the
socket. An attacker who controls the authoritative DNS for a name can answer the
guard's lookup with a public IP and ``httpx``'s lookup with a private one (a fast
DNS rebind), slipping past the check.

Fully closing that window requires validating the IP the socket actually connects
to (as the .NET port does with ``SocketsHttpHandler.ConnectCallback``). ``httpx``
exposes no equivalent connect-time hook; the only way to pin the connection to the
validated IP is to rewrite the request URL to the literal IP and carry the original
host as ``Host`` + TLS ``sni_hostname`` — a fragile workaround that changes the
request in ways that can break legitimate virtual-hosted / TLS endpoints, and which
cannot be verified here against a live server (loopback, the only local target, is
itself blocked by this guard). Per the "do not ship a fake fix" rule we do NOT ship
that hack; the residual rebind window is documented instead. It is narrow (requires
attacker-controlled authoritative DNS with a sub-lookup TTL) and does not affect the
direct-literal or resolve-to-private cases, which are fully closed here.
Recommended follow-up: a custom ``httpx`` transport that pins to the validated IP.
"""

from __future__ import annotations

import asyncio
import ipaddress
import socket
from collections.abc import Awaitable, Callable
from urllib.parse import urlsplit

# Resolver seam: takes a hostname, returns the list of resolved IP strings.
# Injectable so the guard's logic is unit-testable without touching the network.
Resolver = Callable[[str], Awaitable[list[str]]]

# Generic, non-leaking rejection message. Never interpolates the target host/IP.
_BLOCKED_MESSAGE = "Target host is not allowed (resolves to a private, loopback, or reserved address)."

# Hostnames that must never be fetched regardless of what they resolve to.
# MUST stay in sync with the .NET guard's blocked-hostname set in
# Services/SsrfUrlGuard.cs — the two ports block the same union set; update both
# together. (.localhost and .internal SUFFIXES are handled separately below.)
_BLOCKED_HOSTNAMES = frozenset(
    {
        "localhost",
        "metadata",
        "metadata.google.internal",
        "metadata.goog",
        "metadata.azure.com",
    }
)

# RFC 6598 shared address space (carrier-grade NAT). Python's ipaddress.is_private
# does NOT include this range, but it addresses internal CGNAT infrastructure and is
# a legitimate SSRF pivot, so block it explicitly. The .NET PrivateIpAddressDetector
# blocks the same range — the two ports must stay consistent.
_CGNAT_NETWORK = ipaddress.ip_network("100.64.0.0/10")


class SsrfError(Exception):
    """Raised when a URL targets a private/internal/reserved address.

    Carries only the generic, non-leaking message — callers can surface
    ``str(e)`` to the model without disclosing the internal target.
    """

    def __init__(self, message: str = _BLOCKED_MESSAGE) -> None:
        super().__init__(message)


def is_blocked_ip(ip: ipaddress.IPv4Address | ipaddress.IPv6Address) -> bool:
    """True if ``ip`` is private / loopback / link-local / reserved / multicast /
    unspecified — anything that is not a legitimate public fetch target.

    IPv4-mapped IPv6 addresses (e.g. ``::ffff:127.0.0.1``) are unwrapped to their
    IPv4 form first, so a mapped private address cannot slip through the IPv6 path.
    """
    if isinstance(ip, ipaddress.IPv6Address) and ip.ipv4_mapped is not None:
        ip = ip.ipv4_mapped

    # RFC 6598 CGNAT (100.64.0.0/10): not covered by is_private — block explicitly
    # (parity with the .NET detector). Guarded by isinstance so the network-version
    # match is correct.
    if isinstance(ip, ipaddress.IPv4Address) and ip in _CGNAT_NETWORK:
        return True

    return (
        ip.is_private
        or ip.is_loopback
        or ip.is_link_local  # includes 169.254.0.0/16 (cloud metadata) and fe80::/10
        or ip.is_reserved
        or ip.is_multicast
        or ip.is_unspecified
        # IPv6 site-local fec0::/10 (deprecated by RFC 3879 but still internally
        # routable). is_private does NOT cover it; is_site_local exists only on
        # IPv6Address, hence getattr. The .NET detector blocks fec0::/10 too.
        or getattr(ip, "is_site_local", False)
    )


async def _default_resolver(host: str) -> list[str]:
    """Resolve ``host`` to its IP strings via the event loop's async getaddrinfo."""
    loop = asyncio.get_running_loop()
    infos = await loop.getaddrinfo(host, None, proto=socket.IPPROTO_TCP)
    return [str(info[4][0]) for info in infos]


async def validate_url_allowed(url: str, *, resolver: Resolver | None = None) -> None:
    """Raise :class:`SsrfError` if ``url`` targets a private/internal/reserved host.

    Enforces http/https, blocks well-known internal hostnames, and resolves the host
    (unless it is already an IP literal), failing closed if ANY resolved address is
    non-public. Returns ``None`` (allow) when the host does not resolve — the
    connection would fail naturally and the name may be configured later. See the
    module docstring for the DNS-rebind residual window.
    """
    parsed = urlsplit(url)
    scheme = parsed.scheme.lower()
    if scheme not in ("http", "https"):
        raise SsrfError("Only http and https URLs are allowed.")

    host = parsed.hostname
    if not host:
        raise SsrfError("URL has no host.")

    lowered = host.lower()
    if (
        lowered in _BLOCKED_HOSTNAMES
        or lowered.endswith(".internal")
        or lowered.endswith(".localhost")
    ):
        raise SsrfError()

    # IP literal → validate directly, no resolution needed.
    try:
        literal = ipaddress.ip_address(host)
    except ValueError:
        literal = None
    if literal is not None:
        if is_blocked_ip(literal):
            raise SsrfError()
        return

    # Hostname → resolve and validate every answer (fail closed on any private).
    resolve = resolver or _default_resolver
    try:
        addresses = await resolve(host)
    except (OSError, socket.gaierror):
        # Unresolvable now — allow (matches the .NET/API reference). Nothing is
        # fetched until a real connection is made.
        return

    # Fail CLOSED on an empty resolution. This is distinct from the unresolvable case
    # above (which RAISES and is deliberately allowed): a resolver that returns an
    # empty list gave us no address to validate, so the validation loop below would be
    # skipped and the function would fall through to "allow". Treat it as hostile,
    # matching the .NET SsrfConnectValidator which throws on an empty address set.
    if not addresses:
        raise SsrfError()

    for address in addresses:
        try:
            ip = ipaddress.ip_address(address)
        except ValueError:
            # A resolver that returns something unparseable is treated as hostile.
            raise SsrfError() from None
        if is_blocked_ip(ip):
            raise SsrfError()
