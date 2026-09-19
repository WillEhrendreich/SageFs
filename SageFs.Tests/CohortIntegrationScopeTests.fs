module SageFs.Tests.CohortIntegrationScopeTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.CohortIntegrationScope

/// Sanitize an arbitrary FsCheck string to a non-empty, non-whitespace path
/// segment — the properties below reason about "trusted" vs "blank" caller
/// directories, so a generated string must actually BE a real-looking path
/// (not accidentally empty/whitespace, which is its own tested case).
let private sanitizeDir (s: string) : string =
  let cleaned = s.Trim()
  if cleaned = "" then "seg" else cleaned

[<Tests>]
let mainRepoRootSourceTests =
  testList "CohortIntegrationScope.chooseMainRepoRootSource (F16)" [

    // --- The twin: proves the invariant has TEETH -------------------------
    //
    // `buggyChoose` freezes the pre-fix shape `setIntegrationRef` actually
    // shipped with: `Environment.CurrentDirectory` (the daemon PROCESS's own
    // cwd), unconditionally — see cohort-dogfood-findings.md F16 ("the
    // integration worktree scope follows the DAEMON's cwd, not the landing
    // repo"). It ignores the caller session entirely.
    let buggyChoose (_callerSessionWorkingDirectory: string option) (daemonCwd: string) : MainRepoRootSource =
      MainRepoRootSource.DaemonProcessCwd daemonCwd

    test "TWIN WITH TEETH: the CWD-only shape ignores a known caller session directory" {
      let callerDir = "/home/will/Work/SomeOtherRepo"
      let daemonCwd = "/home/will/Work/SageFs"
      buggyChoose (Some callerDir) daemonCwd
      |> Expect.equal
        "the twin reproduces F16: it reports the daemon's cwd even though a real caller session directory was given"
        (MainRepoRootSource.DaemonProcessCwd daemonCwd)
    }

    testProperty "TWIN WITH TEETH: whenever the caller dir differs from the daemon cwd, the CWD-only shape is wrong" <|
      fun (NonEmptyString callerRaw) (NonEmptyString daemonRaw) ->
        let callerDir = sanitizeDir callerRaw
        let daemonCwd = sanitizeDir daemonRaw
        (callerDir <> daemonCwd) ==>
          lazy (buggyChoose (Some callerDir) daemonCwd <> MainRepoRootSource.CallerSession callerDir)

    // --- The real function --------------------------------------------------

    testProperty "a known caller session directory always wins over the daemon cwd" <|
      fun (NonEmptyString callerRaw) (NonEmptyString daemonRaw) ->
        let callerDir = sanitizeDir callerRaw
        let daemonCwd = sanitizeDir daemonRaw
        chooseMainRepoRootSource (Some callerDir) daemonCwd = MainRepoRootSource.CallerSession callerDir

    testProperty "no caller session falls back to the daemon cwd" <| fun (NonEmptyString daemonRaw) ->
      let daemonCwd = sanitizeDir daemonRaw
      chooseMainRepoRootSource None daemonCwd = MainRepoRootSource.DaemonProcessCwd daemonCwd

    test "a blank/whitespace-only caller directory is never trusted — falls back to the daemon cwd" {
      chooseMainRepoRootSource (Some "   ") "/daemon/cwd"
      |> Expect.equal "whitespace is not a real path" (MainRepoRootSource.DaemonProcessCwd "/daemon/cwd")
    }

    testProperty "candidateDirectory round-trips whichever source won" <|
      fun (NonEmptyString callerRaw) (NonEmptyString daemonRaw) ->
        let callerDir = sanitizeDir callerRaw
        let daemonCwd = sanitizeDir daemonRaw
        candidateDirectory (chooseMainRepoRootSource (Some callerDir) daemonCwd) = callerDir
        && candidateDirectory (chooseMainRepoRootSource None daemonCwd) = daemonCwd
  ]

