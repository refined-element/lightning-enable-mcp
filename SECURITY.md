# Security Policy

## Reporting a vulnerability

Please **do not** open a public GitHub issue for a suspected security vulnerability.

Use this repository's **GitHub Private Vulnerability Reporting** / security-advisory flow to report vulnerabilities privately. If that option is unavailable, open a private security advisory from this repository's **Security** tab.

Please include:

- affected version, package, release, or commit SHA;
- a description of the impact and affected boundary;
- reproducible steps or a minimal proof of concept, where safe; and
- any proposed mitigation or patch, if available.

## High-priority scope

This repository is payment- and wallet-adjacent. Please report privately any issue that could affect:

- authorization or policy enforcement for Lightning/L402 payments;
- wallet selection, spend limits, approvals, or transaction intent;
- leakage, substitution, or misuse of NWC connection strings, macaroons, API keys, access tokens, payment preimages, or equivalent credentials;
- tenant isolation, cross-account access, or receipt/audit integrity; or
- package/release integrity, including a compromised build or distribution artifact.

## Keep secrets out of reports

Do not include private keys, seed phrases, wallet credentials, NWC connection strings, macaroons, payment preimages, API keys, access tokens, invoices, customer data, or other sensitive production material in a report.

Use redacted examples or contact us through the private advisory to arrange a safe reproduction path when necessary.

## Supported versions

Security fixes are normally provided for the latest released version. Older releases may require an upgrade.

## Response and coordinated disclosure

We aim to acknowledge valid reports within **five business days**, assess impact, work toward a fix or mitigation, and coordinate public disclosure after affected users have had a reasonable opportunity to update.

Please do not publicly disclose a suspected vulnerability before coordinated disclosure or before we have agreed that disclosure is appropriate.

## Good-faith research

We welcome good-faith security research. Please avoid actions that could harm users, disrupt services, access data you do not own, or create costs or payments for others.
