using LightningEnable.Mcp.Services;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// The durable operation ledger is the idempotency/restart-safety record: it persists
/// "this payment intent was submitted / settled / failed" to disk so a retry — even one
/// that spans a process restart — cannot cause a blind duplicate payment. It stores NO
/// secrets (no preimage/macaroon/invoice), only an opaque operation id + state.
/// </summary>
public class OperationLedgerTests
{
    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "op-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");

    [Fact]
    public void UnknownOperation_ReturnsNull()
    {
        var ledger = new OperationLedger(TempPath());
        ledger.Lookup("ln:unknown").Should().BeNull();
    }

    [Fact]
    public void RecordSubmitted_ThenLookup_ReturnsSubmitted()
    {
        var ledger = new OperationLedger(TempPath());
        ledger.RecordSubmitted("ln:abc", 100, "NWC");

        var record = ledger.Lookup("ln:abc");
        record.Should().NotBeNull();
        record!.State.Should().Be(OperationState.Submitted);
        record.AmountSats.Should().Be(100);
    }

    [Fact]
    public void RecordOutcome_TakesTheLatestState()
    {
        var ledger = new OperationLedger(TempPath());
        ledger.RecordSubmitted("ln:abc", 100, "NWC");
        ledger.RecordOutcome("ln:abc", OperationState.Settled, paymentHash: "deadbeef");

        ledger.Lookup("ln:abc")!.State.Should().Be(OperationState.Settled);
    }

    [Fact]
    public void Lookup_SurvivesRestart_NewInstanceReadsLastStateFromDisk()
    {
        var path = TempPath();
        new OperationLedger(path).RecordSubmitted("ln:abc", 100, "NWC");

        // Simulate a process restart: a brand-new ledger over the same file must see the
        // prior state. This is what stops a blind duplicate payment after a crash/restart.
        var afterRestart = new OperationLedger(path);
        afterRestart.Lookup("ln:abc")!.State.Should().Be(OperationState.Submitted);
    }

    [Fact]
    public void DistinctOperations_DoNotInterfere()
    {
        var ledger = new OperationLedger(TempPath());
        ledger.RecordSubmitted("ln:aaa", 100, "NWC");
        ledger.RecordOutcome("ln:aaa", OperationState.FailedNoFunds, null);
        ledger.RecordSubmitted("ln:bbb", 200, "NWC");

        ledger.Lookup("ln:aaa")!.State.Should().Be(OperationState.FailedNoFunds);
        ledger.Lookup("ln:bbb")!.State.Should().Be(OperationState.Submitted);
    }

    [Fact]
    public void NeverPersistsSecrets()
    {
        var path = TempPath();
        var ledger = new OperationLedger(path);
        ledger.RecordSubmitted("ln:abc", 100, "NWC");
        ledger.RecordOutcome("ln:abc", OperationState.Settled, "deadbeefcafe");

        var contents = System.IO.File.ReadAllText(path);
        // A payment hash is public routing data and MAY appear; a preimage/macaroon/invoice
        // must NEVER. This guards the log against a future field leaking a secret.
        contents.Should().NotContain("preimage");
        contents.Should().NotContain("macaroon");
        contents.Should().NotContain("lnbc");
    }
}
