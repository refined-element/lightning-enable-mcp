"""Guards for the consolidated tool surface: profiles, aliases, annotations, size.

The 2026-09 consolidation folded 16 single-purpose tools into five action-style
verbs and added ``LIGHTNING_ENABLE_TOOL_PROFILE``. Three things must stay true, and
none of them is visible from a single unit test of any one tool:

1. **No caller is stranded.** Every pre-consolidation tool name still dispatches to
   the same implementation, and says so with a ``deprecated`` marker.
2. **The profile picks the advertised surface** and nothing else — a narrower
   profile hides schemas, it never removes capability.
3. **The surface actually got smaller**, which was the point: every advertised
   schema is re-sent into the agent's context each session.
"""

import json
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from mcp.types import CallToolRequest, CallToolRequestParams, ListToolsRequest

from lightning_enable_mcp.server import DEPRECATED_ALIASES, LightningEnableServer
from lightning_enable_mcp.tools import profiles
from lightning_enable_mcp.tools.registry import (
    LEGACY_TOOLS,
    LITE_TOOLS,
    STANDARD_TOOLS,
    tools_for_profile,
)

# ── Schema-size baseline ──────────────────────────────────────────────────────
# Bytes of the pre-consolidation 26-tool list, measured as the compact JSON of
# ``[{name, description, inputSchema}, ...]`` at commit 10d6c2f (the last commit
# before this change). That is the schema payload a client receives on
# ``tools/list`` — the thing that lands in the model's context every session.
#
# ``annotations`` are excluded on BOTH sides of the comparison: the baseline
# predates them (no tool carried any), so including them here would compare a
# schema against a schema-plus-metadata and understate the reduction. The
# annotations' own cost is asserted separately, below.
PRE_CONSOLIDATION_SCHEMA_BYTES = 17_926

#: The consolidated surface must be well under the old one, not marginally under.
#:
#: Raised 0.60 → 0.75 when ``l402_producer`` grew the six seller-setup actions
#: (``configure_receive``, ``status``, ``create_proxy``, ``add_endpoint``, ``publish``,
#: ``list_challenges``). That is ~1.9KB of new arguments, and it is the shape the budget
#: is meant to encourage: six capabilities folded into an EXISTING verb rather than six
#: new tools, and the whole seller side stops needing raw REST. The surface still lands
#: near 70% of the pre-consolidation payload, and the guard keeps roughly 950 bytes of
#: headroom, so the next verbose addition fails again.
#:
#: ``ToolSchemaTests.cs`` holds the same line at a different number — the .NET SDK emits a
#: richer per-parameter schema, so that port's payload is larger. The two fractions are
#: tuned independently rather than copied.
MAX_STANDARD_FRACTION = 0.75


def schema_bytes(tools) -> int:
    """Compact JSON size of a tool list's name/description/inputSchema payload."""
    payload = [
        {
            "name": t.name,
            "description": t.description or "",
            "inputSchema": t.inputSchema,
        }
        for t in tools
    ]
    return len(json.dumps(payload, separators=(",", ":"), sort_keys=True).encode())


def wire_bytes(tools) -> int:
    """Compact JSON size of everything ``tools/list`` sends, annotations included."""
    payload = [t.model_dump(exclude_none=True, by_alias=True) for t in tools]
    return len(json.dumps(payload, separators=(",", ":"), sort_keys=True).encode())


async def _advertised(server: LightningEnableServer) -> list[str]:
    handler = server.server.request_handlers[ListToolsRequest]
    result = await handler(ListToolsRequest(method="tools/list"))
    return [tool.name for tool in result.root.tools]


async def _call_tool(server: LightningEnableServer, name: str, arguments: dict) -> str:
    handler = server.server.request_handlers[CallToolRequest]
    req = CallToolRequest(
        method="tools/call",
        params=CallToolRequestParams(name=name, arguments=arguments),
    )
    result = await handler(req)
    return result.root.content[0].text


# ══ Profiles ══════════════════════════════════════════════════════════════════


