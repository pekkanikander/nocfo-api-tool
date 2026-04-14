# Hawaii Client

> **Status:** Revalidated against the live NoCFO test environment on April 7, 2026.

This folder contains the Hawaii-backed F# library that powers the `nocfo` CLI.
It combines committed generated client/types with hand-written HTTP, streaming,
domain, patch, reports, and CSV layers.

## Layout

```
hawaii-client/
├── generated/              # Hawaii output kept committed for reproducible builds
│   ├── Types.fs            # DTOs (Paginated* etc.)
│   ├── Client.fs           # Generated client surface
│   ├── OpenApiHttp.fs      # Serializer + tolerant enum helpers
│   └── StringEnum.fs
├── src/
│   ├── Endpoints.fs        # Centralized path builders (v1/business/...)
│   ├── Http.fs             # Token-aware HttpClient wiring + retry/error handling
│   ├── AsyncSeq.fs         # Pagination helpers and alignment utilities
│   ├── Streams.fs          # AsyncSeq helpers over paginated endpoints
│   ├── PatchShape.fs       # Typed PATCH normalization/diff helpers
│   ├── Domain.fs           # Hydratable entities + command execution
│   ├── Reports.fs          # Small fold/report helpers
│   ├── CsvHelper.fs        # CsvHelper interop glue
│   └── Csv.fs              # Generic CSV read/write helpers
├── hawaii-client.fsproj    # Main F# library
├── nocfo-api-hawaii.json   # Generator config for the NoCFO OpenAPI spec
├── Domain-design.md        # Notes on the domain model direction
├── api-spec-test.sh        # Optional spec-vs-server drift signal
└── README.md
```

The CLI under `../tools` links against this project. When you evolve the domain
surface or CSV helpers, remember that the CLI depends on those modules directly.

## Prerequisites

- .NET 10 SDK (`dotnet --version` ≥ 10.0).
- Access to the NoCFO API and a valid personal access token.
- `NOCFO_TOKEN` exported in your shell.
- The base URL defaults to `https://api-tst.nocfo.io`, the test environment.
- Optional: `NOCFO_BASE_URL` if you need to point at another cluster; it must be an absolute URI.
- If a local `.env` exists, you may `source .env` before running commands here. That file is local-only and not committed to GitHub.

