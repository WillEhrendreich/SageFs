# Live Testing Golden Fixtures

These JSON files are the cross-client contract anchors for live-testing explanation payloads.

They are intentionally small, human-readable, and semantic. Each fixture exists to preserve a **why** case that downstream clients must continue to understand.

**These files are generated, not hand-authored.** `SageFs.Tests/SseGoldenFixtureGenerationTests.fs`
calls the real `SageFs.SseWriter.format*Event` functions (with the exact `JsonSerializerOptions`
production wires up — no naming-policy transform) and fails if a checked-in file here differs from
what that call produces right now. Do not hand-edit these files: a field rename, an added field, or a
DU shape change to `TestSummary` / `LiveTestingDecision` / `TestResultsBatchPayload` (or anything they
carry) will make that test fail, and the fix is to review the diff and, if it's a deliberate change,
regenerate with `SAGEFS_UPDATE_SSE_FIXTURES=1 dotnet run --project SageFs.Tests -- --filter-test-list "SSE golden fixture generation" --summary`.

Before this file-generation gate existed, two of the three fixtures below (`summary-with-*`) were
shaped after `SseWriter.formatTestSummaryEvent` — a function no production code path actually calls
(only `formatTestSummaryEventWithDiscovery` is wired into `McpServer.fs`) — so they were missing the
`Enabled`/`NotYetRun`/`Activity`/`ActivityText`/`ActivityShort`/`DiscoveryState`/`DiscoveryGeneration`
fields the real event carries, and the batch fixture wrapped `TestId`/`Generation` in a DU `Fields`
envelope and left `Framework`/`Category`/`CurrentPolicy` as bare strings when the real
`JsonFSharpConverter` output unwraps single-case DUs (`TestId`, `RunGeneration`) to a bare value and
wraps multi-case nullary unions (`Framework`, `Category`, `RunPolicy`) as `{"Case":"X"}`. All three are
now the actual server output, pinned.

## Fixtures

### `summary-with-fallback-decision.json`
Represents a live-testing summary where SageFs could not narrow a compiled-file change and therefore queued a conservative rebuild.

This fixture guarantees clients keep understanding:
- `Precision = conservative_fallback`
- `Trust = fresh_approximate`
- a user-facing reason like `fallback rebuild`

### `results-batch-with-coverage-decision.json`
Represents a test-results batch where coverage widened the impacted set beyond the dependency graph alone.

This fixture guarantees clients keep understanding:
- `Precision = coverage_approximation`
- a widened selected-test set
- batch parsing plus explanation parsing together

### `summary-with-suppressed-decision.json`
Represents a live-testing summary where ambient work stayed quiet because run policy intentionally deferred the affected tests.

This fixture guarantees clients keep understanding:
- `Precision = suppressed_by_policy`
- `Trust = suppressed`
- deferred tests remain named
- silence is explained as intentional, not accidental

## Why these live here

The server owns the contract shape, but multiple clients consume it:
- SageFs tests
- VS Code
- Visual Studio
- Neovim

Keeping the fixtures in one place reduces drift between those clients.

## When to add a new fixture

Add a fixture when the contract gains a new **semantic explanation mode**, not just when a field is renamed.

Good reasons:
- a new selection precision
- a new trust/freshness interpretation
- a new kind of user-visible “why did this happen?” explanation

Bad reasons:
- incidental formatting churn
- redundant copies of the same semantic case
