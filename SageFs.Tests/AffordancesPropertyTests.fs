module SageFs.Tests.AffordancesPropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkerProtocol
open SageFs.Affordances
open SageFs.Tests.SharedGenerators

let private pick gen = (Gen.sample 1 gen).[0]

// ── Generators ──

let private genPositiveDuration =
  Gen.choose (1, 10_000_000)
  |> Gen.map (fun ticks -> TimeSpan.FromTicks(int64 ticks))

let private genDurationList =
  genPositiveDuration
  |> Gen.listOfLength 1
  |> Gen.bind (fun _ ->
    Gen.choose (1, 20)
    |> Gen.bind (fun n ->
      Gen.listOfLength n genPositiveDuration))

let private allSessionStates =
  [ Uninitialized; WarmingUp; Ready; Evaluating; Faulted ]

let private genSessionState =
  Gen.elements allSessionStates

// ── EvalStats property tests ──

let evalStatsPropertyTests =
  testList "EvalStats properties" [

    testCase "empty has zero count and zero durations" <| fun _ ->
      let e = EvalStats.empty
      e.EvalCount |> Expect.equal "count" 0
      e.TotalDuration |> Expect.equal "total" TimeSpan.Zero
      e.MinDuration |> Expect.equal "min" TimeSpan.Zero
      e.MaxDuration |> Expect.equal "max" TimeSpan.Zero

    testPropertyWithConfig propConfig "record increments EvalCount by 1 each time" <|
      fun (PositiveInt n) ->
        let n = min n 100
        let dur = TimeSpan.FromMilliseconds 10.0
        let stats =
          List.init n (fun _ -> dur)
          |> List.fold (fun s d -> EvalStats.record d s) EvalStats.empty
        stats.EvalCount = n

    testPropertyWithConfig propConfig "record with positive duration keeps MinDuration <= MaxDuration" <|
      fun (PositiveInt n) ->
        let n = min n 50
        let durations =
          Gen.listOfLength n genPositiveDuration |> pick
        let stats =
          durations
          |> List.fold (fun s d -> EvalStats.record d s) EvalStats.empty
        stats.MinDuration <= stats.MaxDuration

    testCase "averageDuration of empty is TimeSpan.Zero" <| fun _ ->
      EvalStats.empty
      |> EvalStats.averageDuration
      |> Expect.equal "avg of empty" TimeSpan.Zero

    testPropertyWithConfig propConfig "averageDuration after N records equals TotalDuration / N" <|
      fun (PositiveInt n) ->
        let n = min n 50
        let durations =
          Gen.listOfLength n genPositiveDuration |> pick
        let stats =
          durations
          |> List.fold (fun s d -> EvalStats.record d s) EvalStats.empty
        let expected =
          TimeSpan.FromTicks(stats.TotalDuration.Ticks / int64 stats.EvalCount)
        EvalStats.averageDuration stats = expected

    testPropertyWithConfig propConfig "recording same duration N times gives min=max=that duration" <|
      fun (PositiveInt n) ->
        let n = min n 100
        let dur = pick genPositiveDuration
        let stats =
          List.init n (fun _ -> dur)
          |> List.fold (fun s d -> EvalStats.record d s) EvalStats.empty
        stats.MinDuration = dur && stats.MaxDuration = dur

    testPropertyWithConfig propConfig "min <= average <= max for any sequence of records" <|
      fun (PositiveInt n) ->
        let n = min n 50
        let durations =
          Gen.listOfLength n genPositiveDuration |> pick
        let stats =
          durations
          |> List.fold (fun s d -> EvalStats.record d s) EvalStats.empty
        let avg = EvalStats.averageDuration stats
        stats.MinDuration <= avg && avg <= stats.MaxDuration
  ]

// ── Affordances property tests ──

let affordancesPropertyTests =
  testList "Affordances properties" [

    testPropertyWithConfig propConfig "availableTools returns non-empty list for every SessionState" <|
      fun () ->
        let state = pick genSessionState
        availableTools state |> List.isEmpty |> not

    testCase "availableTools for Ready is superset of Uninitialized tools" <| fun _ ->
      let readyTools = availableTools Ready |> Set.ofList
      let uninitTools = availableTools Uninitialized |> Set.ofList
      Set.isSubset uninitTools readyTools
      |> Expect.isTrue "Ready should contain all Uninitialized tools"

    testCase "get_fsi_status is available in every state" <| fun _ ->
      allSessionStates
      |> List.iter (fun state ->
        availableTools state
        |> List.contains "get_fsi_status"
        |> Expect.isTrue (sprintf "get_fsi_status should be available in %A" state))

    testCase "send_fsharp_code is only available in Ready state" <| fun _ ->
      allSessionStates
      |> List.iter (fun state ->
        let contains = availableTools state |> List.contains "send_fsharp_code"
        match state with
        | Ready ->
          contains |> Expect.isTrue "should be available in Ready"
        | _ ->
          contains |> Expect.isFalse (sprintf "should NOT be available in %A" state))

    testCase "cancel_eval is available in Ready and Evaluating" <| fun _ ->
      allSessionStates
      |> List.iter (fun state ->
        let contains = availableTools state |> List.contains "cancel_eval"
        match state with
        | Ready | Evaluating ->
          contains |> Expect.isTrue (sprintf "cancel_eval should be in %A" state)
        | _ ->
          contains |> Expect.isFalse (sprintf "cancel_eval should NOT be in %A" state))

    testPropertyWithConfig propConfig "checkToolAvailability returns Ok for listed tools" <|
      fun () ->
        let state = pick genSessionState
        let tools = availableTools state
        tools
        |> List.forall (fun tool ->
          checkToolAvailability state tool = Ok ())

    testPropertyWithConfig propConfig "checkToolAvailability returns Error for unlisted tools" <|
      fun () ->
        let state = pick genSessionState
        let tools = availableTools state |> Set.ofList
        let bogus = "totally_fake_tool_that_does_not_exist"
        match tools.Contains bogus with
        | true -> true
        | false ->
          match checkToolAvailability state bogus with
          | Error _ -> true
          | Ok _ -> false

    testPropertyWithConfig propConfig "all returned tool names are non-empty strings" <|
      fun () ->
        let state = pick genSessionState
        availableTools state
        |> List.forall (fun t -> String.IsNullOrWhiteSpace t |> not)

    testPropertyWithConfig propConfig "checkToolAvailability error includes tool name in message" <|
      fun () ->
        let state = pick genSessionState
        let bogus = "nonexistent_tool_xyz"
        match checkToolAvailability state bogus with
        | Error (SageFsError.ToolNotAvailable (name, _, _)) ->
          name = bogus
        | Error _ -> false
        | Ok _ -> true

    testCase "SessionState DU has exactly 5 cases" <| fun _ ->
      let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<SessionState>)
      cases.Length
      |> Expect.equal "SessionState should have 5 cases" 5
  ]

[<Tests>]
let allAffordancesPropertyTests =
  testList "Affordances property tests" [
    evalStatsPropertyTests
    affordancesPropertyTests
  ]
