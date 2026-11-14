"""
A minimal Nostr relay client for attestations: publish one event (EVENT -> OK) and run
one query (REQ -> EVENT* -> EOSE). Each call opens its own short-lived websocket.

Writing goes straight from this client to the relay; it does not depend on the hosted
Lightning Enable API. The relay comes from ``AGENT_RELAY_URL`` (default
``wss://agents.lightningenable.com``). It must be ``wss://``; plain ``ws://`` is allowed
only for a loopback host (local relays and tests).

NIP-42 relay authentication is NOT implemented. When a relay demands it (an ``AUTH``
challenge, or an ``OK``/``CLOSED`` message prefixed ``auth-required:``), the call
returns ``status="auth_required"`` with an explanation instead of failing vaguely.

Mirrors the .NET ``NostrRelayClient``.
"""

from __future__ import annotations

import asyncio
import json
import os
import secrets
import time
from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Any
from urllib.parse import urlsplit

import websockets

RELAY_ENV_VAR = "AGENT_RELAY_URL"
DEFAULT_RELAY_URL = "wss://agents.lightningenable.com"
DEFAULT_TIMEOUT_SECONDS = 10.0
MAX_QUERY_EVENTS = 500

ACCEPTED = "accepted"
REJECTED = "rejected"
AUTH_REQUIRED = "auth_required"
TIMEOUT = "timeout"
CONNECT_FAILED = "connect_failed"
PROTOCOL_ERROR = "protocol_error"
INVALID_RELAY_URL = "invalid_relay_url"
COMPLETE = "complete"

_LOOPBACK_HOSTS = {"localhost", "127.0.0.1", "::1"}


@dataclass
class RelayOutcome:
    """What the relay said. ``ok`` is True only for an accepted publish or a query
    that reached EOSE."""

    ok: bool
    status: str
    relay: str
    message: str = ""
    events: list[dict[str, Any]] = field(default_factory=list)


def resolve_relay_url(env: Mapping[str, str] | None = None) -> str:
    env = env if env is not None else os.environ
    value = (env.get(RELAY_ENV_VAR) or "").strip()
    if not value or value.startswith("${"):
        return DEFAULT_RELAY_URL
    return value


def relay_url_problem(url: str) -> str | None:
    """None when ``url`` is an acceptable relay URL, else why not."""
    try:
        parts = urlsplit(url)
    except ValueError:
        return "not a valid URL"
    host = (parts.hostname or "").lower()
    if not host:
        return "no host"
    if parts.scheme == "wss":
        return None
    if parts.scheme == "ws" and host in _LOOPBACK_HOSTS:
        return None
    return "must be wss:// (ws:// is allowed only for localhost)"


def auth_required_message(relay: str, detail: str = "") -> str:
    return (
        f"The relay {relay} requires NIP-42 authentication before it accepts this. This "
        "server does not implement relay AUTH yet. Ask the relay operator to accept "
        "kind 38403 events without AUTH (or to allowlist your reviewer pubkey), or set "
        f"{RELAY_ENV_VAR} to a relay that does." + (f" Relay said: {detail}" if detail else "")
    )


async def publish_event(
    event: dict[str, Any], relay_url: str, timeout: float = DEFAULT_TIMEOUT_SECONDS
) -> RelayOutcome:
    """Send ``["EVENT", event]`` and wait for the matching ``["OK", id, bool, msg]``."""
    problem = relay_url_problem(relay_url)
    if problem:
        return RelayOutcome(False, INVALID_RELAY_URL, relay_url, f"Relay URL {problem}.")

    deadline = time.monotonic() + timeout
    saw_auth = False
    try:
        ws = await asyncio.wait_for(websockets.connect(relay_url), timeout=timeout)
    except Exception as e:
        return RelayOutcome(False, CONNECT_FAILED, relay_url, f"Could not connect: {type(e).__name__}")

    try:
        await ws.send(json.dumps(["EVENT", event], ensure_ascii=False))
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise asyncio.TimeoutError
            frame = _parse(await asyncio.wait_for(ws.recv(), timeout=remaining))
            if frame is None:
                continue
            kind = frame[0]
            if kind == "AUTH":
                saw_auth = True
            elif kind == "OK" and len(frame) >= 3 and frame[1] == event.get("id"):
                msg = frame[3] if len(frame) >= 4 and isinstance(frame[3], str) else ""
                if frame[2] is True:
                    return RelayOutcome(True, ACCEPTED, relay_url, msg)
                if msg.startswith("auth-required:"):
                    return RelayOutcome(False, AUTH_REQUIRED, relay_url, auth_required_message(relay_url, msg))
                return RelayOutcome(False, REJECTED, relay_url, msg or "The relay refused the event.")
    except asyncio.TimeoutError:
        if saw_auth:
            return RelayOutcome(False, AUTH_REQUIRED, relay_url, auth_required_message(relay_url))
        return RelayOutcome(False, TIMEOUT, relay_url, f"No OK from the relay within {timeout:g}s.")
    except websockets.ConnectionClosed:
        if saw_auth:
            return RelayOutcome(False, AUTH_REQUIRED, relay_url, auth_required_message(relay_url))
        return RelayOutcome(False, PROTOCOL_ERROR, relay_url, "The relay closed the connection before replying.")
    except Exception as e:
        return RelayOutcome(False, PROTOCOL_ERROR, relay_url, f"Relay error: {type(e).__name__}")
    finally:
        await _close(ws)


