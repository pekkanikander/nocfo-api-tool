# NoCFO API Tool — Roadmap

Moving from late-exploration to early production quality.
The pattern is settled; this roadmap fills in the gaps that were consciously deferred.

---

## Phase 1 — Solid Foundation (do before anything else) - DONE

### 1.1 Error handling & retry — Done.

### 1.2 Unit test project for pure modules - Done

### 1.3 Create top level `.slnx` — Done

### 1.4 Migrate to a new clean repository — Done

## Phase 2 — Usability (make it safe and convenient to run)

### 2.1 Dry-run mode — Done

### 2.2 Named configuration profiles — Done

### 2.3 Domain module de-duplication (SRTP genericisation) — Done

---

## Phase 3 — Distribution (remove the "needs .NET SDK" barrier)

### 3.1 Self-contained binary builds — Done

Goal: produce a single executable that runs without a .NET SDK or runtime installed.

#### Approach

Self-contained, single-file JIT publishing (not Native AOT — see audit below).
Properties added to `tools/tools.fsproj`:

```xml
<AssemblyName>nocfo</AssemblyName>
<PublishSingleFile>true</PublishSingleFile>
<PublishReadyToRun>true</PublishReadyToRun>
<PublishTrimmed>false</PublishTrimmed>   <!-- trimming unsafe; see audit below -->
```

Publish via `make publish` (see `Makefile`), which runs:

```bash
dotnet publish tools -r osx-arm64 --self-contained -o dist/osx-arm64
dotnet publish tools -r linux-x64 --self-contained -o dist/linux-x64
dotnet publish tools -r win-x64  --self-contained -o dist/win-x64
```

Expected artefact size: ~70–90 MB per platform (JIT runtime + FSharp.Core + CsvHelper;
no trimming). On first launch .NET extracts native libs to a temp directory — acceptable
for a developer tool.

#### Native AOT audit — conclusion: not viable in 3.1

`PatchShape<'Full,'Patch>` uses `FSharpType`/`FSharpValue` over generic type parameters:

- `FSharpType.GetRecordFields(typeof<'Full>)` — record field discovery at runtime
- `FSharpValue.PreComputeRecordConstructor(typeof<'Patch>)` — dynamic constructor compilation
- `FSharpValue.GetUnionFields` / `MakeUnion` — option-type introspection

For Native AOT, the trimmer must see every type accessed via reflection at publish time.
F# does not support `[<DynamicallyAccessedMembers>]` on generic type parameters (a C#-only
mechanism as of .NET 10), so neither `'Full` nor `'Patch` can be annotated, and the trimmer
will silently remove the record fields and constructors that PatchShape uses at runtime.

Native AOT becomes viable only after PatchShape is rewritten as SRTP `inline` functions
(types resolved statically at compile time; no reflection, no trimmer problem). That rewrite
is a natural follow-on to the Phase 2.3 genericisation work and is the preferred eventual path.

### 3.2 GitHub Actions CI pipeline — Done

Triggers: push to main, pull requests

Jobs:

1. `build` — `dotnet build` for all projects
2. `test` — `dotnet test tests/`
3. `publish` — matrix over three RIDs, upload artifacts to GitHub Release on tag

Why after 3.1: CI is much easier to set up clean in the new repo than to retrofit here.

---

## Phase 4 — Completeness (finish what was deferred during exploration)

### 4.1 Resolve the Hawaii fork — Done

The main Hawaii generator has been updated and retargeted to `net10.0` a.


### 4.2 Complete missing CLI commands — Done

Command matrix (list/update/delete/create × businesses/accounts/contacts/documents):

| | list | update | delete | create |
| --- | --- | --- | --- | --- |
| businesses | ✅ | ✅ | — | ✅ |
| accounts | ✅ | ✅ | ✅ | ✅ |
| contacts | ✅ | ✅ | ✅ | ✅ |
| documents | ✅ | — | ✅ | ✅ |

`delete businesses` intentionally omitted.
`update documents` deferred to 4.4.

### 4.3 Structured logging / observability — Done

- Ensure all progress/status output goes to stderr; data output to stdout (partially done)
- Add `--verbose` flag for HTTP-level debug output (request/response bodies, timing)
- Optional: `--log-format json` for machine-readable stderr (useful when scripting)

