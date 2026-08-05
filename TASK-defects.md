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

## D3 — `map accounts` mis-aligns when account numbers have unequal digit counts — **FIXED**

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

**Fixed**: not by the key swap proposed above. Neither the spec nor the API tells us what the
server actually orders accounts by: `padded_number` is documented only as `readOnly integer`, and
its two plausible meanings (`int(number)`, or the number right-padded to a fixed width) give
*opposite* orders for exactly the `999` / `1000` case at issue. Swapping one unverified ordering
assumption for another is not a fix, so `mapAccounts` no longer assumes an order at all: the new
`Program.mapAccountRows` indexes the target chart of accounts into a `Map` keyed on `number` and
looks each source account up in it. This is order-independent by construction. It buffers the
target chart, which is bounded and small — and `map accounts` already buffered the entire result
before writing the CSV, so nothing is lost.

`Streams.alignByKey` now also raises `StreamOrderException` (→ `EX_DATAERR`) when either input is
not sorted by the key it is aligned on, instead of silently desynchronising. That keeps the
remaining and future users of the merge — `deltasToCommands`, and the reconcile stage of
`PLAN-rolling-balance.md` — honest.

Covered by `tests/MapAccountsTests.fs` (mixed-width numbers in either order, unmatched source
accounts, target-only accounts, error propagation) and by the unsorted-input case in
`tests/StreamAlignmentTests.fs`.

**Ordering, measured (August 2026).** Once the api-tst token was refreshed, the question above
was settled empirically by renumbering one account to `999`, listing the chart, and restoring it:

- `padded_number` is the account number **right-padded with zeros to seven digits**:
  `1540 → 1540000`, `999 → 9990000`. It is therefore a numeric restatement of the *lexicographic*
  order of `number`, not of its numeric order.
- `GET …/account/` returns accounts in exactly that order — lexicographic by `number`, so `999`
  sorts after `4290`.

So the merge described in this defect really did break on mixed-width numbers, and the originally
proposed key (`padded_number`) would have worked. The order-independent fix stays: the ordering is
not documented anywhere in the spec, nothing pins it, and `map accounts` gains nothing from
depending on it.

Incidentally, a `PATCH` response carries a **stale** `padded_number` — restoring `999` to `1500`
returned `padded_number = 9990000`, while a subsequent `GET` gave the correct `1500000`. A
server-side wart; harmless here because nothing in this repo reads the field.

---

## D4 — `Business.ofRaw` / `fetchBySlug` index `identifiers.[0]` unconditionally — **FIXED**

