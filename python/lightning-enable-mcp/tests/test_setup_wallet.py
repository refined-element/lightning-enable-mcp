"""``setup_wallet`` — the first tool an agent calls, and the only one that writes a
wallet credential to disk.

The probe is stubbed: what is under test is the ORDER of the guards (parse, then
env-precedence, then live probe, then write) and what each one is allowed to say. A real
relay is covered by the NWC wallet tests.

Mirrors ``dotnet/tests/LightningEnable.Mcp.Tests/Tools/SetupWalletToolTests.cs``.
"""

import json
import sys

import pytest

from lightning_enable_mcp.config import ConfigurationService
from lightning_enable_mcp.tools.setup_wallet import (
    describe_wallet_state,
    parse_nwc_connection_string,
    save_nwc_connection_string,
    setup_wallet,
)

PUBKEY = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
SECRET_FIXTURE = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
RELAY = "wss://relay.example.com"
VALID_NWC = f"nostr+walletconnect://{PUBKEY}?relay={RELAY}&secret={SECRET_FIXTURE}"

WALLET_ENV_VARS = (
    "NWC_CONNECTION_STRING",
    "STRIKE_API_KEY",
    "OPENNODE_API_KEY",
    "LND_REST_HOST",
    "LND_MACAROON_HEX",
    "WALLET_PRIORITY",
)


@pytest.fixture(autouse=True)
def _no_ambient_wallet_env(monkeypatch):
    """A developer's own wallet env must never decide the outcome of these tests."""
    for name in WALLET_ENV_VARS:
        monkeypatch.delenv(name, raising=False)


@pytest.fixture
def config_service(tmp_path, monkeypatch) -> ConfigurationService:
    """A ConfigurationService rooted in tmp_path, so nothing touches the real home dir."""
    monkeypatch.setattr("pathlib.Path.home", lambda: tmp_path)
    return ConfigurationService()


def _probe_ok(methods=("pay_invoice", "get_balance"), balance=12_345, alias="test wallet"):
    async def probe(connection_string: str) -> dict:
        probe.called_with = connection_string  # type: ignore[attr-defined]
        return {
            "success": True,
            "error": None,
            "methods": list(methods),
            "balanceSats": balance,
            "alias": alias,
        }

    probe.called_with = None  # type: ignore[attr-defined]
    return probe


def _saved_nwc(config_service) -> str | None:
    """The connection string currently in the config file, if any.

    ConfigurationService writes a default config on first load, so "nothing was saved"
    means the wallets section has no nwcConnectionString — not that the file is absent.
    """
    path = config_service._config_file_path
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8")).get("wallets", {}).get(
        "nwcConnectionString"
    )


def _probe_fails(error="the wallet did not answer within 10 seconds"):
    async def probe(connection_string: str) -> dict:
        probe.called_with = connection_string  # type: ignore[attr-defined]
        return {"success": False, "error": error}

    probe.called_with = None  # type: ignore[attr-defined]
    return probe


# ── parse_nwc_connection_string ───────────────────────────────────────────────


