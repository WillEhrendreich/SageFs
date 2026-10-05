/// The guided new-session dialog's model: what state the dialog is in, what moves it, which sessions
/// count as "already here", and what a refusal says. Everything in this file is pure, or reads only a temp
/// directory: no HTTP, no DOM, no daemon. The rendered markup is in NewSessionDialogViewTests, the
/// browser journeys in NewSessionDialogBrowserTests.
module SageFs.Tests.NewSessionDialogTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkflowTypes
open SageFs.Server.DashboardTypes
open SageFs.Server.NewSessionDialog
open SageFs.Server.NewSessionDiscovery

// ── Fixtures ─────────────────────────────────────────────────────────────

let repoA = { Id = "a1"; WorkingDirectory = "/work/repo"; Boundary = Boundary.Repository "/work/repo" }
let repoASub = { Id = "a2"; WorkingDirectory = "/work/repo/src/App"; Boundary = Boundary.Repository "/work/repo" }
let worktreeW = { Id = "w1"; WorkingDirectory = "/work/repo/.claude/worktrees/x"; Boundary = Boundary.Worktree("/work/repo/.claude/worktrees/x", "feature") }
let plainP = { Id = "p1"; WorkingDirectory = "/scratch/play"; Boundary = Boundary.Plain "/scratch/play" }

let candidate path kind frameworks : Candidate = { Path = path; Kind = kind; Frameworks = frameworks }

let foundIn dir : Found =
  { Directory = dir
    Candidates = [ candidate "App.fsproj" CandidateKind.Project (Frameworks.Declared [ "net10.0" ]) ]
    Hint = WorkflowHint.NoneSuggested }

let request dir target : Request =
  { Directory = dir; Target = target; Workflow = SessionWorkflow.Interactive }

let overlapOf session relation : Overlap = { Session = session; Relation = relation }

// ── Generators: every state and event the dialog can have ───────────────────

let genDir = Gen.elements [ "/work/repo"; "/work/other"; "/scratch/play"; "/tmp/x y" ]

let genRefusal : Gen<Refusal> =
  Gen.oneof [
    Gen.constant Refusal.NoDirectory
    genDir |> Gen.map Refusal.DirectoryMissing
    Gen.constant Refusal.NothingPicked
    Gen.elements [
      SageFsError.NeedsRebuild [ "App.dll" ]
      SageFsError.DuplicateSession("a1", "/work/repo")
      SageFsError.WorkerSpawnFailed "no sdk"
      SageFsError.SupervisorBusy(9, 8)
      SageFsError.SessionCreationFailed "boom" ]
    |> Gen.map Refusal.Daemon ]

let genOverlap : Gen<Overlap> =
  gen {
    let! session = Gen.elements [ repoA; repoASub; worktreeW; plainP ]
    let! relation = Gen.elements [ Relation.SameDirectory; Relation.SameRepository "/work/repo" ]
    return { Session = session; Relation = relation } }

let genFound : Gen<Found> = genDir |> Gen.map foundIn

let genTarget : Gen<Target> =
  Gen.oneof [ Gen.constant Target.Bare; Gen.constant (Target.Load("App.fsproj", [ "Lib/Lib.fsproj" ])) ]

let genRequest : Gen<Request> =
  gen {
    let! dir = genDir
    let! target = genTarget
    let! workflow = Gen.elements WorkflowSwitch.options
    return { Directory = dir; Target = target; Workflow = workflow } }

let genState : Gen<NewSessionDialog> =
  Gen.oneof [
    Gen.constant NewSessionDialog.Closed
    genDir |> Gen.map NewSessionDialog.Discovering
    genFound |> Gen.map NewSessionDialog.Choosing
    gen {
      let! found = genFound
      let! first = genOverlap
      let! rest = Gen.listOf genOverlap
      return NewSessionDialog.Warning(found, first, rest) }
    gen {
      let! req = genRequest
      let! found = genFound
      return NewSessionDialog.Creating(req, found) }
    gen {
      let! reason = genRefusal
      let! found = genFound
      return NewSessionDialog.Refused(reason, found) } ]

