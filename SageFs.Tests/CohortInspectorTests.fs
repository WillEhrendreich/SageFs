/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "the inspector"):
/// pure unit tests for `CohortInspector` — no daemon, no FSI, no I/O, the
/// same "pure core, no IO" discipline `CohortLanesTests.fs`/
/// `CohortTerritoryPanelTests.fs` use. A separate file from those so this
/// island's tests never collide with edits another agent makes to the
/// matrix/member/claim/lanes/territory sections.
module SageFs.Tests.CohortInspectorTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.WorkerProtocol
open SageFs.Server.CohortInspector

let private render (node: XmlNode) = renderNode node

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private atSec (n: int) : DateTime = epoch.AddSeconds(float n)
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

/// Folds `decide` over a fixed (clock, command) list from an empty state,
/// recording one dense `LedgerEntry` per accepted command — mirrors
/// `CohortLanesTests.fs`'s `ledgerFrom`, instantiated at `'m = MemberId`
/// (the type `DashboardInfra.ReadCohortLedger` actually carries).
let private ledgerFrom (commandsWithClocks: (DateTime * CohortCommand<MemberId>) list) : LedgerEntry<MemberId> list =
  let _, _, entriesRev =
    commandsWithClocks
    |> List.mapi (fun i (clock, cmd) -> i, clock, cmd)
    |> List.fold
      (fun (state, seq, entriesRev) (i, clock, cmd) ->
        match decide clock [| byte i |] state cmd with
        | Ok(newState, events, _) ->
          let seq' = seq + 1L<ledgerSeq>
          newState, seq', { Seq = seq'; Clock = clock; Entropy = [| byte i |]; Command = cmd; Events = events } :: entriesRev
        | Error err -> failwithf "unexpected refusal building test ledger: %A" err)
      (CohortState.empty (), 0L<ledgerSeq>, [])
  entriesRev |> List.rev

let private mintedClaimId (ledger: LedgerEntry<MemberId> list) : ClaimId =
  ledger
  |> List.pick (fun e ->
    e.Events
    |> List.tryPick (function
      | CohortEvent.ClaimAcquired(cid, _, _, _) -> Some cid
      | _ -> None))

let private mintedLandingId (ledger: LedgerEntry<MemberId> list) : LandingId =
  ledger
  |> List.pick (fun e ->
    e.Events
    |> List.tryPick (function
      | CohortEvent.LandingQueued(lid, _) -> Some lid
      | _ -> None))

let private frameOf (ledger: LedgerEntry<MemberId> list) (snapshots: SessionSnapshot<MemberId>[]) : CohortFrame<MemberId> =
  project (replayHead ledger) snapshots

let private emptyFrame : CohortFrame<MemberId> = project (replayHead []) [||]

let private testSession (id: string) (workingDir: string) : SessionInfo =
  { Id = SessionId.validate id |> Result.defaultWith (fun _ -> failwithf "bad test session id %s" id)
    Name = None
    Projects = [ "Foo.fsproj" ]
    WorkingDirectory = workingDir
    SolutionRoot = None
    CreatedAt = epoch
    LastActivity = epoch
    Status = SessionLifecycleStatus.Ready { Pid = 1234; Port = Some 5000 }
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning }

let private fieldValue (label: string) (fields: Field list) : string =
  fields
  |> List.tryFind (fun f -> f.Label = label)
  |> Option.map (fun f -> f.Value)
  |> Option.defaultWith (fun () -> failwithf "no field labeled %s in %A" label fields)

