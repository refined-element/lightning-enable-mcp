"""
A1 — settle_agent_service SSRF hardening.

settle_agent_service used to carve out plain ``http://`` to localhost "for dev" and
handed the URL straight to ``L402Client.fetch`` with no preflight. These tests pin the
closed behaviour:

* HTTPS is required — there is no localhost / plain-HTTP exception any more.
* ``validate_url_allowed`` runs as a preflight so a blocked target is refused with a
  generic, non-disclosing error BEFORE any request (zero transport calls, zero wallet
  calls). The connect-time pin in ``ssrf_transport`` remains the authoritative gate.
* A DNS-rebind (public at validation, private at connect) never opens a socket to the
  private address.
* An approved public HTTPS target still reaches the transport.

Everything is offline-deterministic: DNS goes through the injectable resolver seams and
the wire goes through ``httpx.MockTransport`` / a spy ``httpcore`` backend.
"""

from __future__ import annotations

import json
import importlib
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest
from httpcore._backends.base import AsyncNetworkBackend

from lightning_enable_mcp.l402_client import L402Client
from lightning_enable_mcp.ssrf_transport import build_ssrf_safe_async_transport
from lightning_enable_mcp.tools._ssrf_guard import validate_url_allowed
from lightning_enable_mcp.tools.settle_agent_service import settle_agent_service

# ``tools/__init__`` re-exports the FUNCTION under the submodule's name, so grab the
# module object explicitly for patching.
settle_module = importlib.import_module("lightning_enable_mcp.tools.settle_agent_service")


# --------------------------------------------------------------------------------------
# Doubles
# --------------------------------------------------------------------------------------


class _CountingTransport(httpx.MockTransport):
    """A MockTransport that counts every request it is handed."""

    def __init__(self) -> None:
        self.requests: list[httpx.Request] = []

        def _handler(request: httpx.Request) -> httpx.Response:
            self.requests.append(request)
            return httpx.Response(200, text="ok")

        super().__init__(_handler)


class _SpyBackend(AsyncNetworkBackend):
    """Records ``connect_tcp`` calls; the rebind test asserts none reach a private IP."""

    def __init__(self) -> None:
        self.connect_calls: list[tuple[str, int]] = []

    async def connect_tcp(
        self, host, port, timeout=None, local_address=None, socket_options=None
    ):
        self.connect_calls.append((host, port))
        raise AssertionError("connect_tcp must not be reached in this test")


def _resolver(*addresses: str):
    async def _resolve(host: str) -> list[str]:
        return list(addresses)

    return _resolve


def _fake_wallet() -> MagicMock:
    wallet = MagicMock()
    wallet.pay_invoice = AsyncMock(return_value="00" * 32)
    return wallet


def _client_with_transport(transport: httpx.AsyncBaseTransport) -> tuple[L402Client, MagicMock]:
    """A real L402Client (fake wallet, no budget) whose wire is the given transport."""
    wallet = _fake_wallet()
    client = L402Client(wallet=wallet, budget_service=None)
    client._http_client = httpx.AsyncClient(transport=transport, follow_redirects=False)
    return client, wallet


def _patched_validator(*addresses: str):
    """Run the REAL validate_url_allowed but with a fixed resolver (no DNS)."""

    async def _validate(url: str) -> None:
        await validate_url_allowed(url, resolver=_resolver(*addresses))

    return patch.object(settle_module, "validate_url_allowed", new=_validate)


def _assert_blocked_non_disclosing(parsed: dict, *forbidden: str) -> None:
    assert parsed["success"] is False
    error = parsed["error"]
    # Generic wording only — no resolved/internal address is echoed.
    for token in forbidden:
        assert token not in error, f"error leaks {token!r}: {error}"


# --------------------------------------------------------------------------------------
# HTTPS is required — the localhost carve-out is gone.
# --------------------------------------------------------------------------------------


class TestHttpsRequired:
    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "url",
        [
            "http://localhost:5096/l402",
            "http://127.0.0.1:5096/l402",
            "http://[::1]:5096/l402",
            "http://LOCALHOST/l402",
        ],
    )
    async def test_plain_http_to_localhost_is_rejected(self, url):
        transport = _CountingTransport()
        client, wallet = _client_with_transport(transport)

        result = await settle_agent_service(l402_endpoint=url, l402_client=client)
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert "requires HTTPS" in parsed["error"]
        assert "localhost" not in parsed["error"].lower()
        assert transport.requests == []
        wallet.pay_invoice.assert_not_called()

    def test_no_localhost_allowlist_remains(self):
        assert not hasattr(settle_module, "_LOCALHOST_HOSTS")


# --------------------------------------------------------------------------------------
# Preflight: blocked targets never reach the transport or the wallet.
# --------------------------------------------------------------------------------------


_BLOCKED_LITERALS = [
    "https://localhost/l402",
    "https://127.0.0.1/l402",
    "https://[::1]/l402",
    "https://10.0.0.5/l402",
    "https://172.16.0.9/l402",
    "https://192.168.1.10/l402",
    "https://169.254.169.254/latest/meta-data/",
    "https://100.64.0.1/l402",  # RFC 6598 CGNAT
    "https://[::ffff:10.0.0.1]/l402",  # IPv4-mapped IPv6
    # Case / userinfo / trailing dot / alternate localhost forms
    "https://LOCALHOST/l402",
    "https://user@127.0.0.1/l402",
    "https://user:pw@localhost/l402",
    "https://localhost./l402",
    "https://foo.localhost/l402",
    "https://127.1/l402",
    "https://0.0.0.0/l402",
    "https://[0:0:0:0:0:0:0:1]/l402",
    # Non-canonical numeric IPv4 forms (decimal / hex / octal) — all 127.0.0.1
    "https://2130706433/l402",
    "https://0x7f000001/l402",
    "https://0177.0.0.1/l402",
    "https://0x7f.0.0.1/l402",
]


