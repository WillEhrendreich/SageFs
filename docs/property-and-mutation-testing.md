# Property testing, mutation testing and simulation testing in your own program

SageFs runs your tests as you type and drives your REPL. That makes it fast to
*notice* a failure. It does not make it **likely you would have caught one**.

The tools below are how you make your test suite strong enough that a green run
means something, and SageFs is the place to build and iterate them fast. This
page is about YOUR program's tests. Nothing here requires Expecto or F# — the
ideas are yours to apply in whatever framework you already use.

## Why one green test is not evidence

A single example proves one point. Your function is not defined at one point,
and the example you happened to pick is the one you already believed in.

This failure mode is quiet and total. A suite of 40 example tests can pass
against code where a boundary is off by one, a default returns success where it
should refuse, and a whole branch is dead — because none of them happened to sit
on the edge you got wrong.

The three techniques below each attack a different hole:

| Technique | The question it answers |
|---|---|
| **Property testing** | does it hold for inputs I didn't think of? |
| **Simulation testing (DST)** | does it hold across whole SEQUENCES of events, including failures and chaos? |
| **Mutation testing** | if it were wrong, would anything notice? |

## Property testing: stop choosing the inputs by hand

Instead of asserting `parse "42" = Ok 42`, assert a property over thousands of
generated values:

> For any non-empty run of digits, `parse` returns `Ok` of exactly those digits.

Now the generator decides the inputs, and it hunts for your mistake. The value
is not that it tests more — it is that it finds counterexamples you would never
have chosen, and that it SHRINKS the failing case to the smallest input that
breaks it. "It failed on a 1,000-character string" is a nuisance; "it failed on
the single character `+`" is a bug report.

**The rule that makes it work: generate only VALID inputs.** A generator that
emits a malformed case will fail your property on its own fixture instead of on
your code. If your validator accepts a shape, the generator must only emit that
shape.

## Simulation testing: one input is not a sequence

Most real bugs are not in a function, they are in a SEQUENCE: a session restarts
while a request is in flight; a build is confirmed twice; two agents claim the
same file. An example test cannot express that, because each example starts
clean.

DST says: build the whole timeline, then check the rules hold at every step.

The shape, which SageFs itself uses:

1. **Chaos is DATA.** A scenario is an explicit, ordered, SEEDED list of events
   and faults — not random ambient behaviour. Same seed, same run, every time.
2. **Inject time.** A simulated clock you advance explicitly. Never `now()` in
   the code under test — if the test can read the wall clock, it is not
   deterministic and will not replay.
3. **Test the REAL code.** The scenario folds through your actual function. A
   simulation of a reimplementation proves nothing.
4. **Check named invariants** at every reachable state — "no two sessions
   report the same id", "a claimed file is never released by anyone else".
5. **Shrink the failure.** A 40-event failing run is unreadable. Delta-debug it
   down to the minimal sequence that still fails, or you will never debug it.
6. **Make time and faults composable.** Clock skew, duplicate delivery, dropped
   messages, reordering. Because they are pure transformations of a schedule,
   you can also use them as MUTANTS.

**The part everyone gets wrong: the twin.** A simulation that finds no
violations is only evidence if you have proved it CAN find one. So you build a
**twin**: the same simulation with one rule deliberately broken. If your twin
produces no violation, your harness is decorative and your green run means
nothing.

```fsharp
// The production rule: a departed conductor's seat stays filled for its TTL.
let decideReal = decide

// The twin: the same decision, but the seat is released immediately.
let decideTwinDepartedConductorKeepsSeat ...   // deliberately violates the invariant

// The harness must be able to see the difference, or it cannot see anything.
expect (violationsFor decideTwinDepartedConductorKeepsSeat) |> isNotEmpty
```

If you cannot make the twin fail, say so — do not report a green run as proof.

## Mutation testing: does your suite have any teeth?

This is the one most teams skip, and it is the cheapest to get value from.

Take a real decision, deliberately break it, and require your existing tests to
**fail**:

- flip a `<` to `<=` at a threshold — the boundary case
- return success where it should refuse
- invert a guard
- swap two branches of a case analysis

If your tests still pass on the broken code, **that is a hole in your suite**,
found automatically, and it is a real finding rather than a feeling.

Work outward from the decisions you changed. The mutants that pay most are the
ones at the EDGE of a range, because the middle of a range is the one place
almost everyone gets both sides right.

**Prove your mutation test works.** Before trusting one, break the mutation
itself and confirm the test goes red — a mutation test that cannot fail is not a
test. This takes two minutes and it is the difference between a gate and a
decoration.

## Doing all this in your own framework

**You do not need Expecto, and you do not need F#.** The techniques are
framework-independent; every mainstream ecosystem has them.

