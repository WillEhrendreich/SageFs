/// What the cohort does with a landing that carries production source (P14, findings C6 to C8). The decisions are pure
/// (`CohortLandingPlan`, `ClaimExempt`), so they are proven here without a daemon; the lab test beside this one runs the
/// same decisions through real processes.
module SageFs.Tests.CohortLandingPlanTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.CohortLandingPlan

let private core : ProjectInfo =
  { Path = "Lab.Core/Lab.Core.fsproj"
    Role = ProjectRole.Production
    Compiles = [ "Lab.Core/Greeting.fs" ]
    References = [] }

let private tests' : ProjectInfo =
  { Path = "Lab.Tests/Lab.Tests.fsproj"
    Role = ProjectRole.Test
    Compiles = [ "Lab.Tests/Tests.fs"; "Lab.Tests/Program.fs" ]
    References = [ "Lab.Core/Lab.Core.fsproj" ] }

let private sample : ProjectInfo =
  { Path = "samples/Demo/Demo.fsproj"
    Role = ProjectRole.Production
    Compiles = [ "samples/Demo/Demo.fs" ]
    References = [] }

let private index = [ core; tests'; sample ]
let private loaded = [ "Lab.Tests/Lab.Tests.fsproj" ]
let private classified (path: string) = classify index loaded path ClaimExempt.isExemptPath

let private changedFileGen : Gen<ChangedFile> =
  Gen.elements
    [ ChangedFile.TestSource "T/Tests.fs"
      ChangedFile.CompiledSource("C/Core.fs", "C/C.fsproj")
      ChangedFile.BuildInput "C/C.fsproj"
      ChangedFile.Generated "docs/roadmap.md"
      ChangedFile.Other "README.md" ]

[<Tests>]
let tests =
  testList "CohortLandingPlan (P14): what a landing of production source is verified by" [

    testList "what a changed file is to the integration session" [
      test "a test project's own source is re-evaluated in place" {
        classified "Lab.Tests/Tests.fs" |> Expect.equal "test source" (ChangedFile.TestSource "Lab.Tests/Tests.fs")
      }

      test "source of a library the tests reference is compiled source: only a build puts its new text in the session" {
        classified "Lab.Core/Greeting.fs"
        |> Expect.equal "compiled source" (ChangedFile.CompiledSource("Lab.Core/Greeting.fs", "Lab.Core/Lab.Core.fsproj"))
      }

      test "a signature file of that library is compiled source too" {
        classified "Lab.Core/Greeting.fsi"
        |> Expect.equal "compiled source" (ChangedFile.CompiledSource("Lab.Core/Greeting.fsi", "Lab.Core/Lab.Core.fsproj"))
      }

      test "source of a project the session does not load, directly or through a reference, cannot change its build" {
        classified "samples/Demo/Demo.fs" |> Expect.equal "other" (ChangedFile.Other "samples/Demo/Demo.fs")
      }

      test "a project file in the loaded closure, and the files that decide what a build makes, are build inputs" {
        for path in [ "Lab.Core/Lab.Core.fsproj"; "Lab.Tests/Lab.Tests.fsproj"; "Directory.Packages.props"; "Directory.Build.props"; "global.json"; "Lab.Core/packages.lock.json"; "build/Common.targets" ] do
          classified path |> Expect.equal (sprintf "%s is a build input" path) (ChangedFile.BuildInput path)
      }

      test "documents and scripts cannot change what the build makes" {
        for path in [ "README.md"; "docs/mcp-tools.md"; "scripts/ship.fsx"; "Lab.Core/notes.txt" ] do
          classified path |> Expect.equal (sprintf "%s is other" path) (ChangedFile.Other path)
      }

      test "a generated page is named as one" {
        classified "docs/roadmap.md" |> Expect.equal "generated" (ChangedFile.Generated "docs/roadmap.md")
      }

      test "a path is read the same with backslashes and a leading ./" {
        classified @".\Lab.Core\Greeting.fs"
        |> Expect.equal "compiled source" (ChangedFile.CompiledSource("Lab.Core/Greeting.fs", "Lab.Core/Lab.Core.fsproj"))
      }

      test "FAIL-CLOSED: when no project could be read, F# source counts as compiled source" {
        classify [] loaded "Anything/File.fs" ClaimExempt.isExemptPath
        |> Expect.equal "compiled" (ChangedFile.CompiledSource("Anything/File.fs", "(unknown project)"))
      }

      test "FAIL-CLOSED: when the session's projects are unknown, every project counts as loaded" {
        classify index [] "samples/Demo/Demo.fs" ClaimExempt.isExemptPath
        |> Expect.equal "compiled" (ChangedFile.CompiledSource("samples/Demo/Demo.fs", "samples/Demo/Demo.fsproj"))
      }

      test "a file is owned by the project that compiles it by name, even from another folder" {
        let shared = { core with Compiles = [ "Lab.Core/Greeting.fs"; "Shared/Common.fs" ] }
        classify [ shared; tests' ] loaded "Shared/Common.fs" ClaimExempt.isExemptPath
        |> Expect.equal "owned by Lab.Core" (ChangedFile.CompiledSource("Shared/Common.fs", "Lab.Core/Lab.Core.fsproj"))
      }
    ]

    testList "reading a project file" [
      test "Compile and ProjectReference includes resolve against the project's folder, through .." {
        let xml =
          "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"A.fs\" /><Compile Include=\"..\\Shared\\B.fs\" /><Compile Include=\"*.fs\" /></ItemGroup>"
          + "<ItemGroup><ProjectReference Include=\"../Lab.Core/Lab.Core.fsproj\" /><PackageReference Include=\"Expecto\" Version=\"1\" /></ItemGroup></Project>"
        match parseProject "Lab.Tests/Lab.Tests.fsproj" xml with
        | None -> failtest "the project parses"
        | Some project ->
          project.Compiles |> Expect.equal "compiles, wildcard dropped" [ "Lab.Tests/A.fs"; "Shared/B.fs" ]
          project.References |> Expect.equal "references" [ "Lab.Core/Lab.Core.fsproj" ]
          project.Role |> Expect.equal "references Expecto, so it is a test project" ProjectRole.Test
      }

      test "text that is not XML is no project" {
        parseProject "X/X.fsproj" "not xml" |> Expect.isNone "no project"
      }

      test "the closure follows references through other projects" {
        let a = { core with Path = "A/A.fsproj"; References = [ "B/B.fsproj" ] }
        let b = { core with Path = "B/B.fsproj"; References = [ "C/C.fsproj" ] }
        let c = { core with Path = "C/C.fsproj"; References = [] }
        let lone = { core with Path = "D/D.fsproj" }
        closure [ a; b; c; lone ] [ "A/A.fsproj" ]
        |> Expect.equal "A, B, C and not D" (Set.ofList [ "A/A.fsproj"; "B/B.fsproj"; "C/C.fsproj" ])
      }

      test "a reference cycle ends" {
        let a = { core with Path = "A/A.fsproj"; References = [ "B/B.fsproj" ] }
        let b = { core with Path = "B/B.fsproj"; References = [ "A/A.fsproj" ] }
        closure [ a; b ] [ "A/A.fsproj" ] |> Expect.equal "both, once" (Set.ofList [ "A/A.fsproj"; "B/B.fsproj" ])
      }
    ]

    testList "the strategy" [
      test "test source and documents alone are re-evaluated over the build" {
        strategy None [ ChangedFile.TestSource "T/Tests.fs"; ChangedFile.Other "README.md"; ChangedFile.Generated "docs/roadmap.md" ]
        |> Expect.equal "cheap path" Strategy.EvalOverBuild
      }

      test "compiled source rebuilds, and the reason names the file" {
        match strategy None [ ChangedFile.TestSource "T/Tests.fs"; ChangedFile.CompiledSource("C/Core.fs", "C/C.fsproj") ] with
        | Strategy.RebuildAtHead [ RebuildReason.CompiledSourceChanged files ] -> files |> Expect.equal "the file" [ "C/Core.fs" ]
        | other -> failtestf "expected one rebuild reason, got %A" other
      }

      test "a session whose build was made from a commit that did not land rebuilds even for a test-only landing" {
        match strategy (Some "abc123") [ ChangedFile.TestSource "T/Tests.fs" ] with
        | Strategy.RebuildAtHead [ RebuildReason.SessionBuildIsOffHead "abc123" ] -> ()
        | other -> failtestf "expected the off-head reason, got %A" other
      }

      testProperty "a rebuild happens exactly when the session is off the head or a landing carries compiled source or a build input" <| fun (offHead: bool) ->
        Prop.forAll (Arb.fromGen (Gen.listOf changedFileGen)) (fun changed ->
          let carriesBuildChange = changed |> List.exists (function ChangedFile.CompiledSource _ | ChangedFile.BuildInput _ -> true | _ -> false)
          let decided = strategy (match offHead with true -> Some "x" | false -> None) changed
          let rebuilds = match decided with Strategy.RebuildAtHead _ -> true | Strategy.EvalOverBuild -> false
          rebuilds = (offHead || carriesBuildChange))

      test "the reasons read as sentences that name what caused them" {
        describeReason (RebuildReason.SessionBuildIsOffHead "abc")
        |> Expect.stringContains "names the commit" "abc"
        describeReason (RebuildReason.CompiledSourceChanged [ "A.fs"; "B.fs" ])
        |> Expect.stringContains "names the files" "A.fs, B.fs"
      }
    ]

    testList "where the integration worktree goes when nothing is in flight" [
      test "back to the head when it is elsewhere and the queue is empty" {
        idleResetTarget true "failedcommit" "head" |> Expect.equal "reset" (Some "head")
      }

      test "nowhere while a landing is queued: the next rebase moves it, and moving it under a verification would break that" {
        idleResetTarget false "failedcommit" "head" |> Expect.isNone "left alone"
      }

      test "nowhere when it is already at the head" {
        idleResetTarget true "head" "head" |> Expect.isNone "already there"
      }

      testProperty "the target is always the head, and only when the worktree is elsewhere and the queue is empty" <| fun (queueEmpty: bool) (at: NonEmptyString) (head: NonEmptyString) ->
        let target = idleResetTarget queueEmpty at.Get head.Get
        match target with
        | Some t -> t = head.Get && queueEmpty && at.Get <> head.Get
        | None -> (not queueEmpty) || at.Get = head.Get
    ]

    testList "a build the session holds, against the head's history" [
      test "a commit in the head's history is the head's build" {
        buildOffHead (fun c -> c = "landed") (Some "landed") |> Expect.isNone "on the head"
      }

      test "a commit outside it is off the head and is named" {
        buildOffHead (fun c -> c = "landed") (Some "refused") |> Expect.equal "off" (Some "refused")
      }

      test "a session that was never rebuilt is never off the head" {
        buildOffHead (fun _ -> false) None |> Expect.isNone "never rebuilt"
      }
    ]

    testList "what a refused build says" [
      let diagnostic file code message =
        { BuildDiagnostic.File = Some file; Line = Some 3; Column = Some 5; Severity = BuildDiagnosticSeverity.Blocking; Code = Some code; Message = message }

      test "the first compiler error and its file, not the last line printed" {
        let error =
          SageFsError.BuildFailed(1, [
            diagnostic "/w/Lab.Core/Greeting.fs" "FS0001" "This expression was expected to have type 'string'"
            diagnostic "/w/Lab.Core/Other.fs" "FS0039" "The value 'x' is not defined"
            { BuildDiagnostic.File = None; Line = None; Column = None; Severity = BuildDiagnosticSeverity.Blocking; Code = None; Message = "Build FAILED." } ])
        let said = describeBuildFailure error
        said |> Expect.stringContains "names the first file" "Greeting.fs(3,5)"
        said |> Expect.stringContains "quotes the first code" "FS0001"
        said.Contains "Other.fs" |> Expect.isFalse "does not list the second"
        said.Contains "Build FAILED" |> Expect.isFalse "does not echo the summary"
      }

      test "an MSBuild error with a file is preferred to a bare line when there is no FS code" {
        let error =
          SageFsError.BuildFailed(1, [
            { BuildDiagnostic.File = None; Line = None; Column = None; Severity = BuildDiagnosticSeverity.Blocking; Code = None; Message = "something bare" }
            diagnostic "/w/X.fsproj" "NU1101" "Unable to find package" ])
        describeBuildFailure error |> Expect.stringContains "the located one" "NU1101"
      }

      test "a failure that is not a compile failure is said as it is" {
        describeBuildFailure (SageFsError.SessionNotFound "s1")
        |> Expect.stringContains "says it did not run" "did not run"
      }
    ]
  ]

[<Tests>]
let claimExemptTests =
  testList "ClaimExempt (P14, C6): pages a script generates need no claim" [
    test "every generated page names its path and the command that regenerates it" {
      for page in ClaimExempt.all do
        ClaimExempt.path page |> Expect.stringContains "a docs path" "docs/"
        ClaimExempt.command page |> Expect.isNotEmpty "a command"
    }

    test "the closed list holds each case once" {
      ClaimExempt.all |> List.distinct |> List.length |> Expect.equal "distinct" (List.length ClaimExempt.all)
      ClaimExempt.all |> List.map ClaimExempt.path |> List.distinct |> List.length |> Expect.equal "distinct paths" (List.length ClaimExempt.all)
    }

    test "a page is found by its path however it is spelled" {
      for page in ClaimExempt.all do
        ClaimExempt.ofPath (ClaimExempt.path page) |> Expect.equal "plain" (Some page)
        ClaimExempt.ofPath ("./" + ClaimExempt.path page) |> Expect.equal "dot slash" (Some page)
        ClaimExempt.ofPath ((ClaimExempt.path page).Replace('/', '\\')) |> Expect.equal "backslashes" (Some page)
    }

    test "a file scope on a generated page is exempt, a project scope and any other file are not" {
      ClaimExempt.isExempt (Cohort.ClaimScope.File "docs/roadmap.md") |> Expect.isTrue "the roadmap"
      ClaimExempt.isExempt (Cohort.ClaimScope.File "docs/mcp-tools.md") |> Expect.isFalse "a page written by hand"
      ClaimExempt.isExempt (Cohort.ClaimScope.Project "docs/roadmap.md") |> Expect.isFalse "a project scope"
    }

    test "a diff that touches a page's inputs, or the page, names the page to regenerate" {
      ClaimExempt.pagesToRegenerate [ "scripts/roadmap/RoadmapItems.fs" ] |> Expect.equal "roadmap" [ ClaimExempt.Page.Roadmap ]
      ClaimExempt.pagesToRegenerate [ "SageFs.Core/Timeouts.fs" ] |> Expect.equal "configuration" [ ClaimExempt.Page.Configuration ]
      ClaimExempt.pagesToRegenerate [ "docs/configuration.md" ] |> Expect.equal "the page itself" [ ClaimExempt.Page.Configuration ]
      ClaimExempt.pagesToRegenerate [ "SageFs.Core/Json.fs" ] |> Expect.isEmpty "nothing generated from it"
    }

    test "a rebase conflict is settled by regeneration only when every conflicting file is a generated page" {
      ClaimExempt.conflictIsOnlyGeneratedPages [ "docs/roadmap.md"; "docs/configuration.md" ] |> Expect.isTrue "only pages"
      ClaimExempt.conflictIsOnlyGeneratedPages [ "docs/roadmap.md"; "SageFs.Core/Json.fs" ] |> Expect.isFalse "one is a file somebody wrote"
      ClaimExempt.conflictIsOnlyGeneratedPages [] |> Expect.isFalse "no files is no conflict to settle"
    }

    test "the answer to a claim on a page says it is generated, that none was taken, and the command" {
      let said = ClaimExempt.claimAnswer ClaimExempt.Page.Roadmap
      said |> Expect.stringContains "generated" "generated"
      said |> Expect.stringContains "no claim taken" "none was taken"
      said |> Expect.stringContains "the command" "gen-roadmap.fsx"
    }
  ]

[<Tests>]
let queueTextTests =
  testList "CohortStatusText (P14, C9): where a queued landing stands and what is in front of it" [
    let alice = MemberTable.MemberId.Minted "alice"
    let bob = MemberTable.MemberId.Minted "bob"
    let carol = MemberTable.MemberId.Minted "carol"
    let clock = DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
    let frameAfter (commands: Cohort.CohortCommand<MemberTable.MemberId> list) =
      let state =
        commands
        |> List.indexed
        |> List.fold
          (fun state (i, cmd) ->
            match Cohort.decide clock [| byte i |] state cmd with
            | Ok (next, _, _) -> next
            | Error err -> failwithf "unexpected refusal building the frame: %A" err)
          (Cohort.CohortState.empty ())
      Cohort.project { Seq = 0L<Measures.ledgerSeq>; State = state } [||]
    let scope = CohortScope.Machine
    let join who = Cohort.CohortCommand.Join(who, Cohort.JoinableRole.Implementer, None, scope)
    let land who sha = Cohort.CohortCommand.RequestLanding(who, [], [ sha ], "a change", scope)

    test "the front landing says so, and the ones behind it name what is in front" {
      let frame = frameAfter [ join alice; join bob; join carol; land alice "a1"; land bob "b1"; land carol "c1" ]
      let positions = frame.LandingQueuePosition
      let at (position: int) = Array.findIndex (fun p -> p = position) positions
      Features.CohortStatusText.queuePositionText frame (at 0) |> Expect.equal "the front" "front of queue"
      let second = Features.CohortStatusText.queuePositionText frame (at 1)
      second |> Expect.stringContains "position" "position 1 in queue"
      second |> Expect.stringContains "one ahead" "1 landing ahead"
      let (Cohort.LandingId frontId) = frame.LandingIds.[at 0]
      second |> Expect.stringContains "names the landing in front" frontId
      let third = Features.CohortStatusText.queuePositionText frame (at 2)
      third |> Expect.stringContains "two ahead" "2 landings ahead"
      let (Cohort.LandingId secondId) = frame.LandingIds.[at 1]
      third |> Expect.stringContains "names the landing directly ahead, not the front" secondId
    }

    test "the rendered status carries it on the landing's line" {
      let frame = frameAfter [ join alice; join bob; land alice "a1"; land bob "b1" ]
      Features.CohortStatusText.render frame |> Expect.stringContains "queue line" "position 1 in queue (1 landing ahead"
    }
  ]
