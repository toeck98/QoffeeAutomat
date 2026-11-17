# Commands

A **command** is something the PC can do on request: run a PowerShell script, start a program, carry out a built-in action
such as *sleep*, or run other commands in order. You author commands in the web interface of the agent (page **Commands**,
**New command**); Home Assistant, the web interface and the tray can then start them. A command can also become an entity
in Home Assistant (a button, a switch, ...).

Every command is a JSON document in `%ProgramData%\QoffeeAutomat\config\commands\<id>.json`. The editor writes it for you;
you can also paste JSON into it or [import](backup-restore.md) it. Older revisions are kept and can be restored.

## The pieces of a command

| Field (editor label) | Meaning |
|----------------------|---------|
| `id` (*Id*) | Lower-case letters, digits and `_`, starting with a letter (up to 63 characters). Fixed after the first save. It is the name you use in `qoffeeautomat.run_command` and part of the entity's unique id. |
| `name`, `description`, `icon` | Display name, help text, and a Home Assistant icon such as `mdi:power`. |
| `enabled` (*Enabled*) | A disabled command is rejected from every source. |
| `exposeToHa` (*Show in Home Assistant*) | Only enabled **and** exposed commands appear in Home Assistant. |
| `runAs` (*Runs as*) | `system`, `user` or `user_elevated`; see [Run context](#run-context). |
| `action` | What it does; see [Action types](#action-types). |
| `parameters` | The variables of a run; see [Variables](#variables). |
| `haControl` | The Home Assistant entity; see [Control mapping](#control-mapping). |
| `timeoutSeconds` | 1 to 86400, default 60. After that the whole process tree is killed and the run ends with the error `timeout`. |
| `concurrency` | What happens when the command is started while it runs: `single` refuses the new run (`busy`), `queue` waits for the running one, `parallel` lets them overlap, `restart` cancels the old run and starts again. |
| `cooldownSeconds` | How long after a run the next one is refused (`cooldown`). 0 for none. |
| `captureOutput` | Keep standard output and error (each up to 64 KiB) and return them to the caller. |
| `conditions` (*Conditions*) | Optional. When the command may run: the session state and a time window; see [Conditions](#conditions). |
| `secrets` (*Secrets*) | Optional. Names of stored secrets that the program receives as environment variables; see [Secrets](#secrets). |

```json
{
  "schemaVersion": 2,
  "id": "set_display_profile",
  "name": "Set display profile",
  "icon": "mdi:monitor-multiple",
  "enabled": true,
  "exposeToHa": true,
  "runAs": "user",
  "action": { "type": "powershell", "source": "file", "script": "set_display_profile.ps1", "engine": "auto" },
  "parameters": [
    { "name": "Profile", "type": "enum", "options": ["Normal-Mode", "TV-Mode", "Study-Mode"], "required": true },
    { "name": "UpdateAdapterIds", "type": "bool", "default": false }
  ],
  "haControl": { "type": "select", "parameter": "Profile" },
  "timeoutSeconds": 60,
  "concurrency": "queue",
  "captureOutput": true
}
```

!!! tip "Start from a template"
    The page **Templates** of the agent UI holds ready-made commands and scripts (display profiles, program launchers, volume,
    lock, sleep and more). Preview one and add it; see the [template gallery](templates.md). The files are also in the
    repository folder `templates/`.

## Run context

*Runs as* decides where the command executes:

| Value | Label | Executes in | Rights | Fails with |
|-------|-------|-------------|--------|------------|
| `system` | System | The service | LocalSystem; no desktop, no user profile | – |
| `user` | User session | The session helper of the logged-in user | The user's standard token; desktop access | `no_session` if nobody is logged in |
| `user_elevated` | User session, elevated | The user's desktop, started by the agent with the user's administrator token | Administrator, without a UAC prompt | `no_session`; `elevation_unavailable` if the user is not an administrator |

Rules of thumb:

* Power actions (shut down, restart, sleep, hibernate) and maintenance scripts: **System**.
* Anything that shows a window, starts a program for you, changes the display, plays sound or reads the desktop: **User session**.
* Elevated only for programs that really need administrator rights. A *User session* command never has them.

After a PC start there is a moment in which nobody is logged in yet. Commands of the user contexts fail with `no_session`
until the sensor **Session ready** turns on; see [the example](example-mode-start.md).

### What a script or program knows about its run

Every run gets these environment variables (and, in `process` arguments, the placeholders `{{qa.run_id}}`, `{{qa.source}}`,
`{{qa.command_id}}`, `{{qa.agent_name}}`):

| Variable | Value |
|----------|-------|
| `QA_RUN_ID` | The id of this run (see the history). |
| `QA_SOURCE` | Who started it: `ha`, `ui`, `hub`, `cli`, `sequence` or `test`. |
| `QA_COMMAND_ID` | The command id. |
| `QA_AGENT_NAME` | The name of the agent. |
| `QA_VAR_<NAME>` | Every variable, name in upper case, value as text (for example `QA_VAR_PROFILE`). Handy for programs that cannot take parameters. |
| `QA_EMIT_URL`, `QA_EMIT_TOKEN` | Used by `qoffeeautomat emit` to send [events](events.md#custom-events). |

## Action types

### PowerShell

Runs a script with PowerShell 7 (`pwsh`) if installed, otherwise Windows PowerShell 5.1 (*Engine*: automatic, `pwsh` or
`powershell`). The script runs **in its own process**, with `-NoProfile -NonInteractive`, so it cannot crash the agent and a
timeout can kill it completely.

* **Written here** (`source: inline`): the code is stored in the command.
* **A script file** (`source: file`): the name of a file in `%ProgramData%\QoffeeAutomat\scripts\`. Edit it with any editor;
  the web interface can also edit scripts (a Monaco code editor, bundled, no internet needed).

Declare a `param()` block whose names and types match the command's parameters. The button *Write param() block* in the
editor inserts it for you:

```powershell
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Normal-Mode', 'TV-Mode', 'Study-Mode')]
    [string] $Profile,

    [bool] $UpdateAdapterIds = $false
)

$ErrorActionPreference = 'Stop'
Write-Output "Switching to $Profile"
# ... do the work; 'exit 2' ends the run with the exit code 2
```

* The values arrive as **typed named parameters** (`string`, `int` as a whole number, `float` as `[double]`, `bool` as
  `[bool]`), never as text pasted into the script. A value like `"; Remove-Item C:\ -Recurse` is just a string.
* The **exit code** of the run is the one your script reports with `exit` (or that of the last native program it ran). An
  uncaught error ends the script with exit code 1 and the message on standard error.
* **Output.** With *Capture the output* on, standard output and error are returned to the caller. For a custom
  [sensor](sensors.md) the last line of the output is the value.

### Program

Starts an executable **without a shell**: the *Program* (full path) with the *Arguments* (one per line), an optional
*Working directory*, and *Wait for the program to end* on or off.

```json
{
  "type": "process",
  "file": "C:\\Program Files\\Steam\\steam.exe",
  "args": ["-start", "steam://open/bigpicture", "-fulldesktopres"],
  "waitForExit": false
}
```

Write a variable into an argument as `{{Name}}`. The value replaces the placeholder **inside that argument** and is never
split into several arguments, so spaces and quotes in a value cannot add arguments. Use *Wait for the program to end* off for
programs with a window that keep running; the run ends when the program was started.

### Built-in

One of the [built-in actions](reference/actions.md) of the platform, for example `power.sleep` or `audio.set_volume`. They need no
script and are the best choice for what they cover.

### Sequence

Runs other commands in order. Each step is either `{ "command": "<id>", "vars": { ... } }` or `{ "delaySeconds": n }`.
*On error* decides what a failing step does: `stop` ends the sequence, `continue` goes on. Every step runs with **its own**
*Runs as*, so one sequence can mix system and user steps. Cycles (a sequence that calls itself, directly or not) are rejected when you
save.

```json
{
  "type": "sequence",
  "onError": "stop",
  "steps": [
    { "command": "set_display_profile", "vars": { "Profile": "TV-Mode" } },
    { "command": "launch_steam_big_picture" },
    { "delaySeconds": 10 },
    { "command": "set_volume", "vars": { "Level": 25 } }
  ]
}
```

A sequence is one list of steps. For a set of modes of which exactly one is active, with steps for entering and for leaving each
one and a state that Home Assistant can show, use a **mode group** (the *Modes* page) instead; it is how the old prototype's
"modes" are rebuilt, see [Migration](../migration-from-prototype.md) and
[Example: start the PC in a mode](example-mode-start.md).

## Variables

A command's `parameters` are the values a caller may pass. They are the **only** way to hand data to a command: the agent
checks every value against the definition, whoever sent it (Home Assistant, the web interface, a script) and whatever the
sender checked.

| Setting | Applies to | Meaning |
|---------|-----------|---------|
| `name` | all | Letter first, then letters, digits and `_`, up to 32 characters; unique per command, ignoring case. |
| `type` | all | `string` (Text), `int` (Whole number), `float` (Decimal number), `bool` (Yes or no), `enum` (Choice). |
| `required`, `default` | all | A missing required value without a default is rejected with `invalid_vars`. |
| `min`, `max`, `step` | `int`, `float` | Range and step; Home Assistant's number entity uses them. |
| `maxLength`, `pattern` | `string` | At most 1024 characters unless you set another limit; `pattern` is a regular expression that the **whole** value must match (it has a 100 ms time limit). |
| `options` | `enum` | The allowed values, at least one. They become the options of a Home Assistant select. |
| `optionsFrom` | `enum` | Instead of a fixed list: the id of an enum metric, for example `audio.output_device`. The options are the current options of that metric, so they follow the PC. Not together with `options`. See [Switch the audio device](#switch-the-audio-device). |
| `secret` | all | The value is hidden in the history, the logs and the audit log. |
| `unit`, `description` | all | Shown in the web interface and in Home Assistant. |

Unknown variables are rejected. In the editor, *Test run* shows a form made from the parameters, so you can try values
before Home Assistant sends them.

### Passing Home Assistant templates as variables

In an automation or script, every variable of `qoffeeautomat.run_command` may be a Home Assistant template. Home Assistant
renders it **before** the call, then sends the result as a plain value. Because the agent never pastes values into code,
odd characters in a template result are harmless.

```yaml
actions:
  - action: qoffeeautomat.run_command
    target:
      device_id: "{{ device_id('binary_sensor.gaming_pc_connectivity') }}"
    data:
      command: set_display_profile
      variables:
        Profile: "{{ states('input_select.display_profile') }}"
        UpdateAdapterIds: false
```

More examples:

=== "Number from a slider"

    ```yaml
    - action: qoffeeautomat.run_command
      target:
        device_id: "{{ device_id('binary_sensor.gaming_pc_connectivity') }}"
      data:
        command: set_volume            # a command with the integer variable Level
        variables:
          Level: "{{ states('input_number.pc_volume') | int(0) }}"
    ```

=== "Text from a helper"

    ```yaml
    - action: qoffeeautomat.run_command
      target:
        device_id: "{{ device_id('binary_sensor.gaming_pc_connectivity') }}"
      data:
        command: show_message          # a command with the string variable Text
        variables:
          Text: "{{ states('input_text.pc_message') }} ({{ now().strftime('%H:%M') }})"
    ```

=== "Boolean from another entity"

    ```yaml
    - action: qoffeeautomat.run_command
      target:
        device_id: "{{ device_id('binary_sensor.gaming_pc_connectivity') }}"
      data:
        command: set_mute              # a command with the boolean variable Muted
        variables:
          Muted: "{{ is_state('binary_sensor.tv_on', 'on') }}"
    ```

=== "Use the result"

    ```yaml
    - action: qoffeeautomat.run_command
      target:
        device_id: "{{ device_id('binary_sensor.gaming_pc_connectivity') }}"
      data:
        command: disk_report
      response_variable: result
    - action: notify.mobile_app_phone
      data:
        message: >-
          Exit code {{ result.exit_code }} after {{ result.duration_ms }} ms:
          {{ result.stdout }}
    ```

!!! note "Where the template ends"
    A template can only produce the **values** of variables. It cannot change the script, the program or the arguments'
    structure, and it cannot reach variables the command did not declare. That is deliberate: see
    [Security](security.md#variables-are-data-never-code).

Use `device_id('<any entity of the PC>')` to pick the device in a template, or select the device in the visual editor. The
action takes exactly one device.

## Control mapping

`haControl` decides which entity Home Assistant gets for the command. The editor only offers the controls the parameters allow
and shows a *Home Assistant preview*.

| Control | Entity | Needs | Behaviour |
|---------|--------|-------|-----------|
| `none` | – | – | No entity. The command can only be called with `qoffeeautomat.run_command`. |
| `button` | Button | Every required parameter has a default | Pressing runs the command with the defaults. |
| `switch` | Switch | `parameter` of type `bool` | On runs it with `true`, off with `false`. |
| `select` | Select | `parameter` of type `enum` | Choosing an option runs the command with it. |
| `number` | Number | `parameter` of type `int` or `float` | Setting a value runs the command with it. Uses `min`, `max`, `step` and `unit`. |
| `text` | Text | `parameter` of type `string` | Setting a text runs the command with it. |

`parameter` names the variable that the entity sets; other parameters get their defaults.

### The state of the entity

Without more, the entity is **optimistic**: Home Assistant assumes that it is in the state it just asked for
(`assumed_state`). To show the **real** state, name a metric in `stateMetric`:

```json
{
  "id": "night_light",
  "name": "Night light",
  "runAs": "user",
  "action": { "type": "powershell", "source": "file", "script": "night_light.ps1" },
  "parameters": [ { "name": "On", "type": "bool", "required": true } ],
  "haControl": { "type": "switch", "parameter": "On", "stateMetric": "custom.night_light_state" }
}
```

Here `custom.night_light_state` is a [custom sensor](sensors.md) of value type `bool` that reads the real state. The switch in
Home Assistant then follows the sensor, not the last button press. Only metrics with a fitting type are offered in the editor.

If nothing on the PC can be read to tell the state, let the command's own script report it: a sensor of the type
*Value set from outside* is set with `QoffeeAutomat.Agent.exe state set <id> <value>` at the end of the script, see
[Values that scripts set](sensors.md#values-that-scripts-set).

### Options that follow the PC

A select whose choices change while the PC runs (the audio devices, for example) does not list its options in the command.
The variable names an enum metric in `optionsFrom` instead, and the agent takes the options from that metric whenever it
builds the manifest for Home Assistant:

```json
{
  "id": "audio_output_device",
  "name": "Audio output device",
  "runAs": "user",
  "action": { "type": "builtin", "name": "audio.set_default_output" },
  "parameters": [ { "name": "Device", "type": "enum", "required": true, "optionsFrom": "audio.output_device" } ],
  "haControl": { "type": "select", "parameter": "Device", "stateMetric": "audio.output_device" }
}
```

When an option appears or disappears, Home Assistant gets the new list without a restart. Without an option (for example
while nobody is logged in, so there is no session helper to ask) the select has nothing to offer and is not created until
there is. `optionsFrom` is optional; a command without it is not affected in any way. The value that arrives at the agent
is any non-empty text, because the list is not known when the command is saved; the action that runs checks it (the audio
actions refuse a name that fits no device with `invalid_vars`).

## Switch the audio device

The built-in actions `audio.set_default_output` and `audio.set_default_input` make a playback or recording device the
default device **and** the default communication device. The variable `Device` is the name of an active device or a part of
it; the rules are in the [action reference](reference/actions.md#switching-audio-devices). A name that fits no device or
several devices is rejected with `invalid_vars` and nothing is changed.

Two commands are created on the first start with an empty configuration, **disabled** like the power commands: `audio_output_device`
and `audio_input_device`. They are selects whose options are the active devices (`optionsFrom`) and whose state is the current
default device (`stateMetric`). Switch one on in the web interface and Home Assistant gets a select entity that lists the devices,
shows the active one, and gains an option when you plug in a device. The sensors *Audio output device* and *Audio input device*
(`audio.output_device`, `audio.input_device`) report the same values; they are on by default.

If you already have commands (the defaults are only created on a fresh configuration), create the same commands from the
example above; `audio_input_device` uses `audio.set_default_input` and `audio.input_device`.

!!! warning "Windows does not document the interface for this"
    Reading the devices and their changes uses documented Windows interfaces. **Changing** the default device does not:
    Windows has no documented way to do it, and this agent, like every audio switcher, uses the internal COM interface
    `IPolicyConfig` of the audio policy. The interface has stayed the same from Windows Vista to Windows 11, and the agent
    keeps its use in one small class that can be replaced, but a Windows update could change it. If it stops working, the
    action fails with an error that names this interface, the default device stays as it was, and the sensors keep working.

## Run a command from Home Assistant

* **By entity**: press the button, flip the switch, pick the option.
* **By action** `qoffeeautomat.run_command`, available in every automation, script and in *Developer tools*, *Actions*:

| Field | Meaning |
|-------|---------|
| `device_id` | The PC (exactly one device). |
| `command` | The command id, as shown in the agent. |
| `variables` | Object of values by variable name (optional). |
| `wait` | `true` (default): wait for the result. `false`: return as soon as the agent started the run; the response then only has `run_id`. |
| `timeout` | How long to wait for the answer, 1 to 86400 s, default 60. This is the waiting time of Home Assistant; the command's own timeout is `timeoutSeconds`. |

With `wait: true` the response (use `response_variable`) holds `run_id`, `exit_code`, `stdout`, `stderr`, `duration_ms`,
`timed_out` and `truncated`; the output is cut to 65 536 characters. A **non-zero exit code is not an error of the action**: it
is returned in `exit_code`, so your automation can decide what to do. Use a *condition* on `result.exit_code`.

The action raises an error when the command cannot run at all:

| Error | Raised as | Typical causes |
|-------|-----------|----------------|
| `unknown_command`, `command_disabled`, `invalid_vars`, `bad_request` | Validation error (shown without a stack trace) | The id is wrong, the command is switched off, a variable is missing or out of range. |
| `busy`, `cooldown`, `no_session`, `elevation_unavailable`, `paused`, `cancelled`, `internal` | Action error | The command is running (`single`), the cooldown has not passed, nobody is logged in, commands are paused, the run was cancelled, ... |
| The PC is off or the agent is not connected | Action error *The agent is not connected* | |

A run that **ends** counts as a success of the action, whatever its exit code, and also when it hit its timeout: then
`timed_out` is `true` and the process tree was killed. Check `exit_code` and `timed_out` in a condition if it matters.

Add `continue_on_error: true` to a step to let the automation go on.

## Conditions

A command can say when it may run. The editor has a section *Conditions*; a command without conditions runs whenever the agent
accepts it. A run that does not meet the conditions is refused with the error `condition_not_met`, the message says which
condition failed, and the refusal is recorded in the history. The conditions apply to every source (Home Assistant, the UI, the
command line), to the steps of a sequence and to test runs.

| Setting | Meaning |
|---------|---------|
| *Session* (`conditions.session`) | `any` (no requirement, the default), `logged_in` (somebody is logged in, the screen may be locked), `unlocked` (somebody is logged in and the screen is not locked) or `locked` (somebody is logged in and the screen is locked). |
| *Time window* (`conditions.timeWindow`) | `from` and `to` as `HH:mm` on the local clock of the PC, and optionally the days (`mon` to `sun`). `from` is included, `to` is not. A window whose `to` is not later than its `from` crosses midnight: 22:00 to 06:00 is open late in the evening and early in the morning. The days name the days on which the window *starts*, so 22:00 to 06:00 on Monday is still open on Tuesday at 02:00. |

```json
"conditions": {
  "session": "locked",
  "timeWindow": { "from": "22:00", "to": "06:00", "days": ["mon", "tue", "wed", "thu", "fri"] }
}
```

The conditions are checked when the run is requested. A run that waits in a queue is not checked again when its turn comes.

Commands from before this feature (`schemaVersion` 1) keep working. The agent upgrades such a file to `schemaVersion` 2 the first
time it reads it and keeps the old file in `config/.history/commands/<id>/`.

## Secrets

A script often needs a password or a key. Do not put it into the script or into a variable; store it as a **secret** (page
*Secrets* in the UI):

1. Open *Secrets*, type a **name** (lower-case letters, digits and `_`, for example `backup_password`) and the **value**, and save.
   The value is write-only: the page and the API can replace or delete a secret but never show it again. A value has 4 to 4096
   characters.
2. In the editor of the command, open the section *Secrets* and tick the names the command needs (PowerShell and program
   commands only).
3. In the script, read it from the environment variable `QA_SECRET_<NAME>` with the name in upper case: `$env:QA_SECRET_BACKUP_PASSWORD`
   in PowerShell, `%QA_SECRET_BACKUP_PASSWORD%` for a program that reads its environment.

What the agent guarantees: the value is encrypted on disk (Windows DPAPI, machine scope); only the run of a command that lists the
secret gets it, and only as that environment variable (never as an argument, never in a file); it does not appear in the run
history, the logs, the configuration export or the diagnostics bundle; and when a script prints the value anyway, every occurrence
in the captured output is replaced by `***` before the output is stored or shown. For that reason a command with secrets shows its
output when it ends instead of while it runs. Other forms of the value (base64, a part of it) are not recognised, so do not print
secrets deliberately. A command that lists a secret which no longer exists fails with the error `internal` before it starts.

## Maintenance mode

The agent has a global **maintenance mode**: while it is on, every command from every source is refused with the error `paused`.
Runs that already started continue. You switch it on and off in three ways: the tray icon (*Pause commands* and *Resume commands*),
the switch **Maintenance mode** that the integration creates for each PC in Home Assistant, and the action
`qoffeeautomat.run_command` with the command `agent_maintenance` and the variable `Enabled`. Home Assistant also gets the binary
sensor *Maintenance mode*, so automations can see the state. The switch itself still works while the agent is paused, otherwise it
could not be switched off again. Because `agent_maintenance` is provided by the agent, it is not listed under *Commands* and its id
cannot be used for your own commands. Restarting the agent clears the maintenance mode.

## Test, history and pause

* **Test run** (editor, right-hand panel): runs the draft as it is on screen, without saving it, with live output. It is
  recorded in the history with the source `test`, ignores *Enabled* and the cooldown and raises no events.
* **Run** (page *Commands*): runs a saved command with a form for its variables.
* **History**: every run with source (`ha`, `ui`, `cli`, `sequence`, `test`), who started it, variables (secrets hidden), exit
  code, duration and output. How long it is kept is set in the settings (*Keep run history*).
* **Pause**: the tray icon's *Pause commands* makes the agent reject every run (`paused`) from every source until you resume
  or the agent restarts. Runs that already started continue. It is the same flag as the [maintenance mode](#maintenance-mode).

## Examples

**Lock the PC with a button.** Enable the default command `session_lock` and show it in Home Assistant. Nothing else.

**Set the volume from a slider.** The command in [Built-in actions](reference/actions.md#how-to-use-a-built-in-action).

**Run a script with a text value.** Inline PowerShell, runs as the user, shows a message box:

```json
{
  "schemaVersion": 1,
  "id": "show_message",
  "name": "Show a message",
  "enabled": true,
  "exposeToHa": true,
  "runAs": "user",
  "action": {
    "type": "powershell",
    "source": "inline",
    "engine": "auto",
    "code": "param([Parameter(Mandatory=$true)][string]$Text)\nAdd-Type -AssemblyName PresentationFramework\n[void][System.Windows.MessageBox]::Show($Text, $env:QA_AGENT_NAME)"
  },
  "parameters": [ { "name": "Text", "type": "string", "required": true, "maxLength": 200 } ],
  "haControl": { "type": "text", "parameter": "Text" },
  "timeoutSeconds": 120,
  "concurrency": "queue"
}
```

**Report back to Home Assistant from a script.** A script that the agent runs can send an event to Home Assistant with the
agent's own executable, see [Custom events](events.md#custom-events).
