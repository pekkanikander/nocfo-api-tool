#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck disable=SC1091
source "${SCRIPT_DIR}/lib.sh"

setup_online_test_env
require_local_online_profile
load_fixture_config
require_ledger_fixtures
build_cli_once
require_working_token

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

run_list_case \
  "balance ledger" \
  "account_number,account_name,date,document_number,description,debet,credit,amount,balance" \
  "account_number,account_name,date,debet,credit,amount,balance" \
  "" \
  balance -b "${TEST_BUSINESS_SLUG}" \
    --from "${TEST_LEDGER_FROM}" --to "${TEST_LEDGER_TO}" -a "${TEST_LEDGER_ACCOUNT}" || true

run_list_case \
  "balance daily" \
  "date,balance" \
  "date,balance" \
  "" \
  balance -b "${TEST_BUSINESS_SLUG}" \
    --from "${TEST_LEDGER_FROM}" --to "${TEST_LEDGER_TO}" -a "${TEST_LEDGER_ACCOUNT}" --daily || true

# Reconciling the ledger against the daily balances the tool itself just wrote must
# find no differing day: one would exit EX_DATAERR and fail the case. The csv: path
# is where run_list_case put the previous case's stdout.
run_list_case \
  "reconcile ledger against own daily output" \
  "date,left_balance,right_balance,difference,status" \
  "date,left_balance,right_balance,difference,status" \
  "" \
  reconcile -b "${TEST_BUSINESS_SLUG}" \
    --from "${TEST_LEDGER_FROM}" --to "${TEST_LEDGER_TO}" -a "${TEST_LEDGER_ACCOUNT}" \
    --left ledger --right "csv:${WORK_DIR}/balance_daily.stdout" || true

print_summary_and_exit