[<Tests>]
let cohortInspectorTests =
  testList "CohortInspector" [

    testList "EntityKind" [

      testCase "every kind round-trips through its URL segment" <| fun _ ->
        for kind in [ EntityKind.Member; EntityKind.Claim; EntityKind.Landing; EntityKind.Test; EntityKind.Session ] do
          EntityKind.toUrlSegment kind |> EntityKind.tryParse |> Expect.equal (sprintf "%A round-trips" kind) (Some kind)

      testCase "parsing is case-insensitive" <| fun _ ->
        EntityKind.tryParse "MEMBER" |> Expect.equal "uppercase parses" (Some EntityKind.Member)
        EntityKind.tryParse "Claim" |> Expect.equal "mixed case parses" (Some EntityKind.Claim)

      testCase "an unrecognized segment is None, not an exception" <| fun _ ->
        EntityKind.tryParse "bogus" |> Expect.equal "unknown segment" None
        EntityKind.tryParse "" |> Expect.equal "empty segment" None
    ]

    testList "inspect — member" [

      testCase "a joined member is found with role, presence, and conductor status" <| fun _ ->
        let ledger = ledgerFrom [ atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, Some "sess-1") ]
        match inspect EntityKind.Member "alice" emptyFrame ledger [] with
        | InspectorModel.Found(EntityKind.Member, "alice", _, fields) ->
          fields |> fieldValue "Role" |> Expect.equal "role" "Implementer"
          fields |> fieldValue "Presence" |> Expect.equal "presence" "Present"
          // alice is the cohort's first joiner — v1's create_cohort semantics
          // bind her as conductor (Cohort.fs's `decide`, `Join` case).
          fields |> fieldValue "Conductor" |> Expect.equal "first joiner is conductor" "yes"
          fields |> fieldValue "Session" |> Expect.equal "session" "sess-1"
        | other -> failwithf "expected Found for alice, got %A" other

      testCase "an unknown member id is a clean not-found" <| fun _ ->
        match inspect EntityKind.Member "ghost" emptyFrame [] [] with
        | InspectorModel.NotFound(Some EntityKind.Member, "ghost") -> ()
        | other -> failwithf "expected NotFound(Some Member, ghost), got %A" other

      testCase "a member's held claims and requested landings are cross-referenced" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
            atSec 2, CohortCommand.RequestLanding(alice, [], [ "deadbeef" ], "ship it")
          ]
        let claimId = mintedClaimId ledger
        let (ClaimId cidRaw) = claimId
        let landingId = mintedLandingId ledger
        let (LandingId lidRaw) = landingId
        match inspect EntityKind.Member "alice" emptyFrame ledger [] with
        | InspectorModel.Found(_, _, _, fields) ->
          fields |> fieldValue "Claims held" |> Expect.equal "claim cross-reference" cidRaw
          fields |> fieldValue "Landings requested" |> Expect.equal "landing cross-reference" lidRaw
        | other -> failwithf "expected Found, got %A" other
    ]

    testList "inspect — claim" [

      testCase "a held claim is found with scope, purpose, since, and holder" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing Foo")
          ]
        let (ClaimId cidRaw) = mintedClaimId ledger
        match inspect EntityKind.Claim cidRaw emptyFrame ledger [] with
        | InspectorModel.Found(EntityKind.Claim, id, _, fields) ->
          id |> Expect.equal "id echoes the requested id" cidRaw
          fields |> fieldValue "Scope" |> Expect.stringContains "scope carries the path" "src/Foo.fs"
          fields |> fieldValue "Purpose" |> Expect.equal "purpose is the raw Purpose value" "editing Foo"
          fields |> fieldValue "State" |> Expect.stringContains "state names the holder" "alice"
        | other -> failwithf "expected Found for the minted claim, got %A" other

      testCase "an unknown claim id is a clean not-found" <| fun _ ->
        match inspect EntityKind.Claim "c-nope" emptyFrame [] [] with
        | InspectorModel.NotFound(Some EntityKind.Claim, "c-nope") -> ()
        | other -> failwithf "expected NotFound(Some Claim, c-nope), got %A" other
    ]

    testList "inspect — landing" [

      testCase "a queued landing is found with requester, statement, and state" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.RequestLanding(alice, [], [ "deadbeef" ], "ship the fix")
          ]
        let (LandingId lidRaw) = mintedLandingId ledger
        match inspect EntityKind.Landing lidRaw emptyFrame ledger [] with
        | InspectorModel.Found(EntityKind.Landing, id, _, fields) ->
          id |> Expect.equal "id echoes the requested id" lidRaw
          fields |> fieldValue "Requester" |> Expect.equal "requester" "alice"
          fields |> fieldValue "Statement" |> Expect.equal "statement is the raw value" "ship the fix"
          fields |> fieldValue "Commits" |> Expect.equal "commits" "deadbeef"
          // Front of an otherwise-empty queue advances straight to Rebasing
          // (`Cohort.fs`'s `advanceQueue`) — landingStateLabel must render it.
          fields |> fieldValue "State" |> Expect.stringContains "state advanced past Queued" "Rebasing"
        | other -> failwithf "expected Found for the minted landing, got %A" other

      testCase "an unknown landing id is a clean not-found" <| fun _ ->
        match inspect EntityKind.Landing "l-nope" emptyFrame [] [] with
        | InspectorModel.NotFound(Some EntityKind.Landing, "l-nope") -> ()
        | other -> failwithf "expected NotFound(Some Landing, l-nope), got %A" other
    ]

    testList "inspect — test" [

      testCase "a reported test is found with pass/fail/stale popcounts, not per-session identity" <| fun _ ->
        let snapshots =
          [| { Member = Some alice; SessionId = "s1"; Generation = 1L; PassingTests = [ TestId "T1" ]; FailingTests = []; StaleTests = [] }
             { Member = Some bob; SessionId = "s2"; Generation = 1L; PassingTests = []; FailingTests = [ TestId "T1" ]; StaleTests = [] } |]
        let frame = frameOf [] snapshots
        match inspect EntityKind.Test "T1" frame [] [] with
        | InspectorModel.Found(EntityKind.Test, "T1", _, fields) ->
          fields |> fieldValue "Sessions reporting" |> Expect.equal "two sessions reported" "2"
          fields |> fieldValue "Passing" |> Expect.equal "one session passed" "1"
          fields |> fieldValue "Failing" |> Expect.equal "one session failed" "1"
          fields |> fieldValue "Stale" |> Expect.equal "none stale" "0"
        | other -> failwithf "expected Found for T1, got %A" other

      testCase "an unknown test id is a clean not-found" <| fun _ ->
        match inspect EntityKind.Test "T-nope" emptyFrame [] [] with
        | InspectorModel.NotFound(Some EntityKind.Test, "T-nope") -> ()
        | other -> failwithf "expected NotFound(Some Test, T-nope), got %A" other
    ]

    testList "inspect — session" [

      testCase "a listed session is found with its working directory and status" <| fun _ ->
        let session = testSession "aaaa0001" @"/tmp/proj"
        match inspect EntityKind.Session "aaaa0001" emptyFrame [] [ session ] with
        | InspectorModel.Found(EntityKind.Session, "aaaa0001", _, fields) ->
          fields |> fieldValue "Working directory" |> Expect.equal "working dir" @"/tmp/proj"
          fields |> fieldValue "Status" |> Expect.equal "status label" "Ready"
          fields |> fieldValue "Workflow" |> Expect.equal "workflow label" "Interactive"
        | other -> failwithf "expected Found for the listed session, got %A" other

      testCase "an unlisted session id is a clean not-found" <| fun _ ->
        match inspect EntityKind.Session "deadbeef" emptyFrame [] [] with
        | InspectorModel.NotFound(Some EntityKind.Session, "deadbeef") -> ()
        | other -> failwithf "expected NotFound(Some Session, deadbeef), got %A" other
    ]

    testList "inspectRaw" [

      testCase "an unrecognized kind segment is NotFound(None, id), never an exception" <| fun _ ->
        match inspectRaw "bogus" "x" emptyFrame [] [] with
        | InspectorModel.NotFound(None, "x") -> ()
        | other -> failwithf "expected NotFound(None, x), got %A" other

      testCase "a recognized kind segment delegates to inspect" <| fun _ ->
        let ledger = ledgerFrom [ atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
        match inspectRaw "member" "alice" emptyFrame ledger [] with
        | InspectorModel.Found(EntityKind.Member, "alice", _, _) -> ()
        | other -> failwithf "expected Found via inspectRaw, got %A" other
    ]

    testList "search" [

      testCase "an empty query returns no results, never the whole cohort" <| fun _ ->
        let ledger = ledgerFrom [ atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
        search "" emptyFrame ledger [] |> Expect.equal "blank query" []
        search "   " emptyFrame ledger [] |> Expect.equal "whitespace-only query" []

      testCase "a query matches members, claims, landings, tests, and sessions by substring" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/aliceFile.fs", "editing")
            atSec 2, CohortCommand.RequestLanding(alice, [], [ "deadbeef" ], "alice's landing")
          ]
        let snapshots = [| { Member = Some alice; SessionId = "s1"; Generation = 1L; PassingTests = [ TestId "alice-test" ]; FailingTests = []; StaleTests = [] } |]
        let frame = frameOf ledger snapshots
        let session = testSession "aaaa0001" @"/tmp/alice-checkout"
        let results = search "alice" frame ledger [ session ]
        let kinds = results |> List.map (fun r -> r.Kind) |> List.distinct |> List.sort
        kinds |> Expect.equal "matches across all five kinds" [ EntityKind.Member; EntityKind.Claim; EntityKind.Landing; EntityKind.Test; EntityKind.Session ]

      testCase "results are deterministically ordered by (kind, id)" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.Join(bob, JoinableRole.Verifier, None)
          ]
        let frame = frameOf ledger [||]
        let first = search "b" frame ledger []
        let second = search "b" frame ledger []
        first |> Expect.equal "same inputs, same order" second

      testCase "a non-matching query returns no results" <| fun _ ->
        let ledger = ledgerFrom [ atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
        search "zzz-nothing-matches-zzz" emptyFrame ledger [] |> Expect.equal "no matches" []
    ]

    testList "rendering" [

      testCase "renderInspector escapes an XSS payload in a claim's purpose, never emits it raw" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "<script>alert(1)</script>")
          ]
        let (ClaimId cidRaw) = mintedClaimId ledger
        let model = inspect EntityKind.Claim cidRaw emptyFrame ledger []
        let html = renderInspector model |> render
        (html.Contains "<script>alert(1)</script>") |> Expect.isFalse "the raw payload never appears unescaped"
        html |> Expect.stringContains "the payload is HTML-escaped" "&lt;script&gt;"

      testCase "renderInspector renders a found entity's fields as a definition list" <| fun _ ->
        let ledger = ledgerFrom [ atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
        let html = inspect EntityKind.Member "alice" emptyFrame ledger [] |> renderInspector |> render
        html |> Expect.stringContains "carries a <dl>" "<dl>"
        html |> Expect.stringContains "carries the member id" "alice"
        html |> Expect.stringContains "carries the automation hook" "inspector-found"

      testCase "renderInspector renders a clean not-found view for an unknown entity" <| fun _ ->
        let html = InspectorModel.NotFound(Some EntityKind.Member, "ghost") |> renderInspector |> render
        html |> Expect.stringContains "carries the automation hook" "inspector-not-found"
        html |> Expect.stringContains "names the missing id" "ghost"

      testCase "renderSearchResults links each result to its inspect route, URL-escaping the id" <| fun _ ->
        let results = [ { Kind = EntityKind.Member; Id = "browser:abc def"; Label = "browser:abc def" } ]
        let html = renderSearchResults "abc" results |> render
        html |> Expect.stringContains "links to the member inspect route" "/dashboard/inspect/member/"
        // The space in the id is URL-escaped in the href (the link TEXT still
        // shows the human-readable "abc def" — only the href needs encoding).
        html |> Expect.stringContains "the id's space is percent-encoded in the href" "abc%20def"

      testCase "renderInspectorPage wraps the content in a full document with a link back to the dashboard" <| fun _ ->
        let html = InspectorModel.NotFound(Some EntityKind.Member, "ghost") |> renderInspectorPage |> render
        html |> Expect.stringContains "a full document" "<html"
        html |> Expect.stringContains "links back to the dashboard" "/dashboard\""

      testCase "renderSearchPage embeds a GET form posting to the search route" <| fun _ ->
        let html = renderSearchPage "" [] |> render
        html |> Expect.stringContains "search form targets the inspect route" "/dashboard/inspect"
    ]
  ]
