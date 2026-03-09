module SageFs.Tests.StructAuditTests

open Expecto
open Expecto.Flip
open FsCheck
open System
open System.Runtime.InteropServices
open SageFs
open SageFs.Features.LiveTesting
open SageFs.WorkerProtocol

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 200 }

// ── Struct Layout Assertions ──
// These tests enforce that hot-path types are value types (structs),
// preventing unnecessary heap allocation and GC pressure.
// Synthesis item 2.3: "Verify that every single-case wrapper DU has [<Struct>]"

[<Tests>]
let structLayoutTests =
  testList "Struct audit" [

    testList "Single-case wrapper DUs must be value types" [
      test "TestId is a struct" {
        typeof<TestId>.IsValueType
        |> Expect.isTrue "TestId must be [<Struct>] — hot-path map key"
      }
      test "RunGeneration is a struct" {
        typeof<RunGeneration>.IsValueType
        |> Expect.isTrue "RunGeneration must be [<Struct>]"
      }
      test "SessionId is a struct" {
        typeof<SessionId>.IsValueType
        |> Expect.isTrue "SessionId must be [<Struct>]"
      }
    ]

    testList "Multi-case hot-path DUs should be value types" [
      test "TestRunPhase is a struct" {
        typeof<TestRunPhase>.IsValueType
        |> Expect.isTrue "TestRunPhase must be [<Struct>] — eval loop state machine"
      }
      test "ResultFreshness is a struct" {
        typeof<ResultFreshness>.IsValueType
        |> Expect.isTrue "ResultFreshness must be [<Struct>] — enum-like, no payload"
      }
    ]

    testList "Rendering types must be value types" [
      test "Cell is a struct" {
        typeof<Cell>.IsValueType
        |> Expect.isTrue "Cell must be [<Struct>] — rendering hot path"
      }
      test "Rect is a struct" {
        typeof<Rect>.IsValueType
        |> Expect.isTrue "Rect must be [<Struct>] — layout hot path"
      }
      test "LineAnnotation is a struct" {
        typeof<LineAnnotation>.IsValueType
        |> Expect.isTrue "LineAnnotation must be [<Struct>]"
      }
    ]

    testList "Cell layout is compact" [
      test "sizeof<Cell> is 16 bytes or less" {
        let size = sizeof<Cell>
        size <= 16
        |> Expect.isTrue (sprintf "Cell must fit in 16 bytes for cache efficiency, but was %d" size)
      }
      test "sizeof<Cell> is stable (regression gate)" {
        sizeof<Cell>
        |> Expect.equal "sizeof<Cell> must not grow" 16
      }
    ]

    testList "Struct DU property tests" [
      testPropertyWithConfig cfg "TestId roundtrips through value extraction" <|
        fun (NonEmptyString s) ->
          let id = TestId.TestId s
          let (TestId.TestId extracted) = id
          extracted = s

      testPropertyWithConfig cfg "RunGeneration zero/next is monotonic" <|
        fun (NonNegativeInt n) ->
          let gen = RunGeneration (n % 10000)
          let next = RunGeneration.next gen
          RunGeneration.value next = RunGeneration.value gen + 1

      testPropertyWithConfig cfg "TestRunPhase Idle pattern-matches correctly as struct" <|
        fun () ->
          match TestRunPhase.Idle with
          | Idle -> true
          | _ -> false
    ]
  ]
