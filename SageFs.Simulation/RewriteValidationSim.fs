module SageFs.Simulation.RewriteValidationSim

/// WHY — translation validation is a GATE on rewriting someone's code, so the
/// property that matters is one-sided: a rewrite is never licensed when any
/// use site is not individually equivalent. Getting it wrong silently changes
/// the meaning of a user's program, which is worse than the restart we do
/// today.
///
/// The battery folds the full cross-product of use-site shapes over the real
/// validator. The twin is the bug a real implementation would have: check only
/// the first use site, or treat an `Undecidable` as a pass.

open SageFs
open SageFs.TranslationValidation

type Scenario =
  { Seed: int
    SiteCount: int
    Sites: UseSite array
    /// Independent ground truth: true when EVERY site is a plain read, so the
    /// rewrite must be licensed — and equally, true only then.
    AllPlainReads: bool }

type Trace =
  { Scenario: Scenario
    Licensed: bool
    RefusalCount: int }

let private rng (seed: int) = System.Random(seed)

let scenarioOf (seed: int) : Scenario =
  let r = rng seed
  let count = 1 + r.Next(4)
  let sites =
    Array.init count (fun _ ->
      { Name = sprintf "b%d" (r.Next(3))
        IsWrite = r.Next(4) = 0
        InLambda = r.Next(4) = 0
        InsideLoop = r.Next(4) = 0
        Used = r.Next(6) <> 0 })

  { Seed = seed
    SiteCount = count
    Sites = sites
    // Derived independently of the validator, so a bug in the validator cannot
    // hide behind it.
    AllPlainReads = sites |> Array.forall (fun s -> s.Used && not s.IsWrite && not s.InLambda && not s.InsideLoop) }

let run (scenario: Scenario) : Trace =
  let refusalCount =
    scenario.Sites
    |> Array.sumBy (fun s ->
      match validateUseSite s with
      | Verdict.NotEquivalent rs -> rs.Length
      | Verdict.Undecidable rs -> rs.Length
      | Verdict.Equivalent _ -> 0)

  match validateRewrite "binding" scenario.Sites with
  | Verdict.Equivalent _ -> { Scenario = scenario; Licensed = true; RefusalCount = refusalCount }
  | Verdict.NotEquivalent _ -> { Scenario = scenario; Licensed = false; RefusalCount = refusalCount }
  | Verdict.Undecidable _ -> { Scenario = scenario; Licensed = false; RefusalCount = refusalCount }

/// TWIN — checks only the FIRST use site, and reads `Undecidable` as a pass.
/// The two bugs a real implementation of this gate would plausibly have.
let runFirstSiteOnlyTwin (scenario: Scenario) : Trace =
  let first = scenario.Sites |> Array.tryHead

  let licensed =
    match first with
    | None -> true
    | Some s ->
      match validateUseSite s with
      | Verdict.Equivalent _ -> true
      | Verdict.NotEquivalent _ -> false
      // The bug: unknown is treated as good.
      | Verdict.Undecidable _ -> true

  { Scenario = scenario; Licensed = licensed; RefusalCount = 0 }
