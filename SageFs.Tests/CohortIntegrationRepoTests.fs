/// The cohort's integration is configured for a REPOSITORY, not for the daemon's own directory (P11, finding F16).
///
/// `sagefs install-local` starts the daemon with its install folder as the working directory, so the daemon's own cohort
/// is the one for that folder and nobody is seated in it. `set_integration_ref` took no `working_directory`, acted in
/// that cohort, and refused the real conductor with "your role is Working". These tests pin the pieces that fix it,
/// each with the pure decision first and the wire second: which repository a call means, which cohort an integration
/// serves, what a refusal says when the caller simply is not seated where the call acted, and that every tool the
/// conductor runs names the cohort it acts in.
module SageFs.Tests.CohortIntegrationRepoTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Cohort
open SageFs.Features.CohortIntegrationScope
open SageFs.MemberTable

let private repoA = CohortScope.Repository "/work/repo-a"
let private repoB = CohortScope.Repository "/work/repo-b"

/// The registered tools, by name, as reflection sees them: the same surface the MCP SDK binds.
let private registeredTools : MethodInfo list =
  typeof<SageFs.Server.McpTools.SageFsTools>.GetMethods(BindingFlags.Instance ||| BindingFlags.Public)
  |> Array.filter (fun m -> m.GetCustomAttributes(true) |> Array.exists (fun a -> a.GetType().Name = "McpServerToolAttribute"))
  |> Array.toList

let private toolNamed (name: string) : MethodInfo option = registeredTools |> List.tryFind (fun m -> m.Name = name)

