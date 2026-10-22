# syntax=docker/dockerfile:1.7
# Lightning Enable MCP Server
# The payment layer for AI agents
#
# Supply-chain notes (2026-10-22 installer/CA remediation):
#  - The base image is pinned by digest. A floating `python:3.12-slim` tag says nothing
#    about which pip or CA bundle is inside; the digest does. Refresh it deliberately.
#  - pip and certifi are replaced from SHA256-verified wheels BEFORE anything is
#    installed, and pip runs FROM the verified wheel so the image's shipped pip is never
#    executed. Bump the three PIN_* args together when a new pip/certifi is approved.
#  - The package version is passed in (MCP_VERSION) so the image provably contains the
#    release it is tagged with. Left empty (docker-compose, local builds) it installs the
#    newest release, as before.
#  - Build stage and final runtime are verified independently (two RUN checks, the second
#    as the unprivileged runtime user).
ARG PYTHON_BASE=python:3.12-slim@sha256:cb33f7f771ecec2ef1225cf39b159591a040391bd73e4c01f00fdfc4c47611d2
FROM ${PYTHON_BASE}

LABEL org.opencontainers.image.title="Lightning Enable MCP"
LABEL org.opencontainers.image.description="Pay Lightning invoices and access L402-protected APIs from your AI agent"
LABEL org.opencontainers.image.url="https://lightningenable.com"
LABEL org.opencontainers.image.source="https://github.com/refined-element/lightning-enable-mcp"
LABEL org.opencontainers.image.vendor="Lightning Enable"
LABEL org.opencontainers.image.licenses="MIT"
LABEL io.modelcontextprotocol.server.name="io.github.refined-element/lightning-enable-mcp"

ARG PIN_PIP_VERSION=26.2.2
ARG PIN_PIP_SHA256=fd7f4dfa8190e014013b9f08f3e973f4e4c26ffd179a204895533a7254813a70
ARG PIN_CERTIFI_VERSION=2026.10.19
ARG PIN_CERTIFI_SHA256=42d35340cc5b9ba78e818ddbff0c5b1c2b779de448a340fd1b989f5fa8670e1b

# BuildKit fetches these and refuses the layer unless the checksum matches, so
# integrity does not depend on the download channel.
ADD --checksum=sha256:fd7f4dfa8190e014013b9f08f3e973f4e4c26ffd179a204895533a7254813a70 \
    https://files.pythonhosted.org/packages/3f/06/3e4420302363fbb71be3c7f35ce042f54bfca330e3e66b3a2e3c4cca579f/pip-26.2.2-py3-none-any.whl \
    /tmp/wheels/pip-26.2.2-py3-none-any.whl
ADD --checksum=sha256:42d35340cc5b9ba78e818ddbff0c5b1c2b779de448a340fd1b989f5fa8670e1b \
    https://files.pythonhosted.org/packages/c4/51/29046e48f693c54e5b7412216d447a1d8362c9653148d98c02cdf65fa733/certifi-2026.10.19-py3-none-any.whl \
    /tmp/wheels/certifi-2026.10.19-py3-none-any.whl

# Bootstrap the trusted installer FIRST: pip runs from inside the verified wheel.
RUN python /tmp/wheels/pip-${PIN_PIP_VERSION}-py3-none-any.whl/pip install \
        --isolated --disable-pip-version-check --no-index --no-deps --upgrade --force-reinstall \
        /tmp/wheels/pip-${PIN_PIP_VERSION}-py3-none-any.whl \
        /tmp/wheels/certifi-${PIN_CERTIFI_VERSION}-py3-none-any.whl \
    && rm -rf /tmp/wheels

# Build-stage verification: the installers that will fetch the dependencies below.
RUN python - "$PIN_PIP_VERSION" "$PIN_CERTIFI_VERSION" <<'PY'
import importlib.metadata as m, sys
from pip._vendor import certifi as vendored
want_pip, want_certifi = sys.argv[1], sys.argv[2]
got = {"pip": m.version("pip"), "certifi": m.version("certifi"), "pip._vendor.certifi": vendored.__version__}
print("build-stage installers:", got)
if got["pip"] != want_pip or got["certifi"] != want_certifi or got["pip._vendor.certifi"] != want_certifi:
    sys.exit("installer/CA bundle is not the pinned version after bootstrap")
PY

# Install system dependencies for cryptography and secp256k1
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    libffi-dev \
    pkg-config \
    libsecp256k1-dev \
    && rm -rf /var/lib/apt/lists/*

# Install the MCP server (exact release when MCP_VERSION is given)
ARG MCP_VERSION=""
RUN python -m pip install --no-cache-dir "lightning-enable-mcp${MCP_VERSION:+==$MCP_VERSION}"

# Create non-root user for security
RUN useradd -m -s /bin/bash mcpuser
USER mcpuser
WORKDIR /home/mcpuser

# Runtime verification, as the user the server runs as: the final image still carries
# the pinned installers/CA bundle and, when a version was requested, exactly that release.
# (Single build stage: the ARGs declared above are still in scope here.)
RUN python - "$PIN_PIP_VERSION" "$PIN_CERTIFI_VERSION" "$MCP_VERSION" <<'PY'
import importlib.metadata as m, sys
from pip._vendor import certifi as vendored
want_pip, want_certifi, want_mcp = sys.argv[1], sys.argv[2], sys.argv[3]
got = {"pip": m.version("pip"), "certifi": m.version("certifi"),
       "pip._vendor.certifi": vendored.__version__, "lightning-enable-mcp": m.version("lightning-enable-mcp")}
print("runtime installers:", got)
if got["pip"] != want_pip or got["certifi"] != want_certifi or got["pip._vendor.certifi"] != want_certifi:
    sys.exit("final image installer/CA bundle is not the pinned version")
if want_mcp and got["lightning-enable-mcp"] != want_mcp:
    sys.exit(f"final image has lightning-enable-mcp {got['lightning-enable-mcp']}, expected {want_mcp}")
PY

# Create config directory
RUN mkdir -p /home/mcpuser/.lightning-enable

# Environment variables (to be overridden at runtime)
ENV NWC_CONNECTION_STRING=""
ENV OPENNODE_API_KEY=""
ENV STRIKE_API_KEY=""

# MCP servers communicate via stdio
ENTRYPOINT ["lightning-enable-mcp"]
