"""Notify platform: notifications on the desktop of the PC (PLAN.md S10, S16, T702).

The entity exists only while the manifest lists the feature ``notify``. With it
``notify.send_message`` shows the message (and the title) as a toast notification on the
desktop of the logged-in user::

    actions:
      - action: notify.send_message
        target:
          entity_id: notify.gaming_pc_notification
        data:
          title: Dinner
          message: It is ready.

The entity action of Home Assistant has only a message and a title. Buttons and a
picture need the action ``qoffeeautomat.send_notification``; see ``notifications.py``
for how a press of a button reaches an automation.
"""

from homeassistant.components.notify import NotifyEntity
from homeassistant.components.notify.const import NotifyEntityFeature
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .const import FEATURE_NOTIFY, NOTIFY_KEY
from .entity import QoffeeAutomatEntity, async_setup_feature_platform
from .notifications import async_send_notification
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData

# Notifications do not wait for each other.
PARALLEL_UPDATES = 0


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the notify entity while the agent announces the feature ``notify``."""
    await async_setup_feature_platform(
        hass,
        entry,
        async_add_entities,
        feature=FEATURE_NOTIFY,
        domain="notify",
        key=NOTIFY_KEY,
        build=QoffeeAutomatNotifyEntity,
    )


class QoffeeAutomatNotifyEntity(QoffeeAutomatEntity, NotifyEntity):
    """Shows a notification on the desktop of the PC."""

    _attr_translation_key = "notification"
    _attr_supported_features = NotifyEntityFeature.TITLE

    def __init__(self, runtime: QoffeeAutomatRuntimeData) -> None:
        """Create the entity of the PC."""
        super().__init__(runtime, NOTIFY_KEY)

    async def async_send_message(self, message: str, title: str | None = None) -> None:
        """Show the message; raises a translated error if the agent cannot."""
        await async_send_notification(self._runtime, message=message, title=title)
