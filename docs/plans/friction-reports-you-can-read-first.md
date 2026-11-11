# Friction reports you can read first

Roadmap item: `friction-reports-you-can-read-first` (Later, Dashboard). Plan written 2026-10-26 against master 59a8692c.

## Work order

Written 2026-11-11 against master 8975e898 (0.6.945). Every `path:line` below was read that day; where the older plan under this section disagrees, this wins. Both items this plan depended on are Done: `dashboard-follows-context` (`PanelContext.decide`, `PanelPresence`) and `more-settings-editable` (0.6.940), so nothing blocks it.

### 1. Outcome

Before a friction report leaves the machine, the dashboard shows the exact JSON that will be sent, its byte count and its SHA-256, and where it goes. Three destinations: an endpoint you name (https, or http on loopback), a prefilled GitHub issue (the browser opens the form, the daemon sends nothing), or a JSON file download. Send sends the previewed bytes; if the store changed since the preview, the send is refused and a new preview is shown. The receipt keeps the hash. Outcome gate: an `--integration-browser` journey with a loopback `HttpListener` receiver that compares the body it received to the `<pre>` text the page showed, byte for byte, over {empty store, events only, feedback full of paths, emails, IPs and hex ids, a store at the 5000-row cap} x {endpoint, GitHub link, file} x {375, 1400 px}. Linux only; nothing here is platform-specific.

### 2. Where the code is today

- Store: `SageFs/FrictionSqlite.fs` API at `:44-49` (`AppendEvent`, `AppendFeedback`, `RecordSentReport`, `ListSentReports`); the `PRAGMA user_version` migration pattern at `:309-330`, currently two steps; `SentReport.DestinationKind` is a `string` (`:14`), written at `:494`, read at `:519`. Tests: `FrictionSqlitePersistenceTests.fs`, `FrictionSqliteEndToEndTests.fs`.
- Sanitiser: `SageFs.Core/Features/FrictionSanitize.fs`: `MaxTextLen = 200` (`:15`), `sanitizeText` (`:48`), `CurrentSchemaVersion = 1` (`:113`), `toOutgoing` (`:120`) stamping `DateTimeOffset.UtcNow` inside at `:134`, so two builds of one report differ. Tests: `FrictionSendSafetyTests.fs`.
- View model: `SageFs/FrictionReviewView.fs` (module `SageFs.Features.FrictionReviewView`): `build` (`:45`), `withEdits` (`:59`), `parseEditsJson` (`:78`), `buildOutgoingForSend` (`:103`). Tests: `FrictionReviewViewTests.fs`.
- Panel: `renderFrictionPanel` (`SageFs/DashboardFragments.fs:3150`); the Send button's hand-written script at `:3227` gathers `.friction-edit` textareas into a hidden input and posts `/dashboard/friction/send`; `Ds.bind Signals.FrictionEdits` at `:3234`. Signals `FrictionEndpoint`, `FrictionToken` at `SageFs/DashboardTypes.fs:388-389`. It never shows the JSON.
- Send: `isAllowedFrictionEndpoint` (`SageFs/Dashboard.fs:2980`), `frictionPayloadJson` (`:2992`), `createFrictionSendHandler` (`:3008`), route at `:3952`. It rebuilds the report at send time and writes `DestinationKind = "cloudflare-worker"` for every endpoint (`:3079`). Post timeout `Timeouts.frictionReportPost` (`SageFs.Core/Timeouts.fs:259`, 15 s).
- Visibility: `QuietBecause.FrictionNotRequested` (`SageFs/DashboardPanelContext.fs:140`, text `:197`), `PanelFacts.frictionOptInOfQuery` (`:222`), the decision at `:260`, `decisions` (`:312`); the opt-in is recorded per page at `Dashboard.fs:3790`. Per-page state: `PageChoices<'v>(capacity)` with `Set`, `Update`, `Find` (`SageFs/PageChoices.fs:18-67`).
- Report shape: every list truncated to 5 (`SageFs.Core/Features/FrictionTelemetry.fs:247-257`).
- Receiver: `friction-receiver/src/index.ts` mints a random id per post (`:231`, `makeReportId` at `:431`). It does not deduplicate a retry: that "Measure first" question is answered, and a lost reply after acceptance makes two reports unless the receiver changes.
- MCP: `manage_local_data` (`SageFs/McpTools.fs:1784`), `get_friction_summary` (`:1866`); `docs/mcp-tools.md:519` says "They don't phone home".
- Settings: `SettingValue` (`SageFs.Core/Settings.fs:287`) has no URL case; a persisted endpoint needs a new `VEndpoint` whose parser is `isAllowedFrictionEndpoint` moved into Core.
- `.github/ISSUE_TEMPLATE` does not exist. `[not verified]`: GitHub's limit on an issue-form URL length.

