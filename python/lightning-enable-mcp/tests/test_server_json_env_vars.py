"""Guard: published manifests must not advertise env vars the server no longer reads.

The legacy ``BudgetManager`` and its ``L402_MAX_SATS_PER_REQUEST`` /
``L402_MAX_SATS_PER_SESSION`` env vars were removed (see the comment in
``server.py._initialize_services``). Runtime sats caps are now tightened via the
``configure_budget`` tool, sourced from ``~/.lightning-enable/config.json`` (USD
limits converted to sats). Neither the Python nor the .NET server reads those env
vars anymore, so no published manifest may advertise them: doing so promises the
operator a spending cap that silently does nothing — a funds-safety hazard.

``server.json`` (the official MCP Registry source) and ``smithery.yaml`` both live
at the monorepo root, three levels above this ``tests/`` directory.
"""

import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
SERVER_JSON = REPO_ROOT / "server.json"
SMITHERY_YAML = REPO_ROOT / "smithery.yaml"

# Env vars removed from the code — must not appear in any published manifest.
REMOVED_ENV_VARS = {"L402_MAX_SATS_PER_REQUEST", "L402_MAX_SATS_PER_SESSION"}


def test_server_json_does_not_advertise_removed_env_vars():
    """server.json must not declare budget-cap env vars the server ignores."""
    assert SERVER_JSON.exists(), f"server.json not found at {SERVER_JSON}"
    data = json.loads(SERVER_JSON.read_text(encoding="utf-8"))

    offenders = []
    for package in data.get("packages", []):
        registry = package.get("registryType", "?")
        for env in package.get("environmentVariables", []):
            if env.get("name") in REMOVED_ENV_VARS:
                offenders.append(f"{registry}:{env['name']}")

    assert not offenders, (
        "server.json advertises budget-cap env vars the server no longer reads "
        f"(they silently no-op): {offenders}. Caps now come from the "
        "configure_budget tool / ~/.lightning-enable/config.json."
    )


def test_smithery_yaml_does_not_advertise_removed_env_vars():
    """smithery.yaml must not map removed budget-cap env vars for the same reason."""
    assert SMITHERY_YAML.exists(), f"smithery.yaml not found at {SMITHERY_YAML}"
    text = SMITHERY_YAML.read_text(encoding="utf-8")

    offenders = sorted(name for name in REMOVED_ENV_VARS if name in text)

    assert not offenders, (
        "smithery.yaml still references budget-cap env vars the server no longer "
        f"reads (they silently no-op): {offenders}. Caps now come from the "
        "configure_budget tool / ~/.lightning-enable/config.json."
    )
