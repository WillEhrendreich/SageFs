module SageFs.Tests.AppStateCustomTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState

// ---------------------------------------------------------------------------
// Test-local IFeatureState types for unit tests.
// ---------------------------------------------------------------------------
type IntState = { Value: int } with interface IFeatureState
type StringState = { Value: string } with interface IFeatureState
type SetState = { Files: Set<string> } with interface IFeatureState

// ---------------------------------------------------------------------------
// Minimal AppState stub — only Custom field matters for these tests.
// ---------------------------------------------------------------------------
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
      AppStateCustom.tryGetFeature<IntState> "missing" state
      |> Expect.isNone "absent key should be None"

    testCase "set then tryGetFeature round-trips IFeatureState value" <| fun _ ->
      let state = makeState Map.empty
      let updated = AppStateCustom.set "myKey" { IntState.Value = 42 } state
      AppStateCustom.tryGetFeature<IntState> "myKey" updated
      |> Expect.equal "should round-trip" (Some { Value = 42 })

    testCase "set then tryGetFeature round-trips string-wrapped value" <| fun _ ->
      let state = makeState Map.empty
      let updated = AppStateCustom.set "strKey" { StringState.Value = "hello" } state
      AppStateCustom.tryGetFeature<StringState> "strKey" updated
      |> Expect.equal "should round-trip string" (Some { Value = "hello" })

    testCase "set then tryGetFeature round-trips Set-wrapped value" <| fun _ ->
      let state = makeState Map.empty
      let files = Set.ofList ["a.fs"; "b.fs"]
      let updated = AppStateCustom.set "files" { SetState.Files = files } state
      AppStateCustom.tryGetFeature<SetState> "files" updated
      |> Option.map (fun s -> s.Files)
      |> Expect.equal "should round-trip set" (Some files)

    testCase "set overwrites previous value" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "k" { IntState.Value = 1 } state
      let s2 = AppStateCustom.set "k" { IntState.Value = 2 } s1
      AppStateCustom.tryGetFeature<IntState> "k" s2
      |> Expect.equal "should be updated value" (Some { Value = 2 })

    testCase "remove eliminates key" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "k" { StringState.Value = "v" } state
      let s2 = AppStateCustom.remove "k" s1
      AppStateCustom.tryGetFeature<StringState> "k" s2
      |> Expect.isNone "removed key should be absent"

    testCase "remove on absent key is a no-op" <| fun _ ->
      let state = makeState Map.empty
      let s2 = AppStateCustom.remove "nonexistent" state
      s2.Custom
      |> Expect.equal "map should be unchanged" Map.empty

    testCase "multiple keys coexist" <| fun _ ->
      let state = makeState Map.empty
      let s =
        state
        |> AppStateCustom.set "age" { IntState.Value = 42 }
        |> AppStateCustom.set "name" { StringState.Value = "alice" }
      AppStateCustom.tryGetFeature<IntState> "age" s
      |> Expect.equal "age should be 42" (Some { Value = 42 })
      AppStateCustom.tryGetFeature<StringState> "name" s
      |> Expect.equal "name should be alice" (Some { Value = "alice" })

    testCase "set preserves other keys" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "a" { IntState.Value = 1 } state
      let s2 = AppStateCustom.set "b" { IntState.Value = 2 } s1
      AppStateCustom.tryGetFeature<IntState> "a" s2
      |> Expect.equal "key a should be preserved" (Some { Value = 1 })

    testCase "openedFiles key constant matches contract doc" <| fun _ ->
      SageFs.Middleware.Directives.OpenDirective.openedFileKey
      |> Expect.equal "key must match contract" "openedFiles"

    testCase "set accepts any typed value including feature records" <| fun _ ->
      // AppStateCustom.set stores any value as obj — typed retrieval uses dynamic cast.
      // This test verifies that a feature record round-trips via tryGetFeature.
      let state = makeState Map.empty
      let files = SageFs.Middleware.Directives.OpenDirective.OpenedFiles.ofSet (Set.ofList ["a.fs"])
      let updated = AppStateCustom.set SageFs.Middleware.Directives.OpenDirective.openedFileKey files state
      AppStateCustom.tryGetFeature<SageFs.Middleware.Directives.OpenDirective.OpenedFiles>
        SageFs.Middleware.Directives.OpenDirective.openedFileKey updated
      |> Option.map (fun s -> s.Files)
      |> Expect.equal "IFeatureState round-trips via typed accessor" (Some (Set.ofList ["a.fs"]))

    testCase "tryGetFeature returns None for absent key" <| fun _ ->
      let state = makeState Map.empty
      AppStateCustom.tryGetFeature<SageFs.Middleware.Directives.OpenDirective.OpenedFiles>
        "missing" state
      |> Expect.isNone "absent key returns None"

    testCase "tryGetFeature returns None for wrong type" <| fun _ ->
      let state = makeState Map.empty
      let files = SageFs.Middleware.Directives.OpenDirective.OpenedFiles.ofSet Set.empty
      let updated = AppStateCustom.set SageFs.Middleware.Directives.OpenDirective.openedFileKey files state
      // Try to read with wrong feature type — must return None, not throw
      AppStateCustom.tryGetFeature<SageFs.Middleware.HotReloading.State>
        SageFs.Middleware.Directives.OpenDirective.openedFileKey updated
      |> Expect.isNone "wrong type returns None"
  ]
