"""MCPB entry point.

Launches the Lightning Enable MCP server, which uv installs from PyPI (see the
bundle's pyproject.toml) along with its native dependencies. This thin shim just
hands off to the package's own console entry point.
"""

from lightning_enable_mcp.server import main

if __name__ == "__main__":
    main()
