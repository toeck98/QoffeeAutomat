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
    `power_hibernate`, `session_lock` and `session_logoff`. They are **disabled** and not shown in Home Assistant; switch on
    the ones you want on the page *Commands*. They are only created when no other command exists yet and never again
    after you have deleted them.

## Power and session

| Action | Run as | Parameters | What it does |
|--------|--------|------------|--------------|
| `power.shutdown` | `system` | `Force` (bool, default `false`) | Shuts the computer down. With `Force` programs that do not respond to the request are closed instead of waited for; unsaved work in them is lost. |
| `power.restart` | `system` | `Force` (bool, default `false`) | Restarts the computer. |
| `power.sleep` | `system` | none | Puts the computer to sleep. The run ends when the request was made, not when the computer wakes up. |
| `power.hibernate` | `system` | none | Hibernates the computer. Fails when hibernation is not enabled in Windows. |
| `session.lock` | `user` | none | Locks the session of the logged-in user. |
| `session.logoff` | `user` | `Force` (bool, default `false`) | Logs the user off. |
| `display.off` | `user` | none | Turns the displays off; moving the mouse turns them on again. |

Before the PC goes to sleep or shuts down, the agent tells Home Assistant why the connection ends (the *Power state*
sensor shows `sleep`, `hibernate`, `shutdown` or `restart`).

## Audio and media

| Action | Run as | Parameters | What it does |
|--------|--------|------------|--------------|
| `audio.set_volume` | `user` | `Level` (int, 0 to 100, required) | Sets the master volume of the default playback device. |
| `audio.set_mute` | `user` | `Muted` (bool, default `true`) | Mutes (`true`) or unmutes (`false`) the default playback device. |
| `media.key` | `user` | `Key` (enum: `play_pause`, `next`, `previous`, `stop`, required) | Presses a media key; the program that has the media focus reacts. |

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
| `invalid_vars` | A required variable is missing, an unknown variable was sent, or a value is out of range. |
| `paused` | Commands are paused (tray icon, *Pause commands*). |

## Not in version 1.0

The plan for later releases adds `notify.toast`, `audio.set_default_output`, `audio.set_default_input`,
`power.keep_awake`, `power.cancel` and `media.control`, and the optional variables `DelaySeconds` and `Message` on
the power actions. They are not part of 1.0; a command that names them fails when it runs with the message that the action is not available on this platform. Until then, use a
[PowerShell command](../commands.md#powershell) for such tasks.
