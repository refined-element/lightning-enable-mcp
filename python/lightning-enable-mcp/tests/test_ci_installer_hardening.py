"""Pins the installer-bootstrap hardening of this repository's CI/CD (2026-10-22).

Stdlib only (no PyYAML): the workflows use consistent two-space indentation, which is
enough to split jobs and steps. Every job that installs anything with pip must first run
the `trusted-pip` composite action, which replaces the runner's pip/certifi with pinned,
SHA256-verified wheels. The action itself must carry matching pins. When a Dockerfile is
present it must pin its base image by digest, fetch the wheels with `ADD --checksum`, run
the bootstrap before any other install, and verify the final runtime after `USER`.

Set LE_CI_HARDENING_ROOT to check a different tree (used to prove the test fails on the
unpatched workflows).
"""
from __future__ import annotations

import os
import re
from pathlib import Path

import pytest

PIN_PIP = "26.2.2"
PIN_PIP_SHA256 = "fd7f4dfa8190e014013b9f08f3e973f4e4c26ffd179a204895533a7254813a70"
PIN_CERTIFI = "2026.10.19"
PIN_CERTIFI_SHA256 = "42d35340cc5b9ba78e818ddbff0c5b1c2b779de448a340fd1b989f5fa8670e1b"
ACTION_USE = "./.github/actions/trusted-pip"
# `pip install ...`, `pip3 install`, `python -m pip install`; group 1 is set only for the -m form.
PIP_INSTALL = re.compile(r"(?<![\w./-])(python(?:3)?\s+-m\s+)?pip(?:3)?\s+install\b")


def repo_root() -> Path:
    override = os.environ.get("LE_CI_HARDENING_ROOT")
    if override:
        return Path(override)
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / ".github").is_dir():
            return parent
    raise AssertionError("no .github directory above this test file")


ROOT = repo_root()
WORKFLOWS = sorted((ROOT / ".github" / "workflows").glob("*.yml"))
ACTION = ROOT / ".github" / "actions" / "trusted-pip" / "action.yml"
DOCKERFILES = sorted(p for p in ROOT.rglob("Dockerfile") if ".git" not in p.parts)


def split_jobs(text: str) -> dict[str, list[str]]:
    """Return {job_name: lines} for a GitHub workflow using two-space indentation."""
    jobs: dict[str, list[str]] = {}
    in_jobs, current = False, None
    for line in text.splitlines():
        if re.match(r"^jobs:\s*$", line):
            in_jobs, current = True, None
            continue
        if in_jobs and re.match(r"^\S", line):  # another top-level key ends the jobs map
            in_jobs = False
        if not in_jobs:
            continue
        m = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if m:
            current = m.group(1)
            jobs[current] = []
            continue
        if current:
            jobs[current].append(line)
    return jobs


def is_command(line: str) -> bool:
    """A shell line that could actually run pip: not a YAML/shell comment, not an echo."""
    stripped = line.strip()
    return bool(stripped) and not stripped.startswith("#") and not stripped.startswith("echo ")


def first_index(lines: list[str], pattern) -> int | None:
    for i, line in enumerate(lines):
        if not isinstance(pattern, str) and not is_command(line):
            continue
        if isinstance(pattern, str):
            if pattern in line:
                return i
        elif pattern.search(line):
            return i
    return None


def _pip_jobs():
    for wf in WORKFLOWS:
        for name, lines in split_jobs(wf.read_text(encoding="utf-8")).items():
            if any(PIP_INSTALL.search(l) for l in lines if is_command(l)):
                yield wf, name, lines


PIP_JOBS = list(_pip_jobs())
PIP_JOB_IDS = [f"{w.name}:{j}" for w, j, _ in PIP_JOBS]


def test_workflows_exist():
    assert WORKFLOWS, f"no workflows under {ROOT}"


def test_pip_jobs_were_found():
    assert PIP_JOBS, "expected at least one job installing with pip"


def test_trusted_pip_action_exists_with_expected_pins():
    assert ACTION.is_file(), f"missing {ACTION}"
    text = ACTION.read_text(encoding="utf-8")
    assert f"PIN_PIP_VERSION: '{PIN_PIP}'" in text
    assert f"PIN_PIP_SHA256: {PIN_PIP_SHA256}" in text
    assert f"PIN_CERTIFI_VERSION: '{PIN_CERTIFI}'" in text
    assert f"PIN_CERTIFI_SHA256: {PIN_CERTIFI_SHA256}" in text
    assert f"/pip-{PIN_PIP}-py3-none-any.whl" in text
    assert f"/certifi-{PIN_CERTIFI}-py3-none-any.whl" in text
    assert "--no-index" in text and "--isolated" in text, "bootstrap must not touch an index"
    assert "pip._vendor import certifi" in text, "must verify pip's vendored CA bundle, not only certifi"
    assert "https://files.pythonhosted.org/" in text
    assert "http://" not in text, "plain-http URL in the bootstrap"