### 3. Steps

1. **Pure.** RED `SageFs.Tests/FrictionSendTests.fs`, default tier: "WHY - the bytes the transport receives are the bytes the preview showed" and "WHY - a send for a superseded preview is refused and the transport sees nothing". Register in `SageFs.Tests.fsproj` beside `FrictionSendSafetyTests.fs` (`:1020`). Then `toOutgoing` takes `now: DateTimeOffset` (callers: `buildOutgoingForSend`, the handler) and new `SageFs/FrictionSend.fs` (`Destination`, `PreviewId`, `Preview`, `SendRefusal`, `SendOutcome`, `prepare`, `send` over a transport function), compiled before `Dashboard.fs`. Property: `prepare` output has no sensitive pattern and parses back.
2. **Pure, parallel with 1.** `IssueLink.fs`: `build` returns `FullBody of Uri | SummaryOnly of Uri * copyInstruction`; host `github.com`, path the SageFs issues form, always. First do the measurement: build a report with every list at 5 and every text at 200 percent-encoding characters, print the URL-encoded length from the REPL, and compare it with what GitHub accepts (open the link once in a browser; nobody has written the limit down). The result decides which case is common.
3. **Edge.** Migration `user_version = 3` in `FrictionSqlite.fs`: `sent_reports` gains `payload_sha256` and `destination`; `DestinationKind` becomes a DU (`Endpoint | GitHubIssue | File`) with one `toString`, and `"cloudflare-worker"` reads as `Endpoint`. RED: `FrictionSqliteMigrationTests.fs`, a database at version 2 opens, migrates, keeps rows.
4. **Edge.** `SageFs/FrictionSendRoute.fs`: `POST /dashboard/friction/preview`, `/edit`, `/send`, the pending `Preview` per page in a `PageChoices<Preview>`; `createFrictionSendHandler` moves out of `Dashboard.fs` (which frees lines against its 4,259 budget). Transport is `HttpClient` with `Timeouts.frictionReportPost`.
5. **Edge.** The panel: `<pre>` with the JSON, byte count, hash, destination chooser, three actions, `Ds.indicator Signals.FrictionSending`; the textareas post edits through Datastar bindings and the `:3227` script goes. Fragments has two lines of budget (4,457 of 4,459): write the new panel in a new `FrictionPanelView.fs` and leave one call in Fragments.
6. **Edge.** `VEndpoint` setting `friction.reportEndpoint` in the catalog; the token stays a per-send signal.
7. **DECISION, then text.** Visibility: show the panel collapsed whenever `Events + Feedback > 0` (recommendation), or keep it behind `?panels=friction`. Then `docs/mcp-tools.md:519` gains "a report leaves only when you press Send, after you have read it". Trailer `Plugin: n/a dashboard only, no wire change` on commits touching `DashboardTypes.fs` or `docs/mcp-tools.md`.
8. **Pure, then edge.** `SageFs.Simulation/FrictionSendSim.fs` with the rebuild-at-send twin; then the browser journey from section 1.

### 4. How to check it

