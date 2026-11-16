---
plan: QoffeeAutomat
plan_version: 7
created: 2026-11-14
repository: https://github.com/toeck98/QoffeeAutomat
language: en
license: MIT
task_file: tasks.yaml
target_location_in_repo: docs/plan/
section_id_format: "S<two digits>"      # tasks.yaml references sections by this id
decision_id_format: "D-<three digits>"
requirement_id_format: "R-<two digits>"
---

# QoffeeAutomat – Project Plan

Naming: the project is spelled QoffeeAutomat (domain `qoffeeautomat`, protocol
QoffeeLink). The prototype used the misspelling "QoffeAutomat"; it survives
only in the name of the prototype folder `QoffeAutomatService/` that T001
removes. The owner renames the GitHub repository to `QoffeeAutomat`.

QoffeeAutomat is a PC agent (Windows first, Linux later) that lets Home Assistant
monitor a PC and run user-defined commands on it, plus a matching Home Assistant
custom integration. This document is the specification. The work breakdown lives in
`tasks.yaml` next to this file. Both files are meant to be committed to
`docs/plan/` in the repository.

## S01 How agents use this plan

1. Read `AGENTS.md` (created by task T005), then this file, then `tasks.yaml`.
2. Pick the task with the lowest id whose `status` is `todo`, whose `actor`
   includes `agent`, and whose `depends_on` tasks are all `done`.
   `python scripts/plan/next_task.py` prints it (available after T005).
3. Read every section listed in the task's `spec` field before writing code.
4. Work on a branch named `task/<id>-<slug>`. One task equals one pull request.
5. PR title follows Conventional Commits and ends with the task id, for example
   `feat(agent): add metric engine (T205)`.
6. Run every command in the task's `verify` list plus the global checks in S14.
   A task is `done` only when all `acceptance` items hold and CI is green.
7. In the same PR set the task's `status` to `done` in `tasks.yaml`.
   If blocked, set `status: blocked` and add a `blocked_reason` string.
8. Never change a decision in S03 or a wire format in S07 silently. Change the
   spec in the same PR, add or amend a decision row, and bump the protocol or
   schema version when the change is breaking.
9. Version numbers of NuGet packages, Python packages, GitHub Actions, Home
   Assistant and .NET patch levels are **not** fixed by this plan. Look up the
   current stable release when implementing and pin it (actions by commit SHA).
10. Tasks with `actor: human` need the repository owner (settings, secrets,
    third-party applications, checks on a real desktop). Agents prepare
    everything they can and leave a checklist in the PR description.
11. A task with `detail: epic` is a placeholder whose first step is to refine
    it into concrete tasks in `tasks.yaml` through a PR. The plan currently
    contains none.

Agents usually run on Linux. Code under `Platform.Windows` and `SessionHost`
cannot be executed there; it is verified by the `windows-latest` CI job, and
tasks marked `human_verify: true` additionally need a check on a real Windows
desktop session.

## S02 Requirements

| Id | Requirement | Source |
|----|-------------|--------|
| R-01 | A service installed on Windows that communicates with Home Assistant. | owner |
| R-02 | A web UI (served by the service) to enable and disable commands. | owner |
| R-03 | The UI lets users author their own commands comfortably, including PowerShell one-liners and full scripts. | owner |
| R-04 | Home Assistant can inject values into declared command variables. | owner |
| R-05 | The agent is also a monitoring tool for the PC (metrics). | owner |
| R-06 | Communication between PC and Home Assistant is as efficient as possible. | owner |
| R-07 | A Home Assistant integration that learns dynamically from the PC which metrics it sends and which commands it accepts. | owner |
| R-08 | All useful GitHub features are planned in (Actions, workflows, security, releases, community). | owner |
| R-09 | .NET stays; Linux support must remain possible later. | owner |
| R-10 | The existing repository content may be discarded. | owner |
| R-11 | Quality-of-life features are welcome. | owner |
| R-12 | Later: a central place that configures several agents and collects their metrics. | owner |
| R-13 | The plan is machine-readable so AI agents can execute it. | owner |

Features of the prototype that must survive as command templates (not as
hard-coded modules): applying display profiles through the `DisplayConfig`
PowerShell module, and "modes" that apply a profile and launch programs in
sequence with delays (see T309).

## S03 Decisions

Facts marked *(verified 2026-11-14)* were checked against the linked sources
on that date and should be re-checked when the related task starts.

| Id | Decision | Rationale |
|----|----------|-----------|
| D-001 | Monorepo: agent, UI, HA integration, protocol spec and docs live in `toeck98/QoffeeAutomat`. | HACS requires exactly one integration per repository, located at `custom_components/<domain>/` in the repo root *(verified)*; it does not forbid other content, and with D-003 it only downloads the zip asset. One repo keeps protocol, agent and integration in sync in a single PR. |
| D-002 | Lockstep versioning: agent and integration share one SemVer version and one GitHub release. The wire protocol has its own integer version with a negotiated range. | HACS uses the latest GitHub release tag as the integration version *(verified)*. Independent protocol versioning lets mismatched releases still talk. Escape hatch: split the integration out with `git filter-repo` if release coupling hurts. |
| D-003 | HACS downloads a release asset: `hacs.json` sets `zip_release: true` and `filename: qoffeeautomat.zip`. | Keeps the multi-megabyte agent sources out of Home Assistant installs. |
| D-004 | Target framework `net10.0` (LTS). Revisit at .NET 12 LTS. | .NET 10 is LTS and supported until November 2028; .NET 11 (GA 2026-11-10) is STS *(verified)*. |
| D-005 | Transport "QoffeeLink": the HA integration opens one persistent WebSocket to the agent (`iot_class: local_push`). The agent pushes batched state deltas; no polling in either direction. | Lowest overhead and latency without extra infrastructure (no MQTT broker). The agent already runs a web server for the UI. HA-initiated connections match how HA handles devices and allow zeroconf discovery and Wake-on-LAN while the PC is off. |
| D-006 | QoffeeLink frames are UTF-8 JSON text with `permessage-deflate`. The protocol is symmetric after the handshake so the dial direction can be reversed later (agent to hub, agent to HA for roaming laptops). | JSON is debuggable and trivially handled in Python and C#. Payloads are small because only changed values are sent. |
| D-007 | Demand-driven sampling: a metric is only sampled while at least one consumer (HA, UI, hub) subscribes to it. The integration subscribes only to metrics whose entities are enabled. | Saves CPU on the PC; disabled entities cost nothing. |
| D-008 | Rejected alternatives for D-005: REST polling of HA (prototype; inefficient, one entity), MQTT discovery (needs a broker, weak for variables and command responses; may be added later as optional transport), agent dialling into the HA WebSocket API (needs a long-lived HA token on every PC; kept as later option T710). | Recorded so agents do not reopen the question. |
| D-009 | Two processes on Windows: `Agent` runs as a Windows service (LocalSystem); `SessionHost` runs inside each interactive user session and is started by the agent. They talk over a named pipe. | Services live in session 0 and cannot show windows, start GUI programs on the user's desktop, change the user's display configuration or read idle time and foreground window. The prototype's display switching and program launching need the user session. |
| D-010 | Every command declares `runAs: system` or `runAs: user`. | Follows from D-009; lets power actions run as service and desktop actions run in the session. |
| D-011 | Scripts run out of process (`pwsh` if installed, else Windows PowerShell 5.1; `cmd`; later `bash`). The PowerShell SDK is not embedded. | Isolation (a script cannot crash the service), hard timeouts with process-tree kill, small binaries, works in the user session, same model on Linux. Start-up latency of a few hundred milliseconds is acceptable. A warm runspace pool is a possible later optimisation. |
| D-012 | Variables are never spliced into script text. PowerShell receives them as bound named parameters through a generated wrapper that reads a JSON parameter file; processes receive them as separate argv entries; both also get `QA_VAR_<NAME>` environment variables. | Prevents command injection from HA automations, templates and voice input. |
| D-013 | Configuration is stored as JSON documents on disk (one file per command and sensor) with `schemaVersion` and `revision`. Run history, audit log and events are stored in SQLite. | Human-readable, diffable, exportable, and suitable for desired-state sync from a future hub (R-12). |
| D-014 | API first: the UI only talks to the versioned management REST API (`/api/v1`, OpenAPI document published). | The future hub and third-party tools reuse the same API. |
| D-015 | UI is Blazor WebAssembly with MudBlazor, served as static files by the agent. Pages live in a Razor class library `QoffeeAutomat.Ui.Components`. Script editing uses the Monaco editor bundled locally (no CDN). | Owner's choice. C# end to end, DTOs shared through `QoffeeAutomat.Contracts`, components reusable by the hub UI. The PC may be offline, so no external assets. |
| D-016 | Pairing by one-time code: the agent UI shows a short-lived code and the TLS certificate fingerprint; the HA config flow sends the code, receives a long-lived link token and pins the certificate. Zeroconf type `_qoffeeautomat._tcp.local.`. | No copying of long tokens. Works with self-signed certificates. |
| D-017 | The management API always requires authentication, including on loopback. | The service runs as LocalSystem and can run arbitrary scripts; an unauthenticated loopback API would be a local privilege escalation for any standard user. |
| D-018 | Two listeners: `http://127.0.0.1:8480` (UI and API, loopback only) and `https://0.0.0.0:8481` (QoffeeLink and pairing; UI and API only if "remote UI" is enabled). Ports are configurable. | Browsers accept loopback HTTP; LAN traffic is always encrypted. |
| D-019 | Installer is Inno Setup; the agent executable can also self-install (`qoffeeautomat service install`). MSI is out of scope for v1. | Simple, scriptable, no licensing questions. The self-install path is reused for systemd later. |
| D-020 | Releases are driven by Conventional Commits and release-please; artifacts carry checksums, an SBOM and GitHub build-provenance attestations. Code signing through SignPath Foundation if the application is accepted. | Azure Artifact Signing is not available to individual developers outside the USA and Canada; SignPath Foundation signs qualifying open-source projects for free *(verified)*. |
| D-021 | The agent core must run on Linux from day one using `Platform.Simulated` (deterministic fake metrics and commands). | Keeps platform abstractions honest for R-09, lets Linux-hosted agents and CI run the agent, and enables Docker-based end-to-end tests with a real Home Assistant. |
| D-022 | Minimum Home Assistant version is 2026.3 or newer (final value chosen in T003). | Custom integrations can ship brand images in `custom_components/<domain>/brand/` since 2026.3 *(verified)*. |
| D-036 | Third run context `runAs: user_elevated`: the agent starts the process itself on the user's desktop with the elevated token linked to the interactive user's token. It is available only when that user is an administrator; otherwise the run fails with `elevation_unavailable`. | Programs that require administrator rights would otherwise show a UAC prompt and block automations. The service already has higher rights (`system`), so no new privilege is handed out. |
| D-037 | Modes are a first-class document (mode group) but appear on the wire as a generated command with an enum variable plus a state metric. | Home Assistant gets one select entity with a real state without any protocol or integration change. |
| D-038 | The media player is composed by the integration from ordinary metrics and commands, announced by the manifest feature `media`. | No new message types; the PC side stays a set of metrics. |
| D-039 | The WebSocket close codes 4400, 4401 and 4406 and the canonical JSON form that `rev` is computed from are part of QoffeeLink v1 (S07). The schemas in `protocol/schema/` are normative for which fields are mandatory; optional fields are omitted rather than sent as `null`, and receivers treat `null` like absent. | Specified by T101 so the C# and Python implementations agree byte for byte on `rev`; no wire change for implementations that followed S07 literally. |
| D-023 | Hardware sensors that need a kernel driver (temperatures, fans via LibreHardwareMonitorLib, MPL-2.0) are optional, off by default and delivered after v1. | Driver trust and antivirus history of such drivers; needs a spike (T707). GPU load is available without a driver through performance counters. |

