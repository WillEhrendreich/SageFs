module SageFs.Tests.ClientKeyIdentityTests

open System
open System.Security.Claims
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs
open SageFs.MemberTable
open SageFs.McpTools

/// P13 (findings C3 and C5): the conductor's identity survives a bridge reconnect. The stdio bridge holds a random key for
/// the life of its process and sends it on every request; the daemon names the member after the key, so a re-initialize
/// (a new session id) and a daemon restart leave the same client the same member. The key is a bearer secret: only the
/// id derived from it travels past the HTTP edge.

let private fingerprintShape = Regex(@"^mcp:m-[0-9a-f]{16}$")

let private idOf (key: string) : MemberId =
  match ClientKey.toMemberId key with
  | Result.Ok who -> who
  | Result.Error refusal -> failtestf "expected the key to be accepted: %s" (ClientKey.describe refusal)

/// Bind an ambient identity for the length of `body`, exactly as the request filter does for one call.
let private asCall (sessionHandle: string option) (clientKey: MemberId option) (body: unit -> 'a) : 'a =
  let previousHandle = currentTransportSessionId.Value
  let previousKey = currentClientKeyMember.Value
  let previousCapability = currentCapability.Value
  currentTransportSessionId.Value <- sessionHandle
  currentClientKeyMember.Value <- clientKey
  currentCapability.Value <- None
  try
    body ()
  finally
    currentTransportSessionId.Value <- previousHandle
    currentClientKeyMember.Value <- previousKey
    currentCapability.Value <- previousCapability

let private t0 = DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)

