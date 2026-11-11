# Claim an interface, not just a file

Roadmap item: `claim-an-interface` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

## Work order

Written 2026-11-11 against master 8975e898. Every `path:line` below was read at that commit; the "What exists today" section keeps its older "near line" numbers, corrected inline where they moved.

### 1. Outcome

A member calls `acquire_claim scope=contract:SageFs.Core/Cohort.fsi`. While it holds that claim nobody else can claim the `.fsi`, a save to it by anyone else raises the claim-violation advisory, and a landing that changes it is blocked with `ContractChangedWithoutDecision` unless the holder presented the claim and recorded a decision. A `.fs` that no longer satisfies the signature fails with the compiler's own error at landing. Gate: `SageFs.Tests/CohortContractOutcomeTests.fs` in `--integration-host`, real landings through git on a throwaway repo over two MCP connections, the matrix in "What done looks like", every cell a named case. The no-contract column must be byte-identical to today, because the repo has no `.fsi` and a lone user never sees any of this. Linux only; git behaviour elsewhere is not claimed.

Before any of it: "Measure first" 3 asks whether anyone needs this. Running it today: `git log --since='30 days ago' --format='%h %s' | grep -iE 'merge|conflict|resolve'` returns merge commits and generated-page conflicts (`9b729e27`, `e7914d22`), none of them a shape conflict between two members over one signature. `ClaimMissedCollision` is still not in the tree. DECISION 0: start the item at all now, or leave it Exploring until a cohort run produces a shape conflict. Recommend leaving it until `drive-real-agents-as-members` produces one, and doing step 1 only (the type and the overlap rule) if a dependent item (`plan-a-cohort-as-code`) wants the case.

### 2. Where the code is today

- `ClaimScope = File | Project` at `SageFs.Core/Cohort.fs:97-99`; the header comment at 89-90 says `Module`/`Symbol`/`Contract` are deferred. `ClaimPath.tryCanonical` 121, `canonicalOrRaw` 144, `ClaimScope.tryCanonical` 155, `overlaps` 175.
- Every exhaustive match the new case will break: `Capability.ScopePrefix.covers` `SageFs.Core/Capability.fs:92-93`; `SageFs.Core/Features/CohortLanes.fs:80-81`; `SageFs.Core/Features/CohortTerritory.fs:48-49`; `SageFs.Core/SseWriter.fs:566-569` (`claimScopeToWire`, `Kind = "file" | "project"`); `SageFs/McpCohortTools.fs:48-60` (`parseClaimScope`); `SageFs/CohortErrorMapping.fs:28-29` and 318-319; `SageFs/CohortInspector.fs:118-119`; `SageFs/CohortPlay.fs:72-73`; `SageFs/DashboardFragments.fs:3277-3278`.
- `LandingBlocker` at `Cohort.fs:390-400` (`RebaseConflict`, `FailingTests`, `StaleClaimFence`, `HeadMoved`, `VetoedBy`, `VerificationInconclusive`); the comment at 383-388 says `ContractChangedWithoutDecision` waits for a diff file list. `CohortCommand` 644, `AcquireClaim` 681, `ObserveSave` 718, `RequestLanding` 719, `VerificationInconclusive` 736. `ClaimConflict` 856, `StaleClaimFence` error 863. `validateLandingClaims` 1077 (holder and fence only), called at 1636. `blockAndPop` 1132.
- The file list exists and is dropped: `CohortGit.diffNames` `SageFs/CohortGit.fs:198`, called in the landing performer at `SageFs/DaemonMode.fs:2977` and 3095. `LandingPerformer` fields `SageFs.Core/Features/CohortOwner.fs:88-100`; `ComputeAffected` dispatched at 265-283.
- `ObserveSave` is posted from `DaemonMode.fs:4497` (advisory, never refuses).
- No `Decision` type for the ledger (`Cohort.fs:39-41` says it is "Phase 1 item 10, not built here"); the `Decision` types grep finds belong to other modules. The vision text is `~/Work/SageFs/sagefs-multiagent-vision.md`, untracked, section 5.2 at line 607.
- `git ls-files '*.fsi'` is empty.
- Compiler check: `CallerCheck.compilerAnswerAsync` `SageFs.Core/Features/CallerCheck.fs:197`, `ParseAndCheckProject` under `Timeouts.callerCheck` (`Timeouts.fs:653`). The worker holds `CheckOptions` (`SageFs.Host/WorkerMain.fs:1017`, 2418). `FSharpCheckFileResults.GenerateSignature: ?pageWidth: int -> ISourceText option` exists in the checked-out FCS (`~/Work/fsharp-compiler-services/src/Compiler/Service/FSharpCheckerResults.fsi:463`).
- `ReloadPlanning.SignatureChanged` `SageFs.Core/Features/ReloadPlanning.fs:97` is a hot reload change kind, unrelated to `.fsi` files.
- Wire: `EndpointContracts.apiVersion = 4` (`EndpointContracts.fs:38`); `sagefs-vscode/src/SageFsClient.fs:340` reads it; `sagefs.nvim/lua/sagefs/cohort.lua:275` parses the status text.
- Tests and fixtures: ledgers in `SageFs.Tests/cohorts/*.ledger.jsonl` (eight files, read by `CohortLedgerExportTests.fs:275`, `CohortPlayTests.fs:102`); sim `SageFs.Simulation/CohortVetoSpec.fs` (fsproj line 67, tests at `SageFs.Tests.fsproj:740`); `CohortMcpToolsIntegrationTests.fs:195` (`Integration.hostList`); `CapabilityHttpIntegrationTests.fs:172`. Mutation suites are per-module files (`CohortGitMutationTests.fs` is the cohort one).

