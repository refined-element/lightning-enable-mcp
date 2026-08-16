"""
Connect-time SSRF pin for the HTTP clients that fetch agent-supplied URLs (MCP-03).

``tools/_ssrf_guard.validate_url_allowed`` is a *resolve-then-validate* pre-check: it
resolves the host, rejects a private/metadata answer, then hands the URL to ``httpx`` —
which resolves the host AGAIN when it opens the socket. An attacker who controls the
authoritative DNS for a name can answer the guard's lookup with a public IP and
``httpx``'s lookup with a private one (a fast DNS rebind), slipping past the pre-check.
This is the residual TOCTOU window documented in ``_ssrf_guard``'s module docstring.

The .NET port closes it with a ``SocketsHttpHandler.ConnectCallback`` that validates the
address the socket is ABOUT TO CONNECT TO. Python's ``httpx`` exposes no connect-time
hook directly, but ``httpcore`` — the transport ``httpx`` sits on — lets us swap in a
custom async *network backend*. This module provides one:

* :class:`SsrfSafeAsyncBackend` — an ``httpcore`` async network backend whose
  ``connect_tcp(host, port, ...)`` resolves the host, validates EVERY candidate address
  with :func:`~lightning_enable_mcp.tools._ssrf_guard.is_blocked_ip` (the shared block
  logic — the ranges are NOT duplicated here), **fails closed** if any address is
  private/loopback/link-local/reserved/metadata (or the answer set is empty), and opens
  the TCP connection to a VALIDATED address. There is no third resolution — the socket
  goes to exactly the address that was validated.

* :func:`build_ssrf_safe_async_transport` — wires that backend into an
  ``httpx.AsyncHTTPTransport`` (over an ``httpcore.AsyncConnectionPool``) so every
  agent-controlled-URL client shares one guard.

TLS is preserved. ``httpcore`` performs the TLS handshake with ``server_hostname`` set
to the ORIGINAL request hostname (``AsyncHTTPConnection._connect`` calls ``start_tls``
with ``self._origin.host``, independent of the address ``connect_tcp`` dialed). So
connecting the TCP socket to the validated IP while leaving the URL host untouched keeps
SNI + certificate verification pointed at the real hostname. We do NOT rewrite the URL to
the raw IP and we do NOT disable verification — the pool keeps a normal verifying
``ssl_context`` (``verify=True`` by default).

The clients keep ``follow_redirects=False`` (set on the client, not here): the
connect-pin validates each hop it dials, but that does not make auto-redirect safe (a
redirect can still leak agent-supplied headers cross-origin, or drop an ``Authorization:
L402`` header on a host change), so redirects stay unfollowed and are surfaced as
actionable results by the calling tools.
"""

from __future__ import annotations

import asyncio
import ipaddress
import socket
import ssl
import typing

import httpcore
import httpx
from httpcore._backends.auto import AutoBackend
from httpcore._backends.base import SOCKET_OPTION, AsyncNetworkBackend, AsyncNetworkStream

# NOTE: the shared block logic + SsrfError live in ``tools._ssrf_guard``. They are imported
# LAZILY (inside the methods that use them) rather than at module top: importing any
# ``tools`` submodule runs ``tools/__init__``, which eagerly imports ``access_resource`` →
# ``l402_client``. Since ``l402_client`` imports THIS module at load time, a top-level
# import here would create a circular import. Deferring it keeps ``ssrf_transport``
# dependency-light at load and breaks the cycle.

# Resolver seam: hostname -> list of resolved IP strings. Injectable so the connect-time
# guard is unit-testable without touching real DNS (mirrors the .NET resolver test seam).
ConnectResolver = typing.Callable[[str], typing.Awaitable[list[str]]]

# Block predicate seam: an IP -> bool "is this a non-public/internal target?". Defaults to
# the shared is_blocked_ip. Injectable ONLY for tests that must reach a loopback TLS
# server; production wiring never overrides it.
BlockPredicate = typing.Callable[
    [ipaddress.IPv4Address | ipaddress.IPv6Address], bool
]


async def _default_connect_resolver(host: str) -> list[str]:
    """Resolve ``host`` to its IP strings via the event loop's async getaddrinfo."""
    loop = asyncio.get_running_loop()
    infos = await loop.getaddrinfo(host, None, proto=socket.IPPROTO_TCP)
    return [str(info[4][0]) for info in infos]