- REPL first: a session on `SageFs.Tests`; `send_fsharp_code` `FrictionSanitize.toOutgoing` twice on one report and show the bytes differ, before step 1's test.
- `dotnet build SageFs.Tests -c Release`; `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Inner loop: `... SageFs.Tests.dll --summary --filter-test-list "Friction"`; read `ran=`.
- Acceptance: unfiltered `--summary`; `--integration-browser` unfiltered for the journey.

### 5. Traps in this repo

- `Dashboard.fs` 4,256 of 4,259 and `DashboardFragments.fs` 4,457 of 4,459 (`RatchetBudgets.fs:343-344`): new code goes in new files.
- `SageFs.Json` only (`JsonCentralizationTests`): `frictionPayloadJson` already uses `Json.camelCase`; `Preview.Json` must come from the same call, never a second serializer.
- No magic strings: `DestinationKind` as a DU, `Destination` as a DU; the GitHub URL built from parts, not a literal with holes.
- No timing-sensitive tests: the receiver signals a `TaskCompletionSource`; the journey waits on it with `Started.asTask`, never a stopwatch.
- `TimeoutLiteralsTests`: the preview's staleness window, if any, is a `Timeouts` constant.
- Commit `SageFs.fsproj` and `SageFs.Tests.fsproj` whole, with a pathspec.

### 6. Size and order

A week and a half for one person. 1 and 2 in parallel; 3 beside them; 4 after 1 and 3; 5 after 4; 6 and 7 after 5; 8 last. First commit: `test(friction): the bytes the transport receives are the bytes the preview showed`, RED.

### 7. Not now

- A hosted SageFs receiver: the Worker in `friction-receiver/` exists; nobody runs one by default, and the GitHub path makes that fine.
- Receiver-side deduplication (an idempotency key): a change to `friction-receiver/src/index.ts`, after someone runs a receiver.
- An `ISSUE_TEMPLATE` with labels: nice for triage, not needed for the prefilled link.
- Building the hidden panel only when shown: measure `reportDirect` on a 5000-row store first (the plan's second "Measure first"); if it is under a millisecond, leave it.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: the bytes the transport gets are the bytes the preview showed. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

When you choose to send a friction report, the dashboard first shows the exact bytes that will leave the machine, their size,
and where they will go. You pick one of three destinations: your own receiver endpoint, a prefilled GitHub issue (nothing is
sent by the daemon; your browser opens the issue form with the text already in it), or a saved JSON file. Send then sends
those previewed bytes and nothing rebuilt. If anything changed between preview and send, the send is refused and a fresh
preview is shown. A receipt stores the SHA-256 of the sent bytes. The outcome gate is a browser journey with a loopback receiver
that compares the body it received with the text the page showed, byte for byte, over a store matrix: {empty, events only,
feedback full of paths, emails, IP addresses and session ids, a store at its row cap} x {endpoint, GitHub issue, file} x {375
and 1400 px wide}.

## What exists today

Verified by reading the code.

- Capture and storage: `SageFs/FrictionSqlite.fs` (`FrictionStore`: `AppendEvent`, `AppendFeedback`, `ReadEvents`,
  `ReadFeedback`, `RecordSentReport`, `ListSentReports`, `Prune`, `Usage`, `Clear`) over `friction.db` in the data dir.
  Schema versioning is `PRAGMA user_version`, currently 2, with additive `ALTER TABLE` steps in `Store.create`.
  `SageFs.Core/Features/LocalDataRetention.fs` and `Timeouts.fs` set the bounds: 30 days, 5000 rows per table, 20 version
  aggregates (`SAGEFS_FRICTION_MAX_AGE_DAYS`, `_MAX_ROWS`, `_MAX_AGGREGATE_VERSIONS`), pruned on start and every
  `SAGEFS_DATA_PRUNE_INTERVAL_MINUTES` (60). So "local and bounded" is true today.
- Reading: the MCP tools `get_friction_summary`, `get_friction_report`, `report_friction`, `manage_local_data`
  (`SageFs/McpTools.fs`, `manage_local_data` at line 1784 and `get_friction_summary` at 1866 on 2026-11-11; `docs/mcp-tools.md` line 519 says "They don't phone home").
  `McpFrictionRecorder.Recorder.reportDirect` builds `FrictionReportWithSignals` (the report plus observed signals).
  `FrictionTelemetry` truncates each list in the report to 5 (`FrictionTelemetry.fs` lines 244 to 254), so the report is bounded.
- Sanitising: `SageFs.Core/Features/FrictionSanitize.fs`. `sanitizeText` redacts Windows, UNC and Unix paths, IPv4, emails and
  8+ character hex ids and caps text at 200 characters. `toOutgoing` builds the `OutgoingReport` (schema version 1). It stamps
  `SubmittedAtUtc = DateTimeOffset.UtcNow` inside the function, so two builds from the same data differ.
- View model: `SageFs/FrictionReviewView.fs` (`FrictionReviewSnapshot`, `build`, `withEdits`, `parseEditsJson`,
  `buildOutgoingForSend`).
- Panel: `renderFrictionPanel` (`DashboardFragments.fs` line 3150 on 2026-11-11). It shows a compact preview (top tools, editable reason
  textareas), an endpoint input and optional token input bound to `Signals.FrictionEndpoint` and `FrictionToken`, and a Send
  button whose click gathers edits with a hand-written JavaScript string in `Ds.onEvent` and posts `/dashboard/friction/send`.
  It never shows the JSON.
- Send: `createFrictionSendHandler` (`Dashboard.fs` line 3008 on 2026-11-11). It validates the endpoint (`isAllowedFrictionEndpoint`: https,
  or http to loopback), rebuilds the report from the store at send time, serialises with `frictionPayloadJson`
  (`Json.camelCase`), posts with `X-SageFs-Token`, and records a `SentReport` only after remote acceptance and a successful local
  write. The receipt's `DestinationKind` is the literal `"cloudflare-worker"` for every endpoint, and `DestinationUrlHash` is the
  endpoint's SHA-256, so the URL is never stored.
- Receiver: `friction-receiver/` is a Cloudflare Worker (R2 plus optional Discord) in this repository. It checks
  `schemaVersion`, returns `{ reportId, key }`. There is no SageFs-hosted instance configured by default.
- Visibility: the panel is `OptionalPanel.Friction`, hidden unless the tab opened `/dashboard?panels=friction`
  (`PanelFacts.frictionOptInOfQuery`), with the reason line "hidden until reporting has a real endpoint". The snapshot builder
  still runs the SQLite read and the observed-signal pass on every uncached build, then `PanelVisibility.apply` discards it.
- Tests: `FrictionReviewViewTests.fs`, `FrictionSendSafetyTests.fs` (endpoint allow-list, sanitiser), `Friction*Sqlite*Tests.fs`.
  I found no browser test of the panel.
- `.github/` has `PULL_REQUEST_TEMPLATE.md` and `workflows/`, no `ISSUE_TEMPLATE`.

So the roadmap's "show the exact payload first" is not there: the panel shows a summary, and the bytes are built again at send.

## Design

Types, in a new vertical slice `SageFs/FrictionSend.fs` (pure core plus a transport port):

```fsharp
type Destination =
  | ToEndpoint of endpoint: Uri          // only after isAllowedFrictionEndpoint
  | ToGitHubIssue                        // the daemon makes no network call
  | ToFile                               // a download link; nothing leaves the daemon