class TestProfileResolution:
    @pytest.mark.parametrize(
        "raw,expected",
        [
            (None, "standard"),
            ("", "standard"),
            ("   ", "standard"),
            ("lite", "lite"),
            ("LITE", "lite"),
            ("  Standard  ", "standard"),
            ("full", "full"),
        ],
    )
    def test_recognized_values(self, raw, expected):
        assert profiles.resolve_profile(raw) == expected

    def test_unknown_value_falls_back_to_standard(self):
        """A typo must not silently produce an empty or surprise tool surface."""
        assert profiles.resolve_profile("minimal") == "standard"
        assert profiles.resolve_profile("standrd") == "standard"

    def test_unknown_value_warns(self, caplog):
        with caplog.at_level("WARNING"):
            profiles.resolve_profile("minimal")
        assert profiles.PROFILE_ENV_VAR in caplog.text


class TestProfileContents:
    def test_standard_is_the_consolidated_set(self):
        assert [t.name for t in STANDARD_TOOLS] == list(profiles.STANDARD_TOOL_NAMES)
        assert len(STANDARD_TOOLS) == 16

    def test_lite_is_only_wallet_setup_spend_and_budget(self):
        assert {t.name for t in LITE_TOOLS} == {
            "setup_wallet",
            "pay_invoice",
            "access_l402_resource",
            "get_balance",
            "budget",
            "receipts",
        }
        assert len(LITE_TOOLS) == 6

    def test_lite_is_a_subset_of_standard(self):
        assert {t.name for t in LITE_TOOLS} <= {t.name for t in STANDARD_TOOLS}

    def test_full_is_standard_plus_every_legacy_name(self):
        names = [t.name for t in tools_for_profile("full")]
        assert names[: len(STANDARD_TOOLS)] == [t.name for t in STANDARD_TOOLS]
        assert set(names) - {t.name for t in STANDARD_TOOLS} == set(
            profiles.LEGACY_TOOL_NAMES
        )
        assert len(names) == 32

    def test_legacy_names_are_all_dispatchable_aliases(self):
        """`full` may only re-advertise names the dispatcher actually accepts."""
        for name in profiles.LEGACY_TOOL_NAMES:
            assert name in DEPRECATED_ALIASES

    def test_v1_aliases_stay_hidden_in_every_profile(self):
        """The pre-consolidation renames were already unadvertised; keep them so."""
        v1_aliases = {"confirm_payment", "check_wallet_balance", "get_all_balances"}
        for profile in ("lite", "standard", "full"):
            assert not v1_aliases & {t.name for t in tools_for_profile(profile)}

    def test_no_duplicate_names_in_any_profile(self):
        for profile in ("lite", "standard", "full"):
            names = [t.name for t in tools_for_profile(profile)]
            assert len(names) == len(set(names)), profile


class TestProfileSelection:
    """The env var selects what ``list_tools`` advertises."""

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "value,expected_count",
        [("lite", 6), ("standard", 16), ("full", 32)],
    )
    async def test_env_var_selects_profile(self, monkeypatch, value, expected_count):
        monkeypatch.setenv(profiles.PROFILE_ENV_VAR, value)
        server = LightningEnableServer()
        assert len(await _advertised(server)) == expected_count

    @pytest.mark.asyncio
    async def test_default_is_standard(self, monkeypatch):
        monkeypatch.delenv(profiles.PROFILE_ENV_VAR, raising=False)
        server = LightningEnableServer()
        assert await _advertised(server) == [t.name for t in STANDARD_TOOLS]

    @pytest.mark.asyncio
    async def test_unknown_profile_advertises_standard(self, monkeypatch):
        monkeypatch.setenv(profiles.PROFILE_ENV_VAR, "gigantic")
        server = LightningEnableServer()
        assert await _advertised(server) == [t.name for t in STANDARD_TOOLS]

    @pytest.mark.asyncio
    async def test_lite_hides_but_does_not_disable(self, monkeypatch):
        """A tool missing from `lite` is still callable — profiles are listing-only."""
        monkeypatch.setenv(profiles.PROFILE_ENV_VAR, "lite")
        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        assert "check_invoice_status" not in await _advertised(server)

        with patch(
            "lightning_enable_mcp.server.check_invoice_status",
            new=AsyncMock(return_value='{"success": true}'),
        ) as impl:
            text = await _call_tool(server, "check_invoice_status", {"invoice_id": "i1"})
        impl.assert_awaited_once()
        assert json.loads(text)["success"] is True


# ══ Deprecated aliases ════════════════════════════════════════════════════════

