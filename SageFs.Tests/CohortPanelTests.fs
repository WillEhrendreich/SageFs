/// Cohort dashboard panel (cohort-integration-plan.md Slice 4): pure render
/// tests for `renderCohortPanel`. These drive `Cohort.decide`/`project`
/// directly to build sample frames — no owner, no daemon — the same "pure
/// core, no IO" discipline `CohortOwnerTests.fs` uses, applied to the render
/// side instead of the actor side.
module SageFs.Tests.CohortPanelTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Server
open SageFs.Server.DashboardFragments

let private render (node: XmlNode) = renderNode node

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

/// These panel fixtures drive the pure core with no session and no working
/// directory, so the cohort is the v1 machine-wide one — the scope
/// `frameAfterWith` opens below. A `Repository` or `Named` scope would make every
/// panel test a test of scoping rather than of rendering.
let private machine = CohortScope.Machine

/// Folds `decide` over a fixed command list starting from an empty state —
/// no owner, no ledger, no IO — then projects the resulting `LedgerHead`
/// into a `CohortFrame` exactly as `CohortOwner.frameOf` does. `snapshots`
/// is `project`'s other input — the per-session Pass/Fail/Stale test
/// outcomes that become the frame's matrix bitplanes (§5.7); `frameAfter`
/// passes none, for tests that don't care about the matrix.
let private frameAfterWith (commands: CohortCommand<MemberId> list) (snapshots: SessionSnapshot<MemberId>[]) : CohortFrame<MemberId> =
  let finalState, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.forScope machine, 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } snapshots

let private frameAfter (commands: CohortCommand<MemberId> list) : CohortFrame<MemberId> =
  frameAfterWith commands [||]

/// The same fold as `frameAfterWith`, opened at `scope` rather than at
/// `machine`. This exists for the scope cases at the foot of the list and
/// nothing else: every panel test above is about RENDERING a frame, and one
/// machine-wide cohort renders them all, so threading a `scope` argument
/// through `frameAfter` would put a choice on every call site with no
/// assertion behind it. The reducer refuses a command naming a different scope
/// than the one the cohort was opened at, so this helper cannot build a frame
/// whose commands and state disagree — the fold's `failwithf` says so loudly
/// rather than quietly projecting a frame that never existed.
let private frameIn (scope: CohortScope) (commands: CohortCommand<MemberId> list) : CohortFrame<MemberId> =
  let finalState, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.forScope scope, 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } [||]

let private emptyFrame : CohortFrame<MemberId> =
  project (replayHead []) [||]

