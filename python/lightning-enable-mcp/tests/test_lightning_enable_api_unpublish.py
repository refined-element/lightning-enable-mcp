"""
API-client-level tests for unpublish_capability — locks the target endpoint
(the ungated /api/proxy path) and the URL-path encoding that keeps a hostile
proxy id from traversing the path (the security control the tool relies on).
"""

import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.lightning_enable_api import LightningEnableApiClient


def _client_with_response(status_code, body):
    client = LightningEnableApiClient()
    client._api_key = "test-key"  # is_configured → True
    resp = MagicMock(status_code=status_code)
    resp.json = MagicMock(return_value=body)
    client._client = MagicMock()
    client._client.post = AsyncMock(return_value=resp)
    return client


@pytest.mark.asyncio
async def test_unpublish_targets_proxy_endpoint_and_encodes_path():
    client = _client_with_response(
        200, {"proxyId": "p", "retired": True}
    )

    result = await client.unpublish_capability("svc/../danger", None)

    assert result["success"] is True
    url = client._client.post.call_args[0][0]
    # Targets the ungated proxy management endpoint…
    assert "/api/proxy/" in url
    assert url.endswith("/unpublish")
    # …and the slash in the proxy id is percent-encoded (no path traversal).
    assert "%2F" in url
    assert "/svc/../danger/" not in url


@pytest.mark.asyncio
async def test_unpublish_surfaces_backend_error():
    client = _client_with_response(404, {"message": "Proxy not found"})

    result = await client.unpublish_capability("missing", None)

    assert result["success"] is False
    assert "not found" in result["error"]
