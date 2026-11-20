"""Fixtures of the hub end-to-end test (PLAN.md S14, S20, task T919).

``pytest tests/e2e/hub`` builds and starts the compose stack (hub image, two agents with
the simulated platform, Home Assistant), runs the scenario, and stops the stack. The
fixtures build on each other and are session-scoped: the scenario is one story, told in
file order by ``test_hub_e2e.py``.

* ``E2E_SKIP_COMPOSE=1``  use an already-running stack (nothing started or stopped).
* ``E2E_KEEP_STACK=1``    keep the stack running after tests (CI collects logs first).
"""

import os
import shutil
from collections.abc import Iterator

import pytest

from hub_driver import (
    AGENT_NAMES,
    COMMAND_ID,
    FLEET_TAG,
    HUB_HOST_IN_COMPOSE,
    HUB_PORT_IN_COMPOSE,
    PROFILE_ID,
    SENSOR_ID,
    AgentApi,
    Hub,
    HubEntry,
    HubHomeAssistant,
    allow_network,
    compose,
    fleet_profile,
    library_document,
    wait_for,
)

ENROLL_TIMEOUT = 120
ROLLOUT_TIMEOUT = 120
FLOW_BASE = "/api/config/config_entries/flow"


@pytest.fixture(autouse=True)
def _network_allowed() -> None:
    """Let the test talk to the containers even if dev plugins block sockets."""
    allow_network()


@pytest.fixture(scope="session")
def hub_stack() -> Iterator[None]:
    """Start the compose stack and stop it afterwards."""
    if shutil.which("docker") is None:
        pytest.skip("docker is not installed")
    if os.environ.get("E2E_SKIP_COMPOSE"):
        yield
        return
    compose("up", "--build", "--detach", "--wait", "--wait-timeout", "900")
    try:
        yield
    finally:
        if not os.environ.get("E2E_KEEP_STACK"):
            compose("down", "--volumes", "--remove-orphans", check=False)


@pytest.fixture(scope="session")
def hub(hub_stack: None) -> Hub:
    """The hub with its first administrator, signed in (token from /data)."""
    allow_network()
    client = Hub()
    wait_for(client.is_healthy, timeout=120, what="the hub health check")
    client.set_up()
    return client


@pytest.fixture(scope="session")
def agents(hub_stack: None) -> dict[str, AgentApi]:
    """The management APIs of the two agents, signed in."""
    allow_network()
    apis = {service: AgentApi(service) for service in AGENT_NAMES}
    for api in apis.values():
        wait_for(api.is_healthy, timeout=120, what=f"the health check of {api.name}")
        api.login()
    return apis


@pytest.fixture(scope="session")
def enrolled(hub: Hub, agents: dict[str, AgentApi]) -> dict[str, str]:
    """Join both agents with one enrollment token; returns the agent ids by name."""
    created = hub.create_enrollment_token(name="e2e", tags=[FLEET_TAG], max_uses=2)
    enrollment = created["enrollmentString"]
    assert enrollment.startswith("qahub1."), "not an enrollment string"

    for api in agents.values():
        api.join(enrollment)

    def connected() -> bool:
        return all(
            api.hub_status()["state"] == "connected" for api in agents.values()
        ) and all(hub.agent_id(name) for name in AGENT_NAMES.values())

    wait_for(connected, timeout=ENROLL_TIMEOUT, what="both agents to connect")
    ids: dict[str, str] = {}
    for name in AGENT_NAMES.values():
        agent_id = hub.agent_id(name)
        assert agent_id is not None
        ids[name] = agent_id
    return ids


@pytest.fixture(scope="session")
def published(hub: Hub, enrolled: dict[str, str]) -> int:
    """Publish a profile (command and sensor) to the tag; returns the revision."""
    hub.save_library_document(
        "command", COMMAND_ID, library_document("hub_echo.command.json")
    )
    hub.save_library_document(
        "sensor", SENSOR_ID, library_document("hub_marker.sensor.json")
    )
    hub.save_profile(fleet_profile(local_config="allow"))
    result = hub.publish_profile(PROFILE_ID, "end-to-end test")
    hub.assign(PROFILE_ID, f"tag:{FLEET_TAG}")
    return int(result["revision"])


@pytest.fixture(scope="session")
def rolled_out(hub: Hub, published: int) -> None:
    """Wait until both agents have applied the published generation."""

    def up_to_date() -> bool:
        overview = hub.rollout()
        agents = overview["agents"]
        return len(agents) == len(AGENT_NAMES) and all(
            agent["state"] == "up_to_date" for agent in agents
        )

    wait_for(up_to_date, timeout=ROLLOUT_TIMEOUT, what="the roll-out to finish")


@pytest.fixture(scope="session")
def ha(hub_stack: None) -> HubHomeAssistant:
    """Home Assistant with an owner account and an access token."""
    allow_network()
    home_assistant = HubHomeAssistant()
    home_assistant.onboard()
    return home_assistant


@pytest.fixture(scope="session")
def hub_entry(ha: HubHomeAssistant, hub: Hub, rolled_out: None) -> HubEntry:
    """Pair Home Assistant with the hub: config flow "Connect to a hub" (D-031)."""
    info = hub.info()
    pairing = hub.create_fleet_pairing("E2E Home Assistant")

    flow = ha.http.call(
        "POST", FLOW_BASE, {"handler": "qoffeeautomat", "show_advanced_options": False}
    )
    assert flow["type"] == "menu", flow
    assert "hub" in flow["menu_options"], flow

    flow = ha.http.call(
        "POST", f"{FLOW_BASE}/{flow['flow_id']}", {"next_step_id": "hub"}
    )
    assert flow["step_id"] == "hub", flow

    flow = ha.http.call(
        "POST",
        f"{FLOW_BASE}/{flow['flow_id']}",
        {"host": HUB_HOST_IN_COMPOSE, "port": HUB_PORT_IN_COMPOSE},
    )
    assert flow["step_id"] == "hub_confirm", flow
    # The flow shows the fingerprint of the hub certificate for the user to compare.
    shown = str(flow["description_placeholders"]).replace(":", "").lower()
    fingerprint = str(info["fingerprint"])
    assert fingerprint.replace(":", "").lower() in shown, flow

    flow = ha.http.call("POST", f"{FLOW_BASE}/{flow['flow_id']}", {})
    assert flow["step_id"] == "hub_pair", flow

    result = ha.http.call(
        "POST", f"{FLOW_BASE}/{flow['flow_id']}", {"code": pairing["code"]}
    )
    assert result["type"] == "create_entry", result
    assert result["title"] == info["name"]
    return HubEntry(entry_id=result["result"]["entry_id"], fingerprint=fingerprint)
