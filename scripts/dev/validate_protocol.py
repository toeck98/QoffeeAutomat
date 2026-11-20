#!/usr/bin/env python3
"""Validate the QoffeeLink schemas and fixtures in protocol/ (task T101).

Checks, using only the Python standard library:

* every schema under protocol/schema/ is well formed and only uses the supported subset
  of JSON Schema draft 2020-12 (the .NET tests implement the same subset);
* every message type of PLAN.md S07, the manifest and the pairing request and response
  have a schema and at least one valid fixture;
* all valid fixtures validate and all fixtures under invalid/ are rejected;
* the rev of the manifest fixture equals the SHA-256 based value defined in S07 and the
  manifest_rev of the hello fixtures matches it;
* the same for the configuration documents under schema/config/ and fixtures/config/;
* the same for the hub extensions of PLAN.md S20 (task T901) under schema/hub/ and
  fixtures/hub/: enrollment, config_sync, mgmt_tunnel and the fleet link;
* in the hub fixtures: the hash of every document equals the SHA-256 of its canonical
  JSON body (of the UTF-8 text for scripts), the items of config_desired.json are
  exactly the documents of config_documents.json, and every enrollment_string fixture
  decodes to the enrollment_payload fixture of the same variant;
* no file is named config.yaml, config.yml or config.json (D-034).

Usage:
    python scripts/dev/validate_protocol.py [--protocol-dir DIR] [--update-rev]
                                            [--update-derived]

--update-rev recomputes rev in fixtures/manifest.json and manifest_rev in the hello
fixtures, then validates. --update-derived recomputes the values of the hub fixtures
that are derived from other content (document hashes in config_documents*.json and
config_desired.json, the enrollment_string fixtures), then validates; run it after
editing a hub document body or an enrollment payload. Exit status: 0 when everything
is valid, 1 otherwise.
"""

import argparse
import base64
import binascii
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any, Self
from urllib.parse import urldefrag, urljoin

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PROTOCOL_DIR = REPO_ROOT / "protocol"

DRAFT_2020_12 = "https://json-schema.org/draft/2020-12/schema"
MESSAGE_TYPES = (
    "hello",
    "client_hello",
    "get_manifest",
    "manifest_changed",
    "subscribe",
    "state",
    "run",
    "event",
    "bye",
    "result",
)
OTHER_SCHEMAS = ("manifest", "pair_request", "pair_response")
CONFIG_SCHEMAS = ("command", "sensor", "mode", "metrics", "settings")
# Hub extensions (PLAN.md S20, T901). Fixture prefix selects the schema in schema/hub/.
# hub/message.schema.json dispatches these on t alone ...
HUB_MESSAGES = (
    "config_desired",
    "config_documents",
    "config_status",
    "mgmt_request",
    "mgmt_stream_open",
    "mgmt_stream_data",
    "mgmt_stream_close",
    "credential_rotate",
    "unenroll",
    "get_agents",
    "agent_added",
    "agent_removed",
    "agent_status",
)
# ... and these by the member a (agent-scoped S07 messages) or hub (the fleet hello);
# a frame without that member is not recognised by the dispatch schema, so invalid
# fixtures are only checked against their own schema.
HUB_SCOPED_MESSAGES = (
    "fleet_hello",
    "fleet_get_manifest",
    "fleet_subscribe",
    "fleet_run",
    "fleet_manifest_changed",
    "fleet_state",
    "fleet_event",
)
# Result frames whose data has the shape of the answer to a hub request.
HUB_RESULTS = (
    "result_get_agents",
    "result_config_desired",
    "result_mgmt_request",
    "result_mgmt_stream_open",
    "result_hub_error",
)
HUB_OTHER_SCHEMAS = (
    "enrollment_payload",
    "enrollment_string",
    "enroll_request",
    "enroll_response",
    "pair_response",
)
HUB_SCHEMAS = (*HUB_MESSAGES, *HUB_SCOPED_MESSAGES, *HUB_RESULTS, *HUB_OTHER_SCHEMAS)
ENROLLMENT_STRING_PREFIX = "qahub1."
FORBIDDEN_FILE_NAMES = {"config.yaml", "config.yml", "config.json"}

