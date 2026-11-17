# QoffeeAutomat

[![ci-dotnet](https://github.com/toeck98/QoffeeAutomat/actions/workflows/ci-dotnet.yml/badge.svg)](https://github.com/toeck98/QoffeeAutomat/actions/workflows/ci-dotnet.yml)
[![ci-integration](https://github.com/toeck98/QoffeeAutomat/actions/workflows/ci-integration.yml/badge.svg)](https://github.com/toeck98/QoffeeAutomat/actions/workflows/ci-integration.yml)
[![Release](https://img.shields.io/github/v/release/toeck98/QoffeeAutomat?include_prereleases&sort=semver)](https://github.com/toeck98/QoffeeAutomat/releases)
[![HACS: custom repository](https://img.shields.io/badge/HACS-Custom-orange.svg)](https://hacs.xyz/docs/faq/custom_repositories/)
[![License: MIT](https://img.shields.io/github/license/toeck98/QoffeeAutomat)](LICENSE)
[![Documentation](https://img.shields.io/badge/docs-toeck98.github.io-blue.svg)](https://toeck98.github.io/QoffeeAutomat/)

**Monitor your PC from Home Assistant and run your own commands on it.**

QoffeeAutomat is a small **agent for Windows** (Linux is planned) and a matching **Home Assistant integration**. The agent
measures the PC and executes the commands you define. Home Assistant connects to it, shows what you want as sensors and buttons,
and runs commands from automations, dashboards and voice assistants.

* **Monitor:** CPU, memory, disks, network, battery, uptime, logged-in user, session lock, volume and more. Only what you switch on
  is measured.
* **Control:** sleep, shut down, lock, volume, start programs, switch display profiles, or run any PowerShell script, with
  **variables** from Home Assistant that are passed as values and never pasted into code.
* **React:** events for logon, lock, "the PC was started" and your own scripts.
* **Author in a browser:** a web interface on the PC to write commands and sensors, test them with live output and preview the
  Home Assistant entity they create.
* **Safe by design:** password-protected interface (also on the PC itself), encrypted and paired link, strict input validation,
  audit log of every run, no cloud, no external services.
* **No polling, no broker:** Home Assistant holds one WebSocket connection to the agent; the PC pushes changes.

<!-- TODO(owner): add screenshots of the dashboard, the command editor and the Home Assistant device page
     (docs/assets/, linked here and from docs/index.md). -->

## Quick start

You need a Windows 10/11 PC, Home Assistant 2026.3 or newer with [HACS](https://hacs.xyz/), and both in the same network.

1. **Install the agent.** Download `QoffeeAutomat-Setup-<version>-x64.exe` (or `-arm64`) from the
   [latest release](https://github.com/toeck98/QoffeeAutomat/releases/latest), run it and accept the elevation prompt. The
   last page names the setup token file. ([Verify the download](docs/user/verify-download.md) if you like.)
2. **Set a password.** Open **QoffeeAutomat** from the Start menu, paste the setup token from
   `C:\ProgramData\QoffeeAutomat\secrets\setup-token.txt` (open it from an elevated editor) and choose a password.
3. **Install the integration.** In HACS, add the custom repository `https://github.com/toeck98/QoffeeAutomat` (type
   *Integration*), download **QoffeeAutomat**, restart Home Assistant.
   [![Add the repository to HACS](https://my.home-assistant.io/badges/hacs_repository.svg)](https://my.home-assistant.io/redirect/hacs_repository/?owner=toeck98&repository=QoffeeAutomat&category=integration)
4. **Pair.** In the agent, page *Home Assistant*, select **Pair**. In Home Assistant add the integration **QoffeeAutomat**,
   select your PC, compare the fingerprint and enter the six-digit code.
   [![Set up the integration](https://my.home-assistant.io/badges/config_flow_start.svg)](https://my.home-assistant.io/redirect/config_flow_start/?domain=qoffeeautomat)
5. **Done.** Open the new device: *CPU usage*, *Memory usage*, *Power state* and more are live. Enable the default command *Sleep* in
   the agent (page *Commands*) and a button for it appears in Home Assistant.

The full walk-through with explanations is in the [documentation](https://toeck98.github.io/QoffeeAutomat/user/quick-start/).

## Documentation

The user documentation is published at **<https://toeck98.github.io/QoffeeAutomat/>** and lives in [`docs/`](docs/index.md):

| Topic | Page |
|-------|------|
| Overview and architecture | [overview](docs/user/overview.md) |
| Installation, first run, pairing | [Windows](docs/user/install-windows.md), [first-run setup](docs/user/first-run.md), [pairing](docs/user/pairing.md), [HACS](docs/user/install-integration.md) |
| Commands, variables, controls | [commands](docs/user/commands.md), [built-in actions](docs/user/reference/actions.md) |
| Sensors and events | [sensors](docs/user/sensors.md), [events](docs/user/events.md) |
| Start the PC in a mode (wake, wait, run) | [worked example](docs/user/example-mode-start.md) |
| Security, backup, troubleshooting | [security](docs/user/security.md), [backup](docs/user/backup-restore.md), [troubleshooting](docs/user/troubleshooting.md) |
| Moving from the prototype | [migration](docs/migration-from-prototype.md) |
| Built-in metrics, protocol, API reference | generated while the site is built: see the [documentation site](https://toeck98.github.io/QoffeeAutomat/) |

## How it fits together

```text
Home Assistant (integration)  <-- QoffeeLink: WSS, JSON, push -->  Agent (Windows service)  <-- pipe -->  Session helper
                                                                    web UI, API, engines                  user desktop
```

The wire protocol **QoffeeLink** is specified by the JSON Schemas in [`protocol/`](protocol/README.md); the agent also serves a
documented management API (`/api/v1`, OpenAPI). Details: [overview and architecture](docs/user/overview.md).

## Status

The project is being prepared for **version 1.0**. Until then releases are pre-releases; HACS can install them by choosing a
version. Submission to the HACS default store is planned after 1.0. The original prototype is kept at the `prototype-v0` Git tag
and in [`docs/prototype/`](docs/prototype/README.md).

## For contributors

* [`AGENTS.md`](AGENTS.md) (also for AI coding agents), [`CONTRIBUTING.md`](CONTRIBUTING.md), [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md).
* The specification is [`docs/plan/PLAN.md`](docs/plan/PLAN.md); the work breakdown is [`docs/plan/tasks.yaml`](docs/plan/tasks.yaml).
* Build the documentation locally: `python -m pip install -r requirements-docs.txt` and `mkdocs build --strict`.
* Security problems: please use the [private reporting channel](SECURITY.md), not a public issue.

## Licence

[MIT](LICENSE). The brand artwork in [`assets/brand/`](assets/brand/README.md) is a placeholder until the project's final artwork
is added.
