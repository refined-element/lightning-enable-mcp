"""
Tests for NWC Wallet
"""

import pytest
from lightning_enable_mcp.nwc_wallet import NWCConfig, NWCWallet, NWCError


class TestNWCConfig:
    """Tests for NWC configuration parsing."""

    def test_parse_valid_uri(self):
        """Test parsing a valid NWC URI."""
        uri = (
            "nostr+walletconnect://b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
            "?relay=wss://relay.getalby.com/v1"
            "&secret=71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

        config = NWCConfig.from_uri(uri)

        assert config.wallet_pubkey == (
            "b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
        )
        assert config.relay_url == "wss://relay.getalby.com/v1"
        assert config.secret == (
            "71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

    def test_parse_invalid_scheme(self):
        """Test parsing URI with invalid scheme raises error."""
        uri = "http://example.com"

        with pytest.raises(ValueError, match="Invalid NWC URI"):
            NWCConfig.from_uri(uri)

    def test_parse_missing_pubkey(self):
        """Test parsing URI without pubkey raises error."""
        uri = "nostr+walletconnect://?relay=wss://relay.example.com&secret=abc"

        with pytest.raises(ValueError, match="missing wallet pubkey"):
            NWCConfig.from_uri(uri)

    def test_parse_missing_relay(self):
        """Test parsing URI without relay raises error."""
        uri = (
            "nostr+walletconnect://abc123"
            "?secret=71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

        with pytest.raises(ValueError, match="missing relay"):
            NWCConfig.from_uri(uri)

    def test_parse_missing_secret(self):
        """Test parsing URI without secret raises error."""
        uri = "nostr+walletconnect://abc123?relay=wss://relay.example.com"

        with pytest.raises(ValueError, match="missing secret"):
            NWCConfig.from_uri(uri)


class TestNWCWallet:
    """Tests for NWCWallet."""

    def test_init_parses_uri(self):
        """Test wallet initializes from URI."""
        uri = (
            "nostr+walletconnect://b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
            "?relay=wss://relay.getalby.com/v1"
            "&secret=71a8c14c1407c113601079c4302dab36460f0ccd0ad506f1f2dc73b5100e4f3c"
        )

        wallet = NWCWallet(uri)

        assert wallet.config.wallet_pubkey == (
            "b889ff5b1513b641e2a139f661a661364979c5beee91842f8f0ef42ab558e9d4"
        )
        assert wallet.config.relay_url == "wss://relay.getalby.com/v1"

    def test_init_invalid_uri(self):
        """Test wallet raises on invalid URI."""
        with pytest.raises(ValueError):
            NWCWallet("invalid-uri")
