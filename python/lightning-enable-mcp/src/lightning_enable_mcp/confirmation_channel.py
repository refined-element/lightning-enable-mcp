"""
Approval channels for out-of-band payment confirmation.

An over-threshold payment needs a human's approval, delivered as a short code the model
must never see. Printing that code to stderr is right for a local server with a human at
the terminal — and wrong for a hosted one (a claude.ai connector, a Docker container, a
fleet), where nobody reads stderr and, on a shared host, the agent might. So the
destination is explicit and configurable:

* ``stderr``  — print to the server console. The historical local default, unchanged.
* ``refuse``  — refuse over-threshold payments outright. NO code is minted at all. The safe
  posture for a hosted deployment.
* ``webhook`` — POST the pending confirmation to an operator URL, HMAC signed. The human
  approves through their own system and relays the code back to the agent as usual.
* ``file``    — append the same JSON line to a file the operator tails (0600 on POSIX).

Selection precedence: ``LIGHTNING_ENABLE_CONFIRMATION_CHANNEL`` > ``confirmation.channel``
in ``~/.lightning-enable/config.json`` > automatic. Automatic keeps ``stderr`` unless stdin
is not a TTY *and* the operator opted into the hosted posture with
``LIGHTNING_ENABLE_HOSTED=1``, in which case it is ``refuse``.

Two invariants hold on every channel:

1. The code never appears in a tool result. Only the operator receives it.
2. A payment is never approved because its notification could not be delivered — a delivery
   failure is a REFUSAL, and the minted code is withdrawn.

Mirrors the .NET ``ConfirmationChannelResolver`` / ``ConfirmationChannels`` /
``ConfirmationChannelFactory``; the two ports must stay in sync.
"""

from __future__ import annotations

import hashlib
import hmac
import ipaddress
import json
import os
import stat
import sys
import time
from collections.abc import Callable
from dataclasses import dataclass
from decimal import Decimal
from enum import Enum
from pathlib import Path
from typing import TYPE_CHECKING, Protocol
from urllib.parse import urlsplit

import httpx

if TYPE_CHECKING:  # pragma: no cover - typing only, avoids a runtime import cycle
    from .budget_service import PendingConfirmation
    from .config import ConfirmationSettings

# --------------------------------------------------------------------------------------
# Names
# --------------------------------------------------------------------------------------

CHANNEL_ENV_VAR = "LIGHTNING_ENABLE_CONFIRMATION_CHANNEL"
"""Environment variable that overrides ``confirmation.channel``."""

HOSTED_ENV_VAR = "LIGHTNING_ENABLE_HOSTED"
"""Environment variable that opts a deployment into the hosted (refuse-by-default) posture."""

WEBHOOK_URL_ENV_VAR = "LIGHTNING_ENABLE_CONFIRMATION_WEBHOOK_URL"
WEBHOOK_SECRET_ENV_VAR = "LIGHTNING_ENABLE_CONFIRMATION_WEBHOOK_SECRET"
FILE_PATH_ENV_VAR = "LIGHTNING_ENABLE_CONFIRMATION_FILE"

VALID_CHANNELS = "stderr | refuse | webhook | file"

DESTINATION_SUMMARY_LENGTH = 40
"""Longest destination prefix carried in a payload — enough to recognise, not the whole blob."""


class ConfirmationChannelKind(str, Enum):
    """Where an over-threshold confirmation code is delivered."""

    STDERR = "stderr"
    REFUSE = "refuse"
    WEBHOOK = "webhook"
    FILE = "file"


# --------------------------------------------------------------------------------------
# Value objects
# --------------------------------------------------------------------------------------


@dataclass(frozen=True)
class ConfirmationRequest:
    """What a payment tool asks the approval channel to deliver.

    Everything here is operator-facing; none of it — above all no confirmation code —
    is returned to the model.
    """

    amount_sats: int
    amount_usd: Decimal
    tool_name: str
    description: str
    """Display string for the target (may be truncated / redacted)."""
    destination: str
    """The exact payment target the code authorizes. Bound and checked on consume."""
    title: str
    """Banner for the stderr channel, e.g. ``PAYMENT CONFIRMATION REQUIRED``."""
    summary: str
    """One-line human summary, e.g. ``pay_invoice — $50.00 (50,000 sats), invoice lnbc…``."""


