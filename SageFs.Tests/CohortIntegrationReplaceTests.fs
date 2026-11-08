/// Replacing the cohort's integration worktree (P16, roast 2026-11-08 critical 2).
///
/// `set_integration_ref` used to stop the verifying session and delete the worktree under it, without taking the gate and
/// without looking at the landing queue, and it ran git in the NEW call's repository against a path that belongs to another.
/// The pure decisions are proven here without a daemon; the order of the steps is proven with fake ports and then against
/// real git repositories; the lab test (`CohortLandingSourceLabTests`) runs the whole thing through real processes.
module SageFs.Tests.CohortIntegrationReplaceTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.CohortIntegrationScope

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private labelGen : Gen<string> =
  Gen.elements [ "repo:/work/a"; "repo:/work/b"; "repo:/work/repo-a"; "repo:/work/repo-b"; "repo:/home/will/Work/SageFs"; "named:/tmp/x y"; "machine" ]

let private binding (repository: string) (path: string) (session: McpCohortIntegration.IntegrationSession) : McpCohortIntegration.CohortIntegrationBinding =
  { Scope = CohortScope.Repository repository
    Repository = repository
    WorktreePath = path
    Branch = "sagefs/cohort-abcdef12"
    TrunkPath = path + "-trunk"
    Session = session }

[<Tests>]
let pureTests =
  testList "CohortIntegration replace (P16): what may be replaced, and where each worktree lives" [

    testList "the decision" [
      test "an empty queue allows it" {
        replacementDecision [] |> Expect.equal "nothing in front" Replacement.Allowed
      }

      testProperty "any landing in the queue forbids it and the refusal names every one of them" <| fun (ids: NonEmptyString list) ->
        let landings = ids |> List.map (fun i -> i.Get)
        match landings, replacementDecision landings with
        | [], Replacement.Allowed -> true
        | _ :: _, Replacement.Refused named -> named = landings
        | _ -> false

      test "the refusal names the landings, why, and the two ways out" {
        let said = replacementRefusal "repo:/work/repo-a" [ "land-1"; "land-2" ]
        said |> Expect.stringContains "names the first landing" "land-1"
        said |> Expect.stringContains "names the second landing" "land-2"
        said |> Expect.stringContains "names the cohort" "repo:/work/repo-a"
        said |> Expect.stringContains "says to withdraw" "withdraw_landing"
        said |> Expect.stringContains "says waiting works too" "land"
      }
    ]

    testList "one worktree path per cohort" [
      testProperty "two cohorts never share a worktree path, and one cohort always gets the same one" <| fun () ->
        let labels = [ "repo:/work/a"; "repo:/work/b"; "repo:/work/repo-a"; "repo:/work/repo-b"; "machine"; "named:/tmp/x y" ]
        let names = labels |> List.map integrationWorktreeName
        List.distinct names |> List.length = List.length labels
        && labels |> List.forall (fun l -> integrationWorktreeName l = integrationWorktreeName l)

      testProperty "the name is one path segment, under a stable prefix" <| fun () ->
        Prop.forAll (Arb.fromGen labelGen) (fun label ->
          let name = integrationWorktreeName label
          name.StartsWith "cohort-integration"
          && not (name.Contains '/')
          && not (name.Contains '\\')
          && not (name.Contains ':'))

      test "the trunk checkout is a path of its own, per cohort" {
        let label = "repo:/work/repo-a"
        trunkCheckoutName label |> Expect.notEqual "not the integration worktree" (integrationWorktreeName label)
        trunkCheckoutName label |> Expect.stringStarts "a stable prefix" "cohort-trunk"
        trunkCheckoutName label |> Expect.notEqual "another cohort has another" (trunkCheckoutName "repo:/work/repo-b")
      }

      test "the path under the data dir is derived from the cohort" {
        let a = McpCohortIntegration.integrationWorktreePathFor "/data" (CohortScope.Repository "/work/repo-a")
        let b = McpCohortIntegration.integrationWorktreePathFor "/data" (CohortScope.Repository "/work/repo-b")
        a |> Expect.notEqual "two repositories, two paths" b
        a |> Expect.stringStarts "under the data dir" "/data/"
      }
    ]

    testList "where each old worktree is removed from" [
      test "with no previous integration only the new path is considered, from the new repository" {
        removalTargets None "/work/repo-b" "/data/wt-b"
        |> Expect.equal "the new path" [ "/work/repo-b", "/data/wt-b" ]
      }

      test "a previous integration's worktree is removed from the repository that made it, never from the new one" {
        let previous = Some { Repository = "/work/repo-a"; WorktreePath = "/data/wt-a" }
        let targets = removalTargets previous "/work/repo-b" "/data/wt-b"
        targets |> Expect.contains "the old worktree from the old repository" ("/work/repo-a", "/data/wt-a")
        List.contains ("/work/repo-b", "/data/wt-a") targets |> Expect.isFalse "the old path is never given to the new repository"
      }

      test "a retry for the same repository removes the one worktree once, from its repository" {
        let previous = Some { Repository = "/work/repo-a"; WorktreePath = "/data/wt-a" }
        removalTargets previous "/work/repo-a" "/data/wt-a"
        |> Expect.equal "one target" [ "/work/repo-a", "/data/wt-a" ]
      }

      testProperty "whatever the paths, the previous worktree is only ever removed from its own repository" <| fun (a: NonEmptyString) (b: NonEmptyString) (pa: NonEmptyString) (pb: NonEmptyString) ->
        let previous = { Repository = a.Get; WorktreePath = pa.Get }
        removalTargets (Some previous) b.Get pb.Get
        |> List.forall (fun (repository, path) -> path <> previous.WorktreePath || repository = previous.Repository)
    ]

    testList "the advice for a cohort that is not the one the integration serves" [
      test "it says what moving does: the other cohort's session stops, its worktree goes, and landings queued there forbid it" {
        let said = moveAdvice "repo:/work/repo-a"
        said |> Expect.stringContains "names the cohort it takes the integration from" "repo:/work/repo-a"
        said |> Expect.stringContains "says the session stops" "stops"
        said |> Expect.stringContains "says it is refused while landings are queued" "refused while"
        said |> Expect.stringContains "names the way out" "withdraw_landing"
        said |> Expect.stringContains "names the call" "set_integration_ref"
      }
    ]
  ]

