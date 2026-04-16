# nocfo-api-tool

An F# CLI tool and client library for the [NoCFO](https://nocfo.io/) Finnish accounting API.

> **Bootstrapped** from [nocfo-api-onboard](https://github.com/pekkanikander/nocfo-api-onboard),
> an exploration repo that documents five approaches tried before arriving at this one.
> The full narrative — lessons learned, earlier iterations, exploratory FSI scripts — lives there.

## Repository Tour

- `hawaii-client/` – F# library using Hawaii-generated types, lazy `AsyncSeq` streams, and a thin domain layer. Start here.
- `api/openapi.json` – The upstream NoCFO OpenAPI document used for generation.
- `tools/` – CSV-first CLI ("nocfo") built on top of the Hawaii F# client library; see `tools/README.md`.
- `tests/` – xUnit unit tests for the pure library modules (no network access needed).
- `tests-online/` – Shell-based online regression tests for command line commands against `api-tst`.
- `requests/` – Raw HTTP checks (VS Code REST client format) for manual smoke testing.
- `vendor/Hawaii/` – Forked generator with small fixes for nullable handling, enum tolerance, and operation name cleanup.

## Quick Start: `nocfo` CLI (tools/)

The CLI in `tools/` is the easiest way to interact with the API. It streams
entities, writes them as CSV, and can reconcile edited rows back to the server.

Prebuilt self-contained binaries are published on GitHub Releases for `osx-arm64`,
`linux-x64`, and `win-x64`. Pushing a tag named `vX.Y.Z` triggers CI to build
and attach `nocfo-<tag>-<rid>` archives to that release.

1. **Prerequisites**
   - .NET 10 SDK (`dotnet --version` ≥ 10.0)
   - macOS or Linux shell (the code itself is cross-platform)
   - Auth token and base URL supplied via either a **named profile** or **environment variables**:
     - Named profile: create `~/.config/nocfo/config.toml` (see `tools/README.md`) and pass `--profile <name>`
     - Environment variables: `NOCFO_TARGET_TOKEN` (or fallback `NOCFO_TOKEN`); optionally `NOCFO_TARGET_BASE_URL` (defaults to `https://api-tst.nocfo.io`)
   - `NOCFO_SOURCE_TOKEN` (or a profile with `source_token`) only when running dual-environment commands like `map accounts`
   - If a local `.env` exists, you may `source .env` to populate tokens and aliases for your shell session.
     The GitHub version of this repo does not include `.env`.

   ```bash
   # Option A: env vars
   export NOCFO_TOKEN="paste-your-token"

   # Option B: named profile (see tools/README.md for config file format)
   # dotnet run --project tools -- --profile test list businesses
   ```

2. **Build once and run tests** (from repo root):

   ```bash
   dotnet build
   make test
   ```

   The online regression suite is separate and requires local api-tst credentials
   that need to be first configured, see [`tests-online/README.md`](tests-online/README.md).

3. **List businesses**:

   ```bash
   dotnet run --project tools -- \
     list businesses --fields "id,name,slug" > businesses.csv
   ```

4. **List accounts for a business**:

   ```bash
   dotnet run --project tools -- list accounts \
     -b <business-id> \
     --fields "id,number,name,type" > accounts.csv
   ```

5. **List documents for a business**:

   ```bash
   dotnet run --project tools -- list documents \
     -b <business-id> \
     --fields "id,number,date,balance" > documents.csv
   ```

6. **Update accounts** by editing the CSV (keep `id`) and piping it back in:

   ```bash
   dotnet run --project tools -- update accounts \
     -b <business-id> \
     --fields "id,number,name" < accounts.csv
   ```

7. **Update contacts** by editing exported contacts (keep `id`) and piping them back in:

   ```bash
   dotnet run --project tools -- update contacts \
     -b <business-id> \
     --fields "id,name,invoicing_email,notes" < contacts.csv
   ```

8. **Delete accounts** by piping a CSV with the account `id`s you want to drop:

   ```bash
   dotnet run --project tools -- delete accounts \
     -b <business-id> < ids-to-delete.csv
   ```

9. **Map account IDs between environments** (source -> target by account `number`):

   ```bash
   NOCFO_SOURCE_TOKEN="paste-source-token" NOCFO_TARGET_TOKEN="paste-target-token" \
   dotnet run --project tools -- map accounts \
     -b <business-id> > csv/account-id-map.csv
   ```

10. **Create minimal documents in target**:

   ```bash
   dotnet run --project tools -- create documents \
     -b <target-business-id> \
     --account-id-map csv/account-id-map.csv \
     < csv/documents-create.csv
   ```

### CLI Notes

- `--profile <name>` / `-p <name>` selects a named profile from `~/.config/nocfo/config.toml`.
  Environment variables always override profile values when both are set.
- `--dry-run` prints what mutations would be sent without executing them.
- `--fields` controls both which columns are emitted and which columns are read back.
  `id` is always required when executing updates or deletes.
- Output defaults to stdout and input defaults to stdin;
  `--out`/`--in` override those streams without shell redirection.
- Currently implemented verbs include all `list` commands; `update businesses`, `update accounts`,
  `update contacts`; `delete accounts`, `delete contacts`, `delete documents`; `create businesses`,
  `create accounts`, `create contacts`, and minimal `create documents`; plus `map accounts`.
- Errors and HTTP traces go to stderr so you can keep piping stdout to files.

See `tools/README.md` for a deeper dive into configuration, CSV expectations,
and the internal architecture.

## Online Regression Suite

The repo includes a separate shell-based online test suite under `tests-online/`.
It exercises the existing CLI end to end against `https://api-tst.nocfo.io` and is
not part of the default `dotnet test` flow.

Setup:

1. Copy `tests-online/config/config.toml.example` to `tests-online/config/config.toml`.
2. Set a real api-tst token in the `online-test` profile.
3. Set `TEST_BUSINESS_SLUG` in `tests-online/config/fixture.env`.
4. Ensure `bash` and `python3` are available in your shell environment.

Run it explicitly with:

```bash
make test-online
```

## Releases

CI publishes release archives automatically when a tag matching `v*` is pushed.
Current release targets:

- `osx-arm64`
- `linux-x64`
- `win-x64`

Assets are named `nocfo-<tag>-<rid>.tar.gz` on Unix targets and
`nocfo-<tag>-<rid>.zip` on Windows. Each archive contains the full self-contained
publish directory for that platform.

## Regenerating the Hawaii Client

The generated client under `hawaii-client/generated/` is checked in, so the repo builds without a regeneration step.
Regenerate only when the NoCFO OpenAPI spec changes.

1. Refresh `api/openapi.json`.

   ```bash
   curl -L --fail --silent --show-error \
     -H "Accept: application/vnd.oai.openapi+json;version=3.0" \
     "https://api-tst.nocfo.io/openapi/" \
     -o api/openapi.json
   ```

2. Build the local Hawaii fork:

   ```bash
   dotnet build vendor/Hawaii/src/Hawaii.fsproj -c Release
   ```

3. From the **repo root**, run the built Hawaii CLI against the curated config:

   ```bash
   dotnet ./vendor/Hawaii/src/bin/Release/net10.0/Hawaii.dll \
     --config ./hawaii-client/nocfo-api-hawaii.json \
     --no-logo
   ```

4. Rebuild everything:

   ```bash
   dotnet build
   ```

Notes:

- `hawaii-client/nocfo-api-hawaii.json` currently assumes you run Hawaii from the repo root:
  `schema` is resolved from the current working directory, while `output` is resolved relative to the config file.

For more step-by-step build instructions, see `hawaii-client/README.md`.

## What Our Approach Demonstrates

- **Lazy streaming over paginated endpoints** – `AsyncSeq` wrappers provide pull-based
  iteration over businesses and accounts while preserving the original pagination semantics.
- **Hydratable domain model** – `Hydratable` discriminated unions keep initial payloads
  lightweight and defer full record loading until needed by folds or reports.
- **Token-based auth done correctly** – All HTTP helpers attach the
  `Authorization: Token <value>` header per request,
  preventing subtle errors with .NET's default header validation.
- **Structured HTTP errors and retry** – `Http.fs` returns a typed `HttpError` DU
  and retries 429/5xx responses automatically with exponential backoff.

## Background

The design settled here is the fifth iteration. Earlier approaches (TypeScript, PureScript,
F#+NSwag) are documented in the
[nocfo-api-onboard](https://github.com/pekkanikander/nocfo-api-onboard) exploration repo,
along with `LESSONS-LEARNED.md` and exploratory FSI scripts that are not carried over here.

## Status

Active development. See `ROADMAP.md` for the current plan.

If you use any part of this repo, please do so at your own discretion.
Contributions are welcome.
