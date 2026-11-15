# Security policy

QoffeeAutomat runs with high privileges on the PC (the Windows service uses the
LocalSystem account) and executes commands that Home Assistant triggers. Security
reports are therefore taken seriously.

## Supported versions

Only the latest released version receives security fixes. Until version 1.0.0 is
released, this means the latest `0.x` release; after that, the latest `1.x` release.

| Version | Supported |
|---------|-----------|
| latest release | yes |
| older releases | no |

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting instead:

1. Open the [Security tab](https://github.com/toeck98/QoffeeAutomat/security) of the
   repository.
2. Choose **Report a vulnerability** and describe the problem.

Please include:

- the affected component (agent, SessionHost, web UI, Home Assistant integration,
  QoffeeLink protocol) and version,
- the operating system and Home Assistant version,
- steps to reproduce, or a proof of concept,
- the impact you expect (for example privilege escalation, command injection,
  authentication bypass).

You can expect an acknowledgement within a few days and a status update once the
report has been assessed. Fixes are released as soon as they are ready, and the
reporter is credited in the advisory unless they prefer to stay anonymous.

## Scope

The threat model is described in section S09 of [`docs/plan/PLAN.md`](docs/plan/PLAN.md).
In scope are, among others: authentication and pairing, variable handling (command
injection), file permissions of the configuration, scripts and secrets directories,
the SessionHost pipe and the TLS certificate handling.
