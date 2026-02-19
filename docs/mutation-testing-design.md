# Molina: Epistemic Warrant for F# Test Suites

> *"Middle knowledge is God's knowledge of counterfactuals — what WOULD happen
> under different circumstances."* — Luis de Molina, 1588
>
> **Molina** is SageFs's mutation testing engine. It answers the counterfactual:
> *"If this code were different, would your tests notice?"* Every surviving mutant
> is an epistemic gap — a place where your tests claim confidence but lack warrant.

## The Name

**Molinism** is the theological doctrine of *scientia media* — middle knowledge.
Between what God knows WILL happen (free knowledge) and what COULD happen
(natural knowledge) lies what WOULD happen under specific counterfactual
conditions. Mutation testing is exactly this: you don't care what your tests
DO catch (that's free knowledge). You don't care what they COULD catch in
theory (that's natural knowledge). You care what they WOULD catch if the code
were subtly wrong. That's middle knowledge. That's **Molina**.

The mutation score isn't a number — it's **epistemic warrant**. It tells you
how much confidence your test suite has actually earned, not how much it claims.

## Context

**The gap**: Stryker.NET is Roslyn-only. No F# mutation testing tool exists.
GitHub issue #1216 has been open since 2020. An intern attempted it, abandoned
it Jan 2021. Faultify (bytecode-level) is abandoned, targets netcoreapp3.1.

**What SageFs already has**:
- `FSharp.Compiler.Service` (FCS) — full F# parser, type checker, AST access
- Warm FSI session — can evaluate arbitrary F# code in ~milliseconds
- `EvalInteractionNonThrowing` — eval that returns diagnostics without throwing
- Middleware pipeline — intercepts eval requests, can transform code
- File watcher — detects `.fs`/`.fsx` changes
- Shadow copy system — isolates DLL loads
- Event store (Marten) — can persist mutation run results as event streams
- Expecto integration — test runner already built in
- MCP server — external tools can trigger mutations
- Actor-based session management — `MailboxProcessor` actors for concurrency
- Dashboard with Datastar SSE — real-time UI streaming
- TUI with Cell grid rendering — terminal UI
- Raylib GUI — GPU-accelerated window

**What mutation testing needs**:
1. Parse F# source → AST
2. Identify mutable locations (operators, conditions, literals, etc.)
3. Generate mutants (transform AST nodes)
4. For each mutant: compile → run tests → check if any test fails
5. Report: which mutants survived (tests didn't catch them)
6. Stream results to all UI surfaces in real time
7. Persist results as events for historical analysis

---

## The Deliberation

### Phase 1: AST vs Bytecode vs Source Text Rewriting

**Casey Muratori**: Let's start with the fundamental question: do you even need
the AST? Stryker for C# started with text-based mutations — regex replace `+`
with `-`. It's ugly but it ships tomorrow. You can parse the AST later when you
understand what mutations actually matter. Start simple, transform based on
measured needs.

**Mark Seemann pushes back**: Text-based mutation is a random walk through the
syntax. You'll generate mutations that don't compile, waste cycles on syntactic
noise. The AST is the correct level of abstraction — it tells you what's
semantically meaningful. You already HAVE FCS. The structure is right there.
Don't pretend you don't know about it.

**Don Syme**: FCS gives you the untyped syntax tree via `parseFile`. You get
`SynExpr`, `SynPat`, `SynType` — every node in the F# AST. More importantly,
you get the *typed* tree via `checkFileInProject`, which tells you the actual
types of expressions. This matters because `+` on `int` vs `+` on `string` vs
`+` on a custom operator are very different mutations. The typed tree is what
makes F# mutation testing actually better than what Stryker does for C#.

**Casey Muratori responds**: Fine, but you're designing a cathedral when you
need a bicycle. How many mutation operators do you need for v0.1? Five? Ten?
You can identify `if`, `&&`, `||`, `+`, `-`, `*`, `/`, `true`, `false` with
string scanning. I'll grant you the AST is better in theory — show me it's
better in practice with actual mutation-kill data before you build the full
typed-tree walker.

**Scott Wlaschin**: Casey's pragmatism is valid for a prototype, but F# code
is structurally different from C#. Pattern matching is everywhere. `match` arms,
active patterns, guard clauses — these are where the real bugs hide in F# code.
You can't mutate pattern matching with regex. You NEED the AST for anything
beyond toy mutations.

**Ginger Bill**: What about bytecode? IL is the common denominator. Mutate the
IL directly — you skip the recompilation step entirely. Faultify tried this.
It's the data-oriented approach: the compiled output IS the data structure you
operate on.

**Ryan Fluery pushes back**: IL mutation sounds elegant but it's a trap. You
lose the mapping back to source locations. When you tell a developer "this
mutant survived," they need to see the F# code that wasn't tested, not an IL
offset. And .NET IL has a complex type system at the bytecode level — you can't
just flip a `blt` to `ble` without understanding the stack state. This is the
kind of "clever" shortcut that costs you a month of debugging.

**Jeremy Miller**: I built Marten by learning from every event store database
that tried to be too clever. Bytecode manipulation is being too clever. Source
code is what developers understand. Source code is what your tool should mutate.
If it didn't cost much to write the AST walker, it doesn't cost much to throw
away and rewrite if the approach is wrong.

### Panel Consensus (Phase 1)

**Use the FCS untyped AST (SynExpr tree) for v1. Defer typed tree to v2.**

Rationale: Muratori's "start simple" principle applies — but "simple" in this
context means the untyped AST, not string replacement. The untyped tree is
already parsed as part of FCS's normal workflow. String replacement would be
HARDER because you'd constantly generate non-compiling mutations and waste
cycles. Bill's bytecode approach has merit for performance but loses developer
UX. Defer it.

**Syme's concern**: The typed tree would catch things like custom operators and
type-dependent behavior. We're accepting this gap for now.

**Muratori's concern**: Don't build a generic AST transformation framework.
Build the specific mutations you need. If a visitor pattern emerges, let it
emerge from working code.

---

### Phase 2: Mutation Operators — What to Mutate

**Scott Wlaschin**: Let me lay out F#-specific mutations that matter, in order
of how likely they are to catch real bugs:

1. **Conditional boundary mutations**: `>` → `>=`, `<` → `<=`, `=` → `<>`, etc.
2. **Boolean logic mutations**: `&&` → `||`, `||` → `&&`, `not x` → `x`
3. **Arithmetic operator mutations**: `+` → `-`, `*` → `/`
4. **Literal mutations**: `0` → `1`, `true` → `false`, `""` → `"mutant"`
5. **Pattern match mutations**: remove a match arm, swap arm order, change guard
6. **Option/Result mutations**: `Some x` → `None`, `Ok x` → `Error x`
7. **Collection mutations**: `List.filter` → `List.filter (not)`, `head` → `last`
8. **Function composition**: `>>` → `<<`, `|>` pipeline removal

**Mark Seemann**: I'd prioritize differently. The highest-value mutations are
the ones that reveal missing property-based tests. Boolean logic and boundary
conditions are #1 and #2 because property-based tests should catch ALL of those.
If a boundary mutant survives, the test suite is fundamentally incomplete —
it's not just missing an example.

**The Primeagen**: You're overthinking this. Start with the mutations that
Stryker already does for C#. Port them. Measure which ones produce the most
"interesting" surviving mutants. Then add the F#-specific ones. Don't design
15 mutation operators before you've shipped 1.

**Delaney Gillilan**: I've watched too many projects die from trying to do
everything in v1. Complexity is the apex predator. Start with 5 operators that
cover 80% of the value: conditionals, booleans, arithmetic, literals, and
negation. Ship that. See what users actually want.

**Aaron Stannard pushes back on limiting scope**: Hold on. The whole POINT of
building this into SageFs rather than as a standalone tool is that you have the
warm FSI session. That means compilation is nearly free. If compilation is
nearly free, you can run MORE mutants than Stryker can. The F#-specific
mutations (pattern matching, Option/Result, composition) are what make this
tool worth existing. Don't ship a worse version of Stryker — ship the thing
only SageFs can do.

**John Carmack**: Stannard makes a good point about leveraging the FSI session.
But Gillilan's right about complexity. Here's the compromise: implement the
infrastructure for arbitrary mutations, but only enable 5-7 in v1. Make it
trivial to add new mutation operators — the framework should be open for
extension. Then let the community contribute operators.

### Panel Consensus (Phase 2)

**v1 operators (ship with these):**
1. Conditional boundary: `>` ↔ `>=`, `<` ↔ `<=`, `=` ↔ `<>`
2. Boolean logic: `&&` ↔ `||`, `not x` ↔ `x`
3. Negation: negate conditionals entirely (`if x then` → `if not x then`)
4. Arithmetic: `+` ↔ `-`, `*` ↔ `/`
5. Literal: `true` ↔ `false`, integers `n` → `n+1` / `n-1` / `0`
6. Return value: `Some x` → `None`, `Ok x` → `Error "mutant"`
7. Statement deletion: remove the body of a function (replace with `failwith "mutant"`)

**v2 operators (after v1 ships):**
- Pattern match arm removal/reordering
- Collection function swaps
- Pipeline/composition reversal
- Guard clause mutation
- Active pattern mutation
- Typed-tree-aware operator mutations

**Carmack's principle applies**: the mutation operator interface must be a simple
function `SynExpr -> MutantCandidate list` so adding operators is trivial.

---

### Phase 3: The Execution Model — Actors, CQRS, and Event Sourcing

**Delaney Gillilan**: Here's where the Tao applies. The mutation testing
workflow is inherently CQRS. Generating mutants is a command. Running tests
against mutants is a query ("does this test suite catch this mutant?").
Reporting results is a projection. These should be separate, decoupled
phases — not one monolithic pipeline.

**Jeremy Miller**: This is exactly the Marten pattern. Each mutation run is
an event stream — a first-class aggregate:

```fsharp
// Molina event stream per run
type MolinaEvent =
  | RunRequested of {| Target: string; Operators: MutationOperator list; Timestamp: DateTimeOffset |}
  | BaselineCompleted of {| TestCount: int; PassCount: int; Duration: TimeSpan |}
  | MutantDiscovered of {| Id: MutantId; FilePath: string; Line: int; Operator: MutationOperator; OriginalCode: string; MutatedCode: string; FunctionName: string |}
  | MutantQueued of {| Id: MutantId; SessionId: SessionId |}
  | MutantTested of {| Id: MutantId; Result: MutantResult; Duration: TimeSpan |}
  | MutantSkipped of {| Id: MutantId; Reason: string |}
  | RunCompleted of {| Score: MutationScore; Duration: TimeSpan |}
  | RunFailed of {| Error: string; LastMutantId: MutantId option |}
```

Store these in Marten. You get replay, historical comparison, "what changed
since last run" for free. You can project a `MolinaRunSummary` read model
that the dashboard and TUI consume. You can project `MolinaHistory` for
trend analysis across commits.

**Aaron Stannard**: Now let me make the case for actors, because the execution
model DEMANDS them. You have multiple concerns that are naturally concurrent:

1. **MolinaCoordinator** — the orchestrator. Receives a run request, kicks off
   discovery, manages the lifecycle. This is the aggregate root as an actor.
2. **MutantDiscoveryActor** — parses source, walks AST, generates mutant
   candidates. CPU-bound, no FSI needed. Can run in parallel with baseline.
3. **SessionPool** — manages N warm FSI sessions. Each session is an actor.
   Mutants are dispatched to available sessions round-robin.
4. **MutantRunnerActor** (one per session) — receives a mutant, hot-swaps in
   FSI, runs tests, reports result. Serializes access to its FSI session.
5. **MolinaProjection** — consumes events, builds the live read model that
   all UIs consume. This is where CQRS reads happen.

The single-threaded FSI constraint is per-SESSION, not per-SYSTEM. Multiple
sessions give you parallelism. The actor model gives you the isolation
guarantee: each session actor owns its FSI, no shared mutable state.

**Don Syme**: F# `MailboxProcessor` is the natural actor primitive here. SageFs
already uses it — the eval actor, the query actor, the session manager. Molina
actors are the same pattern. No external framework needed.

**Casey Muratori**: I argued against this earlier but Stannard changed my mind.
The key insight is that session startup is a ONE-TIME cost at the beginning
of a Molina run. If you spin up 4 sessions in parallel during discovery,
by the time mutants are ready to run, the pool is warm. Then you get 4x
throughput on the actual mutant execution. The wall-clock improvement is real.

**Ryan Fluery**: The actor boundaries also solve the session corruption risk.
If MutantRunnerActor-2's FSI session gets corrupted, you kill that one actor,
spin up a replacement, and continue. The other 3 sessions are unaffected.
Fault isolation through ownership boundaries — same principle as arena
allocators, different domain.

**Mark Seemann**: The CQRS split is clean:

- **Command side**: MolinaCoordinator receives `RunMutations` command →
  emits events → actors do work → more events
- **Query side**: MolinaProjection consumes events → builds `MolinaRunState`
  → all UIs read from projection

The projection is a pure fold:
```fsharp
let project (state: MolinaRunState) (event: MolinaEvent) : MolinaRunState =
  match event with
  | RunRequested e ->
    { state with Status = Running; Target = e.Target; StartedAt = e.Timestamp }
  | MutantDiscovered e ->
    { state with
        TotalMutants = state.TotalMutants + 1
        Mutants = state.Mutants |> Map.add e.Id (Pending e) }
  | MutantTested e ->
    let mutants = state.Mutants |> Map.change e.Id (Option.map (fun m -> Tested (m, e.Result)))
    let killed = if e.Result.IsKilled then state.Killed + 1 else state.Killed
    let survived = if e.Result.IsSurvived then state.Survived + 1 else state.Survived
    { state with Mutants = mutants; Killed = killed; Survived = survived; TestedCount = state.TestedCount + 1 }
  | RunCompleted e ->
    { state with Status = Completed; Score = Some e.Score; CompletedAt = Some (DateTimeOffset.UtcNow) }
  | _ -> state
```

This is a monoid. State + Event → State. Testable, replayable, composable.

**Jeremy Miller**: And Marten gives you live projections for free. Register
`MolinaRunState` as an inline projection on the Marten `DocumentSession`.
Every time you append an event, the projection updates atomically in the
same PostgreSQL transaction. Your read model is always consistent with
your write model — no eventual consistency headaches.

**Scott Wlaschin**: The event stream also solves the resume-after-crash
problem. If SageFs crashes mid-run, replay the events to reconstruct state.
Skip already-tested mutants. Continue from where you left off. That's not
a feature you'd get from a simple sequential loop.

**The Primeagen pushes back mildly**: This is a lot of infrastructure for
"run tests against code changes." But I'll grant that if you're already
USING Marten and actors in SageFs, the marginal cost of using them here is
low. Just don't let the plumbing become more code than the mutation logic.

### Panel Consensus (Phase 3)

**Actor-based execution with CQRS event sourcing:**

```
   RunMutations command
         │
         ▼
  ┌──────────────┐
  │  Molina       │ ← Coordinator actor (aggregate root)
  │  Coordinator  │
  └──┬────┬───┬──┘
     │    │   │
     │    │   └─── Emits MolinaEvent stream → Marten
     │    │
     │    ▼
     │  ┌──────────────┐
     │  │  Discovery    │ ← Parse AST, generate mutant candidates
     │  │  Actor        │
     │  └──────┬───────┘
     │         │ MutantDiscovered events
     │         ▼
     │  ┌──────────────┐
     │  │  Session      │ ← Manages N warm FSI sessions
     │  │  Pool         │
     │  └──┬──┬──┬──┬─┘
     │     │  │  │  │
     │     ▼  ▼  ▼  ▼
     │    Runner actors (one per session, parallel execution)
     │     │  │  │  │
     │     └──┴──┴──┘
     │         │ MutantTested events
     │         ▼
     │  ┌──────────────┐
     │  │  Projection   │ ← Pure fold: events → MolinaRunState
     │  │  (Marten)     │ ← All UIs read from this
     │  └──────────────┘
     │
     ▼
  Emits RunCompleted → triggers final report
```

**Event stream**: Each Molina run is a Marten event stream keyed by run ID.
Events are appended as they happen. The `MolinaRunState` projection is
updated inline (same transaction). UIs subscribe to projection changes.

**Session pool**: Start with `Environment.ProcessorCount / 2` sessions
(minimum 2, maximum 8). Each session warms up in parallel during discovery.
Mutants are dispatched round-robin to available runner actors.

**Fault isolation**: If a runner actor's session faults, the coordinator
marks it dead, spins a replacement, and re-queues the failed mutant.
Other runners continue unaffected.

**Resume**: On restart, replay events from Marten. Skip mutants already
in `Tested` state. Continue with remaining `Pending` mutants.

**Stannard's principle**: Every design choice here preserves optionality.
Actor count is configurable. Session pool size adapts. Event stream is
the source of truth — you can always rebuild projections differently later.

---

### Phase 4: UI Surfaces — Every Interface Gets Molina

**Pim Brouwers**: SageFs has FIVE UI surfaces. Molina must work through all of
them, and each surface has different affordances. Let me enumerate:

1. **Dashboard** (browser, Datastar SSE)
2. **TUI** (terminal, Cell grid, ANSI)
3. **Raylib GUI** (GPU window, Cell grid)
4. **MCP** (AI agent / Copilot integration)
5. **CLI** (batch mode, stdout)

Each surface reads from the SAME projection — `MolinaRunState`. The CQRS
split means we design the read model ONCE and render it N ways.

#### 4a. Dashboard (Datastar SSE)

**Greg Holden**: This is the richest surface. The dashboard already has SSE
streaming via Datastar. Molina gets its own route: `/molina/stream`.

**Delaney Gillilan**: One SSE connection. Fat morph. The entire Molina panel
re-renders on every event. Datastar diffs the DOM — the browser only repaints
what changed. No client-side state. Signals only for user input (selecting
filters, expanding mutant details).

```fsharp
// Dashboard SSE handler for Molina
let molinaStreamHandler (projection: MolinaProjection) : HttpHandler =
  fun ctx -> task {
    ctx.Response.ContentType <- "text/event-stream"
    // Initial push: current state
    do! pushMolinaPanel ctx (projection.Current)
    // Subscribe to projection changes
    use _sub = projection.Changed.Subscribe(fun state ->
      pushMolinaPanel ctx state |> Async.RunSynchronously)
    // Hold connection open
    let tcs = TaskCompletionSource()
    use _ct = ctx.RequestAborted.Register(fun () -> tcs.TrySetResult() |> ignore)
    do! tcs.Task
  }
```

**Dashboard layout:**

```
┌─────────────────────────────────────────────────────────────┐
│ 🧬 Molina — Mutation Testing          Run #47 │ 2m 13s     │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ████████████████████░░░░░  78% (312/400)  ⏳ 88 remaining  │
│                                                             │
│  ✅ Killed: 312   ⚠️ Survived: 47   ⏱ Timeout: 3           │
│  ❌ CompileErr: 8  📭 NoCoverage: 12  🔄 Testing: mutant-389│
│                                                             │
├─────────────────────────────────────────────────────────────┤
│ Surviving Mutants                          [Filter ▼]       │
├───────┬──────────┬───────────┬──────────────────────────────┤
│ File  │ Line     │ Operator  │ Mutation                     │
├───────┼──────────┼───────────┼──────────────────────────────┤
│▶Pricing│ 42      │ Boundary  │ discount > 0 → discount >= 0 │
│ Auth   │ 117     │ Boolean   │ isAdmin && hasRole → \|\|    │
│ Cart   │ 89      │ Return    │ Some total → None            │
├───────┴──────────┴───────────┴──────────────────────────────┤
│ ▼ Pricing.fs:42 — applyDiscount                            │
│  let applyDiscount price discount =                         │
│    if discount >= 0.0 then  ← MUTANT (was: > 0.0)          │
│      price * (1.0 - discount)                               │
│    else price                                               │
│                                                             │
│  Tests that should catch this:                              │
│    ✗ Pricing.discountTests (ran, didn't catch)              │
│    ○ Pricing.boundaryTests (not in scope)                   │
└─────────────────────────────────────────────────────────────┘
```

**Interactions (Datastar signals):**
- Click file row → expand inline source view with mutation highlight
- Filter dropdown → operator type, status (survived/killed/all)
- "Run Again" button → POST to `/molina/run` → starts new Molina run
- "Run on Module" → scoped run targeting specific module
- Progress bar morphs in real time via SSE

#### 4b. TUI (Terminal, Cell Grid)

**TJ DeVries**: The TUI needs to show Molina status without taking over the
whole screen. It should integrate with the existing pane system.

**Design**: Molina gets a dedicated pane (toggled with a keybinding, e.g. `Ctrl+M`).
The pane renders from the same `MolinaRunState` projection, but through the
Cell grid abstraction.

```
┌─ Editor ──────────────────┐┌─ Molina ─────────────────────┐
│ let applyDiscount p d =   ││ 🧬 78% (312/400) 2m13s       │
│   if d > 0.0 then         ││                               │
│     p * (1.0 - d)         ││ ⚠ Pricing.fs:42  Boundary    │
│   else p                  ││   > → >=  in applyDiscount    │
│                           ││ ⚠ Auth.fs:117    Boolean      │
│                           ││   && → ||  in checkAccess     │
│                           ││ ⚠ Cart.fs:89     Return       │
│                           ││   Some → None in getTotal     │
│                           ││                               │
│                           ││ ✅ 312 killed  ⏱ 3 timeout    │
│                           ││ ❌ 8 compile   📭 12 no-cov   │
├─ Output ──────────────────┤│                               │
│ val applyDiscount: ...    ││ [Enter] details  [r] re-run   │
└───────────────────────────┘└───────────────────────────────┘
```

**Keybindings:**
- `Ctrl+M` — toggle Molina pane
- `j/k` — navigate surviving mutants list
- `Enter` — expand mutant detail (shows source + affected tests)
- `r` — re-run Molina on current module
- `R` — re-run Molina on full project

**Integration with editor**: When a mutant is selected in the Molina pane,
highlight the corresponding line in the editor pane with a gutter marker.
This uses the existing `EditorAction` DU — add `MolinaHighlight of line: int`.

#### 4c. Raylib GUI (GPU Window)

**John Carmack**: The Raylib GUI has more rendering budget. Use it.

The Raylib GUI renders from the same Cell grid as TUI (shared pipeline through
`Screen.draw`), so the layout is identical. But Raylib can add:

- **Color-coded source gutters**: Red for survived mutants, green for killed,
  yellow for in-progress. Rendered as small colored rectangles in the gutter
  alongside line numbers.
- **Hover tooltips**: Mouse over a gutter marker → shows mutant details in a
  floating panel. TUI can't do this (no mouse hover), but Raylib can.
- **Animated progress**: The mutation score bar can animate smoothly between
  updates instead of jumping. Small polish that makes the tool feel alive.
- **Minimap annotations**: If the editor has a minimap, mark mutant locations
  on it — surviving in red, killed in green.

All of this reads from the same `MolinaRunState` projection. The Raylib-specific
rendering is in `RaylibEmitter` — the shared `PaneRenderer` for Molina produces
the Cell grid content, and Raylib adds GPU-accelerated overlays on top.

#### 4d. MCP (AI Agent / Copilot Integration)

**TJ DeVries**: This is where Molina becomes more than a mutation testing tool.
It becomes a mutation testing COPILOT. The AI agent calls Molina through MCP,
reads the results, and REASONS about the surviving mutants.

**MCP tools:**

```fsharp
// sagefs-molina_run: Start a Molina run
// Input: { target: "Pricing.fs" | "Pricing" | "*", operators: ["all"] | ["boundary", "boolean"] }
// Output: { runId: "run-47", status: "started", totalMutants: 0 }

// sagefs-molina_status: Check run progress
// Input: { runId: "run-47" }
// Output: { status: "running", tested: 312, total: 400, killed: 265, survived: 47, score: 0.849 }

// sagefs-molina_results: Get detailed results
// Input: { runId: "run-47", filter: "survived" }
// Output: { mutants: [{ id: "m-42", file: "Pricing.fs", line: 42, operator: "Boundary",
//            original: "> 0.0", mutated: ">= 0.0", function: "applyDiscount",
//            affectedTests: ["discountTests"], result: "Survived" }] }

// sagefs-molina_suggest_tests: AI-friendly output for test generation
// Input: { runId: "run-47", mutantId: "m-42" }
// Output: { mutant: { ... }, suggestedTestDescription: "Test that discount of exactly 0.0
//            returns unmodified price (boundary between discount and no-discount paths)",
//            testSkeleton: "testCase \"zero discount returns original price\" { ... }" }

// sagefs-molina_history: Compare across runs
// Input: { last: 5 }
// Output: { runs: [{ id: "run-47", score: 0.849, date: "..." }, { id: "run-46", score: 0.823 }] }
```

**The workflow**: Copilot calls `sagefs-molina_run` → polls `sagefs-molina_status`
→ reads `sagefs-molina_results` → for each surviving mutant, calls
`sagefs-molina_suggest_tests` → generates the actual test code → sends it via
`sagefs-send_fsharp_code` to validate in FSI → writes to test file.

**Houston Haynes**: This is the feedback loop. The AI doesn't just report
"you have a gap." It FILLS the gap. Molina + Copilot = continuous epistemic
improvement. The mutation score ratchets up over time, never down.

#### 4e. CLI (Batch Mode)

**The Primeagen**: Sometimes you just want a number. `sagefs --molina Pricing.fs`
should print the score and exit. CI pipeline friendly.

```
$ sagefs --molina Pricing.fs

🧬 Molina — Pricing.fs
  Operators: Boundary, Boolean, Negate, Arithmetic, Literal, Return, Delete
  Mutants generated: 47
  
  Running... ████████████████████ 100% (47/47) in 3.2s
  
  Score: 85.1% (40 killed / 47 total)
  
  ⚠️ 7 surviving mutants:
    Pricing.fs:42  Boundary   > → >=   in applyDiscount
    Pricing.fs:58  Boolean    && → ||  in validateOrder
    Pricing.fs:71  Return     Some → None in calculateTax
    Pricing.fs:89  Literal    0.0 → 1.0 in freeShippingThreshold
    Pricing.fs:103 Negate     if x → if not x in isEligible
    Pricing.fs:115 Arithmetic + → -   in totalWithTax
    Pricing.fs:128 Delete     body → failwith in formatReceipt
  
  Run sagefs --molina Pricing.fs --detail for source-level view
  Exit code: 7 (number of surviving mutants)
```

**Exit codes for CI:**
- `0` — all mutants killed (100% score)
- `N` — N surviving mutants (non-zero = pipeline can fail)
- `-1` — Molina run failed (infrastructure error)

**Flags:**
- `--molina <target>` — run Molina on file/module/project
- `--molina-threshold 80` — exit 0 if score ≥ 80%, non-zero otherwise
- `--molina-operators boundary,boolean` — restrict operators
- `--molina-detail` — show source-level surviving mutant details
- `--molina-json` — output as JSON (for CI integration)
- `--molina-sessions 4` — session pool size override

### Panel Consensus (Phase 4)

**All five surfaces read from the same `MolinaRunState` projection:**

```
  MolinaEvent stream (Marten)
         │
         ▼
  MolinaProjection (pure fold)
         │
         ├──→ Dashboard SSE (/molina/stream, Datastar fat morph)
         ├──→ TUI pane (Cell grid, Ctrl+M toggle)
         ├──→ Raylib pane (Cell grid + GPU overlays)
         ├──→ MCP tools (JSON responses)
         └──→ CLI (stdout, exit code)
```

**Greg Holden's principle**: Every UI surface is a PROJECTION of the same event
stream. No UI has special state. If the Dashboard shows it, the TUI shows it.
If MCP can query it, the CLI can print it. One truth, many views.

**Brouwers' principle**: Each surface adapter is a thin HttpHandler or
PaneRenderer. The adapter transforms `MolinaRunState → surface-specific output`.
No business logic in the adapter — just rendering.

---

### Phase 5: The Mutation Report

**Mark Seemann**: The report needs to answer one question: "Where is my test
suite weak?" Every surviving mutant is a test that should exist but doesn't.
The report should suggest the test, not just flag the location.

**Scott Wlaschin**: For F# specifically, show the mutant in context:
```
// Original (line 42 of Pricing.fs)
let applyDiscount price discount =
  if discount > 0.0 then price * (1.0 - discount)
  else price

// Mutant #7: boundary mutation (> → >=)  [SURVIVED ⚠️]
let applyDiscount price discount =
  if discount >= 0.0 then price * (1.0 - discount)
  else price

// Suggested test:
// What happens when discount is exactly 0.0?
// The original returns `price` (no discount).
// The mutant returns `price * 1.0` (still price, but through discount path).
// This mutant is equivalent — consider whether the distinction matters.
```

**Mark Seemann**: That last point is crucial. Some surviving mutants are
EQUIVALENT MUTANTS — the mutation doesn't change observable behavior. This is
the halting problem in disguise. You can't detect all equivalent mutants, but
you can detect common ones: `x * 1.0`, `x + 0`, `if true then x else y`.
Flag these separately from genuinely surviving mutants.

**Casey Muratori**: Equivalent mutant detection is a research problem. Don't
solve it in v1. Flag all surviving mutants the same way. Let the DEVELOPER
decide if it's equivalent. You can add heuristics later when you have data
on which surviving mutants developers consistently dismiss.

**The Primeagen**: The mutation score is the headline number. Show it big.
`78% — 312 killed, 88 survived, 14 timed out, 6 no coverage`.
Then drill down. Sort surviving mutants by file, then by line. Make it
scannable in 10 seconds.

### Panel Consensus (Phase 5)

**Report structure:**
1. **Headline**: Mutation score (killed / total), duration, operator breakdown
2. **Surviving mutants table**: File, line, operator, original → mutated, affected tests
3. **Per-file detail**: Source view with inline annotations showing mutant locations
4. **Equivalent mutant heuristics** (v2): Flag `x * 1.0`, `x + 0`, etc.
5. **Test suggestions** (v2): AI-assisted "write this test" based on surviving mutants

---

### Phase 6: What Could Go Wrong

**Aaron Stannard**: The biggest risk is FSI session corruption. If a mutated
function throws during eval or leaves state dirty, subsequent mutants get
wrong results. You MUST restore the original definition after each mutant,
and you should have a health check between mutants.

**Don Syme**: FCS `EvalInteractionNonThrowing` is safe — it won't crash the
session. But if the mutant introduces an infinite loop, you need timeout
handling. The existing `CancellationTokenSource` pattern in SageFs handles this.

**Ryan Fluery**: The real failure mode is the developer's test suite having
flaky tests. If a test sometimes fails for unrelated reasons, every mutant
running during that flaky window looks "killed." You need a baseline run
that establishes which tests are stable.

**Ginger Bill**: Consider the data layout. You're going to generate potentially
thousands of mutants. Each one needs: file path, line/col, operator, original
AST node, mutated AST node, test result, duration. That's a LOT of
allocations if you're not careful. Pre-allocate the mutant array. Don't
create an object per mutation — use a flat array of structs.

**Casey Muratori pushes back**: It's F# on .NET. The GC handles allocation.
Don't prematurely optimize the mutant data structure. Optimize the eval loop
instead — that's where 99% of the time goes.

**Jeremy Miller**: The subtle risk is that mutation testing gives developers a
FALSE sense of security. 100% mutation score doesn't mean the tests are good —
it means the mutations you chose are all caught. The mutation operators you DON'T
implement are invisible gaps. Document this clearly.

### Key Risks (ordered by likelihood × impact):

1. **FSI session state leaks between mutants** → Mitigate with restore + health check
2. **Flaky test false positives** → Mitigate with baseline run + 2-of-3 voting
3. **Infinite loop mutants** → Mitigate with per-mutant timeout (existing CTS pattern)
4. **Equivalent mutants noise** → Accept for v1, add heuristics in v2
5. **Large codebases overwhelm** → Mitigate with per-module scoping + test selection
6. **Performance** → Measure first. FSI hot-swap should be fast. Don't parallelize until measured.

---

### Phase 7: Many Worlds — Container Scale-Out

*The proposal: snapshot a warm FSI session into a Docker image. Spin up 10-20
containers. Distribute mutants across them. Collect results. Embarrassingly
parallel many-worlds mutation testing.*

**Aaron Stannard**: This is the actor model taken to its logical conclusion.
The in-process `MailboxProcessor` actors are Level 1 — local concurrency within
a single machine. But what happens when you have 2,000 mutants and a test suite
that takes 3 seconds per affected-test run? Even with 4 local sessions, that's
`2000 × 3s / 4 = 1,500 seconds` — 25 minutes. With 20 containers each running
4 sessions, that's `2000 × 3s / 80 = 75 seconds`. That's the difference between
"go get coffee" and "it finished before you looked away." This is EXACTLY what
the actor model is for — location transparency. The MutantRunnerActor doesn't
care if it's in-process or in a container. It receives a mutant, it returns a
result. The orchestrator decides WHERE to send it.

**Casey Muratori**: Hold on. You're adding Docker, networking, image builds,
container orchestration, and distributed coordination to a mutation testing
tool. That's a LOT of moving parts. What's the failure mode when container 7
of 20 dies mid-run? What happens when the network between the coordinator and
a container hiccups? You've turned a testing tool into a distributed system.

**Aaron Stannard responds**: Fair, but SageFs already has ALL the building
blocks. It has HTTP transport — that's how MCP works. It has Marten for event
persistence — that's how you recover from container failures. It has the session
lifecycle management — that's how you warm up and tear down. The incremental
cost of containerizing what already exists is lower than building a new thing.
And the failure mode is simple: container dies → its in-flight mutants get
re-queued to surviving containers. Events already record which mutants were
tested. Replay handles the rest.

**Jeremy Miller**: The event sourcing design we already committed to makes
container scale-out almost free. Think about it: each container runs its own
MolinaCoordinator that emits events to the SAME Marten stream. The projection
doesn't care where the events come from — it folds them the same way. Container
3 emits `MutantTested { Id = "m-42" }`. Container 7 emits `MutantTested { Id = "m-89" }`.
The projection applies both identically. No special distributed coordination
needed — just a shared PostgreSQL instance, which you already have.

**Delaney Gillilan**: I'm torn on this. On one hand, it's beautiful — the
"many worlds" metaphor is literally what containers give you. On the other
hand, complexity is the apex predator. Docker on a dev machine is one thing.
Docker in CI is another. Docker on a teammate's Windows laptop that hasn't
updated in 6 months is yet another. Every layer you add is a layer that can
break.

**Delaney Gillilan continues**: BUT — if the local actor pool (Level 1) is
the DEFAULT, and container scale-out (Level 2) is OPT-IN for CI pipelines and
"overnight full-codebase" runs, then the complexity is contained. You don't
need Docker to use Molina. You only need it when local parallelism isn't
enough. That's acceptable.

**John Carmack**: The key question is: what's in the container image? If you
have to rebuild the entire project inside each container, you've lost the FSI
hot-swap advantage. The container needs to start with a WARM session — the
project already compiled, namespaces already opened, tests already loaded.
Otherwise container startup eats your parallelism gains.

**Don Syme**: This is solvable. SageFs already does warm-up: it loads the
solution, opens namespaces, loads assemblies. The container image should be
built AFTER that warm-up completes. Think of it as: the host SageFs warms up
once, snapshots the warm state (compiled DLLs, shadow copies, namespace list),
and the container image includes those artifacts. Each container then only
needs to create a fresh `FsiEvaluationSession` and load the pre-compiled
assemblies. That's seconds, not minutes.

**Ryan Fluery**: Don't snapshot FSI state — that's process memory, not
serializable. Snapshot the ARTIFACTS: compiled DLLs, the shadow copy directory,
the list of reference assemblies, the namespace opens script. Each container
runs `createFsiSession` with those artifacts. The warm-up in each container
is maybe 5-10 seconds — you're not recompiling, just loading pre-built DLLs
into a fresh FSI. With 20 containers, that 10-second startup is amortized
across hundreds of mutants.

**Houston Haynes**: Think bigger. Docker containers are one option. But the
same architecture works with Kubernetes jobs, Azure Container Instances,
GitHub Actions matrix builds. The MolinaOrchestrator doesn't need to know
it's Docker. It needs to know: "here's a pool of SageFs workers that speak
HTTP/MCP." The transport is already HTTP. The event store is already
PostgreSQL. The only new thing is: "start N workers" and "distribute work."

**The Primeagen**: I'll be the one to say it: this is awesome. The "many
worlds" thing isn't just a metaphor — it's literally what you're doing.
Each container is a parallel universe where one specific mutation exists.
You're querying 20 counterfactual realities simultaneously. That's peak
Molinism. BUT — ship Level 1 first. If you build Level 2 before Level 1
is proven, you'll debug distributed systems problems when you should be
debugging mutation operators.

**Mark Seemann**: The mathematical structure is clean. The MolinaOrchestrator
partitions the mutant set: `mutants |> List.splitInto N`. Each partition is
a pure value — a list of `MutantCandidate`. Each worker receives its partition,
runs it, emits events. The orchestrator doesn't need to track individual
mutants — the projection does that. The orchestrator just needs to know:
"are all workers done?" That's a simple countdown.

**Pim Brouwers**: For the HTTP transport, each container worker just needs
two endpoints:

```fsharp
// Worker API (each container)
POST /molina/batch    // Receives: { mutants: MutantCandidate list, config: MolinaConfig }
                      // Returns: SSE stream of MolinaEvent as results complete
GET  /molina/health   // Returns: { status: "ready" | "busy" | "faulted", mutantsCompleted: int }
```

That's it. The coordinator POSTs a batch to each worker, consumes the SSE
streams, and forwards events to Marten. Falco handles this trivially — each
worker is just another SageFs HTTP endpoint.

**Greg Holden**: And the dashboard gets REAL-TIME visibility into all workers.
Each worker's SSE stream feeds the same projection. The dashboard shows:

```
🧬 Molina — Many Worlds Mode (20 workers)

Worker  │ Status  │ Mutants  │ Progress │ Rate
────────┼─────────┼──────────┼──────────┼─────────
🟢  w-01 │ Running │ 100/100  │ ████████ │ 2.1/s
🟢  w-02 │ Running │  87/100  │ ███████░ │ 1.8/s
🟢  w-03 │ Running │  92/100  │ ████████ │ 1.9/s
🔴  w-04 │ Faulted │  43/100  │ ████░░░░ │ — (re-queued 57)
🟢  w-05 │ Running │  95/100  │ ████████ │ 2.0/s
   ...
🟢  w-20 │ Running │  78/100  │ ██████░░ │ 1.7/s

Overall: 1,847/2,000 mutants tested │ Score: 81.3% │ ETA: 38s
```

The grug brain in me says this is cool but DO NOT build it until Level 1
proves the concept.

**Ginger Bill**: I want to talk about the Docker image itself. Don't build a
FAT image with the full .NET SDK. Build a two-stage image:

1. **Build stage**: `mcr.microsoft.com/dotnet/sdk:10.0` — compile the project,
   produce DLLs + shadow copies
2. **Runtime stage**: `mcr.microsoft.com/dotnet/aspnet:10.0` + SageFs global
   tool — just the runner with pre-compiled artifacts

The runtime image should be ~200MB, not 2GB. Container startup is
image-pull-time + FSI-warmup. With a local image registry, pull is instant.
FSI warmup with pre-compiled DLLs is 5-10 seconds. 20 containers warm in
parallel — 10 seconds total, not 200.

**Casey Muratori**: Fine. I'll concede the architecture is sound IF: (1) Level 1
ships first and proves the concept, (2) Level 2 is opt-in, never default,
(3) the single-machine experience never requires Docker, and (4) you measure
actual wall-clock improvement before declaring victory. If 4 in-process
sessions handle 95% of real-world codebases, Level 2 is a vanity project.

**Aaron Stannard responds**: Agreed on all four. But I'll add: Level 2 isn't
just about speed. It's about ISOLATION. In-process sessions share the same
.NET runtime, the same GC, the same thread pool. If mutant #247 triggers a
`ThreadPool.QueueUserWorkItem` storm, all sessions suffer. Containers give you
OS-level isolation. Each worker has its own GC, its own threads, its own memory
space. A pathological mutant in container 7 can't affect container 8. That's
not vanity — that's engineering.

### Panel Consensus (Phase 7)

**Three-tier execution model:**

```
Level 0: Single Session (default, simplest)
  └─ One FSI session, sequential mutant execution
  └─ Use case: "quick check on this function" during dev
  └─ Latency: ~50ms/mutant + test time
  └─ No configuration needed

Level 1: Actor Pool (local parallel)
  └─ N in-process FSI sessions via MailboxProcessor actors
  └─ Use case: "run Molina on this module" during dev
  └─ Throughput: Level 0 ÷ N (default N = ProcessorCount/2, min 2, max 8)
  └─ No Docker needed, automatic

Level 2: Container Swarm (many-worlds, opt-in)
  └─ M Docker containers, each running Level 1 internally
  └─ Use case: CI pipeline, overnight full-codebase run, large monorepos
  └─ Throughput: Level 0 ÷ (M × N)
  └─ Requires: Docker, PostgreSQL (shared Marten event store)
  └─ Configuration: --molina-workers 20 or MOLINA_WORKERS=20
```

**Container architecture:**

```
  Host SageFs (MolinaOrchestrator)
       │
       ├── 1. Build project, produce DLLs + shadow copies
       ├── 2. Generate all mutant candidates (Discovery phase)
       ├── 3. Partition mutants into M batches
       ├── 4. docker compose up --scale molina-worker=M
       │
       ├──→ POST /molina/batch to worker-1  ──→ SSE events ──┐
       ├──→ POST /molina/batch to worker-2  ──→ SSE events ──┤
       ├──→ POST /molina/batch to worker-3  ──→ SSE events ──┤
       │    ...                                               │
       ├──→ POST /molina/batch to worker-M  ──→ SSE events ──┤
       │                                                      │
       │    ┌──────────────────────────────────────────────────┘
       │    │  All events → shared Marten stream
       │    │  Projection folds identically regardless of source
       │    ▼
       ├── MolinaProjection (same pure fold as Level 1)
       │    │
       │    ├──→ Dashboard SSE (real-time worker grid)
       │    ├──→ TUI pane (aggregate progress)
       │    ├──→ MCP tools (same API, worker count in metadata)
       │    └──→ CLI (same output format, parallel stats)
       │
       └── 5. docker compose down (cleanup)
```

**Compose template:**

```yaml
# compose.molina.yml — generated by MolinaOrchestrator
services:
  molina-worker:
    image: sagefs-worker:${SAGEFS_VERSION}
    build:
      context: .
      dockerfile: Dockerfile.molina-worker
    environment:
      SAGEFS_DB: "Host=host.docker.internal;Database=SageFs;Username=postgres;Password=SageFs"
      MOLINA_RUN_ID: ${RUN_ID}
    volumes:
      - ./molina-artifacts:/artifacts:ro  # pre-compiled DLLs, shadow copies
    deploy:
      replicas: ${MOLINA_WORKERS:-4}
    ports:
      - "37800-37820:37749"  # each worker gets unique host port
```

**Dockerfile.molina-worker:**

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# Install SageFs global tool
COPY --from=build /nupkg /nupkg
RUN dotnet tool install --global SageFs.cli --add-source /nupkg
ENV PATH="${PATH}:/root/.dotnet/tools"

# Copy pre-compiled artifacts (DLLs, shadow copies, warm-up script)
COPY molina-artifacts/ /artifacts/

# Worker mode: no TUI, no dashboard, just MCP + Molina batch endpoint
ENTRYPOINT ["SageFs", "--worker", "--artifacts", "/artifacts"]
```

**Key design decisions:**

- **Artifacts, not snapshots**: Don't try to serialize FSI session state.
  Ship compiled DLLs + a warmup script. Each container creates a fresh FSI
  and loads pre-built assemblies. 5-10 second warmup per container.
- **Shared Marten**: All containers write to the same PostgreSQL. The
  projection is source-agnostic — events from any container fold identically.
- **SSE for results**: Each worker streams `MolinaEvent`s back to the
  orchestrator via SSE. Same protocol the dashboard already uses. Datastar
  tao: server is source of truth, stream the state.
- **Fault tolerance**: Container dies → orchestrator detects SSE disconnect
  → re-queues unfinished mutants from that batch to surviving workers.
  Events already recorded which mutants were tested.
- **Cleanup**: `docker compose down` after run completes. No lingering
  containers. Artifacts directory is ephemeral.

**Level escalation is automatic in CI:**

```yaml
# GitHub Actions example
- name: Run Molina
  run: |
    sagefs --molina . --workers 20 --threshold 80
    # Automatically: builds artifacts, starts containers, runs, reports, cleans up
    # Exit code: 0 if score >= threshold, non-zero otherwise
```

**Muratori's four conditions (all met):**
1. ✅ Level 1 ships first — containers are Phase 7, not Phase 1
2. ✅ Level 2 is opt-in — `--workers N` flag, default is Level 1
3. ✅ Single-machine never needs Docker — Level 0 and 1 are in-process
4. ✅ Measure before declaring victory — wall-clock benchmarks in docs

**Stannard's isolation argument (accepted):** Container-level isolation
protects against pathological mutants (infinite loops, thread pool storms,
memory leaks) that would poison in-process sessions. This is a safety
feature, not just a performance feature.

---

## Final Architecture: Molina (Three-Tier)

```
                         ┌──────────────────────────┐
                         │     User Trigger          │
                         │  MCP / Dashboard / CLI /  │
                         │  TUI / FileWatch          │
                         └────────────┬─────────────┘
                                      │ RunMutations { level: 0|1|2 }
                                      ▼
                         ┌──────────────────────────┐
                         │   MolinaCoordinator       │ ← Aggregate root actor
                         │   (decides execution tier)│
                         └──┬──────────┬─────────┬──┘
                            │          │         │
                ┌───────────┘          │         └───────────┐
                ▼                      ▼                     ▼
       ┌────────────────┐    ┌────────────────┐    ┌────────────────┐
       │  Discovery      │    │  Baseline       │    │  Event Store   │
       │  Actor          │    │  Runner         │    │  (Marten)      │
       └────────┬───────┘    └────────┬───────┘    └───────┬────────┘
                │                     │                     │
                └─────────┬───────────┘                     │
                          ▼                                 │
              ┌─────────────────────┐                       │
              │  Execution Tier     │                       │
              │  (selected by level)│                       │
              └──┬──────┬────────┬─┘                       │
                 │      │        │                          │
    Level 0      │  Level 1      │  Level 2                 │
    ┌────────┐   │  ┌────────┐   │  ┌──────────────────┐    │
    │ Single │   │  │ Actor  │   │  │ Container Swarm  │    │
    │ FSI    │   │  │ Pool   │   │  │                  │    │
    │ Session│   │  │ N=2-8  │   │  │  ┌─────┐ ┌─────┐│    │
    └───┬────┘   │  │in-proc │   │  │  │ w-1 │ │ w-2 ││    │
        │        │  └──┬─────┘   │  │  │ L1  │ │ L1  ││    │
        │        │     │         │  │  └──┬──┘ └──┬──┘│    │
        │        │     │         │  │  ┌──┴──┐ ┌──┴──┐│    │
        │        │     │         │  │  │ w-M │ │ ... ││    │
        │        │     │         │  │  │ L1  │ │     ││    │
        │        │     │         │  │  └──┬──┘ └─────┘│    │
        │        │     │         │  └─────┼───────────┘    │
        └────────┴─────┴─────────┴────────┘                │
                          │ MolinaEvent stream              │
                          └────────────────┬───────────────┘
                                           │
                                           ▼
                                  ┌────────────────┐
                                  │  Molina         │ ← Pure fold
                                  │  Projection     │   (same at all tiers)
                                  └──┬──┬──┬──┬──┬┘
                                     │  │  │  │  │
                      ┌──────────────┘  │  │  │  └──────────────┐
                      ▼                 ▼  │  ▼                  ▼
                ┌──────────┐  ┌──────────┐│┌──────────┐  ┌──────────┐
                │Dashboard  │  │  TUI     │││ Raylib   │  │  CLI     │
                │SSE/DS     │  │  Pane    │││ Pane     │  │  stdout  │
                └──────────┘  └──────────┘│└──────────┘  └──────────┘
                                          ▼
                                   ┌──────────┐
                                   │  MCP     │
                                   │  Tools   │
                                   └──────────┘
```

### Module Structure

```fsharp
// SageFs.Core/Features/Molina/

MolinaTypes.fs          // Core types: MutantId, MolinaEvent, MolinaRunState, MutationScore
MutationOperator.fs     // DU of operators + SynExpr -> MutantCandidate list
MutantGenerator.fs      // Walk AST, apply operators, produce mutant list
MolinaProjection.fs     // Pure fold: MolinaRunState + MolinaEvent -> MolinaRunState
MolinaCoordinator.fs    // Coordinator actor: lifecycle management
MolinaSessionPool.fs    // Session pool actor: manages N warm FSI sessions
MolinaMutantRunner.fs   // Runner actor: hot-swap + test per session
MolinaReport.fs         // Score calculation, CLI formatting, test suggestions

// Integration points:
Mcp.fs                  // Add sagefs-molina_* tools
Dashboard.fs            // Add /molina/* routes + SSE panel
MolinaPaneRenderer.fs   // Shared TUI/Raylib pane (Cell grid)
```

### Key Types

```fsharp
type MutantId = MutantId of string

type MutationOperator =
  | ConditionalBoundary  // > ↔ >=, < ↔ <=, = ↔ <>
  | BooleanLogic         // && ↔ ||
  | Negate               // if x → if not x
  | Arithmetic           // + ↔ -, * ↔ /
  | Literal              // true ↔ false, n → n±1
  | ReturnValue          // Some → None, Ok → Error
  | StatementDeletion    // body → failwith "mutant"

type MutantCandidate = {
  Id: MutantId
  FilePath: string
  Location: FSharp.Compiler.Text.Range
  Operator: MutationOperator
  OriginalCode: string
  MutatedCode: string
  FunctionName: string
}

type MutantResult =
  | Killed of testName: string * duration: TimeSpan
  | Survived of testsRun: int * duration: TimeSpan
  | TimedOut of duration: TimeSpan
  | CompileError of message: string
  | NoCoverage

/// The event stream — source of truth for every Molina run.
/// Each run is a Marten event stream keyed by RunId.
type MolinaEvent =
  | RunRequested of {| Target: string; Operators: MutationOperator list; Timestamp: DateTimeOffset |}
  | BaselineCompleted of {| TestCount: int; PassCount: int; FailCount: int; Duration: TimeSpan; TestMap: Map<string, string list> |}
  | MutantDiscovered of {| Id: MutantId; FilePath: string; Line: int; Operator: MutationOperator; OriginalCode: string; MutatedCode: string; FunctionName: string |}
  | MutantQueued of {| Id: MutantId; SessionId: string |}
  | MutantTested of {| Id: MutantId; Result: MutantResult; Duration: TimeSpan |}
  | MutantSkipped of {| Id: MutantId; Reason: string |}
  | SessionFaulted of {| SessionId: string; Error: string |}
  | SessionReplaced of {| OldSessionId: string; NewSessionId: string |}
  | RunCompleted of {| Score: MutationScore; Duration: TimeSpan |}
  | RunFailed of {| Error: string; LastMutantId: MutantId option |}

type MolinaRunStatus =
  | Idle
  | Discovering
  | Baselining
  | Running
  | Completed
  | Failed

/// The projection — built by pure fold over MolinaEvent stream.
/// Every UI reads from this. No UI has special state.
type MolinaRunState = {
  RunId: string
  Status: MolinaRunStatus
  Target: string
  StartedAt: DateTimeOffset option
  CompletedAt: DateTimeOffset option
  TotalMutants: int
  TestedCount: int
  Killed: int
  Survived: int
  TimedOut: int
  CompileErrors: int
  NoCoverage: int
  Skipped: int
  CurrentMutant: MutantId option
  Mutants: Map<MutantId, MutantState>
  TestMap: Map<string, string list>  // test name → functions it exercises
  ActiveSessions: int
  Score: MutationScore option
  Duration: TimeSpan
}

type MutantState =
  | Pending of MutantCandidate
  | Queued of MutantCandidate * sessionId: string
  | Tested of MutantCandidate * MutantResult
  | Skipped of MutantCandidate * reason: string

type MutationScore = {
  Total: int
  Killed: int
  Survived: int
  TimedOut: int
  NoCoverage: int
  CompileErrors: int
  Score: float  // killed / (killed + survived)
  Duration: TimeSpan
}

module MolinaProjection =
  let empty = {
    RunId = ""; Status = Idle; Target = ""
    StartedAt = None; CompletedAt = None
    TotalMutants = 0; TestedCount = 0
    Killed = 0; Survived = 0; TimedOut = 0
    CompileErrors = 0; NoCoverage = 0; Skipped = 0
    CurrentMutant = None; Mutants = Map.empty
    TestMap = Map.empty; ActiveSessions = 0
    Score = None; Duration = TimeSpan.Zero
  }

  /// Pure fold. This is the ONLY place MolinaRunState is computed.
  /// Tested, replayable, composable. A monoid over events.
  let apply (state: MolinaRunState) (event: MolinaEvent) : MolinaRunState =
    match event with
    | RunRequested e ->
      { empty with RunId = state.RunId; Status = Discovering; Target = e.Target; StartedAt = Some e.Timestamp }
    | BaselineCompleted e ->
      { state with Status = Running; TestMap = e.TestMap }
    | MutantDiscovered e ->
      let candidate = { Id = e.Id; FilePath = e.FilePath; Location = Unchecked.defaultof<_>; Operator = e.Operator; OriginalCode = e.OriginalCode; MutatedCode = e.MutatedCode; FunctionName = e.FunctionName }
      { state with TotalMutants = state.TotalMutants + 1; Mutants = state.Mutants |> Map.add e.Id (Pending candidate) }
    | MutantQueued e ->
      let mutants = state.Mutants |> Map.change e.Id (Option.map (function Pending c -> Queued (c, e.SessionId) | other -> other))
      { state with Mutants = mutants; CurrentMutant = Some e.Id }
    | MutantTested e ->
      let mutants = state.Mutants |> Map.change e.Id (Option.map (function Queued (c, _) | Pending c -> Tested (c, e.Result) | other -> other))
      let k, s, t, ce, nc = state.Killed, state.Survived, state.TimedOut, state.CompileErrors, state.NoCoverage
      let k, s, t, ce, nc =
        match e.Result with
        | Killed _ -> k+1, s, t, ce, nc
        | Survived _ -> k, s+1, t, ce, nc
        | TimedOut _ -> k, s, t+1, ce, nc
        | CompileError _ -> k, s, t, ce+1, nc
        | NoCoverage -> k, s, t, ce, nc+1
      { state with Mutants = mutants; TestedCount = state.TestedCount + 1; Killed = k; Survived = s; TimedOut = t; CompileErrors = ce; NoCoverage = nc }
    | MutantSkipped e ->
      let mutants = state.Mutants |> Map.change e.Id (Option.map (function Pending c -> Skipped (c, e.Reason) | other -> other))
      { state with Mutants = mutants; Skipped = state.Skipped + 1 }
    | RunCompleted e ->
      { state with Status = Completed; Score = Some e.Score; CompletedAt = Some DateTimeOffset.UtcNow; Duration = e.Duration }
    | RunFailed _ ->
      { state with Status = Failed }
    | _ -> state
```

### The Killer Feature: FSI Hot-Swap in Actor Pool

```fsharp
// Each MutantRunnerActor owns one FSI session and processes mutants serially.
// Multiple runners execute in parallel across the session pool.
type MutantRunnerActor(sessionId: string, fsi: FsiEvaluationSession, emit: MolinaEvent -> unit) =
  let actor = MailboxProcessor.Start(fun mailbox ->
    let rec loop () = async {
      let! (mutant: MutantCandidate, timeout: TimeSpan) = mailbox.Receive()
      emit (MolinaEvent.MutantQueued {| Id = mutant.Id; SessionId = sessionId |})

      // 1. Hot-swap: redefine function with mutation
      let redefineCode = sprintf "let %s = %s" mutant.FunctionName mutant.MutatedCode
      use cts = new CancellationTokenSource(timeout)
      let result, _ = fsi.EvalInteractionNonThrowing(redefineCode, cts.Token)

      match result with
      | Choice2Of2 ex ->
        emit (MolinaEvent.MutantTested {| Id = mutant.Id; Result = CompileError ex.Message; Duration = TimeSpan.Zero |})
      | Choice1Of2 _ ->
        // 2. Run affected tests
        let sw = Diagnostics.Stopwatch.StartNew()
        let testResult = runAffectedTests fsi mutant.FunctionName cts.Token
        sw.Stop()
        emit (MolinaEvent.MutantTested {| Id = mutant.Id; Result = testResult; Duration = sw.Elapsed |})

      // 3. Restore original function (always, even on error)
      let restoreCode = sprintf "let %s = %s" mutant.FunctionName mutant.OriginalCode
      let _, _ = fsi.EvalInteractionNonThrowing(restoreCode, CancellationToken.None)

      return! loop ()
    }
    loop ()
  )
  member _.Post(mutant, timeout) = actor.Post(mutant, timeout)
```

Per-mutant cost: ~50ms (hot-swap) + test execution time. With 4 parallel
sessions, throughput is 4× a single session. For a module with 200 mutants
and 2-second affected-test runs: `200 × 2s / 4 sessions = 100 seconds`.
Stryker equivalent: `200 × 5s = 1000 seconds`. **10× faster at minimum.**

---

## Dissenting Opinions (recorded for future reference)

**Casey Muratori** remains skeptical of the full AST approach for v1. He'd
prefer a text-regex prototype to validate the concept before investing in AST
infrastructure. The panel overrode this because FCS parsing is already available
and the untyped AST is simpler than regex for F#'s syntax.

**Ginger Bill** thinks the managed runtime overhead of .NET GC will matter at
scale (thousands of mutants). The panel notes this should be measured, not
assumed. If GC pressure becomes real, arena-style batching of mutant allocations
is straightforward in F# using `ArrayPool` or struct records.

**Casey Muratori** also initially opposed the actor model and session pooling
("you're designing a distributed system to test 200 lines of code"). He was
persuaded by the wall-clock argument: session startup is amortized during
discovery, and parallel execution is 4× throughput for free. He still insists
on measuring single-session performance first to establish the baseline.

**Houston Haynes** sees a longer-term opportunity: compile F# mutations to
native code via MLIR/LLVM for faster mutation execution. The panel agrees this
is interesting but wildly out of scope. Molina should prove the concept in
FSI first.

**The Primeagen** worries the CQRS/event-sourcing infrastructure will become
more code than the actual mutation logic. The panel's response: Marten does
the heavy lifting. The projection is ~50 lines. The event types are ~20 lines.
The actors are the same `MailboxProcessor` pattern SageFs already uses. The
marginal infrastructure cost is low.

---

## Implementation Order

### Tier 1: Core Molina (Level 0 — single session)
1. **MolinaTypes.fs** — core types (events, state, operators, results)
2. **MolinaProjection.fs** — pure fold with property-based tests proving monoid laws
3. **MutationOperator.fs** — 7 operators as `SynExpr -> MutantCandidate list`
4. **MutantGenerator.fs** — AST walker that applies operators to a parsed file
5. **MolinaMutantRunner.fs** — single-session runner: hot-swap + test + restore
6. **MolinaCoordinator.fs** — coordinator actor: lifecycle, dispatch, event emission
7. **MolinaReport.fs** — score calculation, CLI formatting

### Tier 2: Local Parallelism (Level 1 — actor pool)
8. **MolinaSessionPool.fs** — session pool actor with warm-up during discovery
9. Update MolinaMutantRunner to work as actor receiving from pool
10. Update MolinaCoordinator to partition and distribute across pool

### Tier 3: Integration (all UIs)
11. **Mcp.fs integration** — `sagefs-molina_*` MCP tools (5 tools)
12. **MolinaPaneRenderer.fs** — shared TUI/Raylib pane (Cell grid)
13. **Dashboard integration** — `/molina/*` routes + SSE panel
14. **CLI integration** — `--molina` flag + exit codes

### Tier 4: Many Worlds (Level 2 — container swarm)
15. **Dockerfile.molina-worker** — two-stage image, pre-compiled artifacts
16. **compose.molina.yml** — template with scalable replicas
17. **MolinaOrchestrator.fs** — container lifecycle: start, partition, collect, cleanup
18. **Worker HTTP endpoint** — `POST /molina/batch` + SSE result stream
19. **Fault recovery** — SSE disconnect detection, mutant re-queuing
20. **CLI `--workers N`** — trigger Level 2 from command line

### Tier 5: Continuous Mode (future)
21. File watcher triggers Molina on changed functions
22. Middleware integration for post-eval mutation checking
23. Editor gutter annotations via MCP
24. Mutation score ratchet (score must not decrease between commits)

**Tests at every layer:**
- **Projection**: property-based — fold is a monoid (associative, has identity)
- **Operators**: snapshot — known F# code → known mutant candidates
- **Generator**: property-based — every mutation of valid code either compiles or doesn't
- **Runner**: integration — known mutant + known test → expected result
- **Actor pool**: integration — verify parallel execution produces same results as serial
- **Container**: integration — verify distributed execution matches local
- **End-to-end**: a module with known gaps → Molina finds them at every level

**Tests at every layer:**
- **Projection**: property-based — fold is a monoid (associative, has identity)
- **Operators**: snapshot — known F# code → known mutant candidates
- **Generator**: property-based — every mutation of valid code either compiles or doesn't
- **Runner**: integration — known mutant + known test → expected result
- **End-to-end**: a module with known gaps → Molina finds them

---

## Epistemic Warrant: What Molina Proves

Molina doesn't prove your code is correct. It proves your TESTS have earned
confidence. The distinction matters:

- **100% mutation score**: Every mutation you tested was caught. Your tests
  have warrant for the claims they make. But the mutation operators you
  didn't implement are still blind spots.

- **80% mutation score**: 20% of possible defects would slip past your tests.
  You know WHERE the gaps are. You can decide if they matter.

- **50% mutation score**: Your tests are security theater. They pass, they
  give you green checkmarks, but they'd miss half the bugs. Molina just
  told you what your test suite is actually worth.

The mutation score is not a metric to optimize. It's an **epistemic audit**.
It tells you the difference between what you THINK your tests cover and what
they ACTUALLY cover. That gap is where bugs live.

## Scientia Media: The Three Kinds of Knowledge

Molinism distinguishes three kinds of divine knowledge. Molina maps them
to mutation testing:

1. **Natural knowledge** (*scientia naturalis*) — what COULD go wrong.
   This is the set of all possible mutations. It's combinatorially vast.
   You can't test them all. But you can enumerate the important ones.

2. **Middle knowledge** (*scientia media*) — what WOULD go wrong under
   specific counterfactual conditions. This is what Molina actually computes.
   "If this `>` were `>=`, would your test suite notice?" Each mutant is a
   counterfactual. Each test result is middle knowledge.

3. **Free knowledge** (*scientia libera*) — what WILL go wrong in production.
   This is what you're trying to prevent. You can't know it directly. But
   middle knowledge — knowing what your tests WOULD catch — is the best
   proxy. The more counterfactuals you've tested, the more warrant you have
   for believing your tests will catch the bugs that matter.

**Level 2 (Many Worlds) is the full expression of this:** 20 containers,
each exploring a different partition of counterfactual space. Simultaneously.
In parallel. Every surviving mutant is a world where a bug shipped to
production because your tests didn't have the warrant to catch it.

That's Molina. Middle knowledge for your codebase. Epistemic warrant for
your confidence. Many worlds explored in parallel. The only F# mutation
testing tool in existence — not because it was easy, but because SageFs
had the warm FSI session, the actor model, the event store, and the
audacity to build it.

---

## Phase 8: Protocol Architecture — MCP + A2A + ACP

> *SageFs currently speaks one protocol: MCP. That's tool integration — an LLM
> calls SageFs's tools, gets results. But Molina Level 2 needs SageFs instances
> to talk to EACH OTHER. And the broader vision — SageFs as a hub in a
> multi-agent ecosystem — requires it to be DISCOVERABLE, COMPOSABLE, and able
> to DELEGATE. Three protocols exist for three different problems. The panel
> argues about all of them.*

### The Three Protocols

| | **MCP** | **A2A** | **ACP** |
|---|---|---|---|
| **By** | Anthropic | Google → Linux Foundation | IBM/BeeAI → Linux Foundation |
| **Spec version** | 2025-03-26 | RC v1.0 (from 0.3.0) | 1.0 |
| **Solves** | LLM ↔ Tool integration | Agent ↔ Agent collaboration | Agent ↔ Agent interop |
| **Transport** | JSON-RPC 2.0 (stdio/SSE/HTTP) | JSON-RPC 2.0 + gRPC + REST | REST + SSE |
| **Model** | Client-server, stateful | Task-based, async-first, opaque | Run-based, async-first |
| **Key primitive** | Tools, Resources, Prompts, Sampling | Agent Cards, Tasks, Artifacts | Agents, Runs, Messages |
| **Discovery** | Manual configuration | `.well-known/agent-card.json` + registries | Package metadata (offline) |
| **Streaming** | SSE (custom) | SSE (standardized lifecycle) | SSE |
| **Push notifications** | ❌ | ✅ Webhooks with HMAC/JWT | ❌ |
| **Bidirectional** | Sampling (server → LLM) | Tasks can request input | Runs can await input |
| **.NET SDK** | `ModelContextProtocol` (official) | `A2A` + `A2A.AspNetCore` (official) | ❌ (Python/TS only) |
| **SageFs today** | ✅ Full (McpServer.fs, McpTools.fs) | ❌ | ❌ |

### Expert Deliberation

**Don Syme** opens: The fundamental question isn't which protocol to add — it's
what ROLE each protocol plays in SageFs's architecture. MCP makes SageFs a tool
that LLMs use. A2A makes SageFs an agent that OTHER agents collaborate with.
ACP does roughly the same thing as A2A but with different philosophical
assumptions. We need to be precise about the semantic difference, not just the
wire format difference.

**Delaney Gillilan** immediately: The semantic difference maps perfectly to the
Tao of Datastar. MCP is like a form submission — request/response, the client
is in control. A2A is like an SSE subscription — the server has agency, it
pushes when it has something to say, the task has a lifecycle. ACP is... also
an SSE subscription but with a REST accent. The real question is: what happens
when SageFs needs to PUSH, not just respond? When a Molina run completes at
3 AM, who gets told?

**Greg Holden**: We already have two SSE streams in McpServer.fs — `/events`
for state changes and `/diagnostics` for diagnostic updates. The Dashboard has
a third at `/dashboard/stream`. These are all custom, ad-hoc SSE. What A2A
gives us is a STANDARDIZED lifecycle for those streams. Tasks have states:
`submitted → working → input-required → completed/failed/canceled`. Right now
our SSE events are just "here's some JSON, figure it out." A2A makes them
semantically typed.

**Jeremy Miller** pushes back: We already HAVE semantically typed events. That's
what `SageFsEvent` is. `EvalRequested`, `EvalCompleted`, `SessionFaulted` —
these are proper domain events in Marten. The event store IS our task lifecycle.
Adding A2A's `Task` abstraction on top of our event stream is a second source
of truth unless we're very careful.

**Roger Johansson**: This is where the actor model earns its keep. Each protocol
becomes an actor. MCP requests come in, get translated to `Command` messages on
the existing `AppActor`. A2A tasks come in, get translated to the SAME
`Command` messages. ACP runs come in — same thing. The protocol layer is a
BOUNDARY, not a brain. All three protocols are just different envelopes around
the same `WorkerMessage` discriminated union.

**Stannard agrees strongly**: Location transparency! This is exactly the pattern.
`WorkerMessage` is your internal message format. MCP, A2A, and ACP are external
serialization formats for the same messages. The actor doesn't care which
envelope it arrived in. And this is PRECISELY what Molina Level 2 needs —
the coordinator sends `WorkerMessage.EvalCode` to a runner. Whether that runner
is local (in-process MailboxProcessor), or remote (HTTP worker), or a container
(A2A agent) — same message, different transport.

**Casey Muratori** cuts through: Hold on. I'm counting three protocols, each
with their own discovery mechanism, their own authentication model, their own
streaming format, their own error shapes. That's not "three envelopes around
the same message." That's three entirely separate API surfaces that all need to
be maintained, tested, documented, and debugged. What's the concrete problem
that adding TWO more protocols solves that one protocol can't?

**John Carmack backs Muratori**: I want to hear the specific scenario. Give me
the user story where MCP alone fails.

**Syme**: Three scenarios where MCP breaks down:

1. **Molina Level 2 coordination.** MCP is LLM → tool. The Molina coordinator
   isn't an LLM — it's a SageFs instance. When it needs to distribute mutant
   batches to 20 container workers, MCP has no vocabulary for this. There's no
   "I'm an agent discovering other agents." There's no "here's a task, work on
   it, stream me progress." There's just `tools/call` and `tools/list`.

2. **SageFs-to-SageFs collaboration.** Imagine two developers, each running
   SageFs on their own machines, working on the same Marten event store. One
   runs a Molina analysis, discovers 15 surviving mutants. The other developer's
   SageFs should be NOTIFIED — "hey, there are epistemic gaps in modules you're
   working on." MCP can't initiate that conversation. Sampling can't either —
   it asks the LLM to generate text, not to route messages to other tools.

3. **Agent marketplace discovery.** When SageFs publishes an Agent Card at
   `/.well-known/agent-card.json` saying "I can run F# mutation testing," any
   A2A-compliant orchestrator — GitHub Copilot Workspace, a CI/CD agent,
   another team's custom agent — can DISCOVER SageFs and delegate mutation
   testing to it. MCP has zero discovery story. Servers are manually configured
   in JSON files.

**Carmack**: Scenario 1 is real. Scenarios 2 and 3 are speculative. I'd
implement A2A for Molina Level 2 ONLY and defer the rest until there's a real
user pulling for it.

**Muratori**: Agreed. And ACP? What does ACP give us that A2A doesn't?

**Ginger Bill**: ACP is REST-native. No JSON-RPC ceremony. If you have an HTTP
client, you have an ACP client. No special SDK required. That's appealing for
the "SageFs as a service" story — if someone wants to call SageFs from curl,
from a shell script, from a language without A2A or MCP SDKs, ACP's
`POST /runs` endpoint is dead simple. It's the UNIX philosophy — everything is
a stream of bytes over HTTP.

**Pim Brouwers** reinforces Bill: Falco IS thin HTTP. Falco routes are just
`HttpHandler` functions. Adding ACP endpoints is trivially easy in Falco —
it's basically three routes: `GET /agents`, `POST /runs`, `GET /runs/{id}`.
This is the kind of thing Falco was designed for. I'd argue ACP is the most
natural fit for Falco's philosophy — minimal abstraction over the HTTP platform.

**Seemann** challenges both: REST is a constraint system, not just "HTTP
endpoints." True REST means HATEOAS — the response tells you what you can do
next via links. Neither ACP nor A2A are actually RESTful in the Roy Fielding
sense. They're HTTP-based RPC with JSON payloads. Calling it "REST" is
marketing. That said, the simplicity argument is real. ACP's lack of JSON-RPC
ceremony means fewer failure modes and easier debugging.

**Scott Wlaschin**: I want to zoom out. We're arguing about wire formats when
the real design question is: what's the DOMAIN MODEL for multi-protocol
support? In F#, this should be a discriminated union:

```fsharp
type ProtocolEnvelope =
  | McpRequest of method: string * JsonElement
  | A2ATask of taskId: string * Message
  | AcpRun of runId: string * MimeContent
  | InternalCommand of WorkerMessage

type ProtocolResponse =
  | McpResult of JsonElement
  | A2ATaskUpdate of TaskStatus * Artifact list
  | AcpRunUpdate of RunStatus * MimeContent
  | InternalResult of WorkerResponse
```

Every protocol adapter translates its wire format into `ProtocolEnvelope`,
routes to the same domain logic, and translates back. The domain doesn't know
or care which protocol is speaking. This is the CQRS write side — commands come
in many shapes but hit the same aggregate.

**Gillilan**: And the read side is SSE everywhere. MCP already streams via SSE.
A2A streams via SSE. ACP streams via SSE. Datastar streams via SSE. It's SSE
all the way down. The response format differs but the transport is identical.
We should have ONE SSE infrastructure that all protocols share, not four
separate SSE implementations.

**Houston Haynes**: This is the CLEF insight. Polyglot persistence at the
protocol level. The event store doesn't care what language spoke to it. The
projection doesn't care what protocol generated the event. Marten stores
`SageFsEvent` — extend it with a `source: ProtocolSource` field:

```fsharp
type ProtocolSource =
  | Mcp of toolName: string
  | A2A of taskId: string * agentName: string
  | Acp of runId: string
  | Dashboard
  | Console
  | Internal

type SageFsEvent = {
  // ... existing fields ...
  Source: ProtocolSource
}
```

Now every event knows its provenance. The Molina projection can filter: "show
me only mutations triggered by A2A agents." The Dashboard can show: "this
session has 3 MCP clients, 2 A2A agents, and 1 ACP automation connected."

**Primeagen**: OK but can we talk about LATENCY? MCP's JSON-RPC over SSE has
observable overhead — that's literally why we're in this document, the original
`get_fsi_status` hang issue. A2A adds MORE ceremony — Agent Cards, task
lifecycle, push notifications. If I'm a Molina coordinator sending 500 mutant
batches per second to workers, I need microsecond dispatch, not millisecond
protocol negotiation.

**TJ DeVries**: This is the "hot path vs cold path" distinction. Protocol
negotiation (discovery, auth, capability exchange) happens ONCE at connection
setup — that's the cold path, latency doesn't matter. Mutant dispatch happens
thousands of times — that's the hot path. On the hot path, you want the
thinnest possible wire format. Raw HTTP POST with minimal JSON beats JSON-RPC
with id tracking and method routing.

**Fluery**: Which means the RIGHT answer for Molina Level 2 is: use A2A for
DISCOVERY and LIFECYCLE (coordinator finds workers, creates tasks, monitors
completion), but use a THIN internal protocol for DISPATCH (raw POST with
`WorkerMessage` serialized to minimal JSON). Don't run 500 mutant batches
through A2A's task lifecycle — that's 500 tasks with 500 status streams. Run
ONE A2A task per worker ("process this batch of 50 mutants"), and within that
task, use the existing `WorkerMessage` protocol for individual mutant dispatch.

**Carmack**: THAT is the right architecture. Two layers:

1. **Outer layer (A2A):** Discovery, authentication, task lifecycle. One task
   per worker per run. Progress streamed via A2A's SSE. This is the 10-second
   timescale — "worker started", "worker 40% complete", "worker finished."

2. **Inner layer (WorkerProtocol):** Raw `WorkerMessage` over HTTP, same as
   today's `WorkerHttpTransport.fs`. This is the 50-millisecond timescale —
   individual mutant eval/result cycles. No protocol overhead.

The A2A task wraps the entire worker lifetime. The WorkerProtocol handles the
per-mutant work. You get the benefits of A2A (discovery, monitoring, auth)
without its overhead on the hot path.

**Stannard**: And the actor model makes this clean. The `MolinaCoordinator`
actor creates A2A tasks. Each A2A task maps to a `MolinaBatchActor` that owns
N mutants. Inside that actor, individual mutant dispatch uses `WorkerMessage`.
The actor doesn't care about the protocol — it sends commands and receives
events.

**Miller**: The Marten event stream captures BOTH levels. A2A task lifecycle
events (`WorkerDiscovered`, `BatchAssigned`, `BatchCompleted`) go in the stream
alongside individual mutant events (`MutantTested`, `MutantSurvived`). The
projection folds both. The Dashboard shows both. One truth, many resolutions.

### The ACP Question

**Muratori**: So where does ACP fit in this two-layer model? We've got MCP for
LLM integration, A2A for agent collaboration, and the inner WorkerProtocol for
hot-path dispatch. What's ACP's role?

**Bill**: ACP is the "everyone else" protocol. Not every consumer is an LLM
(MCP) or a sophisticated agent (A2A). Some consumers are shell scripts. CI/CD
pipelines. Monitoring dashboards. Simple HTTP clients that want to say
`POST /agents/molina/runs` and get back results via SSE. ACP is the protocol
you implement so that `curl` works.

**Brouwers**: And in Falco, ACP routes are almost free. Three endpoints:

```fsharp
// ACP Agent Discovery
get "/acp/agents" (fun ctx ->
  let agents = getRegisteredAgents()
  Response.ofJson agents ctx)

// ACP Create Run
post "/acp/agents/{name}/runs" (fun ctx ->
  let name = Route.get "name" ctx
  let input = Request.getBody ctx
  let runId = createRun name input
  Response.ofJson { id = runId; status = "running" } ctx)

// ACP Stream Run
get "/acp/agents/{name}/runs/{id}" (fun ctx ->
  let id = Route.get "id" ctx
  streamRunEvents id ctx) // SSE
```

**Seemann**: The risk is that ACP becomes a maintenance burden with no users.
You build three REST endpoints, write tests for them, document them, handle
edge cases — and nobody calls them because everyone's using MCP or A2A. The
simplicity argument cuts both ways: simple to BUILD, but also simple to IGNORE.

**Wlaschin**: Counter-argument: ACP is the TESTING protocol. When you're
debugging SageFs's protocol layer, you don't want to spin up a JSON-RPC client
or an A2A agent. You want to `curl -X POST localhost:37749/acp/agents/repl/runs
-d '{"code": "1+1"}' `. ACP gives you that for free. It's the "printf
debugging" of agent protocols.

**Carmack**: That's actually a strong argument. I've seen too many systems where
the only way to test is through the production protocol stack, which means you
need a full client to send a single request. If ACP is the "bare metal HTTP"
protocol, it earns its keep as a debugging and integration testing surface.

**Miller**: And for Marten's sake, it's another event source. `ProtocolSource.Acp`
in the event stream. If a CI pipeline triggers a Molina run via ACP every night,
those events are in the store. The projection shows nightly trend lines. The
Dashboard renders them. Zero extra code.

### Agent Card Design

**Gillilan**: Let's get concrete about the Agent Card. This is SageFs's digital
business card — what it tells the world it can do. A2A spec says it goes at
`/.well-known/agent-card.json`:

```json
{
  "name": "SageFs",
  "description": "F# REPL, mutation testing engine, and development environment",
  "url": "http://localhost:37749/a2a",
  "version": "1.0.0",
  "provider": {
    "organization": "SageFs"
  },
  "capabilities": {
    "streaming": true,
    "pushNotifications": false
  },
  "defaultInputModes": ["text", "application/json"],
  "defaultOutputModes": ["text", "application/json"],
  "skills": [
    {
      "id": "fsharp-eval",
      "name": "F# Code Evaluation",
      "description": "Evaluate F# code in a warm FSI session with full IntelliSense",
      "inputModes": ["text"],
      "outputModes": ["text", "application/json"],
      "examples": [
        {
          "input": "let x = 42\nprintfn \"%d\" x",
          "output": "42"
        }
      ]
    },
    {
      "id": "molina-mutation-test",
      "name": "Molina Mutation Testing",
      "description": "Run mutation testing on F# code. Discovers mutants via FCS AST, executes via hot-swap FSI, reports epistemic warrant.",
      "inputModes": ["application/json"],
      "outputModes": ["application/json", "text"],
      "examples": [
        {
          "input": "{\"targetFiles\": [\"src/Domain.fs\"], \"testProject\": \"Tests.fsproj\"}",
          "output": "{\"score\": 0.87, \"killed\": 52, \"survived\": 8, \"totalMutants\": 60}"
        }
      ]
    },
    {
      "id": "molina-worker",
      "name": "Molina Worker",
      "description": "Process a batch of mutation testing candidates. Used by Molina coordinators for distributed Level 2 testing.",
      "inputModes": ["application/json"],
      "outputModes": ["application/json"]
    },
    {
      "id": "fsharp-diagnostics",
      "name": "F# Diagnostics",
      "description": "Type-check F# code and return compiler diagnostics without evaluation",
      "inputModes": ["text"],
      "outputModes": ["application/json"]
    }
  ],
  "security": [
    {
      "type": "http",
      "scheme": "bearer",
      "description": "Bearer token for authenticated access"
    }
  ]
}
```

**Syme**: Notice the `molina-worker` skill. That's how a Molina coordinator
DISCOVERS available workers. It queries `/.well-known/agent-card.json` on each
known host — or queries a registry — and any SageFs instance advertising the
`molina-worker` skill becomes a candidate for batch distribution. The skill
declaration IS the protocol contract.

**Johansson**: And in Akka.NET terms, the Agent Card is the actor's "props" —
it describes what messages the actor can receive and what it produces. The
registry is the actor system's address book. A2A formalized what actor systems
have been doing for decades.

### Unified Protocol Router

**Wlaschin**: Here's the concrete F# architecture. One router, three protocols,
same domain:

```fsharp
module ProtocolRouter

open SageFs.Core

/// Every protocol request normalizes to this
type ProtocolRequest = {
  Source: ProtocolSource
  SessionId: string option
  Command: WorkerMessage
  StreamResponse: bool
}

/// Every protocol response originates from this
type ProtocolResult = {
  Response: WorkerResponse
  Events: SageFsEvent list
  Duration: TimeSpan
}

/// Normalize MCP tool call → ProtocolRequest
let fromMcp (method: string) (args: JsonElement) : ProtocolRequest =
  let command = McpAdapter.toWorkerMessage method args
  { Source = Mcp method
    SessionId = McpAdapter.extractSessionId args
    Command = command
    StreamResponse = false }

/// Normalize A2A message → ProtocolRequest
let fromA2A (taskId: string) (message: A2A.Message) : ProtocolRequest =
  let command = A2AAdapter.toWorkerMessage message
  { Source = A2A (taskId, message.Role)
    SessionId = A2AAdapter.extractSessionId message
    Command = command
    StreamResponse = true }

/// Normalize ACP run → ProtocolRequest
let fromAcp (runId: string) (content: byte[]) (mime: string) : ProtocolRequest =
  let command = AcpAdapter.toWorkerMessage content mime
  { Source = Acp runId
    SessionId = AcpAdapter.extractSessionId content mime
    Command = command
    StreamResponse = true }

/// Route to domain — protocol-agnostic
let execute (proxy: SessionProxy) (req: ProtocolRequest) : Async<ProtocolResult> =
  async {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let! response = proxy req.Command
    sw.Stop()
    let events = EventBuilder.fromResult req.Source response
    return { Response = response; Events = events; Duration = sw.Elapsed }
  }
```

**Muratori**: That's clean. The `execute` function has ZERO protocol knowledge.
It takes a `SessionProxy` (which is just `WorkerMessage -> Async<WorkerResponse>`)
and a normalized request. The protocol adapters are boundary code — they parse
wire formats. The domain is pure.

**Carmack**: And testable. You can unit-test `fromMcp`, `fromA2A`, `fromAcp`
independently with mock JSON. You can test `execute` with a fake `SessionProxy`.
No HTTP servers needed for the protocol logic tests.

### SSE Infrastructure Unification

**Gillilan**: Now the read side. We have FOUR SSE consumers today:

1. **MCP SSE** (`/sse`) — MCP protocol stream
2. **Events SSE** (`/events`) — Elm state change stream
3. **Diagnostics SSE** (`/diagnostics`) — compiler diagnostic stream
4. **Dashboard SSE** (`/dashboard/stream`) — Datastar UI patches

And we're adding:

5. **A2A SSE** — Task status + artifact updates
6. **ACP SSE** — Run status updates

These should NOT be six separate SSE implementations. They should be ONE event
bus with six PROJECTIONS:

```fsharp
type SseSubscription = {
  Id: string
  Protocol: ProtocolSource
  Filter: SageFsEvent -> bool
  Format: SageFsEvent -> string option  // None = skip this event
}

module SseBus =
  let private subscriptions = ConcurrentDictionary<string, SseSubscription>()

  let subscribe (sub: SseSubscription) = subscriptions.TryAdd(sub.Id, sub)
  let unsubscribe (id: string) = subscriptions.TryRemove(id)

  let publish (event: SageFsEvent) =
    for kvp in subscriptions do
      let sub = kvp.Value
      if sub.Filter event then
        match sub.Format event with
        | Some data -> pushToClient sub.Id data
        | None -> ()
```

**Haynes**: That's the CLEF event bus pattern. One event, many consumers, each
with their own projection/format. MCP formats as JSON-RPC notifications. A2A
formats as `TaskStatusUpdateEvent`. ACP formats as run status JSON. Datastar
formats as HTML fragments with `data-ds-*` attributes. Same event, four
different wire representations.

**Holden**: And the Dashboard's connection tracking extends naturally:

```fsharp
type ConnectedClient = {
  Protocol: ProtocolSource
  ConnectedAt: DateTimeOffset
  SessionId: string
  LastActivity: DateTimeOffset
}

// Dashboard shows:
// 🔌 MCP: Copilot CLI (active 2s ago)
// 🤖 A2A: CI Pipeline Agent (task: molina-run-42, 73% complete)
// 🌐 ACP: nightly-mutation-cron (run: abc123)
// 🖥️ Dashboard: Chrome tab (idle 45s)
```

### Dissenting Opinions

**Muratori** dissents on ACP: I still think ACP is premature. We're adding a
third protocol with no .NET SDK, maintained by a different Linux Foundation
working group than A2A, with unclear adoption trajectory. The "curl debugging"
argument is real but you can get that with a single `/api/eval` REST endpoint
that isn't an entire protocol spec. Don't adopt a protocol standard when a
simple REST endpoint solves the same problem.

**Bill** counters: The protocol standard IS the simple REST endpoint — just
with a name and a discovery mechanism. If you're going to build REST endpoints
anyway (and you are — the Dashboard already has them), calling them "ACP-
compatible" costs nothing and gains interoperability. You're not adopting a
complex standard. You're naming what you already have.

**Primeagen** dissents on implementation order: Three protocols is a maintenance
surface area multiplier. Every new feature needs to work across MCP, A2A, AND
ACP. Every bug might be protocol-specific. Every test matrix triples. The panel
is designing for a future where SageFs is a multi-agent hub, but today it's a
REPL with a cool mutation testing idea. Ship Molina first. Add A2A when Level 2
actually exists. Add ACP if anyone asks for it. YAGNI is a survival strategy,
not a character flaw.

**Seemann** partially agrees with Primeagen: The ProtocolRouter abstraction
should exist from day one — it's just good architecture. But the A2A and ACP
adapters should be EMPTY SHELLS that return `501 Not Implemented` until there's
a concrete need. The abstraction protects you. The implementation waits.

**Wlaschin** disagrees with Seemann: Empty shells that return 501 are technical
debt disguised as architecture. They create the illusion of multi-protocol
support without the reality. Either implement it or don't declare it. A
ProtocolRouter that routes to two "not implemented" handlers is worse than no
router at all — it adds complexity without value.

**Carmack** synthesizes: Build the ProtocolRouter abstraction and the SSE bus
NOW — they improve the existing MCP implementation regardless. Implement A2A
when Molina Level 2 starts. Implement ACP routes when the Dashboard's REST API
already does 80% of what ACP requires (which is probably already). Don't build
empty shells. Don't build speculative protocols. But DO build the abstraction
layer that makes adding protocols a 200-line adapter instead of a 2000-line
rewrite.

### Implementation Plan

**Tier 1: Protocol Infrastructure** (do now, improves existing MCP)
1. Extract `ProtocolRequest`/`ProtocolResult` types into `SageFs.Core`
2. Create `ProtocolRouter` module with `fromMcp` + `execute`
3. Unify SSE into `SseBus` with subscription-based routing
4. Add `ProtocolSource` to `SageFsEvent` for provenance tracking
5. Refactor `McpServer.fs` to use `ProtocolRouter` instead of direct dispatch
6. Dashboard connection tracking shows protocol source per client

**Tier 2: A2A for Molina Level 2** (when Level 1 is working)
7. Add `A2A` + `A2A.AspNetCore` NuGet packages
8. Implement Agent Card at `/.well-known/agent-card.json`
9. Create `A2AAdapter` module — `fromA2A` translator
10. Map Molina coordinator → A2A task lifecycle
11. Worker discovery via Agent Card skills matching
12. A2A SSE for task progress streaming
13. `MolinaCoordinator` sends `A2A.SendMessage` to discovered workers

**Tier 3: ACP Surface** (when REST API is mature)
14. Create `AcpAdapter` module — `fromAcp` translator
15. Add ACP discovery endpoint: `GET /acp/agents`
16. Add ACP run endpoints: `POST /acp/agents/{name}/runs`, `GET` for SSE
17. Map to existing `ProtocolRouter.execute`
18. Document curl examples for every SageFs capability

**Tier 4: Advanced Integration** (when the ecosystem demands it)
19. A2A push notifications for long-running Molina jobs
20. Agent Card registry integration (enterprise discovery)
21. Cross-SageFs Molina collaboration via A2A task delegation
22. OpenTelemetry distributed tracing across protocol boundaries
23. MCP ↔ A2A bridge: expose A2A skills as MCP tools for LLM access

### Key Types

```fsharp
type ProtocolSource =
  | Mcp of toolName: string
  | A2A of taskId: string * agentName: string
  | Acp of runId: string
  | Dashboard of connectionId: string
  | Console
  | Internal

type ProtocolCapability =
  | ToolInvocation        // MCP: tools/call
  | TaskLifecycle         // A2A: send/get/cancel task
  | RunExecution          // ACP: create/stream run
  | AgentDiscovery        // A2A: agent card
  | ResourceAccess        // MCP: resources/read
  | Sampling              // MCP: sampling/createMessage
  | PushNotification      // A2A: webhook notifications

type AgentSkill = {
  Id: string
  Name: string
  Description: string
  InputModes: string list
  OutputModes: string list
  Protocol: ProtocolCapability list
}

/// SageFs's self-description — generates Agent Card, ACP agent metadata,
/// and MCP tool list from the SAME source of truth
type SageFsManifest = {
  Name: string
  Version: string
  Skills: AgentSkill list
  Capabilities: ProtocolCapability list
}

module Manifest =
  let toAgentCard (manifest: SageFsManifest) (baseUrl: string) : JsonElement =
    // Generate A2A Agent Card JSON
    ...

  let toAcpAgent (manifest: SageFsManifest) : JsonElement =
    // Generate ACP agent metadata JSON
    ...

  let toMcpToolList (manifest: SageFsManifest) : JsonElement =
    // Generate MCP tools/list response
    ...
```

### The Epistemic Angle

**Syme** closes: There's a Molina parallel here. MCP is *scientia naturalis* —
what your tools CAN do. It's the natural knowledge of capabilities. A2A is
*scientia media* — what agents WOULD do under specific conditions. You don't
know what a remote agent will produce until you send it a task and observe the
outcome. ACP is *scientia libera* — what the system WILL do when all the
protocols are connected and running. The full picture.

Three protocols, three kinds of knowledge, three layers of epistemic warrant.
SageFs doesn't just run F# code. It knows what it CAN do (MCP), what it WOULD
do (A2A counterfactual task execution), and what it WILL do (ACP operational
surface). That's the full Molina stack.

---

*Document produced by the SageFs Expert Panel. 15 voices, productive tension,
three execution tiers, four protocol layers, one architecture. Built on actors,
events, containers, and the conviction that your test suite should prove what
it claims.*
