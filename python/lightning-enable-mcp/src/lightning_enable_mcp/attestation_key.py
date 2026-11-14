"""
The reviewer key that signs this installation's attestations.

A NEW, dedicated secp256k1 key per agent installation. It is not the NWC wallet key,
not any payment key, and not derived from one, so publishing reviews never exposes or
links a key that can move money.

Where it comes from, in order:

1. ``LIGHTNING_ENABLE_ATTESTATION_KEY`` (64 hex characters or ``nsec1...``), for hosts
   that manage secrets themselves. Set but invalid is an error — never a silent
   fallback to another key.
2. ``~/.lightning-enable/attestation-key`` (64 hex characters). Generated on first use.
   The file is created owner-only AT CREATION: mode 0600 on POSIX; on Windows a
   protected DACL (no inherited entries) that grants only the current user. It is
   written to a temporary name and linked into place without replacing, so two
   processes starting at once agree on one key.

The secret is never logged and never returned; only the public key (hex and npub)
leaves this module. Rotation: delete the file (or change the variable) and a new key
is a NEW reviewer identity — earlier reviews stay attributed to the old pubkey.

Mirrors the .NET ``AttestationKeyStore``.
"""

from __future__ import annotations

import logging
import os
import secrets
import sys
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path

from . import nostr_event

logger = logging.getLogger("lightning-enable-mcp.attestation_key")

ENV_VAR = "LIGHTNING_ENABLE_ATTESTATION_KEY"
KEY_FILENAME = "attestation-key"

ROTATION_NOTE = (
    "Rotation: a new key is a new reviewer identity. Earlier reviews stay attributed to "
    "the old pubkey; nothing links the two unless you publish that link yourself."
)


class AttestationKeyError(Exception):
    """The reviewer key could not be loaded or created. Never contains key material."""


@dataclass(frozen=True)
class ReviewerIdentity:
    """Public half of the reviewer key, plus where it came from."""

    pubkey_hex: str
    npub: str
    source: str  # "env" | "file"
    path: str | None
    created: bool

    def to_dict(self) -> dict[str, object]:
        return {
            "pubkey": self.pubkey_hex,
            "npub": self.npub,
            "source": self.source,
            "keyFile": self.path,
            "createdNow": self.created,
        }


class _Secret:
    """Holds the 32 secret bytes; refuses to render them in repr/str."""

    __slots__ = ("_raw",)

    def __init__(self, raw: bytes) -> None:
        self._raw = raw

    def reveal(self) -> bytes:
        return self._raw

    def __repr__(self) -> str:
        return "<secret>"

    __str__ = __repr__