class TestParseNwcConnectionString:
    def test_all_digit_wallet_pubkey_parses(self):
        """A 64-digit pubkey is legal, and is exactly what host-rule parsing mis-reads.

        A URL parser applying host rules to the authority treats it as a malformed IPv4
        literal and rejects it, so a wallet with such a key could never connect.
        """
        all_digits = "1" + "2" * 63
        parsed = parse_nwc_connection_string(
            f"nostr+walletconnect://{all_digits}?relay={RELAY}&secret={SECRET_FIXTURE}"
        )

        assert parsed.wallet_pubkey == all_digits
        assert parsed.relay_url == RELAY
        assert parsed.secret == SECRET_FIXTURE

    def test_uppercase_pubkey_is_normalized(self):
        parsed = parse_nwc_connection_string(
            f"nostr+walletconnect://{PUBKEY.upper()}?relay={RELAY}&secret={SECRET_FIXTURE}"
        )
        assert parsed.wallet_pubkey == PUBKEY

    def test_nwc_scheme_is_accepted(self):
        parsed = parse_nwc_connection_string(
            f"nwc://{PUBKEY}?relay={RELAY}&secret={SECRET_FIXTURE}"
        )
        assert parsed.relay_url == RELAY

    def test_every_advertised_relay_is_kept_in_order(self):
        """An Alby-style string advertises two relays for redundancy — keep both."""
        parsed = parse_nwc_connection_string(
            f"nostr+walletconnect://{PUBKEY}"
            f"?relay=wss://relay.getalby.com&relay=wss://relay2.getalby.com"
            f"&secret={SECRET_FIXTURE}"
        )
        assert parsed.relays == ("wss://relay.getalby.com", "wss://relay2.getalby.com")
        assert parsed.relay_url == "wss://relay.getalby.com"

    @pytest.mark.parametrize(
        "connection_string,why",
        [
            ("", "empty"),
            ("https://example.com", "wrong scheme"),
            (f"nostr+walletconnect://short?relay={RELAY}&secret={SECRET_FIXTURE}", "short pubkey"),
            (
                f"nostr+walletconnect://{'z' * 64}?relay={RELAY}&secret={SECRET_FIXTURE}",
                "non-hex pubkey",
            ),
            (f"nostr+walletconnect://{PUBKEY}?secret={SECRET_FIXTURE}", "no relay"),
            (
                f"nostr+walletconnect://{PUBKEY}?relay=relay.example.com&secret={SECRET_FIXTURE}",
                "relay has no ws/wss scheme",
            ),
            (f"nostr+walletconnect://{PUBKEY}?relay={RELAY}", "no secret"),
            (f"nostr+walletconnect://{PUBKEY}?relay={RELAY}&secret=abc", "short secret"),
        ],
    )
    def test_malformed_strings_raise(self, connection_string, why):
        with pytest.raises(ValueError):
            parse_nwc_connection_string(connection_string)

    def test_error_never_echoes_the_credential(self):
        """The string is a live credential: a parse failure must be safe to log."""
        with pytest.raises(ValueError) as excinfo:
            parse_nwc_connection_string(
                f"nostr+walletconnect://not-a-pubkey?relay={RELAY}&secret={SECRET_FIXTURE}"
            )
        assert SECRET_FIXTURE not in str(excinfo.value)


# ── describe_wallet_state ─────────────────────────────────────────────────────


class TestDescribeWalletState:
    def test_no_wallet_anywhere(self, config_service):
        state = describe_wallet_state(config_service)
        assert state["configured"] is False
        assert state["provider"] is None
        assert state["configuredProviders"] == []

    def test_env_var_is_reported_as_the_source(self, config_service, monkeypatch):
        monkeypatch.setenv("NWC_CONNECTION_STRING", VALID_NWC)
        state = describe_wallet_state(config_service)
        assert state["provider"] == "NWC"
        assert state["fromEnvironment"] is True
        assert "NWC_CONNECTION_STRING" in state["source"]

    def test_unexpanded_env_placeholder_does_not_count(self, config_service, monkeypatch):
        # A client config that never substituted its ${...} template must not read as a
        # configured wallet.
        monkeypatch.setenv("STRIKE_API_KEY", "${STRIKE_API_KEY}")
        assert describe_wallet_state(config_service)["configured"] is False

    def test_lnd_needs_both_halves(self, config_service, monkeypatch):
        monkeypatch.setenv("LND_REST_HOST", "localhost:8080")
        assert describe_wallet_state(config_service)["configured"] is False

        monkeypatch.setenv("LND_MACAROON_HEX", "0a0b0c")
        state = describe_wallet_state(config_service)
        assert state["provider"] == "LND"

    def test_default_priority_is_l402_first(self, config_service, monkeypatch):
        monkeypatch.setenv("STRIKE_API_KEY", "fixture-strike-value")
        monkeypatch.setenv("OPENNODE_API_KEY", "fixture-opennode-value")
        state = describe_wallet_state(config_service)
        assert state["provider"] == "Strike"
        assert state["configuredProviders"] == ["Strike", "OpenNode"]

    def test_wallet_priority_override_wins(self, config_service, monkeypatch):
        monkeypatch.setenv("STRIKE_API_KEY", "fixture-strike-value")
        monkeypatch.setenv("OPENNODE_API_KEY", "fixture-opennode-value")
        monkeypatch.setenv("WALLET_PRIORITY", "opennode")
        assert describe_wallet_state(config_service)["provider"] == "OpenNode"

    def test_config_file_is_reported_as_the_source(self, config_service):
        save_nwc_connection_string(config_service.config_file_path, VALID_NWC)
        config_service.reload()

        state = describe_wallet_state(config_service)
        assert state["provider"] == "NWC"
        assert state["fromEnvironment"] is False
        assert "config file" in state["source"]