type PreviewId = PreviewId of string      // minted per preview

type Preview =
  { Id: PreviewId
    Json: string                          // the exact text shown
    Bytes: byte[]                         // UTF-8 of Json; what is sent
    Sha256: string
    BuiltAt: DateTimeOffset
    Events: int
    Feedback: int }

type SendRefusal =
  | NoPreviewForThisPage
  | PreviewSuperseded                     // a newer preview exists for this page
  | NothingToSend
  | DestinationNotAllowed of why: string

type SendOutcome =
  | Sent of receipt: SentReport
  | Refused of SendRefusal
  | Failed of reason: string * retry: RetryAdvice   // RetrySame | EditThenRetry | DoNotRetry
```

`FrictionSanitize.toOutgoing` takes `now: DateTimeOffset` as a parameter (clock injection), so the timestamp is part of the
preview and the bytes are reproducible in a test. `FrictionSend.prepare now report edits : Preview` builds once.
`FrictionSend.send (transport: Destination -> byte[] -> Task<TransportResult>) preview destination` hands the transport
`preview.Bytes` and nothing else. The daemon holds the pending preview per page in a `PageChoices<Preview>` keyed by client
id (`PageChoices.fs`, bounded). The Send POST carries the `PreviewId` and the destination; the server finds the page's preview,
refuses on mismatch, and sends its bytes. Editing a reason posts the edit, rebuilds the preview, and morphs the `<pre>`.

GitHub issue: a pure `IssueLink.build report` returns `FullBody of Uri | SummaryOnly of Uri * copyInstruction`, targeting
`https://github.com/WillEhrendreich/SageFs/issues/new` with `title` and `body` query parameters, rendered as
`<a target="_blank" rel="noopener noreferrer">`. A report too long for one URL puts a summary in the link and offers a "copy the
full JSON" button for the clipboard, with the instruction to paste it. The preview says plainly that an issue is public. A
markdown `ISSUE_TEMPLATE` is optional and not required for the prefilled link.

