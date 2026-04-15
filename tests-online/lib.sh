#!/usr/bin/env bash

set -euo pipefail

readonly ONLINE_TEST_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPO_ROOT="$(cd "${ONLINE_TEST_DIR}/.." && pwd)"
readonly ONLINE_PROFILE_NAME="online-test"
readonly ONLINE_CONFIG_DIR="${ONLINE_TEST_DIR}/config"
readonly ONLINE_CONFIG_FILE="${ONLINE_CONFIG_DIR}/config.toml"
readonly ONLINE_CONFIG_EXAMPLE_FILE="${ONLINE_CONFIG_DIR}/config.toml.example"
readonly ONLINE_FIXTURE_FILE="${ONLINE_CONFIG_DIR}/fixture.env"
readonly PLACEHOLDER_TOKEN="replace-me-with-api-tst-token"
readonly PLACEHOLDER_SLUG="replace-me-with-api-tst-business-slug"

PASS_COUNT=0
FAIL_COUNT=0
WORK_DIR=""
TEST_BUSINESS_SLUG=""

setup_online_test_env() {
  WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/nocfo-online-tests.XXXXXX")"
  export WORK_DIR
  export DOTNET_CLI_HOME="${WORK_DIR}/dotnet-cli-home"
  export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export NOCFO_TOOL_CONFIG_HOME="${ONLINE_CONFIG_DIR}"

  mkdir -p "${DOTNET_CLI_HOME}"
}

require_local_online_profile() {
  if [ ! -f "${ONLINE_CONFIG_FILE}" ]; then
    cat >&2 <<EOF
Missing ${ONLINE_CONFIG_FILE}.

Copy ${ONLINE_CONFIG_EXAMPLE_FILE} to ${ONLINE_CONFIG_FILE} and replace the placeholder token.
EOF
    exit 2
  fi

  if grep -Fq "${PLACEHOLDER_TOKEN}" "${ONLINE_CONFIG_FILE}"; then
    cat >&2 <<EOF
${ONLINE_CONFIG_FILE} still contains the placeholder token.

Replace ${PLACEHOLDER_TOKEN} with a real api-tst token for the '${ONLINE_PROFILE_NAME}' profile.
EOF
    exit 2
  fi
}

load_fixture_config() {
  if [ ! -f "${ONLINE_FIXTURE_FILE}" ]; then
    echo "Missing ${ONLINE_FIXTURE_FILE}." >&2
    exit 2
  fi

  set -a
  # shellcheck disable=SC1090
  source "${ONLINE_FIXTURE_FILE}"
  set +a

  if [ -z "${TEST_BUSINESS_SLUG:-}" ] || [ "${TEST_BUSINESS_SLUG}" = "${PLACEHOLDER_SLUG}" ]; then
    cat >&2 <<EOF
${ONLINE_FIXTURE_FILE} must define TEST_BUSINESS_SLUG with a real api-tst business slug.

Current value: ${TEST_BUSINESS_SLUG:-<unset>}
EOF
    exit 2
  fi
}

build_cli_once() {
  local stdout_file="${WORK_DIR}/build.stdout"
  local stderr_file="${WORK_DIR}/build.stderr"
  local -a cmd=(dotnet build tools)
  local exit_code=0

  set +e
  (
    cd "${REPO_ROOT}"
    "${cmd[@]}"
  ) >"${stdout_file}" 2>"${stderr_file}"
  exit_code=$?
  set -e

  if [ "${exit_code}" -ne 0 ]; then
    printf 'FAIL build tools\n'
    printf '  command: dotnet build tools\n'
    printf '  exit code: %s\n' "${exit_code}"
    printf '  stdout: %s\n' "${stdout_file}"
    printf '  stderr: %s\n' "${stderr_file}"
    exit 1
  fi

  printf 'PASS build tools\n'
}

csv_header() {
  python3 - "$1" <<'PY'
import csv
import sys

path = sys.argv[1]

with open(path, newline="", encoding="utf-8") as fh:
    reader = csv.reader(fh)
    try:
        header = next(reader)
    except StopIteration:
        print("", end="")
        sys.exit(0)
    print(",".join(header), end="")
PY
}

