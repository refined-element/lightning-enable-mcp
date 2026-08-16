"""
Connect-time SSRF pin tests (MCP-03).

The pre-request ``validate_url_allowed`` guard resolves-then-validates, but httpx
independently re-resolves the host to open the socket — a DNS-rebind TOCTOU window
(public IP to the validator, private/metadata IP to the socket). These tests exercise
the authoritative gate: a custom httpcore network backend that resolves + validates the
host AT CONNECT TIME and pins the socket to a validated address, failing closed on any
private/metadata answer.

Everything here is offline-deterministic: an injected resolver seam supplies the
"resolved" addresses and a spy/fake inner backend stands in for the real socket, so no
test touches DNS or opens a real connection (except the one clearly-marked local TLS
handshake test, which binds a self-signed server to loopback).
"""

from __future__ import annotations

import ssl
from unittest.mock import AsyncMock

import httpx
import pytest
from httpcore._backends.base import AsyncNetworkBackend, AsyncNetworkStream

from lightning_enable_mcp.ssrf_transport import (
    SsrfSafeAsyncBackend,
    build_ssrf_safe_async_transport,
)
from lightning_enable_mcp.tools._ssrf_guard import SsrfError


def _resolver(*addresses: str):
    """A connect-time resolver seam returning fixed IP strings (no DNS)."""

    async def _resolve(host: str) -> list[str]:
        return list(addresses)

    return _resolve


class _SpyBackend(AsyncNetworkBackend):
    """Records connect_tcp calls so a test can assert a socket was (never) opened.

    Returns a sentinel stream when a connection IS expected; the security tests assert
    ``connect_calls == []`` — i.e. the guard refused BEFORE any socket was opened.
    """

    def __init__(self, stream: object | None = None) -> None:
        self.connect_calls: list[tuple[str, int]] = []
        self._stream = stream

    async def connect_tcp(
        self, host, port, timeout=None, local_address=None, socket_options=None
    ):
        self.connect_calls.append((host, port))
        return self._stream


class _FakeStream(AsyncNetworkStream):
    """A fake network stream that serves a canned HTTP/1.1 response and records the
    ``server_hostname`` handed to ``start_tls`` — the SNI-preservation proof."""

    def __init__(self, response_bytes: bytes) -> None:
        self._buf = bytearray(response_bytes)
        self.tls_server_hostname: str | None = None
        self.start_tls_called = False
        self.written = bytearray()

    async def read(self, max_bytes, timeout=None):
        if not self._buf:
            return b""
        chunk = bytes(self._buf[:max_bytes])
        del self._buf[:max_bytes]
        return chunk

    async def write(self, buffer, timeout=None):
        self.written.extend(buffer)

    async def aclose(self):
        pass

    async def start_tls(self, ssl_context, server_hostname=None, timeout=None):
        # Record the SNI/cert-verification hostname the connection layer requested.
        # We keep serving the same buffered bytes over the "TLS" stream — no real
        # handshake, so this stays deterministic and offline.
        self.start_tls_called = True
        self.tls_server_hostname = server_hostname
        return self

    def get_extra_info(self, info):
        return None


class _FakeBackend(AsyncNetworkBackend):
    """Returns a fixed fake stream and records the host it was asked to connect to."""

    def __init__(self, stream: _FakeStream) -> None:
        self.connect_host: str | None = None
        self.connect_port: int | None = None
        self._stream = stream

    async def connect_tcp(
        self, host, port, timeout=None, local_address=None, socket_options=None
    ):
        self.connect_host = host
        self.connect_port = port
        return self._stream


# --------------------------------------------------------------------------------------
# Backend-level: the connect-time gate itself.
# --------------------------------------------------------------------------------------


