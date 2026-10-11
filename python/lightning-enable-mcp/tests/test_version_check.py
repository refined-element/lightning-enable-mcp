"""
Startup version check (Python port of the .NET ``Program.Main`` check).

Best effort: stderr only (stdout is the MCP transport), 5-second timeout, every failure
swallowed, and it never holds up serving.
"""

import asyncio
import io
from contextlib import asynccontextmanager
from unittest.mock import AsyncMock

import httpx
import pytest

from lightning_enable_mcp import server as server_module
from lightning_enable_mcp import version_check
from lightning_enable_mcp._version import USER_AGENT_PYTHON, __version__
from lightning_enable_mcp.version_check import check_for_updates


def _json(payload, status=200):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(status, json=payload)

    return httpx.MockTransport(handler)


def _raising(exc_factory):
    def handler(request: httpx.Request) -> httpx.Response:
        raise exc_factory(request)

    return httpx.MockTransport(handler)


@pytest.fixture(autouse=True)
def _default_api_url(monkeypatch):
    monkeypatch.delenv("LIGHTNING_ENABLE_API_URL", raising=False)


async def test_request_targets_version_check_with_versioned_user_agent():
    seen: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(200, json={"updateRequired": False})

    await check_for_updates("2.1.0", transport=httpx.MockTransport(handler), stream=io.StringIO())

    assert len(seen) == 1
    request = seen[0]
    assert request.method == "GET"
    assert request.url.scheme == "https"
    assert request.url.host == "api.lightningenable.com"
    assert request.url.path == "/api/mcp/version-check"
    assert dict(request.url.params) == {"currentVersion": "2.1.0"}
    assert request.headers["User-Agent"] == USER_AGENT_PYTHON
    assert request.headers["User-Agent"] == f"LightningEnable-MCP-Python/{__version__}"
    assert request.extensions["timeout"]["read"] == 5.0
    assert request.extensions["timeout"]["connect"] == 5.0


async def test_honors_lightning_enable_api_url(monkeypatch):
    monkeypatch.setenv("LIGHTNING_ENABLE_API_URL", "http://localhost:5096/")
    seen: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(200, json={})

    await check_for_updates("2.1.0", transport=httpx.MockTransport(handler), stream=io.StringIO())

    assert str(seen[0].url) == "http://localhost:5096/api/mcp/version-check?currentVersion=2.1.0"


async def test_update_required_prints_banner_to_stderr_only(capsys):
    payload = {
        "updateRequired": True,
        "message": "Version 2.0.0 has a funds-safety bug.",
        "releaseNotes": "Confirmation codes are now bound to the tool.",
    }

    await check_for_updates("2.0.0", transport=_json(payload))

    captured = capsys.readouterr()
    assert captured.out == ""
    assert "CRITICAL UPDATE REQUIRED" in captured.err
    assert "[Lightning Enable MCP] Version 2.0.0 has a funds-safety bug." in captured.err
    assert (
        "[Lightning Enable MCP] What's fixed: Confirmation codes are now bound to the tool."
        in captured.err
    )
    assert "!" * 54 in captured.err


async def test_update_required_without_message_still_prints_banner(capsys):
    await check_for_updates("2.0.0", transport=_json({"updateRequired": True}))

    captured = capsys.readouterr()
    assert captured.out == ""
    assert "CRITICAL UPDATE REQUIRED" in captured.err
    assert "What's fixed" not in captured.err


async def test_update_recommended_prints_one_line(capsys):
    payload = {"updateRequired": False, "updateRecommended": True, "latestVersion": "2.2.0"}

    await check_for_updates("2.1.0", transport=_json(payload))

    captured = capsys.readouterr()
    assert captured.out == ""
    assert "Update available: v2.2.0" in captured.err
    assert "pip install --upgrade lightning-enable-mcp" in captured.err
    assert "uvx --refresh lightning-enable-mcp" in captured.err
    assert "CRITICAL" not in captured.err


async def test_up_to_date_prints_nothing(capsys):
    payload = {"updateRequired": False, "updateRecommended": False, "latestVersion": "2.1.0"}

    await check_for_updates("2.1.0", transport=_json(payload))

    assert capsys.readouterr() == ("", "")


@pytest.mark.parametrize(
    "transport",
    [
        pytest.param(_json({"updateRequired": True, "message": "x"}, status=404), id="404"),
        pytest.param(_json({"updateRequired": True}, status=500), id="500"),
        pytest.param(
            _raising(lambda r: httpx.ReadTimeout("timed out", request=r)), id="timeout"
        ),
        pytest.param(
            _raising(lambda r: httpx.ConnectError("refused", request=r)), id="connect-error"
        ),
        pytest.param(
            httpx.MockTransport(lambda r: httpx.Response(200, text="<html>not json</html>")),
            id="non-json",
        ),
        pytest.param(_json({"updateRequired": "yes"}), id="non-bool-flag"),
        pytest.param(_json(["updateRequired"]), id="non-object"),
    ],
)
async def test_failures_are_silent_and_never_raise(capsys, transport):
    await check_for_updates("2.1.0", transport=transport)

    assert capsys.readouterr() == ("", "")


async def test_run_prints_version_banner_and_does_not_wait_for_the_check(monkeypatch, capsys):
    started = asyncio.Event()
    cancelled = asyncio.Event()

    async def slow_check() -> None:
        started.set()
        try:
            await asyncio.sleep(3600)
        except asyncio.CancelledError:
            cancelled.set()
            raise

    @asynccontextmanager
    async def fake_stdio_server():
        # Let the background task get scheduled before the server loop "runs".
        await asyncio.sleep(0)
        yield object(), object()

    monkeypatch.setattr(server_module, "check_for_updates", slow_check)
    monkeypatch.setattr(server_module, "stdio_server", fake_stdio_server)

    srv = server_module.LightningEnableServer()
    srv.server.run = AsyncMock()

    # A check that never finishes must not hold up serving.
    await asyncio.wait_for(srv.run(), timeout=2)

    srv.server.run.assert_awaited_once()
    assert started.is_set()
    await asyncio.sleep(0)
    assert cancelled.is_set(), "the check is cancelled when the server stops"

    captured = capsys.readouterr()
    assert captured.out == ""
    assert f"[Lightning Enable MCP] Version {__version__} starting..." in captured.err


def test_module_never_writes_to_stdout():
    source = version_check.__file__
    with open(source, encoding="utf-8") as handle:
        text = handle.read()
    assert "sys.stdout" not in text
    # Every print in the module targets an explicit stream (stderr in production).
    assert text.count("print(") > 0
    assert text.count("file=stream") == text.count("print(")
