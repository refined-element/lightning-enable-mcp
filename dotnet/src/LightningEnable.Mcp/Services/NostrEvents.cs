using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NBitcoin.Secp256k1;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Nostr event helpers shared by every Nostr-speaking component (NIP-01): the canonical
/// serialisation, the event id, BIP-340 signing, signature verification and the NIP-19
/// bech32 key encodings. One home so the NWC wallet and the signed-attestation code cannot
/// drift apart. Mirrors the Python <c>nostr_event</c> module; the two ports are pinned to
/// the same bytes by <c>shared/attestation-golden-vector.json</c>.
///
/// Canonical serialisation: <c>[0, pubkey, created_at, kind, tags, content]</c> as compact
/// JSON in UTF-8, with <c>"</c>, <c>\</c>, <c>\b</c>, <c>\f</c>, <c>\n</c>, <c>\r</c> and
/// <c>\t</c> escaped by their short forms, any other control character below U+0020 as a
/// lowercase <c>\u00xx</c> escape, and every other character written raw (no escaping of
/// non-ASCII, emoji, <c>+</c>, <c>&lt;</c> and so on). That is what Python's
/// <c>json.dumps(ensure_ascii=False)</c> and JavaScript's <c>JSON.stringify</c> emit.
/// </summary>
public static class NostrEvents
{
    /// <summary>secp256k1 group order n, big-endian. A valid secret key is 1 &lt;= k &lt; n.</summary>
    private static readonly byte[] CurveOrder = Convert.FromHexString(
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");

    /// <summary>The canonical NIP-01 serialisation that the event id is the SHA-256 of.</summary>
    public static string Serialize(string pubkey, long createdAt, int kind, JsonArray tags, string content)
    {
        var sb = new StringBuilder(256);
        sb.Append("[0,");
        WriteString(sb, pubkey);
        sb.Append(',').Append(createdAt.ToString(CultureInfo.InvariantCulture));
        sb.Append(',').Append(kind.ToString(CultureInfo.InvariantCulture));
        sb.Append(',');
        WriteNode(sb, tags);
        sb.Append(',');
        WriteString(sb, content);
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Lowercase hex event id: SHA-256 of the canonical serialisation.</summary>
    public static string ComputeId(string pubkey, long createdAt, int kind, JsonArray tags, string content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(pubkey, createdAt, kind, tags, content)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// BIP-340 signature over the raw 32-byte event id using libsecp256k1's default nonce
    /// derivation — exactly the call the NWC wallet has always made.
    /// </summary>
    public static string SignId(ECPrivKey key, string eventIdHex)
    {
        key.TrySignBIP340(Convert.FromHexString(eventIdHex), null, out var sig);
        return sig != null ? Convert.ToHexString(sig.ToBytes()).ToLowerInvariant() : "";
    }

    /// <summary>
    /// BIP-340 signature with explicit 32-byte auxiliary randomness. Fresh random bytes are
    /// what BIP-340 recommends; fixed bytes make the signature deterministic (golden vectors).
    /// </summary>
    public static string SignId(ECPrivKey key, string eventIdHex, ReadOnlySpan<byte> auxRand32)
    {
        if (auxRand32.Length != 32)
            throw new ArgumentException("Auxiliary randomness must be 32 bytes.", nameof(auxRand32));
        var sig = key.SignBIP340(Convert.FromHexString(eventIdHex), auxRand32.ToArray());
        return Convert.ToHexString(sig.ToBytes()).ToLowerInvariant();
    }

    /// <summary>Signs with 32 bytes of fresh auxiliary randomness from the OS CSPRNG.</summary>
    public static string SignIdWithFreshAux(ECPrivKey key, string eventIdHex)
        => SignId(key, eventIdHex, RandomNumberGenerator.GetBytes(32));

    /// <summary>The 32-byte x-only public key for <paramref name="key"/>, as lowercase hex.</summary>
    public static string XOnlyPubkeyHex(ECPrivKey key)
        => Convert.ToHexString(key.CreateXOnlyPubKey().ToBytes()).ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="ev"/> has a correct id AND a valid BIP-340 signature under its
    /// <c>pubkey</c>. Recomputes the id from the canonical serialisation, so tampering with any
    /// signed field fails. Returns false on any malformed input instead of throwing.
    /// </summary>
    public static bool Verify(JsonObject ev)
    {
        try
        {
            var idHex = ev["id"]?.GetValue<string>();
            var pubkeyHex = ev["pubkey"]?.GetValue<string>();
            var sigHex = ev["sig"]?.GetValue<string>();
            var createdAt = ev["created_at"]?.GetValue<long>();
            var kind = ev["kind"]?.GetValue<int>();
            var tags = ev["tags"]?.AsArray();
            var content = ev["content"]?.GetValue<string>();

            if (idHex == null || pubkeyHex == null || sigHex == null
                || createdAt == null || kind == null || tags == null || content == null)
                return false;
            if (idHex.Length != 64 || pubkeyHex.Length != 64 || sigHex.Length != 128)
                return false;

            var recomputedId = ComputeId(pubkeyHex, createdAt.Value, kind.Value, tags, content);
            if (!string.Equals(recomputedId, idHex, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!ECXOnlyPubKey.TryCreate(Convert.FromHexString(pubkeyHex), out var pubkey) || pubkey == null)
                return false;
            if (!SecpSchnorrSignature.TryCreate(Convert.FromHexString(sigHex), out var sig) || sig == null)
                return false;

            // The message is the raw 32-byte id, not re-hashed.
            return pubkey.SigVerifyBIP340(sig, Convert.FromHexString(idHex));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="value"/> is exactly <paramref name="length"/> hex characters.</summary>
    public static bool IsHex(string? value, int length)
        => value != null && value.Length == length && value.All(Uri.IsHexDigit);

    /// <summary>NIP-19 <c>npub1...</c> form of a 32-byte x-only pubkey.</summary>
    public static string NpubEncode(string pubkeyHex) => Bech32.Encode("npub", Convert.FromHexString(pubkeyHex));

    /// <summary>
    /// A secret key from 64 hex characters or a NIP-19 <c>nsec1...</c> string. Returns false
    /// (never echoing the value) when it is neither or is not a valid secp256k1 scalar.
    /// </summary>
    public static bool TryParseSecretKey(string? text, out ECPrivKey? key)
    {
        key = null;
        var value = (text ?? string.Empty).Trim();
        byte[]? raw = null;
        if (value.StartsWith("nsec1", StringComparison.OrdinalIgnoreCase))
        {
            if (!Bech32.TryDecode(value.ToLowerInvariant(), "nsec", out raw) || raw.Length != 32)
                return false;
        }
        else if (IsHex(value, 64))
        {
            raw = Convert.FromHexString(value);
        }
        if (raw == null || !IsValidScalar(raw))
            return false;
        return ECPrivKey.TryCreate(raw, out key) && key != null;
    }

    private static bool IsValidScalar(byte[] raw)
    {
        if (raw.All(b => b == 0)) return false;
        return raw.AsSpan().SequenceCompareTo(CurveOrder) < 0;
    }

    // ── canonical JSON writer ───────────────────────────────────────────────

    private static void WriteNode(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteNode(sb, arr[i]);
                }
                sb.Append(']');
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in obj)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, k);
                    sb.Append(':');
                    WriteNode(sb, v);
                }
                sb.Append('}');
                break;
            case JsonValue val:
                if (val.TryGetValue<string>(out var s))
                    WriteString(sb, s);
                else
                    sb.Append(val.ToJsonString()); // numbers / booleans as their JSON text
                break;
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Minimal BIP-173 bech32 for NIP-19 keys (32-byte payloads).</summary>
    private static class Bech32
    {
        private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
        private static readonly uint[] Generator = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };

        public static string Encode(string hrp, byte[] data)
        {
            var words = ConvertBits(data, 8, 5, pad: true)!;
            var checksum = CreateChecksum(hrp, words);
            var sb = new StringBuilder(hrp.Length + 1 + words.Length + 6);
            sb.Append(hrp).Append('1');
            foreach (var w in words.Concat(checksum)) sb.Append(Charset[w]);
            return sb.ToString();
        }

        public static bool TryDecode(string text, string expectedHrp, out byte[] data)
        {
            data = Array.Empty<byte>();
            var sep = text.LastIndexOf('1');
            if (sep < 1 || sep + 7 > text.Length) return false;
            if (text[..sep] != expectedHrp) return false;
            var words = new byte[text.Length - sep - 1];
            for (var i = 0; i < words.Length; i++)
            {
                var idx = Charset.IndexOf(text[sep + 1 + i]);
                if (idx < 0) return false;
                words[i] = (byte)idx;
            }
            if (Polymod(HrpExpand(expectedHrp).Concat(words).ToArray()) != 1) return false;
            var decoded = ConvertBits(words[..^6], 5, 8, pad: false);
            if (decoded == null) return false;
            data = decoded;
            return true;
        }

        private static byte[] CreateChecksum(string hrp, byte[] words)
        {
            var values = HrpExpand(hrp).Concat(words).Concat(new byte[6]).ToArray();
            var mod = Polymod(values) ^ 1;
            var result = new byte[6];
            for (var i = 0; i < 6; i++) result[i] = (byte)((mod >> (5 * (5 - i))) & 31);
            return result;
        }

        private static byte[] HrpExpand(string hrp)
        {
            var result = new byte[hrp.Length * 2 + 1];
            for (var i = 0; i < hrp.Length; i++)
            {
                result[i] = (byte)(hrp[i] >> 5);
                result[i + hrp.Length + 1] = (byte)(hrp[i] & 31);
            }
            return result;
        }

        private static uint Polymod(byte[] values)
        {
            uint chk = 1;
            foreach (var v in values)
            {
                var top = chk >> 25;
                chk = ((chk & 0x1ffffff) << 5) ^ v;
                for (var i = 0; i < 5; i++)
                    if (((top >> i) & 1) != 0) chk ^= Generator[i];
            }
            return chk;
        }

        private static byte[]? ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
        {
            var acc = 0;
            var bits = 0;
            var maxv = (1 << toBits) - 1;
            var result = new List<byte>();
            foreach (var value in data)
            {
                if ((value >> fromBits) != 0) return null;
                acc = (acc << fromBits) | value;
                bits += fromBits;
                while (bits >= toBits)
                {
                    bits -= toBits;
                    result.Add((byte)((acc >> bits) & maxv));
                }
            }
            if (pad)
            {
                if (bits > 0) result.Add((byte)((acc << (toBits - bits)) & maxv));
            }
            else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
            {
                return null;
            }
            return result.ToArray();
        }
    }
}