@dataclass(frozen=True)
class DeliveryResult:
    """Outcome of handing one pending confirmation to a channel."""

    success: bool
    error: str | None = None

    @classmethod
    def ok(cls) -> DeliveryResult:
        return cls(success=True)

    @classmethod
    def fail(cls, error: str) -> DeliveryResult:
        return cls(success=False, error=error)


@dataclass(frozen=True)
class ConfirmationDispatchResult:
    """What a payment tool gets back from ``BudgetService.request_confirmation``.

    Either a pending confirmation whose code went out on the configured channel, or a
    refusal. A refusal never leaves a usable code behind.
    """

    delivered: bool
    channel: ConfirmationChannelKind
    pending: PendingConfirmation | None = None
    operator_hint: str | None = None
    refusal_reason: str | None = None

    @property
    def channel_name(self) -> str:
        """Lower-case channel name for tool results and logs."""
        return self.channel.value

    @classmethod
    def refused(
        cls, channel: ConfirmationChannelKind, reason: str
    ) -> ConfirmationDispatchResult:
        return cls(delivered=False, channel=channel, refusal_reason=reason)

    @classmethod
    def delivered_to(
        cls,
        channel: ConfirmationChannelKind,
        pending: PendingConfirmation,
        operator_hint: str,
    ) -> ConfirmationDispatchResult:
        return cls(
            delivered=True, channel=channel, pending=pending, operator_hint=operator_hint
        )


@dataclass(frozen=True)
class ConfirmationChannelResolution:
    """The channel that was chosen, where the choice came from, and any startup warning."""

    kind: ConfirmationChannelKind
    source: str
    """``env``, ``config`` or ``auto`` — plus ``-invalid`` when a value was rejected."""
    warning: str | None = None


# --------------------------------------------------------------------------------------
# Payload + signature (shared by the webhook and file channels)
# --------------------------------------------------------------------------------------


def summarize_destination(destination: str | None) -> str:
    """Truncate a payment destination to a recognisable prefix."""
    value = (destination or "").strip()
    if len(value) <= DESTINATION_SUMMARY_LENGTH:
        return value
    return value[:DESTINATION_SUMMARY_LENGTH] + "..."


def build_payload(pending: PendingConfirmation, request: ConfirmationRequest) -> str:
    """Build the canonical one-line JSON body for a pending confirmation.

    One shape for both the webhook and the file channel, so an operator can tail a file in
    development and POST to their own system in production without re-learning the payload.
    It carries no wallet credential, preimage, or macaroon.
    """
    expires_in = max(0, round((pending.expires_at - pending.created_at).total_seconds()))
    return json.dumps(
        {
            "type": "payment.confirmation_required",
            "nonce": pending.nonce,
            "tool": pending.tool_name,
            "amountSats": pending.amount_sats,
            "amountUsd": float(round(pending.amount_usd, 2)),
            "destination": summarize_destination(pending.destination),
            "description": request.description,
            "summary": request.summary,
            "createdAt": pending.created_at.isoformat(),
            "expiresAt": pending.expires_at.isoformat(),
            "expiresInSeconds": int(expires_in),
        },
        separators=(",", ":"),
    )


def build_webhook_signature(secret: str, unix_timestamp: int, body: str) -> str:
    """``t={unix},v1={hex}`` — HMAC-SHA256 over ``{t}.{body}``.

    The same scheme the Lightning Enable API signs merchant webhooks with, so an operator
    who already verifies those can reuse their code. The timestamp is inside the signed
    string, so a captured POST cannot be replayed under a fresh timestamp.
    """
    signed = f"{unix_timestamp}.{body}"
    digest = hmac.new(secret.encode("utf-8"), signed.encode("utf-8"), hashlib.sha256).hexdigest()
    return f"t={unix_timestamp},v1={digest}"


