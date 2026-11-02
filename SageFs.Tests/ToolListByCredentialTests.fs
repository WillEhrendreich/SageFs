module SageFs.Tests.ToolListByCredentialTests

/// #181. `tools/list` is a function of the credential the request presents and of nothing else: not the
/// connection, not the cohort, not the conductor seat. The MCP 2026-07-28 Tools page says the list "MUST NOT vary
/// per-connection or as a side effect of other requests on the connection", and "MAY vary by the authorization
/// presented on the request". Seat-based authority is judged when a tool is called.
///
/// `listedTools` takes no cohort at all, so a join cannot change its answer; the property below pins that it
/// answers the same every time it is asked, and the integration case in CapabilityHttpIntegrationTests
/// reads the list across a real `join_cohort`.

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Capability
open SageFs.EdgeCredential

let private now = DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc)

let private grantFor (preset: RolePreset) : Grant =
  { Preset = preset
    Scope = ScopePrefix.repoRoot
    Route = RouteBinding.BoundToSession "session-1"
    NotAfter = now.AddHours 1.0 }

let private registered : string list = Affordances.declaredGateTools

/// What a caller with no credential sees under `TokenRequired`: status, and the one tool a first member joins by.
let private statusAndJoin : string list =
  IdentityPolicy.bootstrapTool :: IdentityPolicy.tokenlessReadable
  |> List.filter (fun tool -> List.contains tool registered)
  |> List.sort

let private genListedFor : Gen<ListedFor> =
  Gen.elements RolePreset.all
  |> Gen.map (fun preset -> ListedFor.Member(grantFor preset))
  |> fun members -> Gen.oneof [ Gen.constant ListedFor.Operator; Gen.constant ListedFor.NoCredential; members ]

let private genPolicy : Gen<IdentityPolicy> = Gen.elements IdentityPolicy.all

let private credentialOf (n: int) : string = OperatorCredential.ofEntropy (Array.create OperatorCredential.entropyBytes (byte n))
let private expected : OperatorHash = OperatorHash.ofCredential (credentialOf 1)
let private bearer (raw: string) : string option = Some(sprintf "Bearer %s" raw)

[<Tests>]
let tests =
  testList "tools/list by credential (#181)" [

    testCase "WHY - a caller that presented nothing sees the same three tools under TokenRequired, whoever it is and whatever has been joined" <| fun () ->
      listedTools IdentityPolicy.TokenRequired ListedFor.NoCredential registered
      |> List.sort
      |> Expect.equal "status and the join tool, the three the issue measured" statusAndJoin
      statusAndJoin.Length |> Expect.equal "and that is three" 3

    testCase "WHY - a caller that presented nothing sees everything under ConnectionsAllowed, as before tokens existed" <| fun () ->
      listedTools IdentityPolicy.ConnectionsAllowed ListedFor.NoCredential registered
      |> Expect.equal "the default policy lists every registered tool" registered

    testCase "WHY - the operator credential lists every tool, mint_member included, under either policy" <| fun () ->
      for policy in IdentityPolicy.all do
        listedTools policy ListedFor.Operator registered
        |> Expect.equal (sprintf "the operator sees everything under %A" policy) registered
      listedTools IdentityPolicy.TokenRequired ListedFor.Operator registered
      |> List.contains "mint_member"
      |> Expect.isTrue "including the tool a first minter needs"

    testCase "WHY - a member token lists its role's tools under either policy, never the conductor's" <| fun () ->
      for policy in IdentityPolicy.all do
        for preset in RolePreset.all do
          let grant = grantFor preset
          listedTools policy (ListedFor.Member grant) registered
          |> Expect.equal (sprintf "%A under %A is what its role may call" preset policy) (visibleTools grant registered)
      listedTools IdentityPolicy.TokenRequired (ListedFor.Member(grantFor RolePreset.Implementer)) registered
      |> List.contains "mint_member"
      |> Expect.isFalse "an Implementer is never offered minting"

    testCase "WHY - the list is a function of what was presented: asked twice it answers the same, in the registered order" <| fun () ->
      let property (policy: IdentityPolicy) (listedFor: ListedFor) =
        let first = listedTools policy listedFor registered
        let second = listedTools policy listedFor registered
        let inRegisteredOrder = first = (registered |> List.filter (fun tool -> List.contains tool first))
        first = second && inRegisteredOrder
      let arbitrary =
        Gen.zip genPolicy genListedFor
        |> Arb.fromGen
      Prop.forAll arbitrary (fun (policy, listedFor) -> property policy listedFor)
      |> Check.QuickThrowOnFailure

    testList "which credential a request presented on its Authorization header" [

      testCase "WHY - nothing, or a header that is not an operator credential, presents no operator" <| fun () ->
        for header in
          [ None
            Some "Basic dXNlcjpwYXNz"
            Some "Bearer"
            bearer "some-other-systems-token"
            bearer (Token.ofEntropy (Array.create Token.entropyBytes 7uy)) ] do
          OperatorPresentation.ofAuthorization expected header
          |> Expect.equal (sprintf "%A is not for the operator door" header) OperatorPresentation.NotPresented

      testCase "WHY - the daemon's own credential is recognized" <| fun () ->
        OperatorPresentation.ofAuthorization expected (bearer (credentialOf 1))
        |> Expect.equal "the right credential" OperatorPresentation.Recognized

      testCase "WHY - an operator-shaped credential that is not the daemon's is refused with the reason, never downgraded to nothing" <| fun () ->
        OperatorPresentation.ofAuthorization expected (bearer (credentialOf 2))
        |> Expect.equal "another daemon's credential" (OperatorPresentation.Refused EdgeRefusal.OperatorNotRecognized)
        OperatorPresentation.ofAuthorization expected (bearer "sfo_too-short")
        |> Expect.equal "a cut-short credential" (OperatorPresentation.Refused(EdgeRefusal.UnreadableCredential Unreadability.MalformedOperator))

      testCase "WHY - the claim a request carries round-trips, so the list filter reads what the edge decided" <| fun () ->
        OperatorPresentation.claimOf OperatorPresentation.NotPresented
        |> Expect.isNone "nothing presented writes no claim"
        OperatorPresentation.claimOf OperatorPresentation.Recognized
        |> Expect.equal "a recognized credential writes the recognized claim" (Some OperatorClaim.Recognized)
        OperatorPresentation.claimOf (OperatorPresentation.Refused EdgeRefusal.OperatorNotRecognized)
        |> Expect.equal "a refused one writes the refused claim" (Some OperatorClaim.Refused)
        for claim in OperatorClaim.all do
          OperatorClaim.toValue claim
          |> OperatorClaim.tryOfValue
          |> Expect.equal (sprintf "%A survives the claim's text" claim) (Some claim)
        OperatorClaim.tryOfValue "anything-else"
        |> Expect.isNone "text no one wrote is no claim"
    ]
  ]
