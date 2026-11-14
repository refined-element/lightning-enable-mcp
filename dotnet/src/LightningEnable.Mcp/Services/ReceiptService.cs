using System.Text.Json.Nodes;
using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Writes and reads durable, append-only spend receipts at
/// <c>~/.lightning-enable/receipts.jsonl</c> — one JSON object per line.
///
/// This is the audit + revocation record the in-memory
/// <see cref="IPaymentHistoryService"/> is not: it survives the session and lives
/// off the agent's context path, so rapid machine-to-machine / agent-to-agent flows
/// don't pay a per-payment token cost for it. Never contains secrets (no preimage,
/// macaroon, or wallet connection string). <c>revokePath</c> is instructions only.
///
/// New lines are generic <c>payment_receipt</c> records; pre-generalization
/// <c>l402_payment_receipt</c> lines stay readable (ReadRecent is schema-agnostic).
/// </summary>
public interface IReceiptService
{
    /// <summary>
    /// Appends one receipt line. Returns true only when the line was durably
    /// written — a false return is the caller's honest "receipt_written: false"
    /// signal. Never throws: a receipt must never break a payment.
    /// </summary>
    bool LogPayment(PaymentReceiptEntry entry);

    List<JsonNode> ReadRecent(int limit);

    string Path { get; }

    /// <summary>
    /// Appends the request/response SHA-256 of a paid L402 exchange (an <c>exchange_digest</c>
    /// line), written after the paid retry returns. Hashes only — never the bodies. Never throws.
    /// </summary>
    bool LogExchangeDigest(string paymentHash, string requestSha256, string responseSha256) => false;

    /// <summary>
    /// What the local log knows about <paramref name="paymentHash"/>: null when it is not there,
    /// else the payment hash plus the request/response digests when they were recorded.
    /// </summary>
    LocalPaymentRecord? LookupPayment(string paymentHash) => null;
}

/// <summary>A payment the local receipt log knows, with its exchange digests if recorded.</summary>
public sealed record LocalPaymentRecord(string PaymentHash, string? RequestSha256, string? ResponseSha256);

/// <summary>
/// The digest definitions shared with the Python port (pinned by
/// <c>shared/attestation-golden-vector.json</c>).
/// </summary>
public static class ExchangeDigest
{
    public const string LineType = "exchange_digest";

    /// <summary>
    /// SHA-256 over UTF-8 <c>"&lt;METHOD&gt; &lt;url&gt;\n"</c> (method upper-cased, URL exactly as
    /// requested) followed by the request body bytes (none for GET/HEAD).
    /// </summary>
    public static string RequestSha256(string method, string url, byte[]? body)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var m = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        h.AppendData(System.Text.Encoding.UTF8.GetBytes($"{m} {url}\n"));
        if (body is { Length: > 0 }) h.AppendData(body);
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>SHA-256 of the response body bytes exactly as the client read them.</summary>
    public static string ResponseSha256(byte[]? body) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body ?? Array.Empty<byte>())).ToLowerInvariant();
}

public sealed class ReceiptService : IReceiptService
{
    // Rotate to a single ".1" backup once the live file passes this size, so the
    // log is self-limiting (~2x this cap) without per-write trims.
    private const long DefaultMaxBytes = 5 * 1024 * 1024; // 5 MB

    private static readonly object _lock = new();

