using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Services;
using LightningEnable.Mcp.Tools;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Tests for the durable payment receipt log (ReceiptService) + get_receipts tool.
/// </summary>
public class ReceiptServiceTests
{
    private static string TempReceiptsPath() =>
        Path.Combine(Path.GetTempPath(), "le-receipts-tests", Guid.NewGuid().ToString("N"), "receipts.jsonl");

    [Fact]
    public void LogPayment_ThenRead_Roundtrips()
    {
        var svc = new ReceiptService(TempReceiptsPath());
        svc.LogPayment("NWC", "https://api.example.com/x", 1, "AutoApprove", 1, 149.0m);

        var recs = svc.ReadRecent(20);
        recs.Should().HaveCount(1);
        var o = recs[0].AsObject();
        o["type"]!.GetValue<string>().Should().Be("l402_payment_receipt");
        o["amountSats"]!.GetValue<long>().Should().Be(1);
        o["wallet"]!.GetValue<string>().Should().Be("NWC");
        o["policy"]!.GetValue<string>().Should().Be("AutoApprove");
        o["sessionSpentSats"]!.GetValue<long>().Should().Be(1);
        o["revokePath"]!.GetValue<string>().ToLowerInvariant().Should().Contain("connection");
    }

    [Fact]
    public void Receipt_CarriesNoSecrets()
    {
        var path = TempReceiptsPath();
        var svc = new ReceiptService(path);
        svc.LogPayment("NWC", "https://api.example.com/x", 1, "AutoApprove", null, null);

        var raw = File.ReadAllText(path);
        foreach (var forbidden in new[] { "preimage", "macaroon", "nostr+walletconnect", "connectionString" })
            raw.Should().NotContain(forbidden);
    }

    [Fact]
    public void ReadRecent_NewestLast_RespectsLimit()
    {
        var svc = new ReceiptService(TempReceiptsPath());
        for (var i = 0; i < 5; i++) svc.LogPayment("NWC", $"https://e/{i}", i, "p", null, null);

        var recs = svc.ReadRecent(2);
        recs.Should().HaveCount(2);
        recs[^1].AsObject()["amountSats"]!.GetValue<long>().Should().Be(4);
    }

    [Fact]
    public void ReadRecent_MissingFile_IsEmpty()
    {
        new ReceiptService(TempReceiptsPath()).ReadRecent(20).Should().BeEmpty();
    }

    [Fact]
    public void ReadRecent_TornLine_IsSkipped()
    {
        var path = TempReceiptsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"amountSats\":1}\nnot valid json\n{\"amountSats\":2}\n");

        var recs = new ReceiptService(path).ReadRecent(20);
        recs.Select(r => r.AsObject()["amountSats"]!.GetValue<long>()).Should().Equal(1, 2);
    }

    [Fact]
    public void LogPayment_WriteFailure_NeverThrows()
    {
        // Parent path is a FILE, so the directory create/append fails — must be swallowed.
        var dir = Path.Combine(Path.GetTempPath(), "le-receipts-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "afile");
        File.WriteAllText(aFile, "x");

        var svc = new ReceiptService(Path.Combine(aFile, "sub", "receipts.jsonl"));
        var act = () => svc.LogPayment("NWC", "https://e", 1, "p", null, null);
        act.Should().NotThrow();
    }

    [Fact]
    public void GetReceiptsTool_Summarizes()
    {
        var svc = new ReceiptService(TempReceiptsPath());
        svc.LogPayment("NWC", "https://e/1", 2, "AutoApprove", null, null);
        svc.LogPayment("NWC", "https://e/2", 3, "AutoApprove", null, null);

        var json = JsonDocument.Parse(GetReceiptsTool.GetReceipts(limit: 10, receiptService: svc)).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("count").GetInt32().Should().Be(2);
        json.GetProperty("totalSatsInView").GetInt64().Should().Be(5);
        json.GetProperty("logFile").GetString().Should().EndWith("receipts.jsonl");
    }

    [Fact]
    public void GetReceiptsTool_NoService_ReturnsUnavailable()
    {
        var json = JsonDocument.Parse(GetReceiptsTool.GetReceipts(receiptService: null)).RootElement;
        json.GetProperty("success").GetBoolean().Should().BeFalse();
    }
}
