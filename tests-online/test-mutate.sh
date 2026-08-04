#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck disable=SC1091
source "${SCRIPT_DIR}/lib.sh"

setup_online_test_env
require_local_online_profile
load_fixture_config
require_mutate_fixtures
build_cli_once
require_working_token

run_mutate_case \
  "mutate account description" \
  "${TEST_ACCOUNT_ID}" \
  "description" \
  "__nocfo_test__" \
  accounts -b "${TEST_BUSINESS_SLUG}" || true

print_summary_and_exit