class TestConnectTimeGate:
    @pytest.mark.asyncio
    async def test_rebind_to_metadata_ip_is_refused_no_socket(self):
        # DNS rebind: the hostname "resolves" (at connect) to the cloud-metadata IP.
        # The socket must NEVER be opened to it.
        spy = _SpyBackend(stream=object())
        backend = SsrfSafeAsyncBackend(inner=spy, resolver=_resolver("169.254.169.254"))
        with pytest.raises(SsrfError):
            await backend.connect_tcp("rebind.evil.example", 443)
        assert spy.connect_calls == []

    @pytest.mark.asyncio
    async def test_rebind_to_rfc1918_ip_is_refused_no_socket(self):
        spy = _SpyBackend(stream=object())
        backend = SsrfSafeAsyncBackend(inner=spy, resolver=_resolver("10.0.0.5"))
        with pytest.raises(SsrfError):
            await backend.connect_tcp("rebind.evil.example", 80)
        assert spy.connect_calls == []

    @pytest.mark.asyncio
    async def test_fail_closed_on_mixed_public_and_private(self):
        # One private address in the answer set blocks the whole connect (rebind defense).
        spy = _SpyBackend(stream=object())
        backend = SsrfSafeAsyncBackend(inner=spy, resolver=_resolver("8.8.8.8", "10.0.0.5"))
        with pytest.raises(SsrfError):
            await backend.connect_tcp("evil.example", 443)
        assert spy.connect_calls == []

    @pytest.mark.asyncio
    async def test_fail_closed_on_empty_resolution(self):
        spy = _SpyBackend(stream=object())
        backend = SsrfSafeAsyncBackend(inner=spy, resolver=_resolver())
        with pytest.raises(SsrfError):
            await backend.connect_tcp("empty.example", 443)
        assert spy.connect_calls == []

    @pytest.mark.asyncio
    async def test_fail_closed_when_resolver_raises(self):
        # A getaddrinfo/DNS error at connect time must fail CLOSED — the error
        # propagates and NO socket is opened to an unvalidated address.
        async def raising_resolver(host):
            raise OSError("getaddrinfo failed")

        spy = _SpyBackend(stream=object())
        backend = SsrfSafeAsyncBackend(inner=spy, resolver=raising_resolver)
        with pytest.raises((OSError, SsrfError)):
            await backend.connect_tcp("dns-error.example", 443)
        assert spy.connect_calls == []

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "literal",
        ["127.0.0.1", "::1", "169.254.169.254", "192.168.1.1", "10.0.0.5", "0.0.0.0"],
    )
    async def test_direct_private_literal_is_refused_no_socket(self, literal):
        # A private/loopback/link-local literal must be blocked at connect too — no
        # resolver involved.
        spy = _SpyBackend(stream=object())
        backend = SsrfSafeAsyncBackend(inner=spy, resolver=_resolver("8.8.8.8"))
        with pytest.raises(SsrfError):
            await backend.connect_tcp(literal, 443)
        assert spy.connect_calls == []

    @pytest.mark.asyncio
    async def test_public_literal_connects_directly(self):
        sentinel = object()
        spy = _SpyBackend(stream=sentinel)
        backend = SsrfSafeAsyncBackend(inner=spy, resolver=_resolver("should-not-be-used"))
        stream = await backend.connect_tcp("93.184.216.34", 443)
        assert stream is sentinel
        assert spy.connect_calls == [("93.184.216.34", 443)]

    @pytest.mark.asyncio
    async def test_public_hostname_connects_to_validated_ip(self):
        # The socket is opened to the VALIDATED IP, not the hostname — so httpx cannot
        # re-resolve to a different (attacker-swapped) address.
        sentinel = object()
        spy = _SpyBackend(stream=sentinel)
        backend = SsrfSafeAsyncBackend(inner=spy, resolver=_resolver("93.184.216.34"))
        stream = await backend.connect_tcp("api.example.com", 443)
        assert stream is sentinel
        assert spy.connect_calls == [("93.184.216.34", 443)]


# --------------------------------------------------------------------------------------
# Transport-level: the guard wired into a real httpx.AsyncClient.
# --------------------------------------------------------------------------------------


