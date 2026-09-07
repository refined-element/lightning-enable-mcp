using System.ComponentModel;
using System.Text.Json.Serialization;
using LightningEnable.Mcp.Services;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Tools;

// Enum members are the literal wire values (see BudgetTool for why).
#pragma warning disable CA1707, IDE1006

/// <summary>Which payment log <c>receipts</c> should read.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReceiptsSource>))]
public enum ReceiptsSource
{
    /// <summary>The append-only log at <c>~/.lightning-enable/receipts.jsonl</c>.</summary>
    durable,

    /// <summary>In-memory payments made since this server started.</summary>
    session,
}

#pragma warning restore CA1707, IDE1006

/// <summary>
/// The consolidated payment-log verb: <c>get_receipts</c> + <c>get_payment_history</c>.
///
/// The two logs answer different questions — "what has this wallet ever spent, and how do
/// I pull the plug?" versus "what did this session spend?" — so both are kept, selected by
/// <see cref="ReceiptsSource"/> rather than by two separate tool schemas. Each source keeps
/// its own default limit, so an aliased call behaves exactly as it did before.
/// </summary>
[McpServerToolType]
public static class ReceiptsTool
{
    /// <summary>Default page size for the durable receipt log.</summary>
    internal const int DurableDefaultLimit = 20;

    /// <summary>Default page size for the in-session payment history.</summary>
    internal const int SessionDefaultLimit = 10;

    /// <summary>Lists payments from the durable receipt log or this session's history.</summary>
    [McpServerTool(
        Name = "receipts",
        Title = "Payment receipts",
        ReadOnly = true,
        OpenWorld = false)]
    [Description(
        "List payments this wallet made. The durable log persists across restarts and says "
        + "how to revoke the wallet; the session list is in-memory only.")]
    public static string Receipts(
        [Description("durable: the append-only log at ~/.lightning-enable/receipts.jsonl. session: payments since startup.")]
        ReceiptsSource source,
        [Description("Max entries (durable clamps to 1-200)")] int? limit = null,
        IReceiptService? receiptService = null,
        IPaymentHistoryService? historyService = null)
        => source switch
        {
            ReceiptsSource.durable => GetReceiptsTool.GetReceipts(
                limit ?? DurableDefaultLimit, receiptService),
            ReceiptsSource.session => GetPaymentHistoryTool.GetPaymentHistory(
                limit ?? SessionDefaultLimit, historyService),
            _ => ActionErrors.Unknown("receipts", "source", source, ReceiptsSource.durable, ReceiptsSource.session),
        };
}
