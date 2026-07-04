using System.Text.Json.Nodes;

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
/// </summary>
public interface IReceiptService
{
    void LogPayment(string walletLabel, string endpoint, long amountSats, string policy,
        long? sessionSpentSats, decimal? sessionRemainingUsd);

    List<JsonNode> ReadRecent(int limit);

    string Path { get; }
}

public sealed class ReceiptService : IReceiptService
{
    // Rotate to a single ".1" backup once the live file passes this size, so the
    // log is self-limiting (~2x this cap) without per-write trims.
    private const long MaxBytes = 5 * 1024 * 1024; // 5 MB

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

    public ReceiptService(IBudgetConfigurationService? configService = null)
    {
        var dir = TryGetConfigDir(configService)
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lightning-enable");
        _path = System.IO.Path.Combine(dir, "receipts.jsonl");
    }

    // Test-only: write to an explicit path.
    internal ReceiptService(string receiptsPath) => _path = receiptsPath;

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

    public void LogPayment(string walletLabel, string endpoint, long amountSats, string policy,
        long? sessionSpentSats, decimal? sessionRemainingUsd)
    {
        try
        {
            var label = string.IsNullOrWhiteSpace(walletLabel) ? "unknown" : walletLabel;
            var receipt = new JsonObject
            {
                ["type"] = "l402_payment_receipt",
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["endpoint"] = endpoint,               // already redacted by the caller
                ["amountSats"] = amountSats,
                ["wallet"] = label,
                ["policy"] = policy,
                ["sessionSpentSats"] = sessionSpentSats.HasValue ? JsonValue.Create(sessionSpentSats.Value) : null,
                ["sessionRemainingUsd"] = sessionRemainingUsd.HasValue ? JsonValue.Create(sessionRemainingUsd.Value) : null,
                ["revokePath"] = RevokePaths.TryGetValue(label, out var rp) ? rp : RevokeDefault,
            };
            Append(receipt.ToJsonString());
        }
        catch (Exception ex)
        {
            // A receipt is an audit convenience — it must NEVER break a payment.
            Console.Error.WriteLine($"[Lightning Enable] Failed to write payment receipt: {ex.Message}");
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
            if (fi.Exists && fi.Length > MaxBytes)
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

    public List<JsonNode> ReadRecent(int limit)
    {
        var outp = new List<JsonNode>();
        try
        {
            if (!File.Exists(_path)) return outp;
            string[] lines;
            lock (_lock) { lines = File.ReadAllLines(_path); }
            var start = Math.Max(0, lines.Length - Math.Max(0, limit));
            for (var i = start; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0) continue;
                try
                {
                    var node = JsonNode.Parse(l);
                    if (node != null) outp.Add(node);
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
