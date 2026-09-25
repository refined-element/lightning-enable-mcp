"""
A3 — generic paid HTTP is restricted to GET and HEAD.

A caller-controlled POST/PUT/PATCH/DELETE to a caller-chosen URL must be refused at the
shared L402Client boundary BEFORE the initial request and BEFORE any payment (zero
transport calls, zero wallet calls), and again at the access_l402_resource tool layer with
a descriptive error. The first-party create_account bootstrap keeps its POST through an
explicit, internal-only allowance that the model cannot reach.
"""

import json
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest

from lightning_enable_mcp.l402_client import (
    L402Client,
    L402Error,
    L402MethodNotAllowedError,
)
from lightning_enable_mcp.tools.access_resource import (
    ACCESS_L402_RESOURCE_TOOL,
    access_l402_resource,
)

URL = "https://api.example.com/paid"
CHALLENGE = 'L402 macaroon="bWFjYXJvb24=", invoice="lnbc10n1fake"'


def _client() -> L402Client:
    wallet = MagicMock()
    wallet.pay_invoice = AsyncMock(return_value="ab" * 32)
    client = L402Client(wallet=wallet)
    client._http_client.request = AsyncMock(
        return_value=httpx.Response(200, text="ok", request=httpx.Request("GET", URL))
    )
    return client


def _paying_client() -> L402Client:
    """402 -> pay -> 200 sequence (invoice amount is patched to 10 sats in the tests)."""
    client = _client()
    req = httpx.Request("GET", URL)
    challenge = httpx.Response(402, headers={"WWW-Authenticate": CHALLENGE}, request=req)
    ok = httpx.Response(200, text="paid content", request=req)
    client._http_client.request = AsyncMock(side_effect=[challenge, ok])
    return client


class TestClientBoundary:
    @pytest.mark.asyncio
    @pytest.mark.parametrize("method", ["POST", "PUT", "PATCH", "DELETE"])
    async def test_write_methods_rejected_before_any_request_or_payment(self, method):
        client = _client()
        with pytest.raises(L402MethodNotAllowedError) as exc_info:
            await client.fetch(url=URL, method=method, body="{}")
        assert client._http_client.request.await_count == 0
        client.wallet.pay_invoice.assert_not_awaited()
        assert isinstance(exc_info.value, L402Error)
        msg = str(exc_info.value)
        assert method in msg
        assert "GET" in msg and "HEAD" in msg

    @pytest.mark.asyncio
    @pytest.mark.parametrize("method", ["post", " Post ", "\tDELETE\n", "pUt", "patch "])
    async def test_casing_and_whitespace_cannot_bypass(self, method):
        client = _client()
        with pytest.raises(L402MethodNotAllowedError):
            await client.fetch(url=URL, method=method)
        assert client._http_client.request.await_count == 0
        client.wallet.pay_invoice.assert_not_awaited()

    @pytest.mark.asyncio
    @pytest.mark.parametrize("method", ["OPTIONS", "TRACE", "CONNECT", "", "G ET"])
    async def test_other_methods_rejected_too(self, method):
        client = _client()
        with pytest.raises(L402MethodNotAllowedError):
            await client.fetch(url=URL, method=method)
        assert client._http_client.request.await_count == 0

    @pytest.mark.asyncio
    async def test_write_method_rejected_even_on_402_path_no_payment(self):
        """A paying endpoint never gets the chance to charge for a rejected method."""
        client = _paying_client()
        with pytest.raises(L402MethodNotAllowedError):
            await client.fetch(url=URL, method="POST", body="{}")
        assert client._http_client.request.await_count == 0
        client.wallet.pay_invoice.assert_not_awaited()

    @pytest.mark.asyncio
    @pytest.mark.parametrize("method", ["GET", "HEAD", "get", " head "])
    async def test_get_and_head_keep_l402_behaviour(self, method):
        client = _paying_client()
        with patch.object(client, "_get_invoice_amount_msat", return_value=10_000):
            text, paid, _receipt = await client.fetch(url=URL, method=method, max_sats=100)
        assert text == "paid content"
        assert paid == 10
        assert client._http_client.request.await_count == 2
        client.wallet.pay_invoice.assert_awaited_once()
        # The normalised method is what goes on the wire — on both legs.
        for call in client._http_client.request.await_args_list:
            assert call.kwargs["method"] == method.strip().upper()

    @pytest.mark.asyncio
    async def test_get_without_challenge_is_plain_fetch(self):
        client = _client()
        text, paid, _ = await client.fetch(url=URL, method="GET")
        assert text == "ok"
        assert paid is None
        assert client._http_client.request.await_count == 1

    @pytest.mark.asyncio
    async def test_first_party_allowance_permits_post_only(self):
        """The internal allowance used by the create_account bootstrap lets POST through
        (it is NOT reachable from any tool argument) and still refuses other writes."""
        client = _client()
        text, _paid, _ = await client.fetch(
            url=URL, method="POST", body="{}", first_party_write=True
        )
        assert text == "ok"
        assert client._http_client.request.await_count == 1
        assert client._http_client.request.await_args.kwargs["method"] == "POST"

        for method in ("PUT", "PATCH", "DELETE"):
            client = _client()
            with pytest.raises(L402MethodNotAllowedError):
                await client.fetch(url=URL, method=method, first_party_write=True)
            assert client._http_client.request.await_count == 0


@pytest.fixture
def _allow_all_urls():
    with patch(
        "lightning_enable_mcp.tools.access_resource.validate_url_allowed",
        new=AsyncMock(return_value=None),
    ):
        yield


class TestToolLayer:
    @pytest.mark.asyncio
    @pytest.mark.parametrize("method", ["POST", "PUT", "PATCH", "DELETE", "post", " delete "])
    async def test_tool_rejects_write_methods_before_ssrf_budget_or_fetch(self, method):
        l402_client = AsyncMock()
        l402_client.fetch = AsyncMock()
        budget = MagicMock()
        budget.check_approval_level = AsyncMock()
        with patch(
            "lightning_enable_mcp.tools.access_resource.validate_url_allowed",
            new=AsyncMock(return_value=None),
        ) as ssrf:
            result = await access_l402_resource(
                url=URL, method=method, body="{}",
                l402_client=l402_client, budget_service=budget,
            )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "GET" in parsed["error"] and "HEAD" in parsed["error"]
        assert method.strip().upper() in parsed["error"]
        l402_client.fetch.assert_not_awaited()
        budget.check_approval_level.assert_not_awaited()
        ssrf.assert_not_awaited()

    @pytest.mark.asyncio
    @pytest.mark.parametrize("method", ["GET", "HEAD", "head"])
    async def test_tool_forwards_get_and_head(self, method, _allow_all_urls):
        l402_client = AsyncMock()
        l402_client.fetch = AsyncMock(return_value=("body", None, None))
        result = await access_l402_resource(
            url=URL, method=method, l402_client=l402_client, budget_service=None,
        )
        parsed = json.loads(result)
        assert parsed["success"] is True
        assert parsed["method"] == method.strip().upper()
        assert l402_client.fetch.await_args.kwargs["method"] == method.strip().upper()

    def test_tool_schema_only_advertises_get_and_head(self):
        props = ACCESS_L402_RESOURCE_TOOL.inputSchema["properties"]
        assert props["method"]["enum"] == ["GET", "HEAD"]
        for word in ("POST", "PUT", "DELETE"):
            assert word not in json.dumps(ACCESS_L402_RESOURCE_TOOL.inputSchema)
            assert word not in ACCESS_L402_RESOURCE_TOOL.description