**Property testing** — FsCheck (F#), fast-check (JS/TS), Hypothesis (Python),
proptest (Rust), jqwik or QuickTheories (Java). All generate, all shrink.

**Mutation testing** — Stryker (JS/TS), mutmut (Python), PITest (Java),
cargo-llvm-cov (Rust), Stryker.NET (C#). Or write it by hand in about 30 lines,
which is often easier than adopting a tool (see the Expecto recipe below).

**Deterministic simulation** — usually yours to build, because it is specific to
your state machine. What you need is exactly what SageFs uses: seeded scenarios,
an injected clock, the real function under test, named invariants, a shrinking
step, and a twin that proves the harness works. If your ecosystem has a
property-testing library with a state-machine runner, it can usually drive this
too — the shape is the same.

### If you are in F#, this is Expecto and FsCheck

SageFs is F#, so this is the shortest route — and Expecto is a natural home for
all three techniques because it runs in the same process as your REPL.

**Property tests** come from the `Expecto.FsCheck` package, which wires
FsCheck's generators and shrinking to Expecto's assertions:

```fsharp
open Expecto
open Expecto.Flip          // Expect.isTrue is curried here: message first, then the value
open ExpectoFsCheck       // the module that carries testProperty

// A property, not a list of examples: the generator picks the inputs.
testProperty "a path always matches itself" <| fun (p: string) ->
  (p = p) |> Expect.isTrue "a path matches itself"
```

Those three opens are not decoration. Two of them are load-bearing in a way
that will otherwise cost you a confusing error: `Expecto.Flip` is what makes
`Expect.isTrue` take the message first and the value second (without it, Expecto
expects a `bool -> 'a` and rejects your `string -> unit`), and the module is
`ExpectoFsCheck`, not `Expecto.FsCheck`.

A property that generates its own inputs generalizes a hand-picked example,
which is the whole point — the example above proves one path is fine, the
property says it is fine for every path including the one you never tried.

**Write your own generator when the built-in one does not fit.** This is the
part people skip and it is where the value is — a generator that emits only
valid inputs, and that knows your domain:

```fsharp
open FsCheck
open FsCheck.FSharp

type Config = { Name: string; Retries: int; Url: string }

type ArbConfig =
  static member Arbitrary() =
    Arb.fromGen (
      Gen.sized (fun _ ->
        let retries = Gen.oneof [ Gen.choose (0, 5); Gen.elements [ 0; 1; 100 ] ]
        let names   = Gen.elements [ "web"; "worker"; "db" ]
        let urls    = Gen.elements [ "http://a"; "http://b" ]
        Gen.map3 (fun retries name url -> { Retries = retries; Name = name; Url = url })
                  retries names urls))
```

Note what that does: `Retries` is drawn from a set that deliberately includes
the boundaries (0, 1 and 100) rather than a uniform range. That is where the
counterexamples come from.

Bind the generators to `map3` **in order** — the n-th generator feeds the n-th
argument, and getting the order wrong is a type error rather than a silent
mistake, which is one of the nicer properties of FsCheck's API.

**Mutation tests in Expecto** need no framework at all — the whole mechanism is
"apply a broken version, assert the old assertion now fails":

```fsharp
let mutantOfBoundary (decideAt: int -> Decision) = decideAt (decideAt 100)
```

Then assert the mutant produces a DIFFERENT result from the real one. If it does
not, your decision had no boundary there worth testing — which is itself the
finding.

**DST in Expecto** is a plain `[<Tests>]` value that loops a seeded scenario and
checks your invariants at each step. There is no magic; the value is that it
replays, and that you can develop the scenario interactively in the SageFs REPL
before you ever commit it.

Because SageFs runs Expecto as you type, a property you have just written fails
in about a second — so the loop "tighten the property, see it fail, adjust" is
genuinely cheap. That is the whole argument for doing this in the REPL.

**How SageFs changes the economics.** These suites are slow to write and slow to
run. SageFs is why they are affordable for you:

- **Iterate in the REPL.** A property test is just code — write it, evaluate it,
  read the counterexample immediately. No build, no runner restart, no waiting.
- **See it re-run as you type.** Change an invariant and watch the property fail
  in seconds, so tightening a property is a loop rather than a chore.
- **The counterexample is already a value.** Grab the shrunk case from the REPL,
  paste it into a fixed example test, and you have a regression test for free.

That is the point of the tool in this context: not to run your tests, but to make
writing strong ones fast enough that you actually do it.

## What "done" means, concretely

Before you call a change tested:

1. An example test exists and was seen failing for the right reason.
2. A property covers the shape of the input space, with a valid-only generator.
3. If it is a state machine or sequence: a DST scenario with named invariants,
   a shrinking step, and a twin that genuinely violates an invariant.
4. The mutants for each decision you changed are all killed.
5. The suite replays from its seed — the same seed gives the same run.

Say which of these you did. "Tests pass" on its own tells a reader nothing about
whether your suite could have caught the bug, and that is the only question worth
asking.

## See it in practice

SageFs tests itself this way — 42 simulations, 39 invariant modules, 21 generator
modules and 18 mutation suites, each simulation folding the real production
function with seeded scenarios and an injected clock. Reading
`SageFs.Simulation/` is the most direct way to see each technique applied to
real code.