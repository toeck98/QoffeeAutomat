# Migrating from the prototype

The prototype (tag `prototype-v0`, spelled "QoffeAutomat") switched display profiles and ran "modes" when Home Assistant
changed an entity. QoffeeAutomat keeps both features, but as ordinary commands built from templates instead of
hard-coded modules. This guide moves an existing installation over.

Reference material from the prototype is kept in [`docs/prototype/`](https://github.com/toeck98/QoffeeAutomat/tree/main/docs/prototype) in the repository.

## What became what

| Prototype | QoffeeAutomat |
|-----------|---------------|
| `DisplayModule`, state `Profile` or `Profile\|updateadapterids` | Command `set_display_profile` with the variables `Profile` and `UpdateAdapterIds`, bound to a Home Assistant `select`. |
| Profile XML files in `C:\ProgramData\Automation\Modes` | Same files, found by `set_display_profile.ps1` in the profile directory (below). |
| Fallback call of `Set-DisplayConfig` | Dropped. The script needs the `DisplayConfig` module and reports a clear error without it. |
| Passing a full file path as profile | Dropped. A profile is a name; paths are rejected (the value comes from Home Assistant). |
| `modes.json`: `DisplayConfig`, `Programs` with `Command`/`Arguments`, `Sleep` | The `enter` steps of a mode in a **mode group**: a `set_display_profile` step, one command per program, `delaySeconds` steps. |
| Mode names such as `TV-Rennsport-Mode` | One mode of the group `pc_mode` (here `racing`). The group appears in Home Assistant as one `select` entity that shows the real current mode. |
| Programs that stayed open after a mode change | The `exit` steps of a mode, which run before the next mode is entered (for example `kill_process`). The prototype had no such step. |
| Polling a Home Assistant entity over REST | QoffeeLink: Home Assistant connects to the agent; see [Pair with Home Assistant](user/pairing.md). |

## Step by step

1. **Install and pair** the new agent ([Install on Windows](user/install-windows.md), [First-run setup](user/first-run.md)) and the
   Home Assistant integration ([Install the integration](user/install-integration.md), [Pairing](user/pairing.md)). The prototype and the new agent can stay
   installed side by side; stop the prototype service before you try the new commands so that both do not react
   to the same event.
2. **Install the DisplayConfig module** for all users (PowerShell as administrator):
   `Install-Module DisplayConfig -Scope AllUsers`.
3. **Keep your profiles.** Either leave them in `C:\ProgramData\Automation\Modes` (the script still looks there) or
   copy the `*.xml` files to `%ProgramData%\QoffeeAutomat\display-profiles`. To use another folder, set the
   environment variable `QOFFEEAUTOMAT_DISPLAY_PROFILES` (user or machine scope) to it; that folder is searched first.
   Lookup of the profile name follows the prototype: `TV-Mode`, `TV-Mode.xml`, `TV Mode.xml`, and finally any file
   whose name contains the text.
4. **Import the templates.** Copy `templates/scripts/*.ps1` to the scripts directory
   (`%ProgramData%\QoffeeAutomat\scripts`) and create the commands from `templates/commands/*.json` in the UI
   (paste the JSON into a new command), or send each file with `PUT /api/v1/commands/{id}`. The templates are:

   | File | Purpose |
   |------|---------|
   | `scripts/set_display_profile.ps1` | Applies a profile (`Profile`, `UpdateAdapterIds`). |
   | `scripts/save_display_profile.ps1` | Saves the current layout as a profile (`Name`, `Overwrite`). |
   | `commands/set_display_profile.json` | The command, bound to a `select`. |
   | `commands/save_display_profile.json` | The command for saving, not exposed to Home Assistant. |
   | `commands/launch_steam_big_picture.json`, `launch_moza_pit_house.json`, `launch_simhub.json` | One command per program of the prototype's example mode. |
   | `commands/kill_process.json`, `commands/set_volume.json` | Used by the exit and enter steps of the example modes. |
   | `modes/pc_mode.json` | A mode group with the prototype's five modes: Normal, Study, TV gaming, TV entertainment and Racing (`TV-Rennsport-Mode`). |

   The mode group is added from the **Template gallery** (*Modes* section, button *Add*); it is not a command. Add the
   command templates it uses first, or let the gallery add them.

5. **Edit the profile names.** Open `set_display_profile` and replace the `Profile` options (`Normal-Mode`,
   `TV-Mode`, `Study-Mode`) with the names of your profile files without `.xml`. The options become the choices of the
   select entity in Home Assistant.
6. **Edit the program paths** in the three `launch_*` commands (the `file` and `args` fields) if your installation
   differs from the defaults in the templates.
7. **Recreate your modes.** Open the **Modes** page of the web interface and add the template `pc_mode` from the
   gallery, or create a group (*New mode group*) and add one mode per entry of `modes.json`. Every mode has *enter* steps
   (what the prototype ran) and *exit* steps (new: what to undo when the mode is left). The steps are edited like the steps
   of a sequence:

   | `modes.json` | Step in the `enter` list of the mode |
   |--------------|---------------|
   | `"DisplayConfig": "TV-Mode"` | `{ "command": "set_display_profile", "vars": { "Profile": "TV-Mode" } }` |
   | `{ "Command": "C:\\...\\app.exe", "Arguments": "..." }` | `{ "command": "launch_app" }` with a `process` command `launch_app` (arguments as separate array entries, `waitForExit: false`) |
   | `{ "Sleep": 10 }` | `{ "delaySeconds": 10 }` |

   Modes that only change the display (`Normal-Mode`, `Study-Mode`, `TV-Entertainment-Mode`) have one step. A step runs an
   ordinary command and cannot switch a mode. Choose the **default mode** (the template uses `normal`), what happens
   **after the PC starts** (`onBoot`: `reset`, `keep` or `apply_default`) and **when a step fails** (`onError`: the template
   uses `continue`, so a failing program does not stop the rest; `stop` leaves the state unknown).
8. **Remove the prototype** once the new entities work: uninstall its service, delete its Home Assistant helper
   entities and automations, and archive `modes.json`.

## Differences to know

- **Arguments are separate entries.** The prototype passed one `Arguments` string; a `process` action takes an array,
  so `-start steam://open/bigpicture -fulldesktopres` becomes `["-start", "steam://open/bigpicture", "-fulldesktopres"]`.
  Values are never spliced into a command line (D-012).
- **Programs run in the user session.** Both the display change and the program launches use `runAs: user`: they are
  executed by the SessionHost inside the logged-in session, because services cannot change the display configuration or
  show windows. Without a logged-in user the commands fail with `no_session`.
- **Programs are started without waiting.** `waitForExit: false` lets the sequence continue while an application with a
  window keeps running, as in the prototype. Use a `delaySeconds` step to give a program time to start.
- **Pause and history.** While the agent is paused (tray icon, `Pause commands`), every run is rejected with the error
  `paused`. Runs, their output and errors appear in the history, which the prototype did not keep.
- **A mode has a real state.** The prototype only fired a command; it never knew which mode was active. The agent now
  remembers the current mode of every group, publishes it as the metric `mode.<group>` and shows it as a `select` entity in Home
  Assistant and on the *Modes* page. It changes to the new mode only after the enter steps finished, and a failed switch with
  `onError: stop` leaves it *unknown*. Each change fires the event `mode_changed` (see [Events](user/events.md)).
- **One switch at a time.** A second switch of the same group while one runs is rejected with `busy`.
- **Modes after a restart of the PC.** After a start of the operating system the group follows `onBoot`; after a restart of
  the agent alone nothing happens and the state is kept.
- **No more `Profile|updateadapterids` states.** Set the boolean variable `UpdateAdapterIds` instead, for example in the
  action `qoffeeautomat.run_command` with `vars: { Profile: TV-Mode, UpdateAdapterIds: true }`.

## Checking the result

1. Run `save_display_profile` once with the layout you have now, then change the layout by hand.
2. Select the profile in the `set_display_profile` entity in Home Assistant (or `POST /api/v1/commands/set_display_profile/run`
   with `{ "vars": { "Profile": "..." } }`): the monitors return to the saved layout.
3. Choose your mode in the `select` entity of the group (or press its button on the *Modes* page): the exit steps of the old
   mode run, then the profile is applied, the programs start, and the wait happens between them. The entity shows the new
   mode when the last step has finished.
4. Open the run history in the UI to see the result of every step.

This check on a real desktop is open item O-06 of the plan.

## Next steps

* To start the PC in a mode from an automation (wake, wait for the session, apply the mode), see
  [Example: start the PC in a mode](user/example-mode-start.md).
* More on sequences, variables and the Home Assistant controls: [Commands](user/commands.md).
