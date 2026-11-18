# QoffeeAutomat

**QoffeeAutomat** lets [Home Assistant](https://www.home-assistant.io/) watch a PC and run your own commands on it.
A small agent runs on the PC (Windows first, Linux later) and a matching Home Assistant integration connects to it.

* **Monitor** CPU, memory, disks, network, battery, uptime, who is logged in, whether the session is locked, the volume
  and more. Only what you switch on is measured.
* **Control** the PC from automations and dashboards: sleep, shut down, lock, change the volume, start programs, switch
  display profiles, or run any PowerShell script. Variables from Home Assistant are passed as values, never pasted into
  code.
* **React** to events from the PC: session locked, user logged in, the PC was started, or an event that your own script
  raises.
* **Author** commands and sensors in a web interface on the PC, with a test run, live output and a preview of the Home
  Assistant entity they create.
* **Stay in control.** The connection is encrypted and paired with a one-time code, every run is written to a history, and
  nothing leaves your network.

The agent and the integration talk over **QoffeeLink**, a WebSocket protocol with JSON messages. Home Assistant connects to
the PC; the PC pushes changed values, so nothing is polled.

## Where to start

| I want to... | Read |
|--------------|------|
| get a first sensor in Home Assistant quickly | [Quick start](user/quick-start.md) |
| understand how the parts fit together | [Overview and architecture](user/overview.md) |
| install the agent | [Install on Windows](user/install-windows.md) or [Install on Linux](user/install-linux.md), then [First-run setup](user/first-run.md) |
| connect Home Assistant | [Install the integration (HACS)](user/install-integration.md), then [Pair with Home Assistant](user/pairing.md) |
| make Home Assistant run something on the PC | [Commands](user/commands.md) |
| report my own values to Home Assistant | [Sensors](user/sensors.md) |
| start the PC in a "mode" from an automation | [Example: start the PC in a mode](user/example-mode-start.md) |
| know what could go wrong | [Security model and recommendations](user/security.md) |
| fix a problem | [Troubleshooting](user/troubleshooting.md) |
| move over from the old prototype | [Migration from the prototype](migration-from-prototype.md) |

## Reference

* [Built-in metrics](user/reference/metrics.md), generated from the catalog in the agent.
* [Built-in actions](user/reference/actions.md)
* [Events](user/events.md)
* [Protocol reference (QoffeeLink v1)](user/reference/protocol.md), generated from the JSON Schemas.
* [Management API reference](user/reference/api.md), generated from the OpenAPI document.

## Project

QoffeeAutomat is open source (MIT licence). The source, the issue tracker and the releases are on
[GitHub](https://github.com/toeck98/QoffeeAutomat). Questions and shared commands belong in the
[discussions](https://github.com/toeck98/QoffeeAutomat/discussions); bugs in the
[issue tracker](https://github.com/toeck98/QoffeeAutomat/issues). Security problems: see the
[security policy](https://github.com/toeck98/QoffeeAutomat/security/policy).
