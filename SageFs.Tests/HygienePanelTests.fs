/// The dashboard's workspace hygiene panel in every state it can be in: no repository, not scanned, scanning,
/// tidying, a failed scan, a scan with nothing to tidy, a scan with something to tidy, and one after a tidy. The
/// control reflects the state (what it says, whether it does anything, which plan it carries), and the panel is one
/// node with a stable id so a scan or a tidy can patch it in place.
module SageFs.Tests.HygienePanelTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.WorkspaceHygieneRender
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.HygieneFixtures

let private html (view: HygieneService.HygieneView) : string = renderNode (renderHygienePanel view)

let private snapshotOf (subjects: Subject list) : HygieneService.Snapshot =
  let leftovers = subjects |> List.map (classify now)
  let plan = Planner.plan leftovers
  { Repo = repoPath; TakenAt = DateTime.UtcNow; Leftovers = leftovers; Plan = plan; Summary = summarize leftovers plan }

let private scanned (subjects: Subject list) : HygieneService.HygieneView =
  HygieneService.HygieneView.Scanned(snapshotOf subjects, HygieneService.TidiedBefore.NothingTidiedYet)

let private contains (what: string) (needle: string) (text: string) = text |> Expect.stringContains what needle

let private doesNotContain (what: string) (needle: string) (text: string) =
  text.Contains needle |> Expect.isFalse what