# Every deprecated name → the ``server`` module attribute its call must reach.
# Parametrized over the WHOLE alias map, so adding an alias without wiring its
# dispatch (or vice versa) fails here rather than in production.
ALIAS_IMPLEMENTATIONS = {
    "confirm_payment": "verify_confirmation_code",
    "check_wallet_balance": "get_balance",
    "get_all_balances": "get_balance",
    "get_budget_status": "get_budget_status",
    "configure_budget": "configure_budget",
    "get_receipts": "get_receipts",
    "get_payment_history": "get_payment_history",
    "get_btc_price": "get_btc_price",
    "exchange_currency": "exchange_currency",
    "send_onchain": "send_onchain",
    "create_l402_challenge": "create_l402_challenge",
    "verify_l402_payment": "verify_l402_payment",
    "discover_agent_services": "discover_agent_services",
    "publish_agent_capability": "publish_agent_capability",
    "unpublish_agent_capability": "unpublish_agent_capability",
    "request_agent_service": "request_agent_service",
    "publish_agent_attestation": "publish_agent_attestation",
    "get_agent_reputation": "get_agent_reputation",
    "settle_agent_service": "settle_agent_service",
}


class TestDeprecatedAliasDispatch:
    def test_alias_map_and_test_table_agree(self):
        assert set(DEPRECATED_ALIASES) == set(ALIAS_IMPLEMENTATIONS)
        assert len(DEPRECATED_ALIASES) == 19

    @pytest.mark.parametrize("alias", sorted(ALIAS_IMPLEMENTATIONS))
    def test_alias_targets_a_real_tool(self, alias):
        target = DEPRECATED_ALIASES[alias]
        advertised = {t.name for t in STANDARD_TOOLS}
        assert target.tool in advertised

    @pytest.mark.asyncio
    @pytest.mark.parametrize("alias", sorted(ALIAS_IMPLEMENTATIONS))
    async def test_every_legacy_name_dispatches_with_a_deprecation_note(self, alias):
        server = LightningEnableServer()
        # Pre-set the services so call_tool skips _initialize_services and the
        # no-wallet guard; this test is about routing, not wallet policy.
        server.wallet = MagicMock()
        server.l402_client = MagicMock()

        impl_name = ALIAS_IMPLEMENTATIONS[alias]
        with patch(
            f"lightning_enable_mcp.server.{impl_name}",
            new=AsyncMock(return_value='{"success": true}'),
        ) as impl:
            text = await _call_tool(server, alias, {})

        impl.assert_awaited_once(), f"{alias} did not reach {impl_name}"
        data = json.loads(text)
        assert data["success"] is True
        target = DEPRECATED_ALIASES[alias]
        assert data["deprecated"]["replaced_by"] == target.tool
        assert data["deprecated"]["use"] == target.use
        assert data["deprecated"]["removal"] == "v2.0.0"

    @pytest.mark.asyncio
    @pytest.mark.parametrize("alias", sorted(ALIAS_IMPLEMENTATIONS))
    async def test_no_legacy_name_is_advertised_by_default(self, alias):
        server = LightningEnableServer()
        assert alias not in await _advertised(server)

    @pytest.mark.asyncio
    async def test_alias_injects_the_action_of_the_tool_it_replaced(self):
        """`get_btc_price` must reach wallet_ops's `price` branch, not another one."""
        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        with (
            patch(
                "lightning_enable_mcp.server.get_btc_price",
                new=AsyncMock(return_value='{"success": true}'),
            ) as price,
            patch(
                "lightning_enable_mcp.server.exchange_currency",
                new=AsyncMock(return_value='{"success": true}'),
            ) as exchange,
        ):
            await _call_tool(server, "get_btc_price", {})
        price.assert_awaited_once()
        exchange.assert_not_awaited()

    @pytest.mark.asyncio
    async def test_caller_supplied_action_cannot_override_the_alias(self):
        """`send_onchain` stays an on-chain send even if the caller passes action."""
        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        with (
            patch(
                "lightning_enable_mcp.server.send_onchain",
                new=AsyncMock(return_value='{"success": true}'),
            ) as onchain,
            patch(
                "lightning_enable_mcp.server.get_btc_price",
                new=AsyncMock(return_value='{"success": true}'),
            ) as price,
        ):
            await _call_tool(
                server, "send_onchain", {"address": "bc1qexample", "action": "price"}
            )
        onchain.assert_awaited_once()
        price.assert_not_awaited()

    @pytest.mark.asyncio
    async def test_new_names_carry_no_deprecation_marker(self):
        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        with patch(
            "lightning_enable_mcp.server.get_btc_price",
            new=AsyncMock(return_value='{"success": true}'),
        ):
            text = await _call_tool(server, "wallet_ops", {"action": "price"})
        assert "deprecated" not in json.loads(text)


