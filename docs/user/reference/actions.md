# Built-in actions

A command of the action type `builtin` runs one of the actions below. They are implemented by QoffeeAutomat itself, need
no script and are the safest way to do common things. See [Commands](../commands.md) for how a command is built.

## How to use a built-in action

1. Create a command in the web interface (page *Commands*, *New command*) and choose the action type **Built-in**.
2. Enter the name of the action under *Built-in action*, for example `audio.set_volume`, or pick one of the suggestions.
3. Declare a **parameter** (a variable of the command) for every value you want to set from outside. Its name must be
   **exactly** the parameter name from the tables below (including upper and lower case), for example `Level`. A
   parameter that you do not declare is not set: the action uses its default, or fails when the parameter is required.
4. Set *Runs as* to the context shown in the table (*System* or *User session*). A power action is carried out by the service (`system`); everything that
   touches the desktop or the audio of the logged-in user is carried out in the session helper (`user`).

```json
{
  "schemaVersion": 1,
  "id": "set_volume",
  "name": "Set volume",
  "enabled": true,
  "exposeToHa": true,
  "runAs": "user",
  "action": { "type": "builtin", "name": "audio.set_volume" },
  "parameters": [
    { "name": "Level", "type": "int", "min": 0, "max": 100, "step": 1, "required": true, "unit": "%" }
  ],
  "haControl": { "type": "number", "parameter": "Level" },
  "timeoutSeconds": 30
}
```

This command becomes a number entity in Home Assistant; moving its slider sets the master volume.

