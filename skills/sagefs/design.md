# How to write the code

Read this before you design a type, a function signature, or a module. Every
other skill is mechanics — how to drive the tool. This one is the OTHER half:
what the code is supposed to LOOK like, which is where a mechanically correct
change can still be the wrong code.

These are the owner's standards. They are not suggestions, and a reviewer who
finds a violation is right to say so.

## TDD: Red, Green, Refactor, in that order

1. **Red.** Write the test that fails for the RIGHT reason, and watch it fail. A
   test that has never been seen red has never been shown to be capable of
   failing, so it might not be. Commit the red test on its own when the change
   is more than a few lines, so the failure is reviewable.
2. **Green.** Make it pass with the simplest thing that works. Not the design
   you have in mind — the one that passes.
3. **Refactor.** Only now, with a green suite, improve the shape.

Never write the implementation first and add the test to match it: that test is
written by the code it blesses, so it asserts whatever the code happens to do.

## Model with exhaustive DUs, not `option` or `bool`

If something can be one of N things, it is a DU with N cases. `option` and
`bool` are for "maybe" and "yes/no" and nothing else.

- `bool isStale` tells a reader nothing. `Stale of string * DateTime | Fresh` says
  what is stale, since when, and forces every match to handle it.
- `option` as a return means "the absence is legitimate". If it is NOT, the
  absence is a BUG and belongs in a `Result` with a named reason, not silently
  as `None`.

An exhaustive match is the point: adding a case then breaks the build in every
place that forgot, which is exactly what you want and exactly what a `bool`
cannot do.

## Railway-oriented error checking

Two kinds of failure, and they must not be confused:

- **Not yet an error** — the operation did not fail, it did not produce a value.
  Return the real sum type.
- **An error** — something went wrong that a caller could act on. Return
  `Result<'T, 'Reason>`, where `'Reason` is a DU, not a string.

A refusal carries **the rule AND the next action**. `RoleForbids(tool, required)`
beats `"not allowed"`: the first tells the caller which role would work, and
the second leaves them guessing.

Refusals in this codebase are NAMED DU cases. `Result<_, string>` is
ratcheted DOWN by a guard test, because an untyped refusal cannot be matched
on, logged by category, or turned into a status code.

## Parse, don't validate

Validate once, at the edge, and produce a type that cannot be wrong after that.
Everything downstream of a parse takes the parsed type, so an invalid state is
not merely discouraged — it is unrepresentable.

```fsharp
// Does not scale: every caller must remember to check.
let readPort (s: string) : int = s |> Int32.Parse

// Scales: the type says what is true.
let readPort (s: string) : Result<int, PortRefusal> = ...
```

If you find yourself writing `if valid x then` inside the consumer, the
validation belongs at the boundary instead.

## Immutability

No mutation of shared state, and no `let mutable` unless it is a local
accumulator whose every step is visible on one line. Prefer returning a new
value over editing one in place: a reducer that returns its next state is
testable, replayable and debuggable, and one that mutates is none of those.

## Pure functional core, effects at the edge

The decision is a pure function you can call from a REPL eval and from a test
with no setup. The IO — the socket, the file, the database, the clock, the
ledger — happens around it, once, at the edge. This is why the cohort reducer
can be replayed offline by `sagefs play`.

## Hexagonal architecture, and vertical slices

**Hexagonal**: the domain knows nothing about the outside. It does not reference
`HttpClient`, `SqliteConnection`, a file path, or the MCP layer. Ports are
declared in terms of what the domain needs ("a ledger of commands") and adapters
supply them.

**Vertical slices**: a feature is one file (or one folder) holding EVERYTHING it
needs — its types, its decision, its projection, its tests. Not
`Models/ | Services/ | Dto/` spread across the tree. A new tool, or a new SSE
event, should live in one place you can read end to end.

These two are why this codebase splits along feature (`Features/Cohort.fs`,
`Features/LiveTestingTypes.fs`) rather than by layer.

## Small composable functions

One job, a name that says what it does, and no need to read the body to call
it. If a function needs a paragraph to explain, it is two functions. If composing
it means reading it, it takes too much.

Prefer a pipeline of named steps over one clever expression: `git show` that a
reader can follow line by line beats a nested `Seq.collect` that saves a
variable.

## No `private`, except one place

**Do not write `private`.** Not on a function, not on a module, not on a
anything. Almost every use of it in this codebase is a default that started as
a decision and became furniture: a helper nobody outside the file calls is
still just a function, and the modifier only adds noise.

F#, unlike most languages, has no way to hide a binding across modules
anyway — `private` is a within-module hint, not encapsulation. So it buys
nothing and reads as a claim about a boundary that does not exist.

The ONE place it earns its keep: an **opaque type with a smart constructor**,
where `private` stops a caller building an invalid value by hand.

