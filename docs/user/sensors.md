# Sensors

A **sensor** reports a value of the PC to Home Assistant. The agent already measures many things
([built-in metrics](reference/metrics.md)); **custom sensors** are the values you define yourself: the output of a script,
whether a program is running, a Windows performance counter, the state of a service.

Every sensor is a *metric* with the id `custom.<sensorId>` and becomes a sensor or binary sensor entity in Home Assistant.
Sensors are JSON documents in `%ProgramData%\QoffeeAutomat\config\sensors\<id>.json`, authored on the page **Sensors** of the
web interface. Older revisions are kept.

## Create a sensor

1. Page **Sensors**, **New sensor**.
2. *General*: name, id (made from the name, fixed after the first save), description, *Enabled*, *Show in Home Assistant*,
   the *interval* (how often it is read while somebody is looking at it) and the *deadband*.
3. *Where the value comes from*: choose the source and fill in its fields.
4. *Value and Home Assistant*: the kind (sensor or binary sensor), the value type, and the details Home Assistant needs (unit,
   device class, state class, icon, precision). Only values that Home Assistant accepts for the value type are offered.
5. **Test** reads the sensor once without saving and shows the value or the reason it failed. **Save**.

If *Show in Home Assistant* is on, the entity appears in Home Assistant within moments. Like every entity it is only measured
while enabled there.

## Value types

| Value type | Kind | Result in Home Assistant |
|------------|------|--------------------------|
| `number` | sensor | A numeric sensor with optional `unit`, `deviceClass`, `stateClass` and `precision`. |
| `string` | sensor | A text sensor. |
| `bool` | binary sensor | On or off. |
| `timestamp` | sensor | A date and time (device class `timestamp`). |
| `enum` | sensor | One of a fixed list of `options`; any other value is rejected. |

`deadband` is the smallest change of a number that is reported; `precision` rounds before comparing. Both reduce traffic and
entity history noise.

## Sources

