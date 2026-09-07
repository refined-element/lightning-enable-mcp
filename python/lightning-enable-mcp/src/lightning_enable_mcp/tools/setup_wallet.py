"""Wallet onboarding — the tool an agent reaches for BEFORE it can do anything else.

Called with no arguments it answers "is there a wallet, and where did it come from?"
and, when there is not, hands back the shortest path to one. Called with an NWC
connection string it validates the string, proves it reaches a live wallet, and only
then writes it to ``~/.lightning-enable/config.json``.

Two rules shape everything here:

1. **The credential never comes back.** Not in the report, not in an error, not in the
   probe result. Only the provider, the source, and what the wallet says about itself
   (engineering standard #5).
2. **Environment wins over the config file.** If an env var already selects a wallet,
   writing the config file would be a silent no-op, so the tool refuses and says which
   variable to unset rather than leaving the operator with a file that looks configured
   and a server that ignores it.

Mirrors the .NET ``SetupWalletTool`` / ``WalletOnboardingService``.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING, Any
from urllib.parse import parse_qs

from mcp.types import Tool

if TYPE_CHECKING:  # pragma: no cover - typing only
    from ..config import ConfigurationService

logger = logging.getLogger("lightning-enable-mcp.tools.setup_wallet")

#: Total budget for a live wallet probe. Long enough for a relay handshake plus a NIP-47
#: round trip, short enough that a wallet which never answers fails the setup call with a
#: clear message instead of stalling the agent.
PROBE_TIMEOUT_SECONDS = 10.0

#: A wallet pubkey and a client secret are both 32 bytes of hex.
_HEX_KEY_LENGTH = 64

_HEX_DIGITS = frozenset("0123456789abcdefABCDEF")

#: The config shape a human can paste by hand instead of using this tool.
CONFIG_SHAPE = (
    '{ "wallets": { "nwcConnectionString": "nostr+walletconnect://<64-hex pubkey>'
    '?relay=wss://<relay>&secret=<64-hex>" } }'
)


@dataclass(frozen=True)
class NwcConnectionInfo:
    """A validated NWC connection string, split into its parts."""

    wallet_pubkey: str
    relays: tuple[str, ...]
    secret: str
    lud16: str | None = None

    @property
    def relay_url(self) -> str:
        """The primary relay — the first that passed validation."""
        return self.relays[0]


def _is_hex_key(value: str | None) -> bool:
    return bool(value) and len(value) == _HEX_KEY_LENGTH and set(value) <= _HEX_DIGITS


def _is_valid_relay(relay: str) -> bool:
    """A relay must be an absolute ``ws://``/``wss://`` URL with a host.

    The comma check rejects a comma-joined value ("wss://a,wss://b") that a lenient URL
    parser would otherwise accept as one host — a real relay URL never contains a comma.
    """
    if "," in relay:
        return False
    for scheme in ("ws://", "wss://"):
        if relay.lower().startswith(scheme):
            return len(relay) > len(scheme)
    return False


def parse_nwc_connection_string(connection_string: str | None) -> NwcConnectionInfo:
    """Validate an NWC connection string, or raise ``ValueError`` naming the bad field.

    Splits scheme / authority / query BY HAND rather than going through a URL parser.
    The authority here is a 64-character hex pubkey, which is NOT a hostname: a parser
    that applies host rules to it rejects an all-digit pubkey as a malformed IPv4 literal
    (a legal x-only key — rare, but a wallet can mint one), and accepts any host-shaped
    64-character value in the other direction. The value is a fixed-shape opaque
    identifier, so validate it directly. Same reasoning as the .NET ``NwcConfig.Parse``
    fix and the Lightning Enable API's ``NwcConnectionConfig``.

    No message ever quotes the input: the string is a live wallet credential and a parse
    failure must be safe to log (engineering standard #5).
    """
    if not connection_string or not connection_string.strip():
        raise ValueError(
            "An NWC connection string is required. Copy it from your wallet app — it "
            "starts with nostr+walletconnect://."
        )

    trimmed = connection_string.strip()

    scheme, separator, remainder = trimmed.partition("://")
    if not separator or scheme.lower() not in ("nostr+walletconnect", "nwc"):
        raise ValueError(
            "The connection string must start with nostr+walletconnect:// (or nwc://)."
        )

    wallet_pubkey, _, query_string = remainder.partition("?")

    if not _is_hex_key(wallet_pubkey):
        raise ValueError(
            f"The wallet public key must be {_HEX_KEY_LENGTH} hex characters."
        )

    # parse_qs, not a dict of first values: an Alby-style string advertises TWO relays for
    # redundancy, and collapsing them loses the failover target.
    params = parse_qs(query_string, keep_blank_values=False)

    advertised = [r.strip() for r in params.get("relay", []) if r and r.strip()]
    relays = tuple(r for r in advertised if _is_valid_relay(r))
    if not relays:
        raise ValueError(
            "The connection string must advertise at least one relay as an absolute "
            "ws:// or wss:// URL."
        )

    secret = next(iter(params.get("secret", [])), None)
    if not _is_hex_key(secret):
        raise ValueError(
            f"The 'secret' parameter must be {_HEX_KEY_LENGTH} hex characters."
        )

    lud16 = next(iter(params.get("lud16", [])), None)

    # Lowercased once here: every downstream use (event tags, relay filters, the ECDH peer
    # key) compares pubkeys as lowercase hex.
    return NwcConnectionInfo(
        wallet_pubkey=wallet_pubkey.lower(),
        relays=relays,
        secret=secret,  # type: ignore[arg-type]  # _is_hex_key proved it is a str
        lud16=lud16,
    )


def _env(name: str) -> str | None:
    """An env var counts only when set and actually expanded (not a literal ``${...}``)."""
    value = os.getenv(name)
    if not value or value.startswith("${"):
        return None
    return value


def describe_wallet_state(config_service: ConfigurationService | None = None) -> dict:
    """Which wallet this server would use, and where that choice came from.

    Carries the PROVIDER and the SOURCE — never the credential itself.
    """
    if config_service is None:
        from ..config import get_config_service

        config_service = get_config_service()

    wallets = config_service.configuration.wallets
    config_path = config_service.config_file_path

    lnd_env = bool(_env("LND_REST_HOST") and _env("LND_MACAROON_HEX"))
    lnd_config = bool(wallets.lnd_rest_host and wallets.lnd_macaroon_hex)

    # In the L402-first default priority order.
    candidates: list[tuple[str, str, bool, bool]] = [
        ("LND", "LND_REST_HOST + LND_MACAROON_HEX", lnd_env, lnd_config),
        ("NWC", "NWC_CONNECTION_STRING", bool(_env("NWC_CONNECTION_STRING")),
         bool(wallets.nwc_connection_string)),
        ("Strike", "STRIKE_API_KEY", bool(_env("STRIKE_API_KEY")),
         bool(wallets.strike_api_key)),
        ("OpenNode", "OPENNODE_API_KEY", bool(_env("OPENNODE_API_KEY")),
         bool(wallets.opennode_api_key)),
    ]

    # Mirrors the server's selection: an explicit WALLET_PRIORITY wins if that wallet is
    # actually configured, otherwise the default order above.
    priority = (os.getenv("WALLET_PRIORITY") or wallets.priority or "").strip().lower()
    if priority:
        for index, candidate in enumerate(candidates):
            if candidate[0].lower() == priority and (candidate[2] or candidate[3]):
                candidates.insert(0, candidates.pop(index))
                break

    configured = [c for c in candidates if c[2] or c[3]]
    if not configured:
        return {
            "configured": False,
            "provider": None,
            "source": None,
            "fromEnvironment": False,
            "configuredProviders": [],
            "configFile": config_path,
        }

    provider, env_var, from_env, _ = configured[0]
    return {
        "configured": True,
        "provider": provider,
        "source": f"environment ({env_var})" if from_env else f"config file ({config_path})",
        "fromEnvironment": from_env,
        "configuredProviders": [c[0] for c in configured],
        "configFile": config_path,
    }


def save_nwc_connection_string(config_path: str | Path, connection_string: str) -> None:
    """Write ``wallets.nwcConnectionString``, leaving every other key untouched.

    Merges into the EXISTING document rather than serializing a fresh configuration: the
    file is the operator's, and it may carry limits, tiers, or keys this build does not
    know about. Restricts the file's permissions afterwards (F-12) — it now holds a live
    wallet credential in plaintext.
    """
    from ..config import _restrict_file_permissions

    path = Path(config_path)
    path.parent.mkdir(parents=True, exist_ok=True)

    data: dict[str, Any] = {}
    if path.exists():
        try:
            with open(path, encoding="utf-8") as f:
                loaded = json.load(f)
            if isinstance(loaded, dict):
                data = loaded
        except json.JSONDecodeError as ex:
            # A hand-edited file that no longer parses must not silently lose the
            # operator's limits: refuse rather than overwrite.
            raise ValueError(
                f"{path} is not valid JSON, so it cannot be updated safely. Fix or remove "
                "the file and run setup_wallet again."
            ) from ex

    wallets = data.get("wallets")
    if not isinstance(wallets, dict):
        wallets = {}
        data["wallets"] = wallets
    wallets["nwcConnectionString"] = connection_string

    with open(path, "w", encoding="utf-8") as f:
        f.write(json.dumps(data, indent=2))

    _restrict_file_permissions(path)


async def _default_probe(connection_string: str) -> dict:
    """Connect to the wallet the connection string names and ask what it can do.

    Bounded by :data:`PROBE_TIMEOUT_SECONDS` so a black-holed relay cannot hang setup.
    Returns the same shape a stub does, so the tool has one code path.
    """
    from ..nwc_wallet import NWCWallet

    wallet = NWCWallet(connection_string)
    methods: list[str] = []
    alias: str | None = None
    info_error: str | None = None
    balance_sats: int | None = None
    balance_error: str | None = None

    async def _probe() -> None:
        nonlocal methods, alias, info_error, balance_sats, balance_error
        await wallet.connect()
        try:
            info = await wallet.get_info()
            declared = info.get("methods")
            if isinstance(declared, list):
                methods = [m for m in declared if isinstance(m, str)]
            raw_alias = info.get("alias")
            alias = raw_alias if isinstance(raw_alias, str) else None
        except Exception as ex:  # noqa: BLE001 - reported, never raised at the agent
            # Some wallets do not implement get_info. Fall through to get_balance, the
            # more widely implemented method, and only report this if that fails too.
            info_error = str(ex)
        try:
            balance_sats = await wallet.get_balance()
        except Exception as ex:  # noqa: BLE001
            balance_error = str(ex)

    try:
        await asyncio.wait_for(_probe(), timeout=PROBE_TIMEOUT_SECONDS)
    except asyncio.TimeoutError:
        return {
            "success": False,
            "error": (
                f"The wallet did not answer within {PROBE_TIMEOUT_SECONDS:.0f} seconds. Check "
                "the relay URL in the connection string, and that the connection is still "
                "active in your wallet app."
            ),
        }
    except Exception as ex:  # noqa: BLE001
        return {"success": False, "error": f"The wallet could not be reached: {ex}"}
    finally:
        try:
            await wallet.disconnect()
        except Exception:  # noqa: BLE001 - teardown must never mask the probe result
            logger.debug("NWC probe disconnect failed", exc_info=True)

    if info_error is not None and balance_error is not None:
        return {
            "success": False,
            "error": (
                f"The wallet could not be reached: {info_error} "
                f"(get_balance also failed: {balance_error})"
            ),
        }

    return {
        "success": True,
        "error": balance_error,
        "methods": methods,
        "balanceSats": balance_sats,
        "alias": alias,
    }


async def setup_wallet(
    nwc_connection_string: str | None = None,
    config_service: ConfigurationService | None = None,
    probe: Callable[[str], Awaitable[dict]] | None = None,
    config_path: str | Path | None = None,
) -> str:
    """Report the configured wallet, or validate + save an NWC connection string."""
    try:
        state = describe_wallet_state(config_service)
    except Exception as ex:  # noqa: BLE001
        return json.dumps({
            "success": False,
            "error": f"Could not read the current wallet configuration: {ex}",
        }, indent=2)

    if not nwc_connection_string or not nwc_connection_string.strip():
        return _report(state)

    return await _connect_nwc(
        nwc_connection_string.strip(),
        state,
        probe or _default_probe,
        Path(config_path) if config_path else Path(state["configFile"]),
        config_service,
    )


def _report(state: dict) -> str:
    """The no-arguments answer: what is configured, or how to configure something."""
    if state["configured"]:
        return json.dumps({
            "success": True,
            "configured": True,
            "wallet": state["provider"],
            "source": state["source"],
            "configuredWallets": state["configuredProviders"],
            "configFile": state["configFile"],
            "note": "The credential itself is never reported by this tool.",
            "nextStep": (
                "Run test_l402_payment to confirm the wallet can pay an L402 challenge end "
                "to end (~1 sat)."
            ),
        }, indent=2)

    return json.dumps({
        "success": True,
        "configured": False,
        "configFile": state["configFile"],
        "nextStep": (
            "Connect a wallet. The shortest path is NWC: copy a connection string from your "
            "wallet app and call setup_wallet again with nwc_connection_string set."
        ),
        "options": [
            {
                "wallet": "NWC (recommended - Alby Hub, CoinOS, or any NIP-47 wallet)",
                "how": (
                    "In the wallet app, create a Nostr Wallet Connect connection and copy "
                    "the nostr+walletconnect:// string it gives you."
                ),
                "then": (
                    "setup_wallet with nwc_connection_string set to that string. This tool "
                    "validates it, checks it against the live wallet, and saves it."
                ),
                "l402": "Returns a preimage, so L402 works.",
            },
            {
                "wallet": "LND",
                "how": (
                    "Set LND_REST_HOST (host:port) and LND_MACAROON_HEX (admin macaroon as hex)."
                ),
                "then": "Restart the server.",
                "l402": "Always returns a preimage, so L402 works.",
            },
            {
                "wallet": "Strike",
                "how": "Set STRIKE_API_KEY from https://dashboard.strike.me/.",
                "then": "Restart the server.",
                "l402": "Returns a preimage, so L402 works.",
            },
            {
                "wallet": "OpenNode",
                "how": "Set OPENNODE_API_KEY.",
                "then": "Restart the server.",
                "l402": (
                    "Receiving and invoicing only - OpenNode does not return a preimage, so "
                    "it CANNOT pay L402 challenges."
                ),
            },
        ],
        "configFileShape": CONFIG_SHAPE,
        "priority": (
            "When several wallets are configured: LND > NWC > Strike > OpenNode. Environment "
            "variables win over the config file."
        ),
    }, indent=2)


async def _connect_nwc(
    connection_string: str,
    state: dict,
    probe: Callable[[str], Awaitable[dict]],
    config_path: Path,
    config_service: ConfigurationService | None,
) -> str:
    """Validate, refuse-if-env-wins, probe, save."""
    # 1. Parse only. A malformed string never reaches a relay, and the failure names the
    #    malformed field without echoing it.
    try:
        parsed = parse_nwc_connection_string(connection_string)
    except ValueError as ex:
        return json.dumps({
            "success": False,
            "error": f"That is not a valid NWC connection string: {ex}",
            "expectedShape": (
                "nostr+walletconnect://<64-hex wallet pubkey>?relay=wss://<relay>"
                "&secret=<64-hex client secret>"
            ),
            "hint": (
                "Copy the string again from your wallet app - it must be complete, including "
                "the relay and secret parameters."
            ),
        }, indent=2)

    # 2. Environment wins over the config file, so writing the file here would leave the
    #    operator with a config that looks right and a server that ignores it.
    if state["fromEnvironment"]:
        return json.dumps({
            "success": False,
            "saved": False,
            "error": (
                f"A wallet is already selected by the environment ({state['source']}), and "
                "environment variables take precedence over the config file. Nothing was "
                "written."
            ),
            "wallet": state["provider"],
            "source": state["source"],
            "howToProceed": (
                "Unset that environment variable and call setup_wallet again, or keep using "
                "the wallet the environment selects."
            ),
        }, indent=2)

    # 3. Prove the string reaches a live wallet BEFORE persisting it — a saved-but-dead
    #    credential fails later, at a payment, where it is far more expensive to diagnose.
    try:
        result = await probe(connection_string)
    except Exception as ex:  # noqa: BLE001
        result = {"success": False, "error": str(ex)}

    if not result.get("success"):
        return json.dumps({
            "success": False,
            "saved": False,
            "error": (
                "The connection string is well-formed but the wallet could not be reached: "
                f"{result.get('error')}"
            ),
            "relay": parsed.relay_url,
            "hint": (
                "Check that the relay is reachable and that this connection is still active "
                "in your wallet app. Nothing was saved."
            ),
        }, indent=2)

    # 4. Persist.
    try:
        save_nwc_connection_string(config_path, connection_string)
        if config_service is not None:
            config_service.reload()
    except Exception as ex:  # noqa: BLE001
        return json.dumps({
            "success": False,
            "saved": False,
            "error": f"The wallet answered, but the connection string could not be saved: {ex}",
            "configFile": str(config_path),
            "configFileShape": CONFIG_SHAPE,
            "hint": "Add the connection string to the config file by hand using the shape above.",
        }, indent=2)

    methods = result.get("methods") or []
    can_pay = any(str(m).lower() == "pay_invoice" for m in methods)

    return json.dumps({
        "success": True,
        "saved": True,
        "wallet": "NWC",
        "walletAlias": result.get("alias"),
        "relay": parsed.relay_url,
        "configFile": str(config_path),
        "methods": methods,
        "balanceSats": result.get("balanceSats"),
        "balanceNote": result.get("error"),
        "canPayL402": can_pay,
        "note": (
            "The wallet declares pay_invoice, so it can pay L402 challenges."
            if can_pay
            else "The wallet did not declare pay_invoice in get_info. If payments fail, check "
                 "what this connection is permitted to do in your wallet app."
        ),
        "security": (
            "The connection string was written to the config file and the file's permissions "
            "were restricted to your user. It is never returned by this tool."
        ),
        "nextStep": (
            "Restart the MCP server so it picks up the new wallet, then run test_l402_payment "
            "to confirm it can pay an L402 challenge end to end (~1 sat)."
        ),
    }, indent=2)


# MCP tool schema (lives beside its handler; registered in tools/registry.py).
#
# The description is deliberately terse: it is re-sent to the model every session, while
# the guided setup path costs nothing until the tool is actually called.
SETUP_WALLET_TOOL = Tool(
    name="setup_wallet",
    description=(
        "Report the wallet this server pays from, or connect one. Call with no arguments "
        "first - it also returns the setup steps when no wallet is configured."
    ),
    inputSchema={
        "type": "object",
        "properties": {
            "nwc_connection_string": {
                "type": "string",
                "description": (
                    "NWC connection string (nostr+walletconnect://...). Omit to just report."
                ),
            },
        },
    },
)