## S04 Architecture

```
                         Home Assistant
                 +-----------------------------+
                 | custom_components/          |
                 |   qoffeeautomat (Python)     |
                 +--------------+--------------+
                                | QoffeeLink v1: WSS, JSON, push, deltas (S07)
                                | zeroconf discovery, pairing code (S09)
   PC  --------------------------+--------------------------------------------
                 +--------------v--------------+       +--------------------+
   browser ----> | QoffeeAutomat.Agent          | pipe  | SessionHost        |
   UI (WASM)     |  Kestrel: UI, /api/v1, link | <---> | (per user session) |
   /api/v1 (S08) |  command engine             |       | user commands,     |
                 |  metric engine              |       | session metrics,   |
                 |  config store, history      |       | tray, toasts       |
                 +--------------+--------------+       +--------------------+
                                | platform interfaces
                 +--------------v--------------+
                 | Platform.Windows | .Linux   |
                 | Platform.Simulated (tests)  |
                 +-----------------------------+

   later: Agent --(QoffeeLink, agent dials out)--> QoffeeAutomat.Hub (S20)
```

| Component | Kind | Responsibility |
|-----------|------|----------------|
| `QoffeeAutomat.Contracts` | .NET library | Protocol messages, API DTOs, config document models, JSON source-generation contexts. No dependencies beyond the BCL. |
| `QoffeeAutomat.Core` | .NET library | Config store, command engine, metric engine, manifest builder, event bus, platform interfaces. Platform-neutral. |
| `QoffeeAutomat.Agent` | ASP.NET Core executable | Host, Kestrel listeners, management API, QoffeeLink server, pairing, auth, CLI verbs, static UI files. |
| `QoffeeAutomat.Platform.Windows` | .NET library (`net10.0-windows`) | Windows metric providers, built-in commands, service integration, session launching, DPAPI. |
| `QoffeeAutomat.Platform.Simulated` | .NET library | Deterministic providers for tests, CI and development on any OS. |
| `QoffeeAutomat.Platform.Linux` | .NET library (milestone M8) | Linux providers and systemd integration. |
| `QoffeeAutomat.SessionHost` | Windows executable | Runs in the user session: user-context commands, session metrics, tray icon, notifications. |
| `QoffeeAutomat.Ui` | Blazor WebAssembly app | Thin shell hosting the component library. |
| `QoffeeAutomat.Ui.Components` | Razor class library | All pages and components; depends on an `IAgentApi` abstraction. |
| `custom_components/qoffeeautomat` | Python package | Home Assistant integration. |
| `protocol/` | JSON Schema and fixtures | Single source of truth for QoffeeLink; validated by .NET and Python tests. |
| `QoffeeAutomat.Hub` | ASP.NET Core app (milestone M9) | Central configuration, metric collection, fleet link for Home Assistant (S20). |
| `QoffeeAutomat.Hub.Ui` | Blazor WebAssembly app (milestone M9) | Hub UI; reuses `Ui.Components`. |

## S05 Repository layout

```
/
├── AGENTS.md  CLAUDE.md  README.md  LICENSE  CHANGELOG.md  SECURITY.md
├── CONTRIBUTING.md  CODE_OF_CONDUCT.md  hacs.json
├── QoffeeAutomat.slnx  global.json  Directory.Build.props  Directory.Packages.props
├── .editorconfig  .gitattributes  .gitignore  release-please-config.json
├── .github/            workflows/, ISSUE_TEMPLATE/, dependabot.yml, CODEOWNERS,
│                       labels.yml, labeler.yml, pull_request_template.md,
│                       copilot-instructions.md
├── .devcontainer/
├── src/
│   ├── QoffeeAutomat.Contracts/
│   ├── QoffeeAutomat.Core/
│   ├── QoffeeAutomat.Agent/
│   ├── QoffeeAutomat.Platform.Windows/
│   ├── QoffeeAutomat.Platform.Simulated/
│   ├── QoffeeAutomat.SessionHost/
│   ├── QoffeeAutomat.Ui/
│   ├── QoffeeAutomat.Ui.Components/
│   └── QoffeeAutomat.Hub/, QoffeeAutomat.Hub.Ui/   (M9)
├── tests/
│   ├── QoffeeAutomat.Contracts.Tests/
│   ├── QoffeeAutomat.Core.Tests/
│   ├── QoffeeAutomat.Agent.Tests/           (WebApplicationFactory, link and API tests)
│   ├── QoffeeAutomat.Platform.Windows.Tests/ (run on windows-latest only)
│   ├── QoffeeAutomat.Ui.Tests/              (bUnit)
│   └── e2e/                                (docker compose: Home Assistant + agent)
├── custom_components/qoffeeautomat/         (integration; must stay at repo root, D-001)
├── tests_integration/                      (pytest for the integration)
├── protocol/           schema/*.schema.json, fixtures/*.json, README.md
├── assets/brand/       icon and logo (placeholder until 1.0, O-05); single source for the
│                       HA brand folder, UI favicon, executable, installer and tray icons
├── templates/          commands/*.json, sensors/*.json, scripts/*.ps1 (gallery, T701)
├── packaging/          windows/ (Inno Setup), linux/ (M8), hub/ (image, HA app; M9)
├── scripts/            plan/, github/, dev/
└── docs/               plan/PLAN.md, plan/tasks.yaml, user docs (MkDocs), adr/
```

Runtime data directories:

