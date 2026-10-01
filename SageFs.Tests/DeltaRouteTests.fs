/// Where a save goes when the app was started by `run_app`, and what a process's chain of deltas is good for.
///
/// The decisions are pure, so they are checked over every combination of their inputs against a statement of the
/// rule that does not share their code. The route itself (the build, the runtime call, the probe that shows the
/// new body running) is what `RunAppDeltaTests` runs against a real host.
module SageFs.Tests.DeltaRouteTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.MetadataDelta
open SageFs.Features.ReloadPlanning
open SageFs.WorkflowTypes

let private mvidA = Guid.Parse "11111111-1111-1111-1111-111111111111"
let private mvidB = Guid.Parse "22222222-2222-2222-2222-222222222222"

let private oneGap = [ CapabilityGap.DebuggerAttached ]

let private cause = RudeCause.FieldsChanged "Handlers+clo@25"

let private decl (name: string) : SourceDecl =
  { Name = name
    Kind = DeclKind.FunctionDecl
    Access = DeclAccess.Public
    Container = []
    Header = name
    Text = name
    StartLine = 1
    EndLine = 1 }

let private standings : Standing list =
  [ Standing.NoBaseline
    Standing.Unusable "an earlier delta did not apply"
    Standing.Tracking(mvidA, 0)
    Standing.Tracking(mvidA, 3)
    Standing.Tracking(mvidB, 3) ]

let private preparations : Preparation list =
  [ Preparation.NothingChanged
    Preparation.Refused [ cause ]
    Preparation.Refused []
    Preparation.Ready 0
    Preparation.Ready 3
    Preparation.Ready 4 ]

/// The rule, stated from its parts and not from `decide`: a delta is applied only when the chain is tracking the module
/// that is running, nothing stands in the way, and the delta was prepared from the generation the process is at.
let private mayApply (standing: Standing) (running: Guid) (gaps: CapabilityGap list) (preparation: Preparation) : bool =
  match standing, gaps, preparation with
  | Standing.Tracking(baseline, generation), [], Preparation.Ready from -> baseline = running && from = generation
  | _ -> false