[<Tests>]
let cohortPanelTests =
  testList "Cohort dashboard panel" [

    testCase "WHY — an empty cohort renders a tasteful empty state, never a blank or broken panel" <| fun _ ->
      let html = renderCohortPanel emptyFrame |> render
      html |> Expect.stringContains "shows the panel heading with zero members" "Cohort — 0 members"
      html |> Expect.stringContains "explains how to join, instead of rendering nothing" "join_cohort"

    testCase "WHY — a burst of orphaned claims renders a BOUNDED list with an honest overflow line, never 43 rows" <| fun _ ->
      // The live symptom: 43 orphaned claims, hours old, all listed. Each of
      // 43 members holds one claim, then departs (orphaning it).
      let members = [ for i in 1 .. 43 -> MemberId.Minted (sprintf "agent-%d" i) ]
      let frame =
        frameAfter
          [ for m in members -> CohortCommand.Join(m, JoinableRole.Implementer, None, machine)
            for i, m in List.indexed members -> CohortCommand.AcquireClaim(m, ClaimScope.File (sprintf "src/F%d.fs" i), "editing", machine)
            for m in members -> CohortCommand.Depart(m, machine) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "the heading still reports the TRUE total" "Claims (43)"
      let cap = Features.CohortBoundedView.rowCap
      (html.Split("fence ").Length - 1)
      |> Expect.equal "only `rowCap` claim rows are drawn" cap
      html |> Expect.stringContains "the overflow line says how many were hidden and why" (sprintf "+%d more claims (%d orphaned)" (43 - cap) (43 - cap))
      html |> Expect.stringContains "members are bounded the same way" (sprintf "+%d more members (%d departed)" (43 - cap) (43 - cap))

    testCase "WHY — get_cohort_status text is bounded the same way, with the true totals and a '+N more' line" <| fun _ ->
      let members = [ for i in 1 .. 43 -> MemberId.Minted (sprintf "agent-%d" i) ]
      let frame =
        frameAfter
          [ for m in members -> CohortCommand.Join(m, JoinableRole.Implementer, None, machine)
            for i, m in List.indexed members -> CohortCommand.AcquireClaim(m, ClaimScope.File (sprintf "src/F%d.fs" i), "editing", machine)
            for m in members -> CohortCommand.Depart(m, machine) ]
      let text = Features.CohortStatusText.render frame
      let cap = Features.CohortBoundedView.rowCap
      text |> Expect.stringContains "the header keeps the true claim total" "Claims (43):"
      (text.Split("held-by=").Length - 1) |> Expect.equal "only `rowCap` claim rows are listed" cap
      text |> Expect.stringContains "the hidden claims are counted, not dropped" (sprintf "+%d more claims (%d orphaned)" (43 - cap) (43 - cap))

    testCase "WHY — a joined member renders their id and role" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None, machine) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "shows the singular member count in the heading" "Cohort — 1 member"
      html |> Expect.stringContains "shows the member's display id" (MemberId.display alice)
      html |> Expect.stringContains "shows the member's role" "Implementer"

    testCase "WHY — two members pluralize the heading count" <| fun _ ->
      let frame =
        frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None, machine); CohortCommand.Join(bob, JoinableRole.Verifier, None, machine) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "pluralizes members in the heading" "Cohort — 2 members"
      html |> Expect.stringContains "shows the second member's role" "Verifier"

    testCase "WHY — a held claim renders its scope and the holder's display id, not the raw MemberId case" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None, machine)
            CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing", machine) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "shows the claimed file path" "src/Foo.fs"
      html |> Expect.stringContains "shows who holds the claim" (sprintf "held by %s" (MemberId.display alice))

    testCase "WHY — the ledger version in the panel is the frame's Version (the ledger Seq), not a fabricated counter" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None, machine)
            CohortCommand.AcquireClaim(alice, ClaimScope.Project "src/Foo.fsproj", "working", machine) ]
      frame.Version |> Expect.equal "two applied commands land ledger seq 1 (0-indexed)" 1L<ledgerSeq>
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "renders the ledger version" (sprintf "Ledger v%d" (int64 frame.Version))

    testCase "WHY — renderMainContent always includes the cohort panel, so it can never be a blank/missing sidebar section" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Observer, None, machine) ]
      let cohortPanel = renderCohortPanel frame
      let snap : DashboardTypes.DashboardSnapshot =
        { DashboardTypes.DashboardSnapshot.Version = "0.0.0"
          ConnectionState = DashboardTypes.DashboardConnectionState.Connected
          SessionState = "No session"; SessionId = ""; WorkingDir = ""
          WarmupProgress = ""; WorkflowLabel = "Interactive"
          EvalStats = FixtureStats.noEvals
          AlarmPanel = Elem.div [] []; DaemonHealth = Elem.div [] []
          FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []
          FilmstripPanel = Elem.div [] []; ThemeName = "default"; ConnectionLabel = None
          HotReloadPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []
          SessionContextPanel = Elem.div [] []; OutputPanel = Elem.div [] []
          SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
          ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
          BindingsPanel = Elem.div [] []; FrictionPanel = Elem.div [] []
          CohortPanel = cohortPanel
          HygienePanel = Elem.div [] []
          ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning
          EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }
      let html = renderMainContent snap |> render
      html |> Expect.stringContains "renderMainContent carries the cohort-panel dom id" "cohort-panel"
      html |> Expect.stringContains "renderMainContent carries the joined member's id" (MemberId.display alice)

    // §6.5 "Matrix view — a picture, with a text fallback" (Phase 2 item 16).
    testCase "WHY — a frame with projected test outcomes renders the matrix picture and its text fallback" <| fun _ ->
      let snapshots =
        [| { Member = None; SessionId = "integration"; Generation = 1L
             PassingTests = [ Cohort.TestId "t1" ]; FailingTests = [ Cohort.TestId "t2" ]; StaleTests = [] }
           { Member = Some alice; SessionId = "sess-alice"; Generation = 1L
             PassingTests = [ Cohort.TestId "t1" ]; FailingTests = []; StaleTests = [ Cohort.TestId "t2" ] } |]
      let frame = frameAfterWith [ CohortCommand.Join(alice, JoinableRole.Implementer, None, machine) ] snapshots
      frame.TestIds.Length |> Expect.equal "two distinct tests were projected" 2
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "carries the cohort-matrix dom id" "cohort-matrix"
      html |> Expect.stringContains "embeds the matrix as an inline PNG data URI, no served route" "data:image/png;base64,"
      html |> Expect.stringContains "labels the text fallback" "Text fallback"
      html
      |> Expect.stringContains
        "the visible text fallback is exactly CohortMatrixRender.toCharGrid's own output"
        (Features.CohortMatrixRender.toCharGrid frame.Pass frame.Fail frame.Stale)

    testCase "WHY — a frame with zero projected test outcomes renders no matrix section at all" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None, machine) ]
      frame.TestIds.Length |> Expect.equal "no snapshots were projected" 0
      let html = renderCohortPanel frame |> render
      (html.Contains "cohort-matrix") |> Expect.isFalse "no matrix dom id when there are no test outcomes to show"
      (html.Contains "data:image/png") |> Expect.isFalse "no PNG data URI when there are no test outcomes to show"

    // ── Scope ──────────────────────────────────────────────────────────────
    //
    // Everything above builds ONE machine-wide cohort, which is what a v1
    // panel fixture was. These cases are the only ones that talk about two,
    // and they assert the thing the scope gate exists for: two cohorts that
    // cannot see each other. `machine` stays the right choice above — a
    // `Repository` scope there would make each of them a test of scoping
    // rather than a test of the rendering it was written for.

    testCase "WHY — the same member in two different scopes is two independent cohorts, not one shared table" <| fun _ ->
      let repoX = Scope.ofWorkingDirectory Scope.PerRepository "/tmp/x"
      let repoY = Scope.ofWorkingDirectory Scope.PerRepository "/tmp/y"
      let frameX = frameIn repoX [ CohortCommand.Join(alice, JoinableRole.Implementer, None, repoX) ]
      let frameY = frameIn repoY [ CohortCommand.Join(bob, JoinableRole.Verifier, None, repoY) ]
      // The panel is rendered from ONE frame, so if the scopes had collapsed
      // into one cohort the member list would carry both names.
      let htmlX = renderCohortPanel frameX |> render
      htmlX |> Expect.stringContains "the first scope's member is present in its own frame" (MemberId.display alice)
      (htmlX.Contains(MemberId.display bob)) |> Expect.isFalse "the other scope's member is NOT in this frame"
      htmlX |> Expect.stringContains "and it is genuinely that scope's cohort, not an empty one" "Cohort — 1 member"
      // Symmetrically: bob's cohort holds bob and has never heard of alice.
      let htmlY = renderCohortPanel frameY |> render
      htmlY |> Expect.stringContains "the second scope's member is present in its own frame" (MemberId.display bob)
      (htmlY.Contains(MemberId.display alice)) |> Expect.isFalse "the first scope's member is NOT in this frame"
      // Each cohort's conductor seat is its OWN: alice binds one in repoX and
      // bob binds one in repoY. Shared, bob would be refused as joining a
      // cohort whose seat is already filled.
      frameX.Conductor |> Expect.equal "repoX's conductor seat is bound by its own first joiner" (ConductorBinding.Bound alice)
      frameY.Conductor |> Expect.equal "repoY's conductor seat is bound by its own first joiner" (ConductorBinding.Bound bob)

    testCase "WHY — a command addressed to a different scope is refused WrongCohortScope, so two cohorts cannot cross-contaminate" <| fun _ ->
      let repoX = Scope.ofWorkingDirectory Scope.PerRepository "/tmp/x"
      let repoY = Scope.ofWorkingDirectory Scope.PerRepository "/tmp/y"
      // repoX's cohort: alice has joined and holds a claim.
      let state =
        [ CohortCommand.Join(alice, JoinableRole.Implementer, None, repoX)
          CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing", repoX) ]
        |> List.fold
          (fun state cmd ->
            match decide epoch [| byte 0 |] state cmd with
            | Ok(newState, _, _) -> newState
            | Error err -> failwithf "unexpected refusal building the fixture cohort: %A" err)
          (CohortState.forScope repoX)
      // A claim over a file in /tmp/y, sent INTO repoX's cohort. It is refused
      // for the scope, not for the path — a crossing command must not get far
      // enough to become a row in this cohort's claim table.
      match decide epoch [| byte 3 |] state (CohortCommand.AcquireClaim(bob, ClaimScope.File "src/Other.fs", "editing", repoY)) with
      | Error(CohortError.WrongCohortScope(requested, cohort)) ->
        requested |> Expect.equal "the refusal names the scope the command was addressed to" repoY
        cohort |> Expect.equal "and the scope of the cohort that refused it" repoX
      | other -> failtestf "expected WrongCohortScope, got %A" other
      let html = renderCohortPanel (frameIn repoX []) |> render
      (html.Contains(MemberId.display bob)) |> Expect.isFalse "the refused command left no member in the cohort"
      (html.Contains("src/Other.fs")) |> Expect.isFalse "the refused command left no claim in the cohort"

    testCase "WHY — the same path spelled two ways is ONE cohort, so canonicalization is what stops an agent escaping its own claim table" <| fun _ ->
      // Under PerRepository, a directory with no .git anywhere above it has no
      // repository, so the fallback is the path itself as a `Named` scope —
      // which is exactly the case that has to be canonicalized: without it,
      // "/tmp/x" and "/tmp/x/" would be two cohorts holding one repository's
      // claims, and the collision `CohortScope` exists to remove would come
      // back wearing a trailing separator.
      let plain = Scope.ofWorkingDirectory Scope.PerRepository "/tmp/x"
      let trailing = Scope.ofWorkingDirectory Scope.PerRepository "/tmp/x/"
      let roundabout = Scope.ofWorkingDirectory Scope.PerRepository "/tmp/x/../x"
      Scope.equal plain trailing |> Expect.isTrue "a trailing separator is the same scope, not a second cohort"
      Scope.equal plain roundabout |> Expect.isTrue "a path that walks through .. and back is the same scope"
      // And the consequences in the reducer, not just in the label: the same
      // member joining under two spellings is a DUPLICATE join, because it is
      // one cohort — not two clean memberships.
      let state =
        match decide epoch [| byte 0 |] (CohortState.forScope plain) (CohortCommand.Join(alice, JoinableRole.Implementer, None, trailing)) with
        | Ok(newState, _, _) -> newState
        | Error err -> failwithf "unexpected refusal joining under the second spelling: %A" err
      match decide epoch [| byte 1 |] state (CohortCommand.Join(alice, JoinableRole.Verifier, None, roundabout)) with
      | Error(CohortError.DuplicateJoin who) -> who |> Expect.equal "the second join names the member that was already there" alice
      | other -> failtestf "expected DuplicateJoin for the same member under a second spelling, got %A" other

    testCase "WHY — a Machine scope is one cohort whatever path an agent says it is in, so a working directory cannot fork the machine-wide cohort" <| fun _ ->
      // The whole-machine scope is not a function of any path, which is the
      // contrast with the case above: renaming a checkout must not renumber the
      // machine cohort's ledger or empty its claim table.
      let wholeMachine = Scope.ofWorkingDirectory Scope.WholeMachine "/tmp/x"
      wholeMachine |> Expect.equal "any working directory maps to the one machine scope" CohortScope.Machine
      Scope.equal wholeMachine CohortScope.Machine |> Expect.isTrue "and it is the machine scope, not a path-derived one"
      // The proof that matters is in the reducer, not the label: there is one
      // machine cohort however it is spelled, so a claim on a path another
      // member already holds collides in ONE claim table.
      let state =
        [ CohortCommand.Join(alice, JoinableRole.Implementer, None, machine)
          CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing", machine)
          // bob joins BEFORE he claims: the reducer refuses a claim from a
          // non-member before it ever looks at the claim table, so the
          // fixture has to be a well-formed cohort or this would assert
          // MemberNotPresent and prove nothing about sharing.
          CohortCommand.Join(bob, JoinableRole.Verifier, None, machine) ]
        |> List.fold
          (fun state cmd ->
            match decide epoch [| byte 0 |] state cmd with
            | Ok(newState, _, _) -> newState
            | Error err -> failwithf "unexpected refusal building the machine cohort: %A" err)
          (CohortState.forScope machine)
      // bob claims the file alice already holds. He is refused, and the refusal
      // names HER — one shared table, whichever directory either of them says
      // they are working in.
      match decide epoch [| byte 1 |] state (CohortCommand.AcquireClaim(bob, ClaimScope.File "src/Foo.fs", "editing", machine)) with
      | Error(CohortError.ClaimConflict(claimed, holder)) ->
        claimed |> Expect.equal "the conflict names the path both members claimed" (ClaimScope.File "src/Foo.fs")
        holder |> Expect.equal "and the member already holding it" alice
      | other -> failtestf "expected ClaimConflict on the machine cohort, got %A" other
  ]