# ── save_nwc_connection_string ────────────────────────────────────────────────


class TestSaveNwcConnectionString:
    def test_creates_the_file_and_the_wallets_section(self, tmp_path):
        path = tmp_path / "nested" / "config.json"
        save_nwc_connection_string(path, VALID_NWC)

        data = json.loads(path.read_text(encoding="utf-8"))
        assert data["wallets"]["nwcConnectionString"] == VALID_NWC

    def test_preserves_every_other_key(self, tmp_path):
        """The file is the operator's: limits and unknown keys must survive."""
        path = tmp_path / "config.json"
        path.write_text(
            json.dumps({
                "currency": "USD",
                "limits": {"maxPerPayment": 7.5, "maxPerSession": 25},
                "somethingThisBuildDoesNotKnow": {"keep": True},
                "wallets": {"strikeApiKey": "fixture-strike-value"},
            }),
            encoding="utf-8",
        )

        save_nwc_connection_string(path, VALID_NWC)

        data = json.loads(path.read_text(encoding="utf-8"))
        assert data["limits"] == {"maxPerPayment": 7.5, "maxPerSession": 25}
        assert data["somethingThisBuildDoesNotKnow"] == {"keep": True}
        assert data["wallets"]["strikeApiKey"] == "fixture-strike-value"
        assert data["wallets"]["nwcConnectionString"] == VALID_NWC

    def test_unparseable_config_refuses_rather_than_overwriting(self, tmp_path):
        path = tmp_path / "config.json"
        path.write_text("{ this is not json", encoding="utf-8")

        with pytest.raises(ValueError, match="not valid JSON"):
            save_nwc_connection_string(path, VALID_NWC)

        assert path.read_text(encoding="utf-8") == "{ this is not json"

    def test_restricts_permissions(self, tmp_path, monkeypatch):
        calls = []
        monkeypatch.setattr(
            "lightning_enable_mcp.config._restrict_file_permissions",
            lambda p: calls.append(p),
        )
        path = tmp_path / "config.json"

        save_nwc_connection_string(path, VALID_NWC)

        assert calls == [path], "the file now holds a live wallet credential in plaintext"


# ── setup_wallet ──────────────────────────────────────────────────────────────


class TestSetupWalletReport:
    @pytest.mark.asyncio
    async def test_no_arguments_no_wallet_returns_a_guided_path(self, config_service):
        result = json.loads(await setup_wallet(config_service=config_service))

        assert result["success"] is True
        assert result["configured"] is False
        assert result["configFile"] == config_service.config_file_path

        wallets = [o["wallet"] for o in result["options"]]
        assert any("NWC" in w for w in wallets)
        assert any("LND" in w for w in wallets)
        assert any("Strike" in w for w in wallets)
        assert "nwcConnectionString" in result["configFileShape"]

    @pytest.mark.asyncio
    async def test_no_arguments_configured_names_provider_and_source_only(
        self, config_service, monkeypatch
    ):
        monkeypatch.setenv("NWC_CONNECTION_STRING", VALID_NWC)

        raw = await setup_wallet(config_service=config_service)
        result = json.loads(raw)

        assert result["configured"] is True
        assert result["wallet"] == "NWC"
        assert "NWC_CONNECTION_STRING" in result["source"]
        assert SECRET_FIXTURE not in raw, "the credential is never reported"

    @pytest.mark.asyncio
    @pytest.mark.parametrize("blank", ["", "   "])
    async def test_blank_string_is_a_report_request(self, config_service, blank):
        result = json.loads(await setup_wallet(blank, config_service=config_service))
        assert result["success"] is True
        assert "options" in result


