"""
FIX A (Python) — access_l402_resource surfaces an unfollowed 3xx as an actionable
redirect_location result (parity with the .NET tool), never following it.
"""

from unittest.mock import AsyncMock, patch

import json
import pytest

from lightning_enable_mcp.l402_client import L402Error, L402RedirectError
from lightning_enable_mcp.tools.access_resource import access_l402_resource


# Patch the SSRF validator to a no-op so these tests are deterministic (no real DNS) —
# the redirect handling under test is independent of the SSRF pre-check.
@pytest.fixture(autouse=True)
def _allow_all_urls():
    with patch(
        "lightning_enable_mcp.tools.access_resource.validate_url_allowed",
        new=AsyncMock(return_value=None),
    ):
        yield


@pytest.mark.asyncio
async def test_access_resource_redirect_returns_actionable_result():
    """When the client raises L402RedirectError, the tool returns success=False with a
    redirect_location the agent can re-call with — not a cryptic HTTP failure."""
    l402_client = AsyncMock()
    l402_client.fetch = AsyncMock(
        side_effect=L402RedirectError("https://www.example.com/new-home", 301)
    )

    result = await access_l402_resource(
        url="https://example.com/old-home",
        l402_client=l402_client,
        budget_service=None,
    )
    parsed = json.loads(result)

    assert parsed["success"] is False
    assert parsed["redirect_location"] == "https://www.example.com/new-home"
    assert "redirected to https://www.example.com/new-home" in parsed["error"]
    # A pre-payment redirect records no spend, so no payment block is surfaced.
    assert "payment" not in parsed


@pytest.mark.asyncio
async def test_access_resource_paid_then_redirect_surfaces_payment_and_location():
    """A redirect on the paid RETRY carries amount_paid — the spend is surfaced so the
    agent is warned money moved, alongside the actionable redirect target."""
    err = L402RedirectError("https://cdn.example.com/asset", 302)
    err.amount_paid = 42
    l402_client = AsyncMock()
    l402_client.fetch = AsyncMock(side_effect=err)

    result = await access_l402_resource(
        url="https://api.example.com/paid",
        l402_client=l402_client,
        budget_service=None,
    )
    parsed = json.loads(result)

    assert parsed["success"] is False
    assert parsed["redirect_location"] == "https://cdn.example.com/asset"
    assert parsed["payment"]["paid"] is True
    assert parsed["payment"]["amountSats"] == 42


@pytest.mark.asyncio
async def test_access_resource_paid_redirect_surfaces_token_and_no_repay_message():
    """FIX 1 parity — the paid credential and an explicit 'already paid, do NOT re-pay'
    message are surfaced so a well-behaved agent retries the redirect target WITH the token
    instead of paying a second time."""
    err = L402RedirectError("https://cdn.example.com/asset", 302)
    err.amount_paid = 42
    err.l402_token = "macaroon:preimage"
    l402_client = AsyncMock()
    l402_client.fetch = AsyncMock(side_effect=err)

    result = await access_l402_resource(
        url="https://api.example.com/paid",
        l402_client=l402_client,
        budget_service=None,
    )
    parsed = json.loads(result)

    assert parsed["alreadyPaid"] is True
    assert parsed["payment"]["l402Token"] == "macaroon:preimage"
    assert "ALREADY PAID" in parsed["message"]
    assert "do NOT pay again" in parsed["message"]
    assert "https://cdn.example.com/asset" in parsed["message"]


@pytest.mark.asyncio
async def test_access_resource_paid_retry_error_surfaces_token_already_paid():
    """FIX a parity — a paid retry that ERRORS (e.g. HTTP 500, not a redirect) is NOT a
    not-paid failure: the invoice was paid + recorded once inside the client, so the tool
    surfaces the token + an ALREADY PAID message (never a bare 'verify the URL' error) so the
    agent reuses the credential instead of paying again."""
    err = L402Error("Request failed after payment: 500 boom")
    err.amount_paid = 42
    err.l402_token = "macaroon:preimage"
    l402_client = AsyncMock()
    l402_client.fetch = AsyncMock(side_effect=err)

    result = await access_l402_resource(
        url="https://api.example.com/paid",
        l402_client=l402_client,
        budget_service=None,
    )
    parsed = json.loads(result)

    assert parsed["success"] is False
    assert "redirect_location" not in parsed  # a 500 is not a redirect
    assert parsed["alreadyPaid"] is True
    assert parsed["payment"]["paid"] is True
    assert parsed["payment"]["amountSats"] == 42
    assert parsed["payment"]["l402Token"] == "macaroon:preimage"
    assert "ALREADY PAID" in parsed["message"]
    assert "do NOT pay again" in parsed["message"]
