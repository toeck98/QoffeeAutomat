# Example: start the PC in a mode

**Goal:** press a button in Home Assistant (or say it to a voice assistant) and the PC starts, logs in and ends up in the mode
you picked, for example *TV gaming*: the TV display profile is active, Steam Big Picture is open and the volume is set.

This is the pattern behind every "do something on a PC that may be off":

1. **Wake** the PC (Wake-on-LAN, done by Home Assistant).
2. **Wait for the connection** (the agent started and Home Assistant is connected).
3. **Wait for the session to be ready** (a user is logged in and the session helper accepts desktop commands).
4. **Run the mode's commands** in the agent.

The action `qoffeeautomat.run_command` does steps 1 to 3 in one call (`wake`, `wait_for`, `ready_timeout`), see
[Short version: one action call](#short-version-one-action-call). Version 1.0 does not have these fields; the longer automation
([Part 2](#part-2-the-automation-in-home-assistant)) works with Home Assistant's own building blocks and keeps working.

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

Build the mode out of small commands that do one thing each, then combine them in a **mode group**: a set of modes of which
exactly one is active, each with *enter* steps (run when the mode is entered) and *exit* steps (run when it is left). Templates for
the display and launch commands, and the mode group `pc_mode` that is used below, are in the **Template gallery** (see also
[Migration from the prototype](../migration-from-prototype.md)).

| Command id | Action | Runs as |
|------------|--------|---------|
| `set_display_profile` | PowerShell script `set_display_profile.ps1`, variables `Profile` and `UpdateAdapterIds` | User session |
| `launch_steam_big_picture` | Program `steam.exe`, arguments `-start`, `steam://open/bigpicture`, `-fulldesktopres`, do not wait | User session |
| `kill_process` | Built-in action that ends the processes of the logged-in user with a given `Name` | User session |
| `set_volume` | Built-in `audio.set_volume`, variable `Level` | User session |

Then the **mode group**. In the web interface open *Modes*, choose *New mode group* and enter the modes (or add the template
`pc_mode` from the gallery and edit it). As a document it looks like this:

```json
{
  "schemaVersion": 1,
  "id": "pc_mode",
  "name": "PC mode",
  "icon": "mdi:monitor-dashboard",
  "enabled": true,
  "exposeToHa": true,
  "defaultMode": "desk",
  "onBoot": "apply_default",
  "onError": "continue",
  "modes": [
    {
      "id": "desk",
      "name": "Desk",
      "enter": [
        { "command": "set_display_profile", "vars": { "Profile": "Normal-Mode" } },
        { "command": "set_volume", "vars": { "Level": 15 } }
      ],
      "exit": []
    },
    {
      "id": "tv_gaming",
      "name": "TV gaming",
      "enter": [
        { "command": "set_display_profile", "vars": { "Profile": "TV-Mode" } },
        { "command": "set_volume", "vars": { "Level": 30 } },
        { "delaySeconds": 5 },
        { "command": "launch_steam_big_picture" }
      ],
      "exit": [
        { "command": "kill_process", "vars": { "Name": "steam" } }
      ]
    }
  ]
}
```

Decisions in this document:

* **Switching from A to B** runs the exit steps of A and then the enter steps of B. Leaving *TV gaming* closes Steam; entering
  *Desk* then restores the display and the volume. Only one switch of a group runs at a time: a second request meanwhile is
  rejected with `busy`.
* **The state is real.** The agent publishes the current mode as the metric `mode.pc_mode`, and Home Assistant shows it as a
  `select` entity that changes to the new mode when the enter steps have finished. After a failed switch with `onError: stop`
  the entity reads *unknown*. Each change fires the event `mode_changed`.
* `exposeToHa: true`: the group appears in Home Assistant as one `select` entity. The generated command is called
  `mode_pc_mode` and has one variable, `Mode`, whose options are the mode ids.
* `onError: continue`: if Steam is not installed, the display profile and the volume are still set. Use `stop` when a later step
  depends on an earlier one.
* `onBoot: apply_default`: after the operating system started, the enter steps of the default mode (*Desk*) run once the session is
  ready. `reset` only sets the state to the default mode and runs nothing; `keep` remembers the last mode. Nothing happens when only
  the agent restarts.
* A step is an ordinary command with its own `runAs`; a step cannot switch a mode.
* Use the buttons of the *Modes* page, with the PC on, to try each mode before you automate anything.

## Part 2: the automation in Home Assistant

A helper to pick the mode (*Settings*, *Devices & services*, *Helpers*, *Dropdown*): **`input_select.pc_mode`** with the options
`desk` and `tv_gaming` (the ids of the modes). It also acts as your dashboard control and as the voice entity. If the PC is on,
the `select` entity of the mode group switches the mode by itself; the automation below is for the case that the PC may be off.

The automation:

```yaml
alias: Start the PC in the chosen mode
mode: restart                        # a new request replaces a running one
triggers:
  - trigger: state
    entity_id: input_select.pc_mode  # the dashboard, a voice assistant, a remote ...
variables:
  mode_command: mode_pc_mode
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
  # 4. Switch the mode: the command of the mode group has one variable, Mode.
  - action: qoffeeautomat.run_command
    target:
      device_id: "{{ device_id('binary_sensor.gaming_pc_connectivity') }}"
    data:
      command: "{{ mode_command }}"
      variables:
        Mode: "{{ states('input_select.pc_mode') }}"
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
* **The mode is a variable of the command**: the selected option of the helper is sent as the value of `Mode`. The agent checks
  it against the modes of the group; a value that is none of them is rejected. Values are never pasted into a script.
* **Only one switch runs at a time.** If a switch of the group is still running, the next one is rejected with `busy`; the
  automation reports it like any other failure. Wait for the entity of the group to show the new mode before you choose again.

### Short version: one action call

With the fields `wake`, `wait_for` and `ready_timeout` the whole automation above is one action:

```yaml
alias: Start the PC in the chosen mode
mode: restart
triggers:
  - trigger: state
    entity_id: input_select.pc_mode
actions:
  - action: qoffeeautomat.run_command
    target:
      device_id: "{{ device_id('binary_sensor.gaming_pc_connectivity') }}"
    data:
      command: mode_pc_mode
      variables:
        Mode: "{{ states('input_select.pc_mode') }}"
      wake: true                # send the Wake-on-LAN packets if the PC is not connected
      wait_for: session_ready   # none (default), connected or session_ready
      ready_timeout: 300        # seconds for waking, connecting and the session together (default 180)
      timeout: 300
    response_variable: result
```

* **`wake`** sends one magic packet per stored MAC address to the broadcast address of the integration's options (*Configure*,
  default `255.255.255.255`), the same as the *Wake on LAN* button, and makes Home Assistant retry the connection every second. It
  only sends when the PC is not connected, and not again while an earlier wake-up is still running. The MAC addresses come from
  the agent; the integration has them once it was connected. Without `wake` the call does not wake anything.
* **`wait_for: connected`** waits until Home Assistant is connected to the agent. **`wait_for: session_ready`** also waits until the
  metric *Session ready* is on (a user is logged in and the session helper is ready). Home Assistant subscribes to the metric for
  the call, even if its entity is disabled. With `wait_for: none` the command runs at once, so `wake` alone is only useful for a
  PC that is already starting.
* **`ready_timeout`** limits everything before the command. If it runs out, the action fails with a message that says which
  condition was missing (not connected, session not ready, or an earlier call for the same PC still waiting) and the command does
  **not** run. Use `continue_on_error: true` on the action if the automation should go on.
* Calls that wake or wait for the same PC **take turns in call order**. A second call does not send a second wake-up; it runs
  after the first call has finished. A call without the new fields is not queued and behaves as before.
* The action needs the integration to be set up. If Home Assistant was restarted while the PC was off and the PC was never
  connected since, the entry is still waiting for it and the action fails with "not connected"; press *Wake on LAN* or use Part 2.

### When the PC is already on

If the PC is on and ready the waits (of the automation above and of `wait_for`) return immediately and the mode starts. If it is
on but nobody is logged in, waiting for the session covers that too, until somebody logs in or the time is over.

### When somebody starts the PC by hand

The simplest way is the setting `onBoot: apply_default` of the mode group (see Part 1): after **any** start of the operating system
the agent runs the enter steps of the default mode once the session is ready, with no automation at all. Use `keep` to find the
last mode again, or `reset` to start in the default mode without running its steps.

If the default mode should depend on something that only Home Assistant knows (the time of day, who is at home), use the `booted`
event instead and choose the mode there:

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
      command: mode_pc_mode
      variables:
        Mode: desk
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
| The entity of the group reads *unknown*. | A step failed in a group with `onError: stop`. Read the run `mode_pc_mode` in the history, fix the step, and switch again. |
| The switch is rejected with `busy`. | An earlier switch of the group is still running. The *Modes* page shows what it is doing; try again when it is over. |