# Keywords that are only annotations for this validator.
ANNOTATIONS = {
    "$schema",
    "$id",
    "$comment",
    "$defs",
    "title",
    "description",
    "default",
    "examples",
    "format",
}
# Keywords that are evaluated. Anything else makes the schema check fail so that the
# schemas stay within the subset that both test suites implement.
APPLICATORS = {
    "$ref",
    "type",
    "enum",
    "const",
    "required",
    "properties",
    "additionalProperties",
    "propertyNames",
    "minProperties",
    "items",
    "minItems",
    "maxItems",
    "uniqueItems",
    "minLength",
    "maxLength",
    "pattern",
    "minimum",
    "maximum",
    "exclusiveMinimum",
    "exclusiveMaximum",
    "allOf",
    "anyOf",
    "oneOf",
    "if",
    "then",
    "else",
}
# Canonical numbers: plain integers or decimals without exponent or trailing zeros.
CANONICAL_NUMBER = re.compile(r"^-?(0|[1-9][0-9]*)(\.[0-9]*[1-9])?$")
REV_IN_MANIFEST = re.compile(r'("rev"\s*:\s*")[0-9a-f]{12}(")')
REV_IN_HELLO = re.compile(r'("manifest_rev"\s*:\s*")[0-9a-f]{12}(")')
HASH_MEMBER = re.compile(r'("hash"\s*:\s*")[0-9a-f]{64}(")')


class RawNumber(float):
    """A float that remembers how it was written, for canonical JSON."""

    literal: str

    def __new__(cls, literal: str) -> Self:
        obj = super().__new__(cls, literal)
        obj.literal = literal
        return obj


def load_json(path: Path) -> Any:
    """Read a JSON file; decimals keep their literal text."""
    return json.loads(path.read_text(encoding="utf-8"), parse_float=RawNumber)


# ---------------------------------------------------------------------------
# Canonical JSON and manifest revision (PLAN.md S07, D-039)
# ---------------------------------------------------------------------------

_ESCAPES = {
    '"': '\\"',
    "\\": "\\\\",
    "\b": "\\b",
    "\f": "\\f",
    "\n": "\\n",
    "\r": "\\r",
    "\t": "\\t",
}


def _escape(text: str) -> str:
    parts = ['"']
    for char in text:
        if char in _ESCAPES:
            parts.append(_ESCAPES[char])
        elif ord(char) < 0x20:
            parts.append(f"\\u{ord(char):04x}")
        else:
            parts.append(char)
    parts.append('"')
    return "".join(parts)


def canonical_json(value: Any) -> str:
    """Return the canonical JSON text of a value (see protocol/README.md)."""
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, RawNumber):
        if not CANONICAL_NUMBER.match(value.literal):
            msg = f"number {value.literal!r} is not in canonical form"
            raise ValueError(msg)
        return value.literal
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        msg = "floats must be loaded through load_json"
        raise TypeError(msg)
    if isinstance(value, str):
        return _escape(value)
    if isinstance(value, list):
        return "[" + ",".join(canonical_json(item) for item in value) + "]"
    if isinstance(value, dict):
        # Ordinal order of the UTF-16 code units; members with null are omitted.
        keys = sorted(
            (key for key, item in value.items() if item is not None),
            key=lambda key: key.encode("utf-16-be"),
        )
        members = (f"{_escape(key)}:{canonical_json(value[key])}" for key in keys)
        return "{" + ",".join(members) + "}"
    msg = f"unsupported JSON value {value!r}"
    raise TypeError(msg)


def compute_rev(manifest: dict[str, Any]) -> str:
    """First 12 hex characters of the SHA-256 of the canonical manifest without rev."""
    body = {key: item for key, item in manifest.items() if key != "rev"}
    digest = hashlib.sha256(canonical_json(body).encode("utf-8")).hexdigest()
    return digest[:12]


def document_hash(kind: str, body: Any) -> str:
    """SHA-256 (hex) of a hub document: canonical JSON or UTF-8 text for scripts."""
    if kind == "script":
        if not isinstance(body, str):
            msg = "the body of a script must be text"
            raise TypeError(msg)
        raw = body.encode("utf-8")
    else:
        raw = canonical_json(body).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def decode_enrollment_string(text: str) -> Any:
    """Decode qahub1.<base64url(JSON)> (PLAN.md S20); raises ValueError if malformed."""
    if not text.startswith(ENROLLMENT_STRING_PREFIX):
        msg = f"does not start with {ENROLLMENT_STRING_PREFIX!r}"
        raise ValueError(msg)
    body = text[len(ENROLLMENT_STRING_PREFIX) :]
    try:
        raw = base64.urlsafe_b64decode(body + "=" * (-len(body) % 4))
        return json.loads(raw.decode("utf-8"))
    except (binascii.Error, UnicodeDecodeError, json.JSONDecodeError) as error:
        msg = f"payload is not base64url JSON ({error})"
        raise ValueError(msg) from error


