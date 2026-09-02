module SageFs.Tests.SageFsErrorPropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Tests.SharedGenerators

let private pick gen = (Gen.sample 1 gen).[0]

// ── Generators ──

let private genSessionState =
  Gen.elements [
    SessionState.Uninitialized
    SessionState.WarmingUp
    SessionState.Ready
    SessionState.Evaluating
    SessionState.Faulted
  ]

let private genNonEmptyString =
  Gen.elements ['a'..'z']
  |> Gen.listOfLength 8
  |> Gen.map (fun cs -> String(cs |> List.toArray))

let private genStringList =
  genNonEmptyString
  |> Gen.listOfLength 3

let private genSageFsError =
  Gen.oneof [
    gen {
      let! tool = genNonEmptyString
      let! state = genSessionState
      let! tools = genStringList
      return SageFsError.ToolNotAvailable(tool, state, tools)
    }
    genNonEmptyString |> Gen.map SageFsError.SessionNotFound
    Gen.constant SageFsError.NoActiveSessions
    genStringList |> Gen.map SageFsError.AmbiguousSessions
    genNonEmptyString |> Gen.map SageFsError.SessionCreationFailed
    gen {
      let! id = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.SessionStopFailed(id, reason)
    }
    gen {
      let! id = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.SessionSwitchFailed(id, reason)
    }
    gen {
      let! id = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.WorkerCommunicationFailed(id, reason)
    }
    genNonEmptyString |> Gen.map SageFsError.WorkerSpawnFailed
    gen {
      let! id = genNonEmptyString
      let! op = genNonEmptyString
      let! sec = Gen.choose (1, 300) |> Gen.map float
      return SageFsError.WorkerTimeout(id, op, sec)
    }
    gen {
      let! id = genNonEmptyString
      let! endpoint = genNonEmptyString
      let! status = Gen.choose (400, 599)
      return SageFsError.WorkerHttpError(id, endpoint, status)
    }
    Gen.constant SageFsError.PipeClosed
    genNonEmptyString |> Gen.map SageFsError.EvalFailed
    genNonEmptyString |> Gen.map SageFsError.ResetFailed
    genNonEmptyString |> Gen.map SageFsError.HardResetFailed
    genNonEmptyString |> Gen.map SageFsError.ScriptLoadFailed
    genNonEmptyString |> Gen.map SageFsError.CheckFailed
    gen {
      let! id = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.CompletionFailed(id, reason)
    }
    genNonEmptyString |> Gen.map SageFsError.CancelFailed
    gen {
      let! name = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.WarmupOpenFailed(name, reason)
    }
    gen {
      let! id = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.WarmupContextFailed(id, reason)
    }
    gen {
      let! path = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.HotReloadFailed(path, reason)
    }
    gen {
      let! id = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.HotReloadStateError(id, reason)
    }
    gen {
      let! count = Gen.choose (1, 20)
      let! minutes = Gen.choose (1, 60) |> Gen.map float
      return SageFsError.RestartLimitExceeded(count, minutes)
    }
    genNonEmptyString |> Gen.map SageFsError.DaemonStartFailed
    Gen.constant SageFsError.DaemonNotRunning
    Gen.choose (1, 65535) |> Gen.map SageFsError.PortInUse
    genNonEmptyString |> Gen.map SageFsError.SseConnectionError
    gen {
      let! ctx = genNonEmptyString
      let! reason = genNonEmptyString
      return SageFsError.JsonParseError(ctx, reason)
    }
    Gen.constant (SageFsError.Unexpected(Exception "test"))
  ]

// ── Reflection helpers ──

let private errorDuType = typeof<SageFsError>

let private allDuCaseInfos =
  FSharpType.GetUnionCases(errorDuType)

/// Build a concrete instance for each DU case using default field values.
let private allDuCaseInstances =
  allDuCaseInfos
  |> Array.map (fun case ->
    let fields =
      case.GetFields()
      |> Array.map (fun f ->
        match f.PropertyType with
        | t when t = typeof<string> -> box "test"
        | t when t = typeof<int> -> box 42
        | t when t = typeof<float> -> box 1.0
        | t when t = typeof<exn> -> box (Exception "test")
        | t when t = typeof<SessionState> -> box SessionState.Ready
        | t when t = typeof<string list> -> box [ "a"; "b" ]
        | t -> failwithf "Unhandled field type %s in case %s" t.Name case.Name)
    FSharpValue.MakeUnion(case, fields) :?> SageFsError)

// ── Tests ──

