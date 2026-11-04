"""Guard: the first-run config.json budget defaults are identical in both ports.

On first run each port writes ``~/.lightning-enable/config.json``. The .NET port
wrote $0.10 / $1 / $10 / $100 tiers with first-payment approval ON, while this port
wrote $1 / $5 / $25 / $100 with it OFF, so the same operator got a looser budget
just by picking the Python package. The shared fixture
``shared/first-run-budget-defaults.json`` (repo root) is read by this test and by the
.NET ``FirstRunBudgetDefaultsTests``, so the two cannot drift again.

The fixture lives above this package and is not shipped in the sdist, so the
parity test skips when it is absent; the inline-constant test always runs.
"""

import json
from decimal import Decimal
from pathlib import Path

import pytest

from lightning_enable_mcp import config as config_module

# Mirrors shared/first-run-budget-defaults.json (the .NET values).
EXPECTED = {
    "currency": "USD",
    "tiers": {
        "autoApprove": Decimal("0.10"),
        "logAndApprove": Decimal("1.00"),
        "formConfirm": Decimal("10.00"),
        "urlConfirm": Decimal("100.00"),
    },
    "limits": {
        "maxPerPayment": Decimal("500.00"),
        "maxPerSession": Decimal("100.00"),
    },
    "session": {
        "requireApprovalForFirstPayment": True,
        "cooldownSeconds": 2,
    },
}


def _find_fixture() -> Path | None:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "shared" / "first-run-budget-defaults.json"
        if candidate.is_file():
            return candidate
    return None


def _write_first_run_config(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> dict:
    monkeypatch.setattr(config_module.Path, "home", staticmethod(lambda: tmp_path))
    config_module.ConfigurationService()
    written = tmp_path / ".lightning-enable" / "config.json"
    assert written.is_file(), "first run must write a default config file"
    return json.loads(written.read_text(encoding="utf-8"), parse_float=Decimal)


def _assert_matches(written: dict, expected: dict) -> None:
    assert written["currency"] == expected["currency"]
    for section in ("tiers", "limits", "session"):
        for key, value in expected[section].items():
            actual = written[section][key]
            if isinstance(value, bool):
                assert actual is value, f"{section}.{key}: wrote {actual!r}, expected {value!r}"
            else:
                assert Decimal(str(actual)) == Decimal(str(value)), (
                    f"{section}.{key}: wrote {actual}, expected {value}"
                )


def test_first_run_writes_the_dotnet_budget_defaults(tmp_path, monkeypatch):
    _assert_matches(_write_first_run_config(tmp_path, monkeypatch), EXPECTED)


def test_first_run_defaults_match_the_shared_cross_port_fixture(tmp_path, monkeypatch):
    fixture = _find_fixture()
    if fixture is None:
        pytest.skip("shared/first-run-budget-defaults.json not present (unpacked sdist)")
    shared = json.loads(fixture.read_text(encoding="utf-8"), parse_float=Decimal)
    _assert_matches(_write_first_run_config(tmp_path, monkeypatch), shared)