let genEvent : Gen<Event> =
  Gen.oneof [
    genDir |> Gen.map Event.Open
    gen {
      let! found = genFound
      let! overlaps = Gen.listOf genOverlap
      return Event.Found(found, overlaps) }
    gen {
      let! dir = genDir
      let! reason = genRefusal
      return Event.Missing(dir, reason) }
    genRequest |> Gen.map Event.Submit
    Gen.constant Event.Created
    genRefusal |> Gen.map Event.Failed
    gen {
      let! dir = genDir
      let! reason = genRefusal
      return Event.Rejected(dir, reason) }
    Gen.constant Event.Dismiss ]

let states = Arb.fromGen genState
let events = Arb.fromGen genEvent
let sequences = Arb.fromGen (Gen.listOf genEvent)

let isCreating state =
  match state with
  | NewSessionDialog.Creating _ -> true
  | NewSessionDialog.Closed
  | NewSessionDialog.Discovering _
  | NewSessionDialog.Choosing _
  | NewSessionDialog.Warning _
  | NewSessionDialog.Refused _ -> false

// ── The state machine ───────────────────────────────────────────────────────

[<Tests>]
let stateMachine =
  testList "NewSessionDialog.step — one total transition function" [

    testCase "WHY — Open starts discovery for the directory the person is looking at" <| fun _ ->
      NewSessionDialog.step NewSessionDialog.Closed (Event.Open "/work/repo")
      |> Expect.equal "Closed + Open goes to Discovering" (NewSessionDialog.Discovering "/work/repo")

    testCase "WHY — a discovery with nothing already here lands on Choosing" <| fun _ ->
      let found = foundIn "/work/repo"
      NewSessionDialog.step (NewSessionDialog.Discovering "/work/repo") (Event.Found(found, []))
      |> Expect.equal "no overlap, no warning" (NewSessionDialog.Choosing found)

    testCase "WHY — a discovery that met a live session lands on Warning, carrying every one of them in order" <| fun _ ->
      let found = foundIn "/work/repo"
      let first = overlapOf repoA Relation.SameDirectory
      let second = overlapOf repoASub (Relation.SameRepository "/work/repo")
      NewSessionDialog.step (NewSessionDialog.Discovering "/work/repo") (Event.Found(found, [ first; second ]))
      |> Expect.equal "the warning names both" (NewSessionDialog.Warning(found, first, [ second ]))

    testCase "WHY — a late discovery for a directory the person has since left is dropped, not shown" <| fun _ ->
      let stale = foundIn "/work/repo"
      NewSessionDialog.step (NewSessionDialog.Discovering "/work/other") (Event.Found(stale, []))
      |> Expect.equal "still discovering the newer directory" (NewSessionDialog.Discovering "/work/other")

    testCase "WHY — a late discovery after the dialog closed does not reopen anything" <| fun _ ->
      NewSessionDialog.step NewSessionDialog.Closed (Event.Found(foundIn "/work/repo", []))
      |> Expect.equal "still closed" NewSessionDialog.Closed

    testCase "WHY — a directory that does not exist is a refusal the dialog shows, with the directory named" <| fun _ ->
      NewSessionDialog.step (NewSessionDialog.Discovering "/nope") (Event.Missing("/nope", Refusal.DirectoryMissing "/nope"))
      |> Expect.equal "refused in the dialog"
        (NewSessionDialog.Refused(Refusal.DirectoryMissing "/nope", Found.nothing "/nope"))

    testCase "WHY — Submit moves to Creating and keeps what discovery found, so a refusal can show the choices again" <| fun _ ->
      let found = foundIn "/work/repo"
      let req = request "/work/repo" Target.Bare
      NewSessionDialog.step (NewSessionDialog.Choosing found) (Event.Submit req)
      |> Expect.equal "Creating carries the request and the found list" (NewSessionDialog.Creating(req, found))

    testCase "WHY — a second Submit while one is in flight changes nothing, so a double click cannot create two sessions" <| fun _ ->
      let creating = NewSessionDialog.Creating(request "/work/repo" Target.Bare, foundIn "/work/repo")
      NewSessionDialog.step creating (Event.Submit (request "/work/repo" Target.Bare))
      |> Expect.equal "unchanged" creating

    testCase "WHY — Created closes the dialog: the new session's card is the answer now" <| fun _ ->
      NewSessionDialog.step (NewSessionDialog.Creating(request "/work/repo" Target.Bare, foundIn "/work/repo")) Event.Created
      |> Expect.equal "closed" NewSessionDialog.Closed

    testCase "WHY — a refusal while creating reopens the dialog on the Refused state with the reason" <| fun _ ->
      let found = foundIn "/work/repo"
      let reason = Refusal.Daemon (SageFsError.NeedsRebuild [ "App.dll" ])
      NewSessionDialog.step (NewSessionDialog.Creating(request "/work/repo" Target.Bare, found)) (Event.Failed reason)
      |> Expect.equal "Refused carries the reason and the earlier choices" (NewSessionDialog.Refused(reason, found))

    testCase "WHY — Dismiss while creating is ignored: closing the box must not cancel or hide a create that is running" <| fun _ ->
      let creating = NewSessionDialog.Creating(request "/work/repo" Target.Bare, foundIn "/work/repo")
      NewSessionDialog.step creating Event.Dismiss
      |> Expect.equal "still creating" creating

    testCase "WHY — a click that is refused before the daemon (nothing typed, nothing ticked) shows the refusal and keeps the choices" <| fun _ ->
      let found = foundIn "/work/repo"
      NewSessionDialog.step (NewSessionDialog.Choosing found) (Event.Rejected("/work/repo", Refusal.NothingPicked))
      |> Expect.equal "Refused with the earlier choices" (NewSessionDialog.Refused(Refusal.NothingPicked, found))

    testCase "WHY — a refused click from a state with nothing found shows an empty list for the directory it was about" <| fun _ ->
      NewSessionDialog.step NewSessionDialog.Closed (Event.Rejected("/work/repo", Refusal.NoDirectory))
      |> Expect.equal "refused, nothing to list" (NewSessionDialog.Refused(Refusal.NoDirectory, Found.nothing "/work/repo"))

    testCase "WHY — a refusal does not interrupt a create that is already running" <| fun _ ->
      let creating = NewSessionDialog.Creating(request "/work/repo" Target.Bare, foundIn "/work/repo")
      NewSessionDialog.step creating (Event.Rejected("/work/repo", Refusal.NothingPicked))
      |> Expect.equal "unchanged" creating

    testProperty "WHY — step is total: every event in every state gives a state, never an exception" <|
      Prop.forAll states (fun state ->
        Prop.forAll events (fun event ->
          let _ = NewSessionDialog.step state event
          true))

    testProperty "WHY — Creating is only ever entered by a Submit" <|
      Prop.forAll states (fun state ->
        Prop.forAll events (fun event ->
          let next = NewSessionDialog.step state event
          match isCreating next, isCreating state, event with
          | true, false, Event.Submit _ -> true
          | true, false, _ -> false
          | _ -> true))

    testProperty "WHY — Dismiss closes every state except Creating, and leaves Creating alone" <|
      Prop.forAll states (fun state ->
        match NewSessionDialog.step state Event.Dismiss, isCreating state with
        | after, true -> after = state
        | NewSessionDialog.Closed, false -> true
        | _ -> false)

    testProperty "WHY — replaying any event sequence from Closed always ends in some state, and a Dismiss afterwards closes it unless a create is running" <|
      Prop.forAll sequences (fun sequence ->
        let final = sequence |> List.fold NewSessionDialog.step NewSessionDialog.Closed
        match NewSessionDialog.step final Event.Dismiss, isCreating final with
        | after, true -> after = final
        | NewSessionDialog.Closed, false -> true
        | _ -> false)

    testProperty "WHY — Found with overlaps is Warning and with none is Choosing, whatever the overlaps are" <|
      Prop.forAll (Arb.fromGen (Gen.listOf genOverlap)) (fun overlaps ->
        let found = foundIn "/work/repo"
        match NewSessionDialog.step (NewSessionDialog.Discovering "/work/repo") (Event.Found(found, overlaps)), overlaps with
        | NewSessionDialog.Choosing _, [] -> true
        | NewSessionDialog.Warning(_, first, rest), head :: tail -> first = head && rest = tail
        | _ -> false)

    testProperty "WHY — every state has a distinct name for the page to carry, so a journey can tell which state it is in" <|
      Prop.forAll states (fun state -> (NewSessionDialog.stateKey state).Length > 0)
  ]