class TestPreflightBlocksBeforeAnyRequest:
    @pytest.mark.asyncio
    @pytest.mark.parametrize("url", _BLOCKED_LITERALS)
    async def test_blocked_literal_never_reaches_transport_or_wallet(self, url):
        transport = _CountingTransport()
        client, wallet = _client_with_transport(transport)

        # Even if DNS were consulted, answer with a PUBLIC address — the literal forms
        # must be refused on their own, not because of what a resolver says.
        with _patched_validator("93.184.216.34"):
            result = await settle_agent_service(l402_endpoint=url, l402_client=client)
        parsed = json.loads(result)

        _assert_blocked_non_disclosing(parsed, "127.0.0.1", "10.0.0", "169.254", "::1")
        assert transport.requests == []
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "url",
        [
            "https://db.internal/l402",
            "https://metadata.google.internal/computeMetadata/v1/",
            "https://metadata/l402",
        ],
    )
    async def test_internal_hostname_never_reaches_transport(self, url):
        transport = _CountingTransport()
        client, wallet = _client_with_transport(transport)

        with _patched_validator("93.184.216.34"):
            result = await settle_agent_service(l402_endpoint=url, l402_client=client)
        parsed = json.loads(result)

        _assert_blocked_non_disclosing(parsed, "10.0.0", "169.254")
        assert transport.requests == []
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_hostname_resolving_to_private_is_rejected(self):
        transport = _CountingTransport()
        client, wallet = _client_with_transport(transport)

        with _patched_validator("10.0.0.5"):
            result = await settle_agent_service(
                l402_endpoint="https://svc.example.com/l402", l402_client=client
            )
        parsed = json.loads(result)

        _assert_blocked_non_disclosing(parsed, "10.0.0.5")
        assert transport.requests == []
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_hostname_resolving_to_public_and_private_is_rejected(self):
        """Fail closed on a mixed answer set — the public address does not salvage it."""
        transport = _CountingTransport()
        client, wallet = _client_with_transport(transport)

        with _patched_validator("93.184.216.34", "10.0.0.5"):
            result = await settle_agent_service(
                l402_endpoint="https://svc.example.com/l402", l402_client=client
            )
        parsed = json.loads(result)

        _assert_blocked_non_disclosing(parsed, "10.0.0.5", "93.184.216.34")
        assert transport.requests == []
        wallet.pay_invoice.assert_not_called()

    @pytest.mark.asyncio
    async def test_preflight_runs_before_budget_check(self):
        """A blocked target is refused before the budget/confirmation machinery runs."""
        transport = _CountingTransport()
        client, _ = _client_with_transport(transport)
        budget = MagicMock()
        budget.check_approval_level = AsyncMock()

        with _patched_validator("93.184.216.34"):
            result = await settle_agent_service(
                l402_endpoint="https://169.254.169.254/l402",
                l402_client=client,
                budget_service=budget,
            )
        parsed = json.loads(result)

        assert parsed["success"] is False
        budget.check_approval_level.assert_not_awaited()
        assert transport.requests == []


# --------------------------------------------------------------------------------------
# DNS rebind: the connect-time pin is the authoritative gate.
# --------------------------------------------------------------------------------------


class TestDnsRebind:
    @pytest.mark.asyncio
    async def test_rebind_public_at_validation_private_at_connect_opens_no_socket(self):
        spy = _SpyBackend()
        transport = build_ssrf_safe_async_transport(
            resolver=_resolver("10.0.0.5"), inner_backend=spy
        )
        client, wallet = _client_with_transport(transport)

        # Preflight sees a public answer; the connect-time resolver flips to private.
        with _patched_validator("93.184.216.34"):
            result = await settle_agent_service(
                l402_endpoint="https://rebind.evil.example/l402", l402_client=client
            )
        parsed = json.loads(result)

        assert parsed["success"] is False
        assert spy.connect_calls == []
        assert "10.0.0.5" not in parsed["error"]
        wallet.pay_invoice.assert_not_called()


# --------------------------------------------------------------------------------------
# Approved public HTTPS target still works.
# --------------------------------------------------------------------------------------


class TestApprovedTargetStillReaches:
    @pytest.mark.asyncio
    async def test_https_public_target_reaches_transport(self):
        transport = _CountingTransport()
        client, wallet = _client_with_transport(transport)

        with _patched_validator("93.184.216.34"):
            result = await settle_agent_service(
                l402_endpoint="https://svc.example.com/l402", l402_client=client
            )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["settlement"]["paid"] is False
        assert parsed["response"]["content"] == "ok"
        assert len(transport.requests) == 1
        assert str(transport.requests[0].url) == "https://svc.example.com/l402"
        # No payment was needed on a 200, so the wallet is untouched.
        wallet.pay_invoice.assert_not_called()
