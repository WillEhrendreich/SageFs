# Testing: DST and mutation testing are part of RED, not a later phase

This repo treats deterministic simulation testing and mutation testing as part of
how a change is PROVEN, not as something you add afterwards if you have time. An
agent that writes a few example tests and calls it done has not met the standard,
and a reviewer who accepts that is accepting a claim it never checked.

Both facilities already exist and are substantial. `SageFs.Simulation/` holds
42 simulations with 39 invariant modules and 21 generator modules.
`SageFs.Tests/*MutationTests.fs` runs 18 mutation suites and a mutation-score
gate in CI. You are expected to reach for them, not to re-invent them.

**What your own users should do, in their own framework**, is a separate
question with a separate answer — the same three techniques, expressed for
whatever test framework they already use, in
`docs/property-and-mutation-testing.md`. That page carries the framework-neutral
ideas, the Expecto/FsCheck recipes for F# users, and the equivalent tools for
the other ecosystems. Point users at it; do not hand them this file, which is
about this repository's own suite.

## The order, and where these sit in it

RED → GREEN → REFACTOR, and inside RED there are two different questions:

- **"Does it do the right thing?"** — an example test. Necessary, never
  sufficient. One input is one point; it says nothing about the space.
- **"Could anything else have got this right?"** — DST and mutation testing.
  This is the question that catches a decision that happens to work on the
  input you tried.

So: write the example test to get RED and drive GREEN, then — before you call
the change done — ask the second question and prove the answer.

## Deterministic simulation testing, for anything with a STATE MACHINE

If your change touches a reducer, a decision function, state transitions, or
anything that sequences over time, it gets a simulation. The pattern is already
established in this repo; follow it rather than inventing one.

**1. Generators** produce the inputs, and every input is VALID by construction.
This matters more than it sounds: a generator that emits invalid input fails the
property on its own fixture instead of on the code under test. See
`CohortLandingGenerators.fs` and the coverage/binding generators in
`TestCountBadge.fs` for the shape.

**2. The sim folds the REAL production function.** Never a re-implementation.
`CohortVacancySpec.fs` does exactly this:

```fsharp
let proof () : ExploreResult = explore (fun c e s cmd -> Cohort.decide c e s cmd) defaultCap
```

If the sim calls your own copy of the logic, it tests nothing.

**3. Invariants** are named, pure functions over one reachable state:
`CohortLandingInvariants.fs`, `BuildConfirmationInvariants.fs`. Scope them to the
DIRECTION that can actually fail, or they will flag healthy states.

**4. Exhaustive where the space is small.** `CohortSpec.fs` enumerates every
reachable state over a bounded model and checks every rule at every one, which is
a proof rather than a sample. Do this when the state space fits; when it does
not, sample and say so.

**5. A twin, and it must be a REAL mutant.** This is the part agents get wrong,
so read it twice. A fault twin whose injected fault cannot possibly be reached
produces a green "no violations" result that MEANS NOTHING.

```fsharp
// A twin that breaks the rule the space is supposed to prove:
let faultDepartedConductorKeepsSeat : Decide = ...
```

The test then requires the twin to actually produce the violation:

```fsharp
r.Violations |> Expect.contains "..." "BOUND-IS-PRESENT"
```

If you cannot make the twin fail, you have not proved the invariant is
reachable — you have proved your harness is decorative. Say so rather than
shipping a green run.

**6. Determinism and replay.** Same seed, same trace, same result. The repo tests
this directly (`same seed => identical trace`). A sim that cannot be replayed is
a sim you cannot debug at 3am.

## Mutation testing, for anything with A BRANCH IN IT

Example tests pass on whatever the code happens to do. Mutation testing asks the
opposite question: **if this decision were wrong, would anything notice?**

The framework is `SageFs.Tests/MutationTestingFramework.fs`. A mutant is a named
transformation that breaks exactly one thing:

```fsharp
type Mutant<'a> = { Name: string; Apply: 'a -> 'a; Description: string }
```

Each case applies a mutant and requires the existing assertions to FAIL. If an
assertion still holds on mutated code, the mutation SURVIVED and that is a test
gap to close now, not a note for later.

**Write a mutation test for every decision you add or change.** The repo has them
for `Affordances`, `RestartPolicy`, `ReloadPlanningDecision`, `SseWriter`,
`Watchdog`, `SageFsError` and more — if your module has none, that is the
pattern to copy.

The mutants that matter are the ones that flip a decision:

- an off-by-one in a threshold or a window
- a `<` written where `<=` belongs (the *boundary* case, not the middle)
- a default branch returning success instead of refusal
- two cases of a DU swapped or merged
- a guard inverted

**The non-vacuity rule, which is the whole point.** A mutation test that does not
fail when you remove it is not a mutation test. Before you trust one, break the
mutation and confirm the test goes red — then put it back and confirm the file is
byte-identical (`git diff` clean). Two mutants in this repo were built and then
found to leave residue; grep for it.

## Boundaries are where this pays most

A mutation test that pins the exact boundary of a window is worth ten example
tests in the middle of it, because the middle of a range is the one place both
sides get right. `RestartPolicyBoundaryTests.fs` exists for this.

## What "done" means, concretely

Before you say a change is finished:

1. An example test exists and was seen RED for the right reason.
2. If it is a state machine or a decision: a sim over the REAL function exists,
   with named invariants, and its twin genuinely violates them.
3. If it has branches: mutants exist for each decision changed, and every one is
   killed by an existing assertion.
4. The sim is deterministic — same seed, same trace.
5. `dotnet build` / the suite run once at the end, as the gate, not as your
   editing loop.

A change that has only (1) is a change that has been shown to work once. Say
which of these you did, and if you skipped one, say that too rather than
letting a green suite imply more than it does.
