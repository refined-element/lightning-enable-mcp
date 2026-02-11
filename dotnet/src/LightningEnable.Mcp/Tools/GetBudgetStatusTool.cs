using System.ComponentModel;
using System.Text.Json;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// MCP tool for viewing current budget status and limits.
/// This is READ-ONLY - budget configuration can only be changed by editing
/// the config file at ~/.lightning-enable/config.json
/// </summary>
[McpServerToolType]
public static class GetBudgetStatusTool
{
    /// <summary>
    /// Gets current budget status and spending limits.
    /// Configuration can only be changed by editing ~/.lightning-enable/config.json
    /// </summary>
    /// <param name="budgetService">Injected budget service.</param>
    /// <param name="priceService">Injected price service.</param>
    /// <param name="configService">Injected config service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current budget status in JSON format.</returns>
    [McpServerTool(Name = "get_budget_status"), Description("View current budget status and spending limits (read-only). Edit ~/.lightning-enable/config.json to change limits.")]
    public static async Task<string> GetBudgetStatus(
        IBudgetService? budgetService = null,
        IPriceService? priceService = null,
        IBudgetConfigurationService? configService = null,
        CancellationToken cancellationToken = default)
    {
        if (budgetService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Budget service not available"
            });
        }

        try
        {
            var config = budgetService.GetUserConfiguration();
            var runtimeConfig = budgetService.GetConfig();

            // Get current BTC price for display
            decimal btcPrice = 100000m; // fallback
            if (priceService != null)
            {
                btcPrice = priceService.GetCachedBtcPrice();
            }

            // Convert session spent to USD
            decimal sessionSpentUsd = 0;
            if (priceService != null && runtimeConfig.SessionSpent > 0)
            {
                sessionSpentUsd = await priceService.SatsToUsdAsync(runtimeConfig.SessionSpent, cancellationToken);
            }

            var remainingUsd = (config.Limits.MaxPerSession ?? 0) - sessionSpentUsd;

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "Budget configuration is READ-ONLY. Edit ~/.lightning-enable/config.json to change limits.",
                configFile = configService?.ConfigFilePath ?? "~/.lightning-enable/config.json",
                currentPrice = new
                {
                    btcUsd = btcPrice,
                    source = "cached"
                },
                tiers = new
                {
                    autoApproveUsd = config.Tiers.AutoApprove,
                    logAndApproveUsd = config.Tiers.LogAndApprove,
                    formConfirmUsd = config.Tiers.FormConfirm,
                    urlConfirmUsd = config.Tiers.UrlConfirm,
                    description = new
                    {
                        autoApprove = $"Payments <= ${config.Tiers.AutoApprove:F2} are auto-approved",
                        logAndApprove = $"Payments ${config.Tiers.AutoApprove:F2} - ${config.Tiers.LogAndApprove:F2} are logged but approved",
                        formConfirm = $"Payments ${config.Tiers.LogAndApprove:F2} - ${config.Tiers.FormConfirm:F2} require your confirmation",
                        urlConfirm = $"Payments ${config.Tiers.FormConfirm:F2} - ${config.Tiers.UrlConfirm:F2} require browser confirmation (AI-proof)"
                    }
                },
                limits = new
                {
                    maxPerPaymentUsd = config.Limits.MaxPerPayment,
                    maxPerSessionUsd = config.Limits.MaxPerSession
                },
                session = new
                {
                    spentSats = runtimeConfig.SessionSpent,
                    spentUsd = Math.Round(sessionSpentUsd, 2),
                    remainingUsd = Math.Round(Math.Max(0, remainingUsd), 2),
                    requestCount = runtimeConfig.RequestCount,
                    started = runtimeConfig.SessionStarted,
                    cooldownSeconds = config.Session.CooldownSeconds,
                    requireFirstPaymentApproval = config.Session.RequireApprovalForFirstPayment
                },
                security = new
                {
                    aiCanModify = false,
                    configLocation = "~/.lightning-enable/config.json",
                    howToChange = "Edit the config.json file directly. AI agents cannot modify budget limits."
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}
