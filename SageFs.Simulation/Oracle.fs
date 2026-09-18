namespace SageFs.Simulation

open System
open SageFs.Simulation.Scenario
open SageFs.Simulation.Runner
open SageFs.Simulation.ReferenceModel

/// Cross-checks the REAL `Runner.Trace` — the actual `RestartPolicy` +
/// `SessionLifecycle` composition — against the INDEPENDENT
/// `ReferenceModel`, step by step. This is stronger than the property-based
/// invariants in `Invariants.fs`: invariants check that *some property*
/// holds of the real trace alone; the oracle checks *exact behavioral
/// equivalence* to a from-first-principles spec model. A `Divergence` is a
/// genuine bug report — the real supervision core did something the spec
/// model, computed independently, did not expect.
module Oracle =

  /// Which field of a step disagreed between the real trace and the
  /// reference model.
  [<RequireQualifiedAccess>]
  type DivergedField =
    | Effect
    | Delay
    | Terminality
    | RestartCount
    /// The two traces have a different number of steps — a structural
    /// divergence that makes per-step comparison impossible.
    | StepCount

  /// A single disagreement between the real trace and the reference model.
  /// A structured record (not a bare string) so a caller can filter/group
  /// findings by field, not just print them.
  type Divergence =
    { StepIndex: int
      Field: DivergedField
      Expected: string
      Actual: string }

  let private effectKindOf (e: StepEffect) : RefEffectKind =
    match e with
    | StepEffect.NoEffect -> RefEffectKind.NoEffect
    | StepEffect.Stopped -> RefEffectKind.Stopped
    | StepEffect.Restarted _ -> RefEffectKind.Restarted
    | StepEffect.GaveUp _ -> RefEffectKind.GaveUp

  let private delayOf (e: StepEffect) : TimeSpan option =
    match e with
    | StepEffect.Restarted d -> Some d
    | StepEffect.NoEffect
    | StepEffect.Stopped
    | StepEffect.GaveUp _ -> None

  /// Compare one real step against its expected reference step. Total: every
  /// comparable field is checked, and a mismatch on any of them is reported
  /// independently (a step can diverge on more than one field at once).
  let private stepDivergences (real: Step) (reference: ReferenceStep) : Divergence list =
    let mk field expected actual =
      { StepIndex = real.Index; Field = field; Expected = expected; Actual = actual }

    let effectDivergence =
      let actualKind = effectKindOf real.Effect
      match actualKind = reference.EffectKind with
      | true -> None
      | false -> Some(mk DivergedField.Effect (sprintf "%A" reference.EffectKind) (sprintf "%A" actualKind))

    let delayDivergence =
      let actualDelay = delayOf real.Effect
      match actualDelay = reference.Delay with
      | true -> None
      | false -> Some(mk DivergedField.Delay (sprintf "%A" reference.Delay) (sprintf "%A" actualDelay))

    let terminalityDivergence =
      let actualTerminal = isTerminal real.Status
      match actualTerminal = reference.Terminal with
      | true -> None
      | false ->
        Some(mk DivergedField.Terminality (sprintf "%b" reference.Terminal) (sprintf "%b" actualTerminal))

    let restartCountDivergence =
      match real.RestartState.RestartCount = reference.RestartCount with
      | true -> None
      | false ->
        Some(
          mk
            DivergedField.RestartCount
            (sprintf "%d" reference.RestartCount)
            (sprintf "%d" real.RestartState.RestartCount)
        )

    [ effectDivergence; delayDivergence; terminalityDivergence; restartCountDivergence ]
    |> List.choose id

  /// Cross-check a real trace against an expected reference trace, per step.
  /// Empty list = full agreement. A step-count mismatch is reported as a
  /// single structural divergence rather than crashing the zip.
  let check (real: Trace) (reference: ReferenceTrace) : Divergence list =
    match List.length real.Steps = List.length reference.Steps with
    | false ->
      [ { StepIndex = -1
          Field = DivergedField.StepCount
          Expected = sprintf "%d reference steps" (List.length reference.Steps)
          Actual = sprintf "%d real steps" (List.length real.Steps) } ]
    | true ->
      List.zip real.Steps reference.Steps
      |> List.collect (fun (r, ref) -> stepDivergences r ref)

  /// Run a scenario through both the real code and the reference model, and
  /// cross-check them. Empty list = the real supervision core agrees with
  /// the independent spec.
  let checkScenario (scenario: Scenario) : Divergence list =
    check (Runner.run scenario) (ReferenceModel.compute scenario)