# --------------------------------------------------------------------------------------
# Channels
# --------------------------------------------------------------------------------------


class ConfirmationChannel(Protocol):
    """Delivers a confirmation code to the human operator — and only to them."""

    kind: ConfirmationChannelKind
    operator_hint: str
    """Agent-safe, code-free description of where a delivered code went."""
    refusal_reason: str | None
    """Why this channel refuses to deliver at all, or None when it delivers."""

    async def deliver(
        self, pending: PendingConfirmation, request: ConfirmationRequest
    ) -> DeliveryResult:
        ...


class StderrConfirmationChannel:
    """Print the code to stderr, where the human at the terminal sees it and the model
    (which only sees tool results) does not. The historical local behaviour, unchanged."""

    kind = ConfirmationChannelKind.STDERR
    operator_hint = "printed to the server console/logs"
    refusal_reason: str | None = None

    def __init__(self, stream=None) -> None:
        # Test seam. None means sys.stderr, read at call time.
        self._stream = stream

    async def deliver(
        self, pending: PendingConfirmation, request: ConfirmationRequest
    ) -> DeliveryResult:
        print(
            f"[Lightning Enable] *** {request.title} ***\n"
            f"  {request.summary}\n"
            f"  Confirmation code: {pending.nonce}\n"
            "  To approve, give this code to the agent. Expires in 120s.",
            file=self._stream or sys.stderr,
            flush=True,
        )
        return DeliveryResult.ok()


DEFAULT_REFUSAL = (
    'This payment needs human approval, but this server has no approval channel '
    '(confirmation.channel = "refuse"), so it was refused rather than approved. No confirmation '
    "code exists for the agent to ask for. To allow payments this size, the OPERATOR must either "
    "raise tiers.autoApprove in ~/.lightning-enable/config.json, or configure an approval channel "
    '— confirmation.channel = "webhook" (with confirmation.webhookUrl and confirmation.webhookSecret) '
    'or "file" (with confirmation.filePath).'
)


class RefusingConfirmationChannel:
    """Refuse over-threshold payments instead of minting a code.

    No pending confirmation is ever created on this channel — ``BudgetService`` short-circuits
    before minting one.
    """

    kind = ConfirmationChannelKind.REFUSE
    operator_hint = "not delivered — over-threshold payments are refused on this server"

    def __init__(self, reason: str = DEFAULT_REFUSAL) -> None:
        self.refusal_reason = reason

    async def deliver(
        self, pending: PendingConfirmation, request: ConfirmationRequest
    ) -> DeliveryResult:
        return DeliveryResult.fail(self.refusal_reason)


def default_confirmation_file_path() -> str:
    """Where confirmations land when no path is configured."""
    return str(Path.home() / ".lightning-enable" / "confirmations.jsonl")


class FileConfirmationChannel:
    """Append the confirmation as a JSON line to a file the operator tails.

    POSIX permissions are pinned to 0600 on every write: the file holds live approval codes,
    so a world-readable copy would hand anyone on the box the ability to approve payments.
    """

    kind = ConfirmationChannelKind.FILE
    refusal_reason: str | None = None

    def __init__(self, path: str) -> None:
        self.path = path

    @property
    def operator_hint(self) -> str:
        return f"appended to the approval file at {self.path}"

    async def deliver(
        self, pending: PendingConfirmation, request: ConfirmationRequest
    ) -> DeliveryResult:
        try:
            line = build_payload(pending, request)
            target = Path(self.path)
            if target.parent and not target.parent.exists():
                target.parent.mkdir(parents=True, exist_ok=True)
            with open(target, "a", encoding="utf-8", newline="\n") as handle:
                handle.write(line + "\n")
            self._restrict_permissions(target)
            return DeliveryResult.ok()
        except Exception as ex:  # noqa: BLE001 — a channel must report, never raise
            return DeliveryResult.fail(
                f"could not append to the approval file at {self.path}: {ex}"
            )

    @staticmethod
    def _restrict_permissions(path: Path) -> None:
        """0600 on POSIX. On Windows the file inherits the profile ACL; nothing portable."""
        if os.name != "posix":
            return
        try:
            os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)
        except OSError as ex:
            # Best effort: the line is already written, and refusing a payment because chmod
            # failed would be worse than a warning. Say so loudly rather than silently.
            print(
                f"[Lightning Enable] Warning: could not restrict permissions on {path}: {ex}",
                file=sys.stderr,
                flush=True,
            )