def encode_enrollment_string(payload: Any) -> str:
    """Encode an enrollment payload as an enrollment string."""
    compact = json.dumps(payload, separators=(",", ":"), ensure_ascii=False)
    encoded = base64.urlsafe_b64encode(compact.encode("utf-8")).decode("ascii")
    return ENROLLMENT_STRING_PREFIX + encoded.rstrip("=")


# ---------------------------------------------------------------------------
# A small JSON Schema validator for the subset used by the protocol schemas
# ---------------------------------------------------------------------------


def _json_equal(left: Any, right: Any) -> bool:
    if isinstance(left, bool) or isinstance(right, bool):
        return isinstance(left, bool) and isinstance(right, bool) and left == right
    if isinstance(left, (int, float)) and isinstance(right, (int, float)):
        return float(left) == float(right)
    if isinstance(left, list) and isinstance(right, list):
        return len(left) == len(right) and all(
            _json_equal(a, b) for a, b in zip(left, right, strict=True)
        )
    if isinstance(left, dict) and isinstance(right, dict):
        return left.keys() == right.keys() and all(
            _json_equal(left[key], right[key]) for key in left
        )
    return type(left) is type(right) and left == right


def _is_type(value: Any, name: str) -> bool:
    match name:
        case "object":
            return isinstance(value, dict)
        case "array":
            return isinstance(value, list)
        case "string":
            return isinstance(value, str)
        case "boolean":
            return isinstance(value, bool)
        case "null":
            return value is None
        case "integer":
            if isinstance(value, bool):
                return False
            if isinstance(value, int):
                return True
            return isinstance(value, float) and value.is_integer()
        case "number":
            return isinstance(value, (int, float)) and not isinstance(value, bool)
    msg = f"unknown type {name!r}"
    raise ValueError(msg)


