#!/usr/bin/env bash
# Tests scripts/github/setup-repo.sh against a fake `gh` executable (task T009).
#
# The fake keeps a small state directory (labels, milestones, rulesets, Pages, environment
# policies) and logs every call that would change GitHub in mutations.log, so the tests can
# check that a dry run changes nothing, that a second run creates nothing, and how the
# required status checks of the ruleset are kept or cleared.
#
# Usage: bash tests/scripts/test_setup_repo.sh

# The small predicate functions below are only called through `check`, which shellcheck
# cannot see.
# shellcheck disable=SC2317,SC2329

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="$ROOT/scripts/github/setup-repo.sh"
LABELS="$ROOT/.github/labels.yml"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
STATE="$TMP/state"
mkdir -p "$TMP/bin" "$STATE"

# --------------------------------------------------------------------------------------
# The fake gh. It understands exactly the calls that setup-repo.sh makes.
# --------------------------------------------------------------------------------------
cat > "$TMP/bin/gh" <<'FAKE_GH'
#!/usr/bin/env bash
set -euo pipefail
state="${FAKE_GH_STATE:?FAKE_GH_STATE is not set}"
mutation() { printf '%s\n' "$*" >> "$state/mutations.log"; }

cmd="${1:-}"
shift || true

case "$cmd" in
  auth)
    exit 0
    ;;
  repo)
    mutation "gh repo $*"
    exit 0
    ;;
  label)
    shift # "create"
    name="$1"
    shift
    color=""
    desc=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --repo) shift 2 ;;
        --color) color="$2"; shift 2 ;;
        --description) desc="$2"; shift 2 ;;
        *) shift ;;
      esac
    done
    touch "$state/labels.tsv"
    awk -F'\t' -v n="$name" '$1 != n' "$state/labels.tsv" > "$state/labels.tmp"
    printf '%s\t%s\t%s\n' "$name" "$color" "$desc" >> "$state/labels.tmp"
    mv "$state/labels.tmp" "$state/labels.tsv"
    mutation "label create $name"
    exit 0
    ;;
  api) ;;
  *)
    echo "fake gh: unsupported command: $cmd $*" >&2
    exit 99
    ;;
esac

# gh api ...
method="GET"
path=""
input=0
fields=()
while [ $# -gt 0 ]; do
  case "$1" in
    --method) method="$2"; shift 2 ;;
    --input) input=1; shift 2 ;;
    --jq) shift 2 ;;
    -f|-F) fields+=("$2"); shift 2 ;;
    -*) shift ;;
    *) path="$1"; shift ;;
  esac
done
body=""
if [ "$input" -eq 1 ]; then
  body="$(cat)"
fi

field() {
  local f
  for f in "${fields[@]}"; do
    case "$f" in "$1="*) printf '%s' "${f#*=}"; return 0 ;; esac
  done
  return 1
}

touch "$state/rulesets.tsv" "$state/milestones.txt" "$state/labels.tsv"

if printf '%s' "$path" | grep -Eq '^repos/[^/]+/[^/]+$' && [ "$method" = "GET" ]; then
  printf 'main\tpublic\ttrue\ttoeck98\tQoffeeAutomat\n'
  exit 0
fi

