using NBitcoin;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Validates Bitcoin addresses for on-chain sends (C-2). Uses NBitcoin's
/// battle-tested parser instead of hand-rolled bech32/base58 so an invalid or
/// wrong-network address is rejected BEFORE an irreversible on-chain send.
/// </summary>
public static class BitcoinAddressValidator
{
    /// <summary>
    /// True only if <paramref name="address"/> is a valid <b>mainnet</b> Bitcoin
    /// address (P2PKH / P2SH / bech32 / bech32m). Testnet, regtest, and garbage
    /// all return false — on-chain sends move real funds irreversibly, so anything
    /// not provably a mainnet address is rejected.
    /// </summary>
    public static bool IsValidMainnet(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        try
        {
            BitcoinAddress.Create(address.Trim(), Network.Main);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