async def query_events(
    filter_: dict[str, Any],
    relay_url: str,
    timeout: float = DEFAULT_TIMEOUT_SECONDS,
    max_events: int = MAX_QUERY_EVENTS,
) -> RelayOutcome:
    """Send ``["REQ", sub, filter]``; collect EVENTs until EOSE. On timeout, return what
    arrived with ``status="timeout"`` so the caller can report a partial read."""
    problem = relay_url_problem(relay_url)
    if problem:
        return RelayOutcome(False, INVALID_RELAY_URL, relay_url, f"Relay URL {problem}.")

    deadline = time.monotonic() + timeout
    sub = "att-" + secrets.token_hex(4)
    events: list[dict[str, Any]] = []
    saw_auth = False
    try:
        ws = await asyncio.wait_for(websockets.connect(relay_url), timeout=timeout)
    except Exception as e:
        return RelayOutcome(False, CONNECT_FAILED, relay_url, f"Could not connect: {type(e).__name__}")

    try:
        await ws.send(json.dumps(["REQ", sub, filter_]))
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise asyncio.TimeoutError
            frame = _parse(await asyncio.wait_for(ws.recv(), timeout=remaining))
            if frame is None:
                continue
            kind = frame[0]
            if kind == "AUTH":
                saw_auth = True
            elif kind == "EVENT" and len(frame) >= 3 and frame[1] == sub:
                if isinstance(frame[2], dict) and len(events) < max_events:
                    events.append(frame[2])
            elif kind == "EOSE" and len(frame) >= 2 and frame[1] == sub:
                try:
                    await ws.send(json.dumps(["CLOSE", sub]))
                except Exception:
                    pass
                return RelayOutcome(True, COMPLETE, relay_url, "", events)
            elif kind == "CLOSED" and len(frame) >= 2 and frame[1] == sub:
                msg = frame[2] if len(frame) >= 3 and isinstance(frame[2], str) else ""
                if msg.startswith("auth-required:"):
                    return RelayOutcome(False, AUTH_REQUIRED, relay_url, auth_required_message(relay_url, msg), events)
                return RelayOutcome(False, REJECTED, relay_url, msg or "The relay closed the query.", events)
    except asyncio.TimeoutError:
        if saw_auth and not events:
            return RelayOutcome(False, AUTH_REQUIRED, relay_url, auth_required_message(relay_url), events)
        return RelayOutcome(False, TIMEOUT, relay_url, f"No EOSE from the relay within {timeout:g}s.", events)
    except websockets.ConnectionClosed:
        return RelayOutcome(False, PROTOCOL_ERROR, relay_url, "The relay closed the connection before EOSE.", events)
    except Exception as e:
        return RelayOutcome(False, PROTOCOL_ERROR, relay_url, f"Relay error: {type(e).__name__}", events)
    finally:
        await _close(ws)


#: Deepest JSON nesting accepted in a relay frame (System.Text.Json's default, so both
#: ports skip the same frames). A Nostr frame needs about 4.
MAX_FRAME_DEPTH = 64


def _parse(raw: Any) -> list[Any] | None:
    """A relay frame as a list, or None to skip it: unparseable, too deep, or not a
    ``["TYPE", ...]`` array. Never raises, so one hostile frame cannot end a query."""
    try:
        if _depth_exceeds(raw, MAX_FRAME_DEPTH):
            return None
        frame = json.loads(raw)
    except (TypeError, ValueError, RecursionError):
        return None
    if isinstance(frame, list) and frame and isinstance(frame[0], str):
        return frame
    return None


def _depth_exceeds(raw: Any, limit: int) -> bool:
    """Whether the bracket nesting of a JSON text goes deeper than ``limit`` (strings
    are skipped), checked before json.loads can recurse."""
    if isinstance(raw, bytes):
        raw = raw.decode("utf-8", errors="replace")
    if not isinstance(raw, str):
        return False
    depth = 0
    in_string = escaped = False
    for ch in raw:
        if in_string:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
        elif ch == '"':
            in_string = True
        elif ch in "[{":
            depth += 1
            if depth > limit:
                return True
        elif ch in "]}":
            depth -= 1
    return False


async def _close(ws: Any) -> None:
    try:
        await asyncio.wait_for(ws.close(), timeout=2.0)
    except Exception:
        pass