assert_csv_rows_have_values() {
  python3 - "$1" "$2" <<'PY'
import csv
import sys

path = sys.argv[1]
required = [item for item in sys.argv[2].split(",") if item]

with open(path, newline="", encoding="utf-8") as fh:
    reader = csv.DictReader(fh)
    rows = list(reader)

if not rows:
    print(f"{path} does not contain any data rows.")
    sys.exit(1)

for index, row in enumerate(rows, start=2):
    missing = [name for name in required if not (row.get(name) or "").strip()]
    if missing:
        print(f"{path} row {index} is missing values for: {', '.join(missing)}")
        sys.exit(1)
PY
}

assert_csv_column_contains() {
  python3 - "$1" "$2" "$3" <<'PY'
import csv
import sys

path, column, expected = sys.argv[1:]

with open(path, newline="", encoding="utf-8") as fh:
    reader = csv.DictReader(fh)
    for row in reader:
        if (row.get(column) or "").strip() == expected:
            sys.exit(0)

print(f"{path} does not contain {column}={expected!r}.")
sys.exit(1)
PY
}

stderr_has_unhandled_exception() {
  local stderr_file="$1"
  grep -Eq 'Unhandled exception|^[[:space:]]+at |^System\.[[:alnum:]_.`]+:|^Microsoft\.[[:alnum:]_.`]+:' "${stderr_file}"
}

run_list_case() {
  local case_name="$1"
  local expected_header="$2"
  local required_columns="$3"
  local slug_to_match="$4"
  shift 4

  local case_slug
  case_slug="$(printf '%s' "${case_name}" | tr ' /' '__')"
  local stdout_file="${WORK_DIR}/${case_slug}.stdout"
  local stderr_file="${WORK_DIR}/${case_slug}.stderr"
  local command_file="${WORK_DIR}/${case_slug}.command"

  local -a cmd=(
    dotnet run --project tools --no-build -- --profile "${ONLINE_PROFILE_NAME}" --verbose "$@"
  )

  printf '%q ' "${cmd[@]}" > "${command_file}"
  printf '\n' >> "${command_file}"

  local exit_code=0
  set +e
  (
    cd "${REPO_ROOT}"
    "${cmd[@]}"
  ) >"${stdout_file}" 2>"${stderr_file}"
  exit_code=$?
  set -e

  local failures=""
  local actual_header=""
  local message=""

  if [ "${exit_code}" -ne 0 ]; then
    failures="${failures}non-zero exit code: ${exit_code}"$'\n'
  fi

  if stderr_has_unhandled_exception "${stderr_file}"; then
    failures="${failures}stderr contains an unhandled exception signature"$'\n'
  fi

  actual_header="$(csv_header "${stdout_file}")"
  if [ "${actual_header}" != "${expected_header}" ]; then
    failures="${failures}unexpected CSV header: got '${actual_header}', expected '${expected_header}'"$'\n'
  fi

  if ! message="$(assert_csv_rows_have_values "${stdout_file}" "${required_columns}" 2>&1)"; then
    failures="${failures}${message}"$'\n'
  fi

  if [ -n "${slug_to_match}" ] && ! message="$(assert_csv_column_contains "${stdout_file}" "slug" "${slug_to_match}" 2>&1)"; then
    failures="${failures}${message}"$'\n'
  fi

  if [ -n "${failures}" ]; then
    FAIL_COUNT=$((FAIL_COUNT + 1))
    printf 'FAIL %s\n' "${case_name}"
    printf '  command: %s\n' "$(cat "${command_file}")"
    printf '  exit code: %s\n' "${exit_code}"
    printf '  stdout: %s\n' "${stdout_file}"
    printf '  stderr: %s\n' "${stderr_file}"
    printf '  reasons:\n'
    while IFS= read -r line; do
      [ -n "${line}" ] && printf '    - %s\n' "${line}"
    done <<< "${failures}"
    return 1
  fi

  PASS_COUNT=$((PASS_COUNT + 1))
  printf 'PASS %s\n' "${case_name}"
  return 0
}

print_summary_and_exit() {
  printf '\nSummary: %s passed, %s failed\n' "${PASS_COUNT}" "${FAIL_COUNT}"
  printf 'Artifacts: %s\n' "${WORK_DIR}"

  if [ "${FAIL_COUNT}" -ne 0 ]; then
    exit 1
  fi
}