Endpoint: today the URL is typed every time. Add a setting `friction.reportEndpoint` to the Settings panel
(`more-settings-editable`; a value type that parses through `isAllowedFrictionEndpoint`, so an illegal URL is unrepresentable).
The token stays a per-send signal and is never persisted.

Persistence: `sent_reports` gains `payload_sha256 TEXT NOT NULL DEFAULT ''` and `destination TEXT NOT NULL DEFAULT ''`
through a `user_version = 3` step in `Store.create`, the same additive pattern as steps 1 and 2. Old rows keep empty values; the
panel shows "sent before payload hashes were kept". `DestinationKind` becomes a DU with one exhaustive to-string
(`Endpoint | GitHubIssue | File`), and the old literal `"cloudflare-worker"` is read as `Endpoint`.

Visibility: once a report can go somewhere without any hosted endpoint, the "no real endpoint" reason no longer holds. Keep the
panel closed by default and show it, collapsed, when `Events + Feedback > 0`, with the one-line state from
`dashboard-follows-context` (`FrictionNotRequested` becomes `NothingRecordedYet`). `?panels=friction` keeps working.

Bounded and honest: the panel header shows `FrictionStore.Usage` (rows, bytes, oldest row) and the retention rule in one line, so
"local and bounded" is something you can see, plus the existing `manage_local_data` clear.

Cost: the snapshot builder should read the store only when the panel is shown (see Measure first).

Wire and clients: dashboard-internal. `/dashboard/friction/send` is not in `EndpointContracts.fs`; `apiVersion` stays 4;
`sagefs.nvim` and `sagefs-vscode` have no friction panel. No new MCP tool: `get_friction_report` already returns the report, and
a tool needs a case this plan does not have. A commit that edits `docs/mcp-tools.md` or `DashboardTypes.fs` carries the trailer
`Plugin: n/a dashboard only, no wire change`. The line in `docs/mcp-tools.md` "They don't phone home" stays true and gains a
sentence: a report leaves only when you press Send, after you have read it.

Files: new `FrictionSend.fs`, `FrictionSendRoute.fs` (moves the handler out of the 3816-line `Dashboard.fs`), `IssueLink.fs`;
changed `FrictionSanitize.fs`, `FrictionReviewView.fs`, `FrictionSqlite.fs`, `DashboardFragments.fs` (friction region),
`DashboardTypes.fs` (signals), `Dashboard.fs`, `SageFs.fsproj`, `docs/mcp-tools.md`.

## Tests

RED first.

1. `FrictionSendTests`: "the bytes the transport receives are the bytes the preview showed". A fake transport records the bytes;
   `prepare` then `send`; assert byte equality and equality of `Sha256`. Compile-red.
2. "a send for a superseded preview is refused and sends nothing". Two previews on one page; the first id is refused; the
   transport was not called.
3. Property over generated feedback strings built from path, email, IPv4, UNC and hex-id fragments mixed with ordinary words:
   `prepare` output contains none of the sensitive patterns, never exceeds `MaxTextLen`, and `Json` parses back to the same
   report (extends the existing sanitiser properties in `FrictionSendSafetyTests.fs`).
4. `IssueLink.build`: decoded `body` equals the preview text when it fits; when it does not, the link carries a summary and a
   copy instruction; the host is `github.com` and the path is the SageFs issues form, always.
5. `FrictionSqliteMigrationTests`: a database at `user_version = 2` opens, migrates to 3, keeps its rows, and records a receipt
   with a hash. A fresh database has the same columns.
6. Mutation: a `Mutant` that rebuilds at send (the current behaviour) and one that skips the destination check; tests 1 and the
   existing endpoint tests must fail.

Simulation (`SageFs.Simulation/FrictionSendSim.fs`, `FrictionSendSimInvariants.fs`; tests `FrictionSendSimDstTests.fs`, after
`RetentionSim.fs`). Pool: two pages, one store, one transport. Ops: `RecordEvent`, `RecordFeedback`, `Prune`, `Preview(page)`,
`EditReason(page)`, `Send(page, destination)`, `RetryAfterFailure`, `DaemonRestart`. Faults from `FaultInjection.fs` on the transport:
drop the request, drop the reply after the receiver accepted, return 5xx, duplicate. Invariants: (I1) the receiver's bytes
equal the sending page's preview bytes at the time of Send; (I2) a receipt exists exactly when the transport reported acceptance
and the local write succeeded, and it carries the hash of those bytes; (I3) a Send without a current preview for that page is
refused and the transport sees nothing; (I4) after any `Prune` row counts are within the caps; (I5) no received payload matches a
sensitive pattern. Twin: rebuild at send, which violates I1 as soon as a `RecordEvent` falls between `Preview` and `Send`. The
clock is injected; no real waits.

