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

import json
import os
import stat
from pathlib import Path
from unittest.mock import patch

import pytest

from lightning_enable_mcp.config import (
    ConfigurationService,
    _restrict_file_permissions,
)
from lightning_enable_mcp.nwc_wallet import _verify_nostr_event_signature


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
