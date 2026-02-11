namespace LightningEnable.Mcp.Services;

/// <summary>
/// Shared utility for parsing BOLT11 Lightning invoice amounts.
/// </summary>
public static class Bolt11Parser
{
    /// <summary>
    /// Extracts the amount in satoshis from a BOLT11 invoice.
    /// Returns null if the invoice has no amount or is invalid.
    /// </summary>
    public static long? ExtractAmountSats(string bolt11)
    {
        if (string.IsNullOrWhiteSpace(bolt11))
            return null;

        var invoice = bolt11.ToLowerInvariant();

        // Find the network prefix
        var prefixEnd = 0;
        if (invoice.StartsWith("lnbcrt"))
            prefixEnd = 6;
        else if (invoice.StartsWith("lntbs"))
            prefixEnd = 5;
        else if (invoice.StartsWith("lnbc"))
            prefixEnd = 4;
        else if (invoice.StartsWith("lntb"))
            prefixEnd = 4;
        else
            return null;

        // Extract amount portion
        var amountChars = new List<char>();
        var multiplier = 1.0m;

        for (int i = prefixEnd; i < invoice.Length; i++)
        {
            var c = invoice[i];

            if (char.IsDigit(c))
            {
                amountChars.Add(c);
            }
            else if (c == 'm' || c == 'u' || c == 'n' || c == 'p')
            {
                multiplier = c switch
                {
                    'm' => 0.001m,
                    'u' => 0.000001m,
                    'n' => 0.000000001m,
                    'p' => 0.000000000001m,
                    _ => 1.0m
                };
                break;
            }
            else
            {
                break;
            }
        }

        if (amountChars.Count == 0)
            return null;

        if (!decimal.TryParse(new string(amountChars.ToArray()), out var amount))
            return null;

        if (amount <= 0)
            return null;

        var btcAmount = amount * multiplier;
        var sats = btcAmount * 100_000_000m;

        var result = (long)Math.Ceiling(sats);
        return result > 0 ? result : null;
    }
}