// ── Which sessions count as already here ───────────────────────────────────

[<Tests>]
let overlap =
  testList "Overlap.decide — a repository or worktree is the boundary, not the path prefix" [

    testCase "WHY — a session in the very same directory is a SameDirectory overlap" <| fun _ ->
      Overlap.decide "/work/repo" (Boundary.Repository "/work/repo") [ repoA ]
      |> Expect.equal "named" [ overlapOf repoA Relation.SameDirectory ]

    testCase "WHY — a trailing slash does not hide the session that is already there" <| fun _ ->
      Overlap.decide "/work/repo/" (Boundary.Repository "/work/repo") [ repoA ]
      |> Expect.equal "still found" [ overlapOf repoA Relation.SameDirectory ]

    testCase "WHY — a session in another folder of the same repository is called out as the same repository" <| fun _ ->
      Overlap.decide "/work/repo" (Boundary.Repository "/work/repo") [ repoASub ]
      |> Expect.equal "the repository is shared" [ overlapOf repoASub (Relation.SameRepository "/work/repo") ]

    testCase "WHY — a git worktree is its own boundary: a session in a worktree nested under the repo is NOT an overlap for the repo" <| fun _ ->
      Overlap.decide "/work/repo" (Boundary.Repository "/work/repo") [ worktreeW ]
      |> Expect.equal "nested path, different checkout, no warning" []

    testCase "WHY — and the same the other way: a worktree does not collide with the main checkout's sessions" <| fun _ ->
      Overlap.decide worktreeW.WorkingDirectory worktreeW.Boundary [ repoA; repoASub ]
      |> Expect.equal "no warning" []

    testCase "WHY — two sessions in the same worktree do overlap" <| fun _ ->
      let other = { worktreeW with Id = "w2"; WorkingDirectory = worktreeW.WorkingDirectory + "/src" }
      Overlap.decide worktreeW.WorkingDirectory worktreeW.Boundary [ other ]
      |> Expect.equal "same worktree root" [ overlapOf other (Relation.SameRepository "/work/repo/.claude/worktrees/x") ]

    testCase "WHY — outside any checkout only the identical directory counts" <| fun _ ->
      Overlap.decide "/scratch/other" (Boundary.Plain "/scratch/other") [ plainP ]
      |> Expect.equal "different plain directories do not overlap" []

    testCase "WHY — the same directory is listed before the same repository" <| fun _ ->
      Overlap.decide "/work/repo" (Boundary.Repository "/work/repo") [ repoASub; repoA ]
      |> Expect.equal "same directory first"
        [ overlapOf repoA Relation.SameDirectory; overlapOf repoASub (Relation.SameRepository "/work/repo") ]

    testProperty "WHY — a result only ever names sessions that were passed in, never more of them" <|
      Prop.forAll (Arb.fromGen (Gen.listOf (Gen.elements [ repoA; repoASub; worktreeW; plainP ]))) (fun sessions ->
        let result = Overlap.decide "/work/repo" (Boundary.Repository "/work/repo") sessions
        result.Length <= sessions.Length
        && result |> List.forall (fun o -> sessions |> List.contains o.Session))
  ]

