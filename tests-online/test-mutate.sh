#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck disable=SC1091
source "${SCRIPT_DIR}/lib.sh"

setup_online_test_env
require_local_online_profile
load_fixture_config
require_mutate_fixtures
require_cashbook_fixtures
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

# The tag and the draft document are created for the run and deleted again, so
# they need no fixture. The tag's name doubles as the document's description,
# by which run_create_case finds the document.
TEST_TAG_NAME="online-test-$(date +%s)"

if run_create_case \
  "create tag" \
  "name,description" \
  "${TEST_TAG_NAME},online test" \
  tags -b "${TEST_BUSINESS_SLUG}"; then
  TEST_TAG_ID="${CREATED_ID}"

  run_mutate_case \
    "mutate tag description" \
    "${TEST_TAG_ID}" \
    "description" \
    "online test changed" \
    tags -b "${TEST_BUSINESS_SLUG}" || true

  if run_create_case \
    "create document with tag" \
    "description,date,is_draft,tag_ids" \
    "${TEST_TAG_NAME},$(date +%F),true,${TEST_TAG_ID}" \
    documents -b "${TEST_BUSINESS_SLUG}"; then
    TEST_DOCUMENT_ID="${CREATED_ID}"

    # Clearing the tag and restoring it covers both directions of a tag_ids update.
    run_mutate_case \
      "mutate document tag_ids" \
      "${TEST_DOCUMENT_ID}" \
      "tag_ids" \
      "" \
      documents -b "${TEST_BUSINESS_SLUG}" || true

    run_delete_case \
      "delete document" \
      "${TEST_DOCUMENT_ID}" \
      documents -b "${TEST_BUSINESS_SLUG}" || true
  fi

  run_delete_case \
    "delete tag" \
    "${TEST_TAG_ID}" \
    tags -b "${TEST_BUSINESS_SLUG}" || true
fi

# cashbook record posts the exact-multi fixture against the fixture accounts: three
# drafts, each with its listing attached. A second run must create nothing (P5).
# The drafts are found by the marker and deleted with their listings.
CASHBOOK_DIR="${WORK_DIR}/cashbook"
CASHBOOK_MARKER="[online-test]"
mkdir -p "${CASHBOOK_DIR}"
cp "${REPO_ROOT}/tests/fixtures/cashbook/exact-multi/kassakirja.csv" \
   "${REPO_ROOT}/tests/fixtures/cashbook/exact-multi/mobilepay.csv" "${CASHBOOK_DIR}/"
cat > "${CASHBOOK_DIR}/rules.toml" <<EOF
[accounts]
cash         = { number = "${TEST_CASHBOOK_CASH}", name = "Kassa" }
mobilepay    = { number = "${TEST_CASHBOOK_MOBILEPAY}", name = "MobilePay" }
fees         = { number = "${TEST_CASHBOOK_FEES}", name = "Palvelumaksut" }
unclassified = { number = "${TEST_CASHBOOK_UNCLASSIFIED}", name = "Selvittelytili" }

[mobilepay]
payment_in = ["Maksa Lippaaseen"]
fee        = ["Palvelumaksu"]
expense    = []
payout     = []

[matching]
days_before = 1
days_after  = 3
max_subset  = 8
tolerance   = 0.50
weight_unmatched     = 100
weight_near_miss     = 10
weight_date_distance = 1
weight_subset_size   = 0.1
weight_coherence     = -5

[classification]
majority_min    = 3
same_amount_min = 3

[[classification.income]]
account  = "${TEST_CASHBOOK_INCOME}"
name     = "Tanssit"
keywords = ["tanssit"]

[coalesce.cash]
max_days = 3
max_gap  = 1

[coalesce.mobilepay]
max_days = 4
max_gap  = 1

[coalesce.fees]
max_days = 7
max_gap  = 7

[posting]
marker = "${CASHBOOK_MARKER}"
EOF

readonly -a CASHBOOK_RECORD=(
  cashbook record -b "${TEST_BUSINESS_SLUG}"
  -k "${CASHBOOK_DIR}/kassakirja.csv" -m "${CASHBOOK_DIR}/mobilepay.csv" -r "${CASHBOOK_DIR}/rules.toml"
)

if run_command_case "cashbook record" 'Created document' '' "${CASHBOOK_RECORD[@]}"; then
  run_command_case "cashbook record again creates nothing" '' 'Created document|unclaimed draft' \
    "${CASHBOOK_RECORD[@]}" || true
fi

if run_command_case "list recorded documents" '' '' \
     list documents -b "${TEST_BUSINESS_SLUG}" --fields "id,description,attachment_ids"; then
  while IFS=, read -r document_id attachment_ids; do
    run_delete_case "delete recorded document ${document_id}" "${document_id}" \
      documents -b "${TEST_BUSINESS_SLUG}" || true
    for file_id in ${attachment_ids//;/ }; do
      run_delete_case "delete listing ${file_id}" "${file_id}" files -b "${TEST_BUSINESS_SLUG}" || true
    done
  done < <(csv_rows_with_prefix "${CASE_STDOUT}" description "${CASHBOOK_MARKER}" "id,attachment_ids")
fi

print_summary_and_exit
