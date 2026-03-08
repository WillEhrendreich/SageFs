module SageFs.Tests.AppStateCustomTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState

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

    testCase "tryGet returns None when key absent" <| fun _ ->
      let state = makeState Map.empty
      AppStateCustom.tryGet<string> "missing" state
      |> Expect.isNone "absent key should be None"

    testCase "set then tryGet round-trips typed int value" <| fun _ ->
      let state = makeState Map.empty
      let updated = AppStateCustom.set<int> "myKey" 42 state
      AppStateCustom.tryGet<int> "myKey" updated
      |> Expect.equal "should round-trip" (Some 42)

    testCase "set then tryGet round-trips typed string value" <| fun _ ->
      let state = makeState Map.empty
      let updated = AppStateCustom.set<string> "strKey" "hello" state
      AppStateCustom.tryGet<string> "strKey" updated
      |> Expect.equal "should round-trip string" (Some "hello")

    testCase "set then tryGet round-trips typed Set value" <| fun _ ->
      let state = makeState Map.empty
      let files : Set<string> = Set.ofList ["a.fs"; "b.fs"]
      let updated = AppStateCustom.set<Set<string>> "files" files state
      AppStateCustom.tryGet<Set<string>> "files" updated
      |> Expect.equal "should round-trip set" (Some files)

    testCase "set overwrites previous value" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set<int> "k" 1 state
      let s2 = AppStateCustom.set<int> "k" 2 s1
      AppStateCustom.tryGet<int> "k" s2
      |> Expect.equal "should be updated value" (Some 2)

    testCase "remove eliminates key" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set<string> "k" "v" state
      let s2 = AppStateCustom.remove "k" s1
      AppStateCustom.tryGet<string> "k" s2
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
        |> AppStateCustom.set<int> "age" 42
        |> AppStateCustom.set<string> "name" "alice"
      AppStateCustom.tryGet<int> "age" s |> Expect.equal "age should be 42" (Some 42)
      AppStateCustom.tryGet<string> "name" s |> Expect.equal "name should be alice" (Some "alice")

    testCase "set preserves other keys" <| fun _ ->
      let state = makeState Map.empty
      let s1 = AppStateCustom.set<int> "a" 1 state
      let s2 = AppStateCustom.set<int> "b" 2 s1
      AppStateCustom.tryGet<int> "a" s2
      |> Expect.equal "key a should be preserved" (Some 1)

    testCase "openedFiles key constant matches contract doc" <| fun _ ->
      SageFs.Middleware.Directives.OpenDirective.openedFileKey
      |> Expect.equal "key must match contract" "openedFiles"
  ]
