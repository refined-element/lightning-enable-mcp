using FluentAssertions;
using LightningEnable.Mcp.Services;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// C-2: on-chain sends must reject invalid / wrong-network Bitcoin addresses
/// before the (irreversible) send. Vectors are real BIP173/legacy mainnet
/// addresses (accept) vs. tampered/garbage/testnet (reject).
/// </summary>
public class BitcoinAddressValidatorTests
{
    [Theory]
    [InlineData("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa")]                               // P2PKH (genesis)
    [InlineData("3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy")]                               // P2SH
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4")]                       // bech32 P2WPKH
    [InlineData("bc1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3qccfmv3")]   // bech32 P2WSH
    public void IsValidMainnet_AcceptsRealMainnetAddresses(string addr)
    {
        BitcoinAddressValidator.IsValidMainnet(addr).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-an-address")]
    [InlineData("bc1qtest123")]                                  // bad bech32 checksum (a real value the Python tests used!)
    [InlineData("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNX")]           // tampered base58 checksum
    [InlineData("tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx")]   // testnet — must reject on mainnet
    [InlineData("0x1234567890abcdef1234567890abcdef12345678")]   // an Ethereum-style address
    public void IsValidMainnet_RejectsInvalidOrWrongNetwork(string? addr)
    {
        BitcoinAddressValidator.IsValidMainnet(addr).Should().BeFalse();
    }
}