# ══ Action dispatch ═══════════════════════════════════════════════════════════


class TestActionDispatch:
    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "tool,key",
        [
            ("budget", "action"),
            ("receipts", "source"),
            ("wallet_ops", "action"),
            ("l402_producer", "action"),
            ("agent_services", "action"),
        ],
    )
    async def test_missing_action_returns_a_descriptive_error(self, tool, key):
        """Never a blank response.

        The discriminator is ``required`` with an ``enum``, so the MCP SDK rejects
        the call against the schema before the handler runs. Assert on the wire
        behaviour a client actually sees, and that the message names the choices.
        """
        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        text = await _call_tool(server, tool, {})
        assert key in text
        assert "error" in text.lower()

    @pytest.mark.asyncio
    async def test_unknown_action_is_named_in_the_error(self):
        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        text = await _call_tool(server, "budget", {"action": "loosen"})
        assert "loosen" in text
        assert "status" in text and "tighten" in text

    @pytest.mark.parametrize(
        "tool,key",
        [
            ("budget", "action"),
            ("receipts", "source"),
            ("wallet_ops", "action"),
            ("l402_producer", "action"),
            ("agent_services", "action"),
        ],
    )
    def test_handler_level_unknown_action_error_is_descriptive(self, tool, key):
        """Defence in depth behind the SDK's schema validation.

        A client that skips schema validation (or a future transport that does not
        run it) must still get a named tool, the offending key and the valid values
        — never a silent no-op.
        """
        from lightning_enable_mcp.server import _unknown_action

        data = json.loads(_unknown_action(tool, key, "nonsense"))
        assert data["success"] is False
        assert data["tool"] == tool
        assert key in data["error"]
        assert "nonsense" in data["error"]
        assert data["valid_actions"]
        for action in data["valid_actions"]:
            assert action in data["error"]

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "source,impl", [("durable", "get_receipts"), ("session", "get_payment_history")]
    )
    async def test_receipts_routes_by_source(self, source, impl):
        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        with patch(
            f"lightning_enable_mcp.server.{impl}",
            new=AsyncMock(return_value='{"success": true}'),
        ) as mock:
            await _call_tool(server, "receipts", {"source": source})
        mock.assert_awaited_once()

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "action,impl",
        [
            ("discover", "discover_agent_services"),
            ("request", "request_agent_service"),
            ("settle", "settle_agent_service"),
            ("publish", "publish_agent_capability"),
            ("unpublish", "unpublish_agent_capability"),
            ("attest", "publish_agent_attestation"),
            ("reputation", "get_agent_reputation"),
        ],
    )
    async def test_agent_services_routes_every_action(self, action, impl):
        server = LightningEnableServer()
        server.wallet = MagicMock()
        server.l402_client = MagicMock()
        with patch(
            f"lightning_enable_mcp.server.{impl}",
            new=AsyncMock(return_value='{"success": true}'),
        ) as mock:
            await _call_tool(server, "agent_services", {"action": action})
        mock.assert_awaited_once()


class TestWalletGuardParity:
    """The no-wallet policy is unchanged — just expressed per action now."""

    @pytest.mark.asyncio
    async def test_durable_receipts_read_needs_no_wallet(self):
        server = LightningEnableServer()
        server.l402_client = MagicMock()
        with patch.object(server, "_initialize_services", new=AsyncMock()):
            text = await _call_tool(server, "receipts", {"source": "durable"})
        assert "No wallet configured" not in text

    @pytest.mark.asyncio
    async def test_session_receipts_read_needs_a_wallet(self):
        server = LightningEnableServer()
        server.l402_client = MagicMock()
        with patch.object(server, "_initialize_services", new=AsyncMock()):
            text = await _call_tool(server, "receipts", {"source": "session"})
        assert "No wallet configured" in text

    @pytest.mark.asyncio
    @pytest.mark.parametrize(
        "action", ["discover", "request", "publish", "unpublish", "attest", "reputation"]
    )
    async def test_non_settle_agent_actions_need_no_wallet(self, action):
        server = LightningEnableServer()
        server.l402_client = MagicMock()
        with patch.object(server, "_initialize_services", new=AsyncMock()):
            text = await _call_tool(server, "agent_services", {"action": action})
        assert "No wallet configured" not in text

    @pytest.mark.asyncio
    async def test_settle_needs_a_wallet(self):
        server = LightningEnableServer()
        server.l402_client = MagicMock()
        with patch.object(server, "_initialize_services", new=AsyncMock()):
            text = await _call_tool(server, "agent_services", {"action": "settle"})
        assert "No wallet configured" in text

    @pytest.mark.asyncio
    async def test_l402_producer_needs_no_wallet(self):
        server = LightningEnableServer()
        server.l402_client = MagicMock()
        with patch.object(server, "_initialize_services", new=AsyncMock()):
            text = await _call_tool(server, "l402_producer", {"action": "create"})
        assert "No wallet configured" not in text


