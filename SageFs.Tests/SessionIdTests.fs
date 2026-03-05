module SageFs.Tests.SessionIdTests

open Expecto
open Expecto.Flip
open SageFs.WorkerProtocol

[<Tests>]
let sessionIdTests = testList "SessionId" [

  testList "validate" [
    test "accepts valid 8-char hex" {
      SessionId.validate "a1b2c3d4"
      |> Expect.isOk "should accept valid hex"
    }

    test "accepts all-zero ID" {
      SessionId.validate "00000000"
      |> Expect.isOk "should accept all zeros"
    }

    test "accepts all-f ID" {
      SessionId.validate "ffffffff"
      |> Expect.isOk "should accept all f's"
    }

    test "rejects empty string" {
      SessionId.validate ""
      |> Expect.isError "should reject empty"
    }

    test "rejects null" {
      SessionId.validate null
      |> Expect.isError "should reject null"
    }

    test "rejects too short" {
      SessionId.validate "a1b2c3"
      |> Expect.isError "should reject 6-char"
    }

    test "rejects too long" {
      SessionId.validate "a1b2c3d4e5"
      |> Expect.isError "should reject 10-char"
    }

    test "rejects uppercase hex" {
      SessionId.validate "A1B2C3D4"
      |> Expect.isError "should reject uppercase"
    }

    test "rejects non-hex characters" {
      SessionId.validate "a1b2g3d4"
      |> Expect.isError "should reject non-hex"
    }

    test "rejects special characters" {
      SessionId.validate "a1b2-3d4"
      |> Expect.isError "should reject special chars"
    }

    test "valid ID roundtrips through Result" {
      let sid = "deadbeef"
      match SessionId.validate sid with
      | Ok validated -> validated |> Expect.equal "should roundtrip" sid
      | Error e -> failwith e
    }
  ]

  testList "property-based" [
    testProperty "valid hex strings of length 8 are accepted" <| fun (b1: byte) (b2: byte) (b3: byte) (b4: byte) ->
      let hex = sprintf "%02x%02x%02x%02x" b1 b2 b3 b4
      match SessionId.validate hex with
      | Ok _ -> true
      | Error _ -> false

    testProperty "random strings are mostly rejected" <| fun (s: string) ->
      match s with
      | null -> true // null is rejected, that's fine
      | s when s.Length <> 8 -> // wrong length should be rejected
        match SessionId.validate s with
        | Error _ -> true
        | Ok _ -> false // shouldn't accept wrong length
      | _ -> true // length-8 strings might or might not be valid hex
  ]
]