### 4.4 Update documents

`update documents` is the only remaining gap in the command matrix.
`PatchedDocumentInstanceRequest` exists in the generated types.
Implementation follows the account/contact `update` pattern:

- Add `DocumentDelta = { id: int; patch: PatchedDocumentInstanceRequest }` with `Create`
- Add `Document.fetchFull`, `Document.diffToPatch`, `Document.executeDeltaUpdates`
- Wire `update documents` in `Program.fs` and add `Documents` to the `update` dispatch

---

## Phase 5 — Broader API Coverage (when the above is stable)

The API has grown to 73 paths / 159 schemas. Currently covered: businesses, accounts,
contacts, documents (list/update). Not yet covered:

- Transactions (read-only streaming, useful for reports)
- Accounting periods
- VAT returns
- Attachments (multipart upload — needs special handling)
- Balance sheet / P&L report folds (a `Reports.fs` stub exists)

Prioritise based on user need, not completeness for its own sake.

---

## Sequencing Summary

```
Phase 1: Http errors → Unit tests → New repo
Phase 2: Dry-run → Config profiles
Phase 3: Self-contained binary → CI
Phase 4: Hawaii → Missing commands → Logging
Phase 5: Broader API coverage
```

Phases 1–3 are the path to "early production quality tool".
Phases 4–5 are "mature tool".

---

## Architectural Direction: SRTP over Code Generation

### The option considered and set aside

A "super-Hawaii" generator was considered: a tool that reads an OpenAPI spec and emits a
complete F# CLI with CSV import/export for all entity types, replacing most of the
hand-written code in this repo. Several architectures were evaluated:

- **Extend Hawaii directly** — single tool, but pollutes the generator with two concerns;
  makes upstreaming patches harder;
- **Pipeline (Hawaii + second-pass CLI generator)** — clean separation; Hawaii unchanged;
  new generator emits `Streaming.fs`, `Arguments.fs`, `Dispatch.fs` per entity
- **Full standalone generator** — maximum control, but reimplements everything Hawaii
  already handles (nullable types, enum robustness, JSON quirks)
- **Template-based rendering** — good for simple scaffolding, awkward for type-relationship
  inference; best as a rendering layer within one of the above, not a primary architecture
- **Runtime interpretation** — no code generation, dynamic CLI from config; loses Argu's
  compile-time type safety and makes debugging harder

The pipeline approach (Option B) was the strongest technically: Hawaii handles the hard
API-layer problems; a new tool handles CLI generation; static runtime files (Http.fs,
AsyncSeq.fs, Streams.fs, PatchShape.fs, Csv.fs) are checked in and never generated.
An annotation file alongside the OpenAPI spec would capture what OpenAPI cannot express
(tenant scope parameter, identity keys, CSV exclusions, custom type converters).

### Why it was set aside

The generator is only worth building if it will be applied to multiple different APIs.
For a NoCFO-specific tool the upfront cost outweighs the ongoing savings — especially
since the hand-written code is already small and well-structured.

### The chosen direction instead: SRTP and generic functions

The codebase should evolve toward maximally generic F# using SRTP and `inline` functions,
so that adding a new entity type costs almost nothing — just a type definition and a few
lines of wiring. `paginateByPageSRTP` in `AsyncSeq.fs` is the established proof of concept.

Target end state:

- `Http.fs`, `AsyncSeq.fs`, `Streams.fs`, `PatchShape.fs`, `Csv.fs` — fully generic,
  no entity-specific code
- `Domain.fs` — type definitions and the minimal entity-specific logic that SRTP cannot
  absorb (e.g. semantic field interpretation)
- `Arguments.fs` — NoCFO-specific CLI structure
- `Program.fs` — small orchestrator

Next candidates for genericisation (in rough priority order):

1. Per-entity streaming wiring currently duplicated across entities in `Domain.fs`
2. CSV field mapping in `Csv.fs`
3. `PatchShape.fs` — reflection is acceptable to keep; SRTP for record fields is verbose
   enough that the reflection approach remains more readable

This approach delivers most of the code-generation benefit (new entity = trivial addition)
without building or maintaining a generator.
