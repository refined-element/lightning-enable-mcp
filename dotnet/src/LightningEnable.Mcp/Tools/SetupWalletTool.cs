using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Models;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// Wallet onboarding — the tool an agent reaches for BEFORE it can do anything else.
///
/// <para>Called with no arguments it answers "is there a wallet, and where did it come
/// from?" and, when there is not, hands back the shortest path to one. Called with an NWC
/// connection string it validates the string, proves it reaches a live wallet, and only
/// then writes it to <c>~/.lightning-enable/config.json</c>.</para>
///
/// <para>Two rules shape everything here:</para>
/// <list type="number">
/// <item><description><b>The credential never comes back.</b> Not in the report, not in an
/// error, not in the probe result. Only the provider, the source, and what the wallet says
/// about itself (engineering standard #5).</description></item>
/// <item><description><b>Environment wins over the config file.</b> If an env var already
/// selects a wallet, writing the config file would be a silent no-op, so the tool refuses
/// and says which variable to unset rather than leaving the operator with a file that looks
/// configured and a server that ignores it.</description></item>
/// </list>
/// </summary>
[McpServerToolType]
public static class SetupWalletTool
{
    /// <summary>The config shape a human can paste by hand instead of using this tool.</summary>
    internal const string ConfigShape =
        "{ \"wallets\": { \"nwcConnectionString\": \"nostr+walletconnect://<64-hex pubkey>"
        + "?relay=wss://<relay>&secret=<64-hex>\" } }";