[<Tests>]
let integrationScopeTests =
  testList "CohortIntegration: which repository, which cohort (P11)" [

    testList "the repository a set_integration_ref call means" [
      test "the directory the call named wins over the caller's session and the daemon's own" {
        chooseIntegrationRepoSource (Some "/work/lab") (Some "/work/session") "/home/will/.local/state/sagefs-install"
        |> Expect.equal "the named directory is the repository" (MainRepoRootSource.NamedByCall "/work/lab")
      }

      test "a blank name is no name, so the caller's session answers, then the daemon's" {
        chooseIntegrationRepoSource (Some "  ") (Some "/work/session") "/daemon"
        |> Expect.equal "a blank name falls through to the session" (MainRepoRootSource.CallerSession "/work/session")
        chooseIntegrationRepoSource None None "/daemon"
        |> Expect.equal "nothing named falls through to the daemon's directory" (MainRepoRootSource.DaemonProcessCwd "/daemon")
      }

      testProperty "whenever a directory is named, it is the one used, whatever else is known" <| fun (NonEmptyString named) (callerSession: string option) (NonEmptyString cwd) ->
        let named = named.Trim()
        (named <> "") ==> lazy (candidateDirectory (chooseIntegrationRepoSource (Some named) callerSession cwd) = named)
    ]

    testList "which cohort a configured integration serves" [
      test "no integration configured is said as that" {
        bindingFit None repoA |> Expect.equal "nothing configured" BindingFit.NotConfigured
      }

      test "an integration configured for the cohort the call acts in serves it" {
        bindingFit (Some repoA) repoA |> Expect.equal "same cohort" BindingFit.ForThisCohort
      }

      test "an integration configured for another repository does not serve this one, and says which it serves" {
        bindingFit (Some repoA) repoB |> Expect.equal "a different cohort" (BindingFit.ConfiguredForOther repoA)
      }

      testProperty "the fit is ForThisCohort exactly when the two scopes are equal" <| fun (NonEmptyString a) (NonEmptyString b) ->
        let sa = CohortScope.Repository a
        let sb = CohortScope.Repository b
        let fit = bindingFit (Some sa) sb
        (fit = BindingFit.ForThisCohort) = (sa = sb)
    ]

    testList "the integration branch of a repeated set_integration_ref" [
      test "a free name is the plain one" {
        freshBranchName "0123456789abcdef0123" (fun _ -> false)
        |> Expect.equal "the first call keeps the name it always had" "sagefs/cohort-01234567"
      }

      test "a name that exists is never reused: the branch may hold landings" {
        let taken = set [ "sagefs/cohort-01234567"; "sagefs/cohort-01234567-2" ]
        freshBranchName "0123456789abcdef0123" taken.Contains
        |> Expect.equal "the next free name" "sagefs/cohort-01234567-3"
      }

      testProperty "the name is never one that exists" <| fun (sha: NonEmptyString) (takenCount: byte) ->
        let sha = sha.Get.Replace(" ", "x")
        let baseName = sprintf "sagefs/cohort-%s" (sha.Substring(0, min 8 sha.Length))
        let existing = set (baseName :: [ for i in 2 .. 1 + int takenCount -> sprintf "%s-%d" baseName i ])
        let chosen = freshBranchName sha existing.Contains
        not (existing.Contains chosen) && chosen.StartsWith baseName

      test "TWIN WITH TEETH: the old fixed name collides with the branch a first call left behind" {
        let oldName (sha: string) = sprintf "sagefs/cohort-%s" (sha.Substring(0, min 8 sha.Length))
        let existing = set [ oldName "0123456789abcdef0123" ]
        existing.Contains (oldName "0123456789abcdef0123")
        |> Expect.isTrue "the fixed name is taken by the first call, which is the 'branch already exists' failure"
        existing.Contains (freshBranchName "0123456789abcdef0123" existing.Contains)
        |> Expect.isFalse "the fresh name is not"
      }
    ]

    testList "the projects the integration session loads" [
      test "a project that references a test framework is a test project" {
        isTestProjectXml """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Expecto" Version="1.0" /></ItemGroup></Project>"""
        |> Expect.isTrue "Expecto"
        isTestProjectXml """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" /></ItemGroup></Project>"""
        |> Expect.isTrue "xunit"
        isTestProjectXml """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>"""
        |> Expect.isTrue "IsTestProject"
      }

      test "a library, an app and text that is not XML are not test projects" {
        isTestProjectXml """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Falco" /></ItemGroup></Project>"""
        |> Expect.isFalse "a library"
        isTestProjectXml "this is not xml" |> Expect.isFalse "garbage"
      }

      test "only the test projects are loaded when the solution has any" {
        let isTest (p: string) = p.EndsWith ".Tests.fsproj"
        narrowToTestProjects [ "Core/Core.fsproj"; "Core.Tests/Core.Tests.fsproj"; "samples/Rust/Rust.fsproj" ] isTest
        |> Expect.equal "the samples and the library are not loaded" [ "Core.Tests/Core.Tests.fsproj" ]
      }

      test "no test project means the whole candidate list stands, never an empty session" {
        narrowToTestProjects [ "A/A.fsproj"; "B/B.fsproj" ] (fun _ -> false)
        |> Expect.equal "nothing to narrow to" [ "A/A.fsproj"; "B/B.fsproj" ]
      }

      testProperty "narrowing never empties a non-empty list and never invents a project" <| fun (candidates: string list) (marked: bool list) ->
        let tests = candidates |> List.mapi (fun i c -> c, (List.tryItem i marked |> Option.defaultValue false)) |> Map.ofList
        let isTest (p: string) = Map.tryFind p tests |> Option.defaultValue false
        let narrowed = narrowToTestProjects candidates isTest
        (List.isEmpty candidates || not (List.isEmpty narrowed)) && narrowed |> List.forall (fun p -> List.contains p candidates)
    ]

    testList "the projects a conductor names" [
      test "a list is read from commas, semicolons and lines, with Windows separators and blanks cleaned up" {
        parseNamedProjects "SageFs.Tests/SageFs.Tests.fsproj, ;Other\\Other.fsproj\n\n SageFs.Tests/SageFs.Tests.fsproj"
        |> Expect.equal "two projects, once each" [ "SageFs.Tests/SageFs.Tests.fsproj"; "Other/Other.fsproj" ]
        parseNamedProjects null |> Expect.equal "nothing named" []
      }

      test "the conductor's own list wins, and no list leaves discovery in charge" {
        chooseIntegrationProjects [ "A/A.fsproj" ] [ "B/B.fsproj"; "C/C.fsproj" ] |> Expect.equal "named wins" [ "A/A.fsproj" ]
        chooseIntegrationProjects [] [ "B/B.fsproj" ] |> Expect.equal "discovery otherwise" [ "B/B.fsproj" ]
      }
    ]

    testList "a faulted integration session says why and what to do" [
      test "the advice carries the cause, the worktree and the retry for the same repository" {
        let advice = failureAdvice "the worktree build failed: error FS0001" "/data/cohort-integration" "repo:/work/repo-a"
        advice |> Expect.stringContains "the cause" "error FS0001"
        advice |> Expect.stringContains "the worktree to look at" "/data/cohort-integration"
        advice |> Expect.stringContains "the retry" "set_integration_ref"
        advice |> Expect.stringContains "for which repository" "/work/repo-a"
      }
    ]
  ]

