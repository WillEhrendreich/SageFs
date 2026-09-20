module SageFs.Tests.WorkerMainTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Middleware.HotReloadCore
open SageFs.Features.ReloadPlanning
open SageFs.Features.ReloadOutcome
open SageFs.Server.WorkerMain

let sampleTestCase id fullName displayName =
  { Id = TestId.TestId id
    FullName = fullName
    DisplayName = displayName
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let expectoProvider =
  ProviderDescription.AttributeBased
    { Name = TestFramework.Expecto
      TestAttributes = [ "Tests" ]
      AssemblyMarker = "Expecto" }

[<Tests>]
let workerMainTests =
  testList "WorkerMain" [
    testCase "initial discovery aggregation removes duplicate discovered tests by TestId"
    <| fun _ ->
      let duplicate =
        sampleTestCase "dup" "Sample.Tests.add infers int" "add infers int"
      let unique =
        sampleTestCase "unique" "Sample.Tests.subtract infers int" "subtract infers int"

      let first =
        { LiveTestHookResult.empty with
            DetectedProviders = [ expectoProvider ]
            DiscoveredTests = [| duplicate |] }

      let second =
        { LiveTestHookResult.empty with
            DetectedProviders = [ expectoProvider ]
            DiscoveredTests = [| duplicate; unique |] }

      let tests, providers =
        SageFs.Server.WorkerMain.mergeInitialDiscoveryResults [| first; second |]

      tests.Length |> Expect.equal "should collapse duplicate test identities" 2
      providers.Length |> Expect.equal "should keep one provider entry" 1
      tests |> Array.map (fun test -> test.Id) |> Array.distinct |> Array.length
      |> Expect.equal "should keep unique test ids only" 2

    testCase "WHY — worker quarantine preserves Mono.Cecil because coverage instrumentation executes before project dependency resolution" <| fun _ ->
      [ "Mono.Cecil.dll"
        "Mono.Cecil.Rocks.dll"
        "Mono.Cecil.Mdb.dll"
        "Mono.Cecil.Pdb.dll" ]
      |> List.iter (fun assemblyName ->
        SageFs.Server.WorkerMain.shouldQuarantineAssembly assemblyName
        |> Expect.isFalse (sprintf "%s must remain in the worker probing root" assemblyName))

    testCase "WHY — worker quarantine still isolates project-owned collisions because protecting runtime dependencies must not disable isolation" <| fun _ ->
      SageFs.Server.WorkerMain.shouldQuarantineAssembly "Falco.dll"
      |> Expect.isTrue "unrelated project collision should remain quarantinable"

      SageFs.Server.WorkerMain.shouldQuarantineAssembly "fr-FR.resources.dll"
      |> Expect.isFalse "satellite resources should never be quarantined"
  ]

// ── escalationOf — what a mutable binding's detour outcome means for a save ──
//
// The gap this closes: `applyDetourPlan` reports Torn/Declined/NeitherLeg
// bindings in full, but `handleNewAsmFromRepl` used to return only the names
// that landed — so a torn binding died in the log and reached no one.
// `escalationOf` is the pure decision at the far end of that pipeline: given
// what a binding's detour did, does this save force a restart, or merely
// carry an extra reason alongside whatever else was patched?

let private neitherBinding = "Demo.App.Config.counter"
let private declinedBinding = { Binding = "Demo.App.Config.orphan"; Orphan = OrphanedLeg.GetterWithoutSetter }

[<Tests>]
let escalationOfTests =
  testList "WorkerMain.escalationOf" [
    testCase "WHY — WorkerMain.escalationOf — nothing to report is ExtraReasons [], because no binding issue means nothing extra to say" <| fun _ ->
      escalationOf [] []
      |> Expect.equal "no reasons" (BindingEscalation.ExtraReasons [])

    testCase "WHY — WorkerMain.escalationOf — a binding whose both legs redirected cleanly adds nothing, because a landed binding is not a missed one" <| fun _ ->
      escalationOf [ BindingOutcome.BothLegsRedirected "Demo.App.Config.ok" ] []
      |> Expect.equal "nothing to add for a landed binding" (BindingEscalation.ExtraReasons [])

    testCase "WHY — WorkerMain.escalationOf — a binding neither leg reached is reported NotYetSupported, carrying the binding AND the reason, because it is a capability gap (preflight/Harmony failed) rather than a state ambiguity" <| fun _ ->
      escalationOf [ BindingOutcome.NeitherLegRedirected(neitherBinding, "boom") ] []
      |> function
        | BindingEscalation.ExtraReasons [ RestartReason.NotYetSupported shape ] ->
          shape |> Expect.stringContains "names the binding" neitherBinding
          shape |> Expect.stringContains "carries the underlying reason" "boom"
        | other -> failtestf "expected one NotYetSupported reason, got %A" other

    testCase "WHY — WorkerMain.escalationOf — a declined orphan leg is reported MutableModuleState, the SAME case a source-level mutable-state edit gets, because the remedy (restart to re-run the initialiser) is identical" <| fun _ ->
      escalationOf [] [ declinedBinding ]
      |> Expect.equal "declined -> MutableModuleState"
        (BindingEscalation.ExtraReasons [ RestartReason.MutableModuleState declinedBinding.Binding ])

    testCase "WHY — WorkerMain.escalationOf — a SINGLE torn binding forces a restart via MutableBindingTorn, never joining the extra-reasons list, because a tear must not be reportable as a mere miss" <| fun _ ->
      escalationOf [ BindingOutcome.Torn("Demo.App.Config.counter", "one leg failed") ] []
      |> Expect.equal "forces restart"
        (BindingEscalation.ForcesRestart(ReloadChange.MutableBindingTorn "Demo.App.Config.counter", []))

    testCase "WHY — WorkerMain.escalationOf — EVERY torn binding is named, not just the first, because a save can tear more than one binding at once" <| fun _ ->
      escalationOf
        [ BindingOutcome.Torn("Demo.App.Config.a", "r1")
          BindingOutcome.Torn("Demo.App.Config.b", "r2") ]
        []
      |> Expect.equal "both named, in order"
        (BindingEscalation.ForcesRestart(
          ReloadChange.MutableBindingTorn "Demo.App.Config.a",
          [ ReloadChange.MutableBindingTorn "Demo.App.Config.b" ]))

    testCase "WHY — WorkerMain.escalationOf — a tear wins the WHOLE verdict even alongside declined/never-landed bindings, because restarting already clears every one of them and a mixed report must not soften the dangerous case into an ordinary miss" <| fun _ ->
      escalationOf
        [ BindingOutcome.Torn("Demo.App.Config.counter", "one leg failed")
          BindingOutcome.NeitherLegRedirected(neitherBinding, "boom")
          BindingOutcome.BothLegsRedirected "Demo.App.Config.ok" ]
        [ declinedBinding ]
      |> function
        | BindingEscalation.ForcesRestart(ReloadChange.MutableBindingTorn "Demo.App.Config.counter", []) -> ()
        | other -> failtestf "a tear must win outright, got %A" other

    testProperty "WHY — WorkerMain.escalationOf — a broken variant that ignores Torn (reports it as an ordinary missed binding) must fail: this proves the test has teeth" <| fun (binding: string, reason: string) ->
      // A deliberately-wrong "escalation" that never forces a restart —
      // the exact regression this gap report described (Torn dying in the
      // log). The REAL escalationOf must disagree with it whenever Torn is
      // present.
      let brokenAlwaysExtraReasons (bindings: BindingOutcome list) =
        bindings
        |> List.choose (function
          | BindingOutcome.Torn(b, r) -> Some(RestartReason.MutableModuleState(sprintf "%s (%s)" b r))
          | _ -> None)
        |> BindingEscalation.ExtraReasons
      let real = escalationOf [ BindingOutcome.Torn(binding, reason) ] []
      let broken = brokenAlwaysExtraReasons [ BindingOutcome.Torn(binding, reason) ]
      real <> broken
  ]
