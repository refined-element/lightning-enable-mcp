using System.Numerics;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// The single BOLT11 amount decoder for the server.
///
/// Every money guard (the per-payment maximum, the session reservation, the approval level, the
/// LND fee limit, the ledger and receipts) enforces the amount this returns, while the wallet pays
/// whatever the raw invoice encodes — so this MUST be an exact, strict decode. It verifies the
/// bech32 checksum, splits the human-readable part at the LAST '1' (the bech32 separator), and
/// parses the HRP amount with integer arithmetic only. Anything it cannot decode with certainty
/// returns null, and every caller treats null as "no amount → refuse".
///
/// History: the previous hand-rolled scanner kept reading digits past the separator and took the
/// first data character — 'p' in every pre-2038 invoice — as the pico multiplier, so a 1 BTC
/// invoice ("lnbc11p…") and an amountless one ("lnbc1p…") both decoded as 1 sat.
/// </summary>
public static class Bolt11Parser
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>Known network prefixes (after "ln"), longest first so "bcrt" wins over "bc".</summary>
    private static readonly string[] NetworkPrefixes = { "bcrt", "tbs", "tb", "bc", "sb" };

    /// <summary>7 chars of timestamp + 104 chars of signature (520 bits) + 6 chars of checksum.</summary>
    private const int MinDataLength = 7 + 104 + 6;

    /// <summary>21M BTC in msat — anything above this is not a real invoice.</summary>
    private static readonly BigInteger MaxMsat = new BigInteger(21_000_000) * 100_000_000 * 1000;

    /// <summary>
    /// Extracts the amount in satoshis from a BOLT11 invoice.
    /// Returns null if the invoice has no amount, fails the checksum, or is malformed in any way.
    /// A sub-satoshi amount is rounded UP (sub-sat invoices exist; rounding up keeps every budget
    /// check at or above what the wallet will actually pay).
    /// </summary>
    public static long? ExtractAmountSats(string bolt11)
    {
        var msat = ExtractAmountMsat(bolt11);
        if (msat is null)
            return null;

        var sats = (msat.Value + 999) / 1000; // ceil — see summary
        return sats > 0 ? sats : null;
    }

    /// <summary>
    /// Extracts the amount in millisatoshis from a BOLT11 invoice, or null when the invoice is
    /// amountless or cannot be decoded with a valid checksum.
    /// </summary>
    public static long? ExtractAmountMsat(string bolt11)
    {
        if (!TryDecodeHrp(bolt11, out var amountPart))
            return null;

        return ParseAmountMsat(amountPart);
    }

    /// <summary>
    /// Validates the bech32 envelope and returns the HRP amount part (possibly empty =
    /// amountless). False on any structural or checksum failure.
    /// </summary>
    private static bool TryDecodeHrp(string? bolt11, out string amountPart)
    {
        amountPart = string.Empty;

        if (string.IsNullOrWhiteSpace(bolt11))
            return false;

        var raw = bolt11.Trim();

        // bech32: all-lower or all-upper, never mixed.
        var hasLower = false;
        var hasUpper = false;
        foreach (var c in raw)
        {
            if (c < 33 || c > 126) return false;
            if (c >= 'a' && c <= 'z') hasLower = true;
            else if (c >= 'A' && c <= 'Z') hasUpper = true;
        }
        if (hasLower && hasUpper)
            return false;

        var invoice = raw.ToLowerInvariant();

        var sep = invoice.LastIndexOf('1');
        if (sep < 1)
            return false;

        var hrp = invoice.Substring(0, sep);
        var dataChars = invoice.Substring(sep + 1);
        if (dataChars.Length < MinDataLength)
            return false;

        var data = new int[dataChars.Length];
        for (var i = 0; i < dataChars.Length; i++)
        {
            var v = Charset.IndexOf(dataChars[i]);
            if (v < 0) return false;
            data[i] = v;
        }

        if (!VerifyChecksum(hrp, data))
            return false;

        if (!hrp.StartsWith("ln", StringComparison.Ordinal))
            return false;

        var afterLn = hrp.Substring(2);
        string? network = null;
        foreach (var prefix in NetworkPrefixes)
        {
            if (afterLn.StartsWith(prefix, StringComparison.Ordinal))
            {
                network = prefix;
                break;
            }
        }
        if (network is null)
            return false;

        amountPart = afterLn.Substring(network.Length);
        return true;
    }

    /// <summary>
    /// Parses the HRP amount ("" | digits [m|u|n|p]) into msat using integer arithmetic.
    /// Null for amountless, zero, leading-zero, malformed, non-msat-aligned pico, or absurd values.
    /// </summary>
    private static long? ParseAmountMsat(string amountPart)
    {
        if (amountPart.Length == 0)
            return null; // amountless — never default to anything

        var multiplier = amountPart[^1];
        string digits;
        if (multiplier is 'm' or 'u' or 'n' or 'p')
        {
            digits = amountPart.Substring(0, amountPart.Length - 1);
        }
        else
        {
            digits = amountPart;
            multiplier = '\0';
        }

        if (digits.Length == 0 || digits.Length > 25)
            return null;
        foreach (var c in digits)
        {
            if (c < '0' || c > '9') return null;
        }
        if (digits[0] == '0')
            return null; // leading zero, or zero amount

        var value = BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);

        // 1 BTC = 100,000,000 sat = 100,000,000,000 msat.
        BigInteger msat;
        switch (multiplier)
        {
            case '\0': msat = value * 100_000_000_000; break; // BTC
            case 'm': msat = value * 100_000_000; break;      // milli-BTC
            case 'u': msat = value * 100_000; break;          // micro-BTC
            case 'n': msat = value * 100; break;              // nano-BTC
            case 'p':                                         // pico-BTC = 0.1 msat
                if (!value.IsZero && value % 10 != 0)
                    return null; // BOLT11: pico amounts must be a whole number of msat
                msat = value / 10;
                break;
            default: return null;
        }

        if (msat <= 0 || msat > MaxMsat)
            return null;

        return (long)msat;
    }

    private static bool VerifyChecksum(string hrp, int[] data)
    {
        var values = new List<int>(hrp.Length * 2 + 1 + data.Length);
        foreach (var c in hrp) values.Add(c >> 5);
        values.Add(0);
        foreach (var c in hrp) values.Add(c & 31);
        values.AddRange(data);
        return Polymod(values) == 1; // bech32 constant (BOLT11 does not use bech32m)
    }

    private static uint Polymod(List<int> values)
    {
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ (uint)v;
            if ((top & 1) != 0) chk ^= 0x3b6a57b2;
            if ((top & 2) != 0) chk ^= 0x26508e6d;
            if ((top & 4) != 0) chk ^= 0x1ea119fa;
            if ((top & 8) != 0) chk ^= 0x3d4233dd;
            if ((top & 16) != 0) chk ^= 0x2a1462b3;
        }
        return chk;
    }
}