class AttestationKeyStore:
    """Loads (or on first use creates) the reviewer key. Caches it for the process."""

    def __init__(self, config_dir: Path | None = None, env: Mapping[str, str] | None = None):
        self._dir = config_dir or (Path.home() / ".lightning-enable")
        self._env = env if env is not None else os.environ
        self._cached: tuple[_Secret, ReviewerIdentity] | None = None

    @property
    def key_path(self) -> Path:
        return self._dir / KEY_FILENAME

    def identity(self) -> ReviewerIdentity:
        return self._load()[1]

    def secret_key(self) -> bytes:
        return self._load()[0].reveal()

    # ---- internals ----

    def _load(self) -> tuple[_Secret, ReviewerIdentity]:
        if self._cached is not None:
            return self._cached

        raw_env = (self._env.get(ENV_VAR) or "").strip()
        if raw_env and not raw_env.startswith("${"):
            try:
                secret = nostr_event.parse_secret_key(raw_env)
            except ValueError:
                raise AttestationKeyError(
                    f"{ENV_VAR} is set but is not a valid key (expected 64 hex characters "
                    "or nsec1...). Fix or unset it; no other key is used in its place."
                ) from None
            self._cached = (_Secret(secret), self._identity(secret, "env", None, False))
            return self._cached

        path = self.key_path
        created = False
        if not path.exists():
            created = self._create(path)
        secret = self._read(path)
        self._cached = (_Secret(secret), self._identity(secret, "file", str(path), created))
        if created:
            logger.info("Created a new attestation reviewer key at %s", path)
        return self._cached

    @staticmethod
    def _identity(secret: bytes, source: str, path: str | None, created: bool) -> ReviewerIdentity:
        pub = nostr_event.xonly_pubkey_hex(secret)
        return ReviewerIdentity(pub, nostr_event.npub_encode(pub), source, path, created)

    @staticmethod
    def _read(path: Path) -> bytes:
        try:
            text = path.read_text(encoding="ascii").strip()
            return nostr_event.parse_secret_key(text)
        except (OSError, ValueError, UnicodeDecodeError):
            raise AttestationKeyError(
                f"The attestation key file {path} is unreadable or not a valid key. It is "
                "left untouched; move it aside to create a new reviewer identity."
            ) from None

    def _create(self, path: Path) -> bool:
        """Create the key file owner-only. False if another process won the race."""
        try:
            path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        except OSError as e:
            raise AttestationKeyError(f"Cannot create {path.parent}: {e.strerror}") from None

        secret = _generate_secret()
        tmp = path.with_name(f"{KEY_FILENAME}.{secrets.token_hex(6)}.tmp")
        try:
            fd = create_owner_only_file(tmp)
            with os.fdopen(fd, "w", encoding="ascii", newline="\n") as f:
                f.write(secret.hex() + "\n")
                f.flush()
                os.fsync(f.fileno())
            try:
                _link_no_replace(tmp, path)
            except FileExistsError:
                return False
            return True
        except OSError as e:
            raise AttestationKeyError(
                f"Cannot create the attestation key file {path}: {e.strerror}"
            ) from None
        finally:
            try:
                tmp.unlink()
            except FileNotFoundError:
                pass
            except OSError:  # pragma: no cover - best effort
                logger.warning("Could not remove temporary key file %s", tmp)


def _generate_secret() -> bytes:
    while True:
        candidate = secrets.token_bytes(32)
        try:
            return nostr_event.parse_secret_key(candidate.hex())
        except ValueError:  # pragma: no cover - probability ~2^-128
            continue


def _link_no_replace(src: Path, dst: Path) -> None:
    """Put ``src`` at ``dst`` without ever replacing an existing ``dst``."""
    if sys.platform == "win32":
        os.rename(src, dst)  # raises FileExistsError if dst exists; keeps the DACL
    else:
        os.link(src, dst)  # raises FileExistsError if dst exists


def create_owner_only_file(path: Path) -> int:
    """Create a NEW file readable and writable only by the current user; return its fd.

    POSIX: ``O_CREAT | O_EXCL`` with mode 0600 (the umask can only remove bits).
    Windows: ``CreateFileW(CREATE_NEW)`` with a security descriptor whose DACL is
    protected (inheritance blocked) and holds one entry: full control for the current
    user's SID. The file never exists with broader access, not even briefly.
    """
    if sys.platform != "win32":
        return os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    return _create_owner_only_file_windows(path)


