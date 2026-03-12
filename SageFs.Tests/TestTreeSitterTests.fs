module SageFs.Tests.TestTreeSitterTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let private sampleCode =
  String.concat "\n" [
    "[<Fact>]"
    "let sampleTest () = ()"
  ]

[<Tests>]
let tests =
  testList "TestTreeSitter" [
    test "availability is stable across calls" {
      let first = TestTreeSitter.availability ()
      let second = TestTreeSitter.availability ()
      second |> Expect.equal "availability should be stable once initialized" first
    }

    test "availability exposes concrete runtime details" {
      match TestTreeSitter.availability () with
      | TestTreeSitter.NativeAvailability.Available (runtimeId, libraryPath) ->
        String.IsNullOrWhiteSpace runtimeId |> Expect.isFalse "runtime id should be populated"
        String.IsNullOrWhiteSpace libraryPath |> Expect.isFalse "native library path should be populated"
        File.Exists libraryPath |> Expect.isTrue "native library path should exist"
      | TestTreeSitter.NativeAvailability.Degraded (runtimeId, reason, searchedPaths) ->
        String.IsNullOrWhiteSpace runtimeId |> Expect.isFalse "runtime id should be populated"
        String.IsNullOrWhiteSpace reason |> Expect.isFalse "degraded reason should be populated"
        List.isEmpty searchedPaths |> Expect.isFalse "searched paths should be captured"
        searchedPaths
        |> List.iter (fun path ->
          String.IsNullOrWhiteSpace path |> Expect.isFalse "searched paths should not contain blanks")
    }

    test "describeAvailability reflects native state" {
      let description = TestTreeSitter.describeAvailability ()
      match TestTreeSitter.availability () with
      | TestTreeSitter.NativeAvailability.Available (runtimeId, libraryPath) ->
        description |> Expect.stringContains "description should mention available state" "available"
        description |> Expect.stringContains "description should mention runtime id" runtimeId
        description |> Expect.stringContains "description should mention native library path" libraryPath
      | TestTreeSitter.NativeAvailability.Degraded (runtimeId, reason, searchedPaths) ->
        description |> Expect.stringContains "description should mention degraded state" "degraded"
        description |> Expect.stringContains "description should mention runtime id" runtimeId
        description |> Expect.stringContains "description should mention degraded reason" reason
        description |> Expect.stringContains "description should mention searched paths" (String.concat ", " searchedPaths)
    }

    test "isAvailable mirrors the richer availability state" {
      let expected =
        match TestTreeSitter.availability () with
        | TestTreeSitter.NativeAvailability.Available _ -> true
        | TestTreeSitter.NativeAvailability.Degraded _ -> false
      TestTreeSitter.isAvailable () |> Expect.equal "isAvailable should match availability" expected
    }

    test "discover returns empty for blank input" {
      [ null; ""; " \t\r\n " ]
      |> List.iter (fun code ->
        TestTreeSitter.discover "Blank.fs" code
        |> Expect.isEmpty "blank input should not produce locations")
    }

    test "discover preserves location shape under the reported availability state" {
      let locations = TestTreeSitter.discover "Sample.fs" sampleCode
      match TestTreeSitter.availability () with
      | TestTreeSitter.NativeAvailability.Available _ ->
        locations
        |> Array.forall (fun location -> location.FilePath = "Sample.fs")
        |> Expect.isTrue "available tree-sitter should preserve the provided file path"
        locations
        |> Array.forall (fun location ->
          not (String.IsNullOrWhiteSpace location.AttributeName)
          && not (String.IsNullOrWhiteSpace location.FunctionName))
        |> Expect.isTrue "available tree-sitter should only emit fully populated locations"
      | TestTreeSitter.NativeAvailability.Degraded _ ->
        locations |> Expect.isEmpty "degraded mode should return empty locations"
    }
  ]