[<Tests>]
let sageFsErrorPropertyTests =
  testList "SageFsError property tests" [

    // 1. describe is total — every DU case returns a non-empty string
    testCase "describe is total over all DU cases" <| fun _ ->
      allDuCaseInstances
      |> Array.iter (fun err ->
        let desc = SageFsError.describe err
        desc
        |> String.IsNullOrWhiteSpace
        |> Expect.isFalse (sprintf "describe should return non-empty for %A" err))

    // 2. describe never throws on random inputs
    testPropertyWithConfig propConfig "describe never throws" <|
      fun () ->
        let err = pick genSageFsError
        try
          SageFsError.describe err |> ignore
          true
        with _ ->
          false

    // 3. toLogLevel is total — every case returns a valid LogLevel
    testCase "toLogLevel is total over all DU cases" <| fun _ ->
      let validLevels =
        [ LogLevel.Critical; LogLevel.Error
          LogLevel.Warning; LogLevel.Information ]
        |> Set.ofList
      allDuCaseInstances
      |> Array.iter (fun err ->
        let level = SageFsError.toLogLevel err
        validLevels
        |> Set.contains level
        |> Expect.isTrue (sprintf "toLogLevel should return valid level for %A, got %A" err level))

    // 4. toHttpStatus returns codes in 400..599
    testPropertyWithConfig propConfig "toHttpStatus returns valid HTTP error codes (400-599)" <|
      fun () ->
        let err = pick genSageFsError
        let status = SageFsError.toHttpStatus err
        status >= 400 && status <= 599

    testCase "toHttpStatus is total over all DU cases" <| fun _ ->
      allDuCaseInstances
      |> Array.iter (fun err ->
        let status = SageFsError.toHttpStatus err
        status
        |> fun s -> s >= 400 && s <= 599
        |> Expect.isTrue (sprintf "toHttpStatus should be 400-599 for %A, got %d" err status))

    // 5. isClientError ↔ toHttpStatus consistency
    // Client errors are 400/404; infra errors (409) are 4xx but NOT client errors.
    testPropertyWithConfig propConfig "isClientError implies toHttpStatus is 4xx" <|
      fun () ->
        let err = pick genSageFsError
        let status = SageFsError.toHttpStatus err
        match SageFsError.isClientError err with
        | true -> status >= 400 && status <= 499
        | false -> true

    testPropertyWithConfig propConfig "4xx status implies isClientError or isInfraError" <|
      fun () ->
        let err = pick genSageFsError
        let status = SageFsError.toHttpStatus err
        match status >= 400 && status <= 499 with
        | true -> SageFsError.isClientError err || SageFsError.isInfraError err
        | false -> true

    // 6. isServerError ↔ toHttpStatus 500 consistency
    testPropertyWithConfig propConfig "isServerError is true iff toHttpStatus is 500" <|
      fun () ->
        let err = pick genSageFsError
        let status = SageFsError.toHttpStatus err
        SageFsError.isServerError err = (status = 500)

    // 7. isGatewayError ↔ toHttpStatus 502/504 consistency
    testPropertyWithConfig propConfig "isGatewayError is true iff toHttpStatus is 502 or 504" <|
      fun () ->
        let err = pick genSageFsError
        let status = SageFsError.toHttpStatus err
        SageFsError.isGatewayError err = (status = 502 || status = 504)

    // 8. Classification predicates are mutually exclusive and exhaustive
    testPropertyWithConfig propConfig "classification predicates are mutually exclusive" <|
      fun () ->
        let err = pick genSageFsError
        let flags = [
          SageFsError.isClientError err
          SageFsError.isServerError err
          SageFsError.isGatewayError err
          SageFsError.isInfraError err
        ]
        let trueCount = flags |> List.filter id |> List.length
        trueCount = 1

    testCase "every DU case belongs to exactly one classification" <| fun _ ->
      allDuCaseInstances
      |> Array.iter (fun err ->
        let flags = [
          "isClientError", SageFsError.isClientError err
          "isServerError", SageFsError.isServerError err
          "isGatewayError", SageFsError.isGatewayError err
          "isInfraError", SageFsError.isInfraError err
        ]
        let trueOnes = flags |> List.filter snd |> List.map fst
        trueOnes
        |> List.length
        |> Expect.equal
          (sprintf "expected exactly 1 classification for %A, got [%s]"
            err (trueOnes |> String.concat ", "))
          1)

    // 9. DU completeness guard — detect new cases
    testCase "SageFsError DU has exactly 31 cases" <| fun _ ->
      allDuCaseInfos
      |> Array.length
      |> Expect.equal
        "SageFsError case count changed — update generators and property tests"
        31

    // 10. Unexpected wraps exception message
    testPropertyWithConfig propConfig "Unexpected description contains exception message" <|
      fun () ->
        let msg = pick genNonEmptyString
        let err = SageFsError.Unexpected(Exception msg)
        let desc = SageFsError.describe err
        desc.Contains(msg)

    // ── isInfraError ↔ toHttpStatus 409 consistency ──
    testPropertyWithConfig propConfig "isInfraError is true iff toHttpStatus is 409" <|
      fun () ->
        let err = pick genSageFsError
        let status = SageFsError.toHttpStatus err
        SageFsError.isInfraError err = (status = 409)
  ]
