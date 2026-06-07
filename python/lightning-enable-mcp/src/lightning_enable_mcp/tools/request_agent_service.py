"""
Request Agent Service Tool

Sends a service request referencing a provider's capability (kind 38401 event),
starting the negotiation process. Requires LIGHTNING_ENABLE_API_KEY.

NOTE: Budget is NOT deducted at request time by design. The budget is only
deducted at settlement time (settle_agent_service) when the L402 payment is
actually made. The budget check here only validates the budget is sufficient.
"""

import json
import logging
from typing import TYPE_CHECKING

from ..config import ApprovalLevel
from . import sanitize_error

if TYPE_CHECKING:
    from ..budget_service import BudgetService
    from ..lightning_enable_api import LightningEnableApiClient

logger = logging.getLogger("lightning-enable-mcp.tools.request_agent_service")


async def request_agent_service(
    capability_event_id: str,
    budget_sats: int,
    parameters: str | None = None,
    api_client: "LightningEnableApiClient | None" = None,
    budget_service: "BudgetService | None" = None,
) -> str:
    """
    Request a service from an agent. Sends a kind 38401 event referencing the
    provider's capability and starts the negotiation process. If the provider
    has an L402 endpoint, you can skip this step and use settle_agent_service.

    Args:
        capability_event_id: Event ID of the capability to request
        budget_sats: Maximum budget in satoshis
        parameters: Additional parameters as a JSON string
        api_client: Lightning Enable API client instance
        budget_service: Optional BudgetService for budget validation

    Returns:
        JSON with request result or an error message.
    """
    try:
        # Input validation
        if not capability_event_id or not capability_event_id.strip():
            return json.dumps({
                "success": False,
                "error": "Capability event ID is required. Use discover_agent_services to find available capabilities.",
            })

        if budget_sats <= 0:
            return json.dumps({
                "success": False,
                "error": "Budget must be greater than 0 sats.",
            })

        # Validate parameters JSON if provided
        if parameters and parameters.strip():
            try:
                json.loads(parameters)
            except (json.JSONDecodeError, ValueError):
                return json.dumps({
                    "success": False,
                    "error": 'Parameters must be valid JSON (e.g., \'{"text": "Hello", "targetLang": "es"}\').',
                })

        if api_client is None:
            return json.dumps({
                "success": False,
                "error": "Agent service not available. The MCP server may not be configured correctly.",
            })

        # Budget check before sending request (validation only — no spend recorded)
        if budget_service is not None:
            check = await budget_service.check_approval_level(budget_sats)
            if check.level == ApprovalLevel.DENY:
                return json.dumps({
                    "success": False,
                    "error": "Budget limit exceeded",
                    "details": {
                        "requestedSats": budget_sats,
                        "remainingUsd": float(check.remaining_session_budget_usd),
                        "reason": check.denial_reason,
                    },
                    "hint": "Reduce the budget amount or check get_budget_status for current limits.",
                })

        result = await api_client.request_service(
            capability_event_id, budget_sats, parameters,
        )

        if not result.get("success"):
            return json.dumps({
                "success": False,
                "error": result.get("error", "Unknown error requesting service"),
            })

        # NOTE: Budget is not deducted at request time by design. Spend is only
        # recorded at settlement time when the L402 payment actually succeeds.
        response: dict = {
            "success": True,
            "requestEventId": result.get("requestEventId"),
            "capabilityEventId": capability_event_id,
            "budgetSats": budget_sats,
            "message": "Service request sent successfully.",
        }

        endpoint = result.get("l402Endpoint")
        if endpoint:
            response["l402Endpoint"] = endpoint
            response["nextStep"] = (
                f'The provider has an L402 endpoint. Use settle_agent_service(l402_endpoint="{endpoint}") '
                "to pay and access the service."
            )
        else:
            response["nextStep"] = (
                "Waiting for provider response. The provider will send a service agreement "
                "or direct response. Monitor for kind 38402 events referencing your request."
            )

        return json.dumps(response, indent=2)

    except Exception as e:
        logger.exception("Error requesting service")
        return json.dumps({
            "success": False,
            "error": f"Error requesting service: {sanitize_error(str(e))}",
        })
