# Events

Events tell Home Assistant that **something happened** on the PC, as opposed to a metric, which says how things *are*. The
agent pushes them over QoffeeLink as they occur.

## The events of the agent

| Type | Name | When | `data` |
|------|------|------|--------|
| `agent_started` | Agent started | The agent finished starting (the service started or restarted). | `version` |
| `session_ready` | Session ready | The session helper of the logged-in user connected: commands that run as the user can now execute. | – |
| `user_logged_in` | User logged in | A user logged in. | – |
| `user_logged_out` | User logged out | The user logged out. | – |
| `session_locked` | Session locked | The session was locked. | – |
| `session_unlocked` | Session unlocked | The session was unlocked. | – |
| `resumed` | Resumed | The PC woke up from sleep or hibernation. | – |
| `command_finished` | Command finished | A command run ended. | `command`, `run_id`, `source`, `exit_code`, `error`, `duration_ms` |
| `custom` | Custom event | A script or the command line raised an event, see [below](#custom-events). | `name` and optionally `data` |

In addition, the **integration** raises one event itself:

| Type | When |
|------|------|
| `booted` | On a connection, the integration sees that the **boot id** of the PC differs from the last one it saw: the PC has been started (or restarted) since. |

`booted` is the reliable "the PC was started" trigger. Events that happen on the PC **before Home Assistant is connected**
(a start usually takes a while to reach the point where Home Assistant can connect) are not delivered afterwards; the boot id
comparison covers exactly that case. An agent restart alone does not change the boot id and does not raise `booted`.

## Using events in Home Assistant

Every event produces

1. the **bus event** `qoffeeautomat_event` with the data `device_id`, `type` and `data`, and
2. a trigger of the **event entity** *Events* of the device (its event types are `booted` and the types that the agent
   announces).

The bus event is fired even if you disabled the entity, so automations can use it with no entity. Use the event entity when
you want to pick the device in the visual editor.

```yaml
# The PC was started
triggers:
  - trigger: event
    event_type: qoffeeautomat_event
    event_data:
      type: booted
```

```yaml
# The user locked the session of a specific PC
triggers:
  - trigger: event
    event_type: qoffeeautomat_event
    event_data:
      type: session_locked
      device_id: 0123456789abcdef0123456789abcdef
```

```yaml
# Tell me about failed commands
triggers:
  - trigger: event
    event_type: qoffeeautomat_event
    event_data:
      type: command_finished
conditions:
  - condition: template
    value_template: "{{ trigger.event.data.data.exit_code | int(0) != 0 }}"
actions:
  - action: notify.mobile_app_phone
    data:
      message: >-
        {{ trigger.event.data.data.command }} failed
        (exit code {{ trigger.event.data.data.exit_code }}, started by {{ trigger.event.data.data.source }})
```

!!! note "Two levels of `data`"
    In the bus event, `trigger.event.data` holds `device_id`, `type` and `data`; the payload of the agent's event is inside
    `data`, hence `trigger.event.data.data.exit_code`.

Use `{{ trigger.event.data.device_id }}` as the target in a following `qoffeeautomat.run_command` to answer on the same PC (see
[the example](example-mode-start.md)).

## Custom events

A script that the agent runs can raise its own event with the agent executable:

```powershell
& "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe" emit backup_done --data '{"files": 128, "duration_s": 42}'
```

Home Assistant receives the event type `custom` with the data `{"name": "backup_done", "data": {"files": 128, "duration_s": 42}}`:

```yaml
triggers:
  - trigger: event
    event_type: qoffeeautomat_event
    event_data:
      type: custom
conditions:
  - condition: template
    value_template: "{{ trigger.event.data.data.name == 'backup_done' }}"
```

| Rule | Value |
|------|-------|
| Name | A letter, then letters, digits, `_`, `.` or `-`; at most 64 characters. |
| `--data` | A JSON object of at most 16 KiB. |
| Where it works | In scripts and programs that the agent started (they get `QA_EMIT_URL` and `QA_EMIT_TOKEN` in their environment), or as an administrator on the PC. The request only ever goes to the agent on the same PC. |

`emit` exits with a non-zero code if no agent can be reached or the name or the data is invalid.

## Event catalog and compatibility

The agent announces the events it can raise in its manifest, so a new agent version can add events without an update of the
integration. Events that the integration does not know still fire the bus event. See the
[protocol reference](reference/protocol.md) for the wire format of the `event` message.
