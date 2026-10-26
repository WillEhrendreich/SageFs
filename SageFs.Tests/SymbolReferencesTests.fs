module SageFs.Tests.SymbolReferencesTests

/// Slice 3 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9): `list_references` states how
/// far the compiler looked on the first line of every reply, and no count appears without it. The scope is derived in
/// one place (`ReferenceReply.ofAnswer`), the budget is shared by every project of the session, and a failure names
/// the budget it used. Default tier, no session and no compiler: the compiler is a parameter of `ReferenceGather.run`.
open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features
open SageFs.McpReferencesPage

let private repoRoot = RepoPaths.requireRepoRoot ()

let private site (file: string) (line: int) (kind: ReferenceKind) : ReferenceSite =
  { File = file; Line = line; Column = 4; Kind = kind }

let private checkedProject (name: string) (sites: ReferenceSite list) : ProjectOutcome =
  ProjectOutcome.Checked(name, sites, [])

let private skippedProject (name: string) : ProjectOutcome =
  ProjectOutcome.Skipped { Project = name; Reason = ProjectSkip.BudgetSpent(Timeouts.referencesCheck, Timeouts.referencesCheck) }

let private reply (outcomes: ProjectOutcome list) : ReferenceReply = ReferenceReply.ofAnswer (ReferencesAnswer.Gathered outcomes)

let private rendered (cap: int) (offset: int) (r: ReferenceReply) : string = build "Mod.fn" cap offset r |> render

let private lines (text: string) : string list = text.Split('\n') |> List.ofArray

/// Outcomes built from a seed per project, so a property covers checked, skipped and failed projects of any count.
let private outcomesFromSeeds (seeds: int list) : ProjectOutcome list =
  seeds
  |> List.mapi (fun i seed ->
    let name = sprintf "P%d" i
    match abs seed % 3 with
    | 0 -> checkedProject name [ site (sprintf "/src/%s/a.fs" name) (1 + abs seed % 90) ReferenceKind.Use ]
    | 1 -> skippedProject name
    | _ -> ProjectOutcome.Skipped { Project = name; Reason = ProjectSkip.CompilerFailed "boom" })

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

