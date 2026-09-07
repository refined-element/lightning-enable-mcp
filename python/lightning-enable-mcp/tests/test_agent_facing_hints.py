"""Drift guard: no agent-facing string may steer an agent to a DIFFERENT, deprecated tool.

The 2026-09 consolidation folded 16 single-purpose tools into five action verbs. The old
names still dispatch, so a hint that names one is not *broken* — it just steers the agent
onto the deprecated path, which comes back carrying a deprecation marker and costs a round
trip. Those hints outlived the rename once already; this stops them coming back.

Scope, deliberately narrow — it fires only on a string a MODEL reads:

* Docstrings are excluded. They are developer-facing, and a module that documents its own
  history ("configure_budget -> BudgetService.configure_budget") is not instructing anyone.
* A module's own tool name is excluded. The confirmation flow tells the agent to call the
  SAME tool again with a nonce; that is a re-call, not a cross-tool hint, and it is correct
  whatever name the agent used to get there.
* A bare mention is fine. The tool's ``name="..."`` schema field, the alias table, and the
  profile lists all name deprecated tools legitimately — only a CALL shape
  (``old_name(``) or an imperative (``use old_name``) counts.

Mirrors ``dotnet/tests/LightningEnable.Mcp.Tests/AgentFacingHintTests.cs``.
"""

import ast
import re
from pathlib import Path

import pytest

from lightning_enable_mcp.tools.profiles import LEGACY_TOOL_NAMES

#: Where an agent-facing message can be written.
_SOURCE_ROOT = Path(__file__).resolve().parents[1] / "src" / "lightning_enable_mcp"

#: Files whose legacy-name mentions are structural, not guidance.
_EXEMPT_FILENAMES = {
    "profiles.py",     # the list of legacy names IS the point
    "registry.py",     # imports the legacy schemas
    "annotations.py",  # the annotation table is keyed by tool name
    "server.py",       # DEPRECATED_ALIASES maps old name -> new call
    "__init__.py",     # re-exports
}

#: Verbs that turn a mention into an instruction.
_IMPERATIVES = ("use", "call", "check", "run", "via", "see")


def _hint_patterns(name: str) -> list[re.Pattern[str]]:
    return [
        re.compile(rf"\b{re.escape(name)}\s*\("),
        *[re.compile(rf"\b{verb}\s+{re.escape(name)}\b", re.IGNORECASE) for verb in _IMPERATIVES],
    ]


def _docstring_node_ids(tree: ast.AST) -> set[int]:
    """Ids of the string constants that are docstrings — developer-facing, not agent-facing."""
    ids: set[int] = set()
    for node in ast.walk(tree):
        if not isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            continue
        body = getattr(node, "body", None)
        if not body:
            continue
        first = body[0]
        if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant):
            if isinstance(first.value.value, str):
                ids.add(id(first.value))
    return ids


def _model_facing_literals(path: Path) -> list[tuple[int, str]]:
    """String constants a model could read: every literal except the docstrings."""
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    docstrings = _docstring_node_ids(tree)
    return [
        (node.lineno, node.value)
        for node in ast.walk(tree)
        if isinstance(node, ast.Constant)
        and isinstance(node.value, str)
        and id(node) not in docstrings
    ]


def _source_files() -> list[Path]:
    return [p for p in _SOURCE_ROOT.rglob("*.py") if p.name not in _EXEMPT_FILENAMES]


def _owning_file(legacy_name: str) -> Path | None:
    """The module that DECLARES ``legacy_name`` as a tool.

    That is the one place a mention is a re-call of the same tool rather than a cross-tool
    hint. Derived from the source (``name="..."`` on the ``Tool`` schema) rather than the
    module name, because several legacy tools share a module (``budget.py`` declares both
    ``configure_budget`` and ``get_payment_history``).
    """
    needle = f'name="{legacy_name}"'
    for path in _source_files():
        if needle in path.read_text(encoding="utf-8"):
            return path
    return None


@pytest.mark.parametrize("legacy_name", sorted(LEGACY_TOOL_NAMES))
def test_no_string_literal_steers_an_agent_to_a_deprecated_tool(legacy_name: str) -> None:
    patterns = _hint_patterns(legacy_name)
    owner = _owning_file(legacy_name)
    assert owner is not None, (
        f"'{legacy_name}' must be declared by some tool schema — if it is not, this guard is "
        "scanning the wrong tree"
    )
    offenders: list[str] = []

    for path in _source_files():
        # A module's own tool name is a re-call, not a cross-tool hint (see the module
        # docstring) — the confirmation flow legitimately says "call <this tool> again".
        if path == owner:
            continue
        for lineno, literal in _model_facing_literals(path):
            if any(pattern.search(literal) for pattern in patterns):
                offenders.append(f"{path.name}:{lineno}: {literal.strip()[:110]}")

    assert not offenders, (
        f"'{legacy_name}' is deprecated, but these strings still tell an agent to call it. "
        "Point them at the consolidated verb instead (see the 'Old name -> new call' table "
        "in the root README):\n  " + "\n  ".join(offenders)
    )


def test_the_guard_would_actually_catch_a_regression() -> None:
    """A guard that cannot fail is not a guard."""
    patterns = _hint_patterns("settle_agent_service")

    assert any(p.search('Use settle_agent_service(l402_endpoint="x") to pay.') for p in patterns)
    assert any(p.search("settle via settle_agent_service.") for p in patterns)
    # A bare mention is fine — that is how the alias table and the schemas name the tool.
    assert not any(p.search('"settle_agent_service"') for p in patterns)


def test_the_guard_actually_reads_some_files() -> None:
    """Guard the guard: an exemption typo must not silently empty the scan."""
    files = _source_files()
    assert len(files) > 20
    assert any(_model_facing_literals(p) for p in files)
