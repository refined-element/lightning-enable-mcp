"""
SSRF guard tests (F-10e).

The classifier and URL validator are driven with IP literals and an INJECTED
resolver, so every case is offline-deterministic and never touches real DNS.
Two integration tests confirm the tools refuse a direct private/metadata target
with a generic error and never reach the network client.
"""

import ipaddress
import json
import socket

import pytest
from unittest.mock import AsyncMock

from lightning_enable_mcp.tools._ssrf_guard import (
    SsrfError,
    is_blocked_ip,
    validate_url_allowed,
)


def _fake_resolver(*addresses: str):
    """A resolver seam that returns the given IP strings (no network)."""

    async def _resolve(host: str) -> list[str]:
        return list(addresses)

    return _resolve


def _raising_resolver(exc: Exception):
    async def _resolve(host: str) -> list[str]:
        raise exc

    return _resolve


class TestIsBlockedIp:
    @pytest.mark.parametrize(
        "ip",
        [
            "127.0.0.1",
            "10.0.0.1",
            "172.16.0.1",
            "172.31.255.255",
            "192.168.1.1",
            "169.254.0.1",
            "169.254.169.254",  # cloud metadata
            "0.0.0.0",
            "224.0.0.1",  # multicast
            "240.0.0.1",  # reserved
            "255.255.255.255",  # broadcast
            "::1",
            "::",
            "fe80::1",
            "fc00::1",
            "fd00::1",
            "fec0::1",  # IPv6 site-local (fec0::/10) — is_private misses it (FIX 3)
            "feff:ffff::1",  # top of the fec0::/10 site-local block
            "ff02::1",
            "::ffff:127.0.0.1",  # IPv4-mapped loopback
            "::ffff:169.254.169.254",  # IPv4-mapped metadata
            # RFC 6598 shared / CGNAT (100.64.0.0/10) — is_private misses it (FIX 3)
            "100.64.0.1",
            "100.64.0.0",
            "100.100.100.100",
            "100.127.255.255",
        ],
    )
    def test_blocked(self, ip):
        assert is_blocked_ip(ipaddress.ip_address(ip)) is True

    @pytest.mark.parametrize(
        "ip",
        [
            "8.8.8.8",
            "1.1.1.1",
            "93.184.216.34",
            "172.15.255.255",  # just below 172.16/12
            "172.32.0.1",  # just above 172.16/12
            "100.63.255.255",  # just below the 100.64/10 CGNAT block
            "100.128.0.1",  # just above the 100.64/10 CGNAT block
            "2606:4700:4700::1111",
            "2001:4860:4860::8888",
        ],
    )
    def test_allowed(self, ip):
        assert is_blocked_ip(ipaddress.ip_address(ip)) is False