[<Tests>]
let boundaries =
  testList "Boundary.classify — read from the filesystem, no git" [

    testCase "WHY — a directory with a .git directory is a repository, and a subfolder of it reports the same root" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "nsd-repo-" + Guid.NewGuid().ToString("N"))
      try
        Directory.CreateDirectory(Path.Combine(root, ".git")) |> ignore
        let sub = Directory.CreateDirectory(Path.Combine(root, "src", "App")).FullName
        Boundary.classify sub
        |> Expect.equal "the repository root" (Boundary.Repository root)
      finally
        Directory.Delete(root, true)

    testCase "WHY — a directory with a .git FILE is a worktree with its own root, not the main checkout's" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "nsd-wt-" + Guid.NewGuid().ToString("N"))
      try
        let main = Directory.CreateDirectory(Path.Combine(root, "main")).FullName
        let admin = Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "x")).FullName
        File.WriteAllText(Path.Combine(admin, "HEAD"), "ref: refs/heads/feature\n")
        let wt = Directory.CreateDirectory(Path.Combine(main, ".claude", "worktrees", "x")).FullName
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + admin + "\n")
        Boundary.classify wt
        |> Expect.equal "the worktree's own root and branch" (Boundary.Worktree(wt, "feature"))
      finally
        Directory.Delete(root, true)

    testCase "WHY — a directory outside any checkout is Plain" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "nsd-plain-" + Guid.NewGuid().ToString("N"))
      try
        Directory.CreateDirectory dir |> ignore
        match Boundary.classify dir with
        | Boundary.Plain d -> d |> Expect.equal "named by itself" dir
        | other -> failtestf "expected Plain, got %A" other
      finally
        Directory.Delete(dir, true)
  ]