@pytest.mark.parametrize("wf,job,lines", PIP_JOBS, ids=PIP_JOB_IDS)
def test_every_pip_installing_job_bootstraps_first(wf, job, lines):
    boot = first_index(lines, f"uses: {ACTION_USE}")
    install = first_index(lines, PIP_INSTALL)
    assert boot is not None, f"{wf.name} job '{job}' installs with pip but never runs {ACTION_USE}"
    assert boot < install, (
        f"{wf.name} job '{job}' runs pip install (line {install}) before the bootstrap (line {boot})")
    setup = first_index(lines, "actions/setup-python@")
    if setup is not None:
        assert setup < boot, f"{wf.name} job '{job}': bootstrap must run after setup-python"


@pytest.mark.parametrize("wf,job,lines", PIP_JOBS, ids=PIP_JOB_IDS)
def test_pip_is_invoked_through_the_bootstrapped_interpreter(wf, job, lines):
    for line in filter(is_command, lines):
        m = PIP_INSTALL.search(line)
        if m:
            assert m.group(1), (
                f"{wf.name} job '{job}': use `python -m pip install` so the pinned pip runs: {line.strip()}")


@pytest.mark.parametrize("wf,job,lines", PIP_JOBS, ids=PIP_JOB_IDS)
def test_no_floating_pip_self_upgrade(wf, job, lines):
    for line in filter(is_command, lines):
        assert not re.search(r"pip install (-U |--upgrade )pip\b", line), (
            f"{wf.name} job '{job}': a floating pip self-upgrade undoes the pin: {line.strip()}")


@pytest.mark.skipif(not DOCKERFILES, reason="no Dockerfile in this repository")
@pytest.mark.parametrize("dockerfile", DOCKERFILES, ids=[str(d.relative_to(ROOT)) for d in DOCKERFILES])
def test_dockerfile_hardening(dockerfile):
    text = dockerfile.read_text(encoding="utf-8")
    lines = text.splitlines()
    assert lines[0].startswith("# syntax=docker/dockerfile:1."), (
        "ADD --checksum and RUN heredocs need the syntax directive on line 1")

    froms = [l for l in lines if l.startswith("FROM ")]
    assert froms, "no FROM"
    for f in froms:
        ref = f.split()[1]
        if ref.startswith("${"):
            var = ref.strip("${}").split(":")[0]
            arg = next((l for l in lines if l.startswith(f"ARG {var}=")), None)
            assert arg, f"FROM uses {ref} but no default ARG {var}"
            ref = arg.split("=", 1)[1]
        assert re.search(r"@sha256:[0-9a-f]{64}$", ref), f"base image not pinned by digest: {ref}"

    assert f"--checksum=sha256:{PIN_PIP_SHA256}" in text, "pip wheel not fetched with ADD --checksum"
    assert f"--checksum=sha256:{PIN_CERTIFI_SHA256}" in text, "certifi wheel not fetched with ADD --checksum"
    assert f"ARG PIN_PIP_VERSION={PIN_PIP}" in text and f"ARG PIN_PIP_SHA256={PIN_PIP_SHA256}" in text
    assert f"ARG PIN_CERTIFI_VERSION={PIN_CERTIFI}" in text and f"ARG PIN_CERTIFI_SHA256={PIN_CERTIFI_SHA256}" in text
    assert "http://" not in text, "plain-http URL in the Dockerfile"

    # Split RUN instructions (with continuations and heredocs) into (line_no, body) blocks.
    runs: list[tuple[int, str]] = []
    i = 0
    while i < len(lines):
        if lines[i].startswith("RUN "):
            start, body = i, lines[i]
            heredoc = re.search(r"<<-?'?(\w+)'?", lines[i])
            i += 1
            if heredoc:
                term = heredoc.group(1)
                while i < len(lines) and lines[i].strip() != term:
                    body += "\n" + lines[i]; i += 1
            else:
                while body.rstrip().endswith("\\") and i < len(lines):
                    body += "\n" + lines[i]; i += 1
            runs.append((start, body))
        else:
            i += 1

    bootstrap = [n for n, b in runs if "-py3-none-any.whl/pip install" in b and "--no-index" in b and "--isolated" in b]
    assert len(bootstrap) == 1, "expected exactly one bootstrap RUN running pip from the verified wheel with --no-index"
    bootstrap = bootstrap[0]
    installs = [n for n, b in runs if PIP_INSTALL.search(b) and n != bootstrap]
    assert installs, "expected a dependency install"
    assert all(n > bootstrap for n in installs), "a pip install runs before the bootstrap"
    assert all("python -m pip install" in b for n, b in runs if n in installs), "use `python -m pip install`"
    assert all("apt-get" not in b or n > bootstrap for n, b in runs), "bootstrap must precede apt-get too"

    user = next((n for n, l in enumerate(lines) if l.startswith("USER ")), None)
    assert user is not None, "no USER switch"
    assert all(n < user for n in installs), "installs after USER would run as the runtime user"

    verifies = [n for n, b in runs if "pip._vendor import certifi" in b]
    assert len(verifies) >= 2, "expected a build-stage and a runtime verification"
    assert min(verifies) < min(installs) < user < max(verifies), (
        "verification must run once before dependency installs and once after USER")
    assert "${MCP_VERSION:+==$MCP_VERSION}" in text, "release version must be pinnable through MCP_VERSION"
