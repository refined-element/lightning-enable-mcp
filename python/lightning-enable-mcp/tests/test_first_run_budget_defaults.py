"""Guard: both ports use the same budget defaults, on first run and for missing keys.

On first run each port writes ``~/.lightning-enable/config.json``; when an existing
file omits a key, each port falls back to a model default. Both must equal
``shared/first-run-budget-defaults.json`` (repo root), which the .NET
``FirstRunBudgetDefaultsTests`` reads too. ``shared/partial-tier-fixtures.json``
pins how both ports repair a partial or out-of-order tier ladder.

The shared fixtures live above this package and are not shipped in the sdist, so
tests fall back to the inline ``EXPECTED`` copy (or skip) when they are absent.
"""

import json
from decimal import Decimal
from pathlib import Path

import pytest

from lightning_enable_mcp import config as config_module
from lightning_enable_mcp.config import (
    PaymentLimits,
    SessionSettings,
    TierThresholds,
    clamp_tier_ladder,
)

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

SECTIONS = ("tiers", "limits", "session")

# Top-level sections each port writes that the fixture does not pin (their shapes
# are port-specific: credentials and the approval channel, all null by default).
UNPINNED_TOP_LEVEL = {"wallets", "confirmation"}


def _find_shared(name: str) -> Path | None:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "shared" / name
        if candidate.is_file():
            return candidate
    return None


def _load_shared(name: str):
    path = _find_shared(name)
    if path is None:
        return None
    return json.loads(path.read_text(encoding="utf-8"), parse_float=Decimal)


def _expected() -> dict:
    """The shared fixture when present, else the inline copy."""
    return _load_shared("first-run-budget-defaults.json") or EXPECTED