class TestValidateUrlAllowed:
    @pytest.mark.asyncio
    @pytest.mark.parametrize("url", ["ftp://example.com/x", "file:///etc/passwd", "gopher://h/"])
    async def test_rejects_non_http_schemes(self, url):
        with pytest.raises(SsrfError):
            await validate_url_allowed(url, resolver=_fake_resolver("8.8.8.8"))

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "url",
        [
            "http://localhost/x",
            "http://svc.internal/x",
            "http://foo.localhost/x",
            "http://metadata/x",  # bare metadata (FIX 7 union set)
            "http://metadata.google.internal/x",
            "http://metadata.goog/x",
            "http://metadata.azure.com/x",  # was missing on the Python side (FIX 7)
        ],
    )
    async def test_rejects_internal_hostnames(self, url):
        # These must be blocked WITHOUT resolution — resolver returns a public IP,
        # yet the hostname rule fires first.
        with pytest.raises(SsrfError):
            await validate_url_allowed(url, resolver=_fake_resolver("8.8.8.8"))

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "url",
        [
            "http://169.254.169.254/latest/meta-data/",
            "http://127.0.0.1/admin",
            "http://[::1]/admin",
            "http://10.0.0.5/internal",
            "http://192.168.1.1/",
        ],
    )
    async def test_rejects_private_ip_literals(self, url):
        # No resolver needed — literals are validated directly.
        with pytest.raises(SsrfError):
            await validate_url_allowed(url)

    @pytest.mark.asyncio
    @pytest.mark.parametrize("url", ["http://8.8.8.8/", "https://93.184.216.34/x"])
    async def test_allows_public_ip_literals(self, url):
        await validate_url_allowed(url)  # no raise

    @pytest.mark.asyncio
    async def test_rejects_hostname_resolving_to_private(self):
        with pytest.raises(SsrfError):
            await validate_url_allowed(
                "https://rebind.evil.example/", resolver=_fake_resolver("10.0.0.5")
            )

    @pytest.mark.asyncio
    async def test_rejects_hostname_resolving_to_metadata_ip(self):
        with pytest.raises(SsrfError):
            await validate_url_allowed(
                "https://evil.example/", resolver=_fake_resolver("169.254.169.254")
            )

    @pytest.mark.asyncio
    async def test_fail_closed_on_mixed_public_and_private(self):
        # One private address in the set blocks the whole request.
        with pytest.raises(SsrfError):
            await validate_url_allowed(
                "https://evil.example/", resolver=_fake_resolver("8.8.8.8", "10.0.0.5")
            )

    @pytest.mark.asyncio
    async def test_allows_hostname_resolving_to_public(self):
        await validate_url_allowed(
            "https://api.example.com/", resolver=_fake_resolver("93.184.216.34")
        )  # no raise

    @pytest.mark.asyncio
    async def test_allows_when_host_does_not_resolve(self):
        # Unresolvable → allow (matches the .NET/API reference). The connection
        # would fail naturally; the name may be configured later.
        await validate_url_allowed(
            "https://not-registered.example/",
            resolver=_raising_resolver(socket.gaierror("Name or service not known")),
        )  # no raise

    @pytest.mark.asyncio
    async def test_fail_closed_on_empty_resolution(self):
        # FIX 6: a resolver returning an EMPTY list (not raising) previously skipped the
        # validation loop and fell through to "allow". It must fail closed instead —
        # parity with the .NET SsrfConnectValidator which throws on an empty address set.
        # This is distinct from the unresolvable (raising) case above, which stays allowed.
        with pytest.raises(SsrfError):
            await validate_url_allowed(
                "https://empty-answer.example/", resolver=_fake_resolver()
            )

    @pytest.mark.asyncio
    async def test_generic_message_does_not_echo_host_or_ip(self):
        with pytest.raises(SsrfError) as exc:
            await validate_url_allowed(
                "https://internal.evil.example/", resolver=_fake_resolver("192.168.13.37")
            )
        assert "192.168.13.37" not in str(exc.value)
        assert "internal.evil.example" not in str(exc.value)


class TestAccessResourceSsrfIntegration:
    """access_l402_resource must refuse a private/metadata target and never fetch."""

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "url",
        ["http://169.254.169.254/latest/meta-data/", "http://127.0.0.1/admin", "http://10.0.0.1/"],
    )
    async def test_private_target_is_refused_before_fetch(self, url):
        from lightning_enable_mcp.tools.access_resource import access_l402_resource

        l402_client = AsyncMock()
        l402_client.fetch = AsyncMock()

        result = await access_l402_resource(url=url, l402_client=l402_client)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "not allowed" in parsed["error"].lower()
        # The generic message must not leak the resolved internal target.
        assert "169.254" not in parsed["error"]
        # Crucially, the network client was never invoked.
        l402_client.fetch.assert_not_called()


class TestDiscoverApiSsrfIntegration:
    """discover_api(url=...) must refuse a private/metadata target."""

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "url", ["http://127.0.0.1/l402.json", "http://169.254.169.254/", "http://192.168.0.1/"]
    )
    async def test_private_manifest_url_is_refused(self, url):
        from lightning_enable_mcp.tools.discover_api import discover_api

        result = await discover_api(url=url)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "not allowed" in parsed["error"].lower()
