# Overview and architecture

## What QoffeeAutomat consists of

```text
                       Home Assistant
              +-------------------------------+
              | integration "QoffeeAutomat"   |
              +---------------+---------------+
                              | QoffeeLink: encrypted WebSocket, JSON,
                              | Home Assistant connects, the PC pushes changes
   PC  ------------------------+------------------------------------------
              +---------------v---------------+        +-------------------+
   browser -> | QoffeeAutomat agent           | named  | session helper    |
   (web UI)   |  web UI and management API    | pipe   | (one per logged-in|
              |  command and metric engine    | <----> |  user session)    |
              |  configuration and history    |        |  user commands,   |
              +-------------------------------+        |  session metrics, |
                 Windows service, LocalSystem            |  tray icon        |
                                                       +-------------------+
```

| Part | What it is | What it does |
|------|-----------|--------------|
| **Agent** | A Windows service (`QoffeeAutomat`, account LocalSystem) | Measures the PC, runs commands, stores the configuration and the run history, serves the web interface and the QoffeeLink connection. |
| **Session helper** | `QoffeeAutomat.SessionHost.exe`, started by the agent inside every logged-in user session | Does everything that needs the user's desktop: starting programs, changing displays, the volume, the active window, the tray icon. |
| **Web interface** | Part of the agent, at `http://127.0.0.1:8480/` on the PC | Create commands and sensors, test them, pair Home Assistant, look at history and logs, back up. |
| **Integration** | A Home Assistant custom integration (`qoffeeautomat`), installed with HACS | Turns the metrics, commands and events of a PC into entities, buttons, events and the action `qoffeeautomat.run_command`. |
| **QoffeeLink** | The protocol between the two | See the [protocol reference](reference/protocol.md). |

### Why two processes?

Windows services run in a separate session (session 0). They cannot show windows, start programs on your desktop, change the
display layout or read the idle time. The agent therefore hands everything that belongs to the desktop to the session helper
of the logged-in user. Everything else (power actions, maintenance scripts) runs directly in the service.

Every command declares where it runs (*Runs as*):

| Runs as | Where | Typical use | Needs |
|---------|-------|-------------|-------|
| **System** | In the service, as LocalSystem, with no desktop | Shut down, restart, sleep, maintenance and network scripts | Nothing |
| **User session** | In the session helper, with the rights of the logged-in user and access to the desktop | Start programs, switch displays, volume, lock | Somebody logged in |
| **User session, elevated** | Like *User session*, with the administrator token of that user | Programs that insist on administrator rights, without a UAC prompt | A logged-in user who is an administrator |

## What you see in Home Assistant

One **device** per PC, with entities created from what the agent announces (its *manifest*):

* a **sensor** or **binary sensor** for every metric (built-in metrics and your own [sensors](sensors.md)); entities that you
  disable in Home Assistant are not measured at all,
* a **button**, **switch**, **select**, **number** or **text** for every command that you expose with a control
  (see [Commands](commands.md#control-mapping)),
* a *Connectivity* binary sensor that stays available while the PC is off, and a *Power state* sensor (`running`, `sleep`,
  `hibernate`, `shutdown`, `restart`, `unknown`),
* an *Events* **event entity** and the bus event `qoffeeautomat_event` (see [Events](events.md)),
* the action **`qoffeeautomat.run_command`** for automations and scripts.

When you change commands or sensors in the agent, Home Assistant adds, changes or removes the entities by itself; there is
nothing to reload.

## How data flows

1. **Pairing** (once): you start pairing in the web interface of the agent, Home Assistant sends the one-time code and receives
   a long-lived link token; it also remembers the certificate fingerprint of the agent. See [Pairing](pairing.md).
2. **Connect**: Home Assistant opens one WebSocket connection to the agent (`wss://<pc>:8481/link/v1`) with the token. The agent
   greets with its protocol versions, its identity and the revision of its manifest.
3. **Subscribe**: Home Assistant tells the agent which metrics it needs (those whose entities are enabled). Only these are
   sampled, at the interval of the metric.
4. **Push**: the agent sends the values that changed, at most once per second per connection. Nothing is polled.
5. **Run**: a button press or the action `qoffeeautomat.run_command` sends the command id and the variables; the agent
   validates them, runs the command and answers with the result (exit code, output, duration).
6. **Events and power**: the agent reports events (locked, unlocked, logged in, session ready, ...) and says goodbye with a
   reason before sleep, hibernate, shutdown or restart. After a PC start Home Assistant notices a new boot id and raises the
   event `booted`.

If the PC is off, Home Assistant keeps retrying with growing pauses (1 s up to 60 s). A zeroconf announcement of the
started agent triggers an immediate attempt, so the entities come back quickly after a start.

## Where things live on the PC

| What | Where |
|------|-------|
| Program files | `C:\Program Files\QoffeeAutomat\` |
| Configuration (commands, sensors, metric overrides, settings) | `%ProgramData%\QoffeeAutomat\config\` |
| PowerShell scripts | `%ProgramData%\QoffeeAutomat\scripts\` |
| Secrets (setup token, password hash, link token hashes, certificate key) | `%ProgramData%\QoffeeAutomat\secrets\` |
| Run history, audit log, events (SQLite) | `%ProgramData%\QoffeeAutomat\data\` |
| Logs | `%ProgramData%\QoffeeAutomat\logs\` and the Windows Event Log (source `QoffeeAutomat`) |

The configuration, scripts and secrets folders can only be written by `SYSTEM` and Administrators, because the scripts in them
run with the rights of the service. See [Security model](security.md).

## Ports

| Port | Interface | Used by |
|------|-----------|---------|
| `8480` | `127.0.0.1` only | Web interface and management API on the PC itself |
| `8481` | All interfaces, HTTPS | QoffeeLink and pairing; the web interface only if *Remote UI* is switched on |

Both can be changed in the settings. The installer opens port 8481 in the Windows firewall for private and domain networks only.

## Versions

Agent and integration share one version number and one release; install both from the same release if you can. The wire
protocol has its own version, negotiated on every connection, so an integration and an agent that are a version apart still
talk to each other. If they share no protocol version, Home Assistant shows a repair issue.
