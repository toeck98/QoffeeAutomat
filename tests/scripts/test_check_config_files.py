"""Tests for scripts/dev/check_config_files.py (D-034, task T918).

A temporary directory is no git checkout, so the script walks the tree there.
"""

import importlib.util
import sys
from pathlib import Path
from types import ModuleType

import pytest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "dev" / "check_config_files.py"


@pytest.fixture(scope="module")
def checker() -> ModuleType:
    spec = importlib.util.spec_from_file_location("check_config_files", SCRIPT)
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["check_config_files"] = module
    spec.loader.exec_module(module)
    return module


def touch(root: Path, relative: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("x\n", encoding="utf-8")


def test_the_repository_is_clean(checker: ModuleType) -> None:
    assert checker.find_violations(ROOT) == []


def test_the_app_definition_is_allowed(checker: ModuleType, tmp_path: Path) -> None:
    touch(tmp_path, "packaging/hub/ha-app/config.yaml")
    assert checker.find_violations(tmp_path) == []


@pytest.mark.parametrize("name", ["config.yaml", "config.yml", "config.json"])
def test_other_files_are_forbidden_at_any_depth(
    checker: ModuleType, tmp_path: Path, name: str
) -> None:
    touch(tmp_path, name)
    touch(tmp_path, f"src/deep/er/{name}")
    assert checker.find_violations(tmp_path) == [name, f"src/deep/er/{name}"]


def test_the_app_name_elsewhere_is_forbidden(
    checker: ModuleType, tmp_path: Path
) -> None:
    touch(tmp_path, "packaging/hub/config.yaml")
    touch(tmp_path, "packaging/hub/ha-app/config.yml")
    touch(tmp_path, "packaging/hub/ha-app/config.json")
    assert checker.find_violations(tmp_path) == [
        "packaging/hub/config.yaml",
        "packaging/hub/ha-app/config.json",
        "packaging/hub/ha-app/config.yml",
    ]


def test_hidden_folders_are_ignored(checker: ModuleType, tmp_path: Path) -> None:
    touch(tmp_path, ".devcontainer/config.json")
    touch(tmp_path, ".github/config.yaml")
    assert checker.find_violations(tmp_path) == []


def test_similar_names_are_fine(checker: ModuleType, tmp_path: Path) -> None:
    touch(tmp_path, "docs/myconfig.yaml")
    touch(tmp_path, "docs/config.yaml.md")
    touch(tmp_path, "docs/config.toml")
    assert checker.find_violations(tmp_path) == []


def test_main_exit_status(
    checker: ModuleType, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    assert checker.main(["--root", str(tmp_path)]) == 0
    touch(tmp_path, "config.json")
    assert checker.main(["--root", str(tmp_path)]) == 1
    assert "config.json" in capsys.readouterr().out
