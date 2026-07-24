using System.Reflection;
using FluentAssertions;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests;

/// <summary>
/// Guard test — the SINGLE SOURCE OF TRUTH for the MCP server's tool inventory.
///
/// The advertised tool counts (package READMEs, docs, marketing) drifted repeatedly
/// because they were hand-copied into ~20 places. This pins the inventory to the CODE:
/// it reflects over every <c>[McpServerTool]</c> the server auto-registers
/// (Program.cs uses <c>WithToolsFromAssembly()</c>) and asserts the exact set plus the
/// free / API-key split. Add or remove a tool and this test fails until you update the
/// ONE list below — which is what every human-facing count is expected to derive from.
///
/// Canonical: 26 total = 18 out-of-the-box (free, just a wallet) + 8 that require
/// <c>LIGHTNING_ENABLE_API_KEY</c> (2 producer + 6 ASA). Keep in lockstep with the Python
/// guard (python/lightning-enable-mcp/tests/test_server.py) and the docs' MCP Complete
/// Guide — the one place that itemizes the tools for humans.
/// </summary>
public class ToolInventoryTests
{
    // 18 tools that work with just a wallet — no LIGHTNING_ENABLE_API_KEY.
    private static readonly IReadOnlySet<string> FreeTools = new HashSet<string>
    {
        "pay_invoice", "check_wallet_balance", "get_payment_history", "get_receipts",
        "get_budget_status", "configure_budget", "create_invoice", "check_invoice_status",
        "access_l402_resource", "pay_l402_challenge", "test_l402_payment", "discover_api",
        "get_btc_price", "get_all_balances", "exchange_currency", "send_onchain",
        "verify_confirmation_code", "create_lightning_enable_account",
    };

    // 8 tools that require LIGHTNING_ENABLE_API_KEY: 2 producer + 6 ASA.
    private static readonly IReadOnlySet<string> ApiKeyTools = new HashSet<string>
    {
        "create_l402_challenge", "verify_l402_payment",
        "discover_agent_services", "request_agent_service", "settle_agent_service",
        "publish_agent_capability", "publish_agent_attestation", "get_agent_reputation",
    };

    /// <summary>
    /// Reflects over the main assembly for every method carrying an
    /// <c>[McpServerTool]</c> attribute (matched by simple type name so the test does not
    /// hard-bind to the SDK attribute type), returning each tool's registered name.
    /// This mirrors what <c>WithToolsFromAssembly()</c> discovers at startup.
    /// </summary>
    private static HashSet<string> RegisteredToolNames()
    {
        var assembly = typeof(PayInvoiceTool).Assembly;
        var names = new HashSet<string>();

        foreach (var type in assembly.GetTypes())
        {
            // WithToolsFromAssembly() only discovers tools on [McpServerToolType] classes,
            // so scope the guard the same way — otherwise a stray [McpServerTool] on a type
            // that forgot [McpServerToolType] would be counted here yet never actually served.
            var isToolType = type.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name == "McpServerToolTypeAttribute");
            if (!isToolType) continue;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (var method in type.GetMethods(flags))
            {
                var attr = method.GetCustomAttributes(inherit: false)
                    .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                if (attr is null) continue;

                var nameValue = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                names.Add(string.IsNullOrEmpty(nameValue) ? method.Name : nameValue);
            }
        }

        return names;
    }

    [Fact]
    public void RegisteredTools_MatchDeclaredInventory_NoDrift()
    {
        var registered = RegisteredToolNames();

        var expected = new HashSet<string>(FreeTools);
        expected.UnionWith(ApiKeyTools);

        registered.Should().BeEquivalentTo(expected,
            "the code's registered [McpServerTool] set must equal the declared inventory — "
            + "if you added or removed a tool, update FreeTools/ApiKeyTools here (the source "
            + "of truth every advertised count derives from) and the Python guard to match");
    }

    [Fact]
    public void ToolCounts_AreCanonical_26_18_8()
    {
        FreeTools.Count.Should().Be(18, "18 out-of-the-box tools");
        ApiKeyTools.Count.Should().Be(8, "8 tools require LIGHTNING_ENABLE_API_KEY (2 producer + 6 ASA)");
        (FreeTools.Count + ApiKeyTools.Count).Should().Be(26, "26 tools total");
        FreeTools.Overlaps(ApiKeyTools).Should().BeFalse("a tool is either free or API-key-gated, never both");
    }
}