    private static readonly Dictionary<string, string> RevokePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NWC"] = "Your NWC wallet app (CoinOS / Alby Hub / CLINK) → Connections / Nostr Wallet Connect → delete this connection.",
        ["Strike"] = "Strike dashboard (dashboard.strike.me) → API keys → revoke the key this agent uses.",
        ["LND"] = "Your LND node → bake a new macaroon and revoke/rotate the one this client uses.",
        ["OpenNode"] = "OpenNode dashboard → Integrations / API keys → revoke the key this agent uses.",
    };
    private const string RevokeDefault = "Revoke this wallet's connection or API key in its own app/dashboard.";

    private readonly string _path;
    private readonly long _maxBytes;

    public ReceiptService(IBudgetConfigurationService? configService = null)
    {
        var dir = TryGetConfigDir(configService)
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lightning-enable");
        _path = System.IO.Path.Combine(dir, "receipts.jsonl");
        _maxBytes = DefaultMaxBytes;
    }

    // Test-only: write to an explicit path.
    internal ReceiptService(string receiptsPath)
    {
        _path = receiptsPath;
        _maxBytes = DefaultMaxBytes;
    }

    // Test-only: explicit path + a shrunk rotation threshold to exercise rotation cheaply.
    internal ReceiptService(string receiptsPath, long maxBytes)
    {
        _path = receiptsPath;
        _maxBytes = maxBytes;
    }

    public string Path => _path;

    private static string? TryGetConfigDir(IBudgetConfigurationService? cs)
    {
        try
        {
            var p = cs?.ConfigFilePath;
            return string.IsNullOrWhiteSpace(p) ? null : System.IO.Path.GetDirectoryName(p);
        }
        catch { return null; }
    }

    public bool LogPayment(PaymentReceiptEntry entry)
    {
        try
        {
            var label = string.IsNullOrWhiteSpace(entry.Wallet) ? "unknown" : entry.Wallet;
            var receipt = new JsonObject
            {
                ["type"] = "payment_receipt",
                ["kind"] = entry.Kind,
                // Canonical millisecond-precision UTC "Z" form (parity with the Python side).
                ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["wallet"] = label,
                ["amountSats"] = entry.AmountSats,
            };
            // Optional fields are omitted, not null — keeps every line lean and lets
            // readers treat presence as meaning.
            if (!string.IsNullOrEmpty(entry.Status)) receipt["status"] = entry.Status;
            if (!string.IsNullOrEmpty(entry.PaymentHash)) receipt["paymentHash"] = entry.PaymentHash;
            if (!string.IsNullOrEmpty(entry.Context)) receipt["context"] = entry.Context;
            if (!string.IsNullOrEmpty(entry.Policy)) receipt["policy"] = entry.Policy;
            // Post-payment session total (session-remaining is intentionally omitted:
            // deriving it consistently across runtimes was error-prone — spentSats is
            // the accurate, unambiguous figure).
            if (entry.SessionSpentSats.HasValue) receipt["sessionSpentSats"] = entry.SessionSpentSats.Value;
            if (entry.FeeSats.HasValue) receipt["feeSats"] = entry.FeeSats.Value;
            if (!string.IsNullOrEmpty(entry.TxId)) receipt["txId"] = entry.TxId;
            receipt["revokePath"] = RevokePaths.TryGetValue(label, out var rp) ? rp : RevokeDefault;
            Append(receipt.ToJsonString());
            return true;
        }
        catch (Exception ex)
        {
            // A receipt is an audit convenience — it must NEVER break a payment.
            // The false return keeps the failure VISIBLE (receipt_written: false).
            Console.Error.WriteLine($"[Lightning Enable] Failed to write payment receipt: {ex.Message}");
            return false;
        }
    }

    private void Append(string line)
    {
        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            RotateIfNeeded();
            var isNew = !File.Exists(_path);
            File.AppendAllText(_path, line + "\n");
            if (isNew) RestrictPerms();
        }
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
            // Windows: the config directory is already ACL-restricted on first-run
            // config write; the receipts file inherits that. Best-effort skip here.
        }
        catch { /* best effort, mirrors config.json */ }
    }

    public bool LogExchangeDigest(string paymentHash, string requestSha256, string responseSha256)
    {
        if (string.IsNullOrEmpty(paymentHash) || string.IsNullOrEmpty(requestSha256) || string.IsNullOrEmpty(responseSha256))
            return false;
        try
        {
            var line = new JsonObject
            {
                ["type"] = ExchangeDigest.LineType,
                ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["paymentHash"] = paymentHash.ToLowerInvariant(),
                ["requestSha256"] = requestSha256.ToLowerInvariant(),
                ["responseSha256"] = responseSha256.ToLowerInvariant(),
            };
            Append(line.ToJsonString());
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] Failed to write exchange digest: {ex.Message}");
            return false;
        }
    }

    public LocalPaymentRecord? LookupPayment(string paymentHash)
    {
        var wanted = (paymentHash ?? string.Empty).Trim().ToLowerInvariant();
        if (wanted.Length == 0) return null;
        LocalPaymentRecord? found = null;
        foreach (var obj in ReadObjects(2_000))
        {
            if (!string.Equals(Str(obj, "paymentHash"), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            found ??= new LocalPaymentRecord(wanted, null, null);
            if (Str(obj, "type") == ExchangeDigest.LineType)
                found = found with { RequestSha256 = Str(obj, "requestSha256"), ResponseSha256 = Str(obj, "responseSha256") };
        }
        return found;
    }

    /// <summary>
    /// The most recent receipts (newest last). <c>exchange_digest</c> lines are not returned on
    /// their own: their request/response hashes are folded into the payment receipt with the
    /// same payment hash.
    /// </summary>
    public List<JsonNode> ReadRecent(int limit)
    {
        var outp = new List<JsonNode>();
        if (limit <= 0) return outp;
        // Each paid L402 call can add one digest line after its receipt, so read twice the
        // window to still return `limit` receipts.
        var objects = ReadObjects(limit * 2);
        var digests = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objects)
        {
            if (Str(o, "type") == ExchangeDigest.LineType && Str(o, "paymentHash") is { } h)
                digests[h] = o;
        }
        foreach (var o in objects)
        {
            if (Str(o, "type") == ExchangeDigest.LineType) continue;
            if (Str(o, "paymentHash") is { } h && digests.TryGetValue(h, out var d))
            {
                o["requestSha256"] = Str(d, "requestSha256");
                o["responseSha256"] = Str(d, "responseSha256");
            }
            // Redact at the READ boundary so every surface — the `receipts` tool and the
            // lightning-enable://receipts resource alike — is covered by one pass.
            outp.Add(ReceiptRedaction.Redact(o)!);
        }
        return outp.Count > limit ? outp.GetRange(outp.Count - limit, limit) : outp;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private List<JsonObject> ReadObjects(int maxLines)
    {
        var outp = new List<JsonObject>();
        try
        {
            // Read the rotated ".1" backup first (older) then the live file (newer), so
            // a read right after a rotation still returns recent history.
            var lines = new List<string>();
            foreach (var p in new[] { _path + ".1", _path })
            {
                if (!File.Exists(p)) continue;
                lock (_lock) { lines.AddRange(File.ReadAllLines(p)); }
            }

            var start = Math.Max(0, lines.Count - maxLines);
            for (var i = start; i < lines.Count; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0) continue;
                try
                {
                    // Only surface object lines; skip a torn/interleaved/non-object line.
                    if (JsonNode.Parse(l) is JsonObject obj) outp.Add(obj);
                }
                catch { /* skip a torn/partial line rather than fail the whole read */ }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lightning Enable] Failed to read receipts: {ex.Message}");
        }
        return outp;
    }
}