# ══ Annotations ═══════════════════════════════════════════════════════════════

MONEY_MOVING_TOOLS = {
    "pay_invoice",
    "access_l402_resource",
    "pay_l402_challenge",
    "test_l402_payment",
    "create_lightning_enable_account",
    "wallet_ops",
    "agent_services",
}

PURE_READ_TOOLS = {
    "get_balance",
    "receipts",
    "discover_api",
    "check_invoice_status",
    "verify_confirmation_code",
}


class TestToolAnnotations:
    """MCP directory review requires a title and an explicit readOnlyHint."""

    @pytest.mark.parametrize("profile", ["lite", "standard", "full"])
    def test_every_listed_tool_has_a_title_and_explicit_read_only_hint(self, profile):
        for tool in tools_for_profile(profile):
            assert tool.annotations is not None, tool.name
            assert tool.annotations.title, tool.name
            assert tool.annotations.readOnlyHint is not None, tool.name
            assert isinstance(tool.annotations.readOnlyHint, bool), tool.name

    def test_money_moving_tools_are_destructive_and_not_read_only(self):
        by_name = {t.name: t for t in STANDARD_TOOLS}
        for name in MONEY_MOVING_TOOLS:
            annotations = by_name[name].annotations
            assert annotations.readOnlyHint is False, name
            assert annotations.destructiveHint is True, name

    def test_pure_reads_are_read_only(self):
        by_name = {t.name: t for t in STANDARD_TOOLS}
        for name in PURE_READ_TOOLS:
            assert by_name[name].annotations.readOnlyHint is True, name

    def test_budget_is_idempotent_and_not_read_only(self):
        """`budget` mixes a read (status) with a write (tighten): classify widest."""
        annotations = {t.name: t.annotations for t in STANDARD_TOOLS}["budget"]
        assert annotations.readOnlyHint is False
        assert annotations.destructiveHint is False
        assert annotations.idempotentHint is True

    def test_deprecated_names_are_titled_as_deprecated(self):
        for tool in LEGACY_TOOLS:
            assert "deprecated" in tool.annotations.title.lower(), tool.name


# ══ Schema size ═══════════════════════════════════════════════════════════════


class TestSchemaSize:
    def test_standard_profile_is_well_under_the_old_26_tool_surface(self):
        actual = schema_bytes(STANDARD_TOOLS)
        budget = int(PRE_CONSOLIDATION_SCHEMA_BYTES * MAX_STANDARD_FRACTION)
        assert actual < budget, (
            f"standard-profile schema is {actual} bytes "
            f"({100 * actual / PRE_CONSOLIDATION_SCHEMA_BYTES:.1f}% of the "
            f"pre-consolidation {PRE_CONSOLIDATION_SCHEMA_BYTES}); the budget is "
            f"{budget} ({MAX_STANDARD_FRACTION:.0%}). Adding a tool or a verbose "
            "description costs every session's context - trim, or consolidate."
        )

    def test_lite_profile_is_far_smaller_still(self):
        assert schema_bytes(LITE_TOOLS) < schema_bytes(STANDARD_TOOLS) // 3

    def test_annotations_stay_a_small_fraction_of_the_payload(self):
        """Annotations are worth their bytes, but must not undo the consolidation."""
        overhead = wire_bytes(STANDARD_TOOLS) - schema_bytes(STANDARD_TOOLS)
        assert 0 < overhead < schema_bytes(STANDARD_TOOLS) * 0.20

    def test_full_profile_is_roughly_the_old_surface(self):
        """`full` is an escape hatch, not a recommendation: it is not smaller."""
        assert schema_bytes(tools_for_profile("full")) > schema_bytes(STANDARD_TOOLS)
