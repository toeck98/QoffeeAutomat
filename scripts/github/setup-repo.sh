#!/usr/bin/env bash
# QoffeeAutomat - GitHub repository setup (PLAN.md S12, task T009, open item O-01).
#
# Applies the repository settings that cannot be stored as files in the repository.
# The script is idempotent: running it again changes nothing that is already set.
#
# Requirements: GitHub CLI (gh) 2.40 or newer, logged in as an administrator of the
# repository (gh auth login). Works in Git Bash on Windows, macOS and Linux.
#
# Usage:
#   bash scripts/github/setup-repo.sh --dry-run        show what would be changed
#   bash scripts/github/setup-repo.sh                  apply everything
#   bash scripts/github/setup-repo.sh --required-check "build (ubuntu-latest)" \
#                                     --required-check "build (windows-latest)"
#
# Options:
#   --repo OWNER/NAME          repository (default: toeck98/QoffeeAutomat)
#   --dry-run                  print the changes instead of applying them
#   --required-check NAME      status check that must pass before merging into the
#                              default branch; repeatable. Without this option the
#                              checks already stored in the ruleset are kept.
#   --clear-required-checks    remove all required status checks from the ruleset
#   --admin-bypass             let repository administrators bypass both rulesets
#                              (pass it on every run, otherwise the bypass is removed)
#   --labels-file PATH         label definitions (default: .github/labels.yml)
#   --only STEP[,STEP...]      run only these steps
#   --skip STEP[,STEP...]      skip these steps
#   -h, --help                 show this help
#
# Steps: metadata merge security actions labels milestones discussions rulesets
#        releases pages environments

set -euo pipefail

REPO="toeck98/QoffeeAutomat"
DRY_RUN=0
ADMIN_BYPASS=0
CHECKS=()
CHECKS_SET=0
ONLY=""
SKIP=""
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LABELS_FILE=""

DESCRIPTION="PC agent and Home Assistant integration: monitor a Windows PC and run your own commands and PowerShell scripts from Home Assistant."
TOPICS="home-assistant,hacs,integration,windows,dotnet,powershell,automation"
MAIN_RULESET="main protection"
TAG_RULESET="release tags"

# One line per milestone: title|description (keep in step with docs/plan/tasks.yaml).
MILESTONES='M0 Foundation|Clean repository, tooling, CI, agent instructions, GitHub configuration.
M1 Protocol and contracts|QoffeeLink schemas, fixtures, C# and Python models, config document models.
M2 Agent core|Cross-platform agent with simulated platform: engines, link server, management API, auth.
M3 Windows platform|Service, SessionHost, real metrics, built-in commands, prototype features as templates.
M4 Web UI|Blazor UI for dashboard, commands, editor, sensors, pairing, settings.
M5 Home Assistant integration|Config flow, dynamic entities, actions, diagnostics, end-to-end test.
M6 Release 1.0|Installer, release automation, signing, documentation, HACS readiness, security review.
M7 Quality of life (1.x)|Templates, notifications, Wake-on-LAN, updates, maintenance mode and more.
M8 Linux support|Linux platform, systemd, packaging.
M9 Central hub|Hub with enrollment, configuration roll-out, metric store, tunnel, fleet link.'

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --repo) REPO="${2:?--repo needs a value}"; shift 2 ;;
    --dry-run) DRY_RUN=1; shift ;;
    --required-check) CHECKS+=("${2:?--required-check needs a value}"); CHECKS_SET=1; shift 2 ;;
    --clear-required-checks) CHECKS=(); CHECKS_SET=1; shift ;;
    --admin-bypass) ADMIN_BYPASS=1; shift ;;
    --labels-file) LABELS_FILE="${2:?--labels-file needs a value}"; shift 2 ;;
    --only) ONLY="${2:?--only needs a value}"; shift 2 ;;
    --skip) SKIP="${2:?--skip needs a value}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

FAILURES=0
CHANGES=0

info() { printf '    %s\n' "$*"; }
warn() { printf '  ! %s\n' "$*" >&2; }
step() { printf '\n== %s\n' "$*"; }