[<Tests>]
let refusalTests =
  testList "CohortIntegration: a caller who is not seated is told so (P11)" [

    test "an unseated caller calling a conductor-only tool is NotSeated, never 'your role is Working'" {
      for tool in Affordances.ToolRole.conductorOnlyTools do
        Affordances.checkAuthorityAllowed Authority.Anonymous tool
        |> Expect.equal
          (sprintf "%s: the problem is the missing seat, not the role" (Affordances.ToolName.toString tool))
          (Error(Affordances.AuthorityRefusal.NotSeated tool))
    }

    test "a seated Implementer calling a conductor-only tool is still refused for its role" {
      let implementer = Authority.Member(MemberId.Minted "alice", JoinableRole.Implementer)
      for tool in Affordances.ToolRole.conductorOnlyTools do
        Affordances.checkAuthorityAllowed implementer tool
        |> Expect.equal
          (sprintf "%s: a seated member's role is what forbids it" (Affordances.ToolName.toString tool))
          (Error(Affordances.AuthorityRefusal.NotInGrant(tool, Affordances.ToolRole.Working)))
    }

    test "the refusal names the cohort the call acted in and the way to be seated there" {
      match ToolAuthorityGate.decideIn "repo:/work/lab" (MemberId.Minted "conductor") Authority.Anonymous "set_integration_ref" with
      | ToolAuthorityGate.Decision.Admitted -> failtest "an unseated caller was admitted"
      | ToolAuthorityGate.Decision.Refused(reason, nextAction) ->
        reason |> Expect.stringContains "which cohort the call acted in" "repo:/work/lab"
        reason |> Expect.stringContains "that the caller has no seat there" "no seat"
        (reason.Contains "your role is") |> Expect.isFalse "it does not blame a role the caller never lacked"
        nextAction |> Expect.stringContains "the next action passes the repository" "working_directory"
        nextAction |> Expect.stringContains "and joins it" "join_cohort"
    }

    testTask "through the real gate: a stranger in the lab repository is told which cohort that was" {
      let ctx = ToolAuthorityGateTests.mkContext ()
      match! ToolAuthorityGateTests.gate ctx "stranger" (Some ToolAuthorityGateTests.molinaRepo) "set_integration_ref" with
      | Ok _ -> failtest "a caller who never joined was admitted to a conductor-only tool"
      | Error refusal ->
        let label = Scope.label (ScopeOf.ofWorkingDirectory (Some ToolAuthorityGateTests.molinaRepo))
        refusal |> Expect.stringContains "the cohort the call acted in" label
        (refusal.Contains "your role is") |> Expect.isFalse "no role is blamed"
    }
  ]

[<Tests>]
let surfaceTests =
  testList "CohortIntegration: every tool the conductor runs names the cohort it acts in (P11)" [

    test "WHY - every cohort tool and every conductor-only tool takes working_directory, or it acts in the daemon's own cohort" {
      let cohortToolNames = Affordances.CohortTool.all |> List.map Affordances.CohortTool.toToolName
      let conductorOnlyNames = Affordances.ToolRole.conductorOnlyTools |> Set.toList |> List.map Affordances.ToolName.toString
      let names = (cohortToolNames @ conductorOnlyNames) |> List.distinct
      let missing =
        names
        |> List.filter (fun name ->
          match toolNamed name with
          | None -> true
          | Some m -> m.GetParameters() |> Array.exists (fun p -> p.Name = "working_directory") |> not)
      missing |> Expect.isEmpty "a tool that cannot be told which repository it is about reads the daemon's own cohort"
      (List.length names > 10) |> Expect.isTrue "the scan covered the cohort tools, not an empty list"
    }

    test "working_directory is optional on set_integration_ref, so a caller in the daemon's own repository passes nothing" {
      match toolNamed "set_integration_ref" with
      | None -> failtest "set_integration_ref is not registered"
      | Some m ->
        let p = m.GetParameters() |> Array.tryFind (fun p -> p.Name = "working_directory")
        p |> Option.map (fun p -> p.HasDefaultValue) |> Expect.equal "it has a default" (Some true)
    }
  ]