```fsharp
/// Opaque: only `create` makes one, and `create` checks it.
type private Port = { Value: int }
let create (raw: string) : Result<Port, PortRefusal> = ...
```

That is the only place: somewhere the TYPE ITSELF guarantees correctness, so
"all or nothing" is a real property and the constructor is the only door. 66
types here are shaped that way and those are load-bearing. The other ~1,500
`let private` are not.

If a binding genuinely should not be used elsewhere, the reviewer enforces
that by reading — and the repo's own ratchets do it in code. A `private` on
everything makes the ones that matter invisible.

## `__SOURCE_DIRECTORY__` — banned in COMPILED code, correct in a script

**Never write `__SOURCE_DIRECTORY__` in a `.fs` file (compiled into an assembly).**
There it is a build-time constant: the compiler bakes the source directory in when it
compiles, so a test that locates a CHECKED-OUT repository through it measures
wherever the assembly was COMPILED, not wherever it RUNS. The fixture looks right,
the paths look plausible, and it silently resolves against a stale or absent tree —
the failure a review cannot see.

**In a `.fsx` script it is a different thing entirely, and it is CORRECT.** The FSI
host expands it on every run, so it names the script's real, current directory and is
unaffected by the working directory. Measured, running a script from an unrelated CWD:

```
CWD                  = /tmp/sd-elsewhere
__SOURCE_DIRECTORY__ = /tmp/sd-test
```

And moving the script and re-running reports the new location. So a script that uses
it as the START of an upward walk is right, and "removing" it would make that script
worse. Every script locator in `scripts/` keeps it for exactly this reason — it is
the anchor the walk starts from, never a path component in a result. **Do not
"fix" those.**

The rule, then: **compiled → `RepoPaths`; script → `__SOURCE_DIRECTORY__` as the walk's
start, composed with `System.IO.Path`.** Getting this backwards in either direction is
the bug: a compiled constant that measures the wrong thing, or a script whose root
depends on the CWD it happened to be launched from.

**Never write an absolute path of one machine's checkout** (`/home/will/...`).
A checkout anywhere on the machine has to work. Use `System.IO.Path`
(`Path.Combine`, `Path.GetFullPath`), which is what makes the composed result
platform-independent.

**The one home for the compiled side is the `RepoPaths` module**
(`SageFs.Core/RepoPaths.fs`), which resolves the root at RUNTIME. It declares NO
namespace — it is a global-namespace `[<RequireQualifiedAccess>] module` — so
`open SageFs.RepoPaths` fails with `FS0039` and you must call it fully qualified:

```fsharp
RepoPaths.repoPath [| "samples"; "getting-started.fsx" |]
RepoPaths.repoPathFull [| "samples"; ".." ; "docs" |]   // canonicalised
RepoPaths.findRepoRootUpward someDir                      // string option; None when there is no repo
RepoPaths.requireRepoRoot ()                              // or throws, naming where it looked
```

Resolution order is `SAGEFS_REPO_ROOT` (explicit override) → the assembly's own
`Location` → the working directory. Never caches, because a test may change the
working directory between cases.

**A fixture sitting BESIDE a test file is not at the repo root.** `Path.Combine
(__SOURCE_DIRECTORY__, "SomeFixture.fs")` was pointing at the source directory, so
its replacement is the assembly's own directory, not `repoPath`:

```fsharp
// A fixture that lives next to the test SOURCE (and is not copied to build output).
Path.GetDirectoryName(typeof<MyTests>.Assembly.Location) |> Path.Combine "MyFixture.fs"

// A fixture under the repo (samples, docs, a whole project directory).
RepoPaths.repoPath [| "samples"; "getting-started" |]
```

Getting this backwards is silent: the file is simply not found, or worse, a
same-named file elsewhere is found instead.

**One trap worth knowing, because the obvious replacement is wrong too.**
`AppContext.BaseDirectory` is the base directory of the running HOST, not of the
assembly that called it. Measured under `dotnet fsi`:

```
AppContext.BaseDirectory      = /home/will/.dotnet/sdk/11.0.100-rc.1.26425.128/FSharp/
SageFs.Core assembly Location = /home/will/Work/SageFs/SageFs.Core/bin/Debug/net11.0/SageFs.Core.dll
```

Walking up from `BaseDirectory` in a script walks up from the F# COMPILER's
install directory and finds no repository. That is the same bug as the build-time
constant wearing a runtime hat, so `RepoPaths` uses
`Reflection.Assembly.GetExecutingAssembly().Location`.

**A locator that only works from the repo root is not a fix.** Prove the
replacement from a foreign working directory, or you have only swapped one
fragile path for another.

## And the rules that already exist

`AGENTS.md` carries the operational ones that are not about style: durations
live in `Timeouts`, refusals are DUs, the daemon is never waited on, MCP
reconnects with a script. Read it; those are not negotiable either.