case "$method $path" in
  "GET repos/"*"/labels?"*)
    cat "$state/labels.tsv"
    ;;
  "GET repos/"*"/milestones?"*)
    cat "$state/milestones.txt"
    ;;
  "POST repos/"*"/milestones")
    field title >> "$state/milestones.txt"
    printf '\n' >> "$state/milestones.txt"
    mutation "POST $path"
    ;;
  "GET repos/"*"/rulesets?"*)
    cat "$state/rulesets.tsv"
    ;;
  "GET repos/"*"/rulesets/"*)
    id="${path##*/}"
    if [ -f "$state/ruleset-$id.json" ]; then
      grep -o '"context":"[^"]*"' "$state/ruleset-$id.json" | sed -e 's/^"context":"//' -e 's/"$//' || true
    fi
    ;;
  "POST repos/"*"/rulesets")
    id=$(($(wc -l < "$state/rulesets.tsv") + 1))
    name="$(printf '%s' "$body" | sed -n 's/^ *"name": *"\([^"]*\)".*/\1/p' | head -n 1)"
    printf '%s' "$body" > "$state/ruleset-$id.json"
    printf '%s\t%s\n' "$id" "$name" >> "$state/rulesets.tsv"
    mutation "POST $path"
    ;;
  "PUT repos/"*"/rulesets/"*)
    id="${path##*/}"
    printf '%s' "$body" > "$state/ruleset-$id.json"
    mutation "PUT $path"
    ;;
  "GET repos/"*"/pages")
    if [ -f "$state/pages.enabled" ]; then
      echo "workflow"
    else
      # The real gh prints the JSON error body on stdout and exits with 1.
      echo '{"message":"Not Found","status":"404"}'
      exit 1
    fi
    ;;
  "POST repos/"*"/pages")
    touch "$state/pages.enabled"
    mutation "POST $path"
    ;;
  "GET user")
    echo "4242"
    ;;
  "GET repos/"*"/environments/"*"/deployment-branch-policies"*)
    env="${path#*/environments/}"
    env="${env%%/*}"
    if [ -f "$state/policies-$env.tsv" ]; then
      cat "$state/policies-$env.tsv"
    fi
    ;;
  "POST repos/"*"/environments/"*"/deployment-branch-policies")
    env="${path#*/environments/}"
    env="${env%%/*}"
    printf '%s\t%s\n' "$(field type)" "$(field name)" >> "$state/policies-$env.tsv"
    mutation "POST $path"
    ;;
  "GET graphql")
    printf 'Q&A\nIdeas\nShow and tell\n'
    ;;
  GET*)
    echo "fake gh: unsupported read: $method $path" >&2
    exit 99
    ;;
  *)
    mutation "$method $path"
    ;;
esac
FAKE_GH
chmod +x "$TMP/bin/gh"

# --------------------------------------------------------------------------------------
# Test helpers
# --------------------------------------------------------------------------------------
FAILED=0
PASSED=0
OUT=""
RC=0

pass() { PASSED=$((PASSED + 1)); printf 'ok   - %s\n' "$1"; }
fail() { FAILED=$((FAILED + 1)); printf 'FAIL - %s\n' "$1"; }

check() {
  local description="$1"
  shift
  if "$@"; then pass "$description"; else fail "$description"; fi
}

# run_setup ARGS...: runs the script with the fake gh and records output and exit code.
run_setup() {
  : > "$STATE/mutations.log"
  RC=0
  OUT="$(PATH="$TMP/bin:$PATH" FAKE_GH_STATE="$STATE" bash "$SCRIPT" "$@" 2>&1)" || RC=$?
}

mutation_count() { grep -c -E "$1" "$STATE/mutations.log" || true; }
latest_main_ruleset() { cat "$STATE/ruleset-1.json"; }

rc_is() { [ "$RC" -eq "$1" ]; }
out_has() { printf '%s\n' "$OUT" | grep -Fq -- "$1"; }
mutations_empty() { [ ! -s "$STATE/mutations.log" ]; }
count_is() { [ "$(mutation_count "$1")" -eq "$2" ]; }
ruleset_has() { latest_main_ruleset | grep -Fq -- "$1"; }
ruleset_lacks() { ! latest_main_ruleset | grep -Fq -- "$1"; }

# --------------------------------------------------------------------------------------
# 1. Command line interface
# --------------------------------------------------------------------------------------
run_setup --help
check "--help exits with 0 and prints the usage" rc_is 0
check "--help documents --required-check" out_has "--required-check"

run_setup --bogus-option
check "an unknown option exits with 2" rc_is 2

# --------------------------------------------------------------------------------------
# 2. Dry run changes nothing
# --------------------------------------------------------------------------------------
run_setup --dry-run --required-check "dotnet ci passed"
check "dry run exits with 0" rc_is 0
check "dry run says that nothing was changed" out_has "Dry run finished. Nothing was changed."
check "dry run does not call any changing command" mutations_empty
check "dry run leaves the label state empty" test ! -s "$STATE/labels.tsv"

