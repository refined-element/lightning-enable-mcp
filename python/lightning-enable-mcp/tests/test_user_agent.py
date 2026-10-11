"""
User-Agent carries the package version.

Server-side analytics attribute MCP traffic by User-Agent, so every outbound call sends
``LightningEnable-MCP-Python/<version>`` (Lightning Enable API, paid L402 fetches) or
``LightningEnable-MCP/<version>`` (confirmation webhook, discover_api), never a
hand-maintained literal.
"""

import os
import re
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest

from lightning_enable_mcp import __version__
from lightning_enable_mcp._version import USER_AGENT_MCP, USER_AGENT_PYTHON
from lightning_enable_mcp.budget_service import SpendReservationResult
from lightning_enable_mcp.l402_client import L402Client
from lightning_enable_mcp.lightning_enable_api import LightningEnableApiClient

_SHAPE = re.compile(r"^LightningEnable-MCP(-Python)?/\d+\.\d+\.\d+$")
_SRC = Path(__file__).resolve().parents[1] / "src" / "lightning_enable_mcp"


def test_user_agents_carry_the_package_version():
    assert USER_AGENT_PYTHON == f"LightningEnable-MCP-Python/{__version__}"
    assert USER_AGENT_MCP == f"LightningEnable-MCP/{__version__}"
    assert _SHAPE.match(USER_AGENT_PYTHON), USER_AGENT_PYTHON
    assert _SHAPE.match(USER_AGENT_MCP), USER_AGENT_MCP


def test_no_source_file_hard_codes_a_user_agent_version():
    offenders = [
        str(path.relative_to(_SRC))
        for path in _SRC.rglob("*.py")
        if re.search(r"LightningEnable-MCP(-Python)?/[0-9]", path.read_text(encoding="utf-8"))
    ]
    assert offenders == [], f"use lightning_enable_mcp._version instead: {offenders}"


def test_lightning_enable_api_client_sends_versioned_user_agent():
    with patch.dict(os.environ, {"LIGHTNING_ENABLE_API_URL": "https://api.example.test"}, clear=True):
        with patch("lightning_enable_mcp.lightning_enable_api.get_config_service") as cfg:
            cfg.return_value.configuration = MagicMock(lightning_enable_api_key=None)
            client = LightningEnableApiClient()

    assert client._client.headers["User-Agent"] == USER_AGENT_PYTHON
    assert _SHAPE.match(client._client.headers["User-Agent"])


def _l402_client_over(handler) -> L402Client:
    """A real L402Client (real httpx header merging) whose transport is a MockTransport."""
    budget = MagicMock()
    budget.try_reserve = AsyncMock(
        side_effect=lambda amt: SpendReservationResult.reserved("resv-1", amt)
    )
    wallet = AsyncMock()
    wallet.pay_invoice = AsyncMock(return_value="preimage456")
    with patch(
        "lightning_enable_mcp.l402_client.build_ssrf_safe_async_transport",
        return_value=httpx.MockTransport(handler),
    ):
        return L402Client(wallet=wallet, budget_service=budget, payment_history_service=MagicMock())


def _challenge_then_ok(seen: list[httpx.Request]):
    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        if "Authorization" in request.headers:
            return httpx.Response(200, content=b'{"ok":true}')
        return httpx.Response(
            402,
            headers=[("WWW-Authenticate", 'L402 macaroon="YWJjZGVm", invoice="lnbc100n1pjtest"')],
        )

    return handler


@pytest.mark.asyncio
async def test_l402_paid_fetch_sends_versioned_user_agent_on_challenge_and_paid_retry():
    seen: list[httpx.Request] = []
    client = _l402_client_over(_challenge_then_ok(seen))

    with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
        text, amount, _receipt = await client.fetch("https://api.provider.example/premium", max_sats=1000)
    await client.close()

    assert amount == 10
    assert '"ok":true' in text
    assert len(seen) == 2
    assert "Authorization" not in seen[0].headers, "first request is the unpaid one"
    assert "Authorization" in seen[1].headers, "second request is the paid retry"
    assert [r.headers["User-Agent"] for r in seen] == [USER_AGENT_PYTHON, USER_AGENT_PYTHON]
    assert _SHAPE.match(seen[1].headers["User-Agent"])


@pytest.mark.asyncio
async def test_l402_caller_supplied_user_agent_still_wins():
    seen: list[httpx.Request] = []
    client = _l402_client_over(_challenge_then_ok(seen))

    with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
        await client.fetch(
            "https://api.provider.example/premium",
            headers={"User-Agent": "fixture-agent/9.9"},
            max_sats=1000,
        )
    await client.close()

    assert len(seen) == 2
    assert [r.headers.get_list("User-Agent") for r in seen] == [["fixture-agent/9.9"]] * 2
