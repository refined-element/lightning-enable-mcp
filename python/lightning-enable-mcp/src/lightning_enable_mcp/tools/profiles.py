"""Tool profiles — how much of the tool surface ``list_tools`` advertises.

Every advertised tool's JSON schema is loaded into the agent's context at the start
of each session, so a wide surface costs tokens on every single turn. A profile
trims what is *advertised*; it never removes capability:

* ``lite``     — the five tools an agent needs to spend money and stay inside its
                 budget. Smallest context footprint.
* ``standard`` — the default. The consolidated verb set (:data:`STANDARD_TOOL_NAMES`).
* ``full``     — the consolidated set plus every pre-consolidation tool name, for
                 prompts and scripts written against the old surface.

Selected with the ``LIGHTNING_ENABLE_TOOL_PROFILE`` environment variable. An
unset or unrecognized value falls back to ``standard`` (an unrecognized one also
logs a warning) — never to an empty surface.

**Profiles are listing-only.** A tool absent from the advertised list is still
callable by name, and the deprecated aliases still dispatch, in every profile. So
narrowing the profile can never strand a caller that knows the tool exists; it only
stops the schema from being pushed into the model's context.
"""

import logging

logger = logging.getLogger("lightning-enable-mcp.tools.profiles")

#: Environment variable that selects the profile.
PROFILE_ENV_VAR = "LIGHTNING_ENABLE_TOOL_PROFILE"

LITE = "lite"
STANDARD = "standard"
FULL = "full"

#: Profile used when the env var is unset, blank, or unrecognized.
DEFAULT_PROFILE = STANDARD

VALID_PROFILES = (LITE, STANDARD, FULL)

#: The consolidated verb set — what ``standard`` advertises, in list_tools order.
STANDARD_TOOL_NAMES: tuple[str, ...] = (
    "access_l402_resource",
    "pay_invoice",
    "pay_l402_challenge",
    "test_l402_payment",
    "get_balance",
    "budget",
    "receipts",
    "create_invoice",
    "check_invoice_status",
    "verify_confirmation_code",
    "discover_api",
    "create_lightning_enable_account",
    "wallet_ops",
    "l402_producer",
    "agent_services",
)

#: The minimal spend-and-stay-in-budget surface.
LITE_TOOL_NAMES: tuple[str, ...] = (
    "access_l402_resource",
    "pay_invoice",
    "get_balance",
    "budget",
    "receipts",
)

#: Pre-consolidation tool names that ``full`` re-advertises. These are the tools the
#: consolidation folded into ``budget`` / ``receipts`` / ``wallet_ops`` /
#: ``l402_producer`` / ``agent_services``; they remain callable in every profile as
#: deprecated aliases (see ``server.DEPRECATED_ALIASES``).
#:
#: The three v1 aliases that predate this consolidation (``confirm_payment``,
#: ``check_wallet_balance``, ``get_all_balances``) are deliberately NOT here: they
#: were already unadvertised before profiles existed, and ``full`` is not a reason to
#: start advertising them again.
LEGACY_TOOL_NAMES: tuple[str, ...] = (
    "get_budget_status",
    "configure_budget",
    "get_receipts",
    "get_payment_history",
    "get_btc_price",
    "exchange_currency",
    "send_onchain",
    "create_l402_challenge",
    "verify_l402_payment",
    "discover_agent_services",
    "publish_agent_capability",
    "unpublish_agent_capability",
    "request_agent_service",
    "publish_agent_attestation",
    "get_agent_reputation",
    "settle_agent_service",
)


def resolve_profile(raw: str | None) -> str:
    """Normalize a raw ``LIGHTNING_ENABLE_TOOL_PROFILE`` value to a known profile.

    Unset/blank is the documented default and resolves quietly. An unrecognized
    value resolves to the same default but logs a warning naming the valid choices,
    so a typo ("standrd", "minimal") is visible instead of silently changing the
    agent's tool surface.
    """
    if raw is None:
        return DEFAULT_PROFILE
    normalized = raw.strip().lower()
    if not normalized:
        return DEFAULT_PROFILE
    if normalized in VALID_PROFILES:
        return normalized
    logger.warning(
        "Unrecognized %s=%r - falling back to %r. Valid profiles: %s.",
        PROFILE_ENV_VAR,
        raw,
        DEFAULT_PROFILE,
        ", ".join(VALID_PROFILES),
    )
    return DEFAULT_PROFILE


def advertised_tool_names(profile: str) -> tuple[str, ...]:
    """Tool names ``list_tools`` advertises under ``profile``, in listing order."""
    resolved = resolve_profile(profile)
    if resolved == LITE:
        return LITE_TOOL_NAMES
    if resolved == FULL:
        return STANDARD_TOOL_NAMES + LEGACY_TOOL_NAMES
    return STANDARD_TOOL_NAMES