# --------------------------------------------------------------------------------------
# 3. First real run
# --------------------------------------------------------------------------------------
CHECK_A="dotnet ci passed"
CHECK_B="integration ci passed"
run_setup --required-check "$CHECK_A" --required-check "$CHECK_B"
check "first run exits with 0" rc_is 0
check "first run reports success" out_has "Finished."
check "first run creates every label" \
  test "$(wc -l < "$STATE/labels.tsv")" -eq "$(grep -c '^- name:' "$LABELS")"
check "first run creates the ten milestones" \
  test "$(grep -c . "$STATE/milestones.txt")" -eq 10
check "first run creates both rulesets" count_is '^POST repos/.*/rulesets$' 2
check "first run creates the Pages site with a POST" count_is '^POST repos/.*/pages$' 1
check "first run does not PUT to a Pages site that does not exist" \
  count_is '^PUT repos/.*/pages$' 0
check "the main ruleset requires the first check" ruleset_has "\"context\":\"$CHECK_A\""
check "the main ruleset requires the second check" ruleset_has "\"context\":\"$CHECK_B\""
check "the main ruleset allows squash merges only" ruleset_has '"allowed_merge_methods": ["squash"]'
check "no bypass actor without --admin-bypass" ruleset_has '"bypass_actors": []'

# --------------------------------------------------------------------------------------
# 4. A second run creates nothing
# --------------------------------------------------------------------------------------
run_setup --required-check "$CHECK_A" --required-check "$CHECK_B"
check "second run exits with 0" rc_is 0
check "second run creates no label" count_is '^label create' 0
check "second run creates no milestone" count_is '^POST repos/.*/milestones$' 0
check "second run creates no ruleset" count_is '^POST repos/.*/rulesets$' 0
check "second run does not create Pages again" count_is '^POST repos/.*/pages$' 0
check "second run creates no deployment policy" \
  count_is '^POST repos/.*/deployment-branch-policies$' 0
check "second run updates both rulesets in place" count_is '^PUT repos/.*/rulesets/' 2
check "ruleset ids stay stable" test "$(wc -l < "$STATE/rulesets.tsv")" -eq 2

# --------------------------------------------------------------------------------------
# 5. Required checks: kept when the option is omitted, cleared on request
# --------------------------------------------------------------------------------------
run_setup
check "run without the option exits with 0" rc_is 0
check "required checks are kept when the option is omitted (first check)" \
  ruleset_has "\"context\":\"$CHECK_A\""
check "required checks are kept when the option is omitted (second check)" \
  ruleset_has "\"context\":\"$CHECK_B\""

run_setup --required-check "$CHECK_A"
check "a new list replaces the stored checks" ruleset_lacks "\"context\":\"$CHECK_B\""
check "a new list keeps the named check" ruleset_has "\"context\":\"$CHECK_A\""

run_setup --clear-required-checks
check "--clear-required-checks exits with 0" rc_is 0
check "--clear-required-checks removes the status check rule" \
  ruleset_lacks "required_status_checks"

run_setup
check "after clearing, an omitted option does not bring the checks back" \
  ruleset_lacks "required_status_checks"

# --------------------------------------------------------------------------------------
# 6. Options
# --------------------------------------------------------------------------------------
run_setup --admin-bypass --only rulesets
check "--admin-bypass adds the administrator role as bypass actor" \
  ruleset_has '"actor_type":"RepositoryRole"'
check "--only rulesets does not touch labels" count_is '^label create' 0

run_setup --skip rulesets,labels,milestones,environments,pages,releases,security,actions,merge,metadata,discussions
check "--skip for every step changes nothing" mutations_empty

# --------------------------------------------------------------------------------------
# 7. The label parser rejects files that do not follow the restricted format
# --------------------------------------------------------------------------------------
printf '%s\n' '- name: "ok"' '  color: "ffffff"' '  description: "fine"' '  extra: "not allowed"' \
  > "$TMP/bad-labels.yml"
run_setup --only labels --labels-file "$TMP/bad-labels.yml"
check "an unparsable label file makes the script fail" rc_is 1
check "the parse error names the file" out_has "Cannot parse"

# --------------------------------------------------------------------------------------
printf '\n%s passed, %s failed\n' "$PASSED" "$FAILED"
if [ "$FAILED" -gt 0 ]; then
  exit 1
fi
