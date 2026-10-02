module SageFs.Tests.CapabilityWireTests

/// The capability core wired into the daemon: `mint_member` and `revoke_member`
/// (conductor-only), the token presented in a header or `_meta` and never an
/// argument, a token outranking the connection it arrives on, a role refusing
/// the tools it may not call and saying why, the explicit identity policy for a
/// connection with no token, and `tools/list` showing only what a caller can use.

open System
open System.Security.Claims
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Capability
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.McpTools
open SageFs.McpCapability
open SageFs.Tests.TestInfrastructure

let private now = DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc)
let private handleConductor = "tJj5NFu4OCqmI2WIWkiBFA"
let private handleWorker = "Y5FMEWuygbQVjRTam-Cmkg"

/// Run `body` as the connection that holds `handle`, optionally presenting a resolved token, the way the call filter binds both.
let private asCaller (handle: string) (capability: ResolvedCapability option) (body: unit -> Task<'a>) : Task<'a> =
  task {
    let previousHandle = currentTransportSessionId.Value
    let previousCapability = currentCapability.Value
    currentTransportSessionId.Value <- Some handle
    currentCapability.Value <- capability
    try
      return! body ()
    finally
      currentTransportSessionId.Value <- previousHandle
      currentCapability.Value <- previousCapability
  }

let private asConnection (handle: string) (body: unit -> Task<'a>) = asCaller handle None body

let private newStore () = CapabilityStore(IdentityPolicy.ConnectionsAllowed)

/// A daemon's cohort in memory: an owner, its ledger, and a context wired to it.
let private withCohort (body: McpContext -> Features.CohortOwner.Handle -> Features.CohortLedger.LedgerPort<MemberId> -> Task<unit>) : Task<unit> =
  task {
    let ledger = Features.CohortLedger.InMemory.create<MemberId> ()
    let mutable seed = 0
    use owner =
      Features.CohortOwner.start
        (Utils.Log.asILogger ())
        ledger
        (fun () -> now)
        (fun () -> seed <- seed + 1; BitConverter.GetBytes seed)
        (fun _ -> ([], [], [], 0L))
    do! body { sharedCtx () with CohortOwner = Some owner } owner ledger
  }

let private mintAs (ctx: McpContext) (store: CapabilityStore) (handle: string) (role: string) (scope: string) (ttl: int) =
  asConnection handle (fun () -> mintMember ctx store now "gateway" role scope ttl None)

let private okOrFail (result: Result<'a, SageFsError>) : 'a =
  match result with
  | Ok value -> value
  | Error err -> failtestf "expected Ok, got %s" (SageFsError.describeForAgent err)

let private errorText (result: Result<'a, SageFsError>) : string =
  match result with
  | Ok _ -> failtest "expected an error, got Ok"
  | Error err -> SageFsError.describeForAgent err

let private resolvedOf (store: CapabilityStore) (minted: MintedMember) : ResolvedCapability =
  match store.Present(now, TokenHash.ofToken minted.Token) with
  | Ok resolved -> resolved
  | Error refusal -> failtestf "the minted token should resolve: %A" refusal

let private joinedAsConductor (ctx: McpContext) =
  task {
    let! joined = asConnection handleConductor (fun () -> joinCohort ctx "gateway" "Implementer" None)
    joined |> okOrFail |> ignore
  }

let private memberIndex (frame: CohortFrame<MemberId>) (who: MemberId) =
  frame.MemberIds |> Array.findIndex ((=) who)

[<Tests>]
let transportTests =
  testList "Capability wire: how a token reaches the daemon" [

    testCase "WHY - the stdio bridge sends the token in the header when its environment names one" <| fun () ->
      let env = function "SAGEFS_MEMBER_TOKEN" -> "sfm_abc" | _ -> null
      CapabilityTransport.headerFromEnvironment env
      |> Expect.equal "name and value" (Some(CapabilityTransport.headerName, "sfm_abc"))
      CapabilityTransport.headerFromEnvironment (fun _ -> null) |> Expect.equal "unset" None
      CapabilityTransport.headerFromEnvironment (fun _ -> "   ") |> Expect.equal "blank" None
      CapabilityTransport.bridgeEnvVar |> Expect.equal "the variable the doc names" "SAGEFS_MEMBER_TOKEN"

    testCase "WHY - the identity policy comes from the environment: unset is the default, unreadable fails closed" <| fun () ->
      CapabilityTransport.policyFromEnvironment (fun _ -> null) |> Expect.equal "unset" IdentityPolicy.ConnectionsAllowed
      CapabilityTransport.policyFromEnvironment (fun _ -> "TokenRequired") |> Expect.equal "set" IdentityPolicy.TokenRequired
      CapabilityTransport.policyFromEnvironment (fun _ -> "ConnectionsAllowed") |> Expect.equal "explicit default" IdentityPolicy.ConnectionsAllowed
      CapabilityTransport.policyFromEnvironment (fun _ -> "tokn-required") |> Expect.equal "a typo must not open the door" IdentityPolicy.TokenRequired

    testCase "WHY - the HTTP edge keeps the token's hash, not the token, in the principal" <| fun () ->
      let token = "sfm_secret-token-value"
      let principal = principalOfHeader token
      let claims = principal.Claims |> Seq.map (fun c -> c.Type, c.Value) |> List.ofSeq
      claims |> Expect.equal "one claim: the hash" [ CapabilityTransport.hashClaimType, TokenHash.value (TokenHash.ofToken token) ]
      principal.Identity.IsAuthenticated |> Expect.isFalse "not an authenticated user, so the SDK's session-to-user binding is neither engaged nor broken"
      principal.FindFirst ClaimTypes.NameIdentifier |> isNull |> Expect.isTrue "and it names no user"

    testCase "WHY - a call presents the token from _meta before the header, and nothing is a plain connection" <| fun () ->
      let header = principalOfHeader "sfm_header-token"
      let meta (token: string) =
        let node = JsonObject()
        node[CapabilityTransport.metaKey] <- JsonValue.Create(token)
        node
      presentationFrom header null
      |> Expect.equal "the header alone" (CapabilityTransport.Presentation.Presented(TokenHash.ofToken "sfm_header-token"))
      presentationFrom header (meta "sfm_meta-token")
      |> Expect.equal "_meta is per call, so it wins" (CapabilityTransport.Presentation.Presented(TokenHash.ofToken "sfm_meta-token"))
      presentationFrom (ClaimsPrincipal()) null |> Expect.equal "nothing" CapabilityTransport.Presentation.NoToken
      presentationFrom (ClaimsPrincipal()) (JsonObject()) |> Expect.equal "empty _meta" CapabilityTransport.Presentation.NoToken
      (match presentationFrom (ClaimsPrincipal()) (meta "   ") with CapabilityTransport.Presentation.Unreadable _ -> true | _ -> false)
      |> Expect.isTrue "a blank token is a bad token, not no token"

    testTask "WHY - a presented token that is bad is an error, never a fall-back to the connection" {
      let store = newStore ()
      (presentToken store now CapabilityTransport.Presentation.NoToken) |> Expect.equal "no token, no capability" (Ok None)
      let unknown = CapabilityTransport.Presentation.Presented(TokenHash.ofToken "sfm_nobody-minted-this")
      match presentToken store now unknown with
      | Error err ->
        let text = SageFsError.describeForAgent err
        text |> Expect.stringContains "says what happened" "not known"
        text |> Expect.stringContains "and that a restart drops tokens" "restart"
      | Ok _ -> failtest "an unknown token must be refused"
      match presentToken store now (CapabilityTransport.Presentation.Unreadable "blank") with
      | Error _ -> ()
      | Ok _ -> failtest "an unreadable token must be refused"
    }
  ]

[<Tests>]
let identityTests =
  testList "Capability wire: a token outranks the connection" [

    testCase "WHY - the daemon starts with the identity policy that changes nothing" <| fun () ->
      newStore().Policy |> Expect.equal "default" IdentityPolicy.ConnectionsAllowed
      capabilityStore.Policy |> Expect.equal "the daemon's own store, with no environment set" IdentityPolicy.ConnectionsAllowed

    testTask "WHY - two sub-agents on one connection with two tokens are two members, and a token is not the connection" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        do! joinedAsConductor ctx
        let! first = mintAs ctx store handleConductor "Analysis" "src/Foo" 60
        let! second = mintAs ctx store handleConductor "Analysis" "src/Bar" 60
        let a = resolvedOf store (okOrFail first)
        let b = resolvedOf store (okOrFail second)
        let idOnSharedConnection (cap: ResolvedCapability) =
          currentTransportSessionId.Value <- Some handleWorker
          currentCapability.Value <- Some cap
          let who = memberIdFor "sub-agent"
          currentCapability.Value <- None
          currentTransportSessionId.Value <- None
          who
        idOnSharedConnection a |> Expect.equal "token A is cap:A" (CapabilityId.memberId a.Id)
        idOnSharedConnection b |> Expect.equal "token B is cap:B" (CapabilityId.memberId b.Id)
        (idOnSharedConnection a <> idOnSharedConnection b) |> Expect.isTrue "same connection, two members"
        currentTransportSessionId.Value <- Some handleWorker
        (memberIdFor "sub-agent") |> Expect.equal "with no token it is the connection" (MemberId.ofConnectionHandle handleWorker)
        currentTransportSessionId.Value <- None
      })
    }
  ]

[<Tests>]
let gateTests =
  testList "Capability wire: the gate" [

    testTask "WHY - an Analysis token is refused send_fsharp_code with the reason and what to do" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        do! joinedAsConductor ctx
        let! minted = mintAs ctx store handleConductor "Analysis" "src/Foo" 60
        let cap = resolvedOf store (okOrFail minted)
        let! verdict = asCaller handleWorker (Some cap) (fun () -> admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" None None "send_fsharp_code")
        match verdict with
        | Error message ->
          message |> Expect.stringContains "names the role" "Analysis"
          message |> Expect.stringContains "names the tool" "send_fsharp_code"
          message |> Expect.stringContains "says which role would do" "Implementer"
          message |> Expect.stringContains "says who to ask" "conductor"
        | Ok _ -> failtest "an Analysis token must not call send_fsharp_code"
        let! allowed = asCaller handleWorker (Some cap) (fun () -> admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" None None "get_daemon_status")
        allowed |> Result.isOk |> Expect.isTrue "but reading status is fine"
      })
    }

    testTask "WHY - a token acting as a member also meets the cohort's own role check, so an Observer cannot claim" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        do! joinedAsConductor ctx
        let! minted = mintAs ctx store handleConductor "Observer" "" 60
        let cap = resolvedOf store (okOrFail minted)
        let! verdict = asCaller handleWorker (Some cap) (fun () -> admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" None None "acquire_claim")
        verdict |> Result.isError |> Expect.isTrue "Observer has no CohortWork"
      })
    }

    testTask "WHY - when a token is required a plain connection may read status and nothing else, and the refusal says how to get one" {
      let store = CapabilityStore(IdentityPolicy.TokenRequired)
      do! withCohort (fun ctx _ _ -> task {
        do! joinedAsConductor ctx
        let! status = asConnection handleWorker (fun () -> admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" None None "get_cohort_status")
        status |> Result.isOk |> Expect.isTrue "status stays readable"
        let! refused = asConnection handleWorker (fun () -> admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" None None "send_fsharp_code")
        match refused with
        | Error message ->
          message |> Expect.stringContains "names the policy" "TokenRequired"
          message |> Expect.stringContains "names the tool" "send_fsharp_code"
          message |> Expect.stringContains "says to ask for a token" "mint_member"
          message |> Expect.stringContains "says where the token goes" CapabilityTransport.headerName
        | Ok _ -> failtest "a plain connection must be refused when a token is required"
        let! conductorOk = asConnection handleConductor (fun () -> admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" None None "get_daemon_status")
        conductorOk |> Result.isOk |> Expect.isTrue "the conductor on its own connection may act, so it can mint"
      })
    }

    testTask "WHY - with the default policy a plain connection is untouched" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        let! verdict = asConnection handleWorker (fun () -> admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" None None "get_daemon_status")
        verdict |> Result.isOk |> Expect.isTrue "no change for a token-less caller"
      })
    }

    testCase "WHY - the cohort's refusal no longer tells an agent the conductor can delegate, because no tool does" <| fun () ->
      let text =
        CohortErrorMapping.toSageFsError (CohortError.NotConductor(MemberId.Minted "worker"))
        |> SageFsError.describeForAgent
      text |> Expect.stringContains "still says it is conductor-only" "conductor"
      text.Contains("delegate", StringComparison.OrdinalIgnoreCase) |> Expect.isFalse "there is no delegate tool, so the advice is false"

    testCase "WHY - tools/list shows each caller only what it can call" <| fun () ->
      let registered = Affordances.declaredGateTools
      let grant (preset: RolePreset) : ResolvedCapability =
        { Id = CapabilityId "0123456789abcdef"
          Hash = TokenHash.ofToken "sfm_x"
          Grant = { Preset = preset; Scope = ScopePrefix.repoRoot; NotAfter = now.AddHours 1.0 } }
      let visibleFor preset = visibleToolNames IdentityPolicy.ConnectionsAllowed Authority.Anonymous (Some(grant preset)) registered
      visibleFor RolePreset.Analysis |> List.contains "diagnose" |> Expect.isTrue "analysis stays"
      visibleFor RolePreset.Analysis |> List.contains "send_fsharp_code" |> Expect.isFalse "eval goes"
      visibleFor RolePreset.Implementer |> List.contains "send_fsharp_code" |> Expect.isTrue "an Implementer keeps eval"
      visibleFor RolePreset.Implementer |> List.contains "mint_member" |> Expect.isFalse "but never minting"
      visibleToolNames IdentityPolicy.ConnectionsAllowed Authority.Anonymous None registered
      |> Expect.equal "a plain connection under the default policy sees everything" registered
      visibleToolNames IdentityPolicy.TokenRequired Authority.Anonymous None registered
      |> Expect.equal "a plain connection when a token is required sees status only" (IdentityPolicy.tokenlessReadable |> List.filter (fun t -> List.contains t registered))
      visibleToolNames IdentityPolicy.TokenRequired (Authority.Conductor(MemberId.Minted "gateway")) None registered
      |> Expect.equal "the conductor sees everything" registered
  ]

