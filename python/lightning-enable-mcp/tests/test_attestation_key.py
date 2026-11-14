"""Reviewer key custody: generated on first use, owner-only at creation, never exposed.

Custody is asserted on the file's ACL (Windows) or mode (POSIX), not on whether the
test process can read it — this host may run elevated, where readability proves nothing.
"""

import json
import re
import stat
import sys
from pathlib import Path

import pytest

from lightning_enable_mcp import attestation_key as ak
from lightning_enable_mcp import nostr_event

# BIP-340 test scalar 3 — a published test value, not a credential.
TEST_KEY_HEX = "0000000000000000000000000000000000000000000000000000000000000003"
TEST_PUBKEY = "f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9"


def _store(tmp_path: Path, env: dict | None = None) -> ak.AttestationKeyStore:
    return ak.AttestationKeyStore(config_dir=tmp_path / ".lightning-enable", env=env or {})


def test_first_use_creates_key_and_second_load_reuses_it(tmp_path):
    first = _store(tmp_path).identity()
    assert first.created is True and first.source == "file"
    assert nostr_event.is_hex(first.pubkey_hex, 64)
    assert first.npub.startswith("npub1")

    again = _store(tmp_path).identity()
    assert again.pubkey_hex == first.pubkey_hex
    assert again.created is False

    text = (tmp_path / ".lightning-enable" / ak.KEY_FILENAME).read_text(encoding="ascii")
    assert re.fullmatch(r"[0-9a-f]{64}\n", text)
    assert not list((tmp_path / ".lightning-enable").glob("*.tmp"))


def test_identity_never_carries_the_secret(tmp_path):
    store = _store(tmp_path)
    ident = store.identity()
    secret_hex = store.secret_key().hex()
    assert secret_hex not in json.dumps(ident.to_dict())
    assert secret_hex not in repr(ident)
    assert secret_hex not in repr(store.__dict__)


@pytest.mark.skipif(sys.platform == "win32", reason="POSIX mode check; Windows checks the DACL")
def test_key_file_mode_is_0600_on_posix(tmp_path):
    store = _store(tmp_path)
    store.identity()
    assert stat.S_IMODE(store.key_path.stat().st_mode) == 0o600


def _assert_owner_only_dacl(path: Path) -> None:
    """Owner is this user; DACL is protected and holds exactly one ACE: allow, full
    access (FA), this user's SID. No Administrators, SYSTEM, Users or Everyone entry.
    SDDL may print a well-known SID as an alias (e.g. LA), so SIDs are resolved."""
    sid = ak.current_user_sid()
    sddl = ak.read_dacl_sddl(path)
    owner, flags, aces = re.fullmatch(r"O:(.+?)D:([A-Z]*)((?:\([^)]*\))*)", sddl).groups()
    assert ak.canonical_sid(owner) == sid
    assert "P" in flags, f"DACL is not protected (inherits from the folder): {sddl}"
    entries = [a.split(";") for a in re.findall(r"\(([^)]*)\)", aces)]
    assert len(entries) == 1, f"expected exactly one ACE: {sddl}"
    ace_type, ace_flags, rights, _obj, _inh, trustee = entries[0]
    assert (ace_type, ace_flags, rights) == ("A", "", "FA")
    assert ak.canonical_sid(trustee) == sid


@pytest.mark.skipif(sys.platform != "win32", reason="Windows DACL check; POSIX checks the mode")
def test_key_file_dacl_is_protected_and_owner_only_on_windows(tmp_path):
    store = _store(tmp_path)
    store.identity()
    _assert_owner_only_dacl(store.key_path)


@pytest.mark.skipif(sys.platform != "win32", reason="Windows only")
def test_owner_only_file_is_protected_from_the_moment_of_creation(tmp_path):
    import os

    target = tmp_path / "probe"
    fd = ak.create_owner_only_file(target)
    try:
        # Still open, nothing written yet: the ACL is already the final one.
        _assert_owner_only_dacl(target)
    finally:
        os.close(fd)