| Source | Needs Windows | The value is |
|--------|---------------|--------------|
| **PowerShell script** (`powershell`) | no | The **last non-empty line** of the output, parsed according to the value type. |
| **Process is running** (`process_running`) | no | `true` while a process with this name exists. Value type `bool`. |
| **Performance counter** (`perf_counter`) | yes | The value of a Windows performance counter (*category*, *counter*, optional *instance*). Value type `number`. |
| **CIM query** (`cim_query`) | yes | A property of the first result of a CIM (WMI) query (*namespace*, *class*, *property*, optional WQL *filter*), converted to the value type of the sensor. |
| **Service state** (`service_state`) | yes | The state of a Windows service (*service* name). Value type `enum`. |
| **Value set from outside** (`state`) | no | Nothing is read: a script, the web interface or the API sets the value; see [Values that scripts set](#values-that-scripts-set). Any value type. |

### Script sensors

A script sensor runs a PowerShell script (inline, or a file in the scripts folder) each time it is sampled. Everything before
the last line is ignored, so the script may write progress first. Parsing by value type:

| Value type | The last line must be |
|------------|-----------------------|
| `number` | A number with a dot as decimal separator (`23.5`). |
| `bool` | `true`, `false`, `1`, `0`, `yes`, `no`, `on` or `off` (any case). |
| `string` | Any text. |
| `timestamp` | A date and time, for example `2026-11-17T09:30:00Z`. Without a zone it counts as UTC. |
| `enum` | One of the options (the case is forgiven). |

A line that does not parse makes the sensor *unavailable* in Home Assistant and records the reason in the sensor's status; it
never crashes anything. A script sensor runs in its own process like a command, has a time limit that is no longer than its
interval and never more than 25 seconds (the process tree is killed when the time is up), and is sampled only while somebody
uses the value. The interval of a script sensor is at least 5 seconds. Keep scripts short and choose a sensible interval.

Example, free space of a data disk in GiB:

```powershell
$drive = Get-PSDrive -Name D
[math]::Round($drive.Free / 1GB, 1).ToString([cultureinfo]::InvariantCulture)
```

Value type `number`, unit `GiB`, interval 300 s, device class `data_size`, state class `measurement`.

Example, is the VPN connected:

```powershell
if (Get-NetAdapter -Name 'Work VPN' -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up') { 'true' } else { 'false' }
```

Value type `bool`, kind binary sensor.

### Process is running

*Process*: the process name, for example `steam` (the editor tells you if the name is not accepted). The binary sensor is on
while a process with that name exists. It is the quickest way to know whether a program is open, for example to use as the real state of a *start the
program* switch.

### Performance counter, CIM query, service state

Use them when you know the Windows object you want to read:

* **Performance counter**: category `Processor Information`, counter `% Processor Utility`, instance `_Total`.
* **CIM query**: namespace `root\cimv2`, class `Win32_Battery`, property `EstimatedChargeRemaining`.
* **Service state**: the service name such as `Spooler` (a name, not a path). Use the value type `enum` and list the states
  you want to tell apart as *options*. The possible states are `stopped`, `start_pending`, `stop_pending`, `running`,
  `continue_pending`, `pause_pending`, `paused` and `not_installed`. A state that is not among your options makes the
  sensor unavailable and names the state in its status, so add `not_installed` if the service may be missing.

Test them with the *Test* button; the editor tells you what is missing.

## Values that scripts set

A sensor of the type **Value set from outside** (`state`) is not read on a timer. It holds a value that something else sets:
a command script, you in the web interface, or any program through the API. Use it when the PC cannot be asked for the value
but a script knows it, and for the *real state* of a switch (see the pattern below).

| Setting | Meaning |
|---------|---------|
| *Value type* | `number`, `string`, `bool`, `timestamp` or `enum` (with its *options*), like every sensor. Every value that is set is checked against it. |
| *Initial value* | Optional. What the sensor shows after the agent starts until a value is set. Without it the sensor has no value (it is *unavailable* in Home Assistant) until one is set. |
| *Keep the value across restarts* (`persist`) | On: the last value is written to `state-values.json` in the data folder and survives a restart of the agent and of the PC. Off: after every start the sensor returns to its initial value. |

There is no interval: a new value is published to Home Assistant at once, within the normal flush time of about one second.

### Set and read a value

From a script that a command runs (the agent gives every script the access it needs, so there is nothing to configure; the
verbs work like [`emit`](events.md#custom-events)):

```powershell
$agent = "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe"
& $agent state set pc_mode Eco
& $agent state get pc_mode
```

| Verb | Does | Exit code |
|------|------|-----------|
| `state set <id> <value>` | Checks the value against the sensor and publishes it. `<value>` is text: `42.5` (a dot), `true`/`false` (also `1`, `0`, `yes`, `no`, `on`, `off`), one of the options (the case is forgiven), a date and time, or any text up to 255 characters. A value that starts with `-` is fine (`-5`); after `--` everything is a value. | `0` set; `64` unknown sensor, sensor of another type or a value that does not fit (the message says which); `77` no access; `69` no agent is running; `1` the value was set but could not be saved. |
| `state get <id>` | Prints the value alone on stdout (`true`, `-5`, `Eco`), so a script can use it. A sensor without a value prints nothing and says so on stderr. | `0` read; the same error codes as above. |

Outside a script that the agent started, run the verbs as an administrator on the PC: they read the address and the secret from
the protected file `secrets\local-channel.json`. The agent only accepts them from this PC.

The same through the management API (with a session, see [Management API](reference/api.md)):

```http
PUT /api/v1/sensors/pc_mode/value
{ "value": "Eco" }
```

The answer is `{ "id": "pc_mode", "value": "Eco", "updatedAt": "..." }`. `GET /api/v1/sensors/pc_mode/value` reads it. An unknown
sensor is answered with 404 (`unknown_sensor`), a sensor of another type with 409 (`not_state_sensor`), and a value that does not
fit with 422 and the path `/value`.

In the web interface, open a saved sensor of this type: the panel *Set the value* shows the current value and sets a new one.

### Pattern: a switch whose script reports its real state

A command with a `switch` control is *optimistic* by default: Home Assistant assumes that the switch is in the state it asked
for. If the script can fail, or the state changes by other means, let the script report what really happened:

1. Create a sensor `night_light_state`: type *Value set from outside*, value type `bool`, kind binary sensor, *Keep the value
   across restarts* on (so the switch is right after a PC start), no initial value (unknown until the first run).
2. In the command, name it as the real state: `"haControl": { "type": "switch", "parameter": "On", "stateMetric": "custom.night_light_state" }`.
3. At the end of the command script, report the result, after the work has really succeeded:

```powershell
param([bool]$On)
$ErrorActionPreference = 'Stop'

Set-NightLight -On $On          # the actual work; throws when it fails
$state = if ($On) { 'true' } else { 'false' }
& "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe" state set night_light_state $state
```

If the work fails the script stops before the last line, the sensor keeps the old value, and Home Assistant shows the switch in
its real state again after the run instead of the state it asked for. The same sensor can be set from anywhere else that learns
about the light, for example a script that a Windows event or a schedule starts.

## Use a sensor as the state of a command

A [command's control](commands.md#the-state-of-the-entity) can name a sensor in `stateMetric`, so the Home Assistant entity
shows the real state instead of assuming it. Create the sensor first, then pick it in the field *Metric that reports the real
state* of the command.

## Built-in metrics: switch on and off

The page **Metrics** lists every built-in value by provider, with its interval. You can switch a metric off for good (it is
then neither sampled nor offered), change its interval or its deadband, and see its current value. These overrides are stored
in `config/metrics.json` and are part of the [backup](backup-restore.md). Metrics that reveal what you do on the PC (the title
and the program of the active window) are **off by default**; the list carries a *Privacy* mark.

In Home Assistant you can additionally disable the entity: then the agent does not sample it for Home Assistant (it may still
sample it while the web interface shows it).

### Audio devices

*Audio output device* and *Audio input device* (`audio.output_device`, `audio.input_device`) are **enum** sensors whose value is
the name of the current default playback or recording device and whose options are the names of the **active** devices (not
unplugged, not disabled). They come from the session helper, so they need a logged-in user. A change is reported at once by
the device notifications of Windows, not at the next interval: when you plug in a headset the option appears and the manifest
revision changes, so Home Assistant offers it without a restart. A second device with the same name gets the suffix ` #2`.
Use the sensors as the `stateMetric` of a [select that switches the device](commands.md#switch-the-audio-device).

The session helper asks Windows for device notifications only while it is connected to the agent, and the value is only read
while somebody is subscribed to it.

## Limits

* Ids of sensors: lower-case letters, digits and `_`, starting with a letter, up to 63 characters.
* A sensor whose source is Windows-only is shown as *Not supported on this platform* on other systems.
* Disabling or deleting a sensor removes its entity from Home Assistant.
