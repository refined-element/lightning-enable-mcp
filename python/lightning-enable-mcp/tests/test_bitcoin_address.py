"""
Tests for mainnet Bitcoin address validation (PY-C2).

Real BIP173 / legacy mainnet addresses must be accepted; tampered, garbage,
and testnet addresses must be rejected before an irreversible on-chain send.
"""

import pytest

from lightning_enable_mcp.bitcoin_address import is_valid_mainnet


@pytest.mark.parametrize("addr", [
    "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa",                                # P2PKH (genesis)
    "3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy",                                # P2SH
    "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4",                        # bech32 P2WPKH
    "bc1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3qccfmv3",    # bech32 P2WSH
])
def test_accepts_real_mainnet_addresses(addr):
    assert is_valid_mainnet(addr) is True


@pytest.mark.parametrize("addr", [
    "",
    "   ",
    None,
    "not-an-address",
    "bc1qtest123",                                  # bad bech32 checksum (the stub the old tests used!)
    "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNX",           # tampered base58 checksum
    "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx",   # testnet — must reject on mainnet
    "0x1234567890abcdef1234567890abcdef12345678",   # Ethereum-style
])
def test_rejects_invalid_or_wrong_network(addr):
    assert is_valid_mainnet(addr) is False
