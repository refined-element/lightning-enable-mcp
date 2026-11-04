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


def test_get_budget_service_prints_the_lockout_warning_to_stderr(monkeypatch, capsys):
    from lightning_enable_mcp import budget_service as budget_module
    from lightning_enable_mcp.config import (
        SessionSettings,
        UserBudgetConfiguration,
    )

    class _Config:
        configuration = UserBudgetConfiguration(
            session=SessionSettings(require_approval_for_first_payment=True)
        )

    monkeypatch.setenv(HOSTED_ENV_VAR, "1")
    monkeypatch.delenv(CHANNEL_ENV_VAR, raising=False)
    monkeypatch.setattr(budget_module, "get_config_service", lambda: _Config())
    monkeypatch.setattr(
        "lightning_enable_mcp.confirmation_channel.stdin_is_a_tty", lambda: False
    )
    monkeypatch.setattr(budget_module, "_default_budget_service", None)
    monkeypatch.setattr(budget_module, "BudgetService", lambda **kwargs: object())

    budget_module.get_budget_service()

    assert HOSTED_FIRST_PAYMENT_LOCKOUT_WARNING in capsys.readouterr().err
