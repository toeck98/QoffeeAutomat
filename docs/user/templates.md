# Template gallery

The template gallery holds ready-made [commands](commands.md) and [sensors](sensors.md). Open the page **Templates** in the
agent UI, look at a template, and add it: it becomes a new command or sensor that you change like any other, and the scripts it
uses are copied for you.

## Add a template

1. Page **Templates**. Use the search field to narrow the list.
2. **Preview** shows what the template does: the context it runs in (*Runs as*), its Home Assistant control, its variables, and the
   text of its scripts. Read scripts before you add them, as you would any script that runs on your PC.
3. **Add** creates the command or sensor. The message that follows names the new id, lists the copied scripts and links to
   the editor, where you adjust paths, names and other details.

Adding the same template again makes a second, independent copy. The id gets a number (`set_volume`, `set_volume_2`), the name
gets the same number (*Set volume (2)*), and every copy has its own script file (`set_display_profile.ps1`,
`set_display_profile_2.ps1`), so editing one never changes another.

A template that runs other commands, such as the mode group *PC mode*, also adds the commands that it needs and that do not exist
yet. Commands that already exist are reused. A mode group is added from the *Modes* section of the gallery and opens in the *Modes*
editor.

!!! note "Check before you rely on it"
    A template is a starting point. Programs such as *Launch SimHub* point at the usual install folder, the sensors watch an
    example drive, process or service, and some commands are hidden from Home Assistant until you decide to show them (*Shown to
    Home Assistant* in the preview). Open the new command or sensor, adjust it, and use **Test**.

## The templates

| Template | Kind | What it does |
|----------|------|--------------|
| Set display profile, Save display profile | command | Switch and save monitor layouts (needs the DisplayConfig module). |
| PC mode | mode group | The modes of the prototype: Normal, Study, TV gaming, TV entertainment and Racing, each with steps for entering and leaving it (display profile, volume, programs). |
| Launch application | command | Starts a program on the desktop of the logged-in user. |
| Launch MOZA Pit House, Launch SimHub, Launch Steam Big Picture | command | Program launchers that the example mode uses. |
| Open web address | command | Opens an http or https address in the default browser. |
| Set volume | command | Sets the master volume; a number entity in Home Assistant. |
| Lock the PC | command | Locks the session of the logged-in user; a button in Home Assistant. |
| Put the PC to sleep | command | Puts the PC to sleep; a button in Home Assistant. See [Wake-on-LAN](wake-on-lan.md) for the way back. |
| Kill process | command | Ends the processes of the logged-in user with a given name. Unsaved work in them is lost. |
| Empty the recycle bin | command | Permanently deletes the contents of the recycle bin of the logged-in user. |
| Disk space free | sensor | The free space of a drive in percent. Change the drive letter in the script. |
| Program running | sensor | On while a process with a given name is running. |
| Service state | sensor | The state of a Windows service. |

## Where the templates come from

The templates ship with the agent, in the folder `templates` next to the executable (`commands`, `sensors` and `scripts`
below it). The same files are in the repository under `templates/`. The gallery reads them once when it is first opened; they are
not changed by adding a template. Set the environment variable `QOFFEEAUTOMAT_TEMPLATES_DIR` to read another folder.
A template that is not valid is left out of the gallery; the tests of the repository fail for such a file, so a release has none.

The agent offers the gallery through the management API (`GET /api/v1/templates`, `GET /api/v1/templates/{id}`,
`POST /api/v1/templates/{id}/instantiate`); see the [API reference](reference/api.md).
