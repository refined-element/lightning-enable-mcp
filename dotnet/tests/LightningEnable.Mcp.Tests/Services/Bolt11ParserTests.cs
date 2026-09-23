using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// The BOLT11 amount is what every money guard in the server enforces (maxSats, the session
/// reservation, the approval level, the LND fee limit, the ledger and receipts), while the wallet
/// pays whatever the raw invoice encodes. The two must agree, so the decoder must be exact.
///
/// Regression: the previous hand-rolled scanner never stopped at the bech32 separator '1', and
/// the first data character of every pre-2038 invoice is 'p' — so "lnbc11p…" (1 BTC) and
/// "lnbc1p…" (amountless) both decoded as 1 sat.
/// </summary>
public class Bolt11ParserTests
{
    // --- BOLT11 specification test vectors (verbatim; checksums and signatures are the spec's) ---

    private const string SpecAmountless =
        "lnbc1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq9qrsgq357wnc5r2ueh7ck6q93dj32dlqnls087fxdwk8qakdyafkq3yap9us6v52vjjsrvywa6rt52cm9r9zqt8r2t7mlcwspyetp5h2tztugp9lfyql";

    private const string Spec2500u =
        "lnbc2500u1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpu9qrsgquk0rl77nj30yxdy8j9vdx85fkpmdla2087ne0xh8nhedh8w27kyke0lp53ut353s06fv3qfegext0eh0ymjpf39tuven09sam30g4vgpfna3rh";

    private const string Spec20m =
        "lnbc20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqhp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqs9qrsgq7ea976txfraylvgzuxs8kgcw23ezlrszfnh8r6qtfpr6cxga50aj6txm9rxrydzd06dfeawfk6swupvz4erwnyutnjq7x39ymw6j38gp7ynn44";

    private const string SpecTestnet20m =
        "lntb20m1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygshp58yjmdan79s6qqdhdzgynm4zwqd5d7xmw5fk98klysy043l2ahrqspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqfpp3x9et2e20v6pu37c5d9vax37wxq72un989qrsgqdj545axuxtnfemtpwkc45hx9d2ft7x04mt8q7y6t0k2dge9e7h8kpy9p34ytyslj3yu569aalz2xdk8xkd7ltxqld94u8h2esmsmacgpghe9k8";

    private const string Spec25m =
        "lnbc25m1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5vdhkven9v5sxyetpdeessp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygs9q5sqqqqqqqqqqqqqqqqsgq2a25dxl5hrntdtn6zvydt7d66hyzsyhqs4wdynavys42xgl6sgx9c4g7me86a27t07mdtfry458rtjr0v92cnmswpsjscgt2vcse3sgpz3uapa";

    [Fact]
    public void SpecVector_Amountless_ReturnsNull()
    {
        Bolt11Parser.ExtractAmountSats(SpecAmountless).Should().BeNull(
            "an amountless invoice has no amount; callers refuse on null — it must never read as 1 sat");
    }

    [Fact]
    public void SpecVector_2500u_Is250000Sats()
    {
        Bolt11Parser.ExtractAmountSats(Spec2500u).Should().Be(250_000);
    }

    [Fact]
    public void SpecVector_20m_Is2000000Sats()
    {
        Bolt11Parser.ExtractAmountSats(Spec20m).Should().Be(2_000_000);
    }

    [Fact]
    public void SpecVector_25m_Is2500000Sats()
    {
        Bolt11Parser.ExtractAmountSats(Spec25m).Should().Be(2_500_000);
    }

    [Fact]
    public void SpecVector_Testnet20m_Is2000000Sats()
    {
        Bolt11Parser.ExtractAmountSats(SpecTestnet20m).Should().Be(2_000_000);
    }

    [Fact]
    public void SpecVector_UpperCase_IsAccepted()
    {
        // BOLT11 invoices are often upper-cased for QR codes; bech32 allows all-upper.
        Bolt11Parser.ExtractAmountSats(Spec2500u.ToUpperInvariant()).Should().Be(250_000);
    }

    // --- Whole-BTC amounts: the drain case ---

    [Fact]
    public void WholeBtc_1Btc_Is100000000Sats()
    {
        var invoice = TestInvoices.Build("lnbc1");
        invoice.Should().StartWith("lnbc11p", "amount '1', separator '1', data 'p…'");
        Bolt11Parser.ExtractAmountSats(invoice).Should().Be(100_000_000);
    }