class WebhookConfirmationChannel:
    """POST the pending confirmation to an operator-controlled URL, HMAC signed.

    The client carries the connect-time SSRF pin (a confirmation POST holds a live approval
    code, so it must never be steerable at a private/metadata address) and never follows
    redirects, so a signed approval can only reach the exact URL the operator configured.
    A delivery failure REFUSES the payment; it never approves it.
    """

    kind = ConfirmationChannelKind.WEBHOOK
    refusal_reason: str | None = None
    operator_hint = "sent to the operator's approval webhook"

    def __init__(
        self,
        url: str,
        secret: str,
        client_factory: Callable[[], httpx.AsyncClient] | None = None,
        timeout: float = 10.0,
    ) -> None:
        self.url = url
        self._secret = secret
        self._client_factory = client_factory or _default_webhook_client_factory
        self._timeout = timeout

    async def deliver(
        self, pending: PendingConfirmation, request: ConfirmationRequest
    ) -> DeliveryResult:
        try:
            body = build_payload(pending, request)
            timestamp = int(time.time())
            headers = {
                "Content-Type": "application/json",
                "X-LightningEnable-Signature": build_webhook_signature(
                    self._secret, timestamp, body
                ),
                "User-Agent": "LightningEnable-MCP/1.0",
            }

            client = self._client_factory()
            async with client:
                response = await client.post(
                    self.url, content=body.encode("utf-8"), headers=headers, timeout=self._timeout
                )

            status = response.status_code
            if 300 <= status < 400:
                # Never chase a redirect with a signed approval payload.
                return DeliveryResult.fail(
                    f"the approval webhook answered with a {status} redirect, which is never "
                    "followed (a signed approval must go only to the configured URL)"
                )
            if status >= 400:
                return DeliveryResult.fail(f"the approval webhook answered HTTP {status}")
            return DeliveryResult.ok()
        except Exception as ex:  # noqa: BLE001 — a channel must report, never raise
            return DeliveryResult.fail(f"the approval webhook could not be reached: {ex}")


def _default_webhook_client_factory() -> httpx.AsyncClient:
    """An httpx client with the connect-time SSRF pin and redirects disabled."""
    from .ssrf_transport import build_ssrf_safe_async_transport

    return httpx.AsyncClient(
        timeout=10.0,
        follow_redirects=False,
        transport=build_ssrf_safe_async_transport(),
    )


# --------------------------------------------------------------------------------------
# Resolution + construction
# --------------------------------------------------------------------------------------


def parse_channel(value: str | None) -> ConfirmationChannelKind | None:
    """Parse a channel name, case- and whitespace-insensitively. None when unrecognised."""
    if value is None:
        return None
    try:
        return ConfirmationChannelKind(value.strip().lower())
    except ValueError:
        return None


def is_hosted(hosted_flag: str | None) -> bool:
    """True when the operator declared a hosted deployment (``1`` or ``true``)."""
    value = (hosted_flag or "").strip().lower()
    return value in ("1", "true")