[<Tests>]
let deltaRouteTests =
  testList "delta route" [

    // -- where a save goes ---------------------------------------------------------------------------------

    testCase "WHY - a body edit to a run_app app goes to the delta route only when the worker was started for it" <| fun _ ->
      let plan = ReloadPlan.PatchFunctions [ decl "describe" ]
      match PatchRoute.choose AppPlacement.InWorkerProcess MetadataDeltaMode.On plan with
      | SaveRoute.ByMetadataDelta [ d ] -> d.Name |> Expect.equal "the functions the save changed travel with it" "describe"
      | other -> failtestf "should be a delta, and is %A" other
      match PatchRoute.choose AppPlacement.InWorkerProcess MetadataDeltaMode.Off plan with
      | SaveRoute.PerPlan (ReloadPlan.RestartRequired (ReloadChange.RunsOutsideAgent "describe", [])) -> ()
      | other -> failtestf "with the route off the edit restarts, as it always did: %A" other

    testCase "WHY - an app that runs in the reload agent's own process is patched by the detour, and the delta route never touches it" <| fun _ ->
      let plan = ReloadPlan.PatchFunctions [ decl "describe" ]
      for mode in [ MetadataDeltaMode.On; MetadataDeltaMode.Off ] do
        match PatchRoute.choose AppPlacement.InAgentProcess mode plan with
        | SaveRoute.PerPlan (ReloadPlan.PatchFunctions [ d ]) -> d.Name |> Expect.equal "the plan is the planner's own" "describe"
        | other -> failtestf "the agent's app keeps the detour with the route %A: %A" mode other

    testCase "WHY - an edit the planner sends to a restart stays one, with the planner's more specific reason, whatever the route" <| fun _ ->
      let restart = ReloadPlan.RestartRequired (ReloadChange.TypeChanged "Shape", [])
      let keeping = ReloadPlan.PatchKeepingState ([ decl "f" ], LiveState.Kept (decl "x"), [])
      for mode in [ MetadataDeltaMode.On; MetadataDeltaMode.Off ] do
        match PatchRoute.choose AppPlacement.InWorkerProcess mode restart with
        | SaveRoute.PerPlan (ReloadPlan.RestartRequired (ReloadChange.TypeChanged "Shape", [])) -> ()
        | other -> failtestf "a type change keeps its reason: %A" other
        match PatchRoute.choose AppPlacement.InWorkerProcess mode keeping with
        | SaveRoute.PerPlan (ReloadPlan.RestartRequired _) -> ()
        | other -> failtestf "live state in a run_app app restarts, a delta does not keep it: %A" other

    testCase "WHY - a save that changed nothing is not a delta, so it stays the non-event it is" <| fun _ ->
      match PatchRoute.choose AppPlacement.InWorkerProcess MetadataDeltaMode.On (ReloadPlan.PatchFunctions []) with
      | SaveRoute.PerPlan (ReloadPlan.PatchFunctions []) -> ()
      | other -> failtestf "nothing to patch is nothing to build: %A" other

    // -- how a worker is started for it ---------------------------------------------------------------------

    testCase "WHY - a hot-reload worker started for the route gets the runtime's variable and says so, and one that is not does not carry the cost" <| fun _ ->
      let hot = SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults
      let envOf (mode: MetadataDeltaMode) (workflow: SessionWorkflow) =
        Args.buildWorkerSpawnConfigWith mode "s" [ SessionProjectTarget.Bare ] false true workflow |> snd |> Map.ofList
      let on = envOf MetadataDeltaMode.On hot
      on[MetadataDeltaMode.environmentVariable] |> Expect.equal "the worker is told" "on"
      on[MetadataDeltaMode.modifiableAssembliesVariable] |> Expect.equal "and can edit its assemblies" "debug"
      let off = envOf MetadataDeltaMode.Off hot
      off[MetadataDeltaMode.environmentVariable] |> Expect.equal "told it is off" "off"
      off.ContainsKey MetadataDeltaMode.modifiableAssembliesVariable |> Expect.isFalse "and does not carry the variable"
      let interactive = envOf MetadataDeltaMode.On SessionWorkflow.Interactive
      interactive.ContainsKey MetadataDeltaMode.environmentVariable |> Expect.isFalse "a worker that is not hot reloading has no use for it"
      interactive.ContainsKey MetadataDeltaMode.modifiableAssembliesVariable |> Expect.isFalse "and does not pay for it"

    testCase "WHY - the variable the worker needs is the one nothing the worker starts may inherit: its build and the REPL host would pay for it" <| fun _ ->
      ProcessEnvironment.poisonedVariables |> List.contains MetadataDeltaMode.modifiableAssembliesVariable
      |> Expect.isTrue "it is stripped from every spawned process"
      let psi = System.Diagnostics.ProcessStartInfo "dotnet"
      psi.Environment[MetadataDeltaMode.modifiableAssembliesVariable] <- "debug"
      ProcessEnvironment.applyTo psi []
      psi.Environment.ContainsKey MetadataDeltaMode.modifiableAssembliesVariable |> Expect.isFalse "a child does not inherit it"
      ProcessEnvironment.applyTo psi [ MetadataDeltaMode.modifiableAssembliesVariable, MetadataDeltaMode.modifiableAssembliesValue ]
      psi.Environment[MetadataDeltaMode.modifiableAssembliesVariable] |> Expect.equal "the worker, which names it, does" "debug"

    testCase "WHY - the flag is read from the spellings a person types, and anything else is the default" <| fun _ ->
      [ "on", MetadataDeltaMode.On; "1", MetadataDeltaMode.On; "TRUE", MetadataDeltaMode.On
        "off", MetadataDeltaMode.Off; "0", MetadataDeltaMode.Off; " False ", MetadataDeltaMode.Off
        "maybe", MetadataDeltaMode.defaultMode; "", MetadataDeltaMode.defaultMode ]
      |> List.iter (fun (text, expected) -> MetadataDeltaMode.parse text |> Expect.equal (sprintf "%A" text) expected)
      MetadataDeltaMode.parse null |> Expect.equal "unset is the default" MetadataDeltaMode.defaultMode

    // -- what a chain is good for ---------------------------------------------------------------------------

    testCase "WHY - a delta is applied exactly when the chain tracks the module that runs, nothing stands in the way, and it was prepared from where the process is" <| fun _ ->
      let wrong =
        [ for standing in standings do
            for running in [ mvidA; mvidB ] do
              for gaps in [ []; oneGap ] do
                for preparation in preparations do
                  let decided = (DeltaSession.decide standing running gaps preparation = SaveDecision.Apply)
                  match decided = mayApply standing running gaps preparation with
                  | true -> ()
                  | false -> yield sprintf "%A running %A gaps %A %A: applies %b" standing running gaps preparation decided ]
      wrong |> Expect.isEmpty "every combination agrees with the rule"

    testCase "WHY - a process whose chain is gone or broken restarts for every save, a save that changed nothing included" <| fun _ ->
      for standing in [ Standing.NoBaseline; Standing.Unusable "an earlier delta did not apply" ] do
        for preparation in preparations do
          match DeltaSession.decide standing mvidA [] preparation with
          | SaveDecision.Restart(Refusal.Unavailable _, []) -> ()
          | other -> failtestf "%A %A: should restart, and is %A" standing preparation other

    testCase "WHY - an edit the emitter refuses is a restart that names every cause, and a refusal with no cause still says something" <| fun _ ->
      match DeltaSession.decide (Standing.Tracking(mvidA, 0)) mvidA [] (Preparation.Refused [ cause; RudeCause.TypeAdded "Handlers+clo@31" ]) with
      | SaveDecision.Restart(Refusal.Rude first, [ Refusal.Rude second ]) ->
        (first, second) |> Expect.equal "both causes, in order" (cause, RudeCause.TypeAdded "Handlers+clo@31")
      | other -> failtestf "should name both: %A" other
      match DeltaSession.decide (Standing.Tracking(mvidA, 0)) mvidA [] (Preparation.Refused []) with
      | SaveDecision.Restart(Refusal.Unavailable why, []) -> why |> Expect.isNotEmpty "a refusal says why"
      | other -> failtestf "an unnamed refusal is still a restart: %A" other

    testCase "WHY - the chain advances exactly when the code changed, and a delta the runtime did not take makes the route unusable" <| fun _ ->
      let tracking = Standing.Tracking(mvidA, 2)
      DeltaSession.settle tracking Landing.Landed |> Expect.equal "applied" (Standing.Tracking(mvidA, 3))
      DeltaSession.settle tracking (Landing.LandedWithHandlerFailures [ "cache" ]) |> Expect.equal "applied, a handler threw: the code changed" (Standing.Tracking(mvidA, 3))
      match DeltaSession.settle tracking (Landing.DidNotLand "the runtime said no") with
      | Standing.Unusable why -> why |> Expect.equal "and says why" "the runtime said no"
      | other -> failtestf "a delta that did not land leaves nothing to trust: %A" other

    testCase "WHY - every answer the runtime can give reads to the chain as landed or not, with the runtime's words kept" <| fun _ ->
      DeltaSession.landingOf ApplyOutcome.Applied |> Expect.equal "applied" Landing.Landed
      DeltaSession.landingOf (ApplyOutcome.AppliedHandlersFailed [ "x" ]) |> Expect.equal "handlers" (Landing.LandedWithHandlerFailures [ "x" ])
      for outcome in
        [ ApplyOutcome.RuntimeNotModifiable "env"
          ApplyOutcome.DebuggerAttached
          ApplyOutcome.NotSupported "nope"
          ApplyOutcome.Rejected "bad" ] do
        match DeltaSession.landingOf outcome with
        | Landing.DidNotLand why -> why |> Expect.isNotEmpty (sprintf "%A says why" outcome)
        | other -> failtestf "%A did not land, and reads as %A" outcome other

    testCase "WHY - a process starts tracking the module it loaded, or says what stops the runtime editing it" <| fun _ ->
      DeltaSession.start mvidA [] |> Expect.equal "nothing in the way" (Standing.Tracking(mvidA, 0))
      match DeltaSession.start mvidA [ CapabilityGap.EnvironmentNotModifiable "unset"; CapabilityGap.ModuleOptimized "App" ] with
      | Standing.Unusable why ->
        why |> Expect.stringContains "the environment is named" "DOTNET_MODIFIABLE_ASSEMBLIES"
        why |> Expect.stringContains "and the optimized module" "App"
      | other -> failtestf "should be unusable: %A" other
  ]
