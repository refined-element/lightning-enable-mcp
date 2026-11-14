"""Direct relay publish/query against an in-process fake relay (127.0.0.1 only)."""

import pytest

from lightning_enable_mcp import attestation as att
from lightning_enable_mcp import nostr_relay as relay

from .fake_nostr_relay import FakeRelay

SK = (3).to_bytes(32, "big")  # BIP-340 test scalar — test-only
SUBJECT = att.Subject("u", "https://api.example.com/v1/forecast")


def _event(quality: int = 4) -> dict:
    tags = att.build_tags(SUBJECT, mech={"responded": True}, quality=quality)
    return att.sign_attestation(SK, tags, "")


@pytest.mark.asyncio
async def test_publish_accepted_then_query_returns_it():
    ev = _event()
    async with FakeRelay("accept") as r:
        out = await relay.publish_event(ev, r.url, timeout=5)
        assert out.ok and out.status == relay.ACCEPTED
        assert r.received[0] == ["EVENT", ev]

        q = await relay.query_events({"kinds": [38403], "#u": [SUBJECT.value]}, r.url, timeout=5)
        assert q.ok and q.status == relay.COMPLETE
        assert q.events == [ev]


@pytest.mark.asyncio
async def test_publish_ok_false_is_rejected_with_the_relay_reason():
    async with FakeRelay("reject") as r:
        out = await relay.publish_event(_event(), r.url, timeout=5)
    assert not out.ok
    assert out.status == relay.REJECTED
    assert "blocked" in out.message


@pytest.mark.asyncio
async def test_publish_auth_challenge_is_a_typed_auth_required_error():
    async with FakeRelay("auth") as r:
        out = await relay.publish_event(_event(), r.url, timeout=5)
    assert not out.ok
    assert out.status == relay.AUTH_REQUIRED
    assert "NIP-42" in out.message


@pytest.mark.asyncio
async def test_query_auth_required_is_typed():
    async with FakeRelay("auth") as r:
        q = await relay.query_events({"kinds": [38403]}, r.url, timeout=5)
    assert q.status == relay.AUTH_REQUIRED


@pytest.mark.asyncio
async def test_silent_relay_times_out():
    async with FakeRelay("silent") as r:
        out = await relay.publish_event(_event(), r.url, timeout=0.5)
        q = await relay.query_events({"kinds": [38403]}, r.url, timeout=0.5)
    assert out.status == relay.TIMEOUT
    assert q.status == relay.TIMEOUT and not q.ok


@pytest.mark.asyncio
async def test_unreachable_relay_is_connect_failed():
    out = await relay.publish_event(_event(), "ws://127.0.0.1:9", timeout=2)
    assert out.status == relay.CONNECT_FAILED


@pytest.mark.parametrize(
    "url,ok",
    [
        ("wss://agents.lightningenable.com", True),
        ("ws://127.0.0.1:7000", True),
        ("ws://localhost:7000", True),
        ("ws://agents.lightningenable.com", False),
        ("https://agents.lightningenable.com", False),
        ("wss://", False),
    ],
)
def test_relay_url_rule(url, ok):
    assert (relay.relay_url_problem(url) is None) is ok


@pytest.mark.asyncio
async def test_plain_ws_to_remote_host_is_refused_before_connecting():
    out = await relay.publish_event(_event(), "ws://relay.example.com", timeout=1)
    assert out.status == relay.INVALID_RELAY_URL


def test_relay_url_defaults_and_env_override():
    assert relay.resolve_relay_url({}) == relay.DEFAULT_RELAY_URL
    assert relay.resolve_relay_url({relay.RELAY_ENV_VAR: "${AGENT_RELAY_URL}"}) == relay.DEFAULT_RELAY_URL
    assert relay.resolve_relay_url({relay.RELAY_ENV_VAR: "wss://r.example"}) == "wss://r.example"
