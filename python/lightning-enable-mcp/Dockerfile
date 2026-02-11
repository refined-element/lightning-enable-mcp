# Lightning Enable MCP Server
# The payment layer for AI agents
FROM python:3.12-slim

LABEL org.opencontainers.image.title="Lightning Enable MCP"
LABEL org.opencontainers.image.description="Pay Lightning invoices and access L402-protected APIs from your AI agent"
LABEL org.opencontainers.image.url="https://lightningenable.com"
LABEL org.opencontainers.image.source="https://github.com/refined-element/lightning-enable-mcp"
LABEL org.opencontainers.image.vendor="Lightning Enable"
LABEL org.opencontainers.image.licenses="MIT"
LABEL io.modelcontextprotocol.server.name="io.github.refined-element/lightning-enable-mcp"

# Install system dependencies for cryptography and secp256k1
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    libffi-dev \
    pkg-config \
    libsecp256k1-dev \
    && rm -rf /var/lib/apt/lists/*

# Install the MCP server
RUN pip install --no-cache-dir lightning-enable-mcp

# Create non-root user for security
RUN useradd -m -s /bin/bash mcpuser
USER mcpuser
WORKDIR /home/mcpuser

# Create config directory
RUN mkdir -p /home/mcpuser/.lightning-enable

# Environment variables (to be overridden at runtime)
ENV NWC_CONNECTION_STRING=""
ENV OPENNODE_API_KEY=""
ENV STRIKE_API_KEY=""
ENV L402_MAX_SATS_PER_REQUEST="1000"
ENV L402_MAX_SATS_PER_SESSION="10000"

# MCP servers communicate via stdio
ENTRYPOINT ["lightning-enable-mcp"]
