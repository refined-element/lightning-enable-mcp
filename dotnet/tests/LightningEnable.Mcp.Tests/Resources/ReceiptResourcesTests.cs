using System.Text.Json;
using System.Text.Json.Nodes;
using LightningEnable.Mcp.Resources;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace LightningEnable.Mcp.Tests.Resources;

/// <summary>
/// The durable receipt log as an MCP resource, over the real protocol.
///
/// <para>What matters here is the same thing that matters for the tool: a preimage is not a
/// receipt number, it IS the proof of payment, and it must never leave the process. The
/// resource inherits that guarantee from <see cref="IReceiptService.ReadRecent"/> — the tests
/// prove it end to end rather than trusting the seam.</para>
/// </summary>
public class ReceiptResourcesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "le-mcp-receipt-resources-" + Guid.NewGuid().ToString("N"));

    private string ReceiptsPath => Path.Combine(_dir, "receipts.jsonl");

    public ReceiptResourcesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp dir must never fail a test run.
        }
        GC.SuppressFinalize(this);
    }

    private void WriteReceipts(params string[] lines) =>
        File.WriteAllLines(ReceiptsPath, lines);

    private Task<McpToolHost> StartAsync() =>
        McpToolHost.StartAsync(ToolProfile.Standard, services =>
            services.AddSingleton<IReceiptService>(
                _ => ReceiptServiceFactory.ForPath(ReceiptsPath)));

    private static string Receipt(string paymentHash, long amountSats, string? extra = null) =>
        $$"""
        {"type":"payment_receipt","kind":"l402","timestamp":"2026-09-07T00:00:00.000Z","wallet":"NWC","amountSats":{{amountSats}},"paymentHash":"{{paymentHash}}"{{extra}}}
        """;

    // ── Listing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListResources_ContainsTheReceiptLogUri()
    {
        await using var host = await StartAsync();

        var resources = await host.Client.ListResourcesAsync();

        resources.Select(r => r.Uri).Should().Contain(ReceiptResources.ReceiptsUri);
    }

    [Fact]
    public async Task ListResourceTemplates_ContainsThePerPaymentHashTemplate()
    {
        await using var host = await StartAsync();

        var templates = await host.Client.ListResourceTemplatesAsync();

        templates.Select(t => t.UriTemplate).Should()
            .Contain(ReceiptResources.ReceiptByHashUriTemplate);
    }

    [Fact]
    public async Task TheLogResource_DeclaresJsonLines()
    {
        await using var host = await StartAsync();

        var resource = (await host.Client.ListResourcesAsync())
            .Single(r => r.Uri == ReceiptResources.ReceiptsUri);

        resource.MimeType.Should().Be("application/x-ndjson");
        resource.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TheReadResult_AlsoDeclaresJsonLines()
    {
        // The listing's media type is a promise; the read has to keep it.
        WriteReceipts(Receipt("hash-a", 100));
        await using var host = await StartAsync();

        var result = await host.Client.ReadResourceAsync(ReceiptResources.ReceiptsUri);

        result.Contents.OfType<TextResourceContents>().Single().MimeType
            .Should().Be("application/x-ndjson");
    }

    // ── Reading the log ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReadingTheLog_ReturnsOneJsonObjectPerLine()
    {
        WriteReceipts(Receipt("hash-a", 100), Receipt("hash-b", 250));
        await using var host = await StartAsync();

        var rows = ParseJsonLines(await ReadTextAsync(host, ReceiptResources.ReceiptsUri));

        rows.Should().HaveCount(2);
        rows[0]!["paymentHash"]!.ToString().Should().Be("hash-a");
        rows[1]!["amountSats"]!.GetValue<long>().Should().Be(250);
    }

    [Fact]
    public async Task ReadingTheLog_RedactsAPreimageThatSomehowReachedTheFile()
    {
        // Receipts never carry a preimage by construction. The log is a plain file on the
        // operator's disk, though, so a hand-edit or a future writer could put one there —
        // and by then it is one read away from a model's context.
        const string preimage = "d0bf1ee9a1b2c3d4e5f60718293a4b5c6d7e8f900112233445566778899aabbcc";
        WriteReceipts(Receipt("hash-a", 100, $",\"preimage\":\"{preimage}\""));
        await using var host = await StartAsync();

        var text = await ReadTextAsync(host, ReceiptResources.ReceiptsUri);

        text.Should().NotContain(preimage);
        ParseJsonLines(text)[0]!["preimage"]!.ToString().Should().Be(ReceiptRedaction.Placeholder,
            "the field is shown as withheld rather than silently dropped");
    }

    [Fact]
    public async Task ReadingTheLog_KeepsTheOrdinaryFields()
    {
        WriteReceipts(Receipt("hash-a", 100));
        await using var host = await StartAsync();

        var row = ParseJsonLines(await ReadTextAsync(host, ReceiptResources.ReceiptsUri))[0]!;

        row["amountSats"]!.GetValue<long>().Should().Be(100);
        row["wallet"]!.ToString().Should().Be("NWC");
        row["paymentHash"]!.ToString().Should().Be("hash-a");
    }

    [Fact]
    public async Task ReadingTheLog_SkipsATornLineRatherThanFailingTheWholeRead()
    {
        WriteReceipts(Receipt("hash-a", 100), "{not json", Receipt("hash-b", 200));
        await using var host = await StartAsync();

        ParseJsonLines(await ReadTextAsync(host, ReceiptResources.ReceiptsUri))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadingTheLog_WithNoFileYet_IsEmptyNotAnError()
    {
        await using var host = await StartAsync();

        var text = await ReadTextAsync(host, ReceiptResources.ReceiptsUri);

        text.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadingTheLog_IsCappedAt200Rows()
    {
        WriteReceipts(Enumerable.Range(0, 250).Select(i => Receipt($"hash-{i}", i + 1)).ToArray());
        await using var host = await StartAsync();

        var rows = ParseJsonLines(await ReadTextAsync(host, ReceiptResources.ReceiptsUri));

        rows.Should().HaveCount(ReceiptResources.MaxRows);
        rows[^1]!["paymentHash"]!.ToString().Should().Be("hash-249", "the most recent are kept");
    }

    // ── Reading one payment hash ────────────────────────────────────────────

    [Fact]
    public async Task ReadingOnePaymentHash_ReturnsOnlyThatPaymentsReceipts()
    {
        WriteReceipts(Receipt("hash-a", 100), Receipt("hash-b", 200), Receipt("hash-a", 300));
        await using var host = await StartAsync();

        var rows = ParseJsonLines(
            await ReadTextAsync(host, "lightning-enable://receipts/hash-a"));

        rows.Should().HaveCount(2);
        rows.Select(r => r!["amountSats"]!.GetValue<long>()).Should().Equal(100, 300);
    }

    [Fact]
    public async Task ReadingOnePaymentHash_AlsoRedacts()
    {
        const string preimage = "aa11bb22cc33dd44ee55ff6677889900aabbccddeeff00112233445566778899";
        WriteReceipts(Receipt("hash-a", 100, $",\"preImage\":\"{preimage}\""));
        await using var host = await StartAsync();

        (await ReadTextAsync(host, "lightning-enable://receipts/hash-a"))
            .Should().NotContain(preimage, "the check is on the property NAME, case-insensitively");
    }

    [Fact]
    public async Task ReadingAnUnknownPaymentHash_FailsWithAnActionableMessage()
    {
        WriteReceipts(Receipt("hash-a", 100));
        await using var host = await StartAsync();

        var act = async () => await host.Client.ReadResourceAsync(
            "lightning-enable://receipts/hash-nope");

        (await act.Should().ThrowAsync<Exception>()).Which.Message
            .Should().Contain("hash-nope").And.Contain(ReceiptResources.ReceiptsUri);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<string> ReadTextAsync(McpToolHost host, string uri)
    {
        var result = await host.Client.ReadResourceAsync(uri);
        return result.Contents.OfType<TextResourceContents>().Single().Text;
    }

    private static List<JsonNode?> ParseJsonLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line))
            .ToList();
}

/// <summary>Reaches the test-only <see cref="ReceiptService"/> constructor from this namespace.</summary>
internal static class ReceiptServiceFactory
{
    public static IReceiptService ForPath(string path) => new ReceiptService(path);
}

/// <summary>
/// The redactor itself, unit-tested — the resource tests prove the wiring, these pin the rule.
/// </summary>
public class ReceiptRedactionTests
{
    [Theory]
    [InlineData("preimage")]
    [InlineData("preImage")]
    [InlineData("PREIMAGE")]
    [InlineData("paymentPreimage")]
    [InlineData("secret")]
    [InlineData("macaroon")]
    [InlineData("nwcConnectionString")]
    [InlineData("apiKey")]
    public void SensitiveFieldsAreReplaced(string propertyName)
    {
        var node = JsonNode.Parse($$"""{"{{propertyName}}":"fixture-value-do-not-leak"}""")!;

        ReceiptRedaction.Redact(node);

        node[propertyName]!.ToString().Should().Be(ReceiptRedaction.Placeholder);
    }

    [Theory]
    [InlineData("paymentHash")]
    [InlineData("amountSats")]
    [InlineData("wallet")]
    [InlineData("revokePath")]
    public void OrdinaryFieldsAreUntouched(string propertyName)
    {
        var node = JsonNode.Parse($$"""{"{{propertyName}}":"keep-me"}""")!;

        ReceiptRedaction.Redact(node);

        node[propertyName]!.ToString().Should().Be("keep-me");
    }

    [Fact]
    public void PaymentHashIsNeverConfusedForAPreimage()
    {
        // The payment hash is the SAFE reference and the whole point of the receipt log.
        ReceiptRedaction.IsSensitive("paymentHash").Should().BeFalse();
        ReceiptRedaction.IsSensitive("preimage").Should().BeTrue();
    }

    [Fact]
    public void NestedObjectsAndArraysAreWalked()
    {
        var node = JsonNode.Parse(
            """{"outer":{"preimage":"x"},"list":[{"secret":"y"}]}""")!;

        ReceiptRedaction.Redact(node);

        node.ToJsonString().Should().NotContain("\"x\"").And.NotContain("\"y\"");
    }

    [Fact]
    public void ADeeplyNestedDocumentTerminates()
    {
        // Bounded depth: a pathological receipt must not hang the read path. 40 levels is
        // well past MaxDepth and still inside System.Text.Json's own 64-level read limit.
        var deep = new string('[', 40) + "1" + new string(']', 40);
        var node = JsonNode.Parse($$"""{"nested":{{deep}}}""")!;

        var act = () => ReceiptRedaction.Redact(node);

        act.Should().NotThrow();
    }

    [Fact]
    public void NonObjectNodesPassThrough()
    {
        ReceiptRedaction.Redact(null).Should().BeNull();
        ReceiptRedaction.Redact(JsonValue.Create(42))!.GetValue<int>().Should().Be(42);
    }
}