type private Recorder() =
  member val Calls = List<string>() with get

let private portsOver (recorder: Recorder) (landings: string list) (removal: string -> string -> Result<unit, string>) : McpCohortIntegration.ReplacePorts =
  { LandingsInFront = fun () -> landings
    StopSession = fun sid -> task { recorder.Calls.Add(sprintf "stop %s" sid) }
    RemoveWorktree =
      fun repo path ->
        async {
          recorder.Calls.Add(sprintf "remove %s from %s" path repo)
          return removal repo path
        }
    DirectoryExists = fun _ -> true }

[<Tests>]
let orderTests =
  testList "CohortIntegration replace (P16): the order of the steps, over fake ports" [

    testTask "a landing in the queue refuses the call before anything is stopped or removed" {
      let recorder = Recorder()
      let previous = binding "/work/repo-a" "/data/wt-a" (McpCohortIntegration.IntegrationSession.Started "s1")
      let! outcome = McpCohortIntegration.replaceIntegrationWorktree (portsOver recorder [ "land-7" ] (fun _ _ -> Ok ())) (Some previous) "/work/repo-a" "/data/wt-a"
      match outcome with
      | McpCohortIntegration.ReplaceOutcome.Refused text ->
        text |> Expect.stringContains "names the landing" "land-7"
        text |> Expect.stringContains "names the way out" "withdraw_landing"
      | other -> failtestf "expected a refusal, got %A" other
      recorder.Calls |> Seq.toList |> Expect.isEmpty "nothing was stopped or removed"
    }

    testTask "an empty queue stops the verifying session first, then removes the old worktree from its own repository" {
      let recorder = Recorder()
      let previous = binding "/work/repo-a" "/data/wt-a" (McpCohortIntegration.IntegrationSession.Started "s1")
      let ports = { portsOver recorder [] (fun _ _ -> Ok ()) with DirectoryExists = fun path -> path = "/data/wt-a" }
      let! outcome = McpCohortIntegration.replaceIntegrationWorktree ports (Some previous) "/work/repo-b" "/data/wt-b"
      outcome |> Expect.equal "replaced" McpCohortIntegration.ReplaceOutcome.Replaced
      recorder.Calls |> Seq.toList |> Expect.equal "stop, then remove from repository A" [ "stop s1"; "remove /data/wt-a from /work/repo-a" ]
    }

    testTask "a failed removal is an error that names the path and what git said, never swallowed" {
      let recorder = Recorder()
      let previous = binding "/work/repo-a" "/data/wt-a" (McpCohortIntegration.IntegrationSession.Failed "x")
      let! outcome =
        McpCohortIntegration.replaceIntegrationWorktree
          (portsOver recorder [] (fun _ _ -> Error "fatal: '/data/wt-a' contains modified or untracked files"))
          (Some previous) "/work/repo-a" "/data/wt-a"
      match outcome with
      | McpCohortIntegration.ReplaceOutcome.RemovalFailed text ->
        text |> Expect.stringContains "names the path" "/data/wt-a"
        text |> Expect.stringContains "quotes git" "contains modified or untracked files"
      | other -> failtestf "expected RemovalFailed, got %A" other
    }

    testTask "no previous integration and no directory on disk removes nothing" {
      let recorder = Recorder()
      let ports = { portsOver recorder [] (fun _ _ -> Ok ()) with DirectoryExists = fun _ -> false }
      let! outcome = McpCohortIntegration.replaceIntegrationWorktree ports None "/work/repo-a" "/data/wt-a"
      outcome |> Expect.equal "replaced" McpCohortIntegration.ReplaceOutcome.Replaced
      recorder.Calls |> Seq.toList |> Expect.isEmpty "nothing to remove"
    }
  ]

