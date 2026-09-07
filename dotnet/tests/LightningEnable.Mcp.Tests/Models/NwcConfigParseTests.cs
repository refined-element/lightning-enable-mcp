using LightningEnable.Mcp.Models;

namespace LightningEnable.Mcp.Tests.Models;

/// <summary>
/// Parsing rules for an NWC connection string, with the emphasis on the authority
/// segment NOT being a hostname.
///
/// <para>The 64-character value between <c>://</c> and <c>?</c> is a secp256k1 x-only
/// public key — a fixed-shape opaque identifier. <see cref="Uri"/> applies HOST rules to
/// it, and an all-digit pubkey (statistically rare but perfectly legal: ~1 in 10^11 keys,
/// and a wallet can mint many) parses as a malformed IPv4 literal and is rejected outright,
/// so such a wallet could never connect. Parsing splits the string by hand for exactly this
/// reason — the same fix the Lightning Enable API's NwcConnectionConfig carries.</para>
/// </summary>
public class NwcConfigParseTests
{
    private const string Relay = "wss://relay.example.com";
    private const string SecretFixture =
        "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static string Uri(string pubkey) =>
        $"nostr+walletconnect://{pubkey}?relay={Relay}&secret={SecretFixture}";

    [Fact]
    public void Parse_AllDigitWalletPubkey_IsNotTreatedAsAnIPv4Literal()
    {
        // 64 digits — a legal x-only pubkey, and the exact shape System.Uri mis-reads as a
        // malformed IPv4 host.
        const string allDigitPubkey =
            "1234567890123456789012345678901234567890123456789012345678901234";

        var config = NwcConfig.Parse(Uri(allDigitPubkey));

        config.WalletPubkey.Should().Be(allDigitPubkey);
        config.RelayUrl.Should().Be(Relay);
        config.Secret.Should().Be(SecretFixture);
    }

    [Fact]
    public void Parse_UppercaseHexPubkey_IsNormalizedToLowercase()
    {
        // Nostr pubkeys are compared as lowercase hex everywhere downstream (event tags,
        // filters, the ECDH peer key), so normalize once at the parse boundary.
        const string upper =
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        var config = NwcConfig.Parse(Uri(upper));

        config.WalletPubkey.Should().Be(upper.ToLowerInvariant());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]   // 63
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefa")] // 65
    [InlineData("zzzz456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]  // non-hex
    public void Parse_MalformedWalletPubkey_Throws(string pubkey)
    {
        var act = () => NwcConfig.Parse(Uri(pubkey));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ErrorMessage_NeverEchoesTheConnectionString()
    {
        // The string is a live wallet credential: a parse failure must be safe to log
        // (engineering standard #5), so no exception message may quote the input.
        var act = () => NwcConfig.Parse(
            $"nostr+walletconnect://not-a-pubkey?relay={Relay}&secret={SecretFixture}");

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().NotContain(SecretFixture);
    }

    [Fact]
    public void Parse_NwcScheme_IsAlsoAccepted()
    {
        var config = NwcConfig.Parse(
            $"nwc://0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            + $"?relay={Relay}&secret={SecretFixture}");

        config.RelayUrl.Should().Be(Relay);
    }
}
