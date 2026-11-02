module SageFs.Tests.McpMintRouteTests

/// What `POST /api/members/mint` decides before a server is involved: who may call it, what a refusal says, how
/// the body is read, and what the reply holds. The route itself runs against a real daemon in
/// CapabilityHttpIntegrationTests.

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Capability
open SageFs.EdgeCredential
open SageFs.McpMintRoute

let credentialOf (n: int) : string = OperatorCredential.ofEntropy (Array.create OperatorCredential.entropyBytes (byte n))
let memberTokenOf (n: int) : string = Token.ofEntropy (Array.create Token.entropyBytes (byte n))
let bearer (raw: string) : string option = Some(sprintf "Bearer %s" raw)

let ready : OperatorStore.OperatorAccess = OperatorStore.OperatorAccess.Ready(OperatorHash.ofCredential (credentialOf 1))

let refusedWith (verdict: Verdict) : int * EdgeBody =
  match verdict with
  | Verdict.Refused(status, body) -> status, body
  | Verdict.Admitted -> failtest "expected a refusal, and the route admitted the caller"

[<Tests>]
let tests =
  testList "McpMintRoute" [

    testList "who may mint" [
      test "WHY - the daemon's own operator credential mints" {
        verdictOf ready (bearer (credentialOf 1)) |> Expect.equal "admitted" Verdict.Admitted
      }

      test "WHY - no credential is a 401 that names the way to get one" {
        let status, body = verdictOf ready None |> refusedWith
        status |> Expect.equal "unauthorized" 401
        body.Case |> Expect.equal "named" "no_credential"
        body.SuggestedAction |> Expect.stringContains "says where the credential is" "operator.token"
      }

      test "WHY - a member token is a 403, whatever it can do elsewhere" {
        let status, body = verdictOf ready (bearer (memberTokenOf 1)) |> refusedWith
        status |> Expect.equal "forbidden" 403
        body.Case |> Expect.equal "named" "member_not_allowed"
      }

      test "WHY - another operator credential and a header that cannot be read are each a 403 with their own case" {
        let wrongStatus, wrong = verdictOf ready (bearer (credentialOf 2)) |> refusedWith
        wrongStatus |> Expect.equal "forbidden" 403
        wrong.Case |> Expect.equal "named" "operator_not_recognized"
        let junkStatus, junk = verdictOf ready (Some "Basic dXNlcjpwYXNz") |> refusedWith
        junkStatus |> Expect.equal "forbidden" 403
        junk.Case |> Expect.equal "named" "unreadable_not_bearer"
      }

      test "WHY - every refusal body fills the four fields the editors read, and none holds a credential" {
        let secret = credentialOf 1
        [ None; bearer (memberTokenOf 1); bearer (credentialOf 2); Some "Basic x"; Some "Bearer "; bearer "sfo_short" ]
        |> List.iter (fun header ->
          let _, body = verdictOf ready header |> refusedWith
          [ body.Error; body.Message; body.Case; body.SuggestedAction ]
          |> List.iter (fun field -> String.IsNullOrWhiteSpace field |> Expect.isFalse "every field says something")
          (body.Message + body.SuggestedAction).Contains secret |> Expect.isFalse "and none quotes the daemon's credential")
      }

      test "WHY - a daemon that could not make operator.token answers 503 with the fix, to every caller" {
        let unavailable = OperatorStore.OperatorAccess.Unavailable(OperatorFile.Refusal.ModeTooWide "644")
        for header in [ None; bearer (credentialOf 1); bearer (memberTokenOf 1) ] do
          let status, body = verdictOf unavailable header |> refusedWith
          status |> Expect.equal "unavailable" 503
          body.Message |> Expect.stringContains "names the mode" "644"
          body.Case |> Expect.equal "named" "operator_credential_unavailable"
      }
    ]

    testList "what is asked" [
      test "WHY - a request needs only a role, and the rest are optional" {
        match parseRequest """{"role":"Implementer"}""" with
        | Ok request -> request |> Expect.equal "the role and nothing else" (requestFor "Implementer")
        | Error err -> failtestf "should parse: %A" err
      }

      test "WHY - every field is read by its snake_case name" {
        match parseRequest """{"role":"Analysis","scope":"src/Foo/","ttl_minutes":30,"working_directory":"/repo","session_id":"abc"}""" with
        | Ok request ->
          request
          |> Expect.equal "all of it"
            { Role = "Analysis"; Scope = "src/Foo/"; TtlMinutes = 30; WorkingDirectory = "/repo"; SessionId = "abc" }
        | Error err -> failtestf "should parse: %A" err
      }

      test "WHY - a field that is null, of the wrong type or absent is read as not given, never as an exception" {
        match parseRequest """{"role":"Verifier","scope":null,"ttl_minutes":"soon","working_directory":7}""" with
        | Ok request -> request |> Expect.equal "only the role was readable" (requestFor "Verifier")
        | Error err -> failtestf "should parse: %A" err
      }

      test "WHY - a body that is not JSON, has no role, or is not an object is a parse refusal and not an exception" {
        for body in [ "not json"; "{}"; "[]"; ""; """{"role":"  "}""" ] do
          match parseRequest body with
          | Error(SageFsError.JsonParseError _) -> ()
          | other -> failtestf "%s should be a parse refusal, got %A" body other
      }
    ]

    testList "who minted" [
      test "WHY - a token minted by the operator records the operator as the minter, and is the same kind of token" {
        let now = DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc)
        let scope = ScopePrefix.tryParse "src/Foo/" |> Result.defaultWith (fun refusal -> failtestf "scope: %A" refusal)
        let requested = { Preset = RolePreset.Analysis; Scope = scope; Route = RouteBinding.BoundToSession "s1"; NotAfter = now.AddMinutes 30.0 }
        let hash = TokenHash.ofToken (memberTokenOf 3)
        match Capability.decide now CapabilityState.empty (CapabilityCommand.Mint(operatorMinter, requested, hash)) with
        | Ok(state, _) -> state.Records.[hash].MintedBy |> Expect.equal "the operator" MemberTable.MemberId.Operator
        | Error refusal -> failtestf "the operator should be able to mint: %A" refusal
      }

      test "WHY - the operator is bound by the same rules as a conductor: a token that widens nothing it holds, no unbound token" {
        let now = DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc)
        let requested = { Preset = RolePreset.Implementer; Scope = ScopePrefix.repoRoot; Route = RouteBinding.Unbound; NotAfter = now.AddMinutes 30.0 }
        match Capability.decide now CapabilityState.empty (CapabilityCommand.Mint(operatorMinter, requested, TokenHash.ofToken (memberTokenOf 4))) with
        | Error(CapabilityRefusal.Mint MintRefusal.UnboundNotMintable) -> ()
        | other -> failtestf "an unbound token is never minted, even for the operator: %A" other
      }
    ]
  ]
