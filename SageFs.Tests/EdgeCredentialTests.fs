module SageFs.Tests.EdgeCredentialTests

/// The pure core of the operator credential (SageFs.Core/EdgeCredential.fs): what a request's Authorization
/// header presents, which routes need the operator, and the named refusal when it is not enough. Slice 0 of
/// docs/plans/a-token-for-every-daemon-call.md.

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Capability
open SageFs.EdgeCredential

let private credentialOf (n: int) : string = OperatorCredential.ofEntropy (Array.create OperatorCredential.entropyBytes (byte n))
let private hashOfCredential (n: int) : OperatorHash = OperatorHash.ofCredential (credentialOf n)
let private memberTokenOf (n: int) : string = Token.ofEntropy (Array.create Token.entropyBytes (byte n))
let private bearer (raw: string) : string option = Some(sprintf "Bearer %s" raw)

/// The daemon's own credential in these tests is number 1.
let private expected : OperatorHash = hashOfCredential 1

let private presentedOf (header: string option) : Presented = Presented.ofAuthorization header

let private genPresented : Gen<Presented> =
  Gen.oneof [
    Gen.constant Presented.Nothing
    Gen.choose (0, 3) |> Gen.map (fun n -> Presented.Operator(hashOfCredential n))
    Gen.choose (0, 3) |> Gen.map (fun n -> Presented.Member(TokenHash.ofToken (memberTokenOf n)))
    Gen.elements [ Unreadability.NotBearer; Unreadability.EmptyCredential; Unreadability.UnknownPrefix; Unreadability.MalformedOperator ]
    |> Gen.map Presented.Unreadable ]

let private genAccess : Gen<EdgeAccess> = Gen.elements [ EdgeAccess.Open; EdgeAccess.OperatorOnly ]

