using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

/// <summary>
/// Reads the durable, append-only payment receipt log
/// (<c>~/.lightning-enable/receipts.jsonl</c>) — the human-facing spend +
/// revocation record. Off the agent's hot path: rapid flows log to the file;
/// this tool is how a human (or the agent, on request) reviews what was spent
/// and how to revoke.
/// </summary>
[McpServerToolType]
public static class GetReceiptsTool
{
    [McpServerTool(Name = "get_receipts"), Description(
        "Read the durable, append-only payment receipt log (~/.lightning-enable/receipts.jsonl). "
        + "Unlike get_payment_history (in-memory, this session only), receipts persist across sessions "
        + "and include the spend policy and how to revoke the wallet. Use to review what an agent has "
        + "spent and how to pull the plug.")]
    public static string GetReceipts(
        [Description("Maximum number of recent receipts to return (1-200). Default 20.")] int limit = 20,
        IReceiptService? receiptService = null)
    {
        if (receiptService == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Receipt logging is not available (no wallet/session initialized)."
            });
        }

        limit = Math.Clamp(limit, 1, 200);
        var receipts = receiptService.ReadRecent(limit);

        long totalSats = 0;
        foreach (var r in receipts)
        {
            if (r is JsonObject o
                && o.TryGetPropertyValue("amountSats", out var a) && a != null
                && long.TryParse(a.ToString(), out var v))
            {
                totalSats += v;
            }
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            count = receipts.Count,
            totalSatsInView = totalSats,
            logFile = receiptService.Path,
            receipts,
            note = "Append-only spend log — one payment receipt per line. Each includes how to revoke the wallet."
        });
    }
}