!!! note "Default commands"
    On the first start the agent creates the commands `power_shutdown`, `power_restart`, `power_sleep`,
    `power_hibernate`, `power_cancel`, `power_keep_awake`, `session_lock` and `session_logoff` (and the two audio device
    selects). They are **disabled** and not shown in Home Assistant; switch on the ones you want on the page *Commands*.
    They are only created when no other command exists yet and never again after you have deleted them. An installation that
    already has its defaults does not get the new ones by itself: create `power_keep_awake` (the example under
    [Keep awake](#keep-awake)) and `power_cancel` (a button with the action `power.cancel`) on the page *Commands*.

## Power and session

| Action | Run as | Parameters | What it does |
|--------|--------|------------|--------------|
| `power.shutdown` | `system` | `Force` (bool, default `false`), `DelaySeconds` (int, 0 to 3600, default `0`), `Message` (string, up to 256 characters) | Shuts the computer down. With `Force` programs that do not respond to the request are closed instead of waited for; unsaved work in them is lost. With `DelaySeconds` the action waits and shows a [countdown](#power-actions-with-a-countdown). |
| `power.restart` | `system` | `Force`, `DelaySeconds`, `Message` (as above) | Restarts the computer. |
| `power.sleep` | `system` | `DelaySeconds`, `Message` (as above) | Puts the computer to sleep. The run ends when the request was made, not when the computer wakes up. |
| `power.hibernate` | `system` | `DelaySeconds`, `Message` (as above) | Hibernates the computer. Fails when hibernation is not enabled in Windows. |
| `power.cancel` | `system` | none | Cancels the delayed power action that is waiting, see [Power actions with a countdown](#power-actions-with-a-countdown). Does nothing (and is not an error) when nothing is pending. |
| `power.keep_awake` | `system` | `On` (bool, default `true`), `Display` (bool, default `false`), `Minutes` (int, 0 to 10080, default `0`) | Keeps the computer from going to sleep, see [Keep awake](#keep-awake). |
| `session.lock` | `user` | none | Locks the session of the logged-in user. |
| `session.logoff` | `user` | `Force` (bool, default `false`), `DelaySeconds`, `Message` (as above) | Logs the user off. |
| `display.off` | `user` | none | Turns the displays off; moving the mouse turns them on again. |

Before the PC goes to sleep or shuts down, the agent tells Home Assistant why the connection ends (the *Power state*
sensor shows `sleep`, `hibernate`, `shutdown` or `restart`).

### Power actions with a countdown

`DelaySeconds` (0 to 3600) makes `power.shutdown`, `power.restart`, `power.sleep`, `power.hibernate` and `session.logoff` wait before they
act, and gives the person at the PC the chance to stop them. `Message` is the text of the dialog.

* **A user is logged in:** the session helper shows a window that is always on top, with the message, the remaining time and a
  **Cancel** button (Esc presses it too). The window cannot be closed in any other way, so an action that was asked for from far
  away is not stopped by accident.
* **Nobody is logged in** (or the session helper is not connected): there is no dialog and the delay simply elapses.
* **Cancelling**, in the dialog or with the action `power.cancel`, ends the run of the waiting command with the error
  `cancelled`, nothing is shut down, and the agent raises the event
  [`power_action_cancelled`](../events.md) with the data `action` (for example `power.shutdown`), `source` (`dialog`: the
  user pressed Cancel; `action`: `power.cancel` was run; `run`: the run itself was cancelled or timed out) and
  `delay_seconds`.
* **Only one delayed power action can be pending.** While one waits, another delayed action (also of another kind, also
  `session.logoff`) is rejected with the error `busy` and the message `A delayed power action is already pending`; cancel the first one
  or wait for it. An action without a delay is not affected and neither does it affect the waiting one.
* **When the time is over** the action is carried out and the run ends like the same action without a delay.
* `session.logoff` with a delay and nobody logged in ends at once with `no_session`; there is nobody to log off. A `sleep` or `hibernate` that this computer cannot
  do is refused at once, before the countdown starts.

!!! warning "The command must wait as long as the countdown"
    The run of the command lasts as long as the delay, so the **timeout** of the command must be longer than `DelaySeconds`;
    otherwise the run times out first and the pending action is cancelled (`source` is `run`). The default commands have a timeout of
    3660 seconds for this reason. A command that you wrote yourself needs `timeoutSeconds` of at least the longest delay plus a minute.

```yaml
# Shut the PC down in five minutes; somebody at the PC can cancel
actions:
  - action: qoffeeautomat.run_command
    target:
      device_id: 0123456789abcdef0123456789abcdef
    data:
      command: power_shutdown
      variables:
        DelaySeconds: 300
        Message: Home Assistant shuts this PC down for the night.
      wait: false # the run lasts as long as the countdown; do not hold the automation
```

```yaml
# Find out when somebody cancelled at the PC
triggers:
  - trigger: event
    event_type: qoffeeautomat_event
    event_data:
      type: power_action_cancelled
```

The run of `power_shutdown` in the first example ends with the error `cancelled` when somebody cancels, which shows in the run history
and in the event `command_finished`.

### Keep awake

`power.keep_awake` holds a **power request** that belongs to the agent: Windows does not put the computer to sleep because of the idle
timeout while the request is held (`powercfg /requests` lists it under `QoffeeAutomat keep awake`). With `Display` set the
screen stays on as well. `Minutes` ends the request by itself after that time; `0` means until it is switched off with `On` set to `false`.
Calling it again with `On` set to `true` replaces the time and the display setting.

* The request is **not stored**. It ends when the agent stops, and after a restart of the agent or the PC the computer is not kept
  awake any more.
* It does not stop a sleep that somebody asks for (the start menu, the lid, the action `power.sleep`).
* The metric `power.keep_awake` reports the **real** state (see [Metrics](metrics.md)). The default command `power_keep_awake` is a **switch**
  in Home Assistant on the variable `On` whose state is that metric, so the switch turns itself off when the time is over. The
  switch sets only `On`; give an automation `Minutes` or `Display` through `qoffeeautomat.run_command`.

```json
{
  "schemaVersion": 1,
  "id": "power_keep_awake",
  "name": "Keep awake",
  "enabled": true,
  "exposeToHa": true,
  "runAs": "system",
  "action": { "type": "builtin", "name": "power.keep_awake" },
  "parameters": [
    { "name": "On", "type": "bool", "required": true },
    { "name": "Display", "type": "bool", "default": false },
    { "name": "Minutes", "type": "int", "min": 0, "max": 10080, "step": 1, "default": 0, "unit": "min" }
  ],
  "haControl": { "type": "switch", "parameter": "On", "stateMetric": "power.keep_awake" },
  "timeoutSeconds": 30
}
```

## Audio and media

| Action | Run as | Parameters | What it does |
|--------|--------|------------|--------------|
| `audio.set_volume` | `user` | `Level` (int, 0 to 100, required) | Sets the master volume of the default playback device. |
| `audio.set_mute` | `user` | `Muted` (bool, default `true`) | Mutes (`true`) or unmutes (`false`) the default playback device. |
| `media.key` | `user` | `Key` (enum: `play_pause`, `next`, `previous`, `stop`, required) | Presses a media key; the program that has the media focus reacts. |
| `audio.set_default_output` | `user` | `Device` (string, required, up to 256 characters) | Makes a playback device the default device and the default communication device. See [Switching audio devices](#switching-audio-devices). |
| `audio.set_default_input` | `user` | `Device` (string, required, up to 256 characters) | Makes a recording device the default device and the default communication device. |

### Switching audio devices

`Device` is matched against the names of the **active** devices, the same names that the sensors *Audio output device* and
*Audio input device* offer as options (`audio.output_device`, `audio.input_device`):

1. A name that is exactly the same wins.
2. Otherwise the text must be part of exactly one device name, ignoring case (`headset` finds `Headset (USB Audio)` when no other device has `headset` in its name).

A text that fits no device or several devices is rejected with `invalid_vars`, and nothing is changed. Two devices with
the same name get the suffix ` #2`, ` #3` and so on, so that every option is unique. Devices that are unplugged or
disabled in Windows are not active and cannot be selected.

!!! note "An interface that Windows does not document"
    Windows has no documented interface to change the default audio device (the *Sound* settings page and every audio
    switcher use an internal COM interface, `IPolicyConfig`). The agent uses that interface too, kept in one small class of
    the session helper. It has worked unchanged since Windows Vista and on Windows 11, but Microsoft could change it in a
    future update. If it stops working, the action fails with a message that says so, the default device stays as it was,
    and the sensors and everything else continue to work. Report it as a bug.

## Programs, web addresses and processes

| Action | Run as | Parameters | What it does |
|--------|--------|------------|--------------|
| `app.launch` | `user` | `Path` (string, required, up to 1024 characters), `Arguments` (string, up to 2048 characters) | Starts a program or opens a document on the desktop of the user; the run ends when it was started. |
| `url.open` | `user` | `Url` (string, required, an `http` or `https` address, up to 2048 characters) | Opens the address in the default browser of the user. |
| `process.kill` | `user` | `Name` (string, required, a file name without path, with or without `.exe`) | Ends every process of the logged-in user with this name together with its child processes. |

!!! warning "Values from Home Assistant are data"
    `Path`, `Arguments`, `Url` and `Name` are validated by the agent and handed over as values, never pasted into a command
    line. `process.kill` refuses names that contain a path separator, a wildcard or a quote. Still, think about who can
    trigger a command that starts programs: see [Security](../security.md).

## Errors

| Error code | When |
|------------|------|
| `no_session` | The action runs as `user` and nobody is logged in (or the session helper is not connected yet). Wait for the *Session ready* sensor. |
| `invalid_vars` | A required variable is missing, an unknown variable was sent, or a value is out of range (for example `DelaySeconds` above 3600). |
| `busy` | A delayed power action is already pending, see [Power actions with a countdown](#power-actions-with-a-countdown). |
| `cancelled` | A delayed power action was cancelled in its countdown dialog or with `power.cancel`; nothing was done. |
| `paused` | Commands are paused (tray icon, *Pause commands*). |

## Added after version 1.0

`notify.toast` (see [Notifications](../notifications.md)) and `media.control` (see [Media player](../media-player.md)) are built-in
actions that were added after 1.0; they have their own pages. `audio.set_default_output` and `audio.set_default_input` are described
above. Version 1.0 itself has none of them: a command that names such an action on an older agent fails when it runs with the message that the
action is not available on this platform.
