"""End-to-end test of the hub: hub image, two agents, Home Assistant (S14, S20, T919).

One scenario, told in file order on one stack. The tests see the stack only from outside
(the REST APIs of the containers and the Docker CLI): the hub is set up, an enrollment
token joins both agents, a profile with a command and a sensor is rolled out to a tag,
stored metrics are read through the query API and the Prometheus endpoint, a command
runs through the management tunnel and as a fleet action, Home Assistant is paired with
the hub, `local_config: locked` refuses a local write, and one removed agent loses its
hub layer.
"""

from datetime import UTC, datetime, timedelta
from typing import Any

import pytest

from hub_driver import (
    AGENT_NAMES,
    COMMAND_ID,
    FLEET_TAG,
    HUB_USERNAME,
    HUB_VARIABLE_VALUE,
    PROFILE_ID,
    SENSOR_ID,
    AgentApi,
    ApiError,
    Hub,
    HubEntry,
    HubHomeAssistant,
    fleet_profile,
    wait_for,
)

ENTITY_TIMEOUT = 180
METRIC_TIMEOUT = 120
ROLLOUT_TIMEOUT = 120
RUN_TIMEOUT = 90

FIRST, SECOND = AGENT_NAMES["agent1"], AGENT_NAMES["agent2"]
EXPECTED_OUTPUT = f"{HUB_VARIABLE_VALUE} Qoffee x3"


def _wait_for_entity(ha: HubHomeAssistant, agent: str, name: str) -> dict[str, Any]:
    return wait_for(
        lambda: ha.entity(agent, name),
        timeout=ENTITY_TIMEOUT,
        what=f"the entity {name!r} of {agent}",
    )


def _wait_for_finished_run(hub: Hub, agent_id: str, run_id: str) -> dict[str, Any]:
    """Poll a run through the tunnel until it has finished; returns the run detail."""

    def finished() -> dict[str, Any] | None:
        status, body = hub.tunnel(agent_id, "GET", f"runs/{run_id}")
        if status == 200 and body["state"] == "finished":
            detail: dict[str, Any] = body
            return detail
        return None

    return wait_for(finished, timeout=RUN_TIMEOUT, what=f"run {run_id} to finish")


# -- the hub -------------------------------------------------------------------------


def test_hub_is_set_up_with_a_self_signed_certificate(hub: Hub) -> None:
    """The first administrator exists; the hub serves its own certificate (D-026)."""
    info = hub.info()
    assert info["tlsMode"] == "selfsigned"
    assert info["fingerprint"]
    assert info["signingKey"]
    assert info["name"] == "E2E Hub"


def test_hub_api_needs_a_session_and_the_anti_forgery_header(hub: Hub) -> None:
    """S20 "Hub security" 1: no anonymous access, no change without the CSRF header."""
    assert hub.anonymous_client().request("GET", "/api/v1/agents")[0] == 401
    assert hub.anonymous_client().request("GET", "/metrics")[0] == 401

    without_header = hub.client_without_csrf_header()
    status, body = without_header.request(
        "POST", "/api/v1/enrollment-tokens", {"name": "csrf", "maxUses": 1}
    )
    assert status == 403, body
    assert body["code"] == "csrf_header_missing"


# -- enrollment and roll-out ---------------------------------------------------------


def test_both_agents_are_enrolled_and_connected(
    hub: Hub, agents: dict[str, AgentApi], enrolled: dict[str, str]
) -> None:
    """The token joins two agents; both hold a link and carry the tag of the token."""
    assert set(enrolled) == set(AGENT_NAMES.values())
    for api in agents.values():
        assert api.hub_status()["state"] == "connected"
        assert api.hub_status()["hubName"] == "E2E Hub"
    by_name = {agent["name"]: agent for agent in hub.agents()}
    for name in AGENT_NAMES.values():
        assert by_name[name]["tags"] == [FLEET_TAG]
        assert by_name[name]["status"] != "pending"

    tokens = hub.http.call("GET", "/api/v1/enrollment-tokens")["tokens"]
    assert [token["uses"] for token in tokens] == [2]
    # The token is used up: a third PC could not join with it.
    assert tokens[0]["active"] is False


