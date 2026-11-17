# The PC as a media player

The integration adds a **Media player** entity to the device of the PC. It shows what plays on the PC (title, artist, album, the
program, length and position) and controls it: play, pause, stop, next, previous, volume and mute. It works with every program
that shows up in the Windows volume flyout, such as Spotify, a browser, VLC or the Windows media player.

The entity exists only while the agent **announces the feature `media`** (a Windows agent does). Without the feature there is no
media player entity at all.

## What it shows

| Attribute | Comes from |
|-----------|------------|
| State `playing`, `paused`, `idle` | Metric `media.state` |
| Title, artist, album | Metrics `media.title`, `media.artist`, `media.album` |
| Program | Metric `media.app` (for example *Spotify*) |
| Length, position | Metrics `media.duration`, `media.position` |
| Volume, mute | Metrics `audio.volume`, `audio.muted` |

* The player is **idle** when no program offers a media session, or when it neither plays nor is paused. It then shows no title.
* The player is **unavailable** while the agent is not connected and **while nobody is logged in**: the media session belongs to
  the user, so without a user session there is nothing to show or control.
* With several programs that play, Windows decides which is the *current* session (usually the one that started last). The
  player follows that one.
* The position is sent **only when something changes** (another track, play or pause, or a jump in the track), together with
  the time at which it was valid; Home Assistant counts on from there. This keeps the traffic low. A PC whose clock is off still
  shows the right position, because the position is related to the time of the message that carried it.
* **Cover art is not supported.** The agent does not publish it, so the entity has no picture. This is a known limitation.

## The metrics are separate entities

The `media.*` metrics also exist as **sensors, all disabled by default**. Enable the ones you want to use in automations
(*Media title*, *Media state*, ...). The media player entity does **not** depend on them: it subscribes to the metrics itself,
and only while the media player entity is enabled. A PC whose media player is disabled in the entity registry costs nothing:
the agent does not even look at the media session.

## Controls

| Action of Home Assistant | What the agent does |
|--------------------------|---------------------|
| `media_player.media_play`, `media_pause`, `media_play_pause`, `media_stop` | Sends the request to the current media session (the built-in action `media.control`). |
| `media_player.media_next_track`, `media_previous_track` | The same. |
| `media_player.volume_set`, `volume_up`, `volume_down` | Sets the master volume (the audio action, `Level` 0 to 100). |
| `media_player.volume_mute` | Mutes or unmutes the sound (the audio action). |

`media.control` is also a [built-in action](reference/actions.md) that you can use in your own commands, with the choice
*Action* (`play`, `pause`, `play_pause`, `next`, `previous`, `stop`). If the session cannot take the request, or there is no
session, the agent **presses the media key** instead (what `media.key` does). A few details:

* `play` on a track that plays and `pause` on one that does not succeed and change nothing, so repeating a request is safe.
* The media keys only know a toggle. Without any session `pause` therefore presses nothing: a toggle could *start* music.
* A control **does not wait** for the effect. The state follows from the metrics, which the agent samples every two seconds while
  the media player is enabled, so a pause shows up in Home Assistant a moment later.
* Turning the PC on or off is not a feature of the media player; use the Wake-on-LAN button and the power commands.

```yaml
# Pause the music when the doorbell rings
actions:
  - action: media_player.media_pause
    target:
      entity_id: media_player.gaming_pc_media_player
```

## Troubleshooting

| Symptom | Check |
|---------|-------|
| There is no media player entity | The agent has to announce the feature `media`. Check the agent version and reload the integration entry. |
| The player is unavailable | The PC is off or nobody is logged in. Wait for the event `session_ready`. |
| The player stays *idle* although music plays | The program does not register with the Windows media transport controls. If it does not show in the volume flyout (the one that opens with the volume keys), it is not visible to the agent either. |
| A control does nothing | Look at the log of the PC and at the *Events* of the device for `command_finished`. A program that does not accept the request is handled with the media key. |
| The title is missing for a browser tab | The page has to provide its metadata to Windows; many do not. |