| Purpose | Windows | Linux (M8) |
|---------|---------|------------|
| Config | `%ProgramData%\QoffeeAutomat\config\` | `/etc/qoffeeautomat/` |
| Scripts | `%ProgramData%\QoffeeAutomat\scripts\` | `/etc/qoffeeautomat/scripts/` |
| Secrets | `%ProgramData%\QoffeeAutomat\secrets\` | `/var/lib/qoffeeautomat/secrets/` |
| Data (SQLite) | `%ProgramData%\QoffeeAutomat\data\` | `/var/lib/qoffeeautomat/` |
| Logs | `%ProgramData%\QoffeeAutomat\logs\` | journald and `/var/log/qoffeeautomat/` |

No file named `config.yaml`, `config.yml` or `config.json` may be added
anywhere in the repository outside hidden folders (D-034).

On Windows the config, scripts and secrets directories are writable only by
`SYSTEM` and `Administrators`. This is a security requirement (S09), because
scripts in them run as LocalSystem.

## S06 Domain model

All config documents carry `schemaVersion` (int), `revision` (int, incremented
on every save), `updatedAt` (UTC ISO 8601) and `origin` (`local` or `hub`).
JSON property names are camelCase. Ids match `^[a-z][a-z0-9_]{0,62}$`.

### Command document (`config/commands/<id>.json`)

```json
{
  "schemaVersion": 1,
  "id": "set_display_profile",
  "name": "Set display profile",
  "description": "Applies a saved DisplayConfig profile.",
  "icon": "mdi:monitor-multiple",
  "enabled": true,
  "exposeToHa": true,
  "runAs": "user",
  "action": { "type": "powershell", "source": "file", "script": "set_display_profile.ps1", "engine": "auto" },
  "parameters": [
    { "name": "Profile", "type": "enum", "options": ["Normal-Mode", "TV-Mode", "Study-Mode"], "required": true },
    { "name": "UpdateAdapterIds", "type": "bool", "default": false }
  ],
  "haControl": { "type": "select", "parameter": "Profile", "stateMetric": null },
  "timeoutSeconds": 60,
  "concurrency": "queue",
  "cooldownSeconds": 0,
  "captureOutput": true
}
```

| Field | Values | Notes |
|-------|--------|-------|
| `enabled` | bool | Disabled commands are rejected everywhere (R-02). |
| `exposeToHa` | bool | Only enabled and exposed commands appear in the manifest. |
| `runAs` | `system`, `user`, `user_elevated` | D-010, D-036. `user` and `user_elevated` fail with error `no_session` when nobody is logged in; `user_elevated` fails with `elevation_unavailable` when the user is not an administrator. |
| `action.type` | `powershell`, `process`, `builtin`, `sequence` | See below. |
| `concurrency` | `single`, `queue`, `parallel`, `restart` | Same semantics as Home Assistant script modes. |
| `timeoutSeconds` | 1..86400, default 60 | On timeout the whole process tree is killed. |
| `captureOutput` | bool | stdout and stderr are captured, each capped at 64 KiB. |

Action types:

| Type | Fields | Behaviour |
|------|--------|-----------|
| `powershell` | `source` (`inline` or `file`), `code` or `script`, `engine` (`auto`, `pwsh`, `powershell`) | Runs through the wrapper described in D-012. `inline` code is stored in the document; `file` refers to a file in the scripts directory. Scripts declare a `param()` block matching the parameters. |
| `process` | `file`, `args` (string array), `workingDirectory`, `waitForExit` (bool) | Starts the executable without a shell. `{{Name}}` placeholders in `args` entries are replaced by variable values; an entry is never split into several arguments. |
| `builtin` | `name` | Platform-provided action (S11), for example `power.shutdown`, `audio.set_volume`, `notify.toast`. |
| `sequence` | `steps` (array of `{ "command": id, "vars": {} }` or `{ "delaySeconds": n }`), `onError` (`stop`, `continue`) | Runs other commands in order; replaces the prototype's "modes". Cycles are rejected at save time. |

Parameters (variables, R-04):

| Field | Applies to | Notes |
|-------|-----------|-------|
| `name` | all | `^[A-Za-z][A-Za-z0-9_]{0,31}$`, unique per command (case-insensitive). |
| `type` | all | `string`, `int`, `float`, `bool`, `enum`. |
| `required`, `default` | all | A missing required value is error `invalid_vars`. |
| `min`, `max`, `step` | `int`, `float` | Validated by the agent, mirrored in HA number entities. |
| `pattern`, `maxLength` | `string` | `maxLength` defaults to 1024. `pattern` is a .NET regex with a 100 ms match timeout. |
| `options` | `enum` | Non-empty list of strings. |
| `optionsFrom` | `enum` | Optional metric id (after v1, T713). The options are taken from that enum metric's current options when the manifest is built, so they follow the PC's state (for example audio devices). Replaces `options`. |
| `secret` | all | Optional bool. The value is redacted in history, logs and audit entries. |
| `unit`, `description` | all | Shown in the UI and in HA. |

Unknown variables are rejected. Values are validated by the agent regardless
of what the client checked. Context values available to every run:
`QA_RUN_ID`, `QA_SOURCE` (`ha`, `ui`, `hub`, `cli`, `sequence`, `test`), `QA_COMMAND_ID`,
`QA_AGENT_NAME` (environment variables; `{{qa.run_id}}` etc. in `process` args).

`haControl` maps a command to a Home Assistant entity:

| `haControl.type` | HA platform | Requirement | Behaviour |
|------------------|-------------|-------------|-----------|
| `none` | none | – | Only callable through the `qoffeeautomat.run_command` action. |
| `button` | `button` | all parameters optional or defaulted | Press runs the command with defaults. |
| `switch` | `switch` | `parameter` of type `bool` | On runs with `true`, off with `false`. |
| `select` | `select` | `parameter` of type `enum` | Selecting an option runs the command. |
| `number` | `number` | `parameter` of type `int` or `float` | Setting a value runs the command. |
| `text` | `text` | `parameter` of type `string` | Setting a value runs the command. |

`stateMetric` optionally names a metric that reports the real state (for
example a custom sensor). Without it the HA entity is optimistic
(`assumed_state`).

### Metric descriptor

Built-in metrics come from providers; custom sensors are config documents in
`config/sensors/<id>.json`. Overrides for built-in metrics (enabled, interval,
deadband, exposeToHa) are stored in `config/metrics.json`.

| Field | Values | Notes |
|-------|--------|-------|
| `id` | dotted id, e.g. `cpu.usage`, `disk.c.free`, `custom.<sensorId>` | Stable; part of the HA unique id. |
| `name` | string | Display name (English); built-ins also carry `translationKey`. |
| `kind` | `sensor`, `binary_sensor` | |
| `valueType` | `number`, `string`, `bool`, `timestamp`, `enum` | |
| `unit`, `deviceClass`, `stateClass`, `entityCategory`, `icon`, `precision`, `options` | optional | Passed through to Home Assistant; values must be valid HA constants. |
| `enabledDefault` | bool | Privacy-sensitive metrics (active window title) default to `false`. |
| `intervalSeconds` | number | Sampling interval while subscribed. Event-driven metrics use `0`. |
| `deadband` | number, optional | Minimum absolute change before a new value is published. |
| `context` | `system`, `user` | `user` metrics come from the SessionHost. |

Custom sensor types: `powershell` (script output, last line parsed according to
`valueType`), `process_running` (process name), and on Windows `perf_counter`,
`cim_query`, `service_state` (T307). After v1 the type `state` (T714): a value
that is not sampled but set from outside with `qoffeeautomat state set <id>
<value>` or `PUT /sensors/{id}/value`, optionally persisted across restarts;
it lets a script report a state that a control uses as `stateMetric`.

### Mode group document (`config/modes/<id>.json`, after v1, T717)

A mode group is a set of mutually exclusive modes of the PC, for example
Normal, Study, TV gaming.

```json
{
  "schemaVersion": 1,
  "id": "pc_mode",
  "name": "PC mode",
  "icon": "mdi:monitor-dashboard",
  "enabled": true,
  "exposeToHa": true,
  "defaultMode": "normal",
  "onBoot": "reset",
  "onError": "stop",
  "modes": [
    { "id": "normal", "name": "Normal",
      "enter": [ { "command": "set_display_profile", "vars": { "Profile": "Normal-Mode" } } ],
      "exit": [] },
    { "id": "tv_gaming", "name": "TV gaming",
      "enter": [ { "command": "set_display_profile", "vars": { "Profile": "TV-Mode" } },
                 { "command": "launch_steam_big_picture" } ],
      "exit":  [ { "command": "close_steam" }, { "delaySeconds": 2 } ] }
  ]
}
```

| Aspect | Behaviour |
|--------|-----------|
| Steps | Same step objects as the `sequence` action; they reference ordinary commands, each with its own `runAs`. A step cannot switch a mode. |
| Switching | From A to B: run the `exit` steps of A, then the `enter` steps of B. Only one switch per group at a time (error `busy`). Selecting the current mode runs its `enter` steps again. |
| State | The current mode is stored by the agent and published as enum metric `mode.<groupId>`. It becomes B when the `enter` steps finished. After a failed switch with `onError: stop` it is `null` (unknown) until the next successful switch. |
| `onBoot` | What happens after an operating-system start (not after an agent restart): `keep` remembers the last mode and runs nothing; `reset` sets the state to `defaultMode` and runs nothing; `apply_default` runs the `enter` steps of `defaultMode` once the session is ready. |
| On the wire | Each exposed group yields a generated command `mode_<groupId>` with one enum variable `Mode`, a `select` control and the state metric (D-037), plus the event `mode_changed` with `group`, `from`, `to`. |

Built-in metric set for v1 (Windows): CPU usage, memory used / total / percent,
per-volume disk free / used percent, per-adapter network receive / transmit
rate, primary IPv4, battery level and charging state, uptime and last boot
time, OS version, GPU usage (performance counters), session locked, session
ready (the SessionHost of the active session accepts user commands), logged-in
user, idle time, active window title and process (off by default), master
volume and mute, microphone in use, webcam in use, display count and primary
resolution, pending reboot.

## S07 QoffeeLink protocol v1

* Endpoint: `wss://<host>:8481/link/v1`, WebSocket subprotocol `qoffeelink.v1`.
* Auth: `Authorization: Bearer <link token>` on the upgrade request. Invalid
  token: HTTP 401, no upgrade.