def test_profile_is_rolled_out_to_the_tag(
    hub: Hub, enrolled: dict[str, str], rolled_out: None
) -> None:
    """Publishing a profile for a tag gives both agents a generation that they apply."""
    overview = hub.rollout()
    assert overview["counts"]["total"] == len(AGENT_NAMES)
    assert overview["counts"]["upToDate"] == len(AGENT_NAMES)
    for agent_id in enrolled.values():
        status = hub.config_status(agent_id)
        assert status["state"] == "up_to_date"
        assert status["appliedGeneration"] == status["desiredGeneration"] > 0
        applied = {
            (doc["kind"], doc["id"]): doc["state"] for doc in status["documents"]
        }
        assert applied[("command", COMMAND_ID)] == "applied"
        assert applied[("sensor", SENSOR_ID)] == "applied"

    profile = hub.http.call("GET", f"/api/v1/profiles/{PROFILE_ID}/rollout")
    assert profile["counts"]["upToDate"] == len(AGENT_NAMES)


def test_agents_run_the_hub_layer(
    agents: dict[str, AgentApi], rolled_out: None
) -> None:
    """The agent side of the roll-out: hub documents, variable and policy."""
    for api in agents.values():
        config = api.hub_config()
        assert COMMAND_ID in str(config), config
        assert SENSOR_ID in str(config), config
        assert "GREETING" in str(config["variables"]), config
        assert COMMAND_ID in api.command_ids()
        status = api.hub_status()
        assert status["configGeneration"] > 0
        assert status["localConfigLocked"] is False


# -- metrics -------------------------------------------------------------------------


def test_metrics_are_stored_and_queryable(hub: Hub, enrolled: dict[str, str]) -> None:
    """The hub stores what the agents report (collection interval 5 s) and serves it."""
    start = (datetime.now(UTC) - timedelta(minutes=10)).strftime("%Y-%m-%dT%H:%M:%SZ")
    for agent_id in enrolled.values():

        def stored(agent_id: str = agent_id) -> bool:
            return any(
                metric["metricId"] == "sim.counter" and metric["value"] is not None
                for metric in hub.latest_metrics(agent_id)
            )

        wait_for(stored, timeout=METRIC_TIMEOUT, what="a stored sim.counter value")

        def two_points(agent_id: str = agent_id) -> list[dict[str, Any]] | None:
            series = hub.query_metrics(agent_id, "sim.counter", start=start)
            points: list[dict[str, Any]] = series[0]["points"] if series else []
            return points if len(points) >= 2 else None

        points = wait_for(
            two_points, timeout=METRIC_TIMEOUT, what="two stored samples of sim.counter"
        )
        values = [point["value"] for point in points]
        assert values == sorted(values), "the simulated counter only counts up"


def test_prometheus_export_needs_a_read_only_token(
    hub: Hub, enrolled: dict[str, str]
) -> None:
    """`/metrics` takes a token of role viewer, no session and no other role (S20)."""
    viewer = hub.token_client(hub.create_api_token("e2e-prometheus", "viewer"))
    operator = hub.token_client(hub.create_api_token("e2e-operator", "operator"))

    # A signed-in browser session is not enough: the export is for Prometheus.
    assert hub.http.request("GET", "/metrics")[0] == 401
    assert operator.request("GET", "/metrics")[0] == 403

    def exported() -> str | None:
        status, text = viewer.request("GET", "/metrics")
        assert status == 200, text
        ready = all(
            f'qoffeeautomat_sim_counter{{agent_id="{agent_id}"' in text
            and f'qoffeeautomat_agent_up{{agent_id="{agent_id}",agent="{name}"}} 1'
            in text
            for name, agent_id in enrolled.items()
        )
        return str(text) if ready else None

    text = wait_for(exported, timeout=METRIC_TIMEOUT, what="the Prometheus export")
    assert "# TYPE qoffeeautomat_sim_counter" in text


# -- commands through the tunnel and as a fleet action -------------------------------


def test_command_runs_through_the_management_tunnel(
    hub: Hub, enrolled: dict[str, str], rolled_out: None
) -> None:
    """The hub calls the agent's own API; the run has the source `hub`."""
    agent_id = enrolled[FIRST]
    status, started = hub.tunnel(
        agent_id,
        "POST",
        f"commands/{COMMAND_ID}/run",
        {"vars": {"Name": "Qoffee", "Count": 3}},
    )
    assert status == 202, started

    run = _wait_for_finished_run(hub, agent_id, started["runId"])
    assert run["succeeded"] is True, run
    assert EXPECTED_OUTPUT in run["standardOutput"]
    assert run["source"] == "hub"

    # The agent validates the variables itself; the hub is not trusted (S20).
    status, body = hub.tunnel(
        agent_id, "POST", f"commands/{COMMAND_ID}/run", {"vars": {"Count": "many"}}
    )
    assert status == 422, body

    # The role decides on the hub: a viewer token cannot start a run (RBAC).
    viewer = hub.token_client(hub.create_api_token("e2e-viewer", "viewer"))
    status, _ = viewer.request(
        "POST",
        f"/api/v1/agents/{agent_id}/agent-api/commands/{COMMAND_ID}/run",
        {"vars": {"Name": "x"}},
    )
    assert status == 403


