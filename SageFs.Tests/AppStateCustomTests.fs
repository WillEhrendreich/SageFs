module SageFs.Tests.AppStateCustomTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState
open SageFs.EvalActorDecision
open SageFs.Middleware.Directives.OpenDirective

let private mkFiles names = OpenedFiles.ofSet (Set.ofList names)

let private makeState customMap : AppState =
  {
    Solution = Unchecked.defaultof<_>
    OriginalSolution = Unchecked.defaultof<_>
    ShadowDir = None
    Logger = Unchecked.defaultof<_>
    Session = Unchecked.defaultof<_>
    OutStream = Unchecked.defaultof<_>
    StartupConfig = None
    Custom = customMap
    Diagnostics = Unchecked.defaultof<_>
    WarmupFailures = []
    WarmupContext = Unchecked.defaultof<_>
    HotReloadState = Unchecked.defaultof<_>
  }

[<Tests>]
let appStateCustomTests =
  testList "AppStateCustom" [

    testCase "tryGetFeature returns None when key absent" <| fun _ ->
      let state = makeState Map.empty
      (AppStateCustom.tryGetFeature<OpenedFiles> "missing" state) |> Expect.isNone "absent key should be None"

    testCase "set then tryGetFeature round-trips value" <| fun _ ->
      let state = makeState Map.empty
      let files = mkFiles ["a.fs"; "b.fs"]
      let updated = AppStateCustom.set "myKey" files state
      (AppStateCustom.tryGetFeature<OpenedFiles> "myKey" updated) |> Expect.equal "should round-trip" (Some files)

    testCase "set then tryGetFeature round-trips empty set" <| fun _ ->
      let state = makeState Map.empty
      let updated = AppStateCustom.set "k" OpenedFiles.empty state
      (AppStateCustom.tryGetFeature<OpenedFiles> "k" updated) |> Expect.equal "should round-trip empty" (Some OpenedFiles.empty)

    testCase "set then tryGetFeature round-trips multi-file set" <| fun _ ->
      let state = makeState Map.empty
      let files = mkFiles ["a.fs"; "b.fs"; "c.fs"]
      let updated = AppStateCustom.set "files" files state
      let result = AppStateCustom.tryGetFeature<OpenedFiles> "files" updated |> Option.map (fun s -> s.Files)
      result |> Expect.equal "should round-trip set" (Some (Set.ofList ["a.fs"; "b.fs"; "c.fs"]))

    testCase "set overwrites previous value" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "k" (mkFiles ["old.fs"]) state
      let s2 = AppStateCustom.set "k" (mkFiles ["new.fs"]) s1
      let result = AppStateCustom.tryGetFeature<OpenedFiles> "k" s2 |> Option.map (fun s -> s.Files)
      result |> Expect.equal "should be updated value" (Some (Set.singleton "new.fs"))

    testCase "remove eliminates key" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "k" (mkFiles ["x.fs"]) state
      let s2 = AppStateCustom.remove "k" s1
      (AppStateCustom.tryGetFeature<OpenedFiles> "k" s2) |> Expect.isNone "removed key should be absent"

    testCase "remove on absent key is a no-op" <| fun _ ->
      let state = makeState Map.empty
      let s2 = AppStateCustom.remove "nonexistent" state
      s2.Custom |> Expect.equal "map should be unchanged" (Map.empty<string, obj>)

    testCase "multiple keys coexist" <| fun _ ->
      let state = makeState Map.empty
      let s =
        state
        |> AppStateCustom.set "sources" (mkFiles ["a.fs"])
        |> AppStateCustom.set "tests" (mkFiles ["b.fs"])
      let srcResult = AppStateCustom.tryGetFeature<OpenedFiles> "sources" s |> Option.map (fun s -> s.Files)
      let tstResult = AppStateCustom.tryGetFeature<OpenedFiles> "tests" s |> Option.map (fun s -> s.Files)
      srcResult |> Expect.equal "sources key preserved" (Some (Set.singleton "a.fs"))
      tstResult |> Expect.equal "tests key preserved" (Some (Set.singleton "b.fs"))

    testCase "set preserves other keys" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "a" (mkFiles ["a.fs"]) state
      let s2 = AppStateCustom.set "b" (mkFiles ["b.fs"]) s1
      let result = AppStateCustom.tryGetFeature<OpenedFiles> "a" s2 |> Option.map (fun s -> s.Files)
      result |> Expect.equal "key a should be preserved" (Some (Set.singleton "a.fs"))

    testCase "openedFiles key constant matches contract doc" <| fun _ ->
      openedFileKey |> Expect.equal "key must match contract" "openedFiles"

    testCase "feature record round-trips via tryGetFeature" <| fun _ ->
      let state = makeState Map.empty
      let files = mkFiles ["a.fs"]
      let updated = AppStateCustom.set openedFileKey files state
      let result = AppStateCustom.tryGetFeature<OpenedFiles> openedFileKey updated |> Option.map (fun s -> s.Files)
      result |> Expect.equal "round-trips via typed accessor" (Some (Set.singleton "a.fs"))

    testCase "tryGetFeature returns None for wrong type" <| fun _ ->
      let state = makeState Map.empty
      let files = mkFiles []
      let updated = AppStateCustom.set openedFileKey files state
      (AppStateCustom.tryGetFeature<SageFs.Middleware.HotReloadCore.State> openedFileKey updated) |> Expect.isNone "wrong type returns None"
  ]

[<Tests>]
let sessionPhaseStatusMessageTests =
  testList "SessionPhase statusMessage" [
    testCase "WHY — SessionPhase.statusMessage — a faulted phase reports why because the daemon and the card must show the reason, not just Faulted" <| fun _ ->
      SessionPhase.statusMessage (Faulted "Missing DLL /src/App/bin/Debug/net10.0/App.dll")
      |> Expecto.Flip.Expect.equal "the fault reason" (Some "Missing DLL /src/App/bin/Debug/net10.0/App.dll")

    testCase "WHY — SessionPhase.statusMessage — an initializing phase reports its progress because warmup must not look stuck" <| fun _ ->
      SessionPhase.statusMessage (Initializing (Some "Loading 3 projects"))
      |> Expecto.Flip.Expect.equal "the progress message" (Some "Loading 3 projects")

    testCase "WHY — SessionPhase.statusMessage — an active phase reports nothing because a ready session has no status to explain" <| fun _ ->
      SessionPhase.statusMessage (Active (makeState Map.empty, Idle))
      |> Expecto.Flip.Expect.isNone "no message"
  ]
