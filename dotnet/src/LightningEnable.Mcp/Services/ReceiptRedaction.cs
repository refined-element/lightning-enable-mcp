using System.Text.Json.Nodes;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Strips anything credential-shaped out of a receipt before it leaves the process.
///
/// <para>Receipts are written by <see cref="ReceiptService"/> and never contain a preimage,
/// macaroon, or wallet connection string by construction. This runs anyway, at the READ
/// boundary, because the log is a plain file on the operator's disk: a hand-edit, an
/// interleaved append from another tool, or a future writer that forgets could put one
/// there, and by then the receipt is on its way to a model's context.</para>
///
/// <para>Applied inside <see cref="ReceiptService.ReadRecent"/>, so the <c>receipts</c> tool
/// and the <c>lightning-enable://receipts</c> resource are covered by the same pass — there
/// is no second surface to keep in sync.</para>
///
/// <para>The value is REPLACED rather than the key dropped: a reader can see the field was
/// present and withheld, instead of silently getting a receipt that looks clean.</para>
/// </summary>
public static class ReceiptRedaction
{
    /// <summary>What a redacted value is replaced with. Matches the tools' error scrubber.</summary>
    public const string Placeholder = "[REDACTED]";

    /// <summary>
    /// Substrings that mark a property as credential-shaped, matched case-insensitively
    /// anywhere in the property name. <c>preimage</c> is the one that matters most: under
    /// L402 it is not a receipt number, it IS the proof of payment.
    /// </summary>
    private static readonly string[] SensitiveNameFragments =
    [
        "preimage", "secret", "macaroon", "connectionstring", "apikey", "privatekey",
        "password", "token",
    ];

    /// <summary>How deep to walk a receipt. Real receipts are flat; this bounds a pathological one.</summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// Redacts <paramref name="node"/> in place and returns it. Non-object nodes pass
    /// through untouched.
    /// </summary>
    public static JsonNode? Redact(JsonNode? node) => RedactAt(node, 0);

    private static JsonNode? RedactAt(JsonNode? node, int depth)
    {
        if (node is null || depth > MaxDepth)
        {
            return node;
        }

        switch (node)
        {
            // Both branches recurse WITHOUT reassigning: a JsonNode belongs to exactly one
            // parent, and putting the same instance back under the same key throws
            // "The node already has a parent." Only a redacted value is ever assigned.
            case JsonObject obj:
                // Materialize the names first: assigning to an indexer while enumerating
                // the object would invalidate the enumerator.
                foreach (var name in obj.Select(p => p.Key).ToArray())
                {
                    if (IsSensitive(name))
                    {
                        obj[name] = Placeholder;
                    }
                    else
                    {
                        RedactAt(obj[name], depth + 1);
                    }
                }
                break;

            case JsonArray array:
                foreach (var element in array)
                {
                    RedactAt(element, depth + 1);
                }
                break;
        }

        return node;
    }

    /// <summary>Whether a property name looks like it carries a credential.</summary>
    public static bool IsSensitive(string propertyName) =>
        SensitiveNameFragments.Any(fragment =>
            propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
