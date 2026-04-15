#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck disable=SC1091
source "${SCRIPT_DIR}/lib.sh"

setup_online_test_env
require_local_online_profile
load_fixture_config
build_cli_once

run_list_case \
  "list businesses" \
  "id,name,slug" \
  "id,name,slug" \
  "${TEST_BUSINESS_SLUG}" \
  list businesses --fields "id,name,slug" || true

run_list_case \
  "list accounts" \
  "id,number,name,type" \
  "id,number,name" \
  "" \
  list accounts -b "${TEST_BUSINESS_SLUG}" --fields "id,number,name,type" || true

run_list_case \
  "list contacts" \
  "id,name" \
  "id,name" \
  "" \
  list contacts -b "${TEST_BUSINESS_SLUG}" --fields "id,name" || true

run_list_case \
  "list documents" \
  "id,number,date,balance" \
  "id,date" \
  "" \
  list documents -b "${TEST_BUSINESS_SLUG}" --fields "id,number,date,balance" || true

print_summary_and_exit