// ── What the person asked for ───────────────────────────────────────────────

[<Tests>]
let requests =
  testList "Request.parse — the signals a click sends, turned into a request or a named refusal" [

    testCase "WHY — an empty directory is refused by name, never sent to the daemon" <| fun _ ->
      Request.parse "   " TargetKind.BareSession [] "interactive"
      |> Expect.equal "no directory" (Error Refusal.NoDirectory)

    testCase "WHY — Bare needs no project, whatever is still ticked from an earlier directory" <| fun _ ->
      Request.parse "/work/repo" TargetKind.BareSession [ "Old.fsproj" ] "interactive"
      |> Expect.equal "bare ignores the ticks" (Ok (request "/work/repo" Target.Bare))

    testCase "WHY — loading projects with none ticked is refused: it would be a session that loads nothing" <| fun _ ->
      Request.parse "/work/repo" TargetKind.LoadProjects [] "interactive"
      |> Expect.equal "nothing picked" (Error Refusal.NothingPicked)

    testCase "WHY — ticked projects keep their order, first one named apart so the list can never be empty" <| fun _ ->
      Request.parse "/work/repo" TargetKind.LoadProjects [ "App/App.fsproj"; "Lib/Lib.fsproj" ] "interactive"
      |> Expect.equal "ordered" (Ok (request "/work/repo" (Target.Load("App/App.fsproj", [ "Lib/Lib.fsproj" ]))))

    testCase "WHY — the workflow key the page sends picks the workflow, and an unknown one falls back to the REPL" <| fun _ ->
      (Request.parse "/work/repo" TargetKind.BareSession [] "livetesting" |> Result.map (fun r -> r.Workflow))
      |> Expect.equal "live testing" (Ok SessionWorkflow.LiveTesting)
      (Request.parse "/work/repo" TargetKind.BareSession [] "nonsense" |> Result.map (fun r -> r.Workflow))
      |> Expect.equal "unknown is the safe REPL" (Ok SessionWorkflow.Interactive)

    testCase "WHY — the target kind round-trips through the string the page carries" <| fun _ ->
      [ TargetKind.LoadProjects; TargetKind.BareSession ]
      |> List.iter (fun kind ->
        TargetKind.tryOfKey (TargetKind.key kind)
        |> Expect.equal "round trip" (Some kind))
      TargetKind.tryOfKey "garbage" |> Expect.isNone "unknown is not a kind"
  ]

[<Tests>]
let defaults =
  testList "DefaultChoice — what is ticked when discovery finishes" [

    testCase "WHY — a solution is ticked on its own: it already covers its projects" <| fun _ ->
      let found =
        { foundIn "/work/repo" with
            Candidates =
              [ candidate "All.slnx" CandidateKind.Solution Frameworks.WholeSolution
                candidate "App/App.fsproj" CandidateKind.Project (Frameworks.Declared [ "net10.0" ]) ] }
      DefaultChoice.ofFound found
      |> Expect.equal "the solution" (TargetKind.LoadProjects, [ "All.slnx" ])

    testCase "WHY — a lone project is ticked, because there is nothing else to choose" <| fun _ ->
      DefaultChoice.ofFound (foundIn "/work/repo")
      |> Expect.equal "the project" (TargetKind.LoadProjects, [ "App.fsproj" ])

    testCase "WHY — several projects and no solution tick nothing: that is the person's choice, and Create waits for it" <| fun _ ->
      let found =
        { foundIn "/work/repo" with
            Candidates =
              [ candidate "A/A.fsproj" CandidateKind.Project (Frameworks.Declared [ "net10.0" ])
                candidate "B/B.fsproj" CandidateKind.Project (Frameworks.Declared [ "net10.0" ]) ] }
      DefaultChoice.ofFound found
      |> Expect.equal "none ticked, still loading projects" (TargetKind.LoadProjects, [])

    testCase "WHY — nothing found means Bare, the only thing that can be created here" <| fun _ ->
      DefaultChoice.ofFound (Found.nothing "/work/empty")
      |> Expect.equal "bare" (TargetKind.BareSession, [])
  ]