### 3. Steps

1. RED `SageFs.Tests/CohortContractTests.fs` ("Tests" 1 and 2), listed in `SageFs.Tests/SageFs.Tests.fsproj` right after `CohortVetoSpecTests.fs` (line 740). Add `Contract of fsiRelativePath: string` at `Cohort.fs:99`, the overlap rule at 175 (`.fsi` only; DECISION 1), `tryCanonical` refusing a path not ending `.fsi`, and fix every match in the list above in the same commit. Domain, plus the print sites. The ledger fixtures replay unchanged because no row holds the new case.
2. `parseClaimScope` (`McpCohortTools.fs:48`) accepts `contract:`; `acquire_claim` description in `McpTools.fs` (around 2755) gains the form. Wire: SSE `Kind = "contract"` and `contract:<path>` in status text. Plugin first: `cohort.lua`'s `parse_claim` must read the new prefix (a busted spec in `sagefs.nvim/spec/cohort_spec.lua`), then the daemon commit with `Plugin: done`. DECISION 2: `apiVersion` 4 to 5. Recommend 5, as the plan says.
3. `RecordDecision` / `DecisionRecorded` and `ContractDecision` state (`Cohort.fs`), "Tests" 3 to 5 decision half. Domain. Keep `DecisionKind` open for later cases.
4. `ChangedFilesObserved`, the blocker, `LandingPerformer.ChangedFiles` (`CohortOwner.fs:88`), the dispatcher ordering at 265 (file list first, then affected tests, inside one `Async.Start`), wired to `CohortGit.diffNames` in `DaemonMode.fs:2977`. `LandingPerformer.stub` (line 122) gets a never-completing twin. Edge. After 3.
5. `SageFs.Core/Features/ContractCheck.fs` and "Tests" 6, mirroring `CallerCheck.fs:197`. New files only; parallel with 3 and 4. Then the landing-gate call in `DaemonMode.fs` after 4.
6. Sim `SageFs.Simulation/CohortContractSpec.fs` (shape of `CohortVetoSpec.fs`) and the outcome gate.
7. Per-save check in the member session (`WorkerMain.fs`), only if "Measure first" 1 says it is cheap. DECISION 3 on `contract_decision` as a `request_landing` parameter: recommend yes.
8. Docs: `docs/mcp-tools.md`, `docs/agents.md`, `sagefs.nvim` display, `sagefs-vscode` tolerance.

### 4. How to check it

