using System.Text.Json;
using FluentAssertions;
using LightningEnable.Mcp.Models;
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

    private static PaymentReceiptEntry Entry(long amountSats = 1, string wallet = "NWC",
        string? context = null, string? policy = "auto_approve", long? sessionSpentSats = null) => new()
    {
        Kind = "l402",
        Wallet = wallet,
        AmountSats = amountSats,
        Status = "settled",
        Context = context,
        Policy = policy,
        SessionSpentSats = sessionSpentSats,
    };

    [Fact]
    public void LogPayment_ThenRead_Roundtrips()
    {
        var svc = new ReceiptService(TempReceiptsPath());
        svc.LogPayment(Entry(amountSats: 1, context: "https://api.example.com/x", sessionSpentSats: 1))
            .Should().BeTrue();

        var recs = svc.ReadRecent(20);
        recs.Should().HaveCount(1);
        var o = recs[0].AsObject();
        o["type"]!.GetValue<string>().Should().Be("payment_receipt");
        o["kind"]!.GetValue<string>().Should().Be("l402");
        o["amountSats"]!.GetValue<long>().Should().Be(1);
        o["wallet"]!.GetValue<string>().Should().Be("NWC");
        o["status"]!.GetValue<string>().Should().Be("settled");
        o["context"]!.GetValue<string>().Should().Be("https://api.example.com/x");
        o["policy"]!.GetValue<string>().Should().Be("auto_approve");
        o["sessionSpentSats"]!.GetValue<long>().Should().Be(1);
        o["revokePath"]!.GetValue<string>().ToLowerInvariant().Should().Contain("connection");
        // canonical millisecond Z timestamp (parity with Python)
        o["timestamp"]!.GetValue<string>().Should().EndWith("Z");
    }

    [Fact]
    public void Receipt_CarriesNoSecrets()
    {
        var path = TempReceiptsPath();
        var svc = new ReceiptService(path);
        svc.LogPayment(Entry(context: "https://api.example.com/x"));

        var raw = File.ReadAllText(path);
        foreach (var forbidden in new[] { "preimage", "macaroon", "nostr+walletconnect", "connectionString" })
            raw.Should().NotContain(forbidden);
    }

    [Fact]
    public void OptionalFields_AreOmitted_NotNull()
    {
        var path = TempReceiptsPath();
        var svc = new ReceiptService(path);
        svc.LogPayment(new PaymentReceiptEntry { Kind = "invoice", Wallet = "NWC", AmountSats = 5 });

        var o = svc.ReadRecent(1)[0].AsObject();
        foreach (var absent in new[] { "status", "paymentHash", "context", "policy", "sessionSpentSats", "feeSats", "txId" })
            o.ContainsKey(absent).Should().BeFalse($"'{absent}' was not supplied and must be omitted");
    }

    [Fact]
    public void UnknownWallet_GetsDefaultRevokePath()
    {
        var svc = new ReceiptService(TempReceiptsPath());
        svc.LogPayment(Entry(wallet: "SomethingNew"));

        var o = svc.ReadRecent(1)[0].AsObject();
        o["revokePath"]!.GetValue<string>().ToLowerInvariant().Should().Contain("revoke");
    }

    [Fact]
    public void ReadRecent_NewestLast_RespectsLimit()
    {
        var svc = new ReceiptService(TempReceiptsPath());
        for (var i = 0; i < 5; i++) svc.LogPayment(Entry(amountSats: i, context: $"https://e/{i}"));

        var recs = svc.ReadRecent(2);
        recs.Should().HaveCount(2);
        recs[^1].AsObject()["amountSats"]!.GetValue<long>().Should().Be(4);
    }

    [Fact]
    public void ReadRecent_ZeroLimit_IsEmpty()
    {
        var svc = new ReceiptService(TempReceiptsPath());
        svc.LogPayment(Entry());
        svc.ReadRecent(0).Should().BeEmpty();
    }

    [Fact]
    public void ReadRecent_MissingFile_IsEmpty()
    {
        new ReceiptService(TempReceiptsPath()).ReadRecent(20).Should().BeEmpty();
    }

    [Fact]
    public void ReadRecent_NonObjectLine_IsSkipped()
    {
        var path = TempReceiptsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"amountSats\":1}\n42\n\"a string\"\n[1,2]\n{\"amountSats\":2}\n");

        var recs = new ReceiptService(path).ReadRecent(20);
        recs.Select(r => r.AsObject()["amountSats"]!.GetValue<long>()).Should().Equal(1, 2);
    }

    [Fact]
    public void ReadRecent_IncludesRotatedBackup()
    {
        var path = TempReceiptsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".1", "{\"amountSats\":1}\n{\"amountSats\":2}\n");
        File.WriteAllText(path, "{\"amountSats\":3}\n");

        var recs = new ReceiptService(path).ReadRecent(20);
        recs.Select(r => r.AsObject()["amountSats"]!.GetValue<long>()).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Rotation_BoundsTheFile_AndPreservesHistoryInBackup()
    {
        var path = TempReceiptsPath();
        // Tiny cap so a handful of appends trips rotation.
        var svc = new ReceiptService(path, maxBytes: 120);
        for (var i = 0; i < 30; i++) svc.LogPayment(Entry(amountSats: i, context: $"https://e/{i}"));

        File.Exists(path + ".1").Should().BeTrue("the live file must rotate to a .1 backup once past the cap");
        // read merges backup + live, so recent history survives rotation
        svc.ReadRecent(50).Should().NotBeEmpty();
    }

    [Fact]
    public void LogPayment_WriteFailure_NeverThrows_ReturnsFalse()
    {
        // Parent path is a FILE, so the directory create/append fails — must be
        // swallowed, and the failure reported through the return value.
        var dir = Path.Combine(Path.GetTempPath(), "le-receipts-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "afile");
        File.WriteAllText(aFile, "x");

        var svc = new ReceiptService(Path.Combine(aFile, "sub", "receipts.jsonl"));
        var written = true;
        var act = () => { written = svc.LogPayment(Entry()); };
        act.Should().NotThrow();
        written.Should().BeFalse("a failed write must be reported, not silently swallowed");
    }

    [Fact]
    public void GetReceiptsTool_Summarizes()
    {
        var svc = new ReceiptService(TempReceiptsPath());
        svc.LogPayment(Entry(amountSats: 2, context: "https://e/1"));
        svc.LogPayment(Entry(amountSats: 3, context: "https://e/2"));

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