class SchemaSet:
    """All schemas of a folder tree, addressed by their $id."""

    def __init__(self, schema_dir: Path) -> None:
        self.documents: dict[str, Any] = {}
        self.problems: list[str] = []
        for path in sorted(schema_dir.rglob("*.schema.json")):
            document = load_json(path)
            uri = document.get("$id")
            if not isinstance(uri, str):
                self.problems.append(f"{path}: missing $id")
                continue
            if document.get("$schema") != DRAFT_2020_12:
                self.problems.append(f"{path}: $schema must be {DRAFT_2020_12}")
            if uri in self.documents:
                self.problems.append(f"{path}: duplicate $id {uri}")
            self.documents[uri] = document
            self._check_keywords(document, f"{path.name}#")

    def _check_keywords(self, schema: Any, where: str) -> None:
        if isinstance(schema, bool) or not isinstance(schema, dict):
            return
        for keyword, value in schema.items():
            if keyword not in ANNOTATIONS and keyword not in APPLICATORS:
                self.problems.append(f"{where}: unsupported keyword {keyword!r}")
            elif keyword == "properties":
                for name, sub in value.items():
                    self._check_keywords(sub, f"{where}/properties/{name}")
            elif keyword == "$defs":
                for name, sub in value.items():
                    self._check_keywords(sub, f"{where}/$defs/{name}")
            elif keyword in ("allOf", "anyOf", "oneOf"):
                for index, sub in enumerate(value):
                    self._check_keywords(sub, f"{where}/{keyword}/{index}")
            elif keyword in (
                "items",
                "additionalProperties",
                "propertyNames",
                "if",
                "then",
                "else",
            ):
                self._check_keywords(value, f"{where}/{keyword}")

    def resolve(self, base: str, reference: str) -> tuple[Any, str]:
        """Resolve a $ref against the base URI; returns the schema and its base."""
        uri, fragment = urldefrag(urljoin(base, reference))
        document = self.documents.get(uri)
        if document is None:
            msg = f"unresolved $ref {reference!r} (base {base})"
            raise KeyError(msg)
        node: Any = document
        for part in [p for p in fragment.split("/") if p]:
            node = node[part.replace("~1", "/").replace("~0", "~")]
        return node, uri

    def validate(self, instance: Any, schema_uri: str) -> list[str]:
        """Validate an instance against the schema with the given $id."""
        return self._validate(instance, self.documents[schema_uri], schema_uri, "")

    def _validate(self, value: Any, schema: Any, base: str, path: str) -> list[str]:
        if schema is True:
            return []
        if schema is False:
            return [f"{path or '/'}: not allowed"]
        errors: list[str] = []
        here = path or "/"
        if "$ref" in schema:
            target, target_base = self.resolve(base, schema["$ref"])
            errors += self._validate(value, target, target_base, path)
        if "type" in schema:
            names = schema["type"]
            names = [names] if isinstance(names, str) else names
            if not any(_is_type(value, name) for name in names):
                errors.append(f"{here}: expected type {names}")
        if "enum" in schema and not any(_json_equal(value, e) for e in schema["enum"]):
            errors.append(f"{here}: not one of {schema['enum']}")
        if "const" in schema and not _json_equal(value, schema["const"]):
            errors.append(f"{here}: must be {schema['const']!r}")
        errors += self._validate_number(value, schema, here)
        errors += self._validate_string(value, schema, here)
        if isinstance(value, dict):
            errors += self._validate_object(value, schema, base, path)
        if isinstance(value, list):
            errors += self._validate_array(value, schema, base, path)
        errors += self._validate_combinators(value, schema, base, path)
        return errors

    @staticmethod
    def _validate_number(value: Any, schema: dict[str, Any], here: str) -> list[str]:
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return []
        errors: list[str] = []
        number = float(value)
        if "minimum" in schema and number < schema["minimum"]:
            errors.append(f"{here}: below minimum {schema['minimum']}")
        if "maximum" in schema and number > schema["maximum"]:
            errors.append(f"{here}: above maximum {schema['maximum']}")
        if "exclusiveMinimum" in schema and number <= schema["exclusiveMinimum"]:
            errors.append(f"{here}: must be above {schema['exclusiveMinimum']}")
        if "exclusiveMaximum" in schema and number >= schema["exclusiveMaximum"]:
            errors.append(f"{here}: must be below {schema['exclusiveMaximum']}")
        return errors

    @staticmethod
    def _validate_string(value: Any, schema: dict[str, Any], here: str) -> list[str]:
        if not isinstance(value, str):
            return []
        errors: list[str] = []
        if "minLength" in schema and len(value) < schema["minLength"]:
            errors.append(f"{here}: shorter than {schema['minLength']}")
        if "maxLength" in schema and len(value) > schema["maxLength"]:
            errors.append(f"{here}: longer than {schema['maxLength']}")
        if "pattern" in schema and not re.search(schema["pattern"], value):
            errors.append(f"{here}: does not match {schema['pattern']}")
        return errors

    def _validate_object(
        self, value: dict[str, Any], schema: dict[str, Any], base: str, path: str
    ) -> list[str]:
        errors: list[str] = []
        here = path or "/"
        for name in schema.get("required", []):
            if name not in value:
                errors.append(f"{here}: missing required property {name!r}")
        if "minProperties" in schema and len(value) < schema["minProperties"]:
            errors.append(f"{here}: fewer than {schema['minProperties']} properties")
        properties = schema.get("properties", {})
        for name, sub in properties.items():
            if name in value:
                errors += self._validate(value[name], sub, base, f"{path}/{name}")
        if "additionalProperties" in schema:
            for name, item in value.items():
                if name not in properties:
                    errors += self._validate(
                        item, schema["additionalProperties"], base, f"{path}/{name}"
                    )
        if "propertyNames" in schema:
            for name in value:
                errors += self._validate(
                    name, schema["propertyNames"], base, f"{path}/{name}"
                )
        return errors

    def _validate_array(
        self, value: list[Any], schema: dict[str, Any], base: str, path: str
    ) -> list[str]:
        errors: list[str] = []
        here = path or "/"
        if "minItems" in schema and len(value) < schema["minItems"]:
            errors.append(f"{here}: fewer than {schema['minItems']} items")
        if "maxItems" in schema and len(value) > schema["maxItems"]:
            errors.append(f"{here}: more than {schema['maxItems']} items")
        if schema.get("uniqueItems"):
            for index, item in enumerate(value):
                if any(_json_equal(item, other) for other in value[:index]):
                    errors.append(f"{here}: items are not unique")
                    break
        if "items" in schema:
            for index, item in enumerate(value):
                errors += self._validate(item, schema["items"], base, f"{path}/{index}")
        return errors

    def _validate_combinators(
        self, value: Any, schema: dict[str, Any], base: str, path: str
    ) -> list[str]:
        errors: list[str] = []
        here = path or "/"
        for sub in schema.get("allOf", []):
            errors += self._validate(value, sub, base, path)
        if "anyOf" in schema and not any(
            not self._validate(value, sub, base, path) for sub in schema["anyOf"]
        ):
            errors.append(f"{here}: matches none of anyOf")
        if "oneOf" in schema:
            matches = 0
            for sub in schema["oneOf"]:
                if not self._validate(value, sub, base, path):
                    matches += 1
            if matches != 1:
                errors.append(f"{here}: matches {matches} schemas of oneOf, expected 1")
        if "if" in schema:
            condition = self._validate(value, schema["if"], base, path)
            branch = "else" if condition else "then"
            if branch in schema:
                errors += self._validate(value, schema[branch], base, path)
        return errors


