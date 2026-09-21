# Why F#? — Lessons from Building SageFs

I built SageFs — a live F# development environment: a REPL engine, web dashboard, editor integrations, an MCP
surface, and a daemon holding it all together — almost entirely in F#. This isn't a pitch deck. It's what I
actually learned, with real code from the codebase as the receipts.

---

## 1. Discriminated Unions Make Impossible States Unrepresentable

SageFs models session lifecycle with a discriminated union. Here's the real one, unedited:

```fsharp
type SessionState =
  | Uninitialized
  | WarmingUp
  | Ready
  | Evaluating
  | Faulted
```

There is no `null` session, and no boolean `isReady` that can quietly desync from `isEvaluating`. The compiler
enforces exhaustive handling: add a new case and every `match` in the codebase fails to compile until you handle
it. That's the whole safety net, and it costs nothing extra to get.

In C# the same idea needs an enum plus runtime checks plus defensive `if (state == null)` guards scattered
wherever someone remembered to add them. In F# the type system does that work for you, at compile time, whether
you remember or not.

---

## 2. Railway-Oriented Programming Eliminates Try/Catch Spaghetti

SageFs uses `Result<'T, SageFsError>` throughout. Errors are values, not exceptions.
The `ResultEx` module gives you composable combinators:

```fsharp
request
|> validate
|> Result.bind transform
|> Result.map serialize
|> Result.mapError (fun e -> e.describe())
```

Every function in the chain either succeeds and passes the value forward, or fails and short-circuits with a
typed error. No hidden control flow. No forgotten catch block. No `NullReferenceException` surfacing three
stack frames away from where it actually went wrong.

The `SageFsError` DU has cases across four categories (client/server/gateway/infra). An architecture test
verifies every case has exactly one classification and a valid HTTP status code, so you cannot add a new error
case without classifying it — the compiler and the test suite both hold you to it.

---

## 3. Immutability by Default Eliminates Entire Bug Categories

SageFs pushes state transitions through pure update functions:

```fsharp
let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
  match msg with
  | KeyPressed key -> handleKey key model
  | EvalCompleted result -> { model with Output = result }, Cmd.none
  | ...
```

The model is a record. Updates produce new records via `{ model with ... }`, which keeps daemon and session
transitions explicit and testable instead of scattering shared mutable state across every client that touches it.

Because the update functions are pure, I can throw FsCheck at them and trust the result. The worker-restart
backoff policy, for instance, gets checked against thousands of generated inputs instead of a handful of
examples I thought to write by hand:

```fsharp
testPropertyWithConfig propConfig "nextBackoff never exceeds BackoffMax" <|
  fun (NonNegativeInt count) ->
    let delay = nextBackoff defaultPolicy count
    (defaultPolicy.BackoffMax, delay) |> Expect.isGreaterThanOrEqual "capped at max"
```

No hand-picked count is going to accidentally prove that property for every possible restart count. FsCheck
doesn't have that problem.

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

You cannot accidentally add milliseconds to seconds. You cannot pass a raw `float` where `float<ms>` is
expected. The compiler catches unit mismatches that would be silent, "why is this eval taking 1000x longer
than it should" runtime bugs in any other language.

**Cost: zero.** Units of measure are erased at compile time. No runtime overhead, no boxing — just compile-time
safety for a whole class of numerical mistakes I'd otherwise make eventually.

---

## 5. Pattern Matching Replaces If/Else Chains

Every control-flow decision in SageFs goes through pattern matching:

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
- **Composable**: nested matches, active patterns, and guard clauses all stack

Compare that to the C# version: `if (result.IsSuccess)` checks, null guards, and `as` casts scattered across
the codebase, each one a place a new case can slip through unnoticed.

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

Each `stage` records timing and outcome. The CE short-circuits on failure automatically, with full trace
context attached. It's the same pattern as `async { }` or `task { }`, just domain-specific to eval tracing.

**You can build your own control-flow abstractions** that read like language features. No macros, no code
generation — just the type system doing what it's there for.

---

## 7. The Module System Scales Without Ceremony

SageFs.Core alone is organized into roughly 190 top-level modules — no class hierarchies, no
dependency-injection containers, no abstract factory patterns in sight.

```fsharp
module SageFs.Middleware.Tracing

let buildTracedPipeline (middleware: NamedMiddleware list) (evalFn: MiddlewareNext) =
  // 40 lines of pure pipeline composition
```

Functions are the unit of abstraction. Modules are the unit of organization. No `ITracingMiddlewareFactory`.
No `AbstractPipelineBuilderBase<T>`. That said — 190 files is also a lot of files, and a few of the biggest
ones in this repo have grown past where I'd like them to be. Modules-not-classes buys you a lot, but it
doesn't save you from writing a 5,000-line file if you're not paying attention. I'm not.

---

## 8. Property-Based Testing Finds Bugs Example Tests Miss

SageFs uses FsCheck to throw thousands of random inputs at the code and check invariants hold:

```fsharp
testProperty "RingBuffer push/toList length ≤ capacity" (fun (items: int list, cap: int) ->
  let cap' = max 1 (abs cap % 100)
  let buf = RingBuffer.create cap'
  items |> List.iter (RingBuffer.push buf)
  RingBuffer.toList buf |> List.length <= cap')
```

This single test replaces dozens of hand-written examples. FsCheck found a `BatchFlusher` race condition that
no example test caught, by generating rapid concurrent sequences that happened to hit the exact interleaving
that caused data loss. I would not have thought to write that example by hand. That's the whole point.

The suite has thousands of tests, with property tests numbering in the hundreds across the core abstractions.
The test-count badge in the README is derived from source, never hand-typed.

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

Same safety. A fraction of the noise. I'll take it.

---

## 10. The Ecosystem Effect

Because SageFs is written in F#, it gets to:
- **Hot-reload F# source files** into a live FSI session — the language's own REPL is first-class, so I'm not bolting one on
- **Use FSharp.Compiler.Service** directly for real-time diagnostics, completions, and symbol analysis
- **Generate Fable JavaScript** for the VS Code extension from the same F# source
- **Share types** between the CLI, dashboard, editor integrations, and test project with minimal translation

None of that is a side effect of choosing F#. It's what made this specific project possible to build the way
I built it — a language that hot-reloads its own source and compiles to JS for free is doing a lot of the
heavy lifting so I don't have to.

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

That starts the daemon in the foreground — it's not a REPL by itself, it's the thing your editor, an MCP
client, or the dashboard talks to. Point one of them at `MyProject.fsproj` and it spins up a session for you.

---

SageFs is open source. Tell me I'm wrong about any of this — that can be fun too.
