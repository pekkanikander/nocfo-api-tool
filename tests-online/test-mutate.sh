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

print_summary_and_exit
