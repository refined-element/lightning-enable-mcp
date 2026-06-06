"""
PY-CONFIGURE: the configure_budget tool must let an agent only TIGHTEN (lower)
its spending caps, never RAISE them — otherwise a prompt-injected agent could
loosen its own limits and then drain the wallet.
"""

import json
import pytest

from lightning_enable_mcp.tools.budget import configure_budget
from lightning_enable_mcp.budget import BudgetManager


@pytest.mark.asyncio
async def test_can_lower_limits():
    mgr = BudgetManager(max_per_request=10000, max_per_session=100000)
    result = await configure_budget(per_request=500, per_session=5000, budget_manager=mgr)
    parsed = json.loads(result)
    assert parsed["success"] is True
    assert mgr.max_per_request == 500
    assert mgr.max_per_session == 5000


@pytest.mark.asyncio
async def test_cannot_raise_per_request():
    mgr = BudgetManager(max_per_request=1000, max_per_session=100000)
    # 50000 > current 1000/request; still <= session so the ordering check passes.
    result = await configure_budget(per_request=50000, per_session=100000, budget_manager=mgr)
    parsed = json.loads(result)
    assert parsed["success"] is False
    assert "only LOWER" in parsed["error"]
    assert mgr.max_per_request == 1000  # unchanged


@pytest.mark.asyncio
async def test_cannot_raise_per_session():
    mgr = BudgetManager(max_per_request=1000, max_per_session=10000)
    result = await configure_budget(per_request=1000, per_session=50000, budget_manager=mgr)
    parsed = json.loads(result)
    assert parsed["success"] is False
    assert "only LOWER" in parsed["error"]
    assert mgr.max_per_session == 10000  # unchanged