[<Tests>]
let mintTests =
  testList "Capability wire: mint_member and revoke_member" [

    testTask "WHY - the conductor mints a token: shown once, the member seated, and no trace of it in the ledger" {
      let store = newStore ()
      do! withCohort (fun ctx owner ledger -> task {
        do! joinedAsConductor ctx
        let! result = mintAs ctx store handleConductor "Analysis" "src/Foo/" 60
        let minted = okOrFail result
        minted.Token |> Expect.stringStarts "a recognizable token" Token.prefix
        minted.Record.Grant.Preset |> Expect.equal "role" RolePreset.Analysis
        ScopePrefix.value minted.Record.Grant.Scope |> Expect.equal "canonical scope" "src/Foo"
        minted.Record.Grant.NotAfter |> Expect.equal "an hour from now" (now.AddHours 1.0)
        let frame = owner.ReadFrame()
        let who = CapabilityId.memberId minted.Record.Id
        let index = memberIndex frame who
        frame.MemberRole.[index] |> Expect.equal "seated as an Observer" JoinableRole.Observer
        frame.MemberSeat.[index] |> Expect.equal "present" SeatState.Present
        let ledgerText = Features.CohortLedgerExport.toJsonl (ledger.ReadAll())
        ledgerText.Contains minted.Token |> Expect.isFalse "the token is in no ledger row"
        ledgerText.Contains(minted.Token.Substring Token.prefix.Length) |> Expect.isFalse "nor the body of it"
        ledgerText |> Expect.stringContains "but the member is there, by its public id" (CapabilityId.value minted.Record.Id)
        let status = SageFs.McpCohortIntegration.getCohortStatus ctx |> fun t -> t.Result |> okOrFail
        status.Contains minted.Token |> Expect.isFalse "status never shows it"
        (describeMinted minted).Contains minted.Token |> Expect.isTrue "the conductor's own reply does"
        (describeMintedForLog minted).Contains minted.Token |> Expect.isFalse "the log's copy does not"
        let reply = describeMinted minted
        reply |> Expect.stringContains "says the header" CapabilityTransport.headerName
        reply |> Expect.stringContains "says the _meta key" CapabilityTransport.metaKey
        reply |> Expect.stringContains "warns off arguments" "never as a tool argument"
        reply |> Expect.stringContains "names the member" (MemberId.display who)
      })
    }

    testTask "WHY - only the conductor mints" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        do! joinedAsConductor ctx
        let! joined = asConnection handleWorker (fun () -> joinCohort ctx "worker" "Implementer" None)
        joined |> okOrFail |> ignore
        let! refused = mintAs ctx store handleWorker "Analysis" "" 60
        errorText refused |> Expect.stringContains "says why" "conductor"
        let! stranger = mintAs ctx store "a-handle-that-never-joined" "Analysis" "" 60
        errorText stranger |> Expect.stringContains "an outsider is refused too" "conductor"
        store.Records |> Expect.isEmpty "nothing was minted"
      })
    }

    testTask "WHY - a request that is wrong is refused with the reason, never adjusted" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        do! joinedAsConductor ctx
        let! badRole = mintAs ctx store handleConductor "admin" "" 60
        errorText badRole |> Expect.stringContains "no free-form role" "Analysis"
        let! badScope = mintAs ctx store handleConductor "Analysis" "../Other" 60
        errorText badScope |> Expect.stringContains "a scope that escapes the repo" "repo"
        let! negative = mintAs ctx store handleConductor "Analysis" "" -5
        errorText negative |> Expect.stringContains "a negative lifetime" "minutes"
        let tooLong = int Timeouts.capabilityMaxLifetime.TotalMinutes + 1
        let! past = mintAs ctx store handleConductor "Analysis" "" tooLong
        errorText past |> Expect.stringContains "past the maximum" "maximum"
        store.Records |> Expect.isEmpty "none of them minted anything"
        let! defaulted = mintAs ctx store handleConductor "Analysis" "" 0
        (okOrFail defaulted).Record.Grant.NotAfter |> Expect.equal "no ttl means the default lifetime" (now + Timeouts.capabilityDefaultLifetime)
      })
    }

    testTask "WHY - revoking stops the token at once, departs the member and orphans its claims" {
      let store = newStore ()
      do! withCohort (fun ctx owner _ -> task {
        do! joinedAsConductor ctx
        let! minted = mintAs ctx store handleConductor "Implementer" "src/Foo" 60
        let m = okOrFail minted
        let cap = resolvedOf store m
        let! claimed = asCaller handleWorker (Some cap) (fun () -> acquireClaim ctx "agent" "file:src/Foo/a.fs" "editing")
        claimed |> okOrFail |> ignore
        let who = CapabilityId.memberId m.Record.Id
        let! revoked = asConnection handleConductor (fun () -> revokeMember ctx store now "gateway" (MemberId.display who))
        revoked |> okOrFail |> ignore
        store.Present(now, TokenHash.ofToken m.Token) |> Expect.equal "the token is refused" (Error(PresentRefusal.Revoked now))
        let frame = owner.ReadFrame()
        (match frame.MemberSeat.[memberIndex frame who] with SeatState.Departed _ -> true | SeatState.Present -> false)
        |> Expect.isTrue "the seat is departed"
        frame.ClaimState |> Array.exists (function ClaimState.Orphaned _ -> true | _ -> false) |> Expect.isTrue "the member's claim is orphaned"
        let! again = asConnection handleConductor (fun () -> revokeMember ctx store now "gateway" (MemberId.display who))
        errorText again |> Expect.stringContains "twice says so" "already revoked"
      })
    }

    testTask "WHY - only the conductor revokes, and only a member that exists" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        do! joinedAsConductor ctx
        let! joined = asConnection handleWorker (fun () -> joinCohort ctx "worker" "Implementer" None)
        joined |> okOrFail |> ignore
        let! minted = mintAs ctx store handleConductor "Observer" "" 60
        let who = MemberId.display (CapabilityId.memberId (okOrFail minted).Record.Id)
        let! byMember = asConnection handleWorker (fun () -> revokeMember ctx store now "worker" who)
        errorText byMember |> Expect.stringContains "a member cannot" "conductor"
        let! unknown = asConnection handleConductor (fun () -> revokeMember ctx store now "gateway" "cap:0000000000000000")
        errorText unknown |> Expect.stringContains "unknown" "no member token"
        let! notACap = asConnection handleConductor (fun () -> revokeMember ctx store now "gateway" "mcp:m-1234567890abcdef")
        errorText notACap |> Expect.stringContains "a connection is not a token" "no member token"
      })
    }

    testTask "WHY - a token holder cannot become conductor by joining an empty cohort" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        let cap : ResolvedCapability =
          { Id = CapabilityId "feedfacefeedface"
            Hash = TokenHash.ofToken "sfm_x"
            Grant = { Preset = RolePreset.Implementer; Scope = ScopePrefix.repoRoot; NotAfter = now.AddHours 1.0 } }
        let! joined = asCaller handleWorker (Some cap) (fun () -> joinCohort ctx "agent" "Implementer" None)
        errorText joined |> Expect.stringContains "says the conductor must join first" "conductor"
      })
    }

    testTask "WHY - a token holder joins as its token's role, whatever role it asks for" {
      let store = newStore ()
      do! withCohort (fun ctx owner _ -> task {
        do! joinedAsConductor ctx
        let! minted = mintAs ctx store handleConductor "Analysis" "" 60
        let m = okOrFail minted
        let cap = resolvedOf store m
        let! left = asCaller handleWorker (Some cap) (fun () -> leaveCohort ctx "agent")
        // An Observer-role seat may not leave; if it could, the rejoin below is the check. Either way the seat's role is the token's.
        ignore left
        let! rejoined = asCaller handleWorker (Some cap) (fun () -> joinCohort ctx "agent" "Implementer" None)
        ignore rejoined
        let frame = owner.ReadFrame()
        frame.MemberRole.[memberIndex frame (CapabilityId.memberId m.Record.Id)] |> Expect.equal "still an Observer" JoinableRole.Observer
      })
    }
  ]

