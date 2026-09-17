# Why F#? — Lessons from Building SageFs

SageFs is a live F# development environment: a REPL engine, web dashboard, editor integrations, MCP
surface, and daemon, built primarily in F#. This document explains why F# was a good fit, with real code
from the codebase as evidence.

---

## 1. Discriminated Unions Make Impossible States Unrepresentable

SageFs models session and worker lifecycle with discriminated unions. A simplified example:

```fsharp
type SessionState =
  | Uninitialized
  | WarmingUp
  | Ready
  | Evaluating
  | Faulted
```

There is no `null` session, and no boolean `isReady` that can desync from `isEvaluating`. The compiler
enforces exhaustive handling: add a new state and every `match` in the codebase fails to compile until you
handle it. When we added `EvalTraced` to the event DU, the compiler flagged both files that needed updating.

In C# the same code needs an enum plus runtime checks plus defensive `if (state == null)` guards. In F# the
type system does that work at compile time.

---

## 2. Railway-Oriented Programming Eliminates Try/Catch Spaghetti

SageFs uses `Result<'T, SageFsError>` throughout. Errors are values, not exceptions.
The `ResultEx` module provides composable combinators:

```fsharp
request
|> validate
|> Result.bind transform
|> Result.map serialize
|> Result.mapError (fun e -> e.describe())
```

Every function in the chain either succeeds and passes the value forward, or fails and
short-circuits with a typed error. No hidden control flow. No forgotten catch blocks.
No `NullReferenceException` three stack frames deep.

The `SageFsError` DU has cases across four categories (client/server/gateway/infra). Architecture tests
verify every case has exactly one classification and a valid HTTP status code, so you cannot add a new error
type without classifying it.

---

## 3. Immutability by Default Eliminates Entire Bug Categories

SageFs processes state transitions through pure update functions:

```fsharp
let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
  match msg with
  | KeyPressed key -> handleKey key model
  | EvalCompleted result -> { model with Output = result }, Cmd.none
  | ...
```

The model is a record. Updates produce new records via `{ model with ... }`, keeping daemon and session transitions explicit and testable instead of spreading shared mutable state across clients.

**The CellGrid monoid** formalizes overlay composition with mathematical properties verified
by FsCheck:

```fsharp
// Associativity: (a <+> b) <+> c = a <+> (b <+> c)
testProperty "overlay is associative" (fun (a, b, c) ->
  CellGrid.overlay (CellGrid.overlay a b) c =
  CellGrid.overlay a (CellGrid.overlay b c))
```

---

## 4. Units of Measure Prevent Timing Bugs at Compile Time

SageFs defines:

```fsharp
[<Measure>] type ms
```

Every timing value in the pipeline instrumentation carries this type:

```fsharp
type CompletedStage = {
  Name: string
  ElapsedMs: float<ms>
  Outcome: StageOutcome
}
```

You cannot accidentally add milliseconds to seconds. You cannot pass a raw `float` where
`float<ms>` is expected. The compiler catches unit mismatches that would be silent runtime
bugs in any other language.

**Cost: zero.** Units of measure are erased at compile time. No runtime overhead. No boxing.
Just compile-time safety that prevents an entire class of numerical errors.

---

## 5. Pattern Matching Replaces If/Else Chains

Every control flow decision in SageFs uses pattern matching:

```fsharp
match response.EvaluationResult with
| Ok result ->
  emit (EvalCompleted {| Code = code; Result = result |})
| Error ex ->
  emit (EvalFailed {| Code = code; Error = ex.Message |})
```

Pattern matching is:
- **Exhaustive**: the compiler warns about missing cases
- **Decomposing**: you extract data in the same expression that checks the shape
- **Composable**: nested matches, active patterns, and guard clauses

Compare this to the C# equivalent with `if (result.IsSuccess)` checks, null guards,
and `as` casts scattered across the codebase.

---

## 6. Computation Expressions Are a Superpower

SageFs's eval pipeline uses a custom computation expression:

```fsharp
let result = pipeline {
  let! validated = stage "Validate" (validate request)
  let! transformed = stage "Transform" (transform validated)
  let! compiled = stage "Compile" (compile transformed)
  return compiled
}
```

Each `stage` records timing and outcome. The CE automatically short-circuits on failure
with full trace context. This is the same pattern as `async { }` or `task { }`, but
domain-specific.

**You can build your own control flow abstractions** that look like language features.
No macros. No code generation. Just the type system.

---

## 7. The Module System Scales Without Ceremony

SageFs.Core has more than 200 top-level modules (tracked by an architecture test with a regression
ceiling). Each module is a namespace with functions: no class hierarchies, no dependency-injection
containers, no abstract factory patterns.

```fsharp
module SageFs.Middleware.Tracing

let buildTracedPipeline (middleware: NamedMiddleware list) (evalFn: MiddlewareNext) =
  // 40 lines of pure pipeline composition
```

Functions are the unit of abstraction. Modules are the unit of organization.
No `ITracingMiddlewareFactory`. No `AbstractPipelineBuilderBase<T>`.

---

## 8. Property-Based Testing Finds Bugs Example Tests Miss

SageFs uses FsCheck to generate thousands of random inputs and verify invariants:

```fsharp
testProperty "RingBuffer push/toList length ≤ capacity" (fun (items: int list, cap: int) ->
  let cap' = max 1 (abs cap % 100)
  let buf = RingBuffer.create cap'
  items |> List.iter (RingBuffer.push buf)
  RingBuffer.toList buf |> List.length <= cap')
```

This single test replaces dozens of hand-written examples. FsCheck found a `BatchFlusher` race condition
that no example test caught, by generating rapid concurrent sequences that hit the exact interleaving that
caused data loss.

The suite has thousands of tests, with property tests covering the core abstractions. The exact count is
auto-derived into the README badge.

---

## 9. Type Inference Keeps Code Clean

F# infers types almost everywhere. You write:

```fsharp
let pipeline = buildTracedPipeline namedMiddleware "CoreEval" evalFn
```

Not:

```csharp
PipelineResult<EvalResponse, AppState> pipeline =
  TracingMiddleware.BuildTracedPipeline<string, EvalRequest, EvalResponse, AppState>(
    namedMiddleware, "CoreEval", evalFn);
```

Same safety. A fraction of the noise.

---

## 10. The Ecosystem Effect

Because SageFs is written in F#, it can:
- **Hot-reload F# source files** into a live FSI session (the language's REPL is first-class)
- **Use FSharp.Compiler.Service** for real-time diagnostics, completions, and symbol analysis
- **Generate Fable JavaScript** for the VS Code extension from the same F# source
- **Share types** between the CLI, dashboard, editor integrations, and test project with minimal translation

Being written in F# is what lets SageFs hot-reload F# source, use FSharp.Compiler.Service directly, and
share types end to end.

---

## The Numbers

| Metric | Value |
|--------|-------|
| Tests | thousands (auto-derived into the README badge) |
| Property tests | hundreds |
| Current client surfaces | VS Code, Neovim, web dashboard, MCP |
| Runtime overhead of units of measure | 0 bytes (erased at compile time) |
| Null reference exceptions | 0 (by design) |
| Unhandled pattern matches | 0 (compiler-enforced) |

---

## Getting Started

```bash
dotnet tool install --global SageFs
sagefs
```

Then open your editor and create a session for `MyProject.fsproj`. SageFs connects automatically.

---

SageFs is open source.