[<Tests>]
let panelTests =
  testList "Dashboard: the workspace hygiene panel" [

    testCase "it is one node with a stable id and a collapsible body, in every state, so a patch can always find it" <| fun _ ->
      let states =
        [ HygieneService.HygieneView.NoRepository
          HygieneService.HygieneView.NotScanned repoPath
          HygieneService.HygieneView.Scanning repoPath
          HygieneService.HygieneView.Tidying repoPath
          HygieneService.HygieneView.ScanFailed(repoPath, "git is not installed")
          scanned [] ]
      for state in states do
        let text = html state
        text |> contains "has the panel id" (sprintf "id=\"%s\"" DomIds.HygienePanel)
        text |> contains "is a details element" "<details"
        text |> contains "is a panel" "panel"

    testCase "no repository says so, offers nothing to press, and does not look broken" <| fun _ ->
      let text = html HygieneService.HygieneView.NoRepository
      text |> contains "explains" "No git repository in play"
      text |> doesNotContain "no scan button" (sprintf "id=\"%s\"" DomIds.HygieneScan)

    testCase "not scanned offers a scan that posts to the scan route, and says a scan changes nothing" <| fun _ ->
      let text = html (HygieneService.HygieneView.NotScanned repoPath)
      text |> contains "has the scan button" (sprintf "id=\"%s\"" DomIds.HygieneScan)
      text |> contains "posts to the scan route" "/dashboard/hygiene/scan"
      text |> contains "says it changes nothing" "changes nothing"
      text |> doesNotContain "no tidy before a scan" (sprintf "id=\"%s\"" DomIds.HygieneTidy)

    testCase "while scanning the button says so and is disabled, so a second press cannot start a second scan" <| fun _ ->
      let text = html (HygieneService.HygieneView.Scanning repoPath)
      text |> contains "says scanning" "Scanning..."
      text |> contains "is disabled" "disabled"
      text |> doesNotContain "posts nothing" "/dashboard/hygiene/scan"

    testCase "while tidying the control says so and cannot be pressed again" <| fun _ ->
      let text = html (HygieneService.HygieneView.Tidying repoPath)
      text |> contains "says tidying" "Tidying..."
      text |> contains "is disabled" "disabled"
      text |> doesNotContain "posts nothing" "/dashboard/hygiene/tidy"

    testCase "a failed scan shows the reason as an error and offers another scan" <| fun _ ->
      let text = html (HygieneService.HygieneView.ScanFailed(repoPath, "git is not installed"))
      text |> contains "has the reason" "git is not installed"
      text |> contains "is an error line" "output-error"
      text |> contains "offers to scan again" "Scan again"

    testCase "nothing to tidy: the tidy control says so and does nothing, and a rescan is still offered" <| fun _ ->
      let busy = worktree "agent-1" |> withUse (InUseReason.LiveSession "abc")
      let text = html (scanned [ busy ])
      text |> contains "says nothing to tidy" "Nothing to tidy"
      text |> doesNotContain "tidy posts nothing" "/dashboard/hygiene/tidy/"
      text |> contains "offers a rescan" "Rescan"

    testCase "something to tidy: the control says how many and how much, and carries the plan it will run" <| fun _ ->
      let subjects = [ worktree "agent-1"; worktree "agent-2"; worktree "agent-3" |> unmergedWith [] ]
      let snapshot = snapshotOf subjects
      let (PlanId id) = snapshot.Plan.Id
      let text = html (HygieneService.HygieneView.Scanned(snapshot, HygieneService.TidiedBefore.NothingTidiedYet))
      text |> contains "counts the safe items" "Tidy 2 safe items"
      text |> contains "carries the plan id" (sprintf "/dashboard/hygiene/tidy/%s" id)
      text |> contains "shows the dry run" "Dry run: 2 safe to reclaim"
      text |> contains "lists what needs a look" "Has commits the base branch lacks"
      text |> contains "shows how to keep the commits" "worktree remove"

    testCase "after a tidy the panel says what it did, until the next scan" <| fun _ ->
      let summary : HygieneService.TidySummary =
        { Removed = 3; AlreadyGone = 1; Skipped = 2; Failed = 0; ReclaimedBytes = 5L * 1024L * 1024L; At = DateTime.UtcNow }
      let view = HygieneService.HygieneView.Scanned(snapshotOf [ worktree "agent-9" |> unmergedWith [] ], HygieneService.TidiedBefore.Tidied summary)
      let text = html view
      text |> contains "says what it did" "Tidied: 3 removed (5 MB reclaimed), 1 already gone, 2 skipped, 0 failed."
      text |> contains "keeps the review group" "Has commits the base branch lacks"

    testCase "a long list shows the biggest few and says how many more, so a hundred worktrees do not become a wall" <| fun _ ->
      let many = [ for i in 1 .. Limits.itemsPerGroup + 4 -> { worktree (sprintf "agent-%d" i) with SizeBytes = int64 i * aSmallTreeBytes } ]
      let text = html (scanned many)
      text |> contains "says how many more" "...and 4 more"

    testCase "what the panel prints is escaped: a path cannot open a tag" <| fun _ ->
      let hostile = { worktree "x" with Target = Target.Directory(worktreeRoot + "/<script>alert(1)</script>") }
      let text = html (scanned [ hostile ])
      text |> doesNotContain "no raw script tag" "<script>alert(1)</script>"

    testCase "the full shell carries the panel inside #main in the sidebar's expanded-only block, and the shell's chrome is still all there" <| fun _ ->
      let snap : DashboardSnapshot =
        { DashboardSnapshot.Version = "0.0.0"
          SessionState = "ready"; SessionId = "0a2b3c4d"; WorkingDir = "/work"
          WarmupProgress = ""; WorkflowLabel = "REPL"
          EvalStats = FixtureStats.noEvals
          ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
          HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
          OutputPanel = renderOutputForSession "0a2b3c4d" 0 [] "No output yet"
          SessionsPanel = Elem.div [ Attr.id "sessions-marker" ] []; SessionPicker = Elem.div [] []
          ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
          BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []
          FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []
          FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []
          LiveTestingPanel = renderLiveTestingPanel Features.LiveTestActivity.LiveTestActivity.Off
          FrictionPanel = Elem.div [] []
          CohortPanel = Elem.div [ Attr.id "cohort-marker" ] []
          HygienePanel = renderHygienePanel (HygieneService.HygieneView.NotScanned repoPath)
          ActiveProject = None
          ProjectRoles = []
          App = AppRun.AppRunState.NotRunning
          EvalToPixelP50Ms = None
          EvalToPixelP99Ms = None }
      let text = renderNode (renderMainContent snap)
      text |> contains "is inside the one main node" (sprintf "id=\"%s\"" DomIds.Main)
      text |> contains "has the hygiene panel" (sprintf "id=\"%s\"" DomIds.HygienePanel)
      text |> contains "keeps the sessions panel" "sessions-marker"
      (text.IndexOf "cohort-marker" < text.IndexOf DomIds.HygienePanel) |> Expect.isTrue "comes after the cohort panel"
      (text.IndexOf "expanded-only" < text.IndexOf DomIds.HygienePanel) |> Expect.isTrue "inside the expanded-only block"
  ]