def test_create_owner_only_file_refuses_to_overwrite(tmp_path):
    import os

    target = tmp_path / "exists"
    target.write_text("x")
    with pytest.raises(FileExistsError):
        os.close(ak.create_owner_only_file(target))


@pytest.mark.parametrize(
    "value",
    [TEST_KEY_HEX, "nsec1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqps52s3re"],
)
def test_env_var_key_is_used_and_no_file_written(tmp_path, value):
    store = _store(tmp_path, {ak.ENV_VAR: value})
    ident = store.identity()
    assert ident.pubkey_hex == TEST_PUBKEY
    assert ident.source == "env" and ident.path is None
    assert not store.key_path.exists()


def test_invalid_env_var_is_an_error_without_echoing_it(tmp_path):
    bogus = "not-a-key-" + "9" * 40
    store = _store(tmp_path, {ak.ENV_VAR: bogus})
    with pytest.raises(ak.AttestationKeyError) as exc:
        store.identity()
    assert bogus not in str(exc.value)
    assert not store.key_path.exists()


def test_placeholder_env_var_is_treated_as_unset(tmp_path):
    ident = _store(tmp_path, {ak.ENV_VAR: "${LIGHTNING_ENABLE_ATTESTATION_KEY}"}).identity()
    assert ident.source == "file"


def _write_owner_only(path: Path, text: str) -> None:
    import os

    with os.fdopen(ak.create_owner_only_file(path), "w", encoding="ascii") as f:
        f.write(text)


def test_corrupt_key_file_is_an_error_and_left_untouched(tmp_path):
    store = _store(tmp_path)
    store.key_path.parent.mkdir(parents=True)
    _write_owner_only(store.key_path, "garbage\n")
    with pytest.raises(ak.AttestationKeyError) as exc:
        store.identity()
    assert "not a valid key" in str(exc.value)
    assert store.key_path.read_text() == "garbage\n"


def test_key_file_readable_by_other_accounts_is_refused(tmp_path):
    """Loading applies the creation rule. A plainly written file inherits the folder's
    access list (on Windows: SYSTEM, Administrators...; on POSIX: made 0644 here)."""
    import os

    store = _store(tmp_path)
    store.key_path.parent.mkdir(parents=True)
    store.key_path.write_text(TEST_KEY_HEX + "\n")
    if sys.platform != "win32":
        os.chmod(store.key_path, 0o644)
    else:
        # Asserted on the ACL, not on readability: it really lists other accounts.
        assert ak._windows_other_trustees(store.key_path)
    with pytest.raises(ak.AttestationKeyError) as exc:
        store.identity()
    msg = str(exc.value)
    assert "other accounts" in msg
    assert ("icacls" in msg) if sys.platform == "win32" else ("chmod 600" in msg)
    assert TEST_KEY_HEX not in msg
    assert store.key_path.read_text() == TEST_KEY_HEX + "\n"


def test_owner_only_key_file_is_accepted(tmp_path):
    store = _store(tmp_path)
    store.key_path.parent.mkdir(parents=True)
    _write_owner_only(store.key_path, TEST_KEY_HEX + "\n")
    assert ak.owner_only_problem(store.key_path) is None
    assert store.identity().pubkey_hex == TEST_PUBKEY


def test_losing_the_creation_race_keeps_the_winners_key(tmp_path):
    store = _store(tmp_path)
    store.key_path.parent.mkdir(parents=True)
    _write_owner_only(store.key_path, TEST_KEY_HEX + "\n")  # the "other process" won
    assert store._create(store.key_path) is False
    assert store.identity().pubkey_hex == TEST_PUBKEY
    assert not list(store.key_path.parent.glob("*.tmp"))


def test_key_is_independent_of_the_nwc_wallet_secret(tmp_path):
    nwc = "nostr+walletconnect://" + "ab" * 32 + "?relay=wss://relay.example.com&secret=" + TEST_KEY_HEX
    store = _store(tmp_path, {"NWC_CONNECTION_STRING": nwc})
    assert store.identity().pubkey_hex != TEST_PUBKEY