[<Tests>]
let claimTests =
  testList "Capability wire: claims are canonical and a token is confined to its scope" [

    testTask "WHY - a token minted for src/Foo/ cannot claim src/Bar/, with or without a .. detour" {
      let store = newStore ()
      do! withCohort (fun ctx owner _ -> task {
        do! joinedAsConductor ctx
        let! minted = mintAs ctx store handleConductor "Implementer" "src/Foo/" 60
        let cap = resolvedOf store (okOrFail minted)
        let claim (scope: string) = asCaller handleWorker (Some cap) (fun () -> acquireClaim ctx "agent" scope "editing")
        let! inside = claim "file:src/Foo/a.fs"
        okOrFail inside |> Expect.stringContains "inside" "Acquired claim"
        let! outside = claim "file:src/Bar/a.fs"
        errorText outside |> Expect.stringContains "names the prefix it is confined to" "src/Foo"
        let! detour = claim "file:src/Foo/../Bar/a.fs"
        errorText detour |> Expect.stringContains "the detour is src/Bar" "src/Foo"
        let! escape = claim "file:../a.fs"
        errorText escape |> Expect.stringContains "an escape is not a path" "repo"
        let! inner = claim "file:src/Foo/./Sub/../b.fs"
        inner |> okOrFail |> ignore
        owner.ReadFrame().ClaimScope |> Array.contains (ClaimScope.File "src/Foo/b.fs") |> Expect.isTrue "the claim is held under its canonical path"
      })
    }

    testTask "WHY - src/Foo/../Bar/x.fs and src/Bar/x.fs are one claim, so the second is a conflict, and nobody claims outside the repo" {
      let store = newStore ()
      do! withCohort (fun ctx _ _ -> task {
        do! joinedAsConductor ctx
        let! joined = asConnection handleWorker (fun () -> joinCohort ctx "worker" "Implementer" None)
        joined |> okOrFail |> ignore
        let! first = asConnection handleConductor (fun () -> acquireClaim ctx "gateway" "file:src/Foo/../Bar/x.fs" "editing")
        first |> okOrFail |> ignore
        let! second = asConnection handleWorker (fun () -> acquireClaim ctx "worker" "file:src/Bar/x.fs" "editing")
        errorText second |> Expect.stringContains "a conflict, naming the scope" "already claimed"
        let! escape = asConnection handleWorker (fun () -> acquireClaim ctx "worker" "file:../x.fs" "editing")
        errorText escape |> Expect.stringContains "refused for everyone" "repo"
        ignore store
      })
    }
  ]

