module SageFs.Tests.SessionIdTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators

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
      | Ok validated ->
        let expected = match SessionId.validate sid with | Ok s -> s | Error e -> failwith e
        validated |> Expect.equal "should roundtrip" expected
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

    testPropertyWithConfig propConfig "newId always validates" <| fun () ->
      SessionId.newId ()
      |> SessionId.value
      |> SessionId.validate
      |> Expect.isOk "newId should always produce a valid ID"

    testPropertyWithConfig propConfig "value roundtrip" <| fun (b1: byte) (b2: byte) (b3: byte) (b4: byte) ->
      let hex = sprintf "%02x%02x%02x%02x" b1 b2 b3 b4
      match SessionId.validate hex with
      | Ok sid -> SessionId.value sid |> Expect.equal "value should extract original string" hex
      | Error e -> failwith e

    testPropertyWithConfig propConfig "toString matches value" <| fun (b1: byte) (b2: byte) (b3: byte) (b4: byte) ->
      let hex = sprintf "%02x%02x%02x%02x" b1 b2 b3 b4
      match SessionId.validate hex with
      | Ok sid -> sid.ToString() |> Expect.equal "ToString should match value" (SessionId.value sid)
      | Error e -> failwith e

    testPropertyWithConfig propConfig "comparison is ordinal string comparison" <| fun (b1: byte) (b2: byte) (b3: byte) (b4: byte) (c1: byte) (c2: byte) (c3: byte) (c4: byte) ->
      let hexA = sprintf "%02x%02x%02x%02x" b1 b2 b3 b4
      let hexB = sprintf "%02x%02x%02x%02x" c1 c2 c3 c4
      let sidA = testSessionId hexA
      let sidB = testSessionId hexB
      let expected = sign (String.Compare(hexA, hexB, StringComparison.Ordinal))
      compare sidA sidB |> sign |> Expect.equal "should compare like ordinal strings" expected

    testPropertyWithConfig propConfig "equality is value-based" <| fun (b1: byte) (b2: byte) (b3: byte) (b4: byte) ->
      let hex = sprintf "%02x%02x%02x%02x" b1 b2 b3 b4
      let sid1 = testSessionId hex
      let sid2 = testSessionId hex
      sid1 |> Expect.equal "same string should produce equal IDs" sid2
  ]

  test "newId produces unique IDs" {
    let ids = List.init 100 (fun _ -> SessionId.newId () |> SessionId.value)
    ids
    |> List.distinct
    |> List.length
    |> Expect.equal "100 IDs should all be distinct" 100
  }

  test "equality rejects different strings" {
    let a = testSessionId "00000001"
    let b = testSessionId "00000002"
    (a = b) |> Expect.isFalse "different strings should produce unequal IDs"
  }
]