**Severity: medium.** [Domain.fs:448](hawaii-client/src/Domain.fs#L448),
[Domain.fs:465](hawaii-client/src/Domain.fs#L465), [Domain.fs:494](hawaii-client/src/Domain.fs#L494),
[Domain.fs:870](hawaii-client/src/Domain.fs#L870) — the first already carries an `XXX fixme`.

A business with an empty `identifiers` list raises `ArgumentException` mid-stream, which via D1
becomes exit 134. This is reachable from `list businesses` — i.e. the very first thing a new user
runs — if any business in the account lacks an identifier.

**Fix**: make `BusinessKey.id` a `BusinessIdentifier option`, or fail with a
`DomainError.Unexpected` naming the offending slug. The four call sites should share one
`BusinessFull` constructor rather than repeating the record literal.

**Fixed** by deleting the field instead. `BusinessKey.id` was never read — not by the library, not
by `tools/`, not by the tests. Every business-scoped endpoint takes the slug, `BusinessResolver`
matches on `full.raw.identifiers` directly, and `BusinessKey` is the *partial* half of
`Hydratable`, i.e. exactly what is needed to fetch the full form. So the crash was paid for a value
nothing wanted. `BusinessKey` is now `{ slug: string }`, and the five record literals collapse into
`Business.fullOfRaw fallbackSlug raw`.

The optional `slug` in the same `XXX fixme` keeps its `"(none)"` fallback, now named
`Business.UnknownSlug`: it matches no endpoint, so `-b "(none)"` fails with the ordinary
"No matching business" error rather than issuing a nonsense request.

Covered by two cases in `tests/DomainDiffTests.fs` (empty `identifiers`; absent `slug`).

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

## D6 — `list accounts` performs one HTTP request per account for nothing — **FIXED**

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

**Fixed** by asking the CSV layer rather than reimplementing the field test: `Csv.canSupplyFields<'T>`
answers whether `writeCsvGeneric<'T>` could serve a selection from `'T` alone, which is exactly the
condition for skipping hydration — and it cannot drift from what the writer actually accepts. An
empty selection means "every field of the full form", so it hydrates.

`Program.writeEntitiesCsv<'Full, 'Row>` picks the branch, writing `'Row` records directly on the
fast path and `'Full` records otherwise. The three `Streams.stream*` functions now sit on top of
`streamAccountRows` / `streamDocumentRows` / `streamContactRows`, so an un-hydrated stream is
available to any caller. `map accounts` takes it too: it only ever read `id` and `number`, and was
paying N requests per environment for them.

Measured against api-tst on a 47-account chart:

| command | requests before | after |
| --- | --- | --- |
| `list accounts --fields "id,number,name,type"` | 49 | **2** |
| `list accounts --fields "id,number,balance"` | 49 | 49 |

Covered by three cases in `tests/MapAccountsTests.fs` counting hydration calls.

---

## D7 — Dead code

**Severity: low**, but the project's own rules say to delete it cleanly.

| What | Where |
| --- | --- |
| `AccountClass`, `AccountClassTotals`, `Account.classify`, `Account.hydrate` — only reachable from tests since `Reports.addToTotals` was replaced | `Domain.fs:83–85, 522–552` |
| `Streams.streamPatches`, `Streams.streamCreates` | `Streams.fs:93–107` |
| `Business.ofContext`, `Business.hydrate`, `Document.hydrate`, `Contact.hydrate` | `Domain.fs` |
| `IOOptions.IncludeHeader`, `.NewLine`, `.UnknownFieldPolicy` and the `UnknownFieldPolicy` DU — declared, defaulted, never read | `Csv.fs:172–195` |
| `AsyncResult.existsOk` | `AsyncSeq.fs:23` |
| `EntityOps.deltasToCommands` + `Account.diffAccount` / `Contact.diffContact` / `Account.deltasToCommands` / `Contact.deltasToCommands` — only reachable from tests | `Domain.fs:375–398, 557–565, 617–625` |
| `AccountCommand.UpdateAccount` / `ContactCommand.UpdateContact` branches — the CLI updates via `executeDeltaUpdates`, not via commands | `Domain.fs` |

`Reports.fs` is no longer dead: its contents were replaced by the rolling balance and the daily
reconciliation (`PLAN-rolling-balance.md`).

---

## D8 — `hawaii-client/src/hawaii-client.csproj` is an empty C# project — **FIXED**

**Severity: low.** It declares `AssemblyName`/`RootNamespace` `Nocfo.CsvHelpers` and a `CsvHelper`
package reference, but `src/` contains no `.cs` files — it builds an empty assembly. It is
referenced by `hawaii-client.fsproj` *and* listed in `nocfo.slnx`, alongside the F# module of the
same name (`CsvHelper.fs`, `module Nocfo.CsvHelpers`), which is what actually gets used.

**Fix**: delete the `.csproj`, its `<ProjectReference>` in `hawaii-client.fsproj`, and its entry in
`nocfo.slnx`. The header comment inside it (`<!-- hawaii-client/hawaii-client.csproj -->`) also
names the wrong path.

**Fixed**, but the premise above was wrong: the project was *not* empty. It compiled the one file
this audit missed, `src/CsvMapExtension.cs`, whose `MapBoxed` bound CsvHelper's non-generic
`ClassMap.Map` overload — F# cannot pick it from the two `Map(Expression …)` candidates (FS0041).
The shim turned out to be unnecessary anyway: `Csv.mapProperty` already holds the `PropertyInfo`,
so it now calls the `Map(Type, MemberInfo)` overload directly, which is unambiguous and drops the
hand-built `Expression` tree too. The `.cs` file, the `.csproj` and both references are gone.

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

## D10 — Repository hygiene — **FIXED**

- `tmp/` is **untracked and not gitignored**, and currently contains
  `Konekielinen tiliote(26-04-01).nda` — a real Nordea statement with individuals' names, IBANs and
  payment references. One `git add .` publishes it. Add `tmp/` to `.gitignore` now.
  Any NDA test fixture must be synthetic or redacted.
- The working-tree change to `.gitignore` adds `.claude`, which hides project-shared
  `.claude/settings.json` as well as personal `settings.local.json`. If only the latter is meant to
  be private, ignore `.claude/settings.local.json` instead.
- `README.md` still refers to `csv/…` paths and a `csv/` directory that does not exist in this repo.

---

## D11 — An empty CSV cell cannot clear a field, and says nothing about it — **FIXED**

**Severity: low-medium.** `hawaii-client/src/Csv.fs`, `hawaii-client/src/PatchShape.fs`

`update accounts --fields "id,description"` fed `616870,` (empty cell) issues **no** `PATCH` at all
and exits 0. The empty value becomes `None`, `PatchShape` strips it as unset, and the row is
classified as a no-op. There is no way to clear a nullable field through the CSV path, and no
diagnostic saying so — the user sees a successful run that changed nothing.

Found while repairing `tests-online/`: the mutation test overwrote an account's `description`, and
its restore step silently did nothing, leaving `__nocfo_test__` in api-tst while reporting PASS.
The harness now verifies that a restore actually landed and rejects an empty original value, and
the case was moved onto `number`, so the suite no longer depends on the answer here.

Two defensible readings, and the choice is a design decision rather than an obvious bug:

- **Empty means "leave unchanged"** (today's behaviour). Then an explicit `--fields` naming the
  column arguably deserves a warning when a named column is uniformly ignored.
- **Empty means "clear"**, with absence of the column meaning "leave unchanged". More expressive,
  and `--fields` already distinguishes the two cases, but it makes a truncated or half-filled CSV
  destructive.

**Resolved** as the second reading. In a PATCH the columns present are exactly the fields the
caller is speaking about, so within those columns an empty cell clears; a column the CSV does not
carry leaves the field alone, and a row the CSV does not list is never fetched, let alone written.
The rule applies only to the delta reader — `readCsvGeneric`, which feeds `create`, still treats an
empty cell as "nothing supplied", since there is nothing there to clear.

Clearing is expressed as the empty string, which is what the API stores for a blank text field.
Types with no empty value (`opening_balance`, the enums, `name_translations`) cannot be cleared this
way, so an empty cell in one of their columns is now a `CsvFormatException` naming the column rather
than the silent no-op that hid this defect.

Verified against api-tst: `update accounts --fields "id,description"` fed `616870,` clears the
description. Covered by three cases in `tests/CsvTests.fs` and one in `tests/EntityOpsTests.fs`
asserting the `PATCH` body carries `"description":""`.

---

## Suggested order

1. **D1 + D2** together — they are the same user-visible symptom and touch the same code.
2. **D3** — silent data loss; small fix, needs a test.
3. **D10** — one line, removes a privacy risk.
4. **D4, D5** — small, independent.
5. **D6** — needs a small design decision about where field-awareness lives.
6. **D7, D8, D9** — cleanup; do D9 before starting `PLAN-rolling-balance.md`.