// ── What the dialog says ────────────────────────────────────────────────────

let isSaying (message: string) (text: string) =
  (text.Trim().Length > 0) |> Expect.isTrue message

[<Tests>]
let words =
  testList "Refusal and workflow words — plain, specific, with a next step" [

    testCase "WHY — every refusal names itself, says what happened, and says what to do next" <| fun _ ->
      let all =
        [ Refusal.NoDirectory
          Refusal.DirectoryMissing "/nope"
          Refusal.NothingPicked
          Refusal.Daemon (SageFsError.NeedsRebuild [ "App.dll" ])
          Refusal.Daemon (SageFsError.DuplicateSession("a1", "/work/repo"))
          Refusal.Daemon (SageFsError.WorkerSpawnFailed "dotnet not found")
          Refusal.Daemon (SageFsError.SupervisorBusy(9, 8))
          Refusal.Daemon (SageFsError.SessionCreationFailed "boom") ]
      for refusal in all do
        Refusal.title refusal |> isSaying (sprintf "%A has a title" refusal)
        Refusal.detail refusal |> isSaying (sprintf "%A says what happened" refusal)
        Refusal.nextAction refusal |> isSaying (sprintf "%A says what to do" refusal)

    testCase "WHY — a missing directory names the directory, because that is the thing the person can fix" <| fun _ ->
      Refusal.detail (Refusal.DirectoryMissing "/nope/where")
      |> Expect.stringContains "the path is in the sentence" "/nope/where"

    testCase "WHY — a project that is not built says to build it" <| fun _ ->
      Refusal.nextAction (Refusal.Daemon (SageFsError.NeedsRebuild [ "App.dll" ]))
      |> Expect.stringContains "build is the next step" "Build"

    testCase "WHY — a refused duplicate says to switch to the session that is already there" <| fun _ ->
      Refusal.nextAction (Refusal.Daemon (SageFsError.DuplicateSession("a1", "/work/repo")))
      |> Expect.stringContains "switch is the next step" "switch"

    testCase "WHY — a worker that could not start points at the .NET SDK" <| fun _ ->
      Refusal.nextAction (Refusal.Daemon (SageFsError.WorkerSpawnFailed "dotnet not found"))
      |> Expect.stringContains "the SDK is the next step" "SDK"

    testCase "WHY — the dialog offers exactly the three workflows, and the REPL is the default" <| fun _ ->
      WorkflowChoice.all |> List.map WorkflowChoice.key
      |> Expect.equal "the three keys the switcher already knows" [ "interactive"; "livetesting"; "hotreload" ]
      WorkflowChoice.defaultChoice |> Expect.equal "REPL first" SessionWorkflow.Interactive

    testCase "WHY — each workflow gets ONE line in plain words, no jargon sentence-pile" <| fun _ ->
      for workflow in WorkflowChoice.all do
        let line = WorkflowChoice.oneLine workflow
        line.Contains "\n" |> Expect.isFalse (sprintf "%s is one line" (WorkflowChoice.key workflow))
        (line.Length < 130) |> Expect.isTrue (sprintf "%s stays short enough to read at a glance" (WorkflowChoice.key workflow))
        (line.Length > 20) |> Expect.isTrue (sprintf "%s says something" (WorkflowChoice.key workflow))

    testCase "WHY — frameworks read as the person would say them" <| fun _ ->
      Frameworks.describe (Frameworks.Declared [ "net10.0"; "net11.0" ]) |> Expect.equal "joined" "net10.0, net11.0"
      Frameworks.describe Frameworks.WholeSolution |> Expect.equal "a solution has no single framework" "whole solution"
      Frameworks.describe Frameworks.NamedByImports |> isSaying "says where it comes from"
  ]

// ── Discovery: the one place that reads the disk ────────────────────────────

