"""Tests for the integration manifest, the HACS metadata and repository rules."""

import json
import os
import re
from pathlib import Path
from typing import Any

import pytest

ROOT = Path(__file__).resolve().parents[1]
INTEGRATION = ROOT / "custom_components" / "qoffeeautomat"

# Keys that HACS requires in the manifest of an integration (PLAN.md S10, T003).
HACS_REQUIRED_KEYS = (
    "domain",
    "documentation",
    "issue_tracker",
    "codeowners",
    "name",
    "version",
)

FORBIDDEN_FILE_NAMES = {"config.yaml", "config.yml", "config.json"}


def _load_json(path: Path) -> dict[str, Any]:
    data: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
    return data


@pytest.fixture(name="manifest")
def manifest_fixture() -> dict[str, Any]:
    """Return the parsed manifest.json of the integration."""
    return _load_json(INTEGRATION / "manifest.json")


def test_manifest_has_the_keys_required_by_hacs(manifest: dict[str, Any]) -> None:
    for key in HACS_REQUIRED_KEYS:
        assert manifest.get(key), f"manifest.json is missing {key}"


def test_manifest_describes_a_local_push_device_integration(
    manifest: dict[str, Any],
) -> None:
    assert manifest["domain"] == "qoffeeautomat"
    assert manifest["config_flow"] is True
    assert manifest["integration_type"] == "device"
    assert manifest["iot_class"] == "local_push"
    assert manifest["requirements"] == []
    assert manifest["zeroconf"] == ["_qoffeeautomat._tcp.local."]
    assert manifest["codeowners"] == ["@toeck98"]


def test_manifest_version_matches_the_dotnet_version(
    manifest: dict[str, Any],
) -> None:
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", props)
    assert match is not None
    assert manifest["version"] == match.group(1)


def test_domain_constant_matches_the_manifest(manifest: dict[str, Any]) -> None:
    from custom_components.qoffeeautomat.const import DOMAIN

    assert manifest["domain"] == DOMAIN


def test_hacs_json_downloads_the_release_zip() -> None:
    hacs = _load_json(ROOT / "hacs.json")
    assert hacs["name"]
    assert hacs["zip_release"] is True
    assert hacs["filename"] == "qoffeeautomat.zip"
    assert hacs["homeassistant"] == "2026.3.0"


def test_strings_and_english_translation_are_in_sync() -> None:
    strings = _load_json(INTEGRATION / "strings.json")
    english = _load_json(INTEGRATION / "translations" / "en.json")
    assert strings == english


def test_no_configuration_file_names_outside_hidden_folders() -> None:
    """D-034: no config.yaml, config.yml or config.json outside hidden folders."""
    offenders = []
    for current, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if not d.startswith(".") and d != "node_modules"]
        offenders.extend(
            str(Path(current, name)) for name in files if name in FORBIDDEN_FILE_NAMES
        )
    assert offenders == []