class TestSsrfSafeTransport:
    @pytest.mark.asyncio
    async def test_rebind_flip_blocked_at_connect_no_socket(self):
        # Models the TOCTOU flip end-to-end: validation would have seen a public IP, but
        # the connect-time resolver hands back the metadata IP. The request is refused and
        # NO socket is opened to the private IP.
        spy = _SpyBackend(stream=object())
        transport = build_ssrf_safe_async_transport(
            resolver=_resolver("169.254.169.254"), inner_backend=spy
        )
        async with httpx.AsyncClient(transport=transport) as client:
            with pytest.raises(SsrfError):
                await client.get("https://rebind.evil.example/latest/meta-data/")
        assert spy.connect_calls == []

    @pytest.mark.asyncio
    async def test_public_host_connects_to_validated_ip_and_preserves_sni(self):
        response = (
            b"HTTP/1.1 200 OK\r\n"
            b"Content-Length: 2\r\n"
            b"Connection: close\r\n"
            b"\r\n"
            b"ok"
        )
        stream = _FakeStream(response)
        inner = _FakeBackend(stream)
        transport = build_ssrf_safe_async_transport(
            resolver=_resolver("93.184.216.34"), inner_backend=inner
        )
        async with httpx.AsyncClient(transport=transport) as client:
            resp = await client.get("https://example.com/path")

        assert resp.status_code == 200
        assert resp.text == "ok"
        # Pinned: the socket was opened to the validated IP, NOT the hostname.
        assert inner.connect_host == "93.184.216.34"
        assert inner.connect_port == 443
        # SNI preserved: TLS used the ORIGINAL hostname (the cert-verification target),
        # never the raw IP — proof we did not rewrite the URL host.
        assert stream.start_tls_called is True
        assert stream.tls_server_hostname == "example.com"

    def test_transport_ssl_context_still_verifies(self):
        # Do NOT disable TLS verification: the pool's context must still require + check.
        transport = build_ssrf_safe_async_transport(resolver=_resolver("8.8.8.8"))
        ctx = transport._pool._ssl_context
        assert ctx is not None
        assert ctx.verify_mode == ssl.CERT_REQUIRED
        assert ctx.check_hostname is True

    @pytest.mark.asyncio
    async def test_redirects_not_followed_through_ssrf_transport(self):
        response = (
            b"HTTP/1.1 302 Found\r\n"
            b"Location: https://elsewhere.example/\r\n"
            b"Content-Length: 0\r\n"
            b"Connection: close\r\n"
            b"\r\n"
        )
        stream = _FakeStream(response)
        inner = _FakeBackend(stream)
        transport = build_ssrf_safe_async_transport(
            resolver=_resolver("93.184.216.34"), inner_backend=inner
        )
        async with httpx.AsyncClient(transport=transport, follow_redirects=False) as client:
            resp = await client.get("https://example.com/")
        assert resp.status_code == 302
        assert resp.headers["location"] == "https://elsewhere.example/"
        # A single connect — the redirect target was never chased.
        assert inner.connect_host == "93.184.216.34"


# --------------------------------------------------------------------------------------
# Real end-to-end TLS handshake through the guard (hermetic, loopback self-signed).
# Proves TLS/SNI genuinely survive the connect-pin: the handshake completes, the server
# sees the original hostname as SNI, and hostname verification is really enforced.
# Loopback is normally blocked by the guard, so this ONE test injects a permissive
# is_blocked seam to reach the local server — production wiring never passes that seam.
# --------------------------------------------------------------------------------------


def _self_signed(hostname: str) -> tuple[bytes, bytes]:
    import datetime

    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, hostname)])
    now = datetime.datetime.now(datetime.timezone.utc)
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(days=1))
        .not_valid_after(now + datetime.timedelta(days=1))
        .add_extension(x509.SubjectAlternativeName([x509.DNSName(hostname)]), critical=False)
        .sign(key, hashes.SHA256())
    )
    cert_pem = cert.public_bytes(serialization.Encoding.PEM)
    key_pem = key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.TraditionalOpenSSL,
        serialization.NoEncryption(),
    )
    return cert_pem, key_pem