[<Tests>]
let selectIntegrationProjectsTests =
  testList "CohortIntegrationScope.selectIntegrationProjects (F6)" [

    // --- The twin: proves the invariant has TEETH -------------------------
    //
    // `buggyWalkOnly` freezes the pre-fix shape: always the unbounded
    // recursive directory walk, ignoring the repo's own curated `.slnx` —
    // see cohort-dogfood-findings.md F5/F6 ("28 missing DLLs" — sample/
    // demo/GUI/VS/vscode projects nothing declared as needed).
    let buggyWalkOnly (_declared: string list) (walked: string list) : string list = walked

    let declared = [ "SageFs.Core/SageFs.Core.fsproj"; "SageFs.Tests/SageFs.Tests.fsproj" ]
    let walked =
      declared @ [
        "samples/from-rust/SageFs.Samples.FromRust/SageFs.Samples.FromRust.fsproj"
        "SageFs.Gui/SageFs.Gui.fsproj"
      ]

    test "TWIN WITH TEETH: the walk-only shape pulls in projects the solution never declared" {
      buggyWalkOnly declared walked
      |> Expect.notEqual "the twin reproduces F6: it returns everything the walk found, not the declared set" declared
    }

    test "the real function is bounded by the solution's own declared set" {
      selectIntegrationProjects declared walked
      |> Expect.equal "a non-empty declared set wins outright — nothing the walk found beyond it is ever included" declared
    }

    testProperty "NO-EMPTY-ESCAPE: an empty/absent solution declaration falls back to the directory walk" <|
      fun (walked: string list) ->
        selectIntegrationProjects [] walked = walked

    testProperty "a non-empty declared set is never exceeded by the selection" <|
      fun (declared: string list) (extra: string list) ->
        not (List.isEmpty declared) ==>
          lazy (
            let walked = declared @ extra
            selectIntegrationProjects declared walked
            |> List.forall (fun p -> List.contains p declared))
  ]

[<Tests>]
let parseSlnxProjectPathsTests =
  testList "CohortIntegrationScope.parseSlnxProjectPaths (F6)" [

    test "malformed XML never throws — total function, returns []" {
      parseSlnxProjectPaths "not xml at all <<<"
      |> Expect.isEmpty "malformed input must fail closed to no declared set, never throw"
    }

    test "empty string returns []" {
      parseSlnxProjectPaths ""
      |> Expect.isEmpty "no content, no declared set"
    }

    test "parses this repo's real SageFs.slnx to a non-empty, curated project set" {
      let path = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.slnx")
      let xml = System.IO.File.ReadAllText path
      let projects = parseSlnxProjectPaths xml
      projects |> Expect.isNonEmpty "the repo's own .slnx must parse to a real project set"
      projects
      |> List.forall (fun p -> p.EndsWith(".fsproj"))
      |> Expect.isTrue "every parsed entry is an .fsproj path"
      // The 34-vs-15 gap the dogfood found: the curated solution set is a
      // strict subset of what an unbounded directory walk turns up.
      (projects.Length, 34) |> Expect.isLessThan "the .slnx set is the curated, SMALLER set (F6's whole point)"
    }

    test "only <Project Path=...> entries ending in .fsproj are kept — other project kinds are excluded" {
      let xml =
        """<Solution>
             <Project Path="A/A.fsproj" />
             <Project Path="B/B.csproj" />
             <Project Path="C/C.vbproj" />
           </Solution>"""
      parseSlnxProjectPaths xml
      |> Expect.equal "csproj/vbproj entries are not .fsproj projects" [ "A/A.fsproj" ]
    }

    testProperty "every declared project path always ends with .fsproj, whatever the input" <| fun (xml: string) ->
      parseSlnxProjectPaths xml |> List.forall (fun p -> p.EndsWith(".fsproj", System.StringComparison.OrdinalIgnoreCase))
  ]

[<Tests>]
let worktreeBuildOutcomeTests =
  testList "CohortIntegrationScope.worktreeBuildOutcome (F5)" [

    // --- The twin: proves the invariant has TEETH -------------------------
    //
    // `buggyAlwaysReady` freezes the pre-fix shape: there is NO build step
    // at all between worktree creation and `CreateSession` — every worktree
    // is treated as ready, whether or not it can actually build. This is
    // exactly cohort-dogfood-findings.md F5's mechanism.
    let buggyAlwaysReady (_buildResult: Result<string, string>) : WorktreeBuildOutcome =
      WorktreeBuildOutcome.ReadyForSession

    test "TWIN WITH TEETH: the no-build-step shape proceeds to session creation even on a failed build" {
      buggyAlwaysReady (Error "NU1101: missing package")
      |> Expect.equal "the twin reproduces F5: a build failure is silently treated as ready" WorktreeBuildOutcome.ReadyForSession
    }

    testProperty "TWIN WITH TEETH: whatever the failure reason, the no-build-step shape never surfaces it" <|
      fun (reason: string) ->
        buggyAlwaysReady (Error reason) = WorktreeBuildOutcome.ReadyForSession

    // --- The real function --------------------------------------------------

    testProperty "a failed build is NEVER classified as ready — fail-fast, the reason is preserved exactly" <|
      fun (reason: string) ->
        worktreeBuildOutcome (Error reason) = WorktreeBuildOutcome.BuildFailed reason

    testProperty "a successful build is always classified as ready for session creation" <| fun (output: string) ->
      worktreeBuildOutcome (Ok output) = WorktreeBuildOutcome.ReadyForSession
  ]
