"""
Package version and the User-Agent values built from it.

A leaf module (no package imports) so every module can read the version at import time
without a circular import through ``__init__``.
"""

from importlib.metadata import PackageNotFoundError as _PackageNotFoundError
from importlib.metadata import version as _pkg_version

# Derive the version from installed package metadata so it can never drift from
# pyproject.toml (the single source of truth). Fall back to a sentinel when running
# directly from an uninstalled source checkout (for example, without pip install).
try:
    __version__ = _pkg_version("lightning-enable-mcp")
except _PackageNotFoundError:  # pragma: no cover
    __version__ = "0.0.0+unknown"

#: User-Agent for calls to the Lightning Enable API and for paid L402 fetches. Server-side
#: analytics attribute traffic by it; keep the product token spelling exactly as is.
USER_AGENT_PYTHON = f"LightningEnable-MCP-Python/{__version__}"

#: User-Agent for the confirmation webhook and discover_api registry/manifest fetches. Same
#: product token as the .NET port; keep the spelling exactly as is.
USER_AGENT_MCP = f"LightningEnable-MCP/{__version__}"