[<Tests>]
let tests =
  testList "EdgeCredential" [

    testList "the credential" [
      test "WHY - a credential is the sfo_ prefix and 43 base64url characters, so a leaked one is recognizable" {
        let raw = credentialOf 7
        raw.StartsWith OperatorCredential.prefix |> Expect.isTrue "it starts with the operator prefix"
        raw.Length |> Expect.equal "prefix plus 43 characters" (OperatorCredential.prefix.Length + 43)
        OperatorCredential.isWellFormed raw |> Expect.isTrue "and is well formed"
      }

      test "WHY - a credential of the wrong length or alphabet is not well formed" {
        let good = credentialOf 7
        [ "", "empty"
          "sfo_short", "too short"
          good + "A", "too long"
          good.Substring(0, good.Length - 1) + "+", "a character outside base64url"
          "sfm_" + good.Substring 4, "the member prefix" ]
        |> List.iter (fun (raw, why) -> OperatorCredential.isWellFormed raw |> Expect.isFalse (sprintf "%s is refused" why))
      }

      test "WHY - the hash is stable and differs per credential" {
        hashOfCredential 1 |> Expect.equal "same credential, same hash" (hashOfCredential 1)
        hashOfCredential 1 |> Expect.notEqual "another credential, another hash" (hashOfCredential 2)
      }
    ]

    testList "what a request presents" [
      test "WHY - no Authorization header presents nothing" {
        presentedOf None |> Expect.equal "nothing" Presented.Nothing
      }

      test "WHY - a Bearer sfo_ credential presents the operator hash, and the scheme is not case sensitive" {
        presentedOf (bearer (credentialOf 1)) |> Expect.equal "operator" (Presented.Operator(hashOfCredential 1))
        presentedOf (Some(sprintf "bearer %s" (credentialOf 1))) |> Expect.equal "lower case scheme" (Presented.Operator(hashOfCredential 1))
      }

      test "WHY - a Bearer sfm_ token presents the member token hash" {
        let token = memberTokenOf 4
        presentedOf (bearer token) |> Expect.equal "member" (Presented.Member(TokenHash.ofToken token))
      }

      test "WHY - a header that cannot be read names why, and is never taken for no credential" {
        presentedOf (Some "Basic dXNlcjpwYXNz") |> Expect.equal "another scheme" (Presented.Unreadable Unreadability.NotBearer)
        presentedOf (Some "Bearer ") |> Expect.equal "blank credential" (Presented.Unreadable Unreadability.EmptyCredential)
        presentedOf (Some "Bearer something-else") |> Expect.equal "unknown prefix" (Presented.Unreadable Unreadability.UnknownPrefix)
        presentedOf (bearer "sfo_short") |> Expect.equal "malformed operator" (Presented.Unreadable Unreadability.MalformedOperator)
        presentedOf (bearer (String('x', CapabilityTransport.maxTokenLength + 1))) |> Expect.equal "absurdly long" (Presented.Unreadable Unreadability.UnknownPrefix)
      }
    ]

    testList "an operator-only route" [
      test "WHY - nothing presented is refused as no credential" {
        decide expected Presented.Nothing EdgeAccess.OperatorOnly |> Expect.equal "refused" (Error EdgeRefusal.NoCredential)
      }

      test "WHY - a member token is refused as not allowed, whatever its role" {
        decide expected (presentedOf (bearer (memberTokenOf 1))) EdgeAccess.OperatorOnly
        |> Expect.equal "refused" (Error EdgeRefusal.MemberNotAllowed)
      }

      test "WHY - another operator credential is refused as not recognized" {
        decide expected (presentedOf (bearer (credentialOf 2))) EdgeAccess.OperatorOnly
        |> Expect.equal "refused" (Error EdgeRefusal.OperatorNotRecognized)
      }

      test "WHY - an unreadable header is refused with its own reason" {
        decide expected (presentedOf (Some "Bearer nope")) EdgeAccess.OperatorOnly
        |> Expect.equal "refused" (Error(EdgeRefusal.UnreadableCredential Unreadability.UnknownPrefix))
      }

      test "WHY - the daemon's own credential is admitted as the operator" {
        decide expected (presentedOf (bearer (credentialOf 1))) EdgeAccess.OperatorOnly
        |> Expect.equal "admitted" (Ok Principal.Operator)
      }
    ]

    testList "an open route" [
      test "WHY - every presentation is admitted, and says who it was recognized as" {
        decide expected Presented.Nothing EdgeAccess.Open |> Expect.equal "nobody" (Ok Principal.Anonymous)
        decide expected (presentedOf (bearer (credentialOf 1))) EdgeAccess.Open |> Expect.equal "operator" (Ok Principal.Operator)
        decide expected (presentedOf (bearer (credentialOf 2))) EdgeAccess.Open |> Expect.equal "a wrong operator is nobody" (Ok Principal.Anonymous)
        decide expected (presentedOf (Some "Basic x")) EdgeAccess.Open |> Expect.equal "unreadable is nobody" (Ok Principal.Anonymous)
        let token = memberTokenOf 3
        decide expected (presentedOf (bearer token)) EdgeAccess.Open
        |> Expect.equal "a member token is that member" (Ok(Principal.Member(TokenHash.ofToken token)))
      }
    ]

    testList "properties" [
      testProperty "WHY - decide is total, and an operator-only route admits nothing but the daemon's own hash" <| fun () ->
        Prop.forAll (Arb.fromGen genPresented) (fun presented ->
          match decide expected presented EdgeAccess.OperatorOnly with
          | Ok Principal.Operator -> presented = Presented.Operator expected
          | Ok _ -> false
          | Error _ -> presented <> Presented.Operator expected)

      testProperty "WHY - an open route admits every presentation" <| fun () ->
        Prop.forAll (Arb.fromGen genPresented) (fun presented ->
          match decide expected presented EdgeAccess.Open with
          | Ok _ -> true
          | Error _ -> false)

      testProperty "WHY - the same presentation and access always decide the same" <| fun () ->
        Prop.forAll (Arb.fromGen (Gen.zip genPresented genAccess)) (fun (presented, access) ->
          decide expected presented access = decide expected presented access)
    ]

    testList "the refusal on the wire" [
      test "WHY - nothing presented is a 401 and every other refusal is a 403, so a launcher's helper can refresh" {
        EdgeRefusal.statusOf EdgeRefusal.NoCredential |> Expect.equal "missing is unauthorized" EdgeStatus.Unauthorized
        [ EdgeRefusal.MemberNotAllowed
          EdgeRefusal.OperatorNotRecognized
          EdgeRefusal.UnreadableCredential Unreadability.NotBearer ]
        |> List.iter (fun refusal -> EdgeRefusal.statusOf refusal |> Expect.equal "presented but unusable is forbidden" EdgeStatus.Forbidden)
      }

      test "WHY - each refusal has its own case name, a message and a suggested action" {
        let all =
          [ EdgeRefusal.NoCredential
            EdgeRefusal.MemberNotAllowed
            EdgeRefusal.OperatorNotRecognized
            EdgeRefusal.UnreadableCredential Unreadability.NotBearer
            EdgeRefusal.UnreadableCredential Unreadability.EmptyCredential
            EdgeRefusal.UnreadableCredential Unreadability.UnknownPrefix
            EdgeRefusal.UnreadableCredential Unreadability.MalformedOperator ]
        all |> List.map EdgeRefusal.caseName |> List.distinct |> List.length
        |> Expect.equal "no two refusals share a case name" all.Length
        all |> List.iter (fun refusal ->
          String.IsNullOrWhiteSpace(EdgeRefusal.message refusal) |> Expect.isFalse "it says what happened"
          String.IsNullOrWhiteSpace(EdgeRefusal.suggestedAction refusal) |> Expect.isFalse "and what to do")
      }
    ]
  ]
