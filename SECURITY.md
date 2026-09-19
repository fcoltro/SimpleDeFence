# Security Policy

SimpleDeFence is a firewall. A defect in it can leave a machine unprotected while telling its
owner it is protected, which is the failure mode this project takes most seriously. Reports are
welcome and will be answered.

## Supported versions

| Version | Supported |
|---------|-----------|
| 1.0.x   | Yes       |
| 0.1.x   | No — please upgrade; installed clients are offered 1.0 in-app |

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting instead:
[**Report a vulnerability**](https://github.com/fcoltro/SimpleDeFence/security/advisories/new).
It is private between you and the maintainer, and it lets a fix be prepared before the details are
public.

Useful things to include, as far as you have them:

- What the firewall did that it should not have, or failed to do that it should
- The version (Settings → About), and your Windows build
- Whether the machine had ever been in Learning mode, and which mode it was in at the time
- Anything in `C:\ProgramData\SimpleDeFence\logs` — note `service.log` only exists if the service
  has thrown, so its absence is itself informative

You will get an acknowledgement within a week. If a report turns out to be a real vulnerability,
you will be credited in the advisory and the release notes unless you would rather not be.

## Scope

In scope, and genuinely interesting:

- Any way to change the firewall's configuration, mode, or rules without administrator rights
- Any way to get traffic past the filters that the configured rules should have stopped
- Any way to make the interface report a state the service is not actually in — a firewall that
  lies about what it is enforcing is as bad as one that is not enforcing
- Tampering with the configuration, its key, the password record, or the hosts backup, from an
  account that should not be able to
- Anything that gets code running as the service's LocalSystem account

Out of scope, and already understood:

- Attacks that require administrator or SYSTEM to begin with. An administrator can stop the
  service, rewrite its configuration, or unwrap its DPAPI-protected key — that is inherent to a
  machine-wide service that must start unattended at boot, and is documented in
  `SimpleDeFence.Core/ConfigProtection.cs`.
- Denial of service against the local machine by an account that already has administrator rights.
- The absence of a hardening measure, as opposed to a concrete way through.
- Reports from automated scanners with no demonstrated impact.

## What this software does not do

Stated plainly, because a security tool should be legible:

- It installs **no kernel driver**. Rules are applied through the Windows Filtering Platform.
- It collects **no telemetry** and sends nothing about you or your machine anywhere.
- Its only outbound network activity is the update check, which fetches one JSON descriptor and can
  be switched off in Settings.
- Update payloads are **SHA-256 verified** against the published descriptor before anything is run;
  a mismatch is refused, not executed.
- The control channel between the interface and the service is a named pipe restricted to
  **Administrators and SYSTEM**, and the caller's token is checked by impersonation on every
  request.
