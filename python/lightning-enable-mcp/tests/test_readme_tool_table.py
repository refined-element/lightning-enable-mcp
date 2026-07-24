"""
Drift guard for the root README's canonical Tools table.

The advertised tool inventory (README tables, docs, marketing) has drifted from the
code repeatedly. ``test_server.py`` / ``ToolInventoryTests.cs`` already pin the tool
SET to the code; this guard pins the human-facing *root README table* to that same
inventory, so a hand-edited README (a mislabeled "Free" row, a forgotten tool, a
wrong count) fails CI instead of silently shipping.

It parses the ``## Tools`` table in the repo-root ``README.md`` and asserts the tool
names and the free / API-key-gated split exactly match ``FREE_TOOLS`` / ``API_KEY_TOOLS``
from ``test_server.py`` (the single source of truth the .NET guard also mirrors).

Kept deliberately simple: match tool names in the first column and their Access cell.
No markdown library, no network.
"""

import importlib.util
import re
from pathlib import Path

import pytest

# ── Load the canonical inventory from the sibling guard test by path ───────────
# Importing by file path (not `from tests.test_server import ...`) keeps this robust
# regardless of how pytest resolves the `tests` package / rootdir.
_TS_PATH = Path(__file__).with_name("test_server.py")
_spec = importlib.util.spec_from_file_location("_canonical_tool_inventory", _TS_PATH)
assert _spec and _spec.loader
_ts = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_ts)
FREE_TOOLS: set[str] = set(_ts.FREE_TOOLS)
API_KEY_TOOLS: set[str] = set(_ts.API_KEY_TOOLS)

# Access-cell label the README uses for the API-key-gated tools.
GATED_LABEL = "Agentic Commerce"
FREE_LABEL = "Free"

# A table row: | `tool_name` | Access | description |
_ROW = re.compile(
    r"^\|\s*`(?P<name>[a-z0-9_]+)`\s*\|\s*(?P<access>[^|]+?)\s*\|",
)


def _find_root_readme() -> Path | None:
    """Locate the repo-root README.md. Known layout: repo/python/<pkg>/tests/<this>."""
    here = Path(__file__).resolve()
    # Fixed relative location first, then an upward search as a fallback.
    candidates = [here.parents[3] / "README.md"] if len(here.parents) > 3 else []
    candidates += [p / "README.md" for p in here.parents]
    for candidate in candidates:
        if candidate.is_file() and "## Tools" in candidate.read_text(encoding="utf-8"):
            return candidate
    return None


def _parse_tools_table(readme_text: str) -> dict[str, str]:
    """Return {tool_name: access_label} for every row of the ``## Tools`` table.

    Every row whose first column is a backticked tool name is captured. If such a
    row carries an Access label that is neither "Free" nor "Agentic Commerce", it is
    a *mislabel* ("Free*", "**Free**", a lowercase or footnote variant, …) — fail
    loudly naming the tool and the offending label. Silently dropping the row (the
    old behaviour) made a mislabeled tool masquerade as *missing from README* and hid
    the real defect — the exact ASA-relabeled-"Free" bug commit 039dab9 hand-fixed.
    """
    lines = readme_text.splitlines()
    # Scope to the "## Tools" section so unrelated backticked names elsewhere
    # (e.g. the ASA how-it-works prose) can't leak into the parse.
    start = next((i for i, ln in enumerate(lines) if ln.strip() == "## Tools"), None)
    assert start is not None, "root README has no '## Tools' section"
    end = next(
        (i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")),
        len(lines),
    )

    tools: dict[str, str] = {}
    for line in lines[start:end]:
        m = _ROW.match(line)
        if not m:
            continue
        name = m.group("name")
        access = m.group("access").strip()
        # A backticked tool row MUST carry one of the two accepted Access labels.
        # Anything else is a mislabel — report the tool AND its bad label rather
        # than silently excluding the row.
        assert access in (FREE_LABEL, GATED_LABEL), (
            f"root README '## Tools' row for `{name}` has an unrecognized Access "
            f"label {access!r} — expected {FREE_LABEL!r} or {GATED_LABEL!r}. "
            "A label variant (e.g. 'Free*', '**Free**', lowercase, a footnote) must "
            "not be used: it would otherwise be dropped and misreported as the tool "
            "being missing from the README table."
        )
        tools[name] = access
    return tools


@pytest.fixture(scope="module")
def tools_table() -> dict[str, str]:
    readme = _find_root_readme()
    if readme is None:
        pytest.skip("root README.md not found — drift guard only runs inside the repo checkout")
    return _parse_tools_table(readme.read_text(encoding="utf-8"))


def test_readme_tool_names_match_inventory(tools_table: dict[str, str]) -> None:
    """Every tool in the README table is a real tool, and none are missing."""
    readme_tools = set(tools_table)
    expected = FREE_TOOLS | API_KEY_TOOLS
    assert readme_tools == expected, (
        "root README '## Tools' table drifted from the canonical inventory "
        f"(test_server.py). Missing from README: {sorted(expected - readme_tools)}; "
        f"extra in README: {sorted(readme_tools - expected)}"
    )


def test_readme_free_gated_split_matches_inventory(tools_table: dict[str, str]) -> None:
    """The Free / Agentic Commerce labels match the free / API-key split exactly."""
    readme_free = {name for name, access in tools_table.items() if access == FREE_LABEL}
    readme_gated = {name for name, access in tools_table.items() if access == GATED_LABEL}

    mislabeled_gated_as_free = readme_free & API_KEY_TOOLS
    assert not mislabeled_gated_as_free, (
        "README marks API-key-gated tools as 'Free': "
        f"{sorted(mislabeled_gated_as_free)} require LIGHTNING_ENABLE_API_KEY"
    )
    mislabeled_free_as_gated = readme_gated & FREE_TOOLS
    assert not mislabeled_free_as_gated, (
        "README marks free tools as 'Agentic Commerce': "
        f"{sorted(mislabeled_free_as_gated)} work out of the box"
    )
    assert readme_free == FREE_TOOLS
    assert readme_gated == API_KEY_TOOLS


def test_readme_tool_counts_are_canonical(tools_table: dict[str, str]) -> None:
    """26 total = 17 free + 9 gated, matching the guard tests."""
    free = sum(1 for a in tools_table.values() if a == FREE_LABEL)
    gated = sum(1 for a in tools_table.values() if a == GATED_LABEL)
    assert free == 17, f"expected 17 free tools in README, found {free}"
    assert gated == 9, f"expected 9 gated tools in README, found {gated}"
    assert free + gated == 26, f"expected 26 tools total in README, found {free + gated}"