def current_user_sid() -> str:  # pragma: no cover - Windows only
    """The current process token's user SID, as an ``S-1-...`` string (Windows)."""
    import ctypes
    from ctypes import wintypes

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    advapi32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi32.GetTokenInformation.argtypes = [
        wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
    ]
    advapi32.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.LPWSTR)]
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

    token_query = 0x0008
    token_user_class = 1
    token = wintypes.HANDLE()
    if not advapi32.OpenProcessToken(kernel32.GetCurrentProcess(), token_query, ctypes.byref(token)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        needed = wintypes.DWORD()
        advapi32.GetTokenInformation(token, token_user_class, None, 0, ctypes.byref(needed))
        buf = ctypes.create_string_buffer(needed.value)
        if not advapi32.GetTokenInformation(token, token_user_class, buf, needed, ctypes.byref(needed)):
            raise ctypes.WinError(ctypes.get_last_error())
        # TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the PSID.
        psid = ctypes.cast(buf, ctypes.POINTER(ctypes.c_void_p))[0]
        sid_str = wintypes.LPWSTR()
        if not advapi32.ConvertSidToStringSidW(psid, ctypes.byref(sid_str)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return str(sid_str.value)
        finally:
            kernel32.LocalFree(sid_str)
    finally:
        kernel32.CloseHandle(token)


def _create_owner_only_file_windows(path: Path) -> int:  # pragma: no cover - Windows only
    import ctypes
    import msvcrt
    from ctypes import wintypes

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    class SECURITY_ATTRIBUTES(ctypes.Structure):  # noqa: N801 - Win32 name
        _fields_ = [
            ("nLength", wintypes.DWORD),
            ("lpSecurityDescriptor", ctypes.c_void_p),
            ("bInheritHandle", wintypes.BOOL),
        ]

    advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p,
    ]
    kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(SECURITY_ATTRIBUTES),
        wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE,
    ]
    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

    sid = current_user_sid()
    # O: owner = this user. D:P = protected DACL (no inherited ACEs). One ACE: allow
    # FILE_ALL_ACCESS (FA) to this user's SID. Nobody else, not even Administrators.
    sddl = f"O:{sid}D:P(A;;FA;;;{sid})"
    psd = ctypes.c_void_p()
    if not advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, ctypes.byref(psd), None):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        sa = SECURITY_ATTRIBUTES(ctypes.sizeof(SECURITY_ATTRIBUTES), psd, False)
        generic_write = 0x40000000
        create_new = 1
        file_attribute_normal = 0x80
        invalid = wintypes.HANDLE(-1).value
        handle = kernel32.CreateFileW(
            str(path), generic_write, 0, ctypes.byref(sa), create_new, file_attribute_normal, None
        )
        if handle == invalid or handle is None:
            err = ctypes.get_last_error()
            if err == 80:  # ERROR_FILE_EXISTS
                raise FileExistsError(str(path))
            raise ctypes.WinError(err)
        try:
            return msvcrt.open_osfhandle(handle, os.O_WRONLY)
        except Exception:
            kernel32.CloseHandle(handle)
            raise
    finally:
        kernel32.LocalFree(psd)


def canonical_sid(text: str) -> str:  # pragma: no cover - Windows only, used by tests
    """``S-1-...`` form of an SDDL SID token, resolving aliases such as ``LA`` or ``BA``."""
    import ctypes
    from ctypes import wintypes

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    advapi32.ConvertStringSidToSidW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_void_p)]
    advapi32.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.LPWSTR)]
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    psid = ctypes.c_void_p()
    if not advapi32.ConvertStringSidToSidW(text, ctypes.byref(psid)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        out = wintypes.LPWSTR()
        if not advapi32.ConvertSidToStringSidW(psid, ctypes.byref(out)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return str(out.value)
        finally:
            kernel32.LocalFree(out)
    finally:
        kernel32.LocalFree(psid)


def read_dacl_sddl(path: Path) -> str:  # pragma: no cover - Windows only, used by tests
    """The file's owner and DACL in SDDL form, e.g. ``O:S-1-...D:P(A;;FA;;;S-1-...)``."""
    import ctypes
    from ctypes import wintypes

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    advapi32.GetNamedSecurityInfoW.argtypes = [
        wintypes.LPCWSTR, ctypes.c_int, wintypes.DWORD, ctypes.c_void_p, ctypes.c_void_p,
        ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p),
    ]
    advapi32.GetNamedSecurityInfoW.restype = wintypes.DWORD
    advapi32.ConvertSecurityDescriptorToStringSecurityDescriptorW.argtypes = [
        ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(wintypes.LPWSTR), ctypes.c_void_p,
    ]
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]

    se_file_object = 1
    owner_info, dacl_info = 0x1, 0x4
    psd = ctypes.c_void_p()
    rc = advapi32.GetNamedSecurityInfoW(
        str(path), se_file_object, owner_info | dacl_info, None, None, None, None, ctypes.byref(psd)
    )
    if rc != 0:
        raise ctypes.WinError(rc)
    try:
        out = wintypes.LPWSTR()
        if not advapi32.ConvertSecurityDescriptorToStringSecurityDescriptorW(
            psd, 1, owner_info | dacl_info, ctypes.byref(out), None
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return str(out.value)
        finally:
            kernel32.LocalFree(out)
    finally:
        kernel32.LocalFree(psd)