# want STEP: true when the step is selected by --only / --skip.
want() {
  case ",$SKIP," in *",$1,"*) return 1 ;; esac
  [ -z "$ONLY" ] && return 0
  case ",$ONLY," in *",$1,"*) return 0 ;; esac
  return 1
}

# mutate "description" command...: runs a changing command, or prints it in dry-run mode.
mutate() {
  local what="$1"; shift
  if [ "$DRY_RUN" -eq 1 ]; then
    info "[dry-run] $what"
    return 0
  fi
  local out
  if out="$("$@" 2>&1)"; then
    info "ok: $what"
    CHANGES=$((CHANGES + 1))
  else
    warn "FAILED: $what"
    printf '%s\n' "$out" | sed 's/^/      /' >&2
    FAILURES=$((FAILURES + 1))
    return 0
  fi
}

# send_json METHOD PATH JSON: gh api call with a JSON body from stdin.
send_json() {
  printf '%s' "$3" | gh api --method "$1" "$2" --input - >/dev/null
}

# mutate_json "description" METHOD PATH JSON
mutate_json() {
  if [ "$DRY_RUN" -eq 1 ]; then
    info "[dry-run] $1"
    printf '%s\n' "$4" | sed 's/^/        /'
    return 0
  fi
  mutate "$1" send_json "$2" "$3" "$4"
}

# has_line TEXT LINE: true when TEXT contains exactly this line.
has_line() { grep -Fxq -- "$2" <<<"$1"; }

json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }

# ----------------------------------------------------------------------------
# Preflight
# ----------------------------------------------------------------------------
command -v gh >/dev/null 2>&1 || { echo "GitHub CLI (gh) not found. Install it from https://cli.github.com/" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Not logged in. Run: gh auth login" >&2; exit 1; }

REPO_INFO="$(gh api "repos/$REPO" --jq '[.default_branch, .visibility, (.permissions.admin | tostring), .owner.login, .name] | @tsv')" \
  || { echo "Cannot read repository $REPO." >&2; exit 1; }
IFS=$'\t' read -r DEFAULT_BRANCH VISIBILITY IS_ADMIN OWNER NAME <<EOF
$REPO_INFO
EOF
NAME="${NAME%$'\r'}"

if [ "$IS_ADMIN" != "true" ]; then
  echo "The logged-in account is not an administrator of $REPO." >&2
  exit 1
fi

if [ -z "$LABELS_FILE" ]; then
  if [ -f "$SCRIPT_DIR/../../.github/labels.yml" ]; then
    LABELS_FILE="$SCRIPT_DIR/../../.github/labels.yml"
  elif [ -f "$SCRIPT_DIR/labels.yml" ]; then
    LABELS_FILE="$SCRIPT_DIR/labels.yml"
  fi
fi

echo "Repository:     $REPO ($VISIBILITY)"
echo "Default branch: $DEFAULT_BRANCH"
if [ "$DRY_RUN" -eq 1 ]; then
  echo "Mode:           dry run, nothing is changed"
fi
if [ "$VISIBILITY" != "public" ]; then
  warn "The repository is not public. HACS needs a public repository, and some security features are only free for public repositories."
fi

# ----------------------------------------------------------------------------
if want metadata; then
  step "Description and topics"
  mutate "set description and topics ($TOPICS)" \
    gh repo edit "$REPO" --description "$DESCRIPTION" --add-topic "$TOPICS"
fi

# ----------------------------------------------------------------------------
if want merge; then
  step "Merge settings and features"
  mutate "squash merge only, delete merged branches, allow auto-merge, issues on, wiki off" \
    gh api --method PATCH "repos/$REPO" --silent \
      -F allow_squash_merge=true -F allow_merge_commit=false -F allow_rebase_merge=false \
      -F delete_branch_on_merge=true -F allow_auto_merge=true -F allow_update_branch=true \
      -f squash_merge_commit_title=PR_TITLE -f squash_merge_commit_message=PR_BODY \
      -F has_issues=true -F has_projects=true -F has_wiki=false
fi

# ----------------------------------------------------------------------------
if want security; then
  step "Security features"
  mutate_json "enable secret scanning with push protection" PATCH "repos/$REPO" \
    '{"security_and_analysis":{"secret_scanning":{"status":"enabled"},"secret_scanning_push_protection":{"status":"enabled"}}}'
  mutate "enable Dependabot alerts" gh api --method PUT "repos/$REPO/vulnerability-alerts" --silent
  mutate "enable Dependabot security updates" gh api --method PUT "repos/$REPO/automated-security-fixes" --silent
  mutate "enable private vulnerability reporting" gh api --method PUT "repos/$REPO/private-vulnerability-reporting" --silent
fi

# ----------------------------------------------------------------------------
if want actions; then
  step "GitHub Actions defaults"
  # Read-only token by default; workflows request what they need. Creating pull
  # requests must be allowed for release-please.
  mutate_json "workflow token read-only by default, workflows may create pull requests" \
    PUT "repos/$REPO/actions/permissions/workflow" \
    '{"default_workflow_permissions":"read","can_approve_pull_request_reviews":true}'
fi

# ----------------------------------------------------------------------------
if want labels; then
  step "Labels"
  if [ -z "$LABELS_FILE" ] || [ ! -f "$LABELS_FILE" ]; then
    warn "Label file not found (expected .github/labels.yml, or labels.yml next to this script). Use --labels-file."
    FAILURES=$((FAILURES + 1))
  else
    # labels.yml is a flat list with exactly the keys name, color, description
    # per entry, values in double quotes. Anything else is reported as an error.
    PARSED="$(awk '
      function val(s) { sub(/^[^:]*:[ \t]*/, "", s); sub(/^"/, "", s); sub(/"[ \t]*$/, "", s); return s }
      { sub(/\r$/, "") }
      /^[ \t]*#/ || /^[ \t]*$/ { next }
      /^- name:/ { if (name != "") print name "\t" color "\t" desc; line = $0; sub(/^- /, "", line); name = val(line); color = ""; desc = ""; next }
      /^  color:/ { color = val($0); next }
      /^  description:/ { desc = val($0); next }
      { print "ERROR\tline " NR "\t" $0 }
      END { if (name != "") print name "\t" color "\t" desc }
    ' "$LABELS_FILE")"
    if grep -q '^ERROR' <<<"$PARSED"; then
      warn "Cannot parse $LABELS_FILE:"
      grep '^ERROR' <<<"$PARSED" | sed 's/^/      /' >&2
      FAILURES=$((FAILURES + 1))
    else
      EXISTING_LABELS="$(gh api "repos/$REPO/labels?per_page=100" --paginate --jq '.[] | [.name, .color, (.description // "")] | @tsv' | tr -d '\r')"
      COUNT=0
      while IFS=$'\t' read -r L_NAME L_COLOR L_DESC; do
        if [ -z "$L_NAME" ]; then
          continue
        fi
        COUNT=$((COUNT + 1))
        L_COLOR="$(printf '%s' "$L_COLOR" | tr 'A-F' 'a-f')"
        if has_line "$EXISTING_LABELS" "$(printf '%s\t%s\t%s' "$L_NAME" "$L_COLOR" "$L_DESC")"; then
          continue
        fi
        mutate "create or update label '$L_NAME'" \
          gh label create "$L_NAME" --repo "$REPO" --color "$L_COLOR" --description "$L_DESC" --force
      done <<EOF
$PARSED
EOF
      info "$COUNT labels defined in $(basename "$LABELS_FILE"); labels not listed there are left untouched"
    fi
  fi
fi

# ----------------------------------------------------------------------------
if want milestones; then
  step "Milestones"
  EXISTING_MS="$(gh api "repos/$REPO/milestones?state=all&per_page=100" --jq '.[].title' | tr -d '\r')"
  while IFS='|' read -r M_TITLE M_DESC; do
    if [ -z "$M_TITLE" ] || has_line "$EXISTING_MS" "$M_TITLE"; then
      continue
    fi
    mutate "create milestone '$M_TITLE'" \
      gh api --method POST "repos/$REPO/milestones" --silent -f title="$M_TITLE" -f description="$M_DESC"
  done <<EOF
$MILESTONES
EOF
  info "existing milestones are left untouched"
fi

# ----------------------------------------------------------------------------
if want discussions; then
  step "Discussions"
  mutate "enable Discussions" gh repo edit "$REPO" --enable-discussions
  # Categories cannot be created through the API. GitHub creates Q&A, Ideas and
  # Show and tell by default; this only checks that they are there.
  if [ "$DRY_RUN" -eq 0 ]; then
    # shellcheck disable=SC2016  # GraphQL variables, not shell variables
    CATS="$(gh api graphql -f owner="$OWNER" -f name="$NAME" \
      -f query='query($owner:String!,$name:String!){repository(owner:$owner,name:$name){discussionCategories(first:50){nodes{name}}}}' \
      --jq '.data.repository.discussionCategories.nodes[].name' 2>/dev/null | tr -d '\r' || true)"
    for CAT in "Q&A" "Ideas" "Show and tell"; do
      if ! has_line "$CATS" "$CAT"; then
        warn "Discussion category '$CAT' is missing. Add it under Discussions > Categories (cannot be done by script)."
      fi
    done
  fi
fi

# ----------------------------------------------------------------------------
if want rulesets; then
  step "Rulesets"
  RULESETS="$(gh api "repos/$REPO/rulesets?per_page=100" --jq '.[] | [(.id | tostring), .name] | @tsv' | tr -d '\r')"
  ruleset_id() { printf '%s\n' "$RULESETS" | awk -F'\t' -v n="$1" '$2 == n { print $1; exit }'; }

  if [ "$ADMIN_BYPASS" -eq 1 ]; then
    # Repository role 5 is the built-in administrator role.
    BYPASS='[{"actor_id":5,"actor_type":"RepositoryRole","bypass_mode":"always"}]'
  else
    BYPASS='[]'
  fi

  MAIN_ID="$(ruleset_id "$MAIN_RULESET")"

  # Required status checks: from the command line, otherwise keep what is stored.
  if [ "$CHECKS_SET" -eq 0 ] && [ -n "$MAIN_ID" ]; then
    while IFS= read -r C; do
      if [ -n "$C" ]; then
        CHECKS+=("$C")
      fi
    done <<EOF
$(gh api "repos/$REPO/rulesets/$MAIN_ID" --jq '.rules[] | select(.type == "required_status_checks") | .parameters.required_status_checks[].context' | tr -d '\r')
EOF
  fi

  CHECK_RULE=""
  if [ "${#CHECKS[@]}" -gt 0 ]; then
    ITEMS=""
    for C in "${CHECKS[@]}"; do
      ITEMS="$ITEMS${ITEMS:+,}{\"context\":\"$(json_escape "$C")\"}"
    done
    CHECK_RULE=",{\"type\":\"required_status_checks\",\"parameters\":{\"strict_required_status_checks_policy\":false,\"required_status_checks\":[$ITEMS]}}"
    info "required status checks: ${CHECKS[*]}"
  else
    info "no required status checks yet; add them later with --required-check once the CI workflows exist"
  fi

  MAIN_JSON="{
  \"name\": \"$MAIN_RULESET\",
  \"target\": \"branch\",
  \"enforcement\": \"active\",
  \"bypass_actors\": $BYPASS,
  \"conditions\": {\"ref_name\": {\"include\": [\"~DEFAULT_BRANCH\"], \"exclude\": []}},
  \"rules\": [
    {\"type\": \"deletion\"},
    {\"type\": \"non_fast_forward\"},
    {\"type\": \"required_linear_history\"},
    {\"type\": \"pull_request\", \"parameters\": {
      \"required_approving_review_count\": 0,
      \"dismiss_stale_reviews_on_push\": false,
      \"require_code_owner_review\": false,
      \"require_last_push_approval\": false,
      \"required_review_thread_resolution\": true,
      \"allowed_merge_methods\": [\"squash\"]}}$CHECK_RULE
  ]
}"
  if [ -n "$MAIN_ID" ]; then
    mutate_json "update ruleset '$MAIN_RULESET' (pull request required, squash only, linear history, no force push, no deletion)" \
      PUT "repos/$REPO/rulesets/$MAIN_ID" "$MAIN_JSON"
  else
    mutate_json "create ruleset '$MAIN_RULESET' (pull request required, squash only, linear history, no force push, no deletion)" \
      POST "repos/$REPO/rulesets" "$MAIN_JSON"
  fi

  # Version tags can be created but never moved or deleted.
  TAG_JSON="{
  \"name\": \"$TAG_RULESET\",
  \"target\": \"tag\",
  \"enforcement\": \"active\",
  \"bypass_actors\": $BYPASS,
  \"conditions\": {\"ref_name\": {\"include\": [\"refs/tags/v*\"], \"exclude\": []}},
  \"rules\": [
    {\"type\": \"deletion\"},
    {\"type\": \"non_fast_forward\"}
  ]
}"
  TAG_ID="$(ruleset_id "$TAG_RULESET")"
  if [ -n "$TAG_ID" ]; then
    mutate_json "update ruleset '$TAG_RULESET' (v* tags cannot be moved or deleted)" PUT "repos/$REPO/rulesets/$TAG_ID" "$TAG_JSON"
  else
    mutate_json "create ruleset '$TAG_RULESET' (v* tags cannot be moved or deleted)" POST "repos/$REPO/rulesets" "$TAG_JSON"
  fi
fi

# ----------------------------------------------------------------------------
if want releases; then
  step "Releases"
  # With immutable releases, assets must be attached while the release is still a
  # draft (see PLAN.md S13).
  mutate "enable immutable releases" gh api --method PUT "repos/$REPO/immutable-releases" --silent
fi

# ----------------------------------------------------------------------------
if want pages; then
  step "GitHub Pages"
  PAGES_TYPE="$(gh api "repos/$REPO/pages" --jq '.build_type // "legacy"' 2>/dev/null | tr -d '\r' || true)"
  if [ -z "$PAGES_TYPE" ]; then
    mutate_json "enable Pages with deployment from GitHub Actions" POST "repos/$REPO/pages" '{"build_type":"workflow"}'
  elif [ "$PAGES_TYPE" != "workflow" ]; then
    mutate_json "switch Pages to deployment from GitHub Actions" PUT "repos/$REPO/pages" '{"build_type":"workflow"}'
  else
    info "already enabled with deployment from GitHub Actions"
  fi
fi

# ----------------------------------------------------------------------------
if want environments; then
  step "Environments"
  USER_ID="$(gh api user --jq '.id' | tr -d '\r')"

  # ensure_policy ENVIRONMENT TYPE NAME: allow deployments from a branch or tag pattern.
  ensure_policy() {
    local existing
    existing="$(gh api "repos/$REPO/environments/$1/deployment-branch-policies?per_page=100" \
      --jq '.branch_policies[] | [(.type // "branch"), .name] | @tsv' 2>/dev/null | tr -d '\r' || true)"
    if has_line "$existing" "$(printf '%s\t%s' "$2" "$3")"; then
      return 0
    fi
    mutate "environment '$1': allow deployments from $2 '$3'" \
      gh api --method POST "repos/$REPO/environments/$1/deployment-branch-policies" --silent -f name="$3" -f type="$2"
  }

  # release: holds the signing secrets; every run needs the approval of the owner.
  mutate_json "create or update environment 'release' (approval by the logged-in user required)" \
    PUT "repos/$REPO/environments/release" \
    "{\"wait_timer\":0,\"prevent_self_review\":false,\"reviewers\":[{\"type\":\"User\",\"id\":$USER_ID}],\"deployment_branch_policy\":{\"protected_branches\":false,\"custom_branch_policies\":true}}"
  ensure_policy release tag 'v*'
  ensure_policy release branch "$DEFAULT_BRANCH"

  mutate_json "create or update environment 'github-pages'" \
    PUT "repos/$REPO/environments/github-pages" \
    '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}'
  ensure_policy github-pages branch "$DEFAULT_BRANCH"
fi

# ----------------------------------------------------------------------------
echo
if [ "$DRY_RUN" -eq 1 ]; then
  echo "Dry run finished. Nothing was changed."
elif [ "$FAILURES" -gt 0 ]; then
  echo "Finished with $FAILURES failed step(s); $CHANGES change(s) applied. See the messages above."
  exit 1
else
  echo "Finished. $CHANGES setting(s) applied or confirmed."
fi
