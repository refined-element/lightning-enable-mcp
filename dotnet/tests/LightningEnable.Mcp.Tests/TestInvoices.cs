using System.Text;

namespace LightningEnable.Mcp.Tests;

/// <summary>
/// Builds real-shaped BOLT11 invoice strings with a VALID bech32 checksum for tests.
///
/// The production decoder (<c>Bolt11Parser</c>) verifies the bech32 checksum, so hand-typed
/// fakes such as <c>"lnbc100n1pjtest"</c> are (correctly) rejected as malformed. This helper is a
/// deliberately independent bech32 encoder — it does not call into the decoder — so a test that
/// round-trips through it proves the decoder against a second implementation, not against itself.
///
/// The signature in the default data part is NOT a valid signature over the invoice (nothing in
/// the MCP server verifies BOLT11 signatures — the wallet does), but the invoice is otherwise
/// well-formed: timestamp, payment secret, payment hash, description, 104-char signature.
/// </summary>
public static class TestInvoices
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>
    /// Data part (timestamp + tagged fields + signature, no checksum) of the BOLT11 spec
    /// "$3 for a cup of coffee" vector. Starts with 'p' like every pre-2038 invoice — which
    /// is exactly the character the old parser mistook for the pico multiplier.
    /// </summary>
    public const string DefaultData =
        "pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpu9qrsgquk0rl77nj30yxdy8j9vdx85fkpmdla2087ne0xh8nhedh8w27kyke0lp53ut353s06fv3qfegext0eh0ymjpf39tuven09sam30g4vgp";

    /// <summary>
    /// Builds <c>{hrp}1{data}{checksum}</c>. <paramref name="hrp"/> is the full human-readable
    /// part, e.g. <c>"lnbc100n"</c> (10 sats), <c>"lnbc1"</c> (1 BTC), <c>"lnbc"</c> (amountless).
    /// </summary>
    public static string Build(string hrp, string data = DefaultData)
    {
        hrp = hrp.ToLowerInvariant();
        var values = new int[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var v = Charset.IndexOf(data[i]);
            if (v < 0) throw new ArgumentException($"'{data[i]}' is not a bech32 character", nameof(data));
            values[i] = v;
        }

        var checksum = CreateChecksum(hrp, values);
        var sb = new StringBuilder(hrp.Length + 1 + data.Length + 6);
        sb.Append(hrp).Append('1').Append(data);
        foreach (var c in checksum) sb.Append(Charset[c]);
        return sb.ToString();
    }

    /// <summary>
    /// Returns <paramref name="invoice"/> with one data character (not in the HRP) replaced by a
    /// different valid bech32 character, so the checksum no longer holds.
    /// </summary>
    public static string Tamper(string invoice, int offsetFromEnd = 20)
    {
        var chars = invoice.ToCharArray();
        var idx = chars.Length - offsetFromEnd;
        chars[idx] = chars[idx] == 'q' ? 'p' : 'q';
        return new string(chars);
    }

    private static int[] CreateChecksum(string hrp, int[] data)
    {
        var values = new List<int>();
        foreach (var c in hrp) values.Add(c >> 5);
        values.Add(0);
        foreach (var c in hrp) values.Add(c & 31);
        values.AddRange(data);
        values.AddRange(new int[6]);
        var mod = Polymod(values) ^ 1; // bech32 (not bech32m) constant, as BOLT11 uses
        var result = new int[6];
        for (var i = 0; i < 6; i++) result[i] = (int)((mod >> (5 * (5 - i))) & 31);
        return result;
    }

    private static uint Polymod(IEnumerable<int> values)
    {
        uint[] gen = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ (uint)v;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) != 0) chk ^= gen[i];
        }
        return chk;
    }
}
