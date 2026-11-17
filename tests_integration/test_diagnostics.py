"""Diagnostics, repairs, translations, brand images and quality scale tests (T507)."""

import importlib.util
import json
from pathlib import Path
from types import ModuleType
from typing import Any
from unittest.mock import patch

import pytest
import yaml
from homeassistant.config_entries import ConfigEntryState
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import issue_registry as ir

from custom_components.qoffeeautomat.const import (
    CONF_FINGERPRINT,
    CONF_TOKEN,
    DOMAIN,
)
from custom_components.qoffeeautomat.diagnostics import (
    async_get_config_entry_diagnostics,
    async_get_device_diagnostics,
)
from custom_components.qoffeeautomat.repairs import (
    ProtocolMismatchRepairFlow,
    async_create_fix_flow,
    protocol_mismatch_issue_id,
)
from tests_integration.fake_agent import FakeAgent
from tests_integration.helpers import EntryFactory

ROOT = Path(__file__).resolve().parents[1]
INTEGRATION = ROOT / "custom_components" / "qoffeeautomat"
SYNC_BRAND = ROOT / "scripts" / "dev" / "sync_brand.py"

REDACTED = "**REDACTED**"


def _load_sync_brand() -> ModuleType:
    spec = importlib.util.spec_from_file_location("sync_brand", SYNC_BRAND)
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _assert_no_secret(diagnostics: dict[str, Any], agent: FakeAgent) -> None:
    """Assert that no secret, address or fingerprint is in the diagnostics."""
    text = json.dumps(diagnostics, default=str)
    lowered = text.lower()
    forbidden = [
        agent.token,
        agent.certificate.fingerprint,
        agent.certificate.fingerprint.lower(),
        agent.certificate.fingerprint.upper(),
        agent.host,
        "gaming-pc.local",
        "a4:b1:c1:00:11:22",
        "a4b1c1001122",
        "a4-b1-c1-00-11-22",
        "GAMING-PC",  # the host name of the PC (the agent *name* is Gaming-PC)
    ]
    for value in forbidden:
        assert value not in text, f"diagnostics leak {value!r}"
        if value != "GAMING-PC":
            assert value.lower() not in lowered, f"diagnostics leak {value!r}"


# -- diagnostics -------------------------------------------------------------------