[<Tests>]
let tests =
  testList "list_references scope" [

    testList "the scope is derived from what was checked" [
      testCase "WHY — every project checked is project-wide, and the scope names them all" <| fun _ ->
        match reply [ checkedProject "Core" [ site "/s/a.fs" 1 ReferenceKind.Use ]; checkedProject "Host" [] ] with
        | ReferenceReply.Counted(CountedScope.ProjectWide projects, sites, _) ->
          projects |> Expect.equal "both projects named" [ "Core"; "Host" ]
          sites.Length |> Expect.equal "the one use" 1
        | other -> failtestf "expected project-wide, got %A" other

      testCase "WHY — one skipped project makes the reply partial, never project-wide, and names what was not reached" <| fun _ ->
        match reply [ checkedProject "Core" [ site "/s/a.fs" 1 ReferenceKind.Use ]; skippedProject "Host" ] with
        | ReferenceReply.Counted(CountedScope.SomeProjects(checkedNames, skipped), _, _) ->
          checkedNames |> Expect.equal "only Core was reached" [ "Core" ]
          skipped |> List.map (fun s -> s.Project) |> Expect.equal "Host was not" [ "Host" ]
        | other -> failtestf "expected partial, got %A" other

      testCase "WHY — when no project was checked there is no count to attach a scope to" <| fun _ ->
        match reply [ skippedProject "Core"; skippedProject "Host" ] with
        | ReferenceReply.NotCounted(ReferenceUnavailable.NoProjectChecked skipped) -> skipped.Length |> Expect.equal "both named" 2
        | other -> failtestf "expected unavailable, got %A" other

      testCase "WHY — a session with no project is unavailable with that reason, not a count of zero" <| fun _ ->
        ReferenceReply.ofAnswer ReferencesAnswer.NoProjectLoaded
        |> Expect.equal "bare session" (ReferenceReply.NotCounted ReferenceUnavailable.NoProjectLoaded)

      testPropertyWithConfig simConfig
        "WHY — project-wide holds exactly when at least one project was checked and none was skipped"
        <| fun (seeds: int list) ->
          let outcomes = outcomesFromSeeds seeds
          let anySkipped = outcomes |> List.exists (fun o -> match o with | ProjectOutcome.Skipped _ -> true | ProjectOutcome.Checked _ -> false)
          let anyChecked = outcomes |> List.exists (fun o -> match o with | ProjectOutcome.Checked _ -> true | ProjectOutcome.Skipped _ -> false)
          match reply outcomes with
          | ReferenceReply.Counted(CountedScope.ProjectWide _, _, _) -> anyChecked && not anySkipped
          | ReferenceReply.Counted(CountedScope.SomeProjects _, _, _) -> anyChecked && anySkipped
          | ReferenceReply.NotCounted _ -> not anyChecked

      testCase "WHY — sites are merged across projects, deduplicated, and ordered by file then line" <| fun _ ->
        let a = site "/s/b.fs" 9 ReferenceKind.Use
        let b = site "/s/a.fs" 30 ReferenceKind.Definition
        let c = site "/s/a.fs" 2 ReferenceKind.Use
        match reply [ checkedProject "One" [ a; b ]; checkedProject "Two" [ c; a ] ] with
        | ReferenceReply.Counted(_, sites, _) -> sites |> Expect.equal "ordered once each" [ c; b; a ]
        | other -> failtestf "expected a count, got %A" other
    ]

    testList "the reply states its scope first" [
      testPropertyWithConfig simConfig
        "WHY — the first line of every reply is the scope line, whatever the outcomes, cap or offset"
        <| fun (seeds: int list) (cap: PositiveInt) (offset: int) ->
          let text = rendered cap.Get offset (reply (outcomesFromSeeds seeds))
          (lines text |> List.head).StartsWith "Scope: "

      testPropertyWithConfig simConfig
        "WHY — no count is printed above the scope line, and an unavailable reply prints no count at all"
        <| fun (seeds: int list) ->
          let r = reply (outcomesFromSeeds seeds)
          let text = rendered 100 0 r
          let beforeScope = lines text |> List.takeWhile (fun l -> not (l.StartsWith "Scope: "))
          let noCount = not (text.Contains " total (" || text.Contains " total, ")
          match r with
          | ReferenceReply.NotCounted _ -> List.isEmpty beforeScope && noCount
          | ReferenceReply.Counted _ -> List.isEmpty beforeScope

      testCase "WHY — a project-wide reply says so, with the projects it covered" <| fun _ ->
        let text = rendered 100 0 (reply [ checkedProject "Core" [ site "/s/a.fs" 3 ReferenceKind.Use ]; checkedProject "Host" [] ])
        (lines text |> List.head) |> Expect.stringStarts "scope line" "Scope: project-wide"
        text |> Expect.stringContains "names the projects" "Core, Host"

      testCase "WHY — a partial reply says so, with the count it covers and the project it did not reach" <| fun _ ->
        let text = rendered 100 0 (reply [ checkedProject "Core" [ site "/s/a.fs" 3 ReferenceKind.Use ]; skippedProject "Host" ])
        (lines text |> List.head) |> Expect.stringStarts "scope line" "Scope: partial"
        (lines text |> List.head) |> Expect.stringContains "names what was not reached" "Not checked: Host"

      testCase "WHY — a partial reply says to ask again, because the projects the compiler finished stay checked, and a whole one does not" <| fun _ ->
        let partial = rendered 100 0 (reply [ checkedProject "Core" [ site "/s/a.fs" 3 ReferenceKind.Use ]; skippedProject "Host" ])
        partial |> Expect.stringContains "asks again" "Ask again for the rest"
        let whole = rendered 100 0 (reply [ checkedProject "Core" [ site "/s/a.fs" 3 ReferenceKind.Use ] ])
        whole.Contains "Ask again" |> Expect.isFalse "nothing is left to ask for"

      testCase "WHY — the scope line never says project-wide when a project was skipped" <| fun _ ->
        let text = rendered 100 0 (reply [ checkedProject "Core" []; skippedProject "Host" ])
        (lines text |> List.head).Contains "project-wide" |> Expect.isFalse "a partial scope must not claim the whole project"
    ]

    testList "a failure states the budget it used" [
      testCase "WHY — a skipped project's line carries the budget and the time spent, from the constant, not a literal" <| fun _ ->
        let budget = Timeouts.referencesCheck
        let outcome = ProjectOutcome.Skipped { Project = "Core"; Reason = ProjectSkip.BudgetSpent(budget, budget) }
        let text = rendered 100 0 (reply [ outcome ])
        text |> Expect.stringContains "the budget in seconds" (sprintf "%.0fs" budget.TotalSeconds)
        text |> Expect.stringContains "says it was spent" "budget was spent"

      testCase "WHY — an unavailable reply says what to do about the budget, naming the variable that sets it" <| fun _ ->
        let text = rendered 100 0 (reply [ skippedProject "Core" ])
        text |> Expect.stringContains "the knob" "SAGEFS_REFERENCES_CHECK_SECONDS"

      testCase "WHY — projects skipped for the same reason share one sentence, so eight projects do not repeat it eight times" <| fun _ ->
        let text = rendered 100 0 (reply [ for i in 1 .. 8 -> skippedProject (sprintf "P%d" i) ])
        let scope = lines text |> List.head
        scope |> Expect.stringContains "all eight named" "P1, P2, P3, P4, P5, P6, P7, P8"
        (scope.Split("budget was spent").Length - 1) |> Expect.equal "the reason is stated once" 1

      testCase "WHY — a compiler failure is named with its detail, not turned into an empty list" <| fun _ ->
        let outcome = ProjectOutcome.Skipped { Project = "Core"; Reason = ProjectSkip.CompilerFailed "FS0001 bad" }
        rendered 100 0 (reply [ outcome ]) |> Expect.stringContains "the detail" "FS0001 bad"

      testCase "WHY — referencesCheck is a bounded, positive budget under the daemon's request ceiling" <| fun _ ->
        (Timeouts.referencesCheck > TimeSpan.Zero) |> Expect.isTrue "positive"
        (Timeouts.referencesCheck < Timeouts.workerHttpRequest) |> Expect.isTrue "the worker answers before the daemon's HTTP request gives up"
    ]

    testList "the reply is bounded by construction" [
      testCase "WHY — 500 references are capped at the configured page and the total is stated before the names" <| fun _ ->
        let sites = [ for i in 1 .. 500 -> site (sprintf "/s/f%03d.fs" (i / 5)) i ReferenceKind.Use ]
        let text = rendered SageFsConfig.ListReferencesMax 0 (reply [ checkedProject "Core" sites ])
        text |> Expect.stringContains "the total" "500 total"
        let siteLines = lines text |> List.filter (fun l -> l.StartsWith "  " && l.Contains " use")
        siteLines.Length |> Expect.equal "capped" SageFsConfig.ListReferencesMax
        text |> Expect.stringContains "the footer names the next offset" (sprintf "offset=%d" SageFsConfig.ListReferencesMax)

      testPropertyWithConfig simConfig
        "WHY — following the footer's offsets visits every reference exactly once"
        <| fun (count: PositiveInt) (cap: PositiveInt) ->
          let n = 1 + count.Get % 300
          let size = 1 + cap.Get % 40
          let sites = [ for i in 1 .. n -> site "/s/a.fs" i ReferenceKind.Use ]
          let r = reply [ checkedProject "Core" sites ]
          let rec walk offset seen =
            match build "X" size offset r with
            | Page.Counted c ->
              let seen' = seen @ (c.Groups |> List.collect snd |> List.map (fun s -> s.Line))
              (match c.Continuation with
               | NextPage(next, _) -> walk next seen'
               | LastPage -> seen')
            | Page.Uncounted _ -> seen
          walk 0 [] = [ 1 .. n ]

      testCase "WHY — a name with no references says none were found in the checked projects and says where to look next" <| fun _ ->
        let text = rendered 100 0 (reply [ checkedProject "Core" [] ])
        text |> Expect.stringContains "none found" "none found in the checked projects"
        text |> Expect.stringContains "points at list_members" "list_members"

      testCase "WHY — files that did not check cleanly are named, because a use inside one may be missing" <| fun _ ->
        let r = ReferenceReply.Counted(CountedScope.ProjectWide [ "Core" ], [ site "/s/a.fs" 1 ReferenceKind.Use ], [ "/s/broken.fs" ])
        rendered 100 0 r |> Expect.stringContains "the file" "did not check cleanly"

      testCase "WHY — files are shown relative to the directory they share" <| fun _ ->
        let r = reply [ checkedProject "Core" [ site "/work/repo/src/a.fs" 1 ReferenceKind.Use; site "/work/repo/src/b.fs" 2 ReferenceKind.Use ] ]
        let text = rendered 100 0 r
        text |> Expect.stringContains "the root" "Files are relative to /work/repo/src."
        text |> Expect.stringContains "a short path" "\na.fs:"
    ]

    testList "the gather shares one budget" [
      let gather (budget: float) (costs: (string * float) list) =
        let clock = ref TimeSpan.Zero
        let check (project: string) (left: TimeSpan) =
          async {
            let cost = TimeSpan.FromSeconds(costs |> List.find (fun (p, _) -> p = project) |> snd)
            match cost <= left with
            | true ->
              clock.Value <- clock.Value + cost
              return ProjectCheck.Answered([ site (project + ".fs") 1 ReferenceKind.Use ], [])
            | false ->
              clock.Value <- clock.Value + left
              return ProjectCheck.CutOff
          }
        ReferenceGather.run (TimeSpan.FromSeconds budget) (fun () -> clock.Value) check (costs |> List.map fst)
        |> Async.StartAsTask

      testTask "WHY — projects that fit in the budget together are all checked" {
        let! answer = gather 60.0 [ "A", 20.0; "B", 20.0 ]
        match ReferenceReply.ofAnswer answer with
        | ReferenceReply.Counted(CountedScope.ProjectWide projects, _, _) -> projects |> Expect.equal "both" [ "A"; "B" ]
        | other -> failtestf "expected project-wide, got %A" other
      }

      testTask "WHY — the budget is shared: the project that runs it out and every project after it are not checked" {
        let! answer = gather 60.0 [ "A", 40.0; "B", 40.0; "C", 5.0 ]
        match ReferenceReply.ofAnswer answer with
        | ReferenceReply.Counted(CountedScope.SomeProjects(checkedNames, skipped), _, _) ->
          checkedNames |> Expect.equal "only A fit" [ "A" ]
          skipped |> List.map (fun s -> s.Project) |> Expect.equal "B ran out, C never started" [ "B"; "C" ]
        | other -> failtestf "expected partial, got %A" other
      }

      testCase "WHY — the smallest project is tried first, so one budget reaches the most projects and the giant is left for the next ask" <| fun _ ->
        let project (name: string) (files: int) : FSharp.Compiler.CodeAnalysis.FSharpProjectOptions =
          { ProjectFileName = name + ".fsproj"
            ProjectId = None
            SourceFiles = Array.init files (fun i -> sprintf "%s/f%d.fs" name i)
            OtherOptions = [||]
            ReferencedProjects = [||]
            IsIncompleteTypeCheckEnvironment = false
            UseScriptResolutionRules = false
            LoadTime = DateTime.UnixEpoch
            UnresolvedReferences = None
            OriginalLoadReferences = []
            Stamp = None }
        [ project "Tests" 900; project "Fixture" 2; project "Core" 330 ]
        |> SymbolReferenceCheck.inBudgetOrder
        |> List.map SymbolReferenceCheck.projectNameOf
        |> Expect.equal "smallest first" [ "Fixture"; "Core"; "Tests" ]

      testTask "WHY — no projects is a bare session, not an empty project-wide answer" {
        let! answer = gather 60.0 []
        answer |> Expect.equal "nothing to check" ReferencesAnswer.NoProjectLoaded
      }
    ]

    testList "the wire" [
      testCase "WHY — a references answer survives the worker wire unchanged, scope inputs and all" <| fun _ ->
        let answer =
          ReferencesAnswer.Gathered
            [ ProjectOutcome.Checked("Core", [ site "/s/a.fs" 3 ReferenceKind.Definition; site "/s/b.fs" 9 ReferenceKind.Use ], [ "/s/x.fs" ])
              ProjectOutcome.Skipped { Project = "Host"; Reason = ProjectSkip.BudgetSpent(Timeouts.referencesCheck, Timeouts.referencesCheck + TimeSpan.FromTicks 15L) }
              ProjectOutcome.Skipped { Project = "Gui"; Reason = ProjectSkip.CompilerFailed "boom" } ]
        let json = WorkerProtocol.Serialization.serialize (WorkerProtocol.WorkerResponse.ReferencesResult("r1", answer))
        match WorkerProtocol.Serialization.tryDeserialize<WorkerProtocol.WorkerResponse> json with
        | Result.Ok(WorkerProtocol.WorkerResponse.ReferencesResult("r1", back)) -> back |> Expect.equal "same answer" answer
        | other -> failtestf "did not round trip: %A" other

      testCase "WHY — a reply that is not a references answer is an unreachable session with the reason, never a count" <| fun _ ->
        match McpReferencesRoute.replyOf (Result.Ok WorkerProtocol.WorkerResponse.WorkerReady) with
        | ReferenceReply.NotCounted(ReferenceUnavailable.SessionUnreachable why) -> why |> Expect.stringContains "names what came back" "WorkerReady"
        | other -> failtestf "expected unreachable, got %A" other
    ]

    testList "the skill teaches the rung" [
      let skillText =
        Directory.GetFiles(Path.Combine(repoRoot, "skills", "sagefs"), "*.md")
        |> Array.sort
        |> Array.map (fun path -> Path.GetFileName path, File.ReadAllText path)
        |> List.ofArray

      testCase "WHY — some skill file names list_references, so an agent that reads it knows the rung exists" <| fun _ ->
        skillText
        |> List.filter (fun (_, text) -> text.Contains "list_references")
        |> List.map fst
        |> Expect.isNonEmpty "no file under skills/sagefs mentions list_references"

      testCase "WHY — the skill that teaches list_references also teaches reading its scope line before trusting a count" <| fun _ ->
        skillText
        |> List.filter (fun (_, text) -> text.Contains "list_references" && text.Contains "Scope:")
        |> List.map fst
        |> Expect.isNonEmpty "the file that teaches list_references must name the Scope: line"
    ]
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
