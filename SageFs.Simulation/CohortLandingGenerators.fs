namespace SageFs.Simulation

open System
open SageFs.Simulation.CohortLandingSim

/// Seeded, dependency-free generators for cohort landing scenarios (mirrors Phase
/// 1/2's `Generators`/`MgrGenerators`). Chaos is data: `run (fromSeed n)` replays
/// identically forever. The generator deliberately mixes non-passing verdicts in
/// AHEAD of passing ones so a jammed reducer is actually exercised — a generator
/// that only ever emitted passing landings could never expose the queue-jam.
module CohortLandingGenerators =

  let private membersPool = [| "alice"; "bob"; "carol"; "dave" |]
  let private verdictPool =
    [| Verdict.Passes; Verdict.Passes; Verdict.FailsTests; Verdict.Conflict; Verdict.Inconclusive |]

  /// A general scenario: 2–7 landing requests over a small member pool, each with
  /// a randomly-chosen verdict biased slightly toward `Passes` (so most scenarios
  /// have a healthy landing that a jam would freeze). Same seed => identical list.
  let fromSeed (seed: int) : LandingScenario =
    let rnd = Random(seed)
    let n = rnd.Next(2, 8)
    let scripts =
      [ for _ in 1 .. n ->
          { Requester = membersPool.[rnd.Next(0, membersPool.Length)]
            Verdict = verdictPool.[rnd.Next(0, verdictPool.Length)] } ]
    { Seed = seed; Landings = scripts }

  // ── Named minimal scenarios (the exact replays the tests assert on) ─────────

  /// The headline queue-jam replay: a member's failing landing is requested FIRST,
  /// with a healthy landing queued behind it. The real reducer pops the failing
  /// one and lands the second; the jammed reducer freezes the second forever.
  let failingThenPassing : LandingScenario =
    { Seed = 1
      Landings =
        [ { Requester = "alice"; Verdict = Verdict.FailsTests }
          { Requester = "bob"; Verdict = Verdict.Passes } ] }

  /// A rebase conflict ahead of a healthy landing — conflict must also pop, not
  /// jam.
  let conflictThenPassing : LandingScenario =
    { Seed = 2
      Landings =
        [ { Requester = "alice"; Verdict = Verdict.Conflict }
          { Requester = "bob"; Verdict = Verdict.Passes } ] }

  /// An inconclusive verification ahead of a healthy landing — the transient case
  /// must pop and `RebaseAndResubmit`, never dead-lock the queue.
  let inconclusiveThenPassing : LandingScenario =
    { Seed = 3
      Landings =
        [ { Requester = "alice"; Verdict = Verdict.Inconclusive }
          { Requester = "bob"; Verdict = Verdict.Passes } ] }

  /// Every kind of failure stacked ahead of a final healthy landing — the strict
  /// worst case for a jam: three distinct terminal blockers must each pop so the
  /// last one lands.
  let everyFailureThenPassing : LandingScenario =
    { Seed = 4
      Landings =
        [ { Requester = "alice"; Verdict = Verdict.FailsTests }
          { Requester = "bob"; Verdict = Verdict.Conflict }
          { Requester = "carol"; Verdict = Verdict.Inconclusive }
          { Requester = "dave"; Verdict = Verdict.Passes } ] }

  /// A run of only healthy landings — they must all land, one after another, the
  /// integration head advancing each time.
  let allPass : LandingScenario =
    { Seed = 5
      Landings =
        [ { Requester = "alice"; Verdict = Verdict.Passes }
          { Requester = "bob"; Verdict = Verdict.Passes }
          { Requester = "carol"; Verdict = Verdict.Passes } ] }
