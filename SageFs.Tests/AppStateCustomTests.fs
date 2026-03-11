module SageFs.Tests.AppStateCustomTests

open Expecto
open SageFs
open SageFs.AppState
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
      Expect.isNone (AppStateCustom.tryGetFeature<OpenedFiles> "missing" state) "absent key should be None"

    testCase "set then tryGetFeature round-trips value" <| fun _ ->
      let state = makeState Map.empty
      let files = mkFiles ["a.fs"; "b.fs"]
      let updated = AppStateCustom.set "myKey" files state
      Expect.equal (AppStateCustom.tryGetFeature<OpenedFiles> "myKey" updated) (Some files) "should round-trip"

    testCase "set then tryGetFeature round-trips empty set" <| fun _ ->
      let state = makeState Map.empty
      let updated = AppStateCustom.set "k" OpenedFiles.empty state
      Expect.equal (AppStateCustom.tryGetFeature<OpenedFiles> "k" updated) (Some OpenedFiles.empty) "should round-trip empty"

    testCase "set then tryGetFeature round-trips multi-file set" <| fun _ ->
      let state = makeState Map.empty
      let files = mkFiles ["a.fs"; "b.fs"; "c.fs"]
      let updated = AppStateCustom.set "files" files state
      let result = AppStateCustom.tryGetFeature<OpenedFiles> "files" updated |> Option.map (fun s -> s.Files)
      Expect.equal result (Some (Set.ofList ["a.fs"; "b.fs"; "c.fs"])) "should round-trip set"

    testCase "set overwrites previous value" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "k" (mkFiles ["old.fs"]) state
      let s2 = AppStateCustom.set "k" (mkFiles ["new.fs"]) s1
      let result = AppStateCustom.tryGetFeature<OpenedFiles> "k" s2 |> Option.map (fun s -> s.Files)
      Expect.equal result (Some (Set.singleton "new.fs")) "should be updated value"

    testCase "remove eliminates key" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "k" (mkFiles ["x.fs"]) state
      let s2 = AppStateCustom.remove "k" s1
      Expect.isNone (AppStateCustom.tryGetFeature<OpenedFiles> "k" s2) "removed key should be absent"

    testCase "remove on absent key is a no-op" <| fun _ ->
      let state = makeState Map.empty
      let s2 = AppStateCustom.remove "nonexistent" state
      Expect.equal s2.Custom (Map.empty<string, obj>) "map should be unchanged"

    testCase "multiple keys coexist" <| fun _ ->
      let state = makeState Map.empty
      let s =
        state
        |> AppStateCustom.set "sources" (mkFiles ["a.fs"])
        |> AppStateCustom.set "tests" (mkFiles ["b.fs"])
      let srcResult = AppStateCustom.tryGetFeature<OpenedFiles> "sources" s |> Option.map (fun s -> s.Files)
      let tstResult = AppStateCustom.tryGetFeature<OpenedFiles> "tests" s |> Option.map (fun s -> s.Files)
      Expect.equal srcResult (Some (Set.singleton "a.fs")) "sources key preserved"
      Expect.equal tstResult (Some (Set.singleton "b.fs")) "tests key preserved"

    testCase "set preserves other keys" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set "a" (mkFiles ["a.fs"]) state
      let s2 = AppStateCustom.set "b" (mkFiles ["b.fs"]) s1
      let result = AppStateCustom.tryGetFeature<OpenedFiles> "a" s2 |> Option.map (fun s -> s.Files)
      Expect.equal result (Some (Set.singleton "a.fs")) "key a should be preserved"

    testCase "openedFiles key constant matches contract doc" <| fun _ ->
      Expect.equal openedFileKey "openedFiles" "key must match contract"

    testCase "feature record round-trips via tryGetFeature" <| fun _ ->
      let state = makeState Map.empty
      let files = mkFiles ["a.fs"]
      let updated = AppStateCustom.set openedFileKey files state
      let result = AppStateCustom.tryGetFeature<OpenedFiles> openedFileKey updated |> Option.map (fun s -> s.Files)
      Expect.equal result (Some (Set.singleton "a.fs")) "round-trips via typed accessor"

    testCase "tryGetFeature returns None for wrong type" <| fun _ ->
      let state = makeState Map.empty
      let files = mkFiles []
      let updated = AppStateCustom.set openedFileKey files state
      Expect.isNone
        (AppStateCustom.tryGetFeature<SageFs.Middleware.HotReloading.State> openedFileKey updated)
        "wrong type returns None"
  ]
