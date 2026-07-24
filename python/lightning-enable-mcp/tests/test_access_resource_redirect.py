"""
FIX A (Python) — access_l402_resource surfaces an unfollowed 3xx as an actionable
redirect_location result (parity with the .NET tool), never following it.
"""

from unittest.mock import AsyncMock, patch

import json
import pytest

from lightning_enable_mcp.l402_client import L402RedirectError
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
