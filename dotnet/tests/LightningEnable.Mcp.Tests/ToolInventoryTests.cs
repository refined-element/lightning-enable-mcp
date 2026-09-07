using FluentAssertions;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests;

/// <summary>
/// Guard test — the SINGLE SOURCE OF TRUTH for the MCP server's advertised tool inventory.
///
/// The advertised tool counts (package READMEs, docs, marketing) drifted repeatedly because
/// they were hand-copied into ~20 places. This pins the inventory to the CODE: it starts a
/// real server on the default profile, reads <c>tools/list</c>, and asserts the exact set
/// plus the free / API-key split. Add or remove a tool and this fails until you update the
/// ONE list below — which is what every human-facing count is expected to derive from.
///
/// Canonical (the <c>standard</c> profile, which is the default): 16 total = 14
/// out-of-the-box (free, just a wallet) + 2 that need <c>LIGHTNING_ENABLE_API_KEY</c>.
/// The 2026-09 tool-surface consolidation folded 16 single-purpose tools into five
/// action-style verbs (budget, receipts, wallet_ops, l402_producer, agent_services), taking
/// the advertised surface from 26 tools to 15; <c>setup_wallet</c> then added the wallet
/// onboarding step every other tool depends on — see <see cref="ToolProfiles"/>.
///
/// Every pre-consolidation name still dispatches as an accepted-but-unadvertised forwarding
/// alias (see <see cref="DeprecatedAliasDispatchTests"/>), and the <c>full</c> profile
/// re-advertises them. Neither belongs in this inventory: this is the DEFAULT advertised
/// surface. Keep in lockstep with the Python guard
/// (<c>python/lightning-enable-mcp/tests/test_server.py</c>) and the docs' MCP Complete Guide.
/// </summary>
public class ToolInventoryTests
{
    // 14 tools that work with just a wallet — no LIGHTNING_ENABLE_API_KEY.
    private static readonly IReadOnlySet<string> FreeTools = new HashSet<string>
    {
        "setup_wallet",
        "access_l402_resource", "pay_invoice", "pay_l402_challenge", "test_l402_payment",
        "get_balance", "budget", "receipts", "create_invoice", "check_invoice_status",
        "verify_confirmation_code", "discover_api", "create_lightning_enable_account",
        "wallet_ops",
    };

    // 2 verbs that need LIGHTNING_ENABLE_API_KEY: the L402 producer flow and the
    // agent-marketplace (ASA) flow.
    private static readonly IReadOnlySet<string> ApiKeyTools = new HashSet<string>
    {
        "l402_producer", "agent_services",
    };

    [Fact]
    public async Task AdvertisedTools_MatchDeclaredInventory_NoDrift()
    {
        await using var host = await McpToolHost.StartAsync(ToolProfile.Standard);
        var advertised = await host.AdvertisedNamesAsync();

        var expected = new HashSet<string>(FreeTools);
        expected.UnionWith(ApiKeyTools);

        advertised.Should().BeEquivalentTo(expected,
            "the tools the server advertises must equal the declared inventory — if you "
            + "added or removed a tool, update FreeTools/ApiKeyTools here (the source of "
            + "truth every advertised count derives from) and the Python guard to match");
    }

    [Fact]
    public void ToolCounts_AreCanonical_16_14_2()
    {
        FreeTools.Count.Should().Be(14, "14 out-of-the-box tools");
        ApiKeyTools.Count.Should().Be(2, "2 API-key-gated verbs (l402_producer, agent_services)");
        (FreeTools.Count + ApiKeyTools.Count).Should().Be(16, "16 tools total");
        FreeTools.Overlaps(ApiKeyTools).Should().BeFalse("a tool is either free or API-key-gated, never both");
    }

    [Fact]
    public void DeclaredInventory_MatchesTheStandardProfile()
    {
        // ToolProfiles.StandardToolNames drives what the server registers; this inventory
        // is what the docs quote. They must not drift apart.
        var expected = new HashSet<string>(FreeTools);
        expected.UnionWith(ApiKeyTools);
        ToolProfiles.StandardToolNames.Should().BeEquivalentTo(expected);
    }
}
