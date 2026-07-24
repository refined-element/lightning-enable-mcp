"""Guard: published manifests must not advertise env vars the server no longer reads.

The legacy ``BudgetManager`` and its ``L402_MAX_SATS_PER_REQUEST`` /
``L402_MAX_SATS_PER_SESSION`` env vars were removed (see the comment in
``server.py._initialize_services``). Runtime sats caps are now tightened via the
``configure_budget`` tool, sourced from ``~/.lightning-enable/config.json`` (USD
limits converted to sats). Neither the Python nor the .NET server reads those env
vars anymore, so no published manifest may advertise them: doing so promises the
operator a spending cap that silently does nothing — a funds-safety hazard.

``server.json`` (the official MCP Registry source) and ``smithery.yaml`` live at
the monorepo root, above this package. They are intentionally NOT shipped in the
sdist (see pyproject ``[tool.hatch.build.targets.sdist]``), so we locate the root
by walking upward and skip cleanly when the manifests aren't present (e.g. running
from an unpacked sdist) rather than failing on a path that doesn't exist there.
"""

import json
import re
from pathlib import Path

import pytest

# Env-var names (SCREAMING_SNAKE) as they appear in server.json and in the
# smithery ``commandFunction`` env mapping.
REMOVED_ENV_VARS = {"L402_MAX_SATS_PER_REQUEST", "L402_MAX_SATS_PER_SESSION"}

# The camelCase ``configSchema`` property names Smithery actually renders as input
# fields in its "Add MCP server" UI — the user-facing half of the same promise.
REMOVED_SMITHERY_PROPS = {"l402MaxSatsPerRequest", "l402MaxSatsPerSession"}


def _find_repo_root() -> Path | None:
    """Nearest ancestor containing both root manifests, or None if absent."""
    for parent in Path(__file__).resolve().parents:
        if (parent / "server.json").is_file() and (parent / "smithery.yaml").is_file():
            return parent
    return None


REPO_ROOT = _find_repo_root()
_skip_if_no_root = pytest.mark.skipif(
    REPO_ROOT is None,
    reason="root manifests (server.json / smithery.yaml) not present — installed/sdist context",
)


def _iter_strings(obj):
    """Yield every string scalar in a nested JSON structure."""
    if isinstance(obj, str):
        yield obj
    elif isinstance(obj, dict):
        for value in obj.values():
            yield from _iter_strings(value)
    elif isinstance(obj, list):
        for value in obj:
            yield from _iter_strings(value)


@_skip_if_no_root
def test_server_json_does_not_advertise_removed_env_vars():
    """server.json must not reference the removed budget-cap env vars anywhere.

    Exact-match over every string scalar (not a substring scan): catches a removed
    name reintroduced as an env-var name OR an argument value, in any package,
    without false-positiving on a description that merely mentions the name.
    """
    data = json.loads((REPO_ROOT / "server.json").read_text(encoding="utf-8"))

    offenders = sorted({s for s in _iter_strings(data) if s in REMOVED_ENV_VARS})

    assert not offenders, (
        "server.json references budget-cap env vars the server no longer reads "
        f"(they silently no-op): {offenders}. Caps now come from the configure_budget "
        "tool / ~/.lightning-enable/config.json."
    )


@_skip_if_no_root
def test_smithery_yaml_does_not_advertise_removed_env_vars():
    """smithery.yaml must not re-add the config field or its env mapping.

    Checks the two structural surfaces precisely so a benign comment mentioning a
    removed name does not trip the guard: (1) a ``configSchema`` property key like
    ``l402MaxSatsPerRequest:``, and (2) a ``commandFunction`` assignment like
    ``env.L402_MAX_SATS_PER_REQUEST``.
    """
    text = (REPO_ROOT / "smithery.yaml").read_text(encoding="utf-8")

    prop_pattern = re.compile(
        r"^[ \t]*(" + "|".join(re.escape(p) for p in REMOVED_SMITHERY_PROPS) + r")[ \t]*:",
        re.MULTILINE,
    )
    schema_offenders = sorted(set(prop_pattern.findall(text)))
    mapping_offenders = sorted(name for name in REMOVED_ENV_VARS if f"env.{name}" in text)

    offenders = schema_offenders + mapping_offenders
    assert not offenders, (
        "smithery.yaml still advertises budget-cap config fields / env vars the server "
        f"no longer reads (they silently no-op): {offenders}. Caps now come from the "
        "configure_budget tool / ~/.lightning-enable/config.json."
    )
