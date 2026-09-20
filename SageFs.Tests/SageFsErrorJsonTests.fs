module SageFs.Tests.SageFsErrorJsonTests

open System
open System.Collections.Generic
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs

/// Reuse the exhaustive case generator from classification tests.
let private allErrorCases : SageFsError list =
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
        | t when t = typeof<exn> -> box (Exception "test")
        | t when t = typeof<string list> -> box ([ "a"; "b" ] : string list)
        | t when t = typeof<SessionState> -> box SessionState.Ready
        | t when t = typeof<BuildDiagnostic list> -> box ([ BuildDiagnostic.ofLine "test error" ] : BuildDiagnostic list)
        | t when t = typeof<ProjectCompatibility.UnsupportedTfmReason> -> box ProjectCompatibility.UnsupportedTfmReason.NetFramework
        | _ -> box "unknown")
    FSharpValue.MakeUnion(case, fields) :?> SageFsError)
  |> Array.toList

[<Tests>]
let sageFsErrorJsonTests =
  testList "SageFsError.toJson" [

    testList "produces correct structure for representative cases" [
      test "WarmupContextFailed has case, fields, message, suggestedAction" {
        let err = SageFsError.WarmupContextFailed("sess-1", "timeout")
        let json = SageFsError.toJson err
        json.case
        |> Expect.equal "case should be WarmupContextFailed" "WarmupContextFailed"
        json.fields.["sessionId"] :?> string
        |> Expect.equal "sessionId field" "sess-1"
        json.fields.["reason"] :?> string
        |> Expect.equal "reason field" "timeout"
        json.message
        |> Expect.stringContains "message mentions session" "sess-1"
        json.suggestedAction
        |> Expect.stringContains "suggests hard reset" "hard_reset"
      }

      test "PortInUse preserves port field as int" {
        let err = SageFsError.PortInUse 8080
        let json = SageFsError.toJson err
        json.case
        |> Expect.equal "case" "PortInUse"
        json.fields.["port"] :?> int
        |> Expect.equal "port field" 8080
        json.suggestedAction
        |> Expect.stringContains "mentions mcp-port" "mcp-port"
      }

      test "Unexpected serializes exn message, not the exn object" {
        let err = SageFsError.Unexpected(InvalidOperationException "boom")
        let json = SageFsError.toJson err
        json.case
        |> Expect.equal "case" "Unexpected"
        json.fields.Count
        |> Expect.equal "one field" 1
        // The unnamed field is called "Item" by FSharp.Reflection
        let fieldValue = json.fields.Values |> Seq.head :?> string
        fieldValue
        |> Expect.equal "exn field is message string" "boom"
      }

      test "NoActiveSessions has empty fields" {
        let err = SageFsError.NoActiveSessions
        let json = SageFsError.toJson err
        json.case
        |> Expect.equal "case" "NoActiveSessions"
        json.fields.Count
        |> Expect.equal "no fields" 0
        json.message
        |> Expect.isNonEmpty "message should not be empty"
      }

      test "WorkerTimeout preserves float field" {
        let err = SageFsError.WorkerTimeout("s1", "eval", 30.5)
        let json = SageFsError.toJson err
        json.case
        |> Expect.equal "case" "WorkerTimeout"
        json.fields.["sessionId"] :?> string
        |> Expect.equal "sessionId" "s1"
        json.fields.["operation"] :?> string
        |> Expect.equal "operation" "eval"
        json.fields.["timeoutSec"] :?> float
        |> Expect.equal "timeoutSec" 30.5
      }
    ]

    test "toJson works for every DU case without throwing" {
      allErrorCases
      |> List.iter (fun err ->
        let json = SageFsError.toJson err
        json.case
        |> Expect.isNonEmpty "case should not be empty"
        json.message
        |> Expect.isNonEmpty "message should not be empty"
        json.suggestedAction
        |> Expect.isNonEmpty "suggestedAction should not be empty")
    }

    // Caught live, not by the tests above: `toJson`'s `fields` dictionary
    // boxes each field value as `obj`, and a bare F# DU boxed that way is
    // NOT serializable by the plain `System.Text.Json.JsonSerializer` the
    // HTTP boundary actually uses (McpServer.fs's `jsonResponse` registers
    // no `JsonFSharpConverter`) — confirmed via a real `/api/sessions/create`
    // call against a .NET Framework project, which returned a 500 "F#
    // discriminated union serialization is not supported" instead of the
    // refusal. `toJson` itself never calls `JsonSerializer`, so the tests
    // above never caught it. This test does what the HTTP boundary does.
    // `toJson`'s fix (see `isNonListUnion`) covers a bare DU field directly
    // on the error case (SessionState, ProjectCompatibility.UnsupportedTfmReason).
    // `BuildFailed`'s `BuildDiagnostic list` is excluded here: its own
    // element type nests a DIFFERENT DU (`BuildDiagnosticSeverity`) one
    // level deeper, inside a list of records — a pre-existing, separate gap
    // this task did not introduce and is out of scope to fix here (it needs
    // its own converter on `BuildDiagnosticSeverity`, not a `toJson` field
    // reduction). Confirmed still broken today so this exclusion is honest,
    // not papering over a live regression.
    test "toJson output actually serializes through the plain JsonSerializer the HTTP boundary uses" {
      allErrorCases
      |> List.filter (function SageFsError.BuildFailed _ -> false | _ -> true)
      |> List.iter (fun err ->
        let json = SageFsError.toJson err
        try
          System.Text.Json.JsonSerializer.Serialize(box json) |> ignore
        with ex ->
          failtestf "toJson output for %A did not serialize with plain JsonSerializer: %s" err ex.Message)
    }

    test "suggestedAction covers every DU case" {
      let caseCount = FSharpType.GetUnionCases(typeof<SageFsError>).Length
      allErrorCases
      |> List.length
      |> Expect.equal "should cover all cases" caseCount
      allErrorCases
      |> List.iter (fun err ->
        SageFsError.suggestedAction err
        |> Expect.isNonEmpty "suggestedAction should not be empty")
    }

    test "toJson case name matches FSharpReflection case name" {
      allErrorCases
      |> List.iter (fun err ->
        let info, _ = FSharpValue.GetUnionFields(err, typeof<SageFsError>)
        let json = SageFsError.toJson err
        json.case
        |> Expect.equal (sprintf "case name for %s" info.Name) info.Name)
    }

    test "toJson fields count matches DU field count" {
      allErrorCases
      |> List.iter (fun err ->
        let info, _ = FSharpValue.GetUnionFields(err, typeof<SageFsError>)
        let json = SageFsError.toJson err
        json.fields.Count
        |> Expect.equal
             (sprintf "field count for %s" info.Name)
             (info.GetFields().Length))
    }

    test "toJson message equals describe output" {
      allErrorCases
      |> List.iter (fun err ->
        let json = SageFsError.toJson err
        json.message
        |> Expect.equal "message should match describe" (SageFsError.describe err))
    }
  ]