class TestSetupWalletConnect:
    @pytest.mark.asyncio
    async def test_valid_string_is_probed_then_saved(self, config_service):
        probe = _probe_ok()

        raw = await setup_wallet(VALID_NWC, config_service=config_service, probe=probe)
        result = json.loads(raw)

        assert result["success"] is True
        assert result["saved"] is True
        assert probe.called_with == VALID_NWC
        assert result["balanceSats"] == 12_345
        assert result["canPayL402"] is True
        assert "pay_invoice" in result["methods"]
        assert result["relay"] == RELAY
        assert "test_l402_payment" in result["nextStep"]
        assert SECRET_FIXTURE not in raw, "the saved credential is never echoed back"

        written = json.loads(
            (config_service._config_file_path).read_text(encoding="utf-8")
        )
        assert written["wallets"]["nwcConnectionString"] == VALID_NWC

    @pytest.mark.asyncio
    async def test_wallet_that_cannot_pay_is_reported_honestly(self, config_service):
        probe = _probe_ok(methods=("get_balance", "make_invoice"), balance=0, alias=None)

        result = json.loads(
            await setup_wallet(VALID_NWC, config_service=config_service, probe=probe)
        )

        assert result["success"] is True
        assert result["canPayL402"] is False
        assert "pay_invoice" in result["note"]

    @pytest.mark.asyncio
    async def test_env_var_wins_so_nothing_is_written(self, config_service, monkeypatch):
        monkeypatch.setenv("STRIKE_API_KEY", "fixture-strike-value")
        probe = _probe_ok()

        result = json.loads(
            await setup_wallet(VALID_NWC, config_service=config_service, probe=probe)
        )

        assert result["success"] is False
        assert result["saved"] is False
        assert "STRIKE_API_KEY" in result["error"]
        assert "precedence" in result["error"]
        assert "Unset" in result["howToProceed"]
        assert probe.called_with is None, "a refused setup must not open a relay connection"
        assert _saved_nwc(config_service) is None

    @pytest.mark.asyncio
    async def test_config_file_wallet_is_overwritten(self, config_service):
        # Only the ENVIRONMENT blocks a write. Replacing the string in the config file is
        # exactly what re-running setup_wallet is for.
        save_nwc_connection_string(config_service.config_file_path, "nwc://old")
        config_service.reload()

        replacement = VALID_NWC.replace("relay.example.com", "relay2.example.com")
        result = json.loads(
            await setup_wallet(replacement, config_service=config_service, probe=_probe_ok())
        )

        assert result["success"] is True
        written = json.loads(
            config_service._config_file_path.read_text(encoding="utf-8")
        )
        assert written["wallets"]["nwcConnectionString"] == replacement

    @pytest.mark.asyncio
    async def test_invalid_string_never_probes_or_saves(self, config_service):
        probe = _probe_ok()

        raw = await setup_wallet(
            "nostr+walletconnect://tooshort?relay=wss://r.example.com&secret=abc",
            config_service=config_service,
            probe=probe,
        )
        result = json.loads(raw)

        assert result["success"] is False
        assert result["error"]
        assert "nostr+walletconnect://" in result["expectedShape"]
        assert probe.called_with is None, "a malformed string must never reach a relay"
        assert _saved_nwc(config_service) is None

    @pytest.mark.asyncio
    async def test_probe_failure_saves_nothing(self, config_service):
        result = json.loads(
            await setup_wallet(
                VALID_NWC, config_service=config_service, probe=_probe_fails()
            )
        )

        assert result["success"] is False
        assert result["saved"] is False
        assert "did not answer" in result["error"]
        assert "Nothing was saved" in result["hint"]
        assert _saved_nwc(config_service) is None, (
            "a credential that cannot reach its wallet must not be persisted"
        )

    @pytest.mark.asyncio
    async def test_probe_that_raises_is_reported_not_propagated(self, config_service):
        async def exploding_probe(_: str) -> dict:
            raise RuntimeError("relay handshake blew up")

        result = json.loads(
            await setup_wallet(
                VALID_NWC, config_service=config_service, probe=exploding_probe
            )
        )

        assert result["success"] is False
        assert "relay handshake blew up" in result["error"]

    @pytest.mark.asyncio
    async def test_save_failure_reports_the_manual_config_shape(
        self, config_service, monkeypatch
    ):
        # tools/__init__ re-exports the HANDLER under the module's own name, so
        # `lightning_enable_mcp.tools.setup_wallet` resolves to the function. Patch the
        # module object itself.
        module = sys.modules["lightning_enable_mcp.tools.setup_wallet"]

        def _explode(*_args, **_kwargs):
            raise OSError("config.json is read-only")

        monkeypatch.setattr(module, "save_nwc_connection_string", _explode)

        result = json.loads(
            await setup_wallet(VALID_NWC, config_service=config_service, probe=_probe_ok())
        )

        assert result["success"] is False
        assert "read-only" in result["error"]
        assert "nwcConnectionString" in result["configFileShape"]
