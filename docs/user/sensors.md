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

!!! note "Value set from outside"
    The editor also shows a type *Value set from outside*. Setting such a value from a script is planned for a later release
    and is not available in 1.0; do not use it yet.

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

## Limits

* Ids of sensors: lower-case letters, digits and `_`, starting with a letter, up to 63 characters.
* A sensor whose source is Windows-only is shown as *Not supported on this platform* on other systems.
* Disabling or deleting a sensor removes its entity from Home Assistant.
