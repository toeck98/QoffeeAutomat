# Example: start the PC in a mode

**Goal:** press a button in Home Assistant (or say it to a voice assistant) and the PC starts, logs in and ends up in the mode
you picked, for example *TV gaming*: the TV display profile is active, Steam Big Picture is open and the volume is set.

This is the pattern behind every "do something on a PC that may be off":

1. **Wake** the PC (Wake-on-LAN, done by Home Assistant).
2. **Wait for the connection** (the agent started and Home Assistant is connected).
3. **Wait for the session to be ready** (a user is logged in and the session helper accepts desktop commands).
4. **Run the mode's commands** in the agent.

Everything here works with version 1.0 and Home Assistant's own building blocks.

!!! info "Later releases"
    The plan for later releases adds options to `qoffeeautomat.run_command` that do steps 1 to 3 in one call (`wake`, `wait_for`)
    and *mode groups* with a real state. They are not part of 1.0. The automation below keeps working afterwards; it only
    becomes shorter.

## What you need

* The PC can be woken by **Wake-on-LAN**: enabled in the BIOS/UEFI and in the network adapter settings of Windows (*Allow this
  device to wake the computer*, *Only allow a magic packet*; switch off *Fast startup* if the PC does not wake from a shutdown),
  and connected by cable. Wi-Fi rarely works. Note the **MAC address** of the adapter.
* Windows logs in **by itself** after the start (automatic sign-in, `netplwiz`; or the PC logs in through a fingerprint reader or
  similar). Without a logged-in user, the commands that need the desktop fail with `no_session`. Automatic sign-in is a trade-off
  with physical security; see [Security](security.md).
* The agent is paired with Home Assistant ([Pairing](pairing.md)).
* The Home Assistant integration [Wake on LAN](https://www.home-assistant.io/integrations/wake_on_lan/) (add `wake_on_lan:` to
  `configuration.yaml` if you use the YAML switch, or use the action `wake_on_lan.send_magic_packet`).

## Part 1: the commands in the agent

Build the mode out of small commands that do one thing each, then combine them in a *sequence*. Templates for the display and
launch commands are in the `templates/` folder of the repository (see [Migration from the prototype](../migration-from-prototype.md)).

| Command id | Action | Runs as |
|------------|--------|---------|
| `set_display_profile` | PowerShell script `set_display_profile.ps1`, variables `Profile` and `UpdateAdapterIds` | User session |
| `launch_steam_big_picture` | Program `steam.exe`, arguments `-start`, `steam://open/bigpicture`, `-fulldesktopres`, do not wait | User session |
| `set_volume` | Built-in `audio.set_volume`, variable `Level` | User session |

Then two **sequence** commands, one per mode. In the web interface create a command, action type *Sequence*, and paste:

```json
{
  "schemaVersion": 1,
  "id": "mode_tv_gaming",
  "name": "Mode: TV gaming",
  "icon": "mdi:television-play",
  "enabled": true,
  "exposeToHa": false,
  "runAs": "user",
  "action": {
    "type": "sequence",
    "onError": "continue",
    "steps": [
      { "command": "set_display_profile", "vars": { "Profile": "TV-Mode" } },
      { "command": "set_volume", "vars": { "Level": 30 } },
      { "delaySeconds": 5 },
      { "command": "launch_steam_big_picture" }
    ]
  },
  "haControl": { "type": "none" },
  "timeoutSeconds": 300,
  "concurrency": "restart"
}
```

```json
{
  "schemaVersion": 1,
  "id": "mode_desk",
  "name": "Mode: desk",
  "icon": "mdi:desk",
  "enabled": true,
  "exposeToHa": false,
  "runAs": "user",
  "action": {
    "type": "sequence",
    "onError": "continue",
    "steps": [
      { "command": "set_display_profile", "vars": { "Profile": "Normal-Mode" } },
      { "command": "set_volume", "vars": { "Level": 15 } }
    ]
  },
  "haControl": { "type": "none" },
  "timeoutSeconds": 300,
  "concurrency": "restart"
}
```

Decisions in these documents:

* `exposeToHa: false`, `haControl: none`: Home Assistant starts them through the action, not through an entity of their own.
  Make them a *button* if you also want one on a dashboard.
* `onError: continue`: if Steam is not installed, the display profile and the volume are still set. Use `stop` when a later step
  depends on an earlier one.
* `concurrency: restart`: switching mode while the previous one is still starting cancels it instead of mixing the two.
* Use *Test run* in the editor, with the PC on, to try each mode before you automate anything.

## Part 2: the automation in Home Assistant

