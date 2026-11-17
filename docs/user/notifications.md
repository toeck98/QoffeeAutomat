# Notifications on the PC

Home Assistant can show a **notification on the desktop of the PC**: a title, a message, optionally a picture and up to three
buttons. A press of a button comes back to Home Assistant as an [event](events.md) that an automation can react to.

The notification is a Windows toast notification, so it appears in the corner of the screen of the logged-in user and stays in
the notification center like any other.

## What you get

* A **Notification** entity (`notify` platform) on the device of the PC. It sends a message and an optional title.
* The action **`qoffeeautomat.send_notification`**, which can also send a picture, a tag and buttons.
* The agent event **`notification_action`**, raised when a button is pressed.

Both exist only while the agent **announces the feature `notify`**. An agent that cannot show notifications has no entity, and the
action fails with a clear message. The feature needs the session helper of the logged-in user, so a notification can be shown only
**while a user is logged in** (the same condition as for every command that runs as the user).

## Send a message

The entity action of Home Assistant knows a message and a title:

```yaml
actions:
  - action: notify.send_message
    target:
      entity_id: notify.gaming_pc_notification
    data:
      title: Dinner
      message: It is ready.
```

## Picture and buttons

For more, use `qoffeeautomat.send_notification`:

```yaml
actions:
  - action: qoffeeautomat.send_notification
    target:
      device_id: "{{ device_id('sensor.gaming_pc_power_state') }}"
    data:
      title: Doorbell
      message: Somebody is at the door.
      image_url: http://homeassistant.local:8123/local/door.png
      tag: door
      actions:
        - action: open_door
          title: Open the door
        - action: ignore
          title: Ignore
```

| Field | Meaning |
|-------|---------|
| `message` | The text. Required. Up to 1024 characters. |
| `title` | The title. Up to 128 characters. |
| `image_url` | An `http` or `https` address of a picture, see [the picture](#the-picture). |
| `tag` | A name that comes back with every button press. A new notification with the **same tag replaces** the old one. |
| `actions` | Up to **three buttons**. Each has an `action` (its id, letters, digits and `_ . : -`, up to 64 characters) and a `title` (the label). Ids must differ. |

Mistakes that the integration can see (more than three buttons, a bad id, a button without a label) are reported to the automation
at once. The action waits until the agent says what became of the notification and fails if the PC could not show it, for example
because nobody is logged in or notifications from *QoffeeAutomat* are switched off in Windows. With *Focus assist* or *Do not
disturb* on, Windows may skip the pop-up and put the notification straight into the notification center; that is not an error.

!!! note "Text is text"
    Title, message and labels are shown as they are. Markup, quotes and template syntax in them are not interpreted by anything.

## The picture

The **agent** fetches the picture, not Home Assistant and not the session of the user, so the limits are enforced in one place:

* only `http` and `https`, no user name or password in the address, no link-local address (`169.254.x.x`), at most three
  redirects and ten seconds in total;
* **at most 1 MiB**, and only PNG, JPEG, GIF or BMP, judged by the content type and by the first bytes.

A picture that fails any of this is left out and the notification appears **without** it; it is not an error. The PC must be able
to reach the address, so use an address of Home Assistant that the PC can resolve (for example `http://homeassistant.local:8123/local/...`
or the IP address), not an address that is only valid inside a container. This is the only network request that a notification
causes, and it goes to the address **you** put in the action.

## React to a button

A press raises the agent event `notification_action` with the `action` (the id of the button) and the `tag` of the notification:

```yaml
triggers:
  - trigger: event
    event_type: qoffeeautomat_event
    event_data:
      type: notification_action
      data:
        tag: door
        action: open_door
actions:
  - action: lock.unlock
    target:
      entity_id: lock.front_door
```

Leave out `tag` to react to the button in every notification, and use `{{ trigger.event.data.data.action }}` in a template to
tell the buttons apart in one automation. The event also fires on the *Events* entity of the device (event type
`notification_action`), see [Events](events.md#using-events-in-home-assistant).

A press arrives **only while Home Assistant is connected** to the agent: an event that happens while the link is down is not
delivered later (see [Events](events.md)). The buttons of a notification also stop working when the session helper of the user
has restarted (log off, restart of the PC) since the notification was shown; a notification expires after a day at the latest and
on a restart of the PC.

## Troubleshooting

| Symptom | Check |
|---------|-------|
| There is no notification entity | The agent has to announce the feature `notify` (a current Windows agent does). Check the agent version, then **Settings → Devices & services → QoffeeAutomat → … → Reload**. |
| The action fails with *no session* | Nobody is logged in, or the session helper has not started yet. Wait for the event `session_ready`. |
| The action fails with *Windows does not show notifications from QoffeeAutomat* | Windows settings → **System → Notifications**: notifications on, and *QoffeeAutomat* not switched off in the list of senders. |
| Nothing appears on screen, but the action succeeded | *Do not disturb* or *Focus assist* is on: look in the notification center. |
| The picture is missing | Open the address in a browser on the PC. It must be a PNG, JPEG, GIF or BMP of at most 1 MiB and reachable without a login. |
| A press does nothing | Look at the bus event with **Developer tools → Events → Listen to** `qoffeeautomat_event`. If it arrives, the trigger of the automation does not match (`tag`, `action`). |