# ---------------------------------------------------------------------------
# Fixture checks
# ---------------------------------------------------------------------------


def _prefix(path: Path) -> str:
    """The part of a fixture file name before the first dot (its type)."""
    return path.name.split(".", 1)[0]


def _json_files(folder: Path) -> list[Path]:
    return sorted(folder.glob("*.json")) if folder.is_dir() else []


class Checker:
    """Runs all checks and collects the problems."""

    def __init__(self, protocol_dir: Path) -> None:
        self.dir = protocol_dir
        self.schema_dir = protocol_dir / "schema"
        self.fixture_dir = protocol_dir / "fixtures"
        self.schemas = SchemaSet(self.schema_dir)
        self.problems: list[str] = list(self.schemas.problems)
        self.checked = 0

    def _uri(self, relative: str) -> str:
        return f"https://github.com/toeck98/QoffeeAutomat/protocol/schema/{relative}"

    def _schema_for(
        self, prefix: str, *, family: str, must_be_valid: bool = True
    ) -> list[str]:
        """Schema URIs a fixture with this prefix must be checked against."""
        if family == "config":
            return [self._uri(f"config/{prefix}.schema.json")]
        if family == "hub":
            if prefix == "message":
                return [self._uri("hub/message.schema.json")]
            uris = [self._uri(f"hub/{prefix}.schema.json")]
            if prefix in HUB_MESSAGES or (
                must_be_valid and prefix in HUB_SCOPED_MESSAGES
            ):
                uris.append(self._uri("hub/message.schema.json"))
            return uris
        uris = [self._uri(f"{prefix}.schema.json")]
        if prefix in MESSAGE_TYPES:
            uris.append(self._uri("message.schema.json"))
        elif prefix == "message":
            uris = [self._uri("message.schema.json")]
        return uris

    def _check_files(self, folder: Path, *, family: str, must_be_valid: bool) -> None:
        known: tuple[str, ...]
        if family == "config":
            known = CONFIG_SCHEMAS
        elif family == "hub":
            known = (*HUB_SCHEMAS, "message")
        else:
            known = (*MESSAGE_TYPES, *OTHER_SCHEMAS, "message")
        for path in _json_files(folder):
            rel = path.relative_to(self.dir)
            if _prefix(path) not in known:
                self.problems.append(f"{rel}: unknown fixture type {_prefix(path)!r}")
                continue
            try:
                instance = load_json(path)
            except json.JSONDecodeError as error:
                self.problems.append(f"{rel}: not valid JSON ({error})")
                continue
            for uri in self._schema_for(
                _prefix(path), family=family, must_be_valid=must_be_valid
            ):
                if uri not in self.schemas.documents:
                    self.problems.append(f"{rel}: schema {uri} does not exist")
                    continue
                errors = self.schemas.validate(instance, uri)
                self.checked += 1
                if must_be_valid and errors:
                    self.problems.append(f"{rel}: invalid against {uri}: {errors[0]}")
                if not must_be_valid and not errors:
                    self.problems.append(f"{rel}: must be rejected by {uri}")

    def _check_coverage(self, folder: Path, names: tuple[str, ...], what: str) -> None:
        present = {_prefix(p) for p in _json_files(folder)}
        for name in names:
            if name not in present:
                self.problems.append(f"no {what} fixture for {name!r}")

    def _check_schema_files(self) -> None:
        for name in (*MESSAGE_TYPES, *OTHER_SCHEMAS, "message"):
            if not (self.schema_dir / f"{name}.schema.json").exists():
                self.problems.append(f"no schema for {name!r}")
        for name in CONFIG_SCHEMAS:
            if not (self.schema_dir / "config" / f"{name}.schema.json").exists():
                self.problems.append(f"no config schema for {name!r}")
        for name in (*HUB_SCHEMAS, "message", "defs"):
            if not (self.schema_dir / "hub" / f"{name}.schema.json").exists():
                self.problems.append(f"no hub schema for {name!r}")

    def check_hub_fixtures(self) -> None:
        """Cross-checks of the hub fixtures that a schema cannot express."""
        hub = self.fixture_dir / "hub"
        expected: dict[tuple[str, str], str] = {}
        for path in _json_files(hub):
            rel = path.relative_to(self.dir)
            prefix = _prefix(path)
            if prefix == "config_documents":
                for document in load_json(path)["documents"]:
                    key = (document["kind"], document["id"])
                    actual = document_hash(document["kind"], document["body"])
                    if document["hash"] != actual:
                        self.problems.append(
                            f"{rel}: hash of {key[0]}/{key[1]} is {document['hash']}, "
                            f"expected {actual} (run with --update-derived)"
                        )
                    expected[key] = actual
            elif prefix == "enrollment_string":
                self._check_enrollment_string(path)
        desired_path = hub / "config_desired.json"
        documents_path = hub / "config_documents.json"
        if not desired_path.exists() or not documents_path.exists():
            return
        full = {
            (document["kind"], document["id"])
            for document in load_json(documents_path)["documents"]
        }
        listed = set()
        for item in load_json(desired_path)["items"]:
            key = (item["kind"], item["id"])
            listed.add(key)
            if key in expected and item["hash"] != expected[key]:
                self.problems.append(
                    f"fixtures/hub/config_desired.json: hash of {key[0]}/{key[1]} is "
                    f"{item['hash']}, expected {expected[key]} "
                    "(run with --update-derived)"
                )
        if listed != full:
            self.problems.append(
                "fixtures/hub/config_desired.json: items must be exactly the documents "
                "of config_documents.json"
            )

    def _check_enrollment_string(self, path: Path) -> None:
        rel = path.relative_to(self.dir)
        payload_path = path.with_name(
            path.name.replace("enrollment_string", "enrollment_payload", 1)
        )
        if not payload_path.exists():
            self.problems.append(f"{rel}: no matching {payload_path.name}")
            return
        text = load_json(path)
        if not isinstance(text, str):
            self.problems.append(f"{rel}: must be a JSON string")
            return
        try:
            decoded = decode_enrollment_string(text)
        except ValueError as error:
            self.problems.append(f"{rel}: {error} (run with --update-derived)")
            return
        if not _json_equal(decoded, load_json(payload_path)):
            self.problems.append(
                f"{rel}: does not decode to {payload_path.name} "
                "(run with --update-derived)"
            )

    def check_revisions(self) -> None:
        manifest_path = self.fixture_dir / "manifest.json"
        if not manifest_path.exists():
            self.problems.append("fixtures/manifest.json is missing")
            return
        manifest = load_json(manifest_path)
        expected = compute_rev(manifest)
        if manifest.get("rev") != expected:
            self.problems.append(
                f"fixtures/manifest.json: rev is {manifest.get('rev')}, "
                f"expected {expected} (run with --update-rev)"
            )
        for path in self._hello_files():
            hello = load_json(path)
            if hello.get("manifest_rev") != expected:
                self.problems.append(
                    f"{path.relative_to(self.dir)}: manifest_rev is "
                    f"{hello.get('manifest_rev')}, expected {expected} "
                    "(run with --update-rev)"
                )

    def _hello_files(self) -> list[Path]:
        folders = (self.fixture_dir, self.fixture_dir / "forward_compat")
        return [p for f in folders for p in _json_files(f) if _prefix(p) == "hello"]

    def check_forbidden_names(self) -> None:
        for path in self.dir.rglob("*"):
            if path.name in FORBIDDEN_FILE_NAMES:
                self.problems.append(f"{path.relative_to(self.dir)}: forbidden (D-034)")

    def run(self) -> list[str]:
        fixtures = self.fixture_dir
        hub = fixtures / "hub"
        self._check_files(fixtures, family="core", must_be_valid=True)
        self._check_files(
            fixtures / "forward_compat", family="core", must_be_valid=True
        )
        self._check_files(fixtures / "invalid", family="core", must_be_valid=False)
        self._check_files(fixtures / "config", family="config", must_be_valid=True)
        self._check_files(
            fixtures / "config" / "invalid", family="config", must_be_valid=False
        )
        self._check_files(hub, family="hub", must_be_valid=True)
        self._check_files(hub / "forward_compat", family="hub", must_be_valid=True)
        self._check_files(hub / "invalid", family="hub", must_be_valid=False)
        self._check_schema_files()
        self._check_coverage(fixtures, (*MESSAGE_TYPES, *OTHER_SCHEMAS), "valid")
        self._check_coverage(fixtures / "config", CONFIG_SCHEMAS, "valid config")
        self._check_coverage(fixtures / "invalid", ("message",), "invalid")
        self._check_coverage(hub, HUB_SCHEMAS, "valid hub")
        self._check_coverage(hub / "invalid", ("message",), "invalid hub")
        self.check_hub_fixtures()
        self.check_revisions()
        self.check_forbidden_names()
        return self.problems


