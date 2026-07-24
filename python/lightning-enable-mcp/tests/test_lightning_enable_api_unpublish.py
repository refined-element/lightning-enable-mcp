"""
API-client-level tests for unpublish_capability — locks the URL-path encoding
that keeps a hostile service_id from traversing the path (the security control
the tool relies on).
"""

import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.lightning_enable_api import LightningEnableApiClient

PUBKEY = "a" * 64


def _client_with_response(status_code, body):
    client = LightningEnableApiClient()
    client._api_key = "test-key"  # is_configured → True
    resp = MagicMock(status_code=status_code)
    resp.json = MagicMock(return_value=body)
    client._client = MagicMock()
    client._client.post = AsyncMock(return_value=resp)
    return client


@pytest.mark.asyncio
async def test_unpublish_percent_encodes_service_id_path():
    client = _client_with_response(
        200, {"serviceId": "x", "proxyId": "p", "mode": "remove", "retired": True}
    )

    result = await client.unpublish_capability(PUBKEY, "svc/../danger", "remove", None)

    assert result["success"] is True
    url = client._client.post.call_args[0][0]
    # The slash in service_id is percent-encoded, so it stays one path segment.
    assert "%2F" in url
    assert "/svc/../danger/" not in url


@pytest.mark.asyncio
async def test_unpublish_surfaces_backend_error():
    client = _client_with_response(404, {"message": "Capability not found for this agent"})

    result = await client.unpublish_capability(PUBKEY, "missing", "remove", None)

    assert result["success"] is False
    assert "not found" in result["error"]
