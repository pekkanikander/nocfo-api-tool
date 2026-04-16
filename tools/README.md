# `nocfo` CLI (tools/)

Thin Argu-based CLI built on top of the shared `hawaii-client` library.

Treat this as the user-facing path for listing businesses/accounts/contacts/documents,
updating accounts and contacts from CSV, deleting accounts/contacts/documents,
mapping account IDs between environments, and creating minimal documents from CSV.

## Quick start

```bash
dotnet build
export NOCFO_TARGET_TOKEN="paste-your-token"        # For tst environment
export NOCFO_SOURCE_TOKEN="paste-your-prod-token"   # needed for `map accounts`

dotnet run --project tools -- list businesses \
  --fields "id,name,slug" > businesses.csv

dotnet run --project tools -- list accounts \
  -b <business-id> --fields id number name type > accounts.csv

dotnet run --project tools -- list contacts \
  -b <business-id> --fields "id,name,invoicing_email,customer_id" > contacts.csv

dotnet run --project tools -- list documents \
  -b <business-id> --fields "id,number,date,balance" > documents.csv

# edit accounts.csv keeping `id`
dotnet run --project tools -- update accounts \
  -b <business-id> --fields "id,number,name" < accounts.csv

# edit contacts.csv keeping `id`
dotnet run --project tools -- update contacts \
  -b <business-id> --fields "id,name,invoicing_email,notes" < contacts.csv

# map account IDs across environments by account number
dotnet run --project tools -- map accounts \
  -b <business-id> > csv/account-id-map.csv

# create documents in target environment from minimal CSV
dotnet run --project tools -- create documents \
  -b <target-business-id> \
  --account-id-map csv/account-id-map.csv \
  < csv/documents-create.csv
```

If a local `.env` exists, you may `source .env` first and skip the manual exports. That file is local-only and is not committed to GitHub.

## Prebuilt releases

GitHub Actions publishes self-contained release archives automatically when a tag
matching `v*` is pushed. Current release targets:

- `osx-arm64`
- `linux-x64`
- `win-x64`

Assets are published as `nocfo-<tag>-<rid>.tar.gz` for Unix targets and
`nocfo-<tag>-<rid>.zip` for Windows. Each archive contains the full publish
directory for that RID.

Requirements:

- .NET 10 SDK
- `NOCFO_TARGET_TOKEN` (preferred) or fallback `NOCFO_TOKEN` (required)
- `NOCFO_TARGET_BASE_URL` (optional), fallback `NOCFO_BASE_URL`, default `https://api-tst.nocfo.io`
- `NOCFO_SOURCE_TOKEN` (required for `map accounts`)
- `NOCFO_SOURCE_BASE_URL` (optional, default `https://api-prd.nocfo.io`)
- Build artifacts: run `dotnet build` from the repo root (uses `nocfo.slnx`)

## Command surface

Read-only commands:

- `list businesses`
- `list accounts`
- `list contacts`
- `list documents`
- `map accounts`

Mutating commands:

- `update businesses`
- `update accounts`
- `update contacts`
- `delete accounts`
- `delete contacts`
- `delete documents`
- `create businesses`
- `create accounts`
- `create contacts`
- `create documents`

| Command | Description | Notes |
| --- | --- | --- |
| `list businesses [--fields …]` | Streams every business the token can access and writes CSV | Default columns are the DTO fields; use `--fields` to select a subset |
| `list accounts -b <id> [--fields …]` | Resolves the business (Y-tunnus or VAT code), streams accounts, hydrates them, writes CSV | Rows are currently emitted in API order |
| `list contacts -b <id> [--fields …]` | Resolves the business (Y-tunnus or VAT code), streams contacts, hydrates them, writes CSV | Contacts are emitted from the `/contacts/` endpoint |
| `list documents -b <id> [--fields …]` | Resolves the business (Y-tunnus or VAT code), streams documents, hydrates them, writes CSV | Document mutation commands are separate (`delete documents`, minimal `create documents`) |
| `update businesses [--fields …]` | Reads CSV from stdin (or `--in`), fetches each current business by `slug`, and emits PATCH requests for changed fields only | CSV **must** include `slug`; rows are processed in CSV order |
| `update accounts -b <id> [--fields …]` | Reads CSV from stdin (or `--in`), fetches each current account by `id`, and emits PATCH requests for changed fields only | CSV **must** include `id`; rows are processed in CSV order and may repeat an `id` |
| `update contacts -b <id> [--fields …]` | Reads CSV from stdin (or `--in`), fetches each current contact by `id`, and emits PATCH requests for changed fields only | CSV **must** include `id`; rows are processed in CSV order and may repeat an `id` |
| `delete accounts -b <id>` | Reads a CSV containing `id` values and issues DELETE calls sequentially | Extra columns are ignored |
| `delete contacts -b <id>` | Reads a CSV containing `id` values and issues DELETE calls sequentially | Extra columns are ignored |
| `delete documents -b <id>` | Reads a CSV containing `id` values and issues DELETE calls sequentially | Extra columns are ignored |
| `map accounts -b <id>` | Resolves source+target business contexts and emits `source_id,target_id,number` matches by account `number` | Prints missing-target warnings to stderr; exit code is `EX_DATAERR` when warnings exist |
| `create businesses [--fields …]` | Reads business-create CSV rows from stdin (or `--in`) and POSTs them sequentially | Supports `name`, optional `slug`, optional `business_id`, and optional `form` |
| `create accounts -b <id> [--fields …]` | Reads account-create CSV rows from stdin (or `--in`) and POSTs them sequentially | Supports `number`, `name`, optional `type`, `description`, and `opening_balance` |
| `create contacts -b <id> [--fields …]` | Reads contact-create CSV rows from stdin (or `--in`) and POSTs them sequentially | Supports `name`, optional `type`, `customer_id`, `contact_business_id`, `notes`, and `phone_number` |
| `create documents -b <id> [--account-id-map <path>] [--strict] [--fields …]` | Reads minimal document-create CSV and POSTs documents sequentially | Optional blueprint account-id remap via mapping CSV |

