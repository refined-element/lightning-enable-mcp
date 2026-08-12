namespace LightningEnable.Mcp.Models;

/// <summary>
/// One durable payment receipt, written as a single <c>payment_receipt</c> line in
/// <c>~/.lightning-enable/receipts.jsonl</c> by the receipt seam
/// (<see cref="LightningEnable.Mcp.Services.ReceiptRecordingWalletService"/>).
///
/// NEVER carries secrets: no preimage, BOLT11 invoice, macaroon, or wallet
/// connection string. <see cref="PaymentHash"/> is the Lightning payment hash
/// (SHA256 of the preimage) — safe to persist, useless to spend.
/// </summary>
public sealed record PaymentReceiptEntry
{
    /// <summary>Payment shape: <c>invoice</c> | <c>l402</c> | <c>onchain</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Wallet provider label (NWC, Strike, LND, OpenNode).</summary>
    public required string Wallet { get; init; }

    /// <summary>Amount in satoshis (excluding on-chain network fee).</summary>
    public required long AmountSats { get; init; }

    /// <summary>
    /// <c>settled</c>, or <c>pending</c> for an in-flight payment whose funds are
    /// committed but which may still fail. Pending payments get a receipt because
    /// the budget counts them — the durable log must not under-report the budget.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>Lightning payment hash, derived as SHA256(preimage). Never the preimage itself.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>Optional caller-supplied context (redacted L402 endpoint, purpose, destination address).</summary>
    public string? Context { get; init; }

    /// <summary>Budget approval policy that allowed the payment (e.g. <c>auto_approve</c>).</summary>
    public string? Policy { get; init; }

    /// <summary>Post-payment session spend total in sats, when a budget service is available.</summary>
    public long? SessionSpentSats { get; init; }

    /// <summary>On-chain network fee in sats (onchain kind only).</summary>
    public long? FeeSats { get; init; }

    /// <summary>Bitcoin transaction id (onchain kind only; public chain data, not a secret).</summary>
    public string? TxId { get; init; }
}