def update_revisions(protocol_dir: Path) -> None:
    """Rewrite rev in the manifest fixture and manifest_rev in the hello fixtures."""
    fixture_dir = protocol_dir / "fixtures"
    manifest_path = fixture_dir / "manifest.json"
    rev = compute_rev(load_json(manifest_path))
    text = manifest_path.read_text(encoding="utf-8")
    manifest_path.write_text(
        REV_IN_MANIFEST.sub(rf"\g<1>{rev}\g<2>", text, count=1), encoding="utf-8"
    )
    for folder in (fixture_dir, fixture_dir / "forward_compat"):
        for path in _json_files(folder):
            if _prefix(path) == "hello":
                text = path.read_text(encoding="utf-8")
                path.write_text(
                    REV_IN_HELLO.sub(rf"\g<1>{rev}\g<2>", text, count=1),
                    encoding="utf-8",
                )
    print(f"rev updated to {rev}")


def _replace_hashes(path: Path, digests: list[str]) -> None:
    """Replace the hash members of a fixture, in order, by the given digests."""
    text = path.read_text(encoding="utf-8")
    if len(HASH_MEMBER.findall(text)) != len(digests):
        msg = f"{path}: expected {len(digests)} hash members"
        raise SystemExit(msg)
    remaining = iter(digests)
    path.write_text(
        HASH_MEMBER.sub(lambda m: f"{m.group(1)}{next(remaining)}{m.group(2)}", text),
        encoding="utf-8",
    )


