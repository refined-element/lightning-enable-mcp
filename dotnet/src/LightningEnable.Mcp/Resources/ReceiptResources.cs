using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LightningEnable.Mcp.Resources;

/// <summary>
/// The durable receipt log, exposed as MCP RESOURCES alongside the <c>receipts</c> tool.
///
/// <para>A tool call is the agent deciding to look; a resource is something a client can
/// attach, watch, or show a human without the model spending a turn on it. The spend log is
/// exactly that kind of artifact — which is why it lives off the agent's hot path in the
/// first place — so it is worth both shapes.</para>
///
/// <list type="bullet">
/// <item><description><c>lightning-enable://receipts</c> — the most recent
/// <see cref="MaxRows"/> receipts, as JSONL (one JSON object per line), oldest first.</description></item>
/// <item><description><c>lightning-enable://receipts/{paymentHash}</c> — every receipt for
/// one payment hash.</description></item>
/// </list>
///
/// <para>Both go through <see cref="IReceiptService.ReadRecent"/>, which redacts at the read
/// boundary (<see cref="ReceiptRedaction"/>), so a preimage cannot leave here even if one
/// somehow reached the file. Keep in lockstep with the Python port
/// (<c>server.py</c>'s <c>list_resources</c> / <c>read_resource</c>).</para>
/// </summary>
[McpServerResourceType]
public static class ReceiptResources
{
    /// <summary>The whole-log resource URI.</summary>
    public const string ReceiptsUri = "lightning-enable://receipts";

    /// <summary>The per-payment-hash resource template.</summary>
    public const string ReceiptByHashUriTemplate = "lightning-enable://receipts/{paymentHash}";

    /// <summary>How many receipts the log resource carries. Matches the tool's clamp.</summary>
    public const int MaxRows = 200;

    /// <summary>
    /// How far back a single-hash lookup searches. Deeper than <see cref="MaxRows"/> because
    /// looking up one known payment is a different question from "what happened lately".
    /// </summary>
    private const int LookupWindow = 2_000;

    /// <summary>JSONL: newline-delimited JSON, one receipt per line.</summary>
    private const string JsonLinesMimeType = "application/x-ndjson";

    [McpServerResource(
        Name = "receipts",
        Title = "Payment receipts",
        UriTemplate = ReceiptsUri,
        MimeType = JsonLinesMimeType)]
    [Description(
        "The durable, append-only payment receipt log (~/.lightning-enable/receipts.jsonl) — "
        + "the most recent 200 receipts as JSONL, oldest first. Never contains preimages.")]
    public static string Receipts(IReceiptService? receiptService = null)
    {
        var receipts = Read(receiptService, MaxRows);
        return ToJsonLines(receipts);
    }

    [McpServerResource(
        Name = "receipt",
        Title = "Payment receipt by payment hash",
        UriTemplate = ReceiptByHashUriTemplate,
        MimeType = JsonLinesMimeType)]
    [Description(
        "Every durable receipt recorded for one payment hash, as JSONL. Never contains "
        + "preimages — the payment hash is the safe reference, the preimage is the proof of "
        + "payment and is not a receipt field.")]
    public static string Receipt(
        string paymentHash,
        IReceiptService? receiptService = null)
    {
        if (string.IsNullOrWhiteSpace(paymentHash))
        {
            throw new McpException(
                "A payment hash is required. Read " + ReceiptsUri + " to see recent receipts "
                + "and their payment hashes.");
        }

        var wanted = paymentHash.Trim();
        var matches = Read(receiptService, LookupWindow)
            .Where(r => string.Equals(
                (r as JsonObject)?["paymentHash"]?.ToString(),
                wanted,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            throw new McpException(
                $"No receipt found for payment hash '{wanted}' in the most recent "
                + $"{LookupWindow:N0} entries of the durable log. Read {ReceiptsUri} to see "
                + "what is there.");
        }

        return ToJsonLines(matches);
    }

    private static List<JsonNode> Read(IReceiptService? receiptService, int limit)
    {
        if (receiptService is null)
        {
            throw new McpException(
                "Receipt logging is not available (no wallet/session initialized), so the "
                + "durable receipt log cannot be read.");
        }

        return receiptService.ReadRecent(limit);
    }

    private static string ToJsonLines(IEnumerable<JsonNode> receipts)
    {
        var builder = new StringBuilder();
        foreach (var receipt in receipts)
        {
            // Compact, one line each — JSONL is only JSONL if no record wraps.
            builder.Append(receipt.ToJsonString()).Append('\n');
        }
        return builder.ToString();
    }
}
