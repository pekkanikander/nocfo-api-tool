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

# `number` rather than `description`: it is the only always-populated field that
# PatchedAccountRequest accepts, and an empty original could not be restored.
run_mutate_case \
  "mutate account number" \
  "${TEST_ACCOUNT_ID}" \
  "number" \
  "9999" \
  accounts -b "${TEST_BUSINESS_SLUG}" || true

print_summary_and_exit
