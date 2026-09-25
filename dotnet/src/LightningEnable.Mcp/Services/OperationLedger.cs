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
    /// <summary>Submitted, then the outcome was lost (timeout, cancellation, transport
    /// error after the money-moving call was issued). Funds MAY have moved. Blocks a re-send.</summary>
    Unknown,
}

/// <summary>A single durable operation record (the latest known state for an operation id).</summary>
public sealed record OperationRecord(
    string OperationId,
    OperationState State,
    long AmountSats,
    string? PaymentHash,
    string? PaymentId = null,
    string? QuoteId = null,
    string? TxId = null);

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

    /// <summary>
    /// ATOMIC check-and-record: if the operation is already in a money-moving state
    /// (Submitted, Pending, Unknown, Settled) returns false with that record in
    /// <paramref name="existing"/> and writes nothing; otherwise records Submitted and returns
    /// true. Two concurrent callers for the same id can never both get true.
    /// </summary>
    bool TryBeginSubmission(string operationId, long amountSats, string provider, out OperationRecord? existing);

    /// <summary>Records the resolved outcome of an operation. Provider ids (payment id, quote
    /// id, txid) are public references, never credentials; a null id keeps the one already
    /// recorded.</summary>
    void RecordOutcome(string operationId, OperationState state, string? paymentHash,
        string? paymentId = null, string? quoteId = null, string? txId = null);
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

    public bool TryBeginSubmission(string operationId, long amountSats, string provider, out OperationRecord? existing)
    {
        existing = null;
        if (string.IsNullOrEmpty(operationId)) return false;
        // Check and record under ONE lock hold so two concurrent callers for the same id can
        // never both observe "not in flight" (the lock is re-entrant for the nested Write).
        lock (_lock)
        {
            EnsureLoaded();
            if (_index!.TryGetValue(operationId, out var rec) && BlocksResubmission(rec.State))
            {
                existing = rec;
                return false;
            }
            Write(operationId, OperationState.Submitted, amountSats, paymentHash: null, provider);
            return true;
        }
    }

    /// <summary>Whether a recorded state means funds may have moved (so a re-send is refused).
    /// Only a proven <see cref="OperationState.FailedNoFunds"/> allows a fresh submission.</summary>
    public static bool BlocksResubmission(OperationState state) =>
        state is OperationState.Submitted or OperationState.Pending
            or OperationState.Settled or OperationState.Unknown;

    public void RecordOutcome(string operationId, OperationState state, string? paymentHash,
        string? paymentId = null, string? quoteId = null, string? txId = null)
    {
        lock (_lock)
        {
            // Preserve what is already recorded for this operation (the outcome line need not
            // repeat it): amount, and any provider id the new outcome does not supply.
            var prior = Lookup(operationId);
            Write(operationId, state, prior?.AmountSats ?? 0, paymentHash ?? prior?.PaymentHash, provider: null,
                paymentId ?? prior?.PaymentId, quoteId ?? prior?.QuoteId, txId ?? prior?.TxId);
        }
    }


    /// <summary>
    /// Parses a persisted state name from EITHER port. This file is shared with the Python
    /// port, which writes lower-case values (<c>submitted</c>, <c>failed_no_funds</c>) where
    /// .NET writes enum names (<c>Submitted</c>, <c>FailedNoFunds</c>). A line the other
    /// flavor wrote must never be dropped, or a submitted send becomes re-sendable.
    /// </summary>
    internal static bool TryParseState(string? raw, out OperationState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().Replace("_", string.Empty);
        if (Enum.TryParse<OperationState>(s, ignoreCase: true, out state)) return true;
        if (s.Equals("failed", StringComparison.OrdinalIgnoreCase)) { state = OperationState.FailedNoFunds; return true; }
        return false;
    }

    private void Write(string operationId, OperationState state, long amountSats, string? paymentHash, string? provider,
        string? paymentId = null, string? quoteId = null, string? txId = null)
    {
        if (string.IsNullOrEmpty(operationId)) return;
        lock (_lock)
        {
            EnsureLoaded();
            var record = new OperationRecord(operationId, state, amountSats, paymentHash, paymentId, quoteId, txId);
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
                // Provider references (Strike payment/quote id, on-chain txid) are public
                // lookup handles, not credentials — they let a retry refresh status.
                if (!string.IsNullOrEmpty(paymentId)) line["paymentId"] = paymentId;
                if (!string.IsNullOrEmpty(quoteId)) line["quoteId"] = quoteId;
                if (!string.IsNullOrEmpty(txId)) line["txId"] = txId;
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
                        if (string.IsNullOrEmpty(id) || !TryParseState(stateStr, out var state))
                            continue;
                        var amount = obj["amountSats"]?.GetValue<long>() ?? 0;
                        var hash = obj["paymentHash"]?.GetValue<string>();
                        var paymentId = obj["paymentId"]?.GetValue<string>();
                        var quoteId = obj["quoteId"]?.GetValue<string>();
                        var txId = obj["txId"]?.GetValue<string>();
                        // Last line wins — the file is append-only in chronological order.
                        _index[id] = new OperationRecord(id, state, amount, hash, paymentId, quoteId, txId);
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
