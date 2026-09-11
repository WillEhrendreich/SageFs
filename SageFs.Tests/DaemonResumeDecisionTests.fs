module SageFs.Tests.DaemonResumeDecisionTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features.DaemonManifest

let private root = Path.Combine(Path.GetTempPath(), "sagefs-resume-decision")
let private app = Path.Combine(root, "App")

let private record (projects: string list) : DaemonSessionRecord =
  { SessionId = "aa000001"
    Projects = projects
    WorkingDir = app
    CreatedAt = DateTimeOffset.UnixEpoch
    StoppedAt = None }

/// A file system where only the given directories and files exist.
let private decideWith (dirs: string list) (files: string list) (r: DaemonSessionRecord) =
  ResumeDecision.decide (fun d -> List.contains d dirs) (fun f -> List.contains f files) r

[<Tests>]
let tests =
  testList "Daemon resume decision" [
    testCase "WHY — ResumeDecision.decide — a deleted directory is forgotten because retrying it every start only repeats the warning" <| fun _ ->
      match decideWith [] [] (record [ "App.fsproj" ]) with
      | ResumeDecision.Forget reason -> reason.Contains app |> Expect.isTrue "the reason names the directory"
      | other -> failwithf "expected Forget, got %A" other

    testCase "WHY — ResumeDecision.decide — a session whose projects were all deleted is forgotten because it can never start again" <| fun _ ->
      match decideWith [ app ] [] (record [ "App.fsproj"; "Lib.fsproj" ]) with
      | ResumeDecision.Forget reason ->
        (reason.Contains "App.fsproj" && reason.Contains "Lib.fsproj") |> Expect.isTrue "the reason names the missing projects"
      | other -> failwithf "expected Forget, got %A" other

    testCase "WHY — ResumeDecision.decide — a deleted project is dropped and the rest resume because one removed project must not cost the whole session" <| fun _ ->
      decideWith [ app ] [ Path.Combine(app, "App.fsproj") ] (record [ "App.fsproj"; "Old.fsproj" ])
      |> Expect.equal "resumes the project that still exists" (ResumeDecision.Resume [ "App.fsproj" ])

    testCase "WHY — ResumeDecision.decide — a session whose projects all exist resumes unchanged" <| fun _ ->
      let projects = [ Path.Combine(app, "App.fsproj"); "Lib.fsproj" ]
      decideWith [ app ] [ Path.Combine(app, "App.fsproj"); Path.Combine(app, "Lib.fsproj") ] (record projects)
      |> Expect.equal "resumes every project, rooted or relative" (ResumeDecision.Resume projects)

    testCase "WHY — ResumeDecision.decide — a session without projects resumes in its directory because a bare session has nothing to lose" <| fun _ ->
      decideWith [ app ] [] (record [])
      |> Expect.equal "resumes bare" (ResumeDecision.Resume [])
  ]