A helper to pick the mode (*Settings*, *Devices & services*, *Helpers*, *Dropdown*): **`input_select.pc_mode`** with the options
`desk` and `tv_gaming`. It also acts as your dashboard control and as the voice entity.

The automation:

```yaml
alias: Start the PC in the chosen mode
mode: restart                        # a new request replaces a running one
triggers:
  - trigger: state
    entity_id: input_select.pc_mode  # the dashboard, a voice assistant, a remote ...
variables:
  mode_command: "{{ 'mode_' ~ states('input_select.pc_mode') }}"
actions:
  # Replace the entity ids below with those of your PC (here: the device "Gaming PC").
  # 1. Wake the PC if it is not connected.
  - if:
      - condition: state
        entity_id: binary_sensor.gaming_pc_connectivity
        state: "off"
    then:
      - action: wake_on_lan.send_magic_packet
        data:
          mac: "A4:B1:C1:00:11:22"
          broadcast_address: "192.168.1.255"
      # 2. Wait until Home Assistant is connected to the agent (at most three minutes).
      - wait_template: "{{ is_state('binary_sensor.gaming_pc_connectivity', 'on') }}"
        timeout: "00:03:00"
        continue_on_timeout: false
  # 3. Wait until a user is logged in and the session helper is ready (at most five minutes).
  - wait_template: "{{ is_state('binary_sensor.gaming_pc_session_ready', 'on') }}"
    timeout: "00:05:00"
    continue_on_timeout: false
  # 4. Run the mode.
  - action: qoffeeautomat.run_command
    target:
      device_id: "{{ device_id('binary_sensor.gaming_pc_connectivity') }}"
    data:
      command: "{{ mode_command }}"
      timeout: 300
    response_variable: result
  - if:
      - condition: template
        value_template: "{{ result.exit_code != 0 or result.timed_out }}"
    then:
      - action: persistent_notification.create
        data:
          title: PC mode failed
          message: "{{ mode_command }}: exit code {{ result.exit_code }}. {{ result.stderr }}"
```

How it works, and why:

* **`wait_template` on the entity state, not on an event.** The state of *Connectivity* and *Session ready* is still there when the
  automation looks at it. An event such as `session_ready` could already be gone: if the PC is already running, nothing happens
  again. Waiting for a state is free of such races, and it returns at once when the PC is already ready.
* **`continue_on_timeout: false`** stops the automation if the PC does not show up, instead of sending commands into the void.
* **Entities of a PC that is off are unavailable**, only *Connectivity* and *Power state* stay. `is_state(..., 'on')` is false for
  unavailable, so the second wait also waits for the entity to come back.
* **The command id is built by a template**: `mode_` plus the selected option. The template only decides **which** command runs; the
  agent checks that the command exists and is enabled. A value that matches no command is rejected with `unknown_command`.
* **`mode: restart`** and the command's `concurrency: restart` together make a quick change of mind work.

### When the PC is already on

If the PC is on and ready the two waits return immediately and the mode starts. If it is on but nobody is logged in, the second
wait covers that too, until somebody logs in or the five minutes are over.

### When somebody starts the PC by hand

Use the `booted` event to apply a default mode after **any** start of the PC, not only yours:

```yaml
alias: Default mode after the PC started
triggers:
  - trigger: event
    event_type: qoffeeautomat_event
    event_data:
      type: booted
actions:
  - wait_template: "{{ is_state('binary_sensor.gaming_pc_session_ready', 'on') }}"
    timeout: "00:05:00"
    continue_on_timeout: false
  - action: qoffeeautomat.run_command
    target:
      device_id: "{{ trigger.event.data.device_id }}"
    data:
      command: mode_desk
```

`booted` is raised when Home Assistant connects and sees that the boot id of the PC changed. It does not fire again after an agent
restart, and it is not lost if the PC finished starting before Home Assistant could connect.

## Troubleshooting this example

| Symptom | Check |
|---------|-------|
| The PC does not wake. | Wake-on-LAN in BIOS and adapter, cable, correct MAC and broadcast address, *Fast startup* off. Test with a WoL app on the same network first. |
| The automation stops after three minutes. | The agent did not connect. Is Windows booting to the logon screen with the service running? See [Troubleshooting](troubleshooting.md#home-assistant-cannot-connect). |
| It stops after five minutes. | Nobody logged in (set up automatic sign-in), or the session helper did not start. Check the dashboard of the agent: *Session host* should read *Ready*. |
| `no_session` although *Session ready* is on. | The session changed between the wait and the call (logoff). Retry; add a `repeat` with a few attempts if this happens often. |
| The display profile is not applied. | Run `set_display_profile` by hand with *Test run* and read its output in the history. |