def resolve_confirmation_channel(
    env_channel: str | None,
    config_channel: str | None,
    stdin_is_tty: bool,
    hosted_flag: str | None,
) -> ConfirmationChannelResolution:
    """Decide WHERE an over-threshold confirmation code goes. Pure and side-effect free.

    An unparseable value fails CLOSED (refuse), not open: a typo in the channel name must not
    silently restore the posture the operator was trying to move away from.
    """
    if env_channel and env_channel.strip():
        kind = parse_channel(env_channel)
        if kind is not None:
            return ConfirmationChannelResolution(kind, "env")
        return ConfirmationChannelResolution(
            ConfirmationChannelKind.REFUSE,
            "env-invalid",
            f'{CHANNEL_ENV_VAR}="{env_channel.strip()}" is not a valid approval channel '
            f"({VALID_CHANNELS}). Over-threshold payments will be REFUSED until it is corrected.",
        )

    if config_channel and config_channel.strip():
        kind = parse_channel(config_channel)
        if kind is not None:
            return ConfirmationChannelResolution(kind, "config")
        return ConfirmationChannelResolution(
            ConfirmationChannelKind.REFUSE,
            "config-invalid",
            f'confirmation.channel="{config_channel.strip()}" in ~/.lightning-enable/config.json is '
            f"not a valid approval channel ({VALID_CHANNELS}). Over-threshold payments will be "
            "REFUSED until it is corrected.",
        )

    if stdin_is_tty:
        # A human is at the terminal: stderr is genuinely out-of-band for the model.
        return ConfirmationChannelResolution(ConfirmationChannelKind.STDERR, "auto")

    if is_hosted(hosted_flag):
        return ConfirmationChannelResolution(
            ConfirmationChannelKind.REFUSE,
            "auto",
            f"{HOSTED_ENV_VAR}=1 with no approval channel configured: over-threshold payments are "
            "REFUSED, because a confirmation code on the stderr of a hosted server is one nobody "
            "reads (and, on a shared host, one the agent may read). Set confirmation.channel to "
            "webhook or file to approve them out of band.",
        )

    return ConfirmationChannelResolution(
        ConfirmationChannelKind.STDERR,
        "auto",
        "stdin is not a TTY: over-threshold confirmation codes go to this server's stderr, so "
        "nobody approves a payment unless a human is reading its console or logs. Set "
        f"confirmation.channel ({VALID_CHANNELS}), or {HOSTED_ENV_VAR}=1 to refuse such payments "
        "instead.",
    )


def _cheap_ssrf_check(url: str) -> str | None:
    """Sync, no-DNS pre-check mirroring the .NET ``SsrfUrlGuard.Validate``.

    Turns a misconfigured webhook URL into one startup line instead of a per-payment failure.
    The authoritative guard is still the connect-time pin on the client.
    """
    # Imported lazily: ``tools/__init__`` eagerly imports the payment tools, which import
    # BudgetService, which imports this module — a top-level import would be circular.
    from .tools._ssrf_guard import is_blocked_ip

    try:
        parsed = urlsplit(url)
        if parsed.scheme.lower() not in ("http", "https"):
            return "only http and https URLs are allowed"
        host = parsed.hostname
        if not host:
            return "the URL has no host"
        lowered = host.lower()
        if (
            lowered in ("localhost", "metadata", "metadata.google.internal", "metadata.goog", "metadata.azure.com")
            or lowered.endswith(".internal")
            or lowered.endswith(".localhost")
        ):
            return "access to internal or private hosts is not allowed"
        try:
            literal = ipaddress.ip_address(host)
        except ValueError:
            literal = None
        if literal is not None and is_blocked_ip(literal):
            return "access to private or internal networks is not allowed"
        return None
    except Exception:  # noqa: BLE001 — a parse edge case must not crash startup
        return "the URL could not be parsed"


def _first_non_blank(*candidates: str | None) -> str | None:
    for candidate in candidates:
        if candidate and candidate.strip():
            return candidate.strip()
    return None


def _misconfigured(warn: Callable[[str], None] | None, problem: str) -> RefusingConfirmationChannel:
    if warn is not None:
        warn(f"Approval channel misconfigured: {problem}. Over-threshold payments will be REFUSED.")
    return RefusingConfirmationChannel(
        f"This payment needs human approval, but the approval channel is misconfigured: {problem}. "
        "It was refused rather than approved. The OPERATOR must fix the configuration (or raise "
        "tiers.autoApprove in ~/.lightning-enable/config.json) — the agent cannot resolve this."
    )