[<Tests>]
let tests =
  testList "Client key identity (P13)" [

    testList "the key" [

      testCase "WHY - a generated key is 256 bits in base64url, 43 characters, and two are never alike" <| fun _ ->
        let keys = [ for _ in 1 .. 200 -> ClientKey.generate () ]
        keys |> List.forall (fun key -> key.Length = ClientKey.length) |> Expect.isTrue "every key is 43 characters"
        keys |> List.forall (fun key -> key |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')) |> Expect.isTrue "base64url, no padding"
        keys |> List.distinct |> List.length |> Expect.equal "no two alike" 200

      testCase "WHY - a generated key is accepted, and the same key is the same member every time" <| fun _ ->
        let key = ClientKey.generate ()
        idOf key |> Expect.equal "same key, same member" (idOf key)

      testCase "WHY - two keys are two members" <| fun _ ->
        idOf (ClientKey.generate ()) |> Expect.notEqual "different keys, different members" (idOf (ClientKey.generate ()))

      testCase "WHY - the member id has the shape of a connection's fingerprint and does not carry the key" <| fun _ ->
        let key = ClientKey.generate ()
        let shown = MemberId.display (idOf key)
        fingerprintShape.IsMatch shown |> Expect.isTrue (sprintf "'%s' is mcp:m- and sixteen hex digits" shown)
        shown.Contains key |> Expect.isFalse "the id must not carry the key"

      testCase "WHY - a key is not a session handle: the same text as a session id names a different member" <| fun _ ->
        let text = ClientKey.generate ()
        idOf text |> Expect.notEqual "the key's member is not the member of a session whose id is that text" (MemberId.ofConnectionHandle text)

      testCase "WHY - a key that cannot be one is refused with the reason, never taken as the session's own member" <| fun _ ->
        ClientKey.toMemberId "" |> Expect.equal "blank" (Result.Error ClientKeyRefusal.Blank)
        ClientKey.toMemberId "   " |> Expect.equal "whitespace" (Result.Error ClientKeyRefusal.Blank)
        ClientKey.toMemberId "short" |> Expect.equal "too short" (Result.Error(ClientKeyRefusal.WrongLength 5))
        ClientKey.toMemberId (String('a', ClientKey.length + 1)) |> Expect.equal "too long" (Result.Error(ClientKeyRefusal.WrongLength(ClientKey.length + 1)))
        ClientKey.toMemberId (String('a', ClientKey.length - 1) + "+") |> Expect.equal "outside base64url" (Result.Error ClientKeyRefusal.NotBase64Url)

      testCase "WHY - every refusal says something and never repeats what it was given" <| fun _ ->
        for refusal in [ ClientKeyRefusal.Blank; ClientKeyRefusal.WrongLength 5; ClientKeyRefusal.NotBase64Url ] do
          ClientKey.describe refusal |> Expect.isNotEmpty "a reason"
    ]

    testList "which name a call's member comes from" [

      testCase "WHY - a token outranks a client key, which outranks the session id, which outranks the agent name" <| fun _ ->
        let token = MemberId.Capability "abcdef0123456789"
        let key = idOf (ClientKey.generate ())
        let handle = "session-handle-00000000000000"
        for capability in [ Some token; None ] do
          for clientKey in [ Some key; None ] do
            for handleOpt in [ Some handle; Some ""; None ] do
              let expected =
                match capability, clientKey, handleOpt with
                | Some who, _, _ -> who
                | None, Some who, _ -> who
                | None, None, Some h when h <> "" -> MemberId.ofConnectionHandle h
                | None, None, _ -> MemberId.Minted "agent"
              ConnectionIdentity.resolve capability clientKey handleOpt "agent"
              |> Expect.equal (sprintf "capability %A key %A handle %A" capability clientKey handleOpt) expected

      testCase "WHY - the daemon's memberIdFor gives the same member for two session ids when the client key is the same" <| fun _ ->
        let key = idOf (ClientKey.generate ())
        let before = asCall (Some "session-one-000000000000000") (Some key) (fun () -> memberIdFor "conductor")
        let after = asCall (Some "session-two-000000000000000") (Some key) (fun () -> memberIdFor "conductor")
        after |> Expect.equal "same client, same member across a re-initialize" before
        after |> Expect.equal "and it is the member the key names" key

      testCase "WHY - without a client key two session ids are two members, as HTTP clients and sagefs call have always been" <| fun _ ->
        let before = asCall (Some "session-one-000000000000000") None (fun () -> memberIdFor "client")
        let after = asCall (Some "session-two-000000000000000") None (fun () -> memberIdFor "client")
        after |> Expect.notEqual "a new session is a new member" before

      testCase "WHY - the member id the daemon derives is the one that is printed, and it carries neither the key nor the session id" <| fun _ ->
        let key = ClientKey.generate ()
        let session = "session-handle-1234567890abcd"
        let shown = asCall (Some session) (Some(idOf key)) (fun () -> MemberId.display (memberIdFor "conductor"))
        shown.Contains key |> Expect.isFalse "no key"
        shown.Contains session |> Expect.isFalse "no session id"
    ]

    testList "the edge's claim" [

      testCase "WHY - the member a client key names travels in an authenticated identity and reads back whole" <| fun _ ->
        let who = idOf (ClientKey.generate ())
        let user = McpCapability.withClientKey who null
        McpCapability.clientKeyMemberOf user |> Expect.equal "the same member" (Some who)
        user.Identities |> Seq.forall (fun identity -> identity.IsAuthenticated) |> Expect.isTrue "authenticated, so the SDK carries it to the filters"

      testCase "WHY - a request with no client key reads as none" <| fun _ ->
        McpCapability.clientKeyMemberOf null |> Expect.isNone "no principal"
        McpCapability.clientKeyMemberOf (ClaimsPrincipal(ClaimsIdentity())) |> Expect.isNone "no claim"

      testCase "WHY - adding the client key leaves a member token's claim where it was" <| fun _ ->
        let who = idOf (ClientKey.generate ())
        let tokenUser = McpCapability.principalOfHeader "sfmt_some-token-value"
        let both = McpCapability.withClientKey who tokenUser
        McpCapability.clientKeyMemberOf both |> Expect.equal "the key's member" (Some who)
        match McpCapability.presentationFrom both null with
        | Capability.CapabilityTransport.Presentation.Presented _ -> ()
        | other -> failtestf "the token was lost: %A" other

      testCase "WHY - the claim holds the derived member id, never a key" <| fun _ ->
        let key = ClientKey.generate ()
        let user = McpCapability.withClientKey (idOf key) null
        let claims = user.Claims |> Seq.map (fun claim -> claim.Value) |> List.ofSeq
        claims |> List.exists (fun value -> value.Contains key) |> Expect.isFalse "no claim holds the key"
    ]

    testList "who is still there" [

      let window = Timeouts.agentActivityFresh
      let live key now state = MemberLiveness.isLive key now window state
      let memberA = MemberId.Minted "a"
      let memberB = MemberId.Minted "b"

      testCase "WHY - nobody is live before they were seen" <| fun _ ->
        live (MemberId.Mcp "m-0000000000000001") t0 MemberLiveness.empty |> Expect.isFalse "a stranger"

      testCase "WHY - a member with an open stream is live however long it has been quiet" <| fun _ ->
        let state = MemberLiveness.empty |> MemberLiveness.streamOpened memberA
        live memberA (t0 + Cohort.leaseWindow + Cohort.leaseWindow) state |> Expect.isTrue "two lease windows later, stream still open"

      testCase "WHY - once the stream closes the member is live only for the window after its last call" <| fun _ ->
        let state =
          MemberLiveness.empty
          |> MemberLiveness.streamOpened memberA
          |> MemberLiveness.callMade memberA t0
          |> MemberLiveness.streamClosed memberA
        live memberA (t0 + window - TestTimeouts.clockMargin) state |> Expect.isTrue "inside the window of the call"
        live memberA (t0 + window + TestTimeouts.clockMargin) state |> Expect.isFalse "past it"

      testCase "WHY - a bridge that reopens its stream before the old one is noticed dead is live until both are closed" <| fun _ ->
        let opened =
          MemberLiveness.empty |> MemberLiveness.streamOpened memberA |> MemberLiveness.streamOpened memberA |> MemberLiveness.streamClosed memberA
        live memberA (t0 + Cohort.leaseWindow) opened |> Expect.isTrue "one stream is still open"
        live memberA (t0 + Cohort.leaseWindow) (MemberLiveness.streamClosed memberA opened) |> Expect.isFalse "none is"

      testCase "WHY - a stream that closes more often than it opened never leaves a debt that hides the next one" <| fun _ ->
        let state =
          MemberLiveness.empty
          |> MemberLiveness.streamClosed memberA
          |> MemberLiveness.streamClosed memberA
          |> MemberLiveness.streamOpened memberA
        live memberA (t0 + Cohort.leaseWindow) state |> Expect.isTrue "the stream that is open counts"

      testCase "WHY - one member's stream says nothing about another" <| fun _ ->
        let state = MemberLiveness.empty |> MemberLiveness.streamOpened memberA
        live memberB t0 state |> Expect.isFalse "b has no stream"

      testCase "WHY - the key is the member id, so two ids that print the same are two members" <| fun _ ->
        // A raw connection handle wrapped by hand prints as its fingerprint, which is also what `ofConnectionHandle` builds.
        // Keyed by the printed text they were one member, and a stream opened under either kept the other live.
        let handle = "a-raw-handle-0000000000000000"
        let fingerprinted = MemberId.ofConnectionHandle handle
        let wrapped = MemberId.Mcp handle
        MemberId.display wrapped |> Expect.equal "they print the same" (MemberId.display fingerprinted)
        let state = MemberLiveness.empty |> MemberLiveness.streamOpened fingerprinted
        live fingerprinted t0 state |> Expect.isTrue "the member the stream was opened for"
        live wrapped t0 state |> Expect.isFalse "an id that only prints the same is not that member"

      testCase "WHY - a member that is not live is forgotten, so the table holds the last window and not every member ever seen" <| fun _ ->
        let state =
          MemberLiveness.empty
          |> MemberLiveness.callMade memberA t0
          |> MemberLiveness.callMade memberB (t0 + window)
        MemberLiveness.tracked state |> Expect.equal "both are held until someone prunes" 2
        let pruned = MemberLiveness.pruned (t0 + window + window) window state
        MemberLiveness.tracked pruned |> Expect.equal "the one silent for a whole window is gone, the one inside it stays" 1
        live memberB (t0 + window + window) pruned |> Expect.isTrue "the member still inside its window is still live"

      testCase "WHY - a member with an open stream is never pruned, however quiet it has been" <| fun _ ->
        let state = MemberLiveness.empty |> MemberLiveness.streamOpened memberA |> MemberLiveness.callMade memberA t0
        let later = t0 + Cohort.leaseWindow + Cohort.leaseWindow
        let pruned = MemberLiveness.pruned later window state
        live memberA later pruned |> Expect.isTrue "the stream is still open"

      testProperty "WHY - pruning never changes who is live, for any events and any moment" <| fun (events: (int * bool * int) list) (probe: int) ->
        // (member 0..2, true = stream open/close and false = a call, n): the same shape as the oracle property below.
        let members = [| memberA; memberB; MemberId.Minted "c" |]
        let clock (seconds: int) = t0.AddSeconds(float ((seconds % 600 + 600) % 600))
        let state =
          List.fold
            (fun state (who, isStream, n) ->
              let key = members.[((who % 3) + 3) % 3]
              match isStream, n % 2 = 0 with
              | true, false -> MemberLiveness.streamOpened key state
              | true, true -> MemberLiveness.streamClosed key state
              | false, _ -> MemberLiveness.callMade key (clock n) state)
            MemberLiveness.empty
            events
        let now = t0.AddSeconds(float ((probe % 900 + 900) % 900))
        let pruned = MemberLiveness.pruned now window state
        members |> Array.iter (fun key ->
          live key now pruned |> Expect.equal (sprintf "%A is live exactly as before" key) (live key now state))
        (MemberLiveness.tracked pruned, MemberLiveness.tracked state)
        |> Expect.isLessThanOrEqual "pruning never adds a record"

      testProperty "WHY - liveness is exactly an open stream or a call inside the window, over any order of events" <| fun (events: (bool * int) list) (probe: int) ->
        // `true` opens (n odd) or closes (n even) a stream; `false` is a call n seconds in. Oracle: a count and a latest time.
        let clock (seconds: int) = t0.AddSeconds(float ((seconds % 600 + 600) % 600))
        let mutable state = MemberLiveness.empty
        let mutable open' = 0
        let mutable lastCall = None
        for (isStream, n) in events do
          match isStream, n % 2 = 0 with
          | true, false ->
            state <- MemberLiveness.streamOpened memberA state
            open' <- open' + 1
          | true, true ->
            state <- MemberLiveness.streamClosed memberA state
            open' <- max 0 (open' - 1)
          | false, _ ->
            state <- MemberLiveness.callMade memberA (clock n) state
            lastCall <- Some(match lastCall with Some previous when previous > clock n -> previous | _ -> clock n)
        let now = t0.AddSeconds(float ((probe % 900 + 900) % 900))
        let expected =
          open' > 0 || (match lastCall with Some at -> now - at <= window | None -> false)
        live memberA now state |> Expect.equal "agrees with the oracle" expected
    ]
  ]