Outcome gate: `DashboardFrictionBrowserTests.fs`, own daemon and `SAGEFS_DATA_DIR` (`--integration-browser`), a loopback
`HttpListener` as the receiver (`http://127.0.0.1:<port>` is allowed by `isAllowedFrictionEndpoint`). The store is seeded through
`FrictionStore.AppendEvent` / `AppendFeedback` before the daemon starts. Waits: `waitForSSE` and selector-text waits; the receiver
signals a `TaskCompletionSource` when a body arrives, and the test awaits it with `Task.WhenAny` against a progress-based
budget. Zero console and `PatchElementsNoTargetsFound` errors.

## Steps

1. RED tests 1 and 2. Add `now` to `toOutgoing` and its callers; add `FrictionSend.prepare`/`send` with a fake transport.
   Owns `FrictionSanitize.fs`, `FrictionReviewView.fs`, `FrictionSend.fs`, their tests.
2. Migration 3 and `Destination` in the receipt. Owns `FrictionSqlite.fs` and its tests. Parallel with 3.
3. `IssueLink.fs`, with the length experiment below. Owns `IssueLink.fs` and tests. Parallel with 2.
4. `FrictionSendRoute.fs`: preview, edit and send routes using `PageChoices<Preview>`, real transport with the existing
   `Timeouts.frictionReportPost`. Owns `FrictionSendRoute.fs`, `Dashboard.fs` (route list only).
5. Panel: `<pre>` preview, byte count, destination chooser, three actions, in-flight `Ds.indicator`, the edit textareas posting
   through Datastar bindings instead of the hand-written script. Owns the friction region of `DashboardFragments.fs`.
6. Settings entry for the endpoint (after `more-settings-editable` step 1).
7. Visibility change and the docs sentence, after Will's decision.
8. Simulation and twin; browser journey.

## Measure first

- How large is the worst-case `OutgoingReport`? Experiment: build a report with every list at its cap of 5, every text at 200
  characters of characters that percent-encode, serialise with `Json.camelCase`, measure the JSON and its URL-encoded length.
  Compare with the length GitHub accepts in an issue-form URL. I have not checked that limit and will not assume one. The result
  decides between `FullBody` and `SummaryOnly` as the common case.
- What does the hidden friction build cost per snapshot? Time `reportDirect` plus `ListSentReports` on a store at the 5000-row cap.
  If it is over a millisecond per uncached push, build the panel only when shown.
- Does the receiver `friction-receiver` deduplicate a retried send? Answered 2026-11-11: no. `src/index.ts:231` draws a random
  id per post and `makeReportId` (`:431`) uses it, so an "accepted but reply lost" retry creates two reports; an Idempotency
  header on a retry would need the receiver changed too.

## Risks and dependencies

- A public GitHub issue publishes the sanitised report. The sanitiser is regex-based and a free-text reason can still carry a
  secret it does not recognise. The preview is the guard; the issue path should say "public" next to the button.
- Dropping the hand-written edit-gathering script changes how reasons reach the server; the new binding needs a per-field signal
  or a per-field post. Test with several feedback entries.
- Depended on `dashboard-follows-context` (the friction reason) and `more-settings-editable` (the endpoint setting); both are
  Done as of 0.6.940. Nothing depends on it.

## Size

M, about a week and a half: the pure core and the migration are small, the panel rewrite and the loopback journey are the work.

## Decisions for Will

- Show the panel collapsed whenever something is recorded, or keep it behind `?panels=friction` until the GitHub path has been
  used once by someone other than you? Recommend collapsed whenever recorded; it says nothing leaves until you press Send.
- Which GitHub repository receives the issue, and should there be an issue template with labels? Recommend the SageFs repository
  and a short template, so reports are easy to triage.
- Should the endpoint be a persisted setting at all, or typed per send? Recommend a persisted URL, token per send.
