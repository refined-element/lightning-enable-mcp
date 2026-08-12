namespace LightningEnable.Mcp.Services;

/// <summary>
/// Ambient "payment intent" a tool may open before paying, read by
/// <see cref="ReceiptRecordingWalletService"/> to enrich the durable receipt
/// (kind, redacted endpoint/purpose, budget policy) — so the receipt keeps its
/// L402-era richness without any tool writing receipts itself.
///
/// Also carries the honest outcome signal back: after the payment,
/// <see cref="ReceiptWritten"/> tells the tool whether the receipt line landed
/// on disk, so a failed write surfaces as <c>receipt_written: false</c> in the
/// tool result instead of disappearing.
///
/// AsyncLocal-backed: flows into everything the tool awaits (including the
/// wallet call buried inside L402HttpClient), and parallel tool calls cannot
/// see each other's scopes.
/// </summary>
public sealed class PaymentReceiptScope : IDisposable
{
    private static readonly AsyncLocal<PaymentReceiptScope?> _current = new();

    private readonly PaymentReceiptScope? _prior;
    private int _attempted;
    private bool _anyFailed;

    private PaymentReceiptScope(string kind, string? context, string? policy)
    {
        Kind = kind;
        Context = context;
        Policy = policy;
        _prior = _current.Value;
        _current.Value = this;
    }

    /// <summary>The scope governing the current async flow, if any.</summary>
    public static PaymentReceiptScope? Current => _current.Value;

    /// <summary>
    /// Opens a scope. Dispose it when the tool call ends (a <c>using</c> at the
    /// top of the tool method) so the prior scope is restored.
    /// </summary>
    public static PaymentReceiptScope Begin(string kind, string? context = null, string? policy = null)
        => new(kind, context, policy);

    /// <summary>Receipt kind for Lightning payments under this scope: <c>invoice</c> | <c>l402</c>.</summary>
    public string Kind { get; set; }

    /// <summary>Redacted endpoint / purpose / destination. Must never contain a secret.</summary>
    public string? Context { get; set; }

    /// <summary>Budget policy label (set after the budget check, before paying).</summary>
    public string? Policy { get; set; }

    /// <summary>
    /// null — no payment observed under this scope (nothing to receipt);
    /// true — every observed payment produced a durable receipt line;
    /// false — at least one receipt write failed (report it, never hide it).
    /// </summary>
    public bool? ReceiptWritten => _attempted == 0 ? null : !_anyFailed;

    /// <summary>Called by the receipt seam after each write attempt.</summary>
    internal void RecordWrite(bool written)
    {
        _attempted++;
        if (!written) _anyFailed = true;
    }

    public void Dispose() => _current.Value = _prior;
}