def test_fleet_action_runs_a_command_on_every_agent_of_the_tag(
    hub: Hub, enrolled: dict[str, str], rolled_out: None
) -> None:
    """A bulk run with variables on a tag ends as success on both agents."""
    started = hub.http.call(
        "POST",
        "/api/v1/fleet-actions/bulk-runs",
        {
            "command": COMMAND_ID,
            "vars": {"Name": "Qoffee", "Count": 3},
            "tags": [FLEET_TAG],
            "parallelism": 2,
            "timeoutSeconds": 60,
        },
    )

    def completed() -> dict[str, Any] | None:
        action: dict[str, Any] = hub.http.call(
            "GET", f"/api/v1/fleet-actions/{started['id']}"
        )
        return action if action["state"] != "running" else None

    action = wait_for(completed, timeout=RUN_TIMEOUT, what="the bulk run to finish")
    assert action["state"] == "completed", action
    assert action["counts"]["success"] == len(AGENT_NAMES), action
    assert {item["agentId"] for item in action["items"]} == set(enrolled.values())


# -- Home Assistant through the fleet link -------------------------------------------


def test_home_assistant_is_paired_with_the_hub(
    ha: HubHomeAssistant, hub_entry: HubEntry
) -> None:
    """The config flow "Connect to a hub" creates one entry that loads (D-031)."""

    def entry_loaded() -> bool:
        entries = ha.http.call("GET", "/api/config/config_entries/entry")
        return any(
            entry["entry_id"] == hub_entry.entry_id and entry["state"] == "loaded"
            for entry in entries
        )

    assert wait_for(entry_loaded, timeout=ENTITY_TIMEOUT, what="the hub entry to load")


def test_home_assistant_holds_the_fleet_token_of_the_pairing(
    hub: Hub, hub_entry: HubEntry
) -> None:
    """The pairing created one fleet token for all agents, and a connection uses it."""

    def connected() -> list[dict[str, Any]] | None:
        tokens: list[dict[str, Any]] = hub.http.call("GET", "/api/v1/fleet-tokens")[
            "tokens"
        ]
        return tokens if tokens and tokens[0]["connections"] >= 1 else None

    tokens = wait_for(connected, timeout=ENTITY_TIMEOUT, what="the fleet link")
    assert len(tokens) == 1
    assert tokens[0]["scope"]["type"] == "all"
    assert tokens[0]["active"] is True


def test_devices_and_entities_of_both_agents_appear(
    ha: HubHomeAssistant, hub_entry: HubEntry
) -> None:
    """Every PC of the hub is a device; the hub sensor is an entity of both."""
    for agent in (FIRST, SECOND):
        connectivity = _wait_for_entity(ha, agent, "Connectivity")
        assert connectivity["state"] == "on"
        _wait_for_entity(ha, agent, "Power state")
        _wait_for_entity(ha, agent, "Simulated counter")
        marker = wait_for(
            lambda agent=agent: (
                entity
                if (entity := ha.entity(agent, "Hub marker")) and entity["state"] == "7"
                else None
            ),
            timeout=ENTITY_TIMEOUT,
            what=f"the hub sensor of {agent} to report 7",
        )
        assert marker["entity_id"].startswith("sensor.")

    first = _wait_for_entity(ha, FIRST, "Connectivity")
    second = _wait_for_entity(ha, SECOND, "Connectivity")
    assert ha.device_id(first["entity_id"]) != ha.device_id(second["entity_id"])


def test_command_with_variables_runs_through_the_fleet_link(
    ha: HubHomeAssistant, hub_entry: HubEntry
) -> None:
    """`qoffeeautomat.run_command` on a PC of the hub goes through the fleet link."""
    for agent in (FIRST, SECOND):
        entity = _wait_for_entity(ha, agent, "Simulated counter")
        device_id = ha.device_id(entity["entity_id"])
        assert device_id

        response = ha.http.call(
            "POST",
            "/api/services/qoffeeautomat/run_command?return_response",
            {
                "device_id": device_id,
                "command": COMMAND_ID,
                "variables": {"Name": "Qoffee", "Count": 3},
                "timeout": 60,
            },
            timeout=90,
        )
        result = response["service_response"]
        assert result["exit_code"] == 0, result
        assert EXPECTED_OUTPUT in result["stdout"]


