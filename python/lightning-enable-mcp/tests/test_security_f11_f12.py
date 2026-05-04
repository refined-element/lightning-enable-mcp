"""
Regression tests for security audit findings F-11 and F-12.

F-11: NWC kind-23195 response events must be sig-verified before their
content is trusted. The encryption layer authenticates as well, but the
explicit signature check catches malformed/relay-forged events earlier and
guards against future spec extensions that decouple encryption from
identity.

F-12: ~/.lightning-enable/config.json holds wallet credentials in plaintext.
Default OS permissions can leave it world-readable on shared machines.
"""

from __future__ import annotations

import asyncio
import os
import stat
import urllib.parse
from pathlib import Path
from unittest.mock import patch

import pytest

from lightning_enable_mcp.config import (
    ConfigurationService,
    _restrict_file_permissions,
)
from lightning_enable_mcp.nwc_wallet import (
    NWCWallet,
    _verify_nostr_event_signature,
)


# ─── F-11 tests ───────────────────────────────────────────────────────────

class TestF11NwcEventSignatureVerification:
    """The verifier helper is the gate the response loop now passes events
    through. Confirms it rejects events the response loop must drop."""

    def test_returns_false_on_empty_event(self):
        assert _verify_nostr_event_signature({}) is False

    def test_returns_false_on_missing_id(self):
        assert _verify_nostr_event_signature({
            "pubkey": "a" * 64,
            "sig": "b" * 128,
            "kind": 23195,
            "created_at": 0,
            "tags": [],
            "content": "",
        }) is False

    def test_returns_false_on_missing_pubkey(self):
        assert _verify_nostr_event_signature({
            "id": "a" * 64,
            "sig": "b" * 128,
            "kind": 23195,
            "created_at": 0,
            "tags": [],
            "content": "",
        }) is False

    def test_returns_false_on_missing_sig(self):
        assert _verify_nostr_event_signature({
            "id": "a" * 64,
            "pubkey": "b" * 64,
            "kind": 23195,
            "created_at": 0,
            "tags": [],
            "content": "",
        }) is False

    def test_returns_false_on_wrong_field_lengths(self):
        # id != 64 hex
        assert _verify_nostr_event_signature({
            "id": "abc",
            "pubkey": "a" * 64,
            "sig": "b" * 128,
            "kind": 23195,
            "created_at": 0,
            "tags": [],
            "content": "",
        }) is False

    def test_returns_false_on_random_id(self):
        # All fields well-formed length but id is arbitrary; recomputed id
        # won't match → reject.
        assert _verify_nostr_event_signature({
            "id": "0" * 64,
            "pubkey": "1" * 64,
            "sig": "2" * 128,
            "kind": 23195,
            "created_at": 1700000000,
            "tags": [["e", "x" * 64]],
            "content": "ciphertext",
        }) is False


# ─── F-12 tests ───────────────────────────────────────────────────────────

# ─── F-11 gate tests (added in PR #21 round-2) ────────────────────────────