def update_derived(protocol_dir: Path) -> None:
    """Recompute the hub fixture values that are derived from other content."""
    hub = protocol_dir / "fixtures" / "hub"
    digests: dict[tuple[str, str], str] = {}
    for path in _json_files(hub):
        if _prefix(path) != "config_documents":
            continue
        documents = load_json(path)["documents"]
        computed = [document_hash(d["kind"], d["body"]) for d in documents]
        digests.update(
            {
                (d["kind"], d["id"]): digest
                for d, digest in zip(documents, computed, strict=True)
            }
        )
        _replace_hashes(path, computed)
    desired = hub / "config_desired.json"
    if desired.exists():
        items = load_json(desired)["items"]
        _replace_hashes(desired, [digests[(i["kind"], i["id"])] for i in items])
    for path in _json_files(hub):
        if _prefix(path) != "enrollment_string":
            continue
        payload_path = path.with_name(
            path.name.replace("enrollment_string", "enrollment_payload", 1)
        )
        payload = json.loads(payload_path.read_text(encoding="utf-8"))
        path.write_text(
            json.dumps(encode_enrollment_string(payload)) + "\n", encoding="utf-8"
        )
    print(f"hub fixtures updated ({len(digests)} document hashes)")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", maxsplit=1)[0])
    parser.add_argument("--protocol-dir", type=Path, default=DEFAULT_PROTOCOL_DIR)
    parser.add_argument("--update-rev", action="store_true")
    parser.add_argument("--update-derived", action="store_true")
    args = parser.parse_args(argv)

    if args.update_rev:
        update_revisions(args.protocol_dir)
    if args.update_derived:
        update_derived(args.protocol_dir)
    checker = Checker(args.protocol_dir)
    problems = checker.run()
    if problems:
        print(f"{len(problems)} problem(s) found:", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1
    print(f"protocol OK: {checker.checked} schema checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