[<Tests>]
let secretHandlingTests =
  testList "Capability wire: the token stays out of the log" [

    testTask "WHY - the tool wrapper returns the token to the conductor and logs only the redacted text" {
      let logged = ResizeArray<string>()
      let previousDebug = Utils.Log.logDebug
      let previousInfo = Utils.Log.logInfo
      Utils.Log.logDebug <- fun line -> lock logged (fun () -> logged.Add line)
      Utils.Log.logInfo <- fun line -> lock logged (fun () -> logged.Add line)
      try
        let token = "sfm_super-secret-token-value"
        let! shown = Server.McpTools.withEchoSecret (sharedCtx ()) "mint_member" (task { return (sprintf "TOKEN %s" token, "TOKEN (redacted)", None) })
        shown |> Expect.stringContains "the conductor gets the token" token
        let everything = lock logged (fun () -> String.concat "\n" logged)
        everything.Contains token |> Expect.isFalse "the daemon's log never holds it"
      finally
        Utils.Log.logDebug <- previousDebug
        Utils.Log.logInfo <- previousInfo
    }
  ]

[<Tests>]
let registrationTests =
  testList "Capability wire: the tools are registered like every other" [

    testCase "WHY - mint_member and revoke_member are declared in the gate table and registered, so discovery lists them" <| fun () ->
      let registered = Server.McpTools.RegisteredTools.describe typeof<Server.McpTools.SageFsTools> |> List.map (fun t -> t.Name)
      for tool in [ "mint_member"; "revoke_member" ] do
        Affordances.declaredGateTools |> List.contains tool |> Expect.isTrue (sprintf "%s is in the gate table" tool)
        registered |> List.contains tool |> Expect.isTrue (sprintf "%s is a registered tool" tool)

    testCase "WHY - every tool class names only tools that exist, and the classes name exactly the declared tools" <| fun () ->
      let classed = ToolClass.all |> List.collect ToolClass.toolsOf |> Set.ofList
      classed |> Expect.equal "the classes cover the gate table exactly" (Set.ofList Affordances.declaredGateTools)

    testCase "WHY - minting and revoking are conductor-only at the cohort gate" <| fun () ->
      let conductorOnly = [ "mint_member"; "revoke_member" ]
      let allowedFor authority = Affordances.CohortTool.all |> List.filter (fun t -> Affordances.checkCohortToolAllowed authority t) |> List.map Affordances.CohortTool.toToolName
      for tool in conductorOnly do
        Affordances.CohortTool.all |> List.map Affordances.CohortTool.toToolName |> List.contains tool |> Expect.isTrue (sprintf "%s is a cohort tool" tool)
        allowedFor (Authority.Conductor(MemberId.Minted "c")) |> List.contains tool |> Expect.isTrue (sprintf "the conductor may call %s" tool)
        allowedFor (Authority.Member(MemberId.Minted "m", JoinableRole.Implementer)) |> List.contains tool |> Expect.isFalse (sprintf "an Implementer may not call %s" tool)
        allowedFor Authority.Anonymous |> List.contains tool |> Expect.isFalse (sprintf "a stranger may not call %s" tool)
  ]