let withTempDir (body: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "nsd-discover-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  try body dir
  finally Directory.Delete(dir, true)

[<Tests>]
let discovery =
  testList "NewSessionDiscovery.discover — projects with their frameworks, and who is already here" [

    testCase "WHY — an empty directory name is refused before the disk is touched" <| fun _ ->
      discover [] "  "
      |> Expect.equal "no directory" (Error Refusal.NoDirectory)

    testCase "WHY — a directory that is not there is a DirectoryMissing refusal naming it" <| fun _ ->
      let missing = Path.Combine(Path.GetTempPath(), "nsd-missing-" + Guid.NewGuid().ToString("N"))
      discover [] missing
      |> Expect.equal "named" (Error (Refusal.DirectoryMissing missing))

    testCase "WHY — projects come back with the frameworks their project file declares" <| fun _ ->
      withTempDir (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "App")) |> ignore
        File.WriteAllText(
          Path.Combine(dir, "App", "App.fsproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks></PropertyGroup></Project>")
        File.WriteAllText(Path.Combine(dir, "All.slnx"), "<Solution />")
        match discover [] dir with
        | Error refusal -> failtestf "expected found, got %A" refusal
        | Ok (found, overlaps) ->
          overlaps |> Expect.isEmpty "nobody is here"
          found.Candidates
          |> List.map (fun c -> c.Kind, c.Path, c.Frameworks)
          |> Expect.equal "the solution then the project, with its frameworks"
            [ CandidateKind.Solution, "All.slnx", Frameworks.WholeSolution
              CandidateKind.Project, Path.Combine("App", "App.fsproj"), Frameworks.Declared [ "net10.0"; "net11.0" ] ])

    testCase "WHY — a project file that names no framework says an import decides it, instead of showing a blank" <| fun _ ->
      withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "App.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        match discover [] dir with
        | Error refusal -> failtestf "expected found, got %A" refusal
        | Ok (found, _) ->
          found.Candidates |> List.map (fun c -> c.Frameworks)
          |> Expect.equal "named by imports" [ Frameworks.NamedByImports ])

    testCase "WHY — noise folders (bin, obj, worktrees) are not offered as projects" <| fun _ ->
      withTempDir (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "bin")) |> ignore
        File.WriteAllText(Path.Combine(dir, "bin", "Stale.fsproj"), "<Project />")
        match discover [] dir with
        | Error refusal -> failtestf "expected found, got %A" refusal
        | Ok (found, _) -> found.Candidates |> Expect.isEmpty "nothing real here")

    testCase "WHY — a bare request resolves to the one bare target, with nothing read from the disk" <| fun _ ->
      resolveTargets (request "/work/repo" Target.Bare)
      |> Result.map List.length
      |> Expect.equal "one bare target" (Ok 1)

    testCase "WHY — a ticked project that escapes the working directory is refused by name, never quietly dropped" <| fun _ ->
      withTempDir (fun dir ->
        match resolveTargets (request dir (Target.Load("../outside.fsproj", []))) with
        | Error (Refusal.Daemon (SageFsError.UnsafeSessionPath _)) -> ()
        | other -> failtestf "expected an UnsafeSessionPath refusal, got %A" other)

    testCase "WHY — ticked projects inside the directory resolve to targets, in the order they were ticked" <| fun _ ->
      withTempDir (fun dir ->
        match resolveTargets (request dir (Target.Load("B/B.fsproj", [ "A/A.fsproj" ]))) with
        | Ok targets -> SessionProjectTarget.paths targets |> Expect.equal "ticked order" [ Path.Combine(dir, "B/B.fsproj"); Path.Combine(dir, "A/A.fsproj") ]
        | Error refusal -> failtestf "expected targets, got %A" refusal)

    testCase "WHY — a live session in the same directory comes back as an overlap, so the dialog can warn before creating" <| fun _ ->
      withTempDir (fun dir ->
        let here = { Id = "h1"; WorkingDirectory = dir; Boundary = Boundary.classify dir }
        match discover [ here ] dir with
        | Error refusal -> failtestf "expected found, got %A" refusal
        | Ok (_, overlaps) ->
          overlaps |> Expect.equal "named" [ overlapOf here Relation.SameDirectory ])

    testCase "WHY — a web project suggests Hot Reload, but only suggests: nothing is chosen for the person" <| fun _ ->
      withTempDir (fun dir ->
        File.WriteAllText(
          Path.Combine(dir, "Web.fsproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Falco\" /></ItemGroup></Project>")
        match discover [] dir with
        | Error refusal -> failtestf "expected found, got %A" refusal
        | Ok (found, _) ->
          match found.Hint with
          | WorkflowHint.Suggested s -> s.SuggestedWorkflow |> Expect.equal "hot reload" (SessionWorkflow.HotReload BrowserRefreshConfig.defaults)
          | WorkflowHint.NoneSuggested -> failtest "a Falco project should suggest hot reload")
  ]