// ── Real git ─────────────────────────────────────────────────────────────────────────────────────────────────

let private git (dir: string) (args: string list) : string =
  let psi = ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir)
  for a in args do psi.ArgumentList.Add a
  SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
  use proc = Process.Start psi
  let out = proc.StandardOutput.ReadToEnd()
  let err = proc.StandardError.ReadToEnd()
  proc.WaitForExit()
  match proc.ExitCode with
  | 0 -> out.Trim()
  | code -> failwithf "git %s in %s exited %d: %s" (String.Join(' ', args)) dir code err

let private initRepo (dir: string) : unit =
  Directory.CreateDirectory dir |> ignore
  git dir [ "init"; "--quiet"; "-b"; "main" ] |> ignore
  git dir [ "config"; "user.email"; "replace-lab@example.com" ] |> ignore
  git dir [ "config"; "user.name"; "Replace Lab" ] |> ignore
  File.WriteAllText(Path.Combine(dir, "a.txt"), "1")
  git dir [ "add"; "-A" ] |> ignore
  git dir [ "commit"; "--quiet"; "-m"; "base" ] |> ignore

let private realPorts (landings: string list) : McpCohortIntegration.ReplacePorts =
  { LandingsInFront = fun () -> landings
    StopSession = fun _ -> task { () }
    RemoveWorktree = Features.CohortGit.removeWorktree
    DirectoryExists = Directory.Exists }

let private worktreesOf (repo: string) : string list =
  git repo [ "worktree"; "list"; "--porcelain" ]
  |> fun text -> text.Split('\n') |> Array.filter (fun l -> l.StartsWith "worktree ") |> Array.map (fun l -> Path.GetFullPath(l.Substring 9)) |> Array.toList