* Frames: one JSON object per text frame, UTF-8, `permessage-deflate` on.
  Maximum frame size 1 MiB. Field names are snake_case.
  Credentials never travel inside compressed frames (the token is only sent
  in the upgrade header and in the pairing response).
* Liveness: WebSocket ping every 30 s from both sides; the connection is
  dropped after 75 s without traffic.
* Envelope: `{"t": "<type>", ...}`. Requests carry an integer `id` chosen by
  the sender; the peer answers with `{"t":"result","id":<id>,"ok":true,"data":{}}`
  or `{"t":"result","id":<id>,"ok":false,"error":{"code":"...","message":"..."}}`.
* Unknown message types and unknown fields are ignored (forward compatibility).
* Version negotiation: the agent sends its supported range in `hello`; the
  client picks the highest common version and states it in `client_hello`. No
  overlap: the agent replies with error `unsupported_version` and closes with
  code 4406.

| `t` | Direction | Purpose | Payload |
|-----|-----------|---------|---------|
| `hello` | agent → client | First frame after connect, sent by the agent no matter who dialled. | `protocol {min,max}`, `agent {id,name,version,os,arch,hostname,macs[],ui_url}`, `manifest_rev`, `boot_id` (changes with every operating-system start, stays the same across agent restarts), `features[]` |
| `client_hello` | client → agent | Answer to `hello`. | `protocol` (int), `client {kind,name,version}` with `kind` in `homeassistant`, `hub`, `tool`, `features[]` |
| `get_manifest` | client → agent (request) | Fetch the manifest. | – ; result `data` is the manifest |
| `manifest_changed` | agent → client | Manifest revision changed. | `rev` |
| `subscribe` | client → agent (request) | Replace the metric subscription set. | `metrics`: array of ids or `"*"`; optional `intervals` (object metric id → seconds): the client receives at most one value per interval for these metrics; the result is followed by a full `state` snapshot of the subscribed metrics |
| `state` | agent → client | Changed values since the last frame. | `ts` (unix ms), `v` (object metric id → value; `null` means unavailable) |
| `run` | client → agent (request) | Execute a command. | `command`, `vars` (object), `wait` (bool, default true), `context {user_id?}`; result `data`: `run_id`, and when `wait` is true `exit_code`, `stdout`, `stderr`, `duration_ms`, `timed_out` |
| `event` | agent → client | Something happened on the PC. | `type`, `ts`, `data` |
| `bye` | agent → client | Agent goes away deliberately. | `reason` in `sleep`, `hibernate`, `shutdown`, `restart`, `service_stop`, `update` |

Optional features are announced in `features` on both sides; v1 without
features is the baseline that the Home Assistant integration needs. The hub
features are defined in S20.

Error codes: `unauthorized`, `unsupported_version`, `bad_request`,
`unknown_command`, `command_disabled`, `invalid_vars`, `busy`, `cooldown`,
`no_session`, `elevation_unavailable`, `cancelled`, `timeout`, `paused`,
`internal`.

Close codes (D-039): the standard WebSocket codes 1000 (normal), 1001 (going
away, sent after `bye`), 1009 (frame too large) and 1011 (internal error), plus
4400 `bad_request` (handshake violation, for example a missing or invalid
`client_hello`), 4401 `unauthorized` (the link token was revoked while connected)
and 4406 `unsupported_version`. The code 44xx mirrors the HTTP status that the
error would have.

State delivery rules (R-06): values are compared after applying `precision`
and `deadband`; changes are coalesced and flushed at most once per second per
connection; a metric that did not change is not sent; one `state` frame can
carry many metrics.

Manifest:

```json
{
  "rev": "b3f1c2a49d10",
  "agent": { "id": "5b0c6c1e-8e0a-4f6e-9a57-3d2f3a1c9b77", "name": "Gaming-PC", "version": "1.0.0",
             "os": "windows", "os_version": "10.0.26200", "arch": "x64", "hostname": "GAMING-PC",
             "macs": ["A4:B1:C1:00:11:22"], "ui_url": "https://gaming-pc.local:8481/" },
  "metrics": [
    { "id": "cpu.usage", "name": "CPU usage", "translation_key": "cpu_usage", "kind": "sensor",
      "value_type": "number", "unit": "%", "state_class": "measurement", "precision": 0,
      "entity_category": "diagnostic", "enabled_default": true }
  ],
  "commands": [
    { "id": "set_display_profile", "name": "Set display profile", "icon": "mdi:monitor-multiple",
      "description": "Applies a saved DisplayConfig profile.", "returns_output": true,
      "vars": [ { "name": "Profile", "type": "enum", "options": ["Normal-Mode", "TV-Mode"], "required": true } ],
      "control": { "type": "select", "var": "Profile", "state_metric": null } }
  ],
  "events": [ { "type": "session_locked", "name": "Session locked" } ],
  "features": ["wol", "notify"]
}
```