class SsrfSafeAsyncBackend(AsyncNetworkBackend):
    """An ``httpcore`` async network backend that pins each TCP connection to a
    connect-time-validated IP, closing the DNS-rebind TOCTOU window.

    Delegates the actual socket open to an inner backend (default: ``httpcore``'s own
    :class:`AutoBackend`, so asyncio/anyio/trio all keep working) but only ever asks it to
    connect to an address this guard has already validated.
    """

    def __init__(
        self,
        inner: AsyncNetworkBackend | None = None,
        resolver: ConnectResolver | None = None,
        is_blocked: BlockPredicate | None = None,
    ) -> None:
        self._inner = inner if inner is not None else AutoBackend()
        self._resolver = resolver if resolver is not None else _default_connect_resolver
        # None => use the shared is_blocked_ip (resolved lazily in _blocked to avoid the
        # import cycle). A non-None override is a TEST seam only.
        self._is_blocked_override = is_blocked

    def _blocked(
        self, ip: ipaddress.IPv4Address | ipaddress.IPv6Address
    ) -> bool:
        if self._is_blocked_override is not None:
            return self._is_blocked_override(ip)
        from .tools._ssrf_guard import is_blocked_ip

        return is_blocked_ip(ip)

    async def _validated_connect_host(self, host: str) -> str:
        """Return the exact address the socket should connect to, or raise
        :class:`SsrfError`. IP literals are validated directly; hostnames are resolved and
        EVERY answer is validated (fail closed on any private/empty), then the first
        validated address is returned to pin the connection."""
        from .tools._ssrf_guard import SsrfError

        # IP literal (incl. the target httpx would connect to) -> validate directly.
        try:
            literal = ipaddress.ip_address(host)
        except ValueError:
            literal = None
        if literal is not None:
            if self._blocked(literal):
                raise SsrfError()
            return host

        addresses = await self._resolver(host)

        # Fail closed on an empty answer set — no address to validate means nothing to
        # trust (parity with the pre-check + the .NET connect validator).
        if not addresses:
            raise SsrfError()

        validated: list[str] = []
        for address in addresses:
            try:
                ip = ipaddress.ip_address(address)
            except ValueError:
                # A resolver that returns something unparseable is treated as hostile.
                raise SsrfError() from None
            # Fail CLOSED on ANY blocked address in the set: a rebind answer of
            # [public, private] must not be salvaged by connecting to the public one.
            if self._blocked(ip):
                raise SsrfError()
            validated.append(address)

        # All validated -> pin the socket to the first validated address. No third
        # resolution happens, so httpx cannot be handed a different (swapped) IP.
        return validated[0]

    async def connect_tcp(
        self,
        host: str,
        port: int,
        timeout: float | None = None,
        local_address: str | None = None,
        socket_options: typing.Iterable[SOCKET_OPTION] | None = None,
    ) -> AsyncNetworkStream:
        connect_host = await self._validated_connect_host(host)
        return await self._inner.connect_tcp(
            connect_host,
            port,
            timeout=timeout,
            local_address=local_address,
            socket_options=socket_options,
        )

    async def connect_unix_socket(
        self,
        path: str,
        timeout: float | None = None,
        socket_options: typing.Iterable[SOCKET_OPTION] | None = None,
    ) -> AsyncNetworkStream:  # pragma: no cover - not used by the agent-URL clients
        # Unix-socket connects target a local path, not an agent-supplied host — there is
        # nothing to resolve/validate. Delegate straight through.
        return await self._inner.connect_unix_socket(
            path, timeout=timeout, socket_options=socket_options
        )

    async def sleep(self, seconds: float) -> None:  # pragma: no cover - retry backoff only
        await self._inner.sleep(seconds)


def build_ssrf_safe_async_transport(
    *,
    verify: bool | str | ssl.SSLContext = True,
    resolver: ConnectResolver | None = None,
    inner_backend: AsyncNetworkBackend | None = None,
    is_blocked: BlockPredicate | None = None,
    retries: int = 0,
) -> httpx.AsyncHTTPTransport:
    """Build an ``httpx.AsyncHTTPTransport`` whose connection pool pins every socket to a
    connect-time-validated IP via :class:`SsrfSafeAsyncBackend`.

    ``verify`` is passed to ``httpx.create_ssl_context`` unchanged (default ``True`` — a
    normal verifying context; TLS verification is NOT disabled). ``resolver`` /
    ``inner_backend`` / ``is_blocked`` are test seams; production callers pass none.
    """
    ssl_context = httpx.create_ssl_context(verify=verify)
    backend = SsrfSafeAsyncBackend(
        inner=inner_backend, resolver=resolver, is_blocked=is_blocked
    )

    # Build a stock transport, then swap its pool for one backed by the SSRF-safe network
    # backend (carrying the verifying ssl_context). handle_async_request only uses
    # self._pool, so this cleanly injects the guard while keeping normal httpx behaviour.
    transport = httpx.AsyncHTTPTransport(verify=verify)
    transport._pool = httpcore.AsyncConnectionPool(
        ssl_context=ssl_context,
        network_backend=backend,
        retries=retries,
        http1=True,
        http2=False,
    )
    return transport
