# TASK — Fix known defects

Findings from a full read of `hawaii-client/src`, `tools/`, `tests/` and a run of the built CLI
(2026-08-04). Build is clean (0 warnings) and all 65 unit tests pass; everything below is a
behavioural or hygiene defect, not a build break.

Ordered by severity. D1–D3 are the ones that bite a user today.

---

## D1 — Every failure path except configuration aborts with SIGABRT (exit 134) — **FIXED**

**Severity: high.** `tools/Program.fs`

`main` wraps its body in a `try … with ex when ex.Message.StartsWith("Tool configuration failed:")`.
Nothing else is caught, and the stream code deliberately throws:

- [Program.fs:50](tools/Program.fs#L50), [Program.fs:80](tools/Program.fs#L80) — `failwithf "Failed to get business: %A"` inside `AsyncSeq.map`
- [Csv.fs:93](hawaii-client/src/Csv.fs#L93), [Csv.fs:107](hawaii-client/src/Csv.fs#L107), [Csv.fs:220](hawaii-client/src/Csv.fs#L220) — `failwithf` on header/field validation
- [Program.fs:513](tools/Program.fs#L513) — `parser.ParseCommandLine(argv, raiseOnUsage = false)` followed by
  `results.GetSubCommand()`, which raises `ArguParseException` when no subcommand was given
- [Program.fs:522](tools/Program.fs#L522) — `new StreamWriter(path)` before the `try`

Reproduced against the built binary:

```
$ nocfo                                   # no subcommand
Unhandled exception. Argu.ArguParseException: ERROR: no valid subcommand has been specified.
… exit 134

$ NOCFO_BASE_URL=http://127.0.0.1:9 nocfo list businesses
<CSV header written to stdout>
Unhandled exception. System.AggregateException: … Connection refused …
… exit 134

$ nocfo list businesses --fields "id,nosuchfield"
… exit 134
```

`mapDomainErrorToExitCode` ([Program.fs:281](tools/Program.fs#L281)) already maps `DomainError` onto
sysexits codes, but it is only reachable from `map accounts`.

**Fix**

1. Wrap the whole of `main` in a handler that maps `ArguParseException` → usage + `EX_USAGE`,
   `DomainStreamException` → `mapDomainErrorToExitCode`, and anything else → message + `EX_SOFTWARE`.
   No stack traces for expected conditions.
2. Route `listBusinesses` / `listEntitiesForBusiness` failures through `mapDomainErrorToExitCode`
   instead of `failwithf` and bare `return 1`.
3. Parse with `raiseOnUsage = true` (or check `results.IsUsageRequested` explicitly) so
   `nocfo --help` still exits 0 while a missing subcommand exits non-zero.
4. Open the `--out` file inside the `try`, and `use` it so it is flushed and closed deterministically.

**Fixed**: `main` is now `try run parser argv with ex -> exitCodeForException ex`. The stream code
raises `DomainStreamException` instead of `failwithf`; `Csv.fs` raises a new `CsvFormatException`
for header and `--fields` errors (the two remaining `failwithf` there are genuine programmer errors
and still map to `EX_SOFTWARE`); business-context failures go through `mapDomainErrorToExitCode`
rather than `return 1`; parsing uses `raiseOnUsage = true` so `--help` prints usage and exits 0
while a missing or unrecognised subcommand exits 64; `--in`/`--out` are `use`-bound, which also
fixes `-o` silently truncating its output because the `StreamWriter` was never flushed.
Verified end to end: `--help` 0, no subcommand 64, unknown `--fields` 65, missing input 66,
connection refused 69, bad token 77, missing token 78 — no stack traces.
Covered by `tests/ExitCodeTests.fs`.

Not addressed here: `writeCsvGeneric` still emits the CSV header to stdout before the first row is
fetched, so a failing `list` prints a header and then the error.

---

## D2 — Transport failures are invisible to the error model and are never retried — **FIXED**

**Severity: high.** `hawaii-client/src/Http.fs`

`Http.send` awaits `client.SendAsync` with no `try`. `HttpRequestException` (connection refused,
DNS failure, TLS failure) and `TaskCanceledException` (the 30 s timeout) propagate as exceptions,
so:

- they bypass the typed `HttpError` DU entirely;
- `retry` ([Http.fs:128](hawaii-client/src/Http.fs#L128)) never sees them, so genuinely transient
  network errors — the case retry exists for — are *not* retried, while 429/5xx are;
- they surface as D1.

**Fix**: add `Transport of url: Uri * message: string` and `Timeout of url: Uri` to `HttpError`,
catch both exception types in `send`, and include them in `shouldRetry`.
Extend `mapDomainErrorToExitCode` with the new cases (`EX_UNAVAILABLE`).

**Fixed**: `send` awaits through `Async.Catch` and classifies the flattened exception —
`HttpRequestException` → `Transport`, `TaskCanceledException` (when the caller's token is not
cancelled) → `Timeout`; anything else is rethrown with its stack intact. Both are retried.
Covered by `tests/HttpErrorTests.fs`, which also asserts that a transient transport failure is
retried rather than surfaced.

---

## D3 — `map accounts` mis-aligns when account numbers have unequal digit counts

**Severity: medium-high.** `tools/Program.fs:351`, `hawaii-client/src/Streams.fs:18`

`alignByKey` is an ordered merge: it requires both inputs sorted by the *same* comparison as the
key it is given. `mapAccounts` keys on `AccountFull.number`, a **string**, so F# `compare` orders
`"1000" < "999"`. The API almost certainly orders accounts numerically (the schema exposes a
`padded_number` field precisely for this).

**Failure scenario**: source has accounts `999` and `1000`, target has only `1000`. Numeric server
order gives left = `["999"; "1000"]`, right = `["1000"]`. First step compares `"999"` vs `"1000"`;
lexicographically `"999" > "1000"`, so the merge emits `MissingLeft "1000"` and advances the
*right* stream, which is now exhausted. `"1000"` on the left never meets `"1000"` on the right.
Result: a silently incomplete `account-id-map.csv` plus a spurious
`Warning: no target mapping for source account …`, and `create documents -m <that map>` then
either fails (strict) or **silently substitutes an empty blueprint** (non-strict,
[BlueprintJson.fs:104](tools/BlueprintJson.fs#L104)) — i.e. it posts documents with no entries.

Finnish charts of accounts are usually uniformly 4-digit, which is why this has not been noticed;
the NoCFO schema permits up to 7 characters.

**Fix**: key on `padded_number` (int) instead of `number`, or on
`(number.PadLeft(7, '0'))`. Add a regression case to `StreamAlignmentTests.fs` with mixed-width keys.

Same latent issue in `EntityOps.deltasToCommands`, which keys accounts on `id` while the API
returns them ordered by number — currently harmless because only tests call it, but it should
either be fixed or deleted (see D7).

---

## D4 — `Business.ofRaw` / `fetchBySlug` index `identifiers.[0]` unconditionally

**Severity: medium.** [Domain.fs:448](hawaii-client/src/Domain.fs#L448),
[Domain.fs:465](hawaii-client/src/Domain.fs#L465), [Domain.fs:494](hawaii-client/src/Domain.fs#L494),
[Domain.fs:870](hawaii-client/src/Domain.fs#L870) — the first already carries an `XXX fixme`.

A business with an empty `identifiers` list raises `ArgumentException` mid-stream, which via D1
becomes exit 134. This is reachable from `list businesses` — i.e. the very first thing a new user
runs — if any business in the account lacks an identifier.

**Fix**: make `BusinessKey.id` a `BusinessIdentifier option`, or fail with a
`DomainError.Unexpected` naming the offending slug. The four call sites should share one
`BusinessFull` constructor rather than repeating the record literal.

---

## D5 — `AsyncSeq.tryHead` buffers the entire stream

**Severity: medium.** [AsyncSeq.fs:27](hawaii-client/src/AsyncSeq.fs#L27) — already marked
`XXX: Replace with a lazy version`.

`liftAsync` calls `AsyncSeq.toListAsync` before applying `Seq.tryHead`. `BusinessResolver.resolve`
uses it, so **every business-scoped command** (`-b …`) pages through the *complete* business list
before it can start work, defeating the repo's central "never buffer" rule.

**Fix**: `let tryHead (s: AsyncSeq<'T>) = FSharp.Control.AsyncSeq.tryFirst s`. The shadowing
`liftAsync`/`tryHead` pair then disappears.

---

## D6 — `list accounts` performs one HTTP request per account for nothing

**Severity: medium.** [Domain.fs:519](hawaii-client/src/Domain.fs#L519),
[Program.fs:64](tools/Program.fs#L64)

`Account.ofRow` wraps every `AccountList` row as `Hydratable.Partial`, and
`listEntitiesForBusiness` immediately calls `hydrateAndUnwrap`, so listing N accounts costs
`⌈N/100⌉ + N` requests. `AccountList` and `Account` differ only in `balance` and `is_used`, so
`list accounts --fields "id,number,name,type"` — the documented example — pays N round-trips for
fields it already has.

**Fix**: hydrate only when a requested field is absent from the row type. The field set is already
known at that point (`fields: string list`), so the test is a set difference between the requested
names and `typeof<AccountRow>`'s properties. This also removes the reason `list accounts` is slow
enough to notice on a real chart of accounts.

---

## D7 — Dead code

**Severity: low**, but the project's own rules say to delete it cleanly.

| What | Where |
| --- | --- |
| `Reports.fs` in its entirety — nothing references `Reports.addToTotals` | `hawaii-client/src/Reports.fs` |
| `AccountClass`, `AccountClassTotals`, `Account.classify`, `Account.hydrate` — only used by `Reports.fs` and its tests | `Domain.fs:83–85, 522–552` |
| `Streams.streamPatches`, `Streams.streamCreates` | `Streams.fs:93–107` |
| `Business.ofContext`, `Business.hydrate`, `Document.hydrate`, `Contact.hydrate` | `Domain.fs` |
| `IOOptions.IncludeHeader`, `.NewLine`, `.UnknownFieldPolicy` and the `UnknownFieldPolicy` DU — declared, defaulted, never read | `Csv.fs:172–195` |
| `AsyncResult.existsOk` | `AsyncSeq.fs:23` |
| `EntityOps.deltasToCommands` + `Account.diffAccount` / `Contact.diffContact` / `Account.deltasToCommands` / `Contact.deltasToCommands` — only reachable from tests | `Domain.fs:375–398, 557–565, 617–625` |
| `AccountCommand.UpdateAccount` / `ContactCommand.UpdateContact` branches — the CLI updates via `executeDeltaUpdates`, not via commands | `Domain.fs` |

Note `Reports.fs` is the natural home for the rolling-balance fold — see `PLAN-rolling-balance.md`.
Replace its contents rather than deleting the file.

---

## D8 — `hawaii-client/src/hawaii-client.csproj` is an empty C# project

**Severity: low.** It declares `AssemblyName`/`RootNamespace` `Nocfo.CsvHelpers` and a `CsvHelper`
package reference, but `src/` contains no `.cs` files — it builds an empty assembly. It is
referenced by `hawaii-client.fsproj` *and* listed in `nocfo.slnx`, alongside the F# module of the
same name (`CsvHelper.fs`, `module Nocfo.CsvHelpers`), which is what actually gets used.

**Fix**: delete the `.csproj`, its `<ProjectReference>` in `hawaii-client.fsproj`, and its entry in
`nocfo.slnx`. The header comment inside it (`<!-- hawaii-client/hawaii-client.csproj -->`) also
names the wrong path.

---

## D9 — URL construction interpolates path segments unescaped — **FIXED**

**Severity: low.** `hawaii-client/src/Endpoints.fs`

Every builder interpolates `slug` and `id` straight into the path, and each paged variant repeats
`?page_size=100&page={page}`. A slug containing `/`, `?` or `#` produces a wrong request rather
than an error. Not exploitable here (slugs come from the API, not from users) but it will become
one as soon as query parameters carry user input — which is exactly what the rolling-balance work
introduces (`date_from`, `date_to`, `account`).

**Fix**: one small `Uri.EscapeDataString`-based segment/query helper before adding new endpoints.

**Fixed**: `seg` escapes every interpolated slug and id; `paged` replaces the four repetitions of
`?page_size=100&page={page}`. Covered by `tests/EndpointsTests.fs`.

---

## D10 — Repository hygiene

- `tmp/` is **untracked and not gitignored**, and currently contains
  `Konekielinen tiliote(26-04-01).nda` — a real Nordea statement with individuals' names, IBANs and
  payment references. One `git add .` publishes it. Add `tmp/` to `.gitignore` now.
  Any NDA test fixture must be synthetic or redacted.
- The working-tree change to `.gitignore` adds `.claude`, which hides project-shared
  `.claude/settings.json` as well as personal `settings.local.json`. If only the latter is meant to
  be private, ignore `.claude/settings.local.json` instead.
- `README.md` still refers to `csv/…` paths and a `csv/` directory that does not exist in this repo.

---

## Suggested order

1. **D1 + D2** together — they are the same user-visible symptom and touch the same code.
2. **D3** — silent data loss; small fix, needs a test.
3. **D10** — one line, removes a privacy risk.
4. **D4, D5** — small, independent.
5. **D6** — needs a small design decision about where field-awareness lives.
6. **D7, D8, D9** — cleanup; do D9 before starting `PLAN-rolling-balance.md`.