def _refusal_reason_for(resolution: ConfirmationChannelResolution) -> str:
    if resolution.source.endswith("-invalid"):
        return (
            "This payment needs human approval, but the approval channel is misconfigured: "
            f"{resolution.warning} The OPERATOR must fix it; the agent cannot."
        )
    if resolution.source == "auto":
        return (
            "This payment needs human approval, but this server runs in hosted mode "
            f"({HOSTED_ENV_VAR}=1) with no approval channel configured, so it was refused rather "
            "than approved. No confirmation code exists for the agent to ask for. To allow payments "
            "this size, the OPERATOR must either raise tiers.autoApprove in "
            '~/.lightning-enable/config.json or set confirmation.channel to "webhook" or "file".'
        )
    return DEFAULT_REFUSAL


def stdin_is_a_tty() -> bool:
    """Whether a human could be watching this console. Never raises."""
    try:
        return bool(sys.stdin is not None and sys.stdin.isatty())
    except Exception:  # noqa: BLE001 — a detached/closed stdin is simply "not a TTY"
        return False


def create_confirmation_channel(
    settings: ConfirmationSettings | None = None,
    warn: Callable[[str], None] | None = None,
    stdin_is_tty: bool | None = None,
    environ: dict | None = None,
    client_factory: Callable[[], httpx.AsyncClient] | None = None,
) -> ConfirmationChannel:
    """Build the approval channel for this process from config + environment + TTY.

    Misconfiguration always resolves to a REFUSING channel with a reason that names the
    missing setting — never a silent fallback to stderr, because "silently fall back to a
    code nobody reads" is the exact failure this feature exists to fix.
    """
    env = os.environ if environ is None else environ
    is_tty = stdin_is_a_tty() if stdin_is_tty is None else stdin_is_tty

    resolution = resolve_confirmation_channel(
        env.get(CHANNEL_ENV_VAR),
        getattr(settings, "channel", None),
        is_tty,
        env.get(HOSTED_ENV_VAR),
    )

    if resolution.warning and warn is not None:
        warn(resolution.warning)

    if resolution.kind is ConfirmationChannelKind.STDERR:
        return StderrConfirmationChannel()

    if resolution.kind is ConfirmationChannelKind.FILE:
        path = (
            _first_non_blank(env.get(FILE_PATH_ENV_VAR), getattr(settings, "file_path", None))
            or default_confirmation_file_path()
        )
        return FileConfirmationChannel(path)

    if resolution.kind is ConfirmationChannelKind.WEBHOOK:
        url = _first_non_blank(
            env.get(WEBHOOK_URL_ENV_VAR), getattr(settings, "webhook_url", None)
        )
        secret = _first_non_blank(
            env.get(WEBHOOK_SECRET_ENV_VAR), getattr(settings, "webhook_secret", None)
        )
        if url is None:
            return _misconfigured(
                warn,
                "the webhook approval channel is selected but no webhook URL is set "
                f"(confirmation.webhookUrl, or {WEBHOOK_URL_ENV_VAR})",
            )
        ssrf_problem = _cheap_ssrf_check(url)
        if ssrf_problem is not None:
            return _misconfigured(
                warn,
                f"the webhook approval URL was rejected by the SSRF guard ({ssrf_problem}); it must "
                "be a public http(s) endpoint, not a private, loopback, or metadata address",
            )
        if secret is None:
            return _misconfigured(
                warn,
                "the webhook approval channel is selected but no signing secret is set "
                f"(confirmation.webhookSecret, or {WEBHOOK_SECRET_ENV_VAR}); an unsigned approval "
                "POST is spoofable, so it is refused",
            )
        return WebhookConfirmationChannel(url, secret, client_factory=client_factory)

    return RefusingConfirmationChannel(_refusal_reason_for(resolution))