    /// <summary>Reports or sets up the wallet this server pays from.</summary>
    [McpServerTool(
        Name = "setup_wallet",
        Title = "Set up wallet",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    // The description is deliberately terse: it is re-sent to the model every session,
    // while the guided setup path costs nothing until the tool is actually called.
    [Description(
        "Report the wallet this server pays from, or connect one. Call with no arguments "
        + "first - it also returns the setup steps when no wallet is configured.")]
    public static async Task<string> SetupWallet(
        [Description("NWC connection string (nostr+walletconnect://...). Omit to just report.")]
        string? nwcConnectionString = null,
        IWalletOnboardingService? onboarding = null,
        CancellationToken cancellationToken = default)
    {
        if (onboarding == null)
        {
            return Json(new
            {
                success = false,
                error = "Wallet onboarding is not available in this server instance.",
            });
        }

        WalletSetupState state;
        try
        {
            state = onboarding.Describe();
        }
        catch (Exception ex)
        {
            return Json(new
            {
                success = false,
                error = $"Could not read the current wallet configuration: {ex.Message}",
            });
        }

        return string.IsNullOrWhiteSpace(nwcConnectionString)
            ? Report(state)
            : await ConnectNwcAsync(nwcConnectionString!, state, onboarding, cancellationToken);
    }

    /// <summary>The no-arguments answer: what is configured, or how to configure something.</summary>
    private static string Report(WalletSetupState state)
    {
        if (state.Configured)
        {
            return Json(new
            {
                success = true,
                configured = true,
                wallet = state.Provider,
                source = state.Source,
                configuredWallets = state.ConfiguredProviders,
                configFile = state.ConfigFilePath,
                note = "The credential itself is never reported by this tool.",
                nextStep = "Run test_l402_payment to confirm the wallet can pay an L402 challenge "
                    + "end to end (~1 sat).",
            });
        }

        return Json(new
        {
            success = true,
            configured = false,
            configFile = state.ConfigFilePath,
            nextStep = "Connect a wallet. The shortest path is NWC: copy a connection string from "
                + "your wallet app and call setup_wallet again with nwcConnectionString set.",
            options = new object[]
            {
                new
                {
                    wallet = "NWC (recommended - Alby Hub, CoinOS, or any NIP-47 wallet)",
                    how = "In the wallet app, create a Nostr Wallet Connect connection and copy the "
                        + "nostr+walletconnect:// string it gives you.",
                    then = "setup_wallet with nwcConnectionString set to that string. This tool "
                        + "validates it, checks it against the live wallet, and saves it.",
                    l402 = "Returns a preimage, so L402 works.",
                },
                new
                {
                    wallet = "LND",
                    how = "Set LND_REST_HOST (host:port) and LND_MACAROON_HEX (admin macaroon as hex).",
                    then = "Restart the server.",
                    l402 = "Always returns a preimage, so L402 works.",
                },
                new
                {
                    wallet = "Strike",
                    how = "Set STRIKE_API_KEY from https://dashboard.strike.me/.",
                    then = "Restart the server.",
                    l402 = "Returns a preimage, so L402 works.",
                },
                new
                {
                    wallet = "OpenNode",
                    how = "Set OPENNODE_API_KEY.",
                    then = "Restart the server.",
                    l402 = "Receiving and invoicing only - OpenNode does not return a preimage, so it "
                        + "CANNOT pay L402 challenges.",
                },
            },
            configFileShape = ConfigShape,
            priority = "When several wallets are configured: LND > NWC > Strike > OpenNode. "
                + "Environment variables win over the config file.",
        });
    }

    /// <summary>Validate, refuse-if-env-wins, probe, save.</summary>
    private static async Task<string> ConnectNwcAsync(
        string nwcConnectionString,
        WalletSetupState state,
        IWalletOnboardingService onboarding,
        CancellationToken cancellationToken)
    {
        // 1. Parse only. A malformed string never reaches a relay, and the failure names the
        //    malformed field without echoing it.
        NwcConfig config;
        try
        {
            config = NwcConfig.Parse(nwcConnectionString.Trim());
        }
        catch (ArgumentException ex)
        {
            return Json(new
            {
                success = false,
                error = $"That is not a valid NWC connection string: {ex.Message}",
                expectedShape = "nostr+walletconnect://<64-hex wallet pubkey>?relay=wss://<relay>"
                    + "&secret=<64-hex client secret>",
                hint = "Copy the string again from your wallet app - it must be complete, including "
                    + "the relay and secret parameters.",
            });
        }

        // 2. Environment wins over the config file, so writing the file here would leave the
        //    operator with a config that looks right and a server that ignores it.
        if (state.FromEnvironment)
        {
            return Json(new
            {
                success = false,
                saved = false,
                error = $"A wallet is already selected by the environment ({state.Source}), and "
                    + "environment variables take precedence over the config file. Nothing was "
                    + "written.",
                wallet = state.Provider,
                source = state.Source,
                howToProceed = "Unset that environment variable and call setup_wallet again, or "
                    + "keep using the wallet the environment selects.",
            });
        }

        // 3. Prove the string reaches a live wallet BEFORE persisting it — a saved-but-dead
        //    credential fails later, at a payment, where it is far more expensive to diagnose.
        NwcProbeResult probe;
        try
        {
            probe = await onboarding.ProbeNwcAsync(nwcConnectionString.Trim(), cancellationToken);
        }
        catch (Exception ex)
        {
            probe = NwcProbeResult.Failed(ex.Message);
        }

        if (!probe.Success)
        {
            return Json(new
            {
                success = false,
                saved = false,
                error = $"The connection string is well-formed but the wallet could not be reached: "
                    + $"{probe.Error}",
                relay = config.RelayUrl,
                hint = "Check that the relay is reachable and that this connection is still active "
                    + "in your wallet app. Nothing was saved.",
            });
        }

        // 4. Persist.
        try
        {
            onboarding.SaveNwcConnectionString(nwcConnectionString.Trim());
        }
        catch (Exception ex)
        {
            return Json(new
            {
                success = false,
                saved = false,
                error = $"The wallet answered, but the connection string could not be saved: {ex.Message}",
                configFile = state.ConfigFilePath,
                configFileShape = ConfigShape,
                hint = "Add the connection string to the config file by hand using the shape above.",
            });
        }

        var canPay = probe.Methods.Contains("pay_invoice", StringComparer.OrdinalIgnoreCase);

        return Json(new
        {
            success = true,
            saved = true,
            wallet = "NWC",
            walletAlias = probe.Alias,
            relay = config.RelayUrl,
            configFile = state.ConfigFilePath,
            methods = probe.Methods,
            balanceSats = probe.BalanceSats,
            balanceNote = probe.Error,
            canPayL402 = canPay,
            note = canPay
                ? "The wallet declares pay_invoice, so it can pay L402 challenges."
                : "The wallet did not declare pay_invoice in get_info. If payments fail, check what "
                  + "this connection is permitted to do in your wallet app.",
            security = "The connection string was written to the config file and the file's "
                + "permissions were restricted to your user. It is never returned by this tool.",
            nextStep = "Restart the MCP server so it picks up the new wallet, then run "
                + "test_l402_payment to confirm it can pay an L402 challenge end to end (~1 sat).",
        });
    }

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
}