    [Fact]
    public void WholeBtc_5Btc_Is500000000Sats()
    {
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build("lnbc5")).Should().Be(500_000_000);
    }

    [Fact]
    public void WholeBtc_10Btc_Is1000000000Sats()
    {
        var invoice = TestInvoices.Build("lnbc10");
        invoice.Should().StartWith("lnbc101p");
        Bolt11Parser.ExtractAmountSats(invoice).Should().Be(1_000_000_000);
    }

    [Fact]
    public void Constructed_Amountless_ReturnsNull()
    {
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build("lnbc")).Should().BeNull();
    }

    // --- Multipliers ---

    [Theory]
    [InlineData("lnbc1500n", 150L)]
    [InlineData("lnbc100n", 10L)]
    [InlineData("lnbc210n", 21L)]
    [InlineData("lnbc10u", 1_000L)]
    [InlineData("lnbc2500u", 250_000L)]
    [InlineData("lnbc25m", 2_500_000L)]
    [InlineData("lnbc20m", 2_000_000L)]
    [InlineData("lnbc10000p", 1L)]      // 10,000 pico = 1,000 msat = exactly 1 sat
    [InlineData("lnbc9678785340p", 967_879L)] // 967,878,534 msat → rounds up to 967,879 sats
    public void Multipliers_DecodeExactly(string hrp, long expectedSats)
    {
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build(hrp)).Should().Be(expectedSats);
    }

    [Fact]
    public void Pico_10p_IsOneMsat_RoundsUpToOneSat()
    {
        // 10 pico-BTC = 1 msat. Sub-satoshi invoices exist; the decoder rounds UP so a budget
        // check is never lower than what the wallet pays.
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build("lnbc10p")).Should().Be(1);
    }

    [Fact]
    public void Nano_SubSat_RoundsUp()
    {
        // 1n = 100 msat
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build("lnbc1n")).Should().Be(1);
        // 15n = 1,500 msat → 2 sats
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build("lnbc15n")).Should().Be(2);
    }

    [Fact]
    public void Pico_NotMultipleOfTen_ReturnsNull()
    {
        // BOLT11: a 'p' amount whose last decimal is not 0 is invalid (msat is the unit).
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build("lnbc11p")).Should().BeNull();
    }

    // --- Networks ---

    [Theory]
    [InlineData("lntb100n", 10L)]
    [InlineData("lnbcrt100n", 10L)]
    [InlineData("lntbs100n", 10L)]
    [InlineData("lnbcrt1", 100_000_000L)]
    [InlineData("lntbs1", 100_000_000L)]
    public void NetworkPrefixes_Decode(string hrp, long expectedSats)
    {
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build(hrp)).Should().Be(expectedSats);
    }

    [Theory]
    [InlineData("lnbcrt")]
    [InlineData("lntbs")]
    [InlineData("lntb")]
    public void NetworkPrefixes_Amountless_ReturnNull(string hrp)
    {
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build(hrp)).Should().BeNull();
    }

    [Theory]
    [InlineData("lnxx100n")]   // unknown network
    [InlineData("bc100n")]     // missing "ln"
    [InlineData("lnbc100x")]   // unknown multiplier
    [InlineData("lnbc0100n")]  // leading zero
    [InlineData("lnbc0")]      // zero amount
    [InlineData("lnbc0n")]     // zero amount with multiplier
    [InlineData("lnbcm")]      // multiplier without digits
    [InlineData("lnbc10mm")]   // doubled multiplier
    [InlineData("lnbc99999999999999999999999")] // absurd / overflow
    public void MalformedHrp_ReturnsNull(string hrp)
    {
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build(hrp)).Should().BeNull();
    }

    // --- Checksum and shape ---

    [Fact]
    public void TamperedCharacter_FailsChecksum_ReturnsNull()
    {
        Bolt11Parser.ExtractAmountSats(TestInvoices.Tamper(Spec2500u)).Should().BeNull();
        Bolt11Parser.ExtractAmountSats(TestInvoices.Tamper(TestInvoices.Build("lnbc1"))).Should().BeNull();
    }

    [Fact]
    public void TamperedAmount_FailsChecksum_ReturnsNull()
    {
        // Rewriting the HRP amount without re-signing/re-checksumming must not decode.
        Bolt11Parser.ExtractAmountSats("lnbc2600u" + Spec2500u.Substring("lnbc2500u".Length)).Should().BeNull();
    }

    [Fact]
    public void MixedCase_ReturnsNull()
    {
        var mixed = "LNBC" + Spec2500u.Substring(4);
        Bolt11Parser.ExtractAmountSats(mixed).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("lnbc")]
    [InlineData("lnbc1")]
    [InlineData("lnbc100n1pjtest")]
    [InlineData("lnbc1500n1pvjluezqqqqqq")]
    [InlineData("lnbc2500u1pvjluez!sp5")]
    public void Garbage_ReturnsNull(string? input)
    {
        Bolt11Parser.ExtractAmountSats(input!).Should().BeNull();
    }

    [Fact]
    public void DataPartTooShortForSignature_ReturnsNull()
    {
        // Valid checksum but no room for timestamp + 104-char signature.
        Bolt11Parser.ExtractAmountSats(TestInvoices.Build("lnbc100n", "pvjluez")).Should().BeNull();
    }
}