[<Tests>]
let statusTests =
  testList "CohortIntegration: the status read says which cohort the integration serves (P11)" [

    testTask "an integration configured for another repository is named, not shown as this cohort's" {
      let ctx = ToolAuthorityGateTests.mkContext ()
      let other = CohortScope.Repository "/work/other-repo"
      let previous = McpCohortIntegration.cohortIntegrationRef.Value
      try
        McpCohortIntegration.cohortIntegrationRef.Value <-
          Some { Scope = other
                 Repository = "/work/other-repo"
                 WorktreePath = "/data/cohort-integration"
                 Branch = "sagefs/cohort-abcdef12"
                 TrunkPath = "/data/cohort-trunk"
                 Session = McpCohortIntegration.IntegrationSession.Started "sess-1" }
        match! McpCohortIntegration.getCohortStatus ctx (Some ToolAuthorityGateTests.molinaRepo) with
        | Error e -> failtestf "status was refused: %s" (SageFsError.describeForAgent e)
        | Ok (text: string) ->
          (text.Contains "sess-1") |> Expect.isFalse "another repository's integration session is not this cohort's"
          text |> Expect.stringContains "it says which repository the integration serves" "/work/other-repo"
          text |> Expect.stringContains "and what to do about it" "set_integration_ref"
      finally
        McpCohortIntegration.cohortIntegrationRef.Value <- previous
    }
  ]

[<Tests>]
let guardTests =
  testList "CohortIntegration: a landing in another cohort is never performed against this integration (P11)" [

    testTask "a performer guarded for another cohort ends every step in the reason and never calls the real one" {
      let calls = ref 0
      let counted (result: 'a) = fun () -> async { calls.Value <- calls.Value + 1; return result }
      let inner : CohortOwner.LandingPerformer<MemberId> =
        { Rebase = fun _ _ _ -> counted (Ok "new-head") ()
          ComputeAffected = fun _ _ _ -> counted (Ok []) ()
          RunTests = fun _ _ -> counted (Ok []) ()
          FastForward = fun _ _ -> counted (Ok "sha") ()
          Notify = fun _ _ -> () }
      let guarded = CohortOwner.LandingPerformer.guardedBy (fun () -> Some "no integration for this cohort") inner
      let! rebase = guarded.Rebase "l" "onto" [ "c" ]
      let! affected = guarded.ComputeAffected "l" "b" "h"
      let! tests = guarded.RunTests "l" []
      let! forward = guarded.FastForward "l" "sha"
      rebase |> Expect.equal "rebase says why" (Error [ "no integration for this cohort" ])
      affected |> Expect.equal "affected says why" (Error (CohortOwner.VerificationFault.Environment "no integration for this cohort"))
      tests |> Expect.equal "tests say why" (Error "no integration for this cohort")
      forward |> Expect.equal "fast-forward says why" (Error "no integration for this cohort")
      calls.Value |> Expect.equal "the real performer was never called" 0
    }

    testTask "a performer guarded for the cohort the integration serves is the real one" {
      let inner : CohortOwner.LandingPerformer<MemberId> =
        { CohortOwner.LandingPerformer.stub with Rebase = fun _ _ _ -> async { return Ok "new-head" } }
      let guarded = CohortOwner.LandingPerformer.guardedBy (fun () -> None) inner
      let! rebase = guarded.Rebase "l" "onto" [ "c" ]
      rebase |> Expect.equal "the step goes through" (Ok "new-head")
    }

    testTask "clearing local data sees a cohort running in a repository the daemon was not started in" {
      let ctx = ToolAuthorityGateTests.mkContext ()
      do! ToolAuthorityGateTests.join ctx "molina-agent" "Implementer" ToolAuthorityGateTests.molinaRepo
      let across = LocalData.liveCohortAcross (CohortOwnerResolution.ownersOf ctx.CohortSupport)
      let daemonsOwnOnly =
        match SageFs.McpTools.cohortOwnerFor ctx None with
        | Some own -> LocalDataRetention.cohortActivity (own.ReadCohortState ())
        | None -> LocalDataRetention.CohortActivity.Finished
      daemonsOwnOnly |> Expect.equal "TWIN WITH TEETH: the daemon's own cohort alone sees nothing running" LocalDataRetention.CohortActivity.Finished
      match across with
      | LocalDataRetention.CohortActivity.Active(members, _, _) -> members |> Expect.equal "the member in the other repository is seen" 1
      | LocalDataRetention.CohortActivity.Finished -> failtest "a running cohort in another repository was not seen, so its ledger could be cleared"
    }
  ]