# -- local_config: locked ------------------------------------------------------------


def test_locked_local_config_refuses_a_local_write(
    hub: Hub, agents: dict[str, AgentApi], rolled_out: None
) -> None:
    """With `local_config: locked` a write to the local layer is refused (D-029)."""
    first = agents["agent1"]
    # Before: a local command can be changed.
    status, body = first.set_command_enabled("e2e_ping", enabled=True)
    assert status == 200, body

    hub.save_profile(fleet_profile(local_config="locked"))
    hub.publish_profile(PROFILE_ID, "lock the local configuration")

    wait_for(
        lambda: first.hub_status()["localConfigLocked"],
        timeout=ROLLOUT_TIMEOUT,
        what="the agent to apply the lock",
    )
    status, body = first.set_command_enabled("e2e_ping", enabled=False)
    assert status == 403, body
    assert body["code"] == "local_config_locked"

    # Viewing stays possible, and so does everything the hub documents offer.
    assert COMMAND_ID in first.command_ids()


# -- removing an agent ---------------------------------------------------------------


def test_removed_agent_loses_its_hub_layer(
    hub: Hub,
    agents: dict[str, AgentApi],
    enrolled: dict[str, str],
    ha: HubHomeAssistant,
    hub_entry: HubEntry,
) -> None:
    """Removing an agent on the hub deletes its hub layer; the local layer is back."""
    removed, kept = agents["agent2"], agents["agent1"]
    removed_id = enrolled[SECOND]
    hub.http.call("DELETE", f"/api/v1/agents/{removed_id}")

    assert hub.agent_id(SECOND) is None
    wait_for(
        lambda: removed.hub_status()["state"] == "not_joined",
        timeout=ROLLOUT_TIMEOUT,
        what="the removed agent to leave the hub",
    )
    config = removed.hub_config()
    assert config["items"] == [], config
    commands = removed.command_ids()
    assert COMMAND_ID not in commands
    # The local layer is active again and writable (the lock was a hub policy).
    assert "e2e_ping" in commands
    status, body = removed.set_command_enabled("e2e_ping", enabled=True)
    assert status == 200, body

    # The hub no longer reaches it, and the other agent is untouched.
    assert hub.tunnel(removed_id, "GET", "commands")[0] == 404
    assert kept.hub_status()["state"] == "connected"
    assert COMMAND_ID in kept.command_ids()

    wait_for(
        lambda: ha.entity(SECOND, "Connectivity") is None,
        timeout=ENTITY_TIMEOUT,
        what="the entities of the removed agent to disappear",
    )
    assert ha.entity(FIRST, "Connectivity") is not None


# -- audit ---------------------------------------------------------------------------


def test_audit_log_records_publish_and_fleet_actions(
    hub: Hub, enrolled: dict[str, str]
) -> None:
    """S20 "Hub security" 2: changes, publishes and runs are in the audit log."""

    def audited() -> list[dict[str, Any]] | None:
        entries = hub.audit_entries()
        actions = {entry["action"] for entry in entries}
        # "bulkRunFinished" is written by the hub when the action ends.
        needed = {"publishProfile", "startBulkRun", "bulkRunFinished", "removeAgent"}
        return entries if needed <= actions else None

    entries = wait_for(audited, timeout=ROLLOUT_TIMEOUT, what="the audit entries")
    by_action: dict[str, dict[str, Any]] = {}
    for entry in reversed(entries):  # the newest entry of every action wins
        by_action[entry["action"]] = entry

    publish = by_action["publishProfile"]
    assert publish["target"] == f"profile:{PROFILE_ID}"
    assert publish["user"] == HUB_USERNAME
    assert publish["result"] == "success"

    for action in ("startBulkRun", "bulkRunFinished"):
        entry = by_action[action]
        assert entry["target"].startswith("fleet-action:"), entry
        assert entry["target"].endswith(f":command:{COMMAND_ID}"), entry
        assert set(entry["agents"]) == set(enrolled.values()), entry
        assert entry["result"] == "success", entry

    assert by_action["removeAgent"]["target"] == f"agent:{enrolled[SECOND]}"


def test_rejected_api_calls_raise_api_errors(hub: Hub) -> None:
    """Sanity check of the harness: a call without a session raises ApiError."""
    with pytest.raises(ApiError) as error:
        hub.anonymous_client().call("GET", "/api/v1/agents", timeout=5)
    assert error.value.status == 401
