"""
Tests for publish_agent_attestation tool
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock

from lightning_enable_mcp.tools.publish_agent_attestation import publish_agent_attestation


class TestPublishAgentAttestation:
    """Tests for publish_agent_attestation tool."""

    @pytest.mark.asyncio
    async def test_missing_subject_pubkey_returns_error(self):
        result = await publish_agent_attestation(
            subject_pubkey="", agreement_id="agr", rating=5, content="great",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Subject pubkey is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_missing_agreement_id_returns_error(self):
        result = await publish_agent_attestation(
            subject_pubkey="pk", agreement_id="", rating=5, content="great",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Agreement ID is required" in parsed["error"]

    @pytest.mark.asyncio
    @pytest.mark.parametrize("rating", [0, 6, -1, 10])
    async def test_rating_out_of_range_returns_error(self, rating):
        result = await publish_agent_attestation(
            subject_pubkey="pk", agreement_id="agr", rating=rating, content="x",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "between 1 and 5" in parsed["error"]

    @pytest.mark.asyncio
    async def test_missing_content_returns_error(self):
        result = await publish_agent_attestation(
            subject_pubkey="pk", agreement_id="agr", rating=4, content="",
            api_client=MagicMock(is_configured=True),
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "Review content is required" in parsed["error"]

    @pytest.mark.asyncio
    async def test_no_api_client_returns_error(self):
        result = await publish_agent_attestation(
            subject_pubkey="pk", agreement_id="agr", rating=4, content="x",
            api_client=None,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "not available" in parsed["error"]

    @pytest.mark.asyncio
    async def test_unconfigured_api_client_returns_error(self):
        client = MagicMock()
        client.is_configured = False
        result = await publish_agent_attestation(
            subject_pubkey="pk", agreement_id="agr", rating=4, content="x",
            api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert "LIGHTNING_ENABLE_API_KEY" in parsed["error"]

    @pytest.mark.asyncio
    async def test_successful_attestation_with_proof(self):
        client = AsyncMock()
        client.is_configured = True
        client.publish_attestation.return_value = {
            "success": True,
            "eventId": "evt-x",
            "attestationId": "att-agr-123",
        }

        result = await publish_agent_attestation(
            subject_pubkey="pk", agreement_id="agr", rating=5,
            content="Excellent service", proof="deadbeef",
            api_client=client,
        )
        parsed = json.loads(result)

        assert parsed["success"] is True
        assert parsed["eventId"] == "evt-x"
        assert parsed["attestationId"] == "att-agr-123"
        assert parsed["rating"] == 5
        assert parsed["proof"] == "included"
        client.publish_attestation.assert_called_once_with(
            "pk", "agr", 5, "Excellent service", "deadbeef"
        )

    @pytest.mark.asyncio
    async def test_successful_attestation_without_proof(self):
        client = AsyncMock()
        client.is_configured = True
        client.publish_attestation.return_value = {
            "success": True,
            "eventId": "evt-y",
            "attestationId": "att-agr-456",
        }

        result = await publish_agent_attestation(
            subject_pubkey="pk", agreement_id="agr", rating=3, content="ok",
            api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is True
        assert parsed["proof"] == "none"

    @pytest.mark.asyncio
    async def test_api_error_response(self):
        client = AsyncMock()
        client.is_configured = True
        client.publish_attestation.return_value = {
            "success": False,
            "error": "rate limited",
        }

        result = await publish_agent_attestation(
            subject_pubkey="pk", agreement_id="agr", rating=4, content="x",
            api_client=client,
        )
        parsed = json.loads(result)
        assert parsed["success"] is False
        assert parsed["error"] == "rate limited"
