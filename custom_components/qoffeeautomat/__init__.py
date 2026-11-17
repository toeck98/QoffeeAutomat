"""The QoffeeAutomat integration (docs/plan/PLAN.md S07, S10)."""

from functools import partial

from homeassistant.const import CONF_HOST, CONF_PORT, Platform
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import (
    ConfigEntryAuthFailed,
    ConfigEntryError,
    ConfigEntryNotReady,
)
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.typing import ConfigType
from homeassistant.loader import async_get_integration

from .client import (
    AuthenticationError,
    QoffeeLinkClient,
    QoffeeLinkError,
    UnsupportedProtocolError,
)
from .const import CONF_FINGERPRINT, CONF_LAST_BOOT_ID, CONF_TOKEN, DOMAIN
from .entity import async_load_translation_keys
from .manifest_manager import ManifestManager
from .protocol import ProtocolError
from .repairs import (
    async_create_protocol_mismatch_issue,
    async_delete_protocol_mismatch_issue,
)
from .runtime import (
    ConnectionState,
    QoffeeAutomatConfigEntry,
    QoffeeAutomatRuntimeData,
    SubscriptionManager,
)
from .services import async_setup_services

PLATFORMS: list[Platform] = [
    Platform.BINARY_SENSOR,
    Platform.BUTTON,
    Platform.EVENT,
    Platform.NUMBER,
    Platform.SELECT,
    Platform.SENSOR,
    Platform.SWITCH,
    Platform.TEXT,
]

CONFIG_SCHEMA = cv.config_entry_only_config_schema(DOMAIN)


async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Register the actions of the integration."""
    async_setup_services(hass)
    return True


def _store_boot_id(
    hass: HomeAssistant, entry: QoffeeAutomatConfigEntry, boot_id: str
) -> None:
    """Remember the boot id of the agent in the config entry."""
    hass.config_entries.async_update_entry(
        entry, data={**entry.data, CONF_LAST_BOOT_ID: boot_id}
    )


async def async_setup_entry(
    hass: HomeAssistant, entry: QoffeeAutomatConfigEntry
) -> bool:
    """Set up QoffeeAutomat from a config entry."""
    integration = await async_get_integration(hass, DOMAIN)
    host: str = entry.data[CONF_HOST]
    port: int = entry.data[CONF_PORT]
    placeholders = {"host": host, "port": str(port)}
    client = QoffeeLinkClient(
        async_get_clientsession(hass),
        host,
        port,
        entry.data[CONF_TOKEN],
        entry.data[CONF_FINGERPRINT],
        client_name=hass.config.location_name,
        client_version=str(integration.version or "0"),
    )

    # Test the first connection before the setup goes on, so a PC that is switched off
    # leads to "retrying" instead of an integration without entities.
    try:
        await client.probe()
    except AuthenticationError as err:
        raise ConfigEntryAuthFailed(
            translation_domain=DOMAIN, translation_key="invalid_auth"
        ) from err
    except UnsupportedProtocolError as err:
        async_create_protocol_mismatch_issue(
            hass, entry.entry_id, entry.title, err.agent_range
        )
        raise ConfigEntryError(
            translation_domain=DOMAIN, translation_key="unsupported_protocol"
        ) from err
    except QoffeeLinkError as err:
        raise ConfigEntryNotReady(
            translation_domain=DOMAIN,
            translation_key="cannot_connect",
            translation_placeholders=placeholders,
        ) from err
    async_delete_protocol_mismatch_issue(hass, entry.entry_id)

    manager = ManifestManager(hass, entry, client)
    connection = ConnectionState(
        client,
        entry.title,
        last_boot_id=entry.data.get(CONF_LAST_BOOT_ID),
        store_boot_id=partial(_store_boot_id, hass, entry),
        on_manifest_rev=manager.handle_hello_rev,
    )
    subscriptions = SubscriptionManager(hass, client, entry.title)
    # Callbacks run in reverse order: subscriptions, manifest, connection, client.
    entry.async_on_unload(client.stop)
    entry.async_on_unload(connection.close)
    entry.async_on_unload(manager.close)
    entry.async_on_unload(subscriptions.async_shutdown)

    client.start()
    try:
        await connection.async_wait_first_connection()
    except TimeoutError as err:
        raise ConfigEntryNotReady(
            translation_domain=DOMAIN,
            translation_key="cannot_connect",
            translation_placeholders=placeholders,
        ) from err
    if client.auth_failed:
        raise ConfigEntryAuthFailed(
            translation_domain=DOMAIN, translation_key="invalid_auth"
        )
    try:
        manifest = await manager.async_load()
    except (QoffeeLinkError, ProtocolError) as err:
        raise ConfigEntryNotReady(
            translation_domain=DOMAIN, translation_key="manifest_unavailable"
        ) from err

    entry.runtime_data = QoffeeAutomatRuntimeData(
        client=client,
        manifest_manager=manager,
        connection=connection,
        subscriptions=subscriptions,
        agent_id=manifest.agent.id,
        translation_keys=await async_load_translation_keys(hass),
    )
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)

    # From now on a rejected token (revoked in the agent) starts reauthentication.
    entry.async_on_unload(
        client.add_auth_failed_listener(lambda _: entry.async_start_reauth(hass))
    )
    return True


async def async_unload_entry(
    hass: HomeAssistant, entry: QoffeeAutomatConfigEntry
) -> bool:
    """Unload a config entry; the callbacks registered on it stop the client."""
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)


async def async_remove_entry(
    hass: HomeAssistant, entry: QoffeeAutomatConfigEntry
) -> None:
    """Remove the repair issue of a deleted config entry."""
    async_delete_protocol_mismatch_issue(hass, entry.entry_id)