class TestF11ProcessMessageRejectsForgedEvents:
    """End-to-end gate test for F-11: kind-23195 events whose pubkey doesn't
    match the configured wallet OR whose BIP340 sig is invalid must NOT
    resolve a pending request future. Helper-level coverage above is
    necessary but not sufficient — Copilot review on PR #21 noted there's
    no proof the *gate inside _process_message* actually invokes the helper.

    Constructing an NWCWallet calls _get_pubkey which requires the secp256k1
    native extension; skip the whole class if it's not installed in the test
    env (CI installs it via dev deps).
    """

    pytestmark = pytest.mark.skipif(
        __import__("importlib").util.find_spec("secp256k1") is None,
        reason="secp256k1 native extension not installed in this env",
    )

    @staticmethod
    def _make_nwc_uri(wallet_pubkey_hex: str) -> str:
        # Construct a valid-shaped NWC URI without actually connecting. The
        # secret can be any 64-hex-char string for ctor purposes.
        secret_hex = "1" * 64
        relay = urllib.parse.quote("wss://relay.example.invalid", safe="")
        return f"nostr+walletconnect://{wallet_pubkey_hex}?relay={relay}&secret={secret_hex}"

    @pytest.mark.asyncio
    async def test_forged_pubkey_does_not_resolve_pending_future(self):
        # Wallet expects events from this pubkey
        expected_wallet_pubkey = "a" * 64
        wallet = NWCWallet(self._make_nwc_uri(expected_wallet_pubkey))

        # Register a pending request the forged event tries to resolve
        request_id = "deadbeef" * 8  # 64 hex chars
        future: asyncio.Future = asyncio.get_event_loop().create_future()
        wallet._pending_requests[request_id] = future

        # Forged event: kind 23195 but pubkey != wallet's
        forged_event = {
            "id": "0" * 64,
            "pubkey": "b" * 64,  # WRONG — attacker pubkey
            "sig": "0" * 128,
            "kind": 23195,
            "created_at": 1700000000,
            "tags": [
                ["p", wallet._pubkey],  # addressed to us
                ["e", request_id],
            ],
            "content": "ciphertext-irrelevant",
        }

        await wallet._process_message(["EVENT", "subid", forged_event])

        assert not future.done(), \
            "F-11 — forged event with wrong pubkey must not resolve our pending future"

    @pytest.mark.asyncio
    async def test_invalid_signature_does_not_resolve_pending_future(self):
        # Wallet expects events from THIS pubkey; the forged event matches the
        # pubkey but has a bogus signature.
        expected_wallet_pubkey = "a" * 64
        wallet = NWCWallet(self._make_nwc_uri(expected_wallet_pubkey))

        request_id = "cafef00d" * 8
        future: asyncio.Future = asyncio.get_event_loop().create_future()
        wallet._pending_requests[request_id] = future

        forged_event = {
            "id": "0" * 64,           # Won't match recomputed id either
            "pubkey": expected_wallet_pubkey,
            "sig": "0" * 128,         # Invalid sig
            "kind": 23195,
            "created_at": 1700000000,
            "tags": [
                ["p", wallet._pubkey],
                ["e", request_id],
            ],
            "content": "ciphertext-irrelevant",
        }

        await wallet._process_message(["EVENT", "subid", forged_event])

        assert not future.done(), \
            "F-11 — forged event with invalid sig must not resolve our pending future"


@pytest.mark.skipif(os.name != "posix",
                    reason="POSIX-only — Windows uses icacls (process spawn)")
class TestF12RestrictFilePermissionsPosix:
    """On POSIX, _restrict_file_permissions chmods to 0600. Verifies the
    actual mode bits after the call."""

    def test_restrict_sets_user_only_rw(self, tmp_path: Path):
        target = tmp_path / "config.json"
        target.write_text("{}")
        # World-readable starting state (typical default)
        os.chmod(target, 0o644)

        _restrict_file_permissions(target)

        mode = stat.S_IMODE(target.stat().st_mode)
        assert mode == 0o600, f"expected 0600, got {oct(mode)}"

    def test_restrict_idempotent_on_already_locked(self, tmp_path: Path):
        target = tmp_path / "config.json"
        target.write_text("{}")
        os.chmod(target, 0o600)

        _restrict_file_permissions(target)

        mode = stat.S_IMODE(target.stat().st_mode)
        assert mode == 0o600

    def test_restrict_does_not_raise_on_missing_file(self, tmp_path: Path):
        # Best-effort hardening — failure logs a warning, never raises.
        # An exception here would hard-block first-run setup.
        missing = tmp_path / "does-not-exist.json"
        _restrict_file_permissions(missing)  # should not raise


class TestF12ConfigurationServiceCreatesLockedFile:
    """End-to-end: ConfigurationService._create_default_config_file must
    leave the file with 0600 perms on POSIX."""

    @pytest.mark.skipif(os.name != "posix",
                        reason="POSIX-only — Windows uses icacls")
    def test_first_run_creates_locked_config(self, tmp_path: Path):
        # The service hardcodes Path.home() / ".lightning-enable", so we
        # patch Path.home at import time to redirect to a temp dir.
        with patch("lightning_enable_mcp.config.Path.home", return_value=tmp_path):
            svc = ConfigurationService()
            # Trigger the create-default path explicitly so the test is robust
            # to any caching or reload logic in the service constructor.
            svc._create_default_config_file()

            config_path = svc._config_file_path
            assert config_path.exists()
            mode = stat.S_IMODE(config_path.stat().st_mode)
            assert mode == 0o600, f"expected 0600 on freshly-written config, got {oct(mode)}"
