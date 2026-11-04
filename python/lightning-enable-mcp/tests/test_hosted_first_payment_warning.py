"""Startup warning: hosted + no approval channel + first-payment approval = no payments.

With ``LIGHTNING_ENABLE_HOSTED=1`` and no approval channel configured, the channel resolves
to ``refuse``. With ``session.requireApprovalForFirstPayment`` true, the first payment of a
session needs approval, is refused, and the session never records a payment, so every
later payment is a "first payment" too: nothing can ever be paid. The server must say so
loudly at startup (owner decision 2026-11-03), and only in exactly that state.
"""

import pytest

from lightning_enable_mcp.config import ConfirmationSettings
from lightning_enable_mcp.confirmation_channel import (
    CHANNEL_ENV_VAR,
    HOSTED_ENV_VAR,
    HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING,
    create_confirmation_channel,
)


def _warnings(*, hosted=True, channel=None, first_payment=True, tty=False, env_channel=None):
    environ = {}
    if hosted:
        environ[HOSTED_ENV_VAR] = "1"
    if env_channel:
        environ[CHANNEL_ENV_VAR] = env_channel
    collected: list[str] = []
    create_confirmation_channel(
        ConfirmationSettings(channel=channel, file_path="/tmp/le-approvals.jsonl"),
        warn=collected.append,
        stdin_is_tty=tty,
        environ=environ,
        require_approval_for_first_payment=first_payment,
    )
    return collected


def _has_lockout(warnings: list[str]) -> bool:
    return HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING in warnings


def test_lockout_warning_is_emitted_in_the_dead_configuration():
    warnings = _warnings()
    assert _has_lockout(warnings)


def test_lockout_warning_says_no_payment_can_be_made_and_names_the_three_fixes():
    message = HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING
    assert "No payment can be made" in message
    assert "config volume" in message
    assert "requireApprovalForFirstPayment" in message and "false" in message
    assert "webhook" in message and "file" in message


def test_no_lockout_warning_when_not_hosted():
    assert not _has_lockout(_warnings(hosted=False))


@pytest.mark.parametrize("channel", ["file", "stderr"])
def test_no_lockout_warning_when_a_channel_is_configured(channel):
    assert not _has_lockout(_warnings(channel=channel))


def test_no_lockout_warning_when_a_channel_is_set_by_env():
    assert not _has_lockout(_warnings(env_channel="file"))


def test_no_lockout_warning_when_first_payment_approval_is_off():
    assert not _has_lockout(_warnings(first_payment=False))


def test_no_lockout_warning_when_a_human_is_at_the_terminal():
    # Hosted with a TTY resolves to stderr: a human can read and relay the code.
    assert not _has_lockout(_warnings(tty=True))


# --- Startup path: printed once, at server start, with or without a wallet --------------

_WALLET_ENV_VARS = (
    "LND_REST_HOST",
    "LND_MACAROON_HEX",
    "NWC_CONNECTION_STRING",
    "STRIKE_API_KEY",
    "OPENNODE_API_KEY",
)


def _startup_env(monkeypatch, tmp_path, *, hosted=True, config_json=None):
    """Hosted, non-TTY, no wallet, fresh HOME; returns the server module."""
    from contextlib import asynccontextmanager
    from unittest.mock import AsyncMock

    from lightning_enable_mcp import budget_service as budget_module
    from lightning_enable_mcp import config as config_module
    from lightning_enable_mcp import server as server_module

    for name in _WALLET_ENV_VARS + (CHANNEL_ENV_VAR,):
        monkeypatch.delenv(name, raising=False)
    if hosted:
        monkeypatch.setenv(HOSTED_ENV_VAR, "1")
    else:
        monkeypatch.delenv(HOSTED_ENV_VAR, raising=False)
    monkeypatch.setattr(config_module.Path, "home", staticmethod(lambda: tmp_path))
    monkeypatch.setattr(config_module, "_config_service", None)
    monkeypatch.setattr(budget_module, "_default_budget_service", None)
    monkeypatch.setattr(
        "lightning_enable_mcp.confirmation_channel.stdin_is_a_tty", lambda: False
    )
    if config_json is not None:
        (tmp_path / ".lightning-enable").mkdir()
        (tmp_path / ".lightning-enable" / "config.json").write_text(config_json, encoding="utf-8")

    @asynccontextmanager
    async def _fake_stdio():
        yield (None, None)

    monkeypatch.setattr(server_module, "stdio_server", _fake_stdio)
    monkeypatch.setattr(server_module, "check_for_updates", AsyncMock(return_value=None))
    return server_module


def _run_startup(server_module) -> None:
    import asyncio
    from unittest.mock import AsyncMock

    srv = server_module.LightningEnableServer()
    srv.server.run = AsyncMock(return_value=None)
    asyncio.run(srv.run())


def test_startup_prints_the_lockout_warning_once_with_no_wallet(monkeypatch, tmp_path, capsys):
    server_module = _startup_env(monkeypatch, tmp_path)

    _run_startup(server_module)

    assert capsys.readouterr().err.count(HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING) == 1


def test_startup_prints_no_lockout_warning_when_not_hosted(monkeypatch, tmp_path, capsys):
    server_module = _startup_env(monkeypatch, tmp_path, hosted=False)

    _run_startup(server_module)

    assert HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING not in capsys.readouterr().err


def test_startup_prints_no_lockout_warning_when_first_payment_approval_is_off(
    monkeypatch, tmp_path, capsys
):
    server_module = _startup_env(
        monkeypatch,
        tmp_path,
        config_json='{"session": {"requireApprovalForFirstPayment": false}}',
    )

    _run_startup(server_module)

    assert HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING not in capsys.readouterr().err


def test_startup_prints_no_lockout_warning_when_a_channel_is_configured(
    monkeypatch, tmp_path, capsys
):
    server_module = _startup_env(
        monkeypatch, tmp_path, config_json='{"confirmation": {"channel": "file"}}'
    )

    _run_startup(server_module)

    assert HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING not in capsys.readouterr().err


def test_budget_service_creation_does_not_repeat_the_lockout_warning(
    monkeypatch, tmp_path, capsys
):
    """Startup owns the warning; building the budget service on the first tool call must not
    print it a second time."""
    _startup_env(monkeypatch, tmp_path)
    from lightning_enable_mcp import budget_service as budget_module

    budget_module.get_budget_service()

    assert HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING not in capsys.readouterr().err