- Inner loop: a SageFs session on `SageFs.Tests`; `Cohort.decide` is pure, so every command test runs as an eval.
- "Measure first" 1 by REPL: in a session on `SageFs.Core`, take the worker's `CheckOptions`, write `Cohort.fsi` from `GenerateSignature`, time `ParseAndCheckProject` cold and warm. Under about 5 s warm means step 7 is affordable.
- `dotnet build SageFs.Tests -c Release`; `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Filtered: `--summary --filter-test-list "CohortContract"`.
- Acceptance: unfiltered `--summary` (pure, sim, fixtures), `--integration-host --summary` for the outcome gate, `busted spec/cohort_spec.lua` in `~/Work/sagefs.nvim`.

### 5. Traps in this repo

- CohortCommand corpus fixtures: `SageFs.Tests/cohorts/*.ledger.jsonl` replay through `decide`; a new case is additive and must leave them green. A new field on an existing case would not.
- A wire change needs the plugin first and a `Plugin:` trailer (steps 2 and 4 touch `SseWriter.fs` and the status text).
- Tool-list weight (`RatchetBudgets.toolList`, `SageFs.Tests/RatchetBudgets.fs:462`): no new tool; two description edits only.
- SAGE-005: `RecordDecision of by * contract: string * statement: string * scope` has two strings side by side; wrap `contract` in a single-case type or order the parameters so the test catches a swap.
- Budgets only go down: the analyzer counts and `TimeoutLiteralsTests` must not grow; `ContractCheck` reuses `Timeouts.callerCheck` or adds a named constant.
- No timing-sensitive tests; the sim uses the injected clock, landings advance on performer completions.
- Adding the first `.fsi` to this repo changes how `ReloadPlanning` and hot reload read a file; run the hot reload parity tests once before the first real contract lands.

### 6. Size and order

M, two to three weeks for one person: steps 1 and 2 a day or two; 3 to 5 a week; 6 the rest. Step 1 can land alone and is what `plan-a-cohort-as-code` would need. First commit, if the item starts: `feat(cohort): a Contract claim scope over an .fsi, exclusive against the file and its project, no landing rule yet`.

### 7. Not now

- Symbol and module claims: the vision gates them on a collision counter that does not exist.
- The full decision log (`DesignChoice`, `Deferral`): this item adds one case; the type stays open.
- `check-the-scope-before-the-write`: the same enforcement for writes, shares `Capability.admitClaim` (`Capability.fs:898`); keep them apart so neither blocks the other.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: claim overlap is symmetric and total over every scope pair. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

A member calls `acquire_claim scope=contract:SageFs.Core/Cohort.fsi`. While it holds that claim, nobody else can claim the same `.fsi`, a save to it by anyone else raises the existing claim-violation advisory, and a landing that changes it is blocked with `ContractChangedWithoutDecision` unless the holder presented the claim and recorded a decision first. A member whose `.fs` no longer satisfies the signature sees the compiler's own error, in its own session and again at landing time. The blocker text says which decision to record and with which parameter.

The outcome gate runs real landings through git and two MCP connections. Its matrix is: change to the `.fsi` {by the holder with a decision, by the holder without one, by a non-holder}, a change to the `.fs` {satisfies the signature, violates it}, and the cohort {has no contract claim, has one, has one inside another member's `Project` claim}, each under {Implementer token, Verifier token, no token}. The no-contract column must behave exactly as today, because a lone user never sees any of this.

## What exists today

- `ClaimScope` has two cases, `File` and `Project` (`SageFs.Core/Cohort.fs`, near line 97). `ClaimScope.overlaps` is plain path arithmetic over `ClaimPath.canonicalOrRaw`. The module header says `Module`, `Symbol` and `Contract` are deferred, and that `ContractChangedWithoutDecision` waits for a diff file list that "only exists once the shell's `ComputeAffected` effect reports it back".
- The scope wire format is `file:<path>` or `project:<path>`, parsed in `parseClaimScope` (`SageFs/McpCohortTools.fs`, near line 48) and printed back by `SageFs/CohortErrorMapping.fs` (two sites), `SageFs/CohortInspector.fs`, `SageFs/CohortPlay.fs`, `SageFs/DashboardFragments.fs` (line 3277 at 8975e898) and `SageFs.Core/SseWriter.fs` (`claimScopeToWire`, `Kind = "file" | "project"`). `Capability.ScopePrefix.covers` and `CohortTerritory.labelFor` and `CohortLanes` match on the two cases as well. Adding a case breaks every one of those matches at compile time, which is the intended forcing function.
- A landing's claims are checked for holder and fence only (`validateLandingClaims` in `Cohort.fs`). Nothing checks that the landing's diff stays inside the claims. `ObserveSave` only warns (the daemon posts it from `DaemonMode.fs:4497` at 8975e898) and the claim doc says a claim cannot stop an edit.
- The performer for `ComputeAffected` already computes the changed file list: `Features.CohortGit.diffNames binding.WorktreePath baseSha headSha` (`SageFs/DaemonMode.fs:2977` and 3095, `SageFs/CohortGit.fs:198`). The list never reaches `decide`.
- There is no `Decision` type and no `record_decision` tool. I grepped `SageFs.Core` and `SageFs` and found none. The vision document (`sagefs-multiagent-vision.md`, section 5.2, untracked in the main checkout at `~/Work/SageFs`, so not in a fresh clone) sketches `Decision` and `DecisionKind.ContractChange`.
- The repository has no `.fsi` files (`git ls-files '*.fsi'` is empty).
- The compiler check exists in a reusable shape. `CallerCheck.compilerAnswerAsync` (`SageFs.Core/Features/CallerCheck.fs`) runs `ParseAndCheckProject` over an `FSharpProjectOptions` under `Timeouts.callerCheck` and reads `results.Diagnostics`. The worker already holds the options (`ActorCreation.CheckOptions`, `SageFs.Host/WorkerMain.fs` near line 949 uses them for callers). `Diagnostics.fs` line 109 uses `ParseAndCheckInteraction`, which checks one interaction and cannot see a signature file.
- Member tokens already confine claims by prefix (`Capability.admitClaim`).

## Design

Types, in `Cohort.fs`:

```fsharp
type ClaimScope =
  | File of repoRelativePath: string
  | Project of fsprojRelativePath: string
  | Contract of fsiRelativePath: string      // canonical, ends in ".fsi"

type DecisionId = DecisionId of string       // minted from entropy like ClaimId

type ContractDecision = { Id: DecisionId; By: 'm; Contract: string; Statement: Statement; Consumed: DecisionUse }
and DecisionUse = | Open | ConsumedBy of LandingId

// LandingBlocker gains:
| ContractChangedWithoutDecision of fsiPath: string
```

Overlap rules, pure and total. `Contract c` overlaps `Contract c'` when the canonical paths are equal. It overlaps `File f` when `f` is the `.fsi` itself. It overlaps `Project p` when the `.fsi` is under the project directory. It does not overlap the implementation `.fs`. That is a deliberate narrowing of the vision, which makes the contract holder block the implementer; the point of a signature file is that others edit the implementation freely and the compiler judges it. This is the first decision for Will.

New commands and events, all additive (a new union case does not break reading old ledger rows, while a new field on an existing case would):

- `CohortCommand.RecordDecision of by * contract: string * statement: string * scope`, event `DecisionRecorded`. Refused unless `by` holds a `Held` `Contract` claim on that path.
- `CohortCommand.ChangedFilesObserved of LandingId * files: string list * scope`. Valid only for the front landing while `Verifying`. The arm blocks the landing with `ContractChangedWithoutDecision` when `files` contains a `.fsi` that some `Held` contract claim covers and the landing does not present that claim (right holder, current fence) together with an `Open` decision for the path. On success it consumes the decision (`DecisionConsumed`). It uses `blockAndPop` like the other blockers, so the queue never jams.
- The rule is inert when the cohort holds no contract claim, so existing ledgers and the four fixtures in `SageFs.Tests/cohorts/` replay unchanged.

Edge changes:

- `LandingPerformer` (`SageFs.Core/Features/CohortOwner.fs`) gets a `ChangedFiles: string -> string -> string -> Async<Result<string list, string>>` field. In `dispatchLandingEffects` the `ComputeAffected` arm awaits `ChangedFiles` first, posts `ChangedFilesObserved`, and only then asks for the affected tests, all inside the one `Async.Start`, so the order is fixed by code and not by a race. An `Error` maps to `VerificationInconclusive`, the same fail-closed route as today. `DaemonMode.fs` wires it to `CohortGit.diffNames`; `LandingPerformer.stub` gets a never-completing twin.
- `SageFs.Core/Features/ContractCheck.fs` (new, vertical slice): `ContractCheck.withSignature : FSharpProjectOptions -> fsiPath -> Result<FSharpProjectOptions, ContractRefusal>` puts the `.fsi` before its `.fs` in `SourceFiles` when the project does not list it, and `checkAsync` mirrors `CallerCheck.compilerAnswerAsync`, returning `Satisfied | Violated of diagnostics | Unavailable of reason`. A signature file also needs a `<Compile Include="X.fsi" />` line before the `.fs` in the fsproj, so the claim-introducing landing carries an fsproj edit; say so in the tool text.
- The landing gate calls `checkAsync` in the integration worktree after the rebase. `Violated` becomes `FailingTests`-style blocking with a new `LandingBlocker.SignatureViolated of fsiPath * firstDiagnostic`; `Unavailable` is `Inconclusive`. Per-save checking in each member session is step 6 and waits for the measurement below.

Tool surface and wire:

- No new tool. `acquire_claim` accepts `contract:<path>` (its `scope` parameter description grows by about 25 tokens). `request_landing` gets one optional `contract_decision` string; when present the boundary issues `RecordDecision` and then `RequestLanding`. That is about 40 tokens, against about 250 for a new `record_decision` tool, and the catalog is already near 28,000 tokens.
- SSE `claim_changed` payloads gain `Kind = "contract"`, and the landing wire gains the two blocker names. Both are additions, but sagefs-vscode requires an exact `apiVersion` and the status text that `sagefs.nvim/lua/sagefs/cohort.lua` parses will print `contract:<path>`. Recommendation: move `EndpointContracts.apiVersion` from 4 to 5, land the Lua and VS Code tolerance first, and carry a `Plugin:` trailer on each wire commit.
- Persistence: ledger rows gain new cases only. `SageFs.Json` already carries the F# union converter, so no profile change.

## Tests

RED tests first, in `SageFs.Tests/CohortContractTests.fs` (default suite, Expecto.Flip, message first):

1. `overlaps` is symmetric and total over every pair of `ClaimScope` cases (FsCheck over generated canonical paths), and a `Contract` over `X.fsi` overlaps `File X.fsi` and does not overlap `File X.fs`. This fails to compile until the case exists, which counts as red.
2. `AcquireClaim` of `Contract p` while another member holds `File p` is `ClaimConflict`, and while another holds `Project` above `p`.
3. `ChangedFilesObserved` with a covered `.fsi` and no decision blocks the landing with `ContractChangedWithoutDecision` and pops the queue.
4. With a decision and the claim presented, the landing proceeds and the decision reads `ConsumedBy`. Presenting a stale fence is still `StaleClaimFence`.
5. Property: a cohort with no `Contract` claim gives byte-identical `decide` results with and without the new commands in the alphabet (the solo-user invariant).
6. `ContractCheck.withSignature` on a fixture project that lacks the `.fsi` inserts it before the `.fs`; on one that lists it already, returns the options unchanged.

Simulation, `SageFs.Simulation/CohortContractSpec.fs`, modelled on `CohortVetoSpec.fs`: actors {holder, implementer, stranger}, commands {acquire contract, record decision, request landing touching `.fsi`, touching `.fs`, depart, tick past the lease window}, seeded schedule, plus fault injection that drops and duplicates `ChangedFilesObserved`. Invariants: no `Landed` landing changed a held contract path without a consumed decision; every decision is consumed at most once; a departed holder's contract claim is `Orphaned` and its open decisions cannot be consumed. Twins: real `decide` with the contract rule removed (caught by the first invariant), with consumption removed (caught by the second), and a performer that skips `ChangedFilesObserved` (caught by a fourth invariant: a landing may not reach `Landed` in a cohort that holds a contract claim without having recorded its file list).

Outcome gate: `SageFs.Tests/CohortContractOutcomeTests.fs` in the `--integration-host` tier, copying the two-connection pattern in `CohortMcpToolsIntegrationTests.fs` and `CapabilityHttpIntegrationTests.fs`, with a throwaway git repo holding a small project with an `X.fsi` and `X.fs`. It asserts the text a person reads from `get_cohort_status` and from the `request_landing` refusal across the matrix above, and that no cell is skipped: each cell is a named case, and a missing Verifier-token cell fails the run.

Mutation: the new overlap arms and the blocker branch get a mutation suite entry like the other cohort pieces. No test waits on a wall clock; landings advance by the performer's completions, and the sim uses an injected `Clock`.

## Steps

1. Test 1 and 2, then add `Contract` to `ClaimScope`, `overlaps`, `tryCanonical`, and fix every exhaustive match in the files listed above in the same commit. Owns `Cohort.fs`, `Capability.fs`, `CohortTerritory.fs`, `CohortLanes.fs`, `CohortErrorMapping.fs`, `CohortInspector.fs`, `CohortPlay.fs`, `DashboardFragments.fs`, `SseWriter.fs`.
2. `parseClaimScope` accepts `contract:`, refuses non-`.fsi` paths with the reason, `acquire_claim` text updated. Owns `McpCohortTools.fs`, `McpTools.fs` (description only). Wire commit, needs the `Plugin:` trailer.
3. `RecordDecision`, `DecisionRecorded`, state and tests 3 to 5 (decision half). Owns `Cohort.fs`.
4. `ChangedFilesObserved`, the blocker, the `LandingPerformer.ChangedFiles` field and the dispatcher change, `DaemonMode.fs` wiring. Owns `CohortOwner.fs`, `DaemonMode.fs`. Sequential after step 3.
5. `ContractCheck.fs` and test 6, in parallel with steps 3 and 4 (new file, new test file, no shared files). Then wire it into the landing gate in `DaemonMode.fs` after step 4.
6. Simulation and the outcome gate.
7. Per-save check in the member session through the worker. Only after the first measurement says it is cheap enough. Owns `SageFs.Host/WorkerMain.fs` and the worker protocol.
8. `docs/mcp-tools.md`, `docs/agents.md`, roadmap link to this plan, `sagefs.nvim` display, sagefs-vscode tolerance.

## Measure first

1. Cost of the signature check. In a SageFs session on `SageFs.Core`, take the `CheckOptions`, add a generated `Cohort.fsi` (`FSharpCheckFileResults.GenerateSignature : ?pageWidth: int -> ISourceText option`, confirmed in the checked-out FCS source at `src/Compiler/Service/FSharpCheckerResults.fsi:463`; its output on this version is still untested) and time `ParseAndCheckProject` cold and warm with `projectChecker`. Under about 5 seconds warm means a per-save check is affordable; over 20 seconds means the check runs only at landing and step 7 is dropped.
2. Whether the diagnostics name the pair. Break one member of the `.fs` deliberately and confirm the error points at the `.fs`/`.fsi` pair, and that a project with no `.fsi` listed is not made noisy by the inserted file.
3. Whether anyone needs it. The vision gates symbol-level claims on a `ClaimMissedCollision` friction counter; `grep -rn ClaimMissedCollision` finds nothing in the tree, so the counter was never built. Cheap proxy: `git log --since='30 days ago' --format='%h %s' | grep -iE 'merge|conflict|resolve'` and read the top 20 for conflicts that file claims did not prevent. If none is a shape conflict, this item stays Exploring.

## Risks and dependencies

- Adding an `.fsi` changes how the repo builds and how hot reload reads a file; `ReloadPlanning.SignatureChanged` compares header text on the untyped AST, so a repo with `.fsi` files needs one run of the hot reload parity tests before the first real contract lands.
- The decision slice is a down payment on the full decision log. Keep `DecisionKind` open to the other cases (`DesignChoice`, `Deferral`) so the later work adds cases and does not reshape the type.
- Depends on nothing outside this group. `plan-a-cohort-as-code` can declare `Contract` claims once step 1 lands. `check-the-scope-before-the-write` is the same kind of enforcement for writes and shares `Capability.admitClaim`.

## Size

M. The cohort core change is small and well covered by sim patterns; the landing gate wiring and the integration-tier matrix take most of the time.

## Decisions for Will

1. Does a `Contract` claim cover only the `.fsi` (my recommendation) or the `.fs` as well (the vision)?
2. Move `apiVersion` to 5 for the new scope kind and blocker names? Recommendation: yes, because the extension requires an exact match and a silent new kind is how a client shows a blank row.
3. Is the `contract_decision` parameter on `request_landing` acceptable in place of a separate `record_decision` tool? Recommendation: yes, for the token cost.