async def test_config_entry_diagnostics_contain_no_secret(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Token, fingerprint, host, host name and MAC addresses are redacted."""
    entry = await entry_factory()
    assert entry.state is ConfigEntryState.LOADED

    diagnostics = await async_get_config_entry_diagnostics(hass, entry)

    data = diagnostics["entry"]["data"]
    assert data[CONF_TOKEN] == REDACTED
    assert data[CONF_FINGERPRINT] == REDACTED
    assert data["host"] == REDACTED
    assert diagnostics["agent"]["hostname"] == REDACTED
    assert diagnostics["agent"]["macs"] == REDACTED
    assert diagnostics["agent"]["ui_url"] == REDACTED
    _assert_no_secret(diagnostics, fake_agent)


async def test_config_entry_diagnostics_describe_the_agent(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Structure that helps with support is kept: versions, ids, connection state."""
    entry = await entry_factory()

    diagnostics = await async_get_config_entry_diagnostics(hass, entry)

    assert diagnostics["agent"]["version"]
    assert diagnostics["agent"]["os"] == "windows"
    assert diagnostics["connection"]["online"] is True
    assert diagnostics["connection"]["protocol"] == {"min": 1, "max": 1}
    metric_ids = {metric["id"] for metric in diagnostics["manifest"]["metrics"]}
    assert "cpu.usage" in metric_ids
    assert diagnostics["manifest"]["commands"]
    assert isinstance(diagnostics["subscribed_metrics"], list)


async def test_device_diagnostics_contain_no_secret(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The device registry data are redacted as well."""
    entry = await entry_factory()
    device = dr.async_get(hass).async_get_device_by_identifier(
        (DOMAIN, fake_agent.agent_id), entry.entry_id
    )
    assert device is not None

    diagnostics = await async_get_device_diagnostics(hass, entry, device)

    assert diagnostics["device"]["connections"] == REDACTED
    assert diagnostics["device"]["configuration_url"] == REDACTED
    assert diagnostics["entities"]
    _assert_no_secret(diagnostics, fake_agent)


# -- repairs -------------------------------------------------------------------------


async def test_protocol_mismatch_creates_and_clears_a_repair_issue(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """No common protocol version raises an issue; a compatible agent clears it."""
    fake_agent.protocol_range = (2, 3)

    entry = await entry_factory()

    assert entry.state is ConfigEntryState.SETUP_ERROR
    issue = ir.async_get(hass).async_get_issue(
        DOMAIN, protocol_mismatch_issue_id(entry.entry_id)
    )
    assert issue is not None
    assert issue.is_fixable
    assert issue.severity is ir.IssueSeverity.ERROR
    assert issue.translation_key == "protocol_mismatch"
    placeholders = issue.translation_placeholders
    assert placeholders is not None
    assert placeholders["agent_protocol"] == "2-3"
    assert placeholders["integration_protocol"] == "1-1"
    assert fake_agent.host not in json.dumps(placeholders)

    fake_agent.protocol_range = (1, 1)
    assert await hass.config_entries.async_reload(entry.entry_id)
    await hass.async_block_till_done()

    assert entry.state is ConfigEntryState.LOADED
    assert (
        ir.async_get(hass).async_get_issue(
            DOMAIN, protocol_mismatch_issue_id(entry.entry_id)
        )
        is None
    )


async def test_removing_the_entry_removes_the_issue(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A deleted config entry leaves no repair issue behind."""
    fake_agent.protocol_range = (2, 3)
    entry = await entry_factory()
    issue_id = protocol_mismatch_issue_id(entry.entry_id)
    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is not None

    await hass.config_entries.async_remove(entry.entry_id)
    await hass.async_block_till_done()

    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is None


async def test_repair_flow_reloads_the_entry(hass: HomeAssistant) -> None:
    """Confirming the repair flow schedules a reload of the config entry."""
    flow = await async_create_fix_flow(
        hass, "protocol_mismatch_abc", {"entry_id": "abc"}
    )
    assert isinstance(flow, ProtocolMismatchRepairFlow)
    flow.hass = hass
    flow.handler = DOMAIN
    flow.context = {"issue_id": "protocol_mismatch_abc"}

    shown = await flow.async_step_init()
    assert shown["type"] is FlowResultType.FORM
    assert shown["step_id"] == "confirm"

    with patch.object(hass.config_entries, "async_schedule_reload") as schedule:
        done = await flow.async_step_confirm({})

    assert done["type"] is FlowResultType.CREATE_ENTRY
    schedule.assert_called_once_with("abc")


async def test_repair_flow_rejects_unknown_issues(hass: HomeAssistant) -> None:
    """Only the protocol mismatch has a repair flow."""
    with pytest.raises(ValueError, match="No repair flow"):
        await async_create_fix_flow(hass, "other_issue", None)


# -- translations and icons ------------------------------------------------------------


def _load_json(path: Path) -> dict[str, Any]:
    data: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
    return data


def _paths(tree: dict[str, Any], prefix: str = "") -> set[str]:
    """Return the dotted paths of all leaves of a nested mapping."""
    found: set[str] = set()
    for key, value in tree.items():
        path = f"{prefix}{key}"
        if isinstance(value, dict):
            found |= _paths(value, f"{path}.")
        else:
            found.add(path)
    return found


def test_strings_and_english_translation_are_identical() -> None:
    """``strings.json`` is the source of ``translations/en.json``."""
    assert _load_json(INTEGRATION / "strings.json") == _load_json(
        INTEGRATION / "translations" / "en.json"
    )


def test_german_translation_has_the_same_keys_as_english() -> None:
    """Every English string has a German counterpart and vice versa."""
    english = _paths(_load_json(INTEGRATION / "translations" / "en.json"))
    german = _paths(_load_json(INTEGRATION / "translations" / "de.json"))
    assert english - german == set()
    assert german - english == set()


def test_every_translated_entity_has_an_icon() -> None:
    """Icon translations cover the entities and the action."""
    strings = _load_json(INTEGRATION / "strings.json")
    icons = _load_json(INTEGRATION / "icons.json")
    for platform, entities in strings["entity"].items():
        for key in entities:
            assert key in icons["entity"][platform], f"no icon for {platform}.{key}"
    assert icons["services"]["run_command"]["service"].startswith("mdi:")


def test_the_protocol_mismatch_issue_is_translated() -> None:
    """The repair issue has a title and a fix flow in both languages."""
    for name in ("strings.json", "translations/en.json", "translations/de.json"):
        issue = _load_json(INTEGRATION / name)["issues"]["protocol_mismatch"]
        assert issue["title"]
        assert issue["fix_flow"]["step"]["confirm"]["description"]


# -- quality scale ---------------------------------------------------------------------

TARGETED_RULES = {
    # Bronze
    "action-setup",
    "appropriate-polling",
    "brands",
    "common-modules",
    "config-flow-test-coverage",
    "config-flow",
    "dependency-transparency",
    "docs-actions",
    "docs-high-level-description",
    "docs-installation-instructions",
    "docs-removal-instructions",
    "entity-event-setup",
    "entity-unique-id",
    "has-entity-name",
    "runtime-data",
    "test-before-configure",
    "test-before-setup",
    "unique-config-entry",
    # Silver
    "action-exceptions",
    "config-entry-unloading",
    "docs-configuration-parameters",
    "docs-installation-parameters",
    "entity-unavailable",
    "integration-owner",
    "log-when-unavailable",
    "parallel-updates",
    "reauthentication-flow",
    "test-coverage",
    # Gold (PLAN.md S10)
    "devices",
    "diagnostics",
    "discovery",
    "discovery-update-info",
    "dynamic-devices",
    "stale-devices",
    "entity-category",
    "entity-device-class",
    "entity-disabled-by-default",
    "entity-translations",
    "exception-translations",
    "reconfiguration-flow",
    "repair-issues",
    "icon-translations",
}


def test_quality_scale_marks_every_targeted_rule_done_or_exempt() -> None:
    """Each rule is ``done`` or ``exempt`` with a reason; none is missing."""
    document = yaml.safe_load((INTEGRATION / "quality_scale.yaml").read_text("utf-8"))
    rules: dict[str, Any] = document["rules"]

    assert TARGETED_RULES - set(rules) == set()
    for name, rule in rules.items():
        if rule == "done":
            continue
        assert isinstance(rule, dict), f"{name}: unexpected value {rule!r}"
        assert rule.get("status") == "exempt", f"{name} is neither done nor exempt"
        assert str(rule.get("comment", "")).strip(), f"{name}: exempt without reason"


def test_every_platform_sets_parallel_updates() -> None:
    """The ``parallel-updates`` rule is claimed as done: every platform sets it."""
    for platform in (
        "binary_sensor",
        "button",
        "event",
        "number",
        "select",
        "sensor",
        "switch",
        "text",
    ):
        source = (INTEGRATION / f"{platform}.py").read_text(encoding="utf-8")
        assert "PARALLEL_UPDATES" in source, platform


# -- brand images ----------------------------------------------------------------------


def test_brand_images_are_in_sync_with_the_assets() -> None:
    """``brand/`` equals ``assets/brand/`` (open item O-05)."""
    sync_brand = _load_sync_brand()
    assert sync_brand.main(["--check"]) == 0


def test_sync_brand_check_detects_differences(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """``--check`` fails for a changed, a missing and an unexpected file."""
    sync_brand = _load_sync_brand()
    source = tmp_path / "assets"
    target = tmp_path / "brand"
    source.mkdir()
    for name in sync_brand.BRAND_FILES:
        (source / name).write_bytes(name.encode())

    paths = ["--source", str(source), "--target", str(target)]

    # Missing target folder: fails, and a normal run creates it.
    assert sync_brand.main(["--check", *paths]) == 1
    assert sync_brand.main(paths) == 0
    assert sync_brand.main(["--check", *paths]) == 0

    (target / "icon.png").write_bytes(b"changed")
    assert sync_brand.main(["--check", *paths]) == 1
    assert "differs" in capsys.readouterr().err

    assert sync_brand.main(paths) == 0
    (target / "extra.png").write_bytes(b"x")
    assert sync_brand.main(["--check", *paths]) == 1
    assert "extra.png" in capsys.readouterr().err

    # --check never writes.
    assert (target / "extra.png").exists()