`rev` is the first 12 hex characters of the SHA-256 of the canonical manifest
JSON without the `rev` field. Canonical JSON (D-039) is UTF-8 without
insignificant whitespace, with object members sorted by name (ordinal order of
the UTF-16 code units), members whose value is `null` omitted, strings escaped
only where JSON requires it (`"`, `\`, control characters as `\b \f \n \r \t`
or lower-case `\u00xx`) and numbers written as plain integers or decimals
without exponent, in the shortest form. Clients cache the manifest by `rev` and call
`get_manifest` only when `hello.manifest_rev` or `manifest_changed.rev` differs.

Pairing endpoint (HTTPS, same listener, no bearer token):
`POST /link/v1/pair` with `{"code":"123456","client":{"kind":"homeassistant","name":"Home"}}`
returns `{"token":"<base64url 32 bytes>","agent":{"id":"...","name":"..."}}`.
Codes have 6 digits, live 5 minutes, are single-use and are invalidated after
5 failed attempts.

The normative definition is `protocol/schema/*.schema.json`; every message
type has at least one fixture in `protocol/fixtures/` that both the .NET and
the Python test suites validate and round-trip.

## S08 Management API

Base path `/api/v1`, JSON (camelCase), authenticated (S09), OpenAPI document
at `/openapi/v1.json` (it needs a session like the rest). Errors use RFC 9457
problem details (`application/problem+json`) with a machine-readable `code`; a
document that fails validation is answered with 422 and an `errors` array of
`{ path, code, message }` where `path` is a JSON pointer into the request body.
Reading a document returns its revision as `ETag` (`"3"`; the ETag of a script
is the SHA-256 of its bytes). Writes take an optional `If-Match` header with that
ETag and return 412 on a stale revision (the problem carries `currentRevision`);
creating a document whose id exists returns 409.

| Resource | Operations |
|----------|------------|
| `/auth/setup`, `/auth/login`, `/auth/logout`, `/auth/me`, `/auth/password` | First-run setup, session handling, password change. |
| `/status` | Agent version, uptime, platform, link clients, session host state, paused flag. |
| `/commands`, `/commands/{id}` | List, create, read, update, delete; `PATCH` for `enabled` and `exposeToHa`. |
| `/commands/{id}/run` | Start a run with variables; returns `runId`. |
| `/commands/validate`, `/commands/test` | Validate a draft document without saving; run a draft without saving (used by the editor's test panel). A test run is recorded in the history with source `test`, publishes no events and ignores `enabled` and the cooldown. |
| `/scripts/{name}` | Read and write script files. |
| `/runs`, `/runs/{id}`, `/runs/{id}/stream` | History; live output as Server-Sent Events. |
| `/metrics`, `/metrics/{id}` | Catalog with current value; update overrides. |
| `/metrics/stream`, `/metrics/history` | Live values (SSE) and ring-buffer history for charts. |
| `/sensors`, `/sensors/{id}`, `/sensors/test` | Custom sensor CRUD and dry run. |
| `/sensors/{id}/value` | Set the value of a sensor of type `state` (T714). |
| `/modes`, `/modes/{id}`, `/modes/{id}/switch` | Mode group CRUD, current mode, switch (T717). |
| `/link/pairing`, `/link/clients`, `/link/clients/{id}` | Start pairing (returns code, expiry, fingerprint), list and revoke clients. |
| `/events` | Recent events. |
| `/templates` | Template gallery (T701). |
| `/settings` | Agent name, ports, remote UI, logging, retention. |
| `/config/export`, `/config/import` | Zip export of all documents and scripts (no secrets) and import with validation: the whole archive is validated before anything is written; items with the same id are replaced, settings are imported only on request (`?settings=true`). |
| `/logs`, `/diagnostics` | Log tail and a redacted diagnostics bundle. |

Unauthenticated endpoints: `/healthz`, `/auth/setup` (only while the setup
token file exists, that is until the first password is set or after
`qoffeeautomat reset-password` wrote a new token, and only with that token),
`/auth/login`, static UI files. Everything else below `/api/v1` answers 401
without a session; this is enforced by one authorization policy on the route
group and checked by a test over the endpoint data source. Requests that change
state also need the header `X-QoffeeAutomat-Csrf: 1` (S09 item 1). Requests
from a non-loopback address are answered with 403 unless "remote UI" is on
(D-018).

## S09 Security model

Threats considered: other users on the same PC, other devices on the LAN,
malicious values arriving from Home Assistant, stolen link tokens.

1. Authentication everywhere (D-017). First run: the agent writes a random
   setup token to `secrets/setup-token.txt` (readable by Administrators only);
   the UI asks for it once and lets the user set a password. Passwords are
   stored as salted PBKDF2-SHA256 hashes (at least 600 000 iterations) or
   Argon2id. Sessions use an `HttpOnly`, `SameSite=Strict` cookie (`Secure` on
   HTTPS); state-changing requests require the anti-forgery header
   `X-QoffeeAutomat-Csrf: 1` and, when an `Origin` header is present, the
   agent's own origin. Login and setup are rate limited with lockout (five
   failures per address within 15 minutes lock that address out for 15
   minutes; 30 failures from all addresses lock everybody out). The password is
   reset with `qoffeeautomat reset-password` (local administrator or root),
   which writes a new setup token file; the old password works until a new one
   is set through the setup page, which ends all sessions.
2. Link tokens are 32 random bytes, shown to the client once, stored as
   SHA-256 hashes, revocable individually. Several clients may be paired.
3. TLS: the agent creates a self-signed ECDSA P-256 certificate on first start
   (SANs: hostname, `<hostname>.local`). Clients pin its SHA-256 fingerprint at
   pairing time. The pairing dialog in the UI and the HA config flow both show
   the fingerprint so the user can compare them. A user-supplied certificate
   can replace it. A PAKE-based pairing is a possible later hardening.
4. No injection (D-012). All variable values are validated against the
   parameter definition by the agent.
5. File system ACLs on config, scripts and secrets (S05). The agent refuses to
   start a `runAs: system` script whose file or directory is writable by
   non-administrators and logs a security warning.
6. Secrets at rest use DPAPI machine scope on Windows; on Linux a key file with
   mode 0600.
7. SessionHost pipe: the agent is the pipe server; the pipe ACL allows only
   interactive users to connect; the agent checks that the client process image
   path is the installed SessionHost; the SessionHost checks that the pipe
   server process runs as SYSTEM.
8. Every run is written to the audit log with source, client id, HA user id
   (if supplied), variables (values of parameters marked secret are redacted),
   exit code and duration.
9. Remote UI is off by default. The installer opens the link port only for
   private and domain firewall profiles.
10. Supply chain: pinned GitHub Actions, Dependabot, CodeQL, dependency review,
    SBOM and provenance attestation on releases (S12).

## S10 Home Assistant integration

* Domain `qoffeeautomat`, `integration_type: device`, `iot_class: local_push`,
  `config_flow: true`, zeroconf `_qoffeeautomat._tcp.local.`, no external Python
  requirements (aiohttp ships with Home Assistant).
* One config entry per PC; unique id is the agent id. One device per PC with
  `identifiers={(DOMAIN, agent_id)}`, `connections` from the MAC list,
  `sw_version`, `configuration_url` pointing at the agent UI.
* Config flow steps: discovery or manual host and port → show fingerprint →
  ask for the pairing code → create entry (stores host, port, token,
  fingerprint). Also: reauthentication, reconfiguration, and address updates
  from rediscovery.
* `client.py` contains the protocol client with no Home Assistant imports so
  it can become a PyPI library later. Reconnect with exponential backoff and
  jitter (1 s up to 60 s). Two shortcuts keep the delay after a PC start
  small: a zeroconf announcement of an already configured agent triggers an
  immediate connection attempt, and after the Wake-on-LAN button was pressed
  the client retries every second for up to five minutes.
* Entities are created from the manifest and updated on `manifest_changed`:
  new ones are added, vanished ones are removed from the entity registry.
  Unique id: `<agent_id>_<metric or command id>`. `has_entity_name` is true.
* State updates are pushed per entity (a `state` frame only touches the
  entities whose metric changed). The integration re-sends `subscribe`
  whenever the set of enabled metric entities changes (D-007).
* Platforms: `sensor`, `binary_sensor`, `button`, `switch`, `select`,
  `number`, `text`, `event`; after v1 `notify`, `update`, `media_player` and
  the Wake-on-LAN `button` (M7).
* On every connect the integration compares `hello.boot_id` with the last
  value it saw and fires the event `booted` (event entity and bus) when it
  changed. This is the reliable trigger for "the PC was started", because
  events raised on the PC before Home Assistant is connected are not
  delivered.
* A connectivity `binary_sensor` stays available while the PC is offline; all
  other entities become unavailable. The last `bye.reason` is exposed as an
  enum sensor (`running`, `sleep`, `hibernate`, `shutdown`, `restart`, `unknown`).
* Action `qoffeeautomat.run_command`: target device; fields `command`,
  `variables` (object), `wait` (default true), `timeout`; optional response data
  `exit_code`, `stdout`, `stderr`, `duration_ms`. After v1 (T712) also `wake`
  (send Wake-on-LAN first when the PC is offline), `wait_for` (`none`,
  `connected`, `session_ready`) and `ready_timeout` (default 180 seconds), so
  one call can start the PC, wait until it can execute the command and run it. Agent errors map to
  `ServiceValidationError` or `HomeAssistantError` with translated messages.
* Agent `event` frames fire the event entity and a `qoffeeautomat_event` bus
  event.
* Diagnostics with redaction, a repair issue for protocol mismatch, English
  and German translations, brand images in `brand/`.
* Quality target: all Bronze and Silver rules of the integration quality scale
  and the Gold rules `devices`, `diagnostics`, `discovery`, `discovery-update-info`,
  `dynamic-devices`, `stale-devices`, `entity-category`, `entity-device-class`,
  `entity-disabled-by-default`, `entity-translations`, `exception-translations`,
  `reconfiguration-flow`, `repair-issues`. Tracked in `quality_scale.yaml`.

## S11 Windows specifics

* Service name `QoffeeAutomat`, account LocalSystem, automatic (delayed) start,
  recovery action restart. Logs to the Windows Event Log and rolling files.
* The agent handles session change notifications (logon, logoff, lock,
  unlock) and power events (suspend, resume). Before suspend or shutdown it
  sends `bye` to all link clients.
* SessionHost: started for every interactive session through
  `WTSQueryUserToken` and `CreateProcessAsUser`, restarted when it exits, one
  instance per session. When the SessionHost of the active console session has
  completed its handshake, the metric `session.ready` turns on and the event
  `session_ready` is published; both reset when it disconnects or the user
  logs off. Automations wait for this signal before sending `runAs: user`
  commands after a start. IPC is length-prefixed JSON over the named pipe
  `QoffeeAutomat.Session`. Messages: `hello`, `run`, `run_output`, `run_result`,
  `cancel`, `metrics_subscribe`, `metrics_state`, `notify`, `event`, `shutdown`.
* `runAs: user_elevated` (D-036): the agent obtains the token of the active
  console session, takes its linked elevated token and starts the process
  with `CreateProcessAsUser` on the user's desktop, capturing output itself.
  No UAC prompt appears. Without a linked token (standard user, or UAC
  switched off) the run fails with `elevation_unavailable`.
* The command engine routes `runAs: user` commands to the SessionHost of the
  active console session. The SessionHost runs with the user's standard
  (non-elevated) token; commands that need elevation use `runAs: system`.
* Built-in actions for v1: `power.shutdown`, `power.restart`, `power.sleep`,
  `power.hibernate`, `session.lock`, `session.logoff`, `display.off`,
  `audio.set_volume`, `audio.set_mute`, `media.key` (play_pause, next,
  previous, stop), `app.launch`, `url.open`, `process.kill`. After v1:
  `notify.toast`, `audio.set_default_output`, `audio.set_default_input`,
  `power.keep_awake`, `power.cancel`, `media.control`, and the optional
  variables `DelaySeconds` and `Message` on the power and session actions
  (countdown dialog with a cancel button on the desktop).
* Tray icon (WinForms `NotifyIcon`) in the SessionHost: connection status,
  open UI, pause and resume commands.

## S12 GitHub setup

Workflows (`.github/workflows/`), all with least-privilege `permissions`,
concurrency groups and actions pinned by commit SHA:

| File | Trigger | Content |
|------|---------|---------|
| `ci-dotnet.yml` | PR, push to main (path filtered) | restore, `dotnet format --verify-no-changes`, build with warnings as errors, tests with coverage on `ubuntu-latest` and `windows-latest`, upload test results. |
| `ci-integration.yml` | PR, push to main (path filtered) | ruff, mypy, pytest with coverage, hassfest action, HACS validation action. |
| `ci-protocol.yml` | PR touching `protocol/` | Schema lint and fixture validation. |
| `ci-meta.yml` | PR | Plan validation (`scripts/plan/validate.py`), actionlint, workflow security lint, markdown link check. |
| `pr-title.yml` | PR | Conventional Commit title check. |
| `labeler.yml` | PR | Path-based labels. |
| `codeql.yml` | PR, push, weekly | CodeQL for `csharp`, `python`, `actions`. |
| `dependency-review.yml` | PR | Blocks vulnerable or disallowed-license dependencies. |
| `e2e.yml` | nightly, manual, release PRs | Docker compose end-to-end test with Home Assistant (T508). |
| `release-please.yml` | push to main | Maintains the release PR; when it is merged, creates the tag and a draft GitHub release and calls `release.yml`. |
| `release.yml` | called by `release-please.yml`, manual for prereleases | Builds artifacts, checksums, SBOM, attestations and signatures, attaches them to the draft release and then publishes it (S13). |
| `docs.yml` | push to main (docs paths) | Builds MkDocs and deploys to GitHub Pages. |
| `plan-sync.yml` | push to main touching `docs/plan/tasks.yaml`, manual | Syncs tasks to GitHub issues and the project board (T010). |
| `winget.yml` | release published | Submits the winget manifest (T607). |
| `scorecard.yml` | weekly | OpenSSF Scorecard. |
| `copilot-setup-steps.yml` | used by GitHub coding agents | Preinstalls the .NET SDK, Python and dev dependencies. |

Other GitHub features:

| Feature | Use |
|---------|-----|
| Dependabot | `nuget`, `pip`, `github-actions`, `devcontainers`; weekly, grouped minor and patch updates. |
| Security | Secret scanning with push protection, private vulnerability reporting, Dependabot alerts, `SECURITY.md`. |
| Rulesets | `main protection`: pull request required, required status checks, squash merge only, linear history, no force push, no deletion. `release tags`: tags `v*` cannot be moved or deleted. Immutable releases enabled. |
| Issue forms | Bug report, feature request, command or sensor template request; `config.yml` links to Discussions and the security policy. |
| PR template, CODEOWNERS | Checklist with task id; `@toeck98` owns everything. |
| Labels as code | `.github/labels.yml`: `area:*` (same values as the `area` field in `tasks.yaml`), `type:*`, `task`, `human`, `human-verify`, `blocked`, `needs-triage`, `release`, `dependencies`, `good first issue`, `help wanted`. |
| Milestones and Projects | One milestone per plan milestone, titled `<id> <title>` (for example `M0 Foundation`); one project board fed by `plan-sync.yml`. |
| Discussions | Categories Q&A, Ideas, Show and tell (shared commands). |
| Pages | User documentation. |
| Environments | `release` (signing secrets, required reviewer), `github-pages`. |
| Releases | Generated notes from Conventional Commits, assets listed in S13. |
| Artifact attestations | Build provenance for every release asset. |
| Packages (GHCR) | Hub container image (T918) and agent test image (T508). |
| Codespaces / devcontainer | .NET SDK, Python, a Home Assistant development instance. |
| Repository metadata | Description and topics (`home-assistant`, `hacs`, `integration`, `windows`, `dotnet`); required by HACS. |

Settings that cannot be stored in the repository are applied by the
idempotent script `scripts/github/setup-repo.sh`, which the owner runs with
the `gh` CLI. The script and `.github/labels.yml` are delivered together with
this plan and can be run before any other task; T009 reviews them and adds
the required status checks once the CI workflows exist.

## S13 Versioning and release

* SemVer, single version for the whole repository (D-002), starting at `0.1.0`;
  `1.0.0` at the end of milestone M6.
* release-please updates `CHANGELOG.md`, `Directory.Build.props`
  (`<Version>`), `custom_components/qoffeeautomat/manifest.json` (`version`).
* Release assets: `QoffeeAutomat-Setup-<version>-x64.exe`, `...-arm64.exe`,
  `QoffeeAutomat-<version>-win-x64.zip`, `...-win-arm64.zip`, `qoffeeautomat.zip`
  (the integration, for HACS), `SHA256SUMS.txt`, `sbom.spdx.json`; from M8
  Linux tarballs and packages.
* Immutable releases are enabled, so a published release can no longer be
  changed. Assets are therefore attached while the release is a draft, and
  publishing is the last step of `release.yml`.
* The agent is published self-contained and single-file with ReadyToRun.
* Compatibility promise from 1.0: protocol v1 messages stay backward
  compatible; config documents are migrated forward automatically; breaking
  protocol changes need a new protocol version and one release of overlap.

## S14 Quality and testing

Global checks every PR must pass:

```
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test
ruff check . && ruff format --check .
mypy custom_components/qoffeeautomat
pytest tests_integration
python scripts/plan/validate.py
```

| Layer | Tooling | Notes |
|-------|---------|-------|
| .NET unit | xUnit, coverage through `dotnet test` | Core and Contracts at 80 % line coverage or more. |
| .NET integration | `WebApplicationFactory`, real WebSocket and HTTP against the in-memory host with `Platform.Simulated` | Covers link server, pairing, API, auth. |
| Protocol conformance | Shared fixtures validated by both languages | Fails CI when either side drifts. |
| Windows | Tests on `windows-latest`; manual checklist for session features | Tasks carry `human_verify: true` where needed. |
| UI | bUnit for components, Playwright smoke test against the agent with `Platform.Simulated` | |
| Integration | `pytest-homeassistant-custom-component`, fake agent server replaying fixtures | Config flow at 100 % coverage (quality scale rule). |
| End to end | Docker compose: Home Assistant container and Linux agent with `Platform.Simulated` | Pairs, checks entities, runs a command with variables. |
| Performance budget | Benchmark task T218 | Idle agent with 20 subscribed metrics: under 1 % of one core and under 120 MB working set; a typical `state` frame under 300 bytes. |

Code conventions: nullable reference types on, warnings as errors, analyzers
at latest recommended level, file-scoped namespaces, async all the way,
`System.Text.Json` source generation (no reflection-based serialization in
Contracts), central package management, no `InternalsVisibleTo` across product
assemblies. Python: full type hints, ruff rule set aligned with Home Assistant
core, no blocking I/O in the event loop.

## S15 Milestones

| Id | Title | Outcome |
|----|-------|---------|
| M0 | Foundation | Clean repository, tooling, CI, agent instructions, GitHub configuration. |
| M1 | Protocol and contracts | QoffeeLink schemas, fixtures, C# and Python models, config document models. |
| M2 | Agent core | Cross-platform agent with simulated platform: config store, engines, link server, management API, auth. |
| M3 | Windows platform | Service, SessionHost, real metrics, built-in commands, prototype features as templates. |
| M4 | Web UI | Blazor UI for dashboard, commands, editor, sensors, pairing, settings. |
| M5 | Home Assistant integration | Config flow, dynamic entities, actions, diagnostics, end-to-end test. |
| M6 | Release 1.0 | Installer, release automation, signing, documentation, HACS readiness, security review. |
| M7 | Quality of life (1.x) | Templates, notifications, Wake-on-LAN, updates, maintenance mode, modes, audio device switching, media player and more. |
| M8 | Linux support | Linux platform, systemd, packaging. |
| M9 | Central hub | Hub with enrollment, configuration roll-out, metric store, tunnel, fleet link for Home Assistant, container image and Home Assistant app. |

M4 and M5 only depend on M1 and M2 and can run in parallel with M3. The first
end-to-end slice (simulated agent paired with a real Home Assistant) exists
after T210, T211, T501, T502, T503, T504.

## S16 Quality-of-life catalog

| Feature | Milestone | Task |
|---------|-----------|------|
| Zeroconf discovery and pairing code instead of token copying | M2, M5 | T211, T216, T502 |
| Disabled HA entities cost no CPU on the PC | M2, M5 | T205, T504 |
| Test run in the UI with a variable form and live output | M4 | T404 |
| Preview of the resulting HA entity and a copyable automation snippet | M4 | T404 |
| Live dashboard with charts | M4 | T402 |
| Run history and audit log | M2, M4 | T207, T407 |
| Config export and import, revision history with undo | M2, M4 | T202, T407 |
| Diagnostics bundle download | M4 | T407 |
| Link to the agent UI from the HA device page | M5 | T503 |
| "PC is asleep" versus "PC is unreachable" distinction | M3, M5 | T301, T503 |
| Events from PC to HA (lock, unlock, logon, custom `emit`) | M2, M5 | T217, T506 |
| "Session ready" signal so HA can orchestrate desktop commands right after a start | M2, M3 | T217, T302, T305 |
| Fast reconnect after a PC start (zeroconf announcement, fast retry after wake) | M2, M5, M7 | T216, T501, T502, T703 |
| Tray icon with status, open UI, pause | M3 | T310 |
| Template gallery for commands and sensors | M7 | T701 |
| Notifications from HA on the PC with action buttons | M7 | T702 |
| Wake-on-LAN button in HA | M7 | T703 |
| Update entity in HA and agent self-update | M7 | T704 |
| Maintenance mode and per-command conditions | M7 | T705 |
| Secrets for scripts | M7 | T706 |
| Hardware temperatures and fans | M7 | T707 |
| Global hotkeys that fire HA events | M7 | T708 |
| Prometheus endpoint | M7 | T709 |
| Reverse link for roaming laptops | M7 | T710 |
| One-click UI login from the tray | M7 | T711 |
| Desktop programs that need administrator rights start without a UAC prompt | M3 | T311 |
| "PC was started" event that cannot be missed | M5 | T503 |
| Wake, wait until ready and run in a single action call | M7 | T712 |
| Audio output and input device switching | M7 | T713 |
| State values set by scripts | M7 | T714 |
| Keep-awake switch; power actions with countdown and cancel | M7 | T715 |
| PC as media player in Home Assistant | M7 | T716 |
| Modes with enter and exit actions and a real state | M7 | T717 |

## S17 Future: Linux and hub

Linux (M8): the agent runs as a systemd system service; the session helper is
a systemd user service talking over a Unix domain socket; action type `bash`;
metrics from `/proc`, `/sys` and logind; packaging as tarball and `.deb`.
Features built on Windows APIs (elevated user context, audio device
switching, media player, keep awake, countdown dialog) get Linux equivalents
where the desktop offers one and are otherwise reported as unsupported by
validation.

Hub (M9, R-12): designed in S20. Constraints that already apply to v1 so the
hub stays cheap: D-006, D-013, D-014, the layered config store (T202), the
`features` and `subscribe.intervals` fields of S07, stable metric ids, a
stable agent id, and several simultaneous link clients.

## S18 Open items for the owner

Status `decided` means the owner has answered (2026-11-14); a remaining action
is listed where the owner still has to do something.

| Id | Item | Status | Decision or default | Needed by |
|----|------|--------|---------------------|-----------|
| O-01 | Run `scripts/github/setup-repo.sh` (delivered with the plan; `--dry-run` first). Run it again with the `--required-check` options that T009 provides. | open (action) | Required. | T009 |
| O-02 | Code signing through SignPath Foundation. | decided | Apply. Remaining action: the owner submits the application; until it is accepted, releases ship unsigned with checksums and attestations. | T603 |
| O-03 | Default ports. | decided | Keep 8480 (UI and API on loopback), 8481 (QoffeeLink), 8490 (hub). | T201, T902 |
| O-04 | HACS default store. | decided | Submit after 1.0. Remaining action: the owner opens the submission. Until then installation as HACS custom repository. | T605 |
| O-05 | Icon and logo artwork. | decided | Placeholder artwork is delivered with this plan and lives in `assets/brand/`. Remaining action: the owner replaces it before 1.0. | T001, T507, T606 |
| O-06 | Verify session features on a real Windows desktop (display profiles, program launch, tray). | open (action) | Required. | T302, T303, T309, T310 |
| O-07 | Review the hub design in S20 before milestone M9 starts. | open | Build as designed. | T901 |
| O-08 | Decide which PCs use the hub route in Home Assistant and which stay directly paired. | open | All stay directly paired. | T914 |

## S19 Risks

| Risk | Mitigation |
|------|------------|
| A LocalSystem service that executes scripts is an attractive target. | S09; security review T606; authentication on loopback; ACL checks. |
| Session 0 isolation breaks desktop commands. | D-009; SessionHost built and verified early in M3. |
| Agent and integration drift apart. | Shared schemas and fixtures; nightly end-to-end test. |
| Lockstep releases produce integration updates without changes. | Accepted (D-002); escape hatch documented. |
| mDNS libraries for .NET are thinly maintained. | T216 starts with an evaluation; manual host entry always works. |
| Unsigned installer triggers SmartScreen. | O-02; checksums, attestations, winget. |
| Windows-only code cannot be run by Linux-hosted agents. | D-021; `windows-latest` CI; `human_verify` tasks. |
| The hub is a single point from which code can be run on every PC. | S20 hub security; signed desired state; audit log; security review T919. |
| The repository is at once a HACS repository and a Home Assistant app repository. | D-034; CI check for stray config files; escape hatch: move the app definition to its own small repository. |
| Home Assistant API changes between releases. | Pin the test harness, nightly run against the `stable` and `beta` containers. |

## S20 Hub design

The hub is the central place of R-12. It is optional: an agent works without
it, and everything in S01 to S14 stays valid. Design target: up to 50 agents
with 30 collected metrics each on small hardware (the Home Assistant host).

### Hub decisions

| Id | Decision | Rationale |
|----|----------|-----------|
| D-024 | The hub is one ASP.NET Core application (`QoffeeAutomat.Hub`, `net10.0`) shipped as a multi-arch container image on GHCR and as a Home Assistant app (the new name for add-ons) with ingress. No Windows package. | Owner's choice. One artifact for both deployments. |
| D-025 | Agents always dial out to the hub (`wss://<hub>/hub/v1/agent`). After the handshake the hub acts as a QoffeeLink client of kind `hub`. Hub functions are optional protocol features negotiated in `hello` and `client_hello`; protocol v1 is not broken. | No inbound port on the PCs, works across subnets and NAT, reuses the outbound connector of T710. |
| D-026 | Enrollment uses an enrollment string created in the hub UI. It carries hub URL, a token, the hub certificate fingerprint and the hub's signing public key. The agent exchanges it for a per-agent credential. | One paste (or one installer parameter) joins a PC; suitable for unattended roll-out. |
| D-027 | Configuration is distributed as desired state: per agent the hub computes the complete set of hub documents, numbers it with a generation, signs the item list (ECDSA P-256) and the agent pulls only documents whose hash it lacks. Apply is atomic per generation. | Idempotent, resumable, cheap on reconnect, tamper-evident even behind a TLS-terminating reverse proxy. |
| D-028 | Hub documents live in a separate layer on the agent (`config/hub/`). The effective configuration is the hub layer over the local layer; on equal kind and id the hub document wins and the local one is kept but shadowed. Hub documents are read-only locally. | Local work is never destroyed; leaving the hub restores it. |
| D-029 | The hub sets a policy per agent: `local_config` is `allow` or `locked`, `direct_link` is `allow` or `deny`. | Owner's choice: per PC or group the hub decides whether local commands are permitted, and whether the PC still accepts direct Home Assistant pairing. |
| D-030 | Hub configuration is organised as a library of documents, profiles (named sets of documents, variables and policy) and assignments of profiles to targets `all`, `tag:<tag>` or `agent:<id>`. More specific targets override less specific ones; ties are broken by profile priority. | Simple mental model that covers "all PCs", groups and single PCs without a rule language. |
| D-031 | Home Assistant can reach PCs in two ways: direct pairing (default, S07) or through the hub's fleet link, where one connection carries all agents. Both can be used in the same Home Assistant instance, but each agent uses exactly one route. Entity unique ids are identical on both routes. | Owner's choice. Direct pairing keeps working when the hub is down; the fleet link needs a single pairing and no inbound access to the PCs. |
| D-032 | Metrics are stored in the hub's SQLite database with raw values and 5-minute and 1-hour roll-ups, behind an `IMetricStore` interface. A Prometheus endpoint exports current values. | Owner's choice. No external database needed; Grafana and others can still be attached. |
| D-033 | The hub UI is a Blazor WebAssembly app that reuses `Ui.Components` through two `IAgentApi` implementations: one tunnels to a live agent, one edits library documents. | The command and sensor editors are written once (D-015). |
| D-034 | The Home Assistant app definition lives in this repository under `packaging/hub/ha-app/` and uses the prebuilt GHCR image. No other file named `config.yaml`, `config.yml` or `config.json` may exist in the repository outside hidden folders. | The Supervisor searches app repositories for these file names at any depth *(verified 2026-11-14)*. |
| D-035 | Not part of the first hub release: alerting, single sign-on (OIDC), distribution of script secrets, metric backfill after an outage, staged roll-outs, an external time-series store, hub high availability. | Keeps the first release small; Home Assistant already covers alerting and automation. |

### Hub architecture

```
 Home Assistant ──fleet link (QoffeeLink + feature fleet)──┐
 browser ──hub UI, /api/v1 (direct or HA ingress)─────────┤
 Prometheus ──/metrics────────────────────────────────────┤
                                                 +--------v---------+
                                                 | QoffeeAutomat.Hub |
                                                 |  agent gateway   |
                                                 |  config service  |
                                                 |  metric store    |
                                                 |  tunnel proxy    |
                                                 +--------^---------+
        agents dial out: QoffeeLink + config_sync, mgmt_tunnel
          PC 1 ───────────────────┘      │      └─────────────────── PC n
```

Listeners: `https://0.0.0.0:8490` (everything; self-signed certificate by
default, user certificate or plain HTTP behind a trusted reverse proxy as
options) and, only in the Home Assistant app, `http://0.0.0.0:8099` for
ingress, which accepts connections from `172.30.32.2` only.

| Endpoint | Client | Auth | Purpose |
|----------|--------|------|---------|
| `POST /hub/v1/enroll` | agent | enrollment token | Exchange the token for an agent credential. |
| `GET /hub/v1/agent` (WebSocket) | agent | agent credential | Agent link. |
| `POST /hub/v1/pair` | Home Assistant | pairing code | Same flow as S07 pairing; returns a fleet token. |
| `GET /hub/v1/fleet` (WebSocket) | Home Assistant | fleet token | Fleet link. |
| `/api/v1/...` | hub UI, tools | session or API token | Hub management API (OpenAPI published). |
| `/api/v1/agents/{id}/agent-api/...` | hub UI | session | Tunnel to the agent's management API. |
| `/metrics` | Prometheus | read-only token | Export. |
| `/healthz` | anyone | none | Liveness. |

### Enrollment and trust

1. An administrator creates an enrollment token in the hub UI: optional
   expiry, maximum number of uses, tags to assign, approval required or not.
   The UI shows the enrollment string
   `qahub1.<base64url(JSON {"url","token","fp","key"})>`.
2. On the PC the string is entered in the agent UI, passed to the installer
   (`/HUBENROLL=<string>`) or to `qoffeeautomat hub join <string>`. The agent UI
   states clearly that joining gives the hub full control over the PC.
3. The agent calls `POST /hub/v1/enroll` (pinning `fp`; a missing `fp` means
   normal certificate validation) with the token and its agent info. The hub
   answers with a credential (32 random bytes, stored hashed on the hub,
   protected by the secrets store on the agent). With approval required the
   agent stays in state `pending` until an administrator accepts it.
4. The agent connects to the agent link and reconnects with backoff and jitter
   (1 second up to 5 minutes).
5. Rotation: the hub sends `credential_rotate`; the old credential stays valid
   until the agent has acknowledged the new one.
6. Leaving, started on the agent: the local administrator chooses whether hub
   documents are converted to local documents or removed. Removal, started on
   the hub: the hub sends `unenroll` when the agent is online; an agent that
   later connects with a revoked credential receives HTTP 401 with code
   `revoked`. In both hub-initiated cases the agent deletes the hub layer and
   its credential, and the local layer becomes active again.

### Hub link extensions

The agent sends `hello` first on every QoffeeLink connection, no matter who
dialled. Features are negotiated through `hello.features` and
`client_hello.features`; a message of a feature the peer did not announce
must not be sent.

| Feature | `t` | Direction | Payload and behaviour |
|---------|-----|-----------|-----------------------|
| `config_sync` | `config_desired` | hub → agent (request) | `generation` (int, increasing per agent), `policy {local_config, direct_link}`, `items [{kind, id, hash}]`, `sig`. Result: `need [{kind, id}]`. |
| `config_sync` | `config_documents` | hub → agent (request) | `generation`, `documents [{kind, id, hash, body}]`, `final` (bool). Several frames when the set exceeds the frame limit. |
| `config_sync` | `config_status` | agent → hub | `generation`, `state` in `applied`, `failed`; `results [{kind, id, state, errors[]}]` with item states `applied`, `skipped_unsupported`, `invalid`. Also sent once after every handshake with the last applied generation. |
| `mgmt_tunnel` | `mgmt_request` | hub → agent (request) | `method`, `path` (must start with `/api/v1/`), `query`, `body`, `principal {user, role}`. Result: `status`, `body`. |
| `mgmt_tunnel` | `mgmt_stream_open` | hub → agent (request) | `path` of a Server-Sent-Events endpoint, `principal`. Result: `sid`. |
| `mgmt_tunnel` | `mgmt_stream_data`, `mgmt_stream_close` | agent → hub, either | `sid`, `event`, `data`; close with `sid`. |
| (always) | `credential_rotate` | hub → agent (request) | `credential`. |
| (always) | `unenroll` | hub → agent | `reason`. |

Document kinds in `config_sync`: `command`, `sensor`, `script`, `mode`,
`metric_overrides`, `variables`. `hash` is the SHA-256 of the canonical JSON
body (for scripts: of the UTF-8 text). `sig` is the base64 ECDSA P-256
signature over the canonical JSON of `{agent_id, generation, policy, items}`,
verified against the key pinned at enrollment; because the item list contains
the hashes, the bodies are authenticated too.

Apply rules on the agent: verify signature and hashes; validate every
document with the normal validators; if any document is `invalid` the
generation fails and the previous generation stays active; documents whose
action or sensor type the platform does not support are skipped and reported;
otherwise the hub layer is replaced atomically. A generation lower than or
equal to the applied one is ignored. `variables` are key/value pairs exposed
to every run as `QA_HUBVAR_<NAME>` environment variables (never spliced,
D-012). The agent keeps working with the last applied generation while the
hub is unreachable.

Policy effects: with `local_config: locked` the local layer is inactive and
the local UI and API reject changes to commands, sensors, scripts and metric
overrides; viewing, password change and leaving the hub stay possible. With
`direct_link: deny` the agent stops advertising through zeroconf, rejects
pairing and closes direct link clients.

Tunnel authorisation on the agent: the tunnel can be switched off locally;
role `viewer` may only issue GET requests, `operator` additionally the run
and test endpoints, `admin` everything; hub documents stay read-only through
the tunnel as well; every tunnelled request is written to the agent's audit
log with the hub user.

### Fleet link (Home Assistant through the hub)

Endpoint `/hub/v1/fleet`, QoffeeLink framing and liveness as in S07. The hub
sends `hello` with `hub {id, name, version}` in place of `agent` and the
feature `fleet`. Agent-scoped messages are the S07 messages with an
additional field `a` holding the agent id.

| `t` | Direction | Payload |
|-----|-----------|---------|
| `get_agents` | client → hub (request) | Result: `agents [{id, name, online, power_state, manifest_rev, agent {...}}]`, limited to the token's scope. |
| `agent_added`, `agent_removed` | hub → client | `a`, and for added the same object as in `get_agents`. |
| `agent_status` | hub → client | `a`, `online`, `power_state` (last `bye` reason or `unknown`). |
| `get_manifest`, `subscribe`, `run` | client → hub (requests) | As S07 plus `a`. Manifests are served from the hub's cache. |
| `manifest_changed`, `state`, `event` | hub → client | As S07 plus `a`. |

Additional error codes: `unknown_agent`, `agent_offline`, `forbidden`.
A fleet token is scoped to all agents or to a set of tags. Towards each agent
the hub holds one subscription that is the union of its collection policy, the
fleet clients' subscriptions and open hub UI views; it uses
`subscribe.intervals` (S07) so that stored metrics arrive at the collection
interval while metrics watched live arrive unthrottled. `run` requests are
forwarded with source `ha` and the Home Assistant user id and appear in the
audit logs of hub and agent.

Integration side: a second config-flow path "Connect to a hub" creates one
config entry per hub (unique id is the hub id) with one device per agent,
linked to a hub device through `via_device`. Agents appear and disappear
dynamically. An agent that already has a direct config entry is skipped by
the hub entry and a repair issue offers to switch its route; switching keeps
entity ids and customisations because unique ids and device identifiers are
the same on both routes.

### Configuration model on the hub

| Object | Content |
|--------|---------|
| Library document | A command, sensor, script, mode group or metric-override document in the agent's formats (S06), with revision history. |
| Profile | Name, priority (int), optional OS filter, list of library documents pinned at revisions, variables, policy (`local_config`, `direct_link`, each also `inherit`). Has a draft and immutable published revisions. |
| Assignment | Profile → target `all`, `tag:<tag>` or `agent:<id>`. |
| Agent | Id, name, tags, enrollment data, desired and applied generation, per-document status. |

Desired state of an agent is a pure function: take the published revisions
of all profiles assigned to matching targets and passing the OS filter; order
them by target specificity (`all`, then `tag`, then `agent`) and then by
priority; later entries override earlier ones on equal kind and id; variables
and policy merge the same way; policy defaults are `allow` and `allow`. If
the resulting set differs from the last generation, the generation number is
increased and the set is signed and pushed. Publishing shows a preview with
the affected agents and per-agent differences. Roll-back republishes an
earlier revision. Overrides between profiles are shown as warnings.

### Metrics on the hub

A collection policy (global default, overridable per profile) lists metric
ids or prefixes and a collection interval (default 60 seconds). Storage:

| Table | Content | Default retention |
|-------|---------|-------------------|
| `series` | agent id, metric id, value type, unit | while the agent exists |
| `raw` | series, timestamp, numeric or boolean value | 48 hours |
| `states` | series, timestamp, text value (string and enum metrics, changes only) | 90 days |
| `rollup_5m` | series, bucket, min, avg, max, count | 30 days |
| `rollup_1h` | series, bucket, min, avg, max, count | 2 years |

The query API picks the resolution from the requested time range. Retention
and interval are configurable; the documentation contains a sizing formula.
`/metrics` exports the latest numeric and boolean values as
`qoffeeautomat_<metric id with dots replaced by underscores>{agent_id, agent}`
plus hub health metrics, protected by a read-only token.

### Hub security

The hub can run code as SYSTEM on every enrolled PC and is therefore the most
sensitive component of the project.

1. Authentication is mandatory. Local accounts with the password rules of S09
   and the roles `admin` (everything), `operator` (view, run commands) and
   `viewer`. API tokens carry a role. Behind Home Assistant ingress the user
   supplied by the Supervisor is trusted only on the ingress listener and
   mapped to a configurable role (default `admin` for Home Assistant
   administrators).
2. Every change to library, profiles, assignments, tokens and users, every
   publish and every command run is written to the hub audit log with user,
   time and affected agents.
3. Agent credentials, fleet tokens and API tokens are stored as hashes. The
   signing key and TLS key are protected at rest in the data volume.
4. Agents verify the signature of every desired state (D-027) and validate
   every document themselves; the hub is never trusted to have validated.
5. Enrollment tokens expire, are limited in uses and can require approval.
6. The enrollment endpoint and logins are rate limited.
7. Publishing a profile that contains `runAs: system` scripts requires role
   `admin` and shows the affected agents before confirmation.