[<Tests>]
let gitTests =
  Integration.hostList "CohortIntegration replace (P16): against real git repositories" [

    testTask "moving the integration from repository A to B removes A's worktree from A, and B gets a path of its own" {
      let world = Directory.CreateTempSubdirectory("replace-git-").FullName
      try
        let repoA = Path.Combine(world, "repo-a")
        let repoB = Path.Combine(world, "repo-b")
        initRepo repoA
        initRepo repoB
        let scopeA = CohortScope.Repository repoA
        let scopeB = CohortScope.Repository repoB
        let pathA = McpCohortIntegration.integrationWorktreePathFor world scopeA
        let pathB = McpCohortIntegration.integrationWorktreePathFor world scopeB
        pathA |> Expect.notEqual "two repositories, two paths" pathB
        match! Features.CohortGit.addWorktree repoA pathA "sagefs/cohort-a" "main" with
        | Error e -> failtestf "could not cut A's worktree: %s" e
        | Ok () -> ()
        let previous = binding repoA pathA (McpCohortIntegration.IntegrationSession.Started "s1")
        let! outcome = McpCohortIntegration.replaceIntegrationWorktree (realPorts []) (Some previous) repoB pathB
        outcome |> Expect.equal "replaced" McpCohortIntegration.ReplaceOutcome.Replaced
        Directory.Exists pathA |> Expect.isFalse "A's worktree is gone from disk"
        worktreesOf repoA |> List.contains (Path.GetFullPath pathA) |> Expect.isFalse "and from A's registry"
        match! Features.CohortGit.addWorktree repoB pathB "sagefs/cohort-b" "main" with
        | Error e -> failtestf "B's worktree could not be cut beside it: %s" e
        | Ok () -> Directory.Exists pathB |> Expect.isTrue "B's worktree exists"
      finally
        try Directory.Delete(world, true) with _ -> ()
    }

    testTask "the pre-fix call ran git in the new repository against the old path, and git refused" {
      let world = Directory.CreateTempSubdirectory("replace-git-wrong-").FullName
      try
        let repoA = Path.Combine(world, "repo-a")
        let repoB = Path.Combine(world, "repo-b")
        initRepo repoA
        initRepo repoB
        let pathA = Path.Combine(world, "shared-path")
        match! Features.CohortGit.addWorktree repoA pathA "sagefs/cohort-a" "main" with
        | Error e -> failtestf "could not cut A's worktree: %s" e
        | Ok () -> ()
        match! Features.CohortGit.removeWorktree repoB pathA with
        | Ok () -> failtest "git removed a worktree of another repository"
        | Error said -> said |> Expect.isNotEmpty "git says why"
        Directory.Exists pathA |> Expect.isTrue "A's worktree is still there: nothing read the refusal"
      finally
        try Directory.Delete(world, true) with _ -> ()
    }

    testTask "a directory that git does not know as a worktree fails the replacement with the path and git's message" {
      let world = Directory.CreateTempSubdirectory("replace-git-stray-").FullName
      try
        let repoA = Path.Combine(world, "repo-a")
        initRepo repoA
        let stray = Path.Combine(world, "stray-worktree")
        Directory.CreateDirectory stray |> ignore
        File.WriteAllText(Path.Combine(stray, "x.txt"), "x")
        let previous = binding repoA stray (McpCohortIntegration.IntegrationSession.Failed "x")
        let! outcome = McpCohortIntegration.replaceIntegrationWorktree (realPorts []) (Some previous) repoA stray
        match outcome with
        | McpCohortIntegration.ReplaceOutcome.RemovalFailed text -> text |> Expect.stringContains "names the path" stray
        | other -> failtestf "expected RemovalFailed, got %A" other
        Directory.Exists stray |> Expect.isTrue "nothing of the person's was deleted"
      finally
        try Directory.Delete(world, true) with _ -> ()
    }

    testTask "a landing in the queue leaves the worktree on disk and in git's registry" {
      let world = Directory.CreateTempSubdirectory("replace-git-queued-").FullName
      try
        let repoA = Path.Combine(world, "repo-a")
        initRepo repoA
        let pathA = McpCohortIntegration.integrationWorktreePathFor world (CohortScope.Repository repoA)
        match! Features.CohortGit.addWorktree repoA pathA "sagefs/cohort-a" "main" with
        | Error e -> failtestf "could not cut A's worktree: %s" e
        | Ok () -> ()
        let previous = binding repoA pathA (McpCohortIntegration.IntegrationSession.Started "s1")
        let! outcome = McpCohortIntegration.replaceIntegrationWorktree (realPorts [ "land-1" ]) (Some previous) repoA pathA
        match outcome with
        | McpCohortIntegration.ReplaceOutcome.Refused _ -> ()
        | other -> failtestf "expected a refusal, got %A" other
        Directory.Exists pathA |> Expect.isTrue "the worktree is still on disk"
        worktreesOf repoA |> List.contains (Path.GetFullPath pathA) |> Expect.isTrue "and registered"
      finally
        try Directory.Delete(world, true) with _ -> ()
    }
  ]