def _use_home(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    monkeypatch.setattr(config_module.Path, "home", staticmethod(lambda: tmp_path))
    return tmp_path / ".lightning-enable" / "config.json"


def _write_first_run_config(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> dict:
    path = _use_home(tmp_path, monkeypatch)
    config_module.ConfigurationService()
    assert path.is_file(), "first run must write a default config file"
    return json.loads(path.read_text(encoding="utf-8"), parse_float=Decimal)


def _load_from_file(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, content: str):
    path = _use_home(tmp_path, monkeypatch)
    path.parent.mkdir()
    path.write_text(content, encoding="utf-8")
    return config_module.ConfigurationService().configuration


def _as_dict(cfg) -> dict:
    return {
        "currency": cfg.currency,
        "tiers": cfg.tiers.to_dict(),
        "limits": cfg.limits.to_dict(),
        "session": cfg.session.to_dict(),
    }


def _assert_matches(written: dict, expected: dict) -> None:
    assert written["currency"] == expected["currency"]
    for section in SECTIONS:
        for key, value in expected[section].items():
            actual = written[section][key]
            if isinstance(value, bool):
                assert actual is value, f"{section}.{key}: got {actual!r}, expected {value!r}"
            else:
                assert Decimal(str(actual)) == Decimal(str(value)), (
                    f"{section}.{key}: got {actual}, expected {value}"
                )


def _assert_key_sets(written: dict, expected: dict) -> None:
    """No key beyond the fixture's in the pinned sections, and no stray sections."""
    pinned = {k for k in expected if not k.startswith("_")}
    assert set(written) - pinned <= UNPINNED_TOP_LEVEL, set(written) - pinned
    for section in SECTIONS:
        assert set(written[section]) == set(expected[section]), section


def _ladder(tiers: TierThresholds) -> dict:
    return {
        "autoApprove": tiers.auto_approve,
        "logAndApprove": tiers.log_and_approve,
        "formConfirm": tiers.form_confirm,
        "urlConfirm": tiers.url_confirm,
    }


def _assert_ladder(actual: dict, expected: dict) -> None:
    for key, value in expected.items():
        assert Decimal(str(actual[key])) == Decimal(str(value)), (
            f"{key}: got {actual[key]}, expected {value}"
        )


# --- First run ----------------------------------------------------------------


def test_inline_constants_match_the_shared_fixture():
    shared = _load_shared("first-run-budget-defaults.json")
    if shared is None:
        pytest.skip("shared/first-run-budget-defaults.json not present (unpacked sdist)")
    _assert_matches(EXPECTED, shared)
    _assert_matches(shared, EXPECTED)


def test_first_run_writes_the_shared_defaults_and_no_other_keys(tmp_path, monkeypatch):
    written = _write_first_run_config(tmp_path, monkeypatch)
    _assert_matches(written, _expected())
    _assert_key_sets(written, _expected())


# --- Fallbacks for an existing config.json that omits keys -------------------
# Owner decision 2026-11-03: a missing key falls back to the first-run set.


def test_dataclass_defaults_match_the_shared_defaults():
    _assert_matches(
        {
            "currency": "USD",
            "tiers": TierThresholds().to_dict(),
            "limits": PaymentLimits().to_dict(),
            "session": SessionSettings().to_dict(),
        },
        _expected(),
    )


def test_from_dict_fallbacks_match_the_shared_defaults():
    _assert_matches(
        {
            "currency": "USD",
            "tiers": TierThresholds.from_dict({}).to_dict(),
            "limits": PaymentLimits.from_dict({}).to_dict(),
            "session": SessionSettings.from_dict({}).to_dict(),
        },
        _expected(),
    )


@pytest.mark.parametrize("partial", ["{}", '{"tiers": {}, "limits": {}, "session": {}}'])
def test_partial_config_file_falls_back_to_the_shared_defaults(tmp_path, monkeypatch, partial):
    loaded = _as_dict(_load_from_file(tmp_path, monkeypatch, partial))
    _assert_matches(loaded, _expected())
    _assert_key_sets(loaded, _expected())


def test_explicit_null_limits_still_mean_no_limit(tmp_path, monkeypatch):
    loaded = _load_from_file(
        tmp_path, monkeypatch, '{"limits": {"maxPerPayment": null, "maxPerSession": null}}'
    )
    assert loaded.limits.max_per_payment is None
    assert loaded.limits.max_per_session is None


# --- Out-of-order tiers: clamp lower tiers down, never raise one --------------


@pytest.mark.parametrize(
    ("tiers", "expected"),
    [
        (
            {"autoApprove": 2},
            {"autoApprove": "1", "logAndApprove": "1", "formConfirm": "10", "urlConfirm": "100"},
        ),
        (
            {"logAndApprove": 20},
            {"autoApprove": "0.10", "logAndApprove": "10", "formConfirm": "10", "urlConfirm": "100"},
        ),
        (
            {"formConfirm": 0.5},
            {"autoApprove": "0.10", "logAndApprove": "0.5", "formConfirm": "0.5", "urlConfirm": "100"},
        ),
    ],
)
def test_clamp_rule_lowers_the_lower_tier(tiers, expected):
    _assert_ladder(_ladder(clamp_tier_ladder(TierThresholds.from_dict(tiers))), expected)


def test_clamp_never_raises_a_tier():
    original = TierThresholds.from_dict(
        {"autoApprove": 50, "logAndApprove": 40, "formConfirm": 30, "urlConfirm": 20}
    )
    clamped = _ladder(clamp_tier_ladder(original))
    for key, value in clamped.items():
        assert value <= _ladder(original)[key]
    assert list(clamped.values()) == sorted(clamped.values())


def _partial_tier_cases():
    shared = _load_shared("partial-tier-fixtures.json")
    if shared is None:
        return [pytest.param(None, None, marks=pytest.mark.skip(reason="fixture absent"))]
    return [pytest.param(c["tiers"], c["expected"], id=c["name"]) for c in shared["cases"]]


@pytest.mark.parametrize(("tiers", "expected"), _partial_tier_cases())
def test_partial_tier_file_matches_the_shared_ladder(tmp_path, monkeypatch, tiers, expected):
    loaded = _load_from_file(tmp_path, monkeypatch, json.dumps({"tiers": tiers}, default=float))
    _assert_ladder(_ladder(loaded.tiers), expected)


# --- Unusable limits and cooldown reset to the default, like .NET -------------


@pytest.mark.parametrize("bad", [0, -5])
def test_non_positive_limits_reset_to_the_default(tmp_path, monkeypatch, capsys, bad):
    loaded = _load_from_file(
        tmp_path, monkeypatch, json.dumps({"limits": {"maxPerPayment": bad, "maxPerSession": bad}})
    )
    assert loaded.limits.max_per_payment == PaymentLimits.max_per_payment
    assert loaded.limits.max_per_session == PaymentLimits.max_per_session
    err = capsys.readouterr().err
    assert "maxPerPayment must be positive" in err
    assert "maxPerSession must be positive" in err


@pytest.mark.parametrize("bad", [-1, 61])
def test_out_of_range_cooldown_resets_to_the_default(tmp_path, monkeypatch, capsys, bad):
    loaded = _load_from_file(tmp_path, monkeypatch, json.dumps({"session": {"cooldownSeconds": bad}}))
    assert loaded.session.cooldown_seconds == SessionSettings.cooldown_seconds
    assert "cooldownSeconds should be 0-60" in capsys.readouterr().err