class TestLiveTlsThroughGuard:
    @pytest.mark.asyncio
    async def test_real_tls_handshake_preserves_sni_and_verifies(self, tmp_path):
        import asyncio

        pytest.importorskip("cryptography")
        hostname = "sni-check.test"
        cert_pem, key_pem = _self_signed(hostname)
        cert_file = tmp_path / "cert.pem"
        key_file = tmp_path / "key.pem"
        cert_file.write_bytes(cert_pem)
        key_file.write_bytes(key_pem)

        server_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        server_ctx.load_cert_chain(certfile=str(cert_file), keyfile=str(key_file))
        seen_sni: list[str | None] = []
        server_ctx.sni_callback = lambda sslobj, servername, ctx: seen_sni.append(servername)

        async def handle(reader, writer):
            try:
                await reader.readuntil(b"\r\n\r\n")
            except Exception:
                pass
            writer.write(
                b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello"
            )
            await writer.drain()
            writer.close()

        server = await asyncio.start_server(handle, "127.0.0.1", 0, ssl=server_ctx)
        port = server.sockets[0].getsockname()[1]

        # Client trusts our self-signed cert (verify stays ON — CERT_REQUIRED).
        client_ctx = ssl.create_default_context(cadata=cert_pem.decode())
        transport = build_ssrf_safe_async_transport(
            verify=client_ctx,
            resolver=_resolver("127.0.0.1"),
            is_blocked=lambda ip: False,  # test-only: allow loopback for the local server
        )

        async with server:
            async with httpx.AsyncClient(transport=transport) as client:
                resp = await client.get(f"https://{hostname}:{port}/")
        assert resp.status_code == 200
        assert resp.text == "hello"
        # Server observed the original hostname as SNI — connect-pin preserved it.
        assert seen_sni == [hostname]

    @pytest.mark.asyncio
    async def test_real_tls_hostname_mismatch_is_rejected(self, tmp_path):
        import asyncio

        pytest.importorskip("cryptography")
        cert_pem, key_pem = _self_signed("sni-check.test")
        cert_file = tmp_path / "cert.pem"
        key_file = tmp_path / "key.pem"
        cert_file.write_bytes(cert_pem)
        key_file.write_bytes(key_pem)

        server_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        server_ctx.load_cert_chain(certfile=str(cert_file), keyfile=str(key_file))

        async def handle(reader, writer):
            try:
                await reader.readuntil(b"\r\n\r\n")
            except Exception:
                pass
            writer.close()

        server = await asyncio.start_server(handle, "127.0.0.1", 0, ssl=server_ctx)
        port = server.sockets[0].getsockname()[1]

        client_ctx = ssl.create_default_context(cadata=cert_pem.decode())
        transport = build_ssrf_safe_async_transport(
            verify=client_ctx,
            resolver=_resolver("127.0.0.1"),
            is_blocked=lambda ip: False,
        )

        # Cert is for sni-check.test; we request other-host.test -> hostname mismatch must
        # fail verification (proves check_hostname is really enforced through the guard).
        async with server:
            async with httpx.AsyncClient(transport=transport) as client:
                with pytest.raises(httpx.ConnectError):
                    await client.get(f"https://other-host.test:{port}/")


def test_l402_client_wires_ssrf_transport_and_keeps_no_redirect():
    """Regression guard: the L402 client fetches through the connect-pin backend and
    still does not auto-follow redirects."""
    from lightning_enable_mcp.l402_client import L402Client

    client = L402Client(wallet=AsyncMock())
    assert client._http_client.follow_redirects is False
    backend = client._http_client._transport._pool._network_backend
    assert isinstance(backend, SsrfSafeAsyncBackend)