Token management portals:
- Test: <https://login-tst.nocfo.io/auth/tokens/>
- (Production: <https://login.nocfo.io/auth/tokens/>)

## Build And Test

```bash
dotnet build hawaii-client/hawaii-client.fsproj
```

To build the whole repo instead:

```bash
dotnet build nocfo.slnx
```

The pure unit tests live in the repo-root `tests/` project:

```bash
dotnet test tests/tests.fsproj
```

No network access is required for those unit tests. Network access is only needed when
you regenerate the OpenAPI client, run the spec-drift script, or exercise the CLI
against a live NoCFO environment.

## How the Pieces Fit

- `Http.createHttpContext` normalises the base URL (always `/v1`) and attaches `Authorization: Token <value>` to each request. All HTTP helpers surface a structured `HttpError` DU (`Unauthorized`, `NotFound`, `RateLimited`, `ServerError`, `ClientError`, `ParseError`) and retry automatically on 429/5xx with exponential backoff.
- `AsyncSeqHelpers.paginateByPageSRTP` expresses “fetch page → yield results → follow `next`” as an `AsyncSeq<Result<'a,_>>`, preserving ordering and letting callers apply back-pressure.
- `Streams.streamBusinesses` / `streamAccounts` / `streamContacts` / `streamDocuments`
  wrap generated DTOs in domain types, ensuring we always carry hydration hooks inside
  `Hydratable`.
- `Domain.Hydratable` plus `Streams.hydrateAndUnwrap` give consumers (CLI + scripts) the choice between lazy partials and eagerly hydrated entities.
- `PatchShape` computes typed PATCH bodies by comparing desired delta rows with the
  freshly fetched API entity.
- `Account.executeDeltaUpdates` and `Contact.executeDeltaUpdates` drive update flows
  by fetching the current entity for each CSV `id`, diffing, and PATCHing in CSV order.
- `Streams.executeAccountCommands`, `Streams.executeContactCommands`, and
  `Streams.executeDocumentCommands` interpret command streams and turn them into HTTP API calls.
- `Reports.addToTotals` shows how to write deterministic folds on top of the streaming surface; treat it as a template for new reporting modules.
- `Csv.fs` plus `CsvHelper.fs` provide the generic CSV mapping used by both tests and CLI commands.

Use `Domain-design.md` for the higher-level rationale before touching alignment logic or hydration semantics.

## How `tools/` depends on this project

`dotnet run --project tools -- …` is a thin Argu-based CLI that reuses nearly every module here:

- `Nocfo.Tools.Runtime` builds on `Http.createHttpContext` + `Accounting.ofHttp`.
- `BusinessResolver.resolve` (from `Domain.fs`) is how the CLI maps free-form IDs to a `BusinessContext`.
- `Streams.streamBusinesses` / `streamAccounts` / `streamContacts` / `streamDocuments`
  plus `hydrateAndUnwrap` provide the listing flows.
- `Account.executeDeltaUpdates` / `Contact.executeDeltaUpdates` implement per-row updates.
- `Streams.executeAccountCommands`, `Streams.executeContactCommands`, and
  `Streams.executeDocumentCommands` implement deletes and document creation.
- `Alignment.alignEntries` is used by `map accounts` to align hydrated source and target
  account streams by account number.
- `Csv.fs` and `Nocfo.CsvHelpers` keep CSV exports/imports deterministic.

If you add or rename domain types, plan to update both this README and `tools/README.md` so users understand which commands are affected.

## Extending the client

1. Add or adjust paths in `src/Endpoints.fs`.
2. Compose a stream using `Streams.streamPaginated` or a custom AsyncSeq that fetches the endpoint and maps DTOs into domain types.
3. If the entity benefits from lazy hydration, create a `Hydratable` wrapper similar to `Account`.
4. Extend `Domain` with diff/command helpers so CLI-style workflows stay declarative.
5. Add CSV helpers (if needed) and wire the flow into the CLI and unit tests.

The `Account` modules are the most complete example of this pattern (listing, delta computation, execution).
Mirror that approach for new entities that are not covered yet (for example transactions or accounting periods).

## Refreshing the OpenAPI spec

The canonical schema in this repo is `../api/openapi.json`.
Grab a fresh copy from the NoCFO docs endpoint before regenerating the client:

Run `curl` from the repo root:

   ```bash
   # Test (default)
   curl --fail --silent --show-error \
     -H "Accept: application/vnd.oai.openapi+json;version=3.0" \
     "https://api-tst.nocfo.io/openapi/" \
     > api/openapi.json

   # Production (if you need parity with live data)
   curl --fail --silent --show-error \
     -H "Accept: application/vnd.oai.openapi+json;version=3.0" \
     "https://api-prd.nocfo.io/openapi/" \
     > api/openapi.json
   ```

Commit the updated schema before running Hawaii so the generated sources remain reproducible.

## Regenerating the Hawaii Output

We keep the generated code checked in so you can build immediately. Regenerate only when the upstream OpenAPI spec changes.

1. From the repo root, build the local Hawaii fork:
   ```bash
   dotnet build vendor/Hawaii/src/Hawaii.fsproj -c Release
   ```

Our forked generator in `vendor/Hawaii` includes fixes for nullable fields,
enum parsing, and operation name normalization that we relied on during November 2025.
If you use an upstream Hawaii, cross-check that those fixes have landed.

2. Still from the repo root, run the resulting Hawaii CLI against `hawaii-client/nocfo-api-hawaii.json`:
   ```bash
   dotnet ./vendor/Hawaii/src/bin/Release/net6.0/Hawaii.dll \
      --config ./hawaii-client/nocfo-api-hawaii.json \
      --no-logo
   ```

   The current config assumes you run this command from the repo root: `schema` is resolved from the working directory, while `output` is resolved relative to the config file.

3. Rebuild everything from the repo root to verify the regenerated code compiles:

   ```bash
   dotnet build
   dotnet test
   ```

**Operational note:** the local Hawaii fork still targets `net6.0`, so modern .NET SDKs emit support warnings during build. That warning is expected for now.

**Known generator workaround:**
`hawaii-client/nocfo-api-hawaii.json` overrides `AttachmentInstance.analysis_results` to a dummy nullable string.
Hawaii (as of Nov 2025) cannot serialize multipart fields that are arrays of objects,
so this keeps the generated client compiling even though the upload endpoint still accepts the real structure server-side.
If you start using that endpoint, revisit the override (or teach Hawaii how to encode complex multipart parts).

## Optional: Spec Drift Check (not recommended)

`api-spec-test.sh` can run Schemathesis and Dredd against a live server to spot
drift between the spec and production.
It is a convenience wrapper; you need `NOCFO_TOKEN` plus local installations
(or fallback commands) for Schemathesis, Dredd, `uvx`, and `python3`.

Note that running **Schemathesis may pollute you server environment.** Be careful.

```bash
./api-spec-test.sh --token "$NOCFO_TOKEN" \
  --spec ../api/openapi.json \
  --base-url https://api-tst.nocfo.io \
  --out ../api/reports/api-spec
```

The script produces a short Markdown summary alongside the raw JUnit/HAR artifacts.

## What’s Next (if you continue)

- Extend the domain/stream surface to other endpoints that the CLI does not cover yet,
  such as transactions, accounting periods, or VAT/reporting flows.
- Upstream the Hawaii generator patch set instead of pinning to the local fork.

Until then, treat this folder as the maintained Hawaii-based NoCFO client library
that the CLI builds on.
