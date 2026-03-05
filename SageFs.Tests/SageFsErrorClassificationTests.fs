module SageFs.Tests.SageFsErrorClassificationTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open SageFs

/// Generate representative instances of every SageFsError case.
/// Uses FSharp.Reflection to ensure exhaustiveness — if a case is added,
/// this generator fails loudly rather than silently missing it.
let allErrorCases : SageFsError list =
  let cases = FSharpType.GetUnionCases(typeof<SageFsError>)
  cases
  |> Array.map (fun case ->
    let fields =
      case.GetFields()
      |> Array.map (fun f ->
        match f.PropertyType with
        | t when t = typeof<string> -> box "test"
        | t when t = typeof<int> -> box 42
        | t when t = typeof<float> -> box 1.0
        | t when t = typeof<exn> -> box (System.Exception "test")
        | t when t = typeof<string list> -> box (["a"; "b"] : string list)
        | t when t = typeof<SessionState> -> box SessionState.Ready
        | _ -> box "unknown")
    FSharpValue.MakeUnion(case, fields) :?> SageFsError)
  |> Array.toList

[<Tests>]
let sageFsErrorClassificationTests =
  testList "SageFsError classification" [

    test "allErrorCases covers every DU case" {
      let caseCount = FSharpType.GetUnionCases(typeof<SageFsError>).Length
      allErrorCases
      |> List.length
      |> Expect.equal "should have one instance per case" caseCount
    }

    testList "isClientError" [
      test "every case is classified" {
        allErrorCases
        |> List.iter (fun e ->
          // Should not throw — all cases handled
          SageFsError.isClientError e |> ignore)
      }

      test "client errors map to 4xx HTTP status" {
        allErrorCases
        |> List.filter SageFsError.isClientError
        |> List.iter (fun e ->
          let status = SageFsError.toHttpStatus e
          (status, 400)
          |> Expect.isGreaterThanOrEqual
            (sprintf "client error %A should have status >= 400" e)
          (status, 500)
          |> Expect.isLessThan
            (sprintf "client error %A should have status < 500" e))
      }
    ]

    testList "isServerError" [
      test "every case is classified" {
        allErrorCases
        |> List.iter (fun e ->
          SageFsError.isServerError e |> ignore)
      }

      test "server errors map to 500 HTTP status" {
        allErrorCases
        |> List.filter SageFsError.isServerError
        |> List.iter (fun e ->
          SageFsError.toHttpStatus e
          |> Expect.equal
            (sprintf "server error %A should be 500" e) 500)
      }
    ]

    testList "isGatewayError" [
      test "every case is classified" {
        allErrorCases
        |> List.iter (fun e ->
          SageFsError.isGatewayError e |> ignore)
      }

      test "gateway errors map to 502/504 HTTP status" {
        allErrorCases
        |> List.filter SageFsError.isGatewayError
        |> List.iter (fun e ->
          let status = SageFsError.toHttpStatus e
          [502; 504]
          |> List.contains status
          |> Expect.isTrue
            (sprintf "gateway error %A should be 502 or 504, got %d" e status))
      }
    ]

    testList "isInfraError" [
      test "every case is classified" {
        allErrorCases
        |> List.iter (fun e ->
          SageFsError.isInfraError e |> ignore)
      }

      test "infra errors map to 409 HTTP status" {
        allErrorCases
        |> List.filter SageFsError.isInfraError
        |> List.iter (fun e ->
          SageFsError.toHttpStatus e
          |> Expect.equal
            (sprintf "infra error %A should be 409" e) 409)
      }
    ]

    testList "classification properties" [
      test "every case is in exactly one category" {
        allErrorCases
        |> List.iter (fun e ->
          let categories =
            [ SageFsError.isClientError e
              SageFsError.isServerError e
              SageFsError.isGatewayError e
              SageFsError.isInfraError e ]
            |> List.filter id
            |> List.length
          categories
          |> Expect.equal
            (sprintf "case %A should be in exactly 1 category" e) 1)
      }

      test "describe returns non-empty for all cases" {
        allErrorCases
        |> List.iter (fun e ->
          SageFsError.describe e
          |> String.length
          |> fun len ->
            (len, 0)
            |> Expect.isGreaterThan
              (sprintf "describe for %A should be non-empty" e))
      }

      test "toHttpStatus is valid HTTP code for all cases" {
        allErrorCases
        |> List.iter (fun e ->
          let status = SageFsError.toHttpStatus e
          (status, 100)
          |> Expect.isGreaterThanOrEqual
            (sprintf "status for %A should be >= 100" e)
          (status, 600)
          |> Expect.isLessThan
            (sprintf "status for %A should be < 600" e))
      }

      test "toLogLevel is valid for all cases" {
        allErrorCases
        |> List.iter (fun e ->
          let level = SageFsError.toLogLevel e
          // LogLevel.None = 6, should never be None
          (int level, int Microsoft.Extensions.Logging.LogLevel.None)
          |> Expect.isLessThan
            (sprintf "log level for %A should not be None" e))
      }
    ]
  ]