Still unimplemented:

- `update documents`
- `delete businesses` (intentionally omitted)

## CSV expectations

- `--fields` accepts top-level DTO property names as comma-separated and/or space-separated input (for example `--fields "id,name"` or `--fields id name`). The same selection applies to both output and input.
- `id` is always required when reading updates or deletes; if `--fields` is present, the CSV header still has to include it.
- Update rows are handled one CSV row at a time: the CLI fetches the current server-side entity by `id`, normalizes the requested patch against that fresh value, and only then issues a PATCH when something changed.
- Repeated `id` values in update CSV input are allowed. They are processed sequentially, so a later row sees the result of earlier successful PATCHes for the same `id`.
- Collections of strings are stored as `;`-separated lists. `option<_>` values use empty cells for `None`.
- Extra columns in the input are ignored when `--fields` is present; otherwise we validate that every header maps to a property.

Use `--out`/`--in` if you prefer explicit file paths over shell redirection.

## Configuration and runtime

`Nocfo.Tools.Runtime.ToolConfig` builds a `ToolContext` from two sources, lowest-priority first:

1. **Named profile** (from `~/.config/nocfo/config.toml`, selected with `--profile <name>`)
2. **Environment variables** (always override the profile)

### Named profiles

Create `~/.config/nocfo/config.toml`:

```toml
[profiles.test]
token    = "your-test-token"
base_url = "https://api-tst.nocfo.io"

[profiles.prod]
token    = "your-prod-token"
base_url = "https://api-prd.nocfo.io"

# Cross-environment profiles also support source_token / source_base_url
[profiles.prod-to-test]
token          = "your-test-token"
base_url       = "https://api-tst.nocfo.io"
source_token   = "your-prod-token"
source_base_url = "https://api-prd.nocfo.io"
```

Pass `--profile <name>` (or `-p <name>`) as a top-level flag:

```bash
dotnet run --project tools -- --profile prod list businesses
```

If `NOCFO_TOOL_CONFIG_HOME` is set, the config file is read from
`$NOCFO_TOOL_CONFIG_HOME/config.toml` instead of `~/.config/nocfo/config.toml`.

Specifying an unknown profile or a missing config file is a hard error (`EX_CONFIG`).
Env vars set alongside `--profile` still take precedence over the profile values.

### Repo-local online test profile

The online regression suite uses a repo-local config root under `tests-online/config`:

1. Copy `tests-online/config/config.toml.example` to `tests-online/config/config.toml`.
2. Put the real api-tst token in `[profiles.online-test]`.
3. Copy `tests-online/config/fixture.env.example` to `tests-online/config/fixture.env`
4. Set `TEST_BUSINESS_SLUG` in `tests-online/config/fixture.env`.
5. Ensure `bash` and `python3` are available.

Then run:

```bash
make test-online
```

### Environment variables

- Target/default context:
  - `NOCFO_TARGET_TOKEN` (fallback: `NOCFO_TOKEN`, then profile `token`)
  - `NOCFO_TARGET_BASE_URL` (fallback: `NOCFO_BASE_URL`, then profile `base_url`, default `https://api-tst.nocfo.io`)
- Source context (used by dual-environment commands):
  - `NOCFO_SOURCE_TOKEN` (fallback: profile `source_token`)
  - `NOCFO_SOURCE_BASE_URL` (fallback: profile `source_base_url`, default `https://api-prd.nocfo.io`)

The context wraps the shared `Http.createHttpContext` and `Accounting.ofHttp` from
`hawaii-client`, plus the active stdin/stdout handles.

## Internals

- **Arguments** (`Arguments.fs`): Argu discriminated unions define `list`, `update`, `delete`, and nested subcommands (`businesses`, `accounts`, `contacts`, `documents`). `--fields`, `--profile`, and `--dry-run` live at the top level.
- **Config** (`Config.fs`): reads `~/.config/nocfo/config.toml` and resolves a named profile into `ProfileSettings`.
- **Runtime + Streams** (`Tools.fs`): layers profile settings under env vars, builds `AccountingContext`, and routes CSV readers/writers.
- **Program flow** (`Program.fs`):
  - `list` commands: stream via `Streams.streamBusinesses`, `Streams.streamAccounts`, `Streams.streamContacts`, or `Streams.streamDocuments`, hydrate rows (`Streams.hydrateAndUnwrap`), write CSV lazily.
  - `update businesses/accounts/contacts`: read CSV into repo-owned delta records, fetch the current entity (`slug` for businesses, `id` for accounts/contacts), normalize against that fresh API state, and PATCH immediately when something changed.
  - `delete` accounts/contacts/documents: read CSV `id` rows, map them to command DUs, and execute them through the shared command stream machinery.
  - `map accounts`: align source/target account streams by `number` and output `source_id,target_id,number`.
  - `create` businesses/accounts/contacts/documents: read create payload rows, optionally rewrite blueprint account IDs for documents, and POST sequentially through the shared command stream machinery.

Everything runs on `AsyncSeq`, so listing scales to large datasets without holding them all in memory.

## Limitations / future work

- `update documents` remains the main command-surface gap.
- `update` trades extra GET requests for streaming-friendly semantics; large CSV updates are network-bound.
- Standalone binaries are published through GitHub Releases.

See `../hawaii-client/README.md` for deeper implementation notes and ideas for extending the domain model.
