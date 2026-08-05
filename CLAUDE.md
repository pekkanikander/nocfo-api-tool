# CLAUDE.md — nocfo-api-tool

## What This Is

An F# CLI tool and client library for the [NoCFO](https://nocfo.io) Finnish accounting API.
The repo is in **late exploration / early adoption** stage: the pattern is settled, the code works,
but it is not yet a polished production tool.

The fifth iteration won: **F# + Hawaii OpenAPI generator + lazy `AsyncSeq` streams + pure domain folds**.
Previous iterations (TypeScript, PureScript, F#+NSwag) are **not** in this repo; they live in the
[nocfo-api-onboard](https://github.com/pekkanikander/nocfo-api-onboard) exploration repo together with
`LESSONS-LEARNED.md` and the exploratory FSI scripts.

---

## Repository Layout

```
api/openapi.json         NoCFO OpenAPI spec (source of truth for regeneration)
hawaii-client/           The library (used by tools/)
  generated/             Hawaii-generated code — do NOT hand-edit
  src/                   Hand-written domain, streaming, HTTP, CSV logic
tools/                   CLI exe project `nocfo` (depends on hawaii-client)
tests/                   xUnit unit tests — pure, no network
tests-online/            Bash regression tests against api-tst (not part of `dotnet test`)
requests/                VS Code REST client files for manual HTTP testing
vendor/Hawaii/           Local fork of the Hawaii generator (git submodule)
.github/workflows/ci.yml Build + test on PR/push; publish + release on `v*` tags
Makefile                 build / test / test-online / test-mutate / publish-* / clean
dist/                    Self-contained publish output (gitignored)
```

`nocfo.slnx` is the solution. There is **no** `csv/` directory and no archived `v1-…`/`v4-…`
directories in this repo, despite what older docs and the README examples may imply.

---

## Key Architectural Concepts

### Generated vs. Hand-Written Code

- `hawaii-client/generated/` is fully machine-generated from `api/openapi.json`; never edit it manually.
  It is checked in, so the repo builds without running the generator.
- All business logic lives in `hawaii-client/src/`.
- `Domain.fs` wraps the generated types into a cleaner domain model.
- The generated `Client.fs` is **not used**: `Http.fs` talks to the API directly and only the
  generated *types* plus `NocfoApi.Http.Serializer.options` are consumed.

### Pagination & Streaming

- All list endpoints are paginated (page numbers, not cursors, since the Apr 2026 API update).
- `AsyncSeq.fs` → `paginateByPageSRTP` drives pagination lazily via SRTP constraints.
- `Streams.fs` provides `alignByKey`, `streamPaginated`, `streamChanges`, `streamPatches`, `streamCreates`
  (the last two are currently unused).
- Use `AsyncSeq` everywhere; never buffer full lists into memory.
- `Streams.alignByKey` requires **both** inputs to be sorted by the *same* comparison as the key
  function it is given; it raises `StreamOrderException` if they are not. Do not use it against
  a stream whose order is not known — the API documents no ordering for its list endpoints.

### Full / Patch / Delta Pattern

- API responses have rich *Full* types; PATCH payloads have sparse *Patch* types.
- `PatchShape.fs` uses cached reflection to normalise patch records, strip unchanged fields, and
  detect no-op updates before sending them to the API.
- CSV imports become `*Delta` records (hand-typed subsets of full records); they are diffed against
  current state before generating PATCH calls.
- Two diff/update paths exist: `EntityOps.executeDeltaUpdates` (fetch-per-row; what the CLI uses)
  and `EntityOps.deltasToCommands` (stream alignment; only exercised by tests).

### Authentication & Configuration

- Token resolution order: `NOCFO_TARGET_TOKEN` → `NOCFO_TOKEN` → profile `token`.
- Base URL order: `NOCFO_TARGET_BASE_URL` → `NOCFO_BASE_URL` → profile `base_url` →
  `https://api-tst.nocfo.io`.
- Cross-environment commands additionally use `NOCFO_SOURCE_TOKEN` / `NOCFO_SOURCE_BASE_URL`
  (source default `https://api-prd.nocfo.io`).
- Header format is `Authorization: Token <value>` (NOT `Bearer`).
- Named profiles: `~/.config/nocfo/config.toml`, `[profiles.<name>]`, selected with `--profile`.
  Override the directory with `NOCFO_TOOL_CONFIG_HOME` (this is how `tests-online/` isolates itself).
  Environment variables always win over profile values.

### CSV Layer

- `CsvHelper` (v33) with semicolon-separated list support.
- `--fields` flag selects which CSV columns to emit or consume.
- Reading: validates headers, maps to typed F# records, ignores unspecified columns.
- Writing uses CsvHelper class maps; **reading bypasses CsvHelper's mapping** and builds records
  reflectively (`collectRecordMetadata` / `buildRecordFromCsv`) because AutoMap trips on F# shapes.

---

## Build Commands

```bash
# Build everything (from repo root — uses nocfo.slnx)
dotnet build

# Run unit tests (`dotnet test` alone also works; `make test` is the shorthand)
dotnet test tests

# Online regression tests — needs tests-online/config/{config.toml,fixture.env}
make test-online
make test-mutate

# Build a specific project
dotnet build hawaii-client
dotnet build tools

# Run the CLI
dotnet run --project tools -- <args>

# Self-contained binaries into dist/
make publish

# Regenerate from updated OpenAPI spec (run from repo root)
curl -H "Accept: application/vnd.oai.openapi+json;version=3.0" \
  https://api-tst.nocfo.io/openapi/ > api/openapi.json
dotnet ./vendor/Hawaii/src/bin/Release/net10.0/Hawaii.dll \
  --config ./hawaii-client/nocfo-api-hawaii.json --no-logo

# Build the local Hawaii generator (only needed after submodule changes)
dotnet build vendor/Hawaii/src/Hawaii.fsproj -c Release
```

`nocfo-api-hawaii.json` carries an `overrideSchema` block that patches three upstream spec bugs
(`DocumentList.period`, `DocumentInstance.period`, `AttachmentInstance.analysis_results`).
Keep it when refreshing the spec.

---

## CLI Surface

Global flags: `--in/-i`, `--out/-o`, `--profile/-p`, `--dry-run/-n`, `--verbose/-v`.
Input defaults to stdin, output to stdout; errors and HTTP traces go to stderr.

| verb | entities |
| --- | --- |
| `list` | businesses, accounts, contacts, documents |
| `update` | businesses, accounts, contacts, documents |
| `delete` | accounts, contacts, documents |
| `create` | businesses, accounts, contacts, documents |
| `map` | accounts (source → target env, keyed on account number) |
| `balance` | rolling balance of the selected accounts over a period (`--daily` for closing balances) |
| `reconcile` | daily balances of two sources compared day by day |

```bash
dotnet run --project tools -- list businesses --fields "id,name,slug"
dotnet run --project tools -- list accounts -b <slug-or-vat> --fields "id,number,name,type"
dotnet run --project tools -- update accounts -b <slug-or-vat> --fields "id,number,name" < file.csv
dotnet run --project tools -- map accounts -b <slug> > account-id-map.csv
dotnet run --project tools -- create documents -b <slug> -m account-id-map.csv < documents.csv
dotnet run --project tools -- balance -b <slug> --from 2025-01-01 --to 2025-03-31 -a 1920 --daily
dotnet run --project tools -- reconcile -b <slug> --from 2025-01-01 --to 2025-03-31 -a 1920 \
  --left ledger --right "nda:statement.nda#<bank-account>"
```

`balance` and `reconcile` read the server's own ledger report
(`POST /v1/business/{slug}/report/ledger/`), whose `balance_csum` already includes the balance
carried into the period. A reconcile source is `ledger`, `nda:<path>[#<account>]` (a TITO bank
statement) or `csv:<path>` (a `date,balance` CSV, i.e. what `balance --daily` writes).

`delete businesses` is intentionally omitted.

---

## Source File Compile Order (hawaii-client.fsproj)

F# requires declaration-before-use ordering:

1. `Endpoints.fs` — URL builders
2. `Http.fs` — HTTP client wrapper, typed `HttpError`, retry
3. `AsyncSeq.fs` — Async/Result/AsyncSeq helpers, `paginateByPageSRTP`
4. `Streams.fs` — Generic streaming + `alignByKey`
5. `JsonHelpers.fs` — STJ utility layer (wraps `Serializer.options`, JSON helpers)
6. `PatchShape.fs` — Reflection-based patch normalisation
7. `Domain.fs` — Domain model, hydration, diffing, commands, per-entity streams
8. `CsvHelper.fs` — Custom CsvHelper converters
9. `Csv.fs` — CSV read/write API
10. `Tito.fs` — Reader for the Finnish machine-readable bank statement (TITO, `.nda`)
11. `Reports.fs` — Rolling balance, daily reconciliation, and the sources they read

`tools/`: `Config.fs` → `Tools.fs` → `Arguments.fs` → `BlueprintJson.fs` → `Program.fs`.

---

## Known Limitations & TODOs (as of August 2026)

- Generated code is **checked in** — regeneration is a manual step when the API spec changes.
- API coverage: businesses, accounts, contacts, documents, and the ledger report. Not covered:
  entries, periods, other reports, VAT, tags, files, invoicing. See `ROADMAP.md` Phase 5.
- Dead code flagged in `TASK-defects.md` D7 (test-only diff/command machinery, unused stream
  helpers) awaits a decision on whether the stream-alignment update path stays.

---

## Hawaii Generator

Located at `vendor/Hawaii/` (git submodule), pointing at
`Alterna-Dev-Studio/Hawaii-5-0`.  Hawaii-5-0 targets net10.0 and uses
`System.Text.Json` throughout; the generated code exposes
`NocfoApi.Http.Serializer.options : JsonSerializerOptions`.
Free-form JSON fields (e.g. `BusinessIdentifier.type`) are generated as
`System.Text.Json.JsonElement`.

The generator includes:

- Nullable primitive → `Option<T>` generation
- Robust enum deserialisation (tolerant converter)
- Operation name normalisation for names with spaces

---

## Testing Approach

Two layers.

### 1. xUnit unit tests (`tests/`)

```bash
dotnet test tests     # 131 tests
```

Framework: **xUnit** with **Unquote** for assertions (`test <@ expr @>`).
No network access — HTTP is faked with a stub `HttpMessageHandler` (see `EntityOpsTests.fs`).

- `PatchShapeTests.fs` — `PatchShape.Normalize` and `HasChanges` logic
- `StreamAlignmentTests.fs` — `Streams.alignByKey` merge algorithm
- `DomainDiffTests.fs` — `Account.diffAccount`, `Account.classify`
- `EntityOpsTests.fs` — `EntityOps.fetchById` / `diffToPatch` / `deltasToCommands` / `executeDeltaUpdates`
- `CsvTests.fs` — `Csv.readCsvGeneric` / `writeCsvGeneric` round-trips
- `TitoTests.fs` — `Tito` record framing, charset, and bank-account selection, on synthetic fixtures
- `BalanceTests.fs` — `Balance.rows` / `dailyClosing` over a hand-built ledger response
- `ReconcileTests.fs` — `Reconcile.daily` carry-forward, tolerance, and ordering
- `BlueprintJsonTests.fs` — document blueprint account-ID remapping

**Unquote + `inline` functions:** Unquote cannot dynamically invoke `inline` SRTP
functions via quotation reflection. Pre-compute the result into a `let result = ...`
binding and assert on `result`:

```fsharp
// ✗ fails at runtime — Unquote can't reflect into an inline function
test <@ Account.classify acc = Some Asset @>

// ✓ correct pattern
let result = Account.classify acc
test <@ result = Some Asset @>
```

### 2. Online regression tests (`tests-online/`)

Bash + `python3`, driven through `make test-online` / `make test-mutate`. They build the CLI once,
point `NOCFO_TOOL_CONFIG_HOME` at `tests-online/config/`, and use the `online-test` profile.
Both `config/config.toml` and `config/fixture.env` are gitignored and must be created locally.

---

## House Rules

- Never hand-edit `hawaii-client/generated/`.
- Prefer SRTP + `inline` generic functions over per-entity duplication; see the
  "Architectural Direction" section of `ROADMAP.md`. `paginateByPageSRTP` is the reference example.
- Amounts from the API are `float32`/`double`. Convert to `decimal` before any accumulation or
  comparison — never fold money in floating point.
- Do not commit anything under `tmp/`: it is *not* gitignored and currently holds a real bank
  statement with personal data.
