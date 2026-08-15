using System.Text.Json.Nodes;

namespace LightningEnable.Mcp.Services;

/// <summary>Lifecycle state of a payment operation in the durable ledger.</summary>
public enum OperationState
{
    /// <summary>Submitted to the wallet; outcome not yet known. Blocks a re-pay.</summary>
    Submitted,
    /// <summary>Accepted but not settled. Blocks a re-pay (funds may still move).</summary>
    Pending,
    /// <summary>Settled — money moved. Blocks a re-pay.</summary>
    Settled,
    /// <summary>Proven no funds moved. Does NOT block a retry.</summary>
    FailedNoFunds,
}

/// <summary>A single durable operation record (the latest known state for an operation id).</summary>
public sealed record OperationRecord(string OperationId, OperationState State, long AmountSats, string? PaymentHash);

/// <summary>
/// Durable, append-only idempotency ledger at <c>~/.lightning-enable/operations.jsonl</c>.
///
/// It records that a payment intent was submitted / settled / failed so a retry — even one
/// that spans a process restart — cannot cause a blind duplicate payment: the idempotency
/// guard consults <see cref="Lookup"/> before paying and refuses to re-submit an operation
/// already in a money-moving state. Stores NO secrets (no preimage, macaroon, invoice, or
/// connection string) — only an opaque operation id, amount, provider, and a public payment
/// hash. This is distinct from the receipt log (which proves an observed outcome); the
/// ledger governs execution/idempotency.
/// </summary>
public interface IOperationLedger
{
    /// <summary>Latest known state for the operation id, or null if never seen. Reads
    /// through to disk so it is correct after a restart.</summary>
    OperationRecord? Lookup(string operationId);

    /// <summary>Records that an operation was submitted to the wallet, BEFORE the wallet
    /// call — so a crash immediately after submission still leaves a durable record.</summary>
    void RecordSubmitted(string operationId, long amountSats, string provider);

    /// <summary>Records the resolved outcome of an operation.</summary>
    void RecordOutcome(string operationId, OperationState state, string? paymentHash);
}

public sealed class OperationLedger : IOperationLedger
{
    private const long DefaultMaxBytes = 5 * 1024 * 1024; // 5 MB, rotate to ".1"

    private static readonly object _lock = new();
    private readonly string _path;
    private readonly long _maxBytes;

    // Latest state per operation id, rebuilt from disk on first use (covers restart).
    private Dictionary<string, OperationRecord>? _index;

    public OperationLedger(IBudgetConfigurationService? configService = null)
    {
        var dir = TryGetConfigDir(configService)
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lightning-enable");
        _path = System.IO.Path.Combine(dir, "operations.jsonl");
        _maxBytes = DefaultMaxBytes;
    }

    // Test-only: explicit path.
    internal OperationLedger(string path)
    {
        _path = path;
        _maxBytes = DefaultMaxBytes;
    }

    public string Path => _path;

    public OperationRecord? Lookup(string operationId)
    {
        if (string.IsNullOrEmpty(operationId)) return null;
        lock (_lock)
        {
            EnsureLoaded();
            return _index!.TryGetValue(operationId, out var rec) ? rec : null;
        }
    }

    public void RecordSubmitted(string operationId, long amountSats, string provider)
        => Write(operationId, OperationState.Submitted, amountSats, paymentHash: null, provider);

    public void RecordOutcome(string operationId, OperationState state, string? paymentHash)
    {
        // Preserve the amount already recorded for this operation (the outcome line need
        // not repeat it); default to 0 if this is the first line we've seen.
        long amount = Lookup(operationId)?.AmountSats ?? 0;
        Write(operationId, state, amount, paymentHash, provider: null);
    }

    private void Write(string operationId, OperationState state, long amountSats, string? paymentHash, string? provider)
    {
        if (string.IsNullOrEmpty(operationId)) return;
        lock (_lock)
        {
            EnsureLoaded();
            var record = new OperationRecord(operationId, state, amountSats, paymentHash);
            _index![operationId] = record;

            try
            {
                var line = new JsonObject
                {
                    ["type"] = "operation",
                    ["operationId"] = operationId,
                    ["state"] = state.ToString(),
                    ["amountSats"] = amountSats,
                    ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                };
                if (!string.IsNullOrEmpty(provider)) line["provider"] = provider;
                // Payment hash is public routing data (safe to persist); it links the
                // operation to its receipt. Secrets are never written.
                if (!string.IsNullOrEmpty(paymentHash)) line["paymentHash"] = paymentHash;
                Append(line.ToJsonString());
            }
            catch (Exception ex)
            {
                // The in-memory index is already updated so idempotency holds for THIS
                // process even if the durable write fails; surface it, never throw.
                Console.Error.WriteLine($"[Lightning Enable] Failed to write operation ledger: {ex.Message}");
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_index != null) return;
        _index = new Dictionary<string, OperationRecord>(StringComparer.Ordinal);
        try
        {
            foreach (var p in new[] { _path + ".1", _path })
            {
                if (!File.Exists(p)) continue;
                foreach (var raw in File.ReadAllLines(p))
                {
                    var l = raw.Trim();
                    if (l.Length == 0) continue;
                    try
                    {
                        if (JsonNode.Parse(l) is not JsonObject obj) continue;
                        var id = obj["operationId"]?.GetValue<string>();
                        var stateStr = obj["state"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(id) || !Enum.TryParse<OperationState>(stateStr, out var state))
                            continue;
                        var amount = obj["amountSats"]?.GetValue<long>() ?? 0;
                        var hash = obj["paymentHash"]?.GetValue<string>();
                        // Last line wins — the file is append-only in chronological order.
                        _index[id] = new OperationRecord(id, state, amount, hash);
                    }
                    catch { /* skip a torn/partial line rather than fail the whole load */ }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] Failed to load operation ledger: {ex.Message}");
        }
    }

    private void Append(string line)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        RotateIfNeeded();
        var isNew = !File.Exists(_path);
        File.AppendAllText(_path, line + "\n");
        if (isNew) RestrictPerms();
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > _maxBytes)
            {
                var backup = _path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(_path, backup);
            }
        }
        catch { /* rotation is best-effort */ }
    }

    private void RestrictPerms()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
        }
        catch { /* best effort, mirrors receipts.jsonl / config.json */ }
    }

    private static string? TryGetConfigDir(IBudgetConfigurationService? cs)
    {
        try
        {
            var p = cs?.ConfigFilePath;
            return string.IsNullOrWhiteSpace(p) ? null : System.IO.Path.GetDirectoryName(p);
        }
        catch { return null; }
    }
}
