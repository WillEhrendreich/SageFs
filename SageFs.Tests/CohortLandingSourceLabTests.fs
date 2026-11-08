/// A landing that changes PRODUCTION source, run end to end by real processes (P14, findings C6 to C9).
///
/// The first cohort run landed only test files. The first landing of source in a compiled project (SageFs.Core's
/// AppState.fs) was Blocked Inconclusive three times: the verifier re-evaluated the rebased file into the integration
/// session, whose loaded project already holds that file's types from its compiled DLL, and the re-evaluation failed with
/// "Operation could not be completed due to earlier error". Nothing named the first error, the integration worktree stayed
/// on the failed landing's commit, and the session kept the failed landing's bindings.
///
/// The world is a throwaway repository with TWO projects, the shape that matters: Lab.Core (a library, compiled) and
/// Lab.Tests (the test project, which references it). A daemon on a port of its own, an empty directory as its cwd, a
/// conductor and a member holding a token, exactly as in `CohortIntegrationLabTests`.
module SageFs.Tests.CohortLandingSourceLabTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Tests.CohortIntegrationLabTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private coreProject =
  String.concat "\n" [
    "<Project Sdk=\"Microsoft.NET.Sdk\">"
    "  <PropertyGroup>"
    "    <TargetFramework>net11.0</TargetFramework>"
    "  </PropertyGroup>"
    "  <ItemGroup>"
    "    <Compile Include=\"Greeting.fs\" />"
    "  </ItemGroup>"
    "</Project>"
    "" ]

let private testsProject () =
  String.concat "\n" [
    "<Project Sdk=\"Microsoft.NET.Sdk\">"
    "  <PropertyGroup>"
    "    <TargetFramework>net11.0</TargetFramework>"
    "    <OutputType>Exe</OutputType>"
    "    <GenerateProgramFile>false</GenerateProgramFile>"
    "  </PropertyGroup>"
    "  <ItemGroup>"
    "    <Compile Include=\"Tests.fs\" />"
    "    <Compile Include=\"Program.fs\" />"
    "  </ItemGroup>"
    "  <ItemGroup>"
    "    <ProjectReference Include=\"../Lab.Core/Lab.Core.fsproj\" />"
    "  </ItemGroup>"
    "  <ItemGroup>"
    sprintf "    <PackageReference Include=\"Expecto\" Version=\"%s\" />" (expectoVersion ())
    "  </ItemGroup>"
    "</Project>"
    "" ]

let private greetingFile (extraCase: bool) (warm: string) (plain: string) =
  String.concat "\n" [
    "namespace Lab"
    ""
    "type Tone ="
    "  | Warm"
    "  | Plain"
    (match extraCase with true -> "  | Loud" | false -> "")
    ""
    "module Greeting ="
    ""
    "  let greeting (tone: Tone) : string ="
    "    match tone with"
    sprintf "    | Warm -> \"%s\"" warm
    sprintf "    | Plain -> \"%s\"" plain
    (match extraCase with true -> "    | Loud -> \"HELLO\"" | false -> "")
    "" ]

let private testsFile (extraTest: bool) =
  String.concat "\n" [
    "module Lab.Tests"
    ""
    "open Expecto"
    "open Expecto.Flip"
    ""
    "[<Tests>]"
    "let tests ="
    "  testList \"Lab\" ["
    "    test \"the warm greeting starts with hello\" {"
    "      Lab.Greeting.greeting Lab.Warm |> Expect.stringStarts \"the greeting opens with hello\" \"hello\""
    "    }"
    "    test \"the plain greeting is hello\" {"
    "      Lab.Greeting.greeting Lab.Plain |> Expect.equal \"the plain greeting\" \"hello\""
    "    }"
    (match extraTest with
     | true -> "    test \"a second test that only reads the greeting\" { Lab.Greeting.greeting Lab.Warm |> Expect.isNotEmpty \"not empty\" }"
     | false -> "")
    "  ]"
    "" ]

let private programFile =
  String.concat "\n" [
    "module Lab.Program"
    ""
    "open Expecto"
    ""
    "[<EntryPoint>]"
    "let main argv = runTestsWithCLIArgs [] argv Lab.Tests.tests"
    "" ]

/// The same project with a package no cache holds, and a feed that cannot be reached: the restore of a landing that carries both
/// fails the way an offline machine's does (NU1301), which says nothing about the commit.
let private coreProjectWithAPackage =
  coreProject.Replace("</Project>", "  <ItemGroup>\n    <PackageReference Include=\"SageFs.Lab.NotCached.Package\" Version=\"9.9.9\" />\n  </ItemGroup>\n</Project>")

let private unreachableFeed =
  String.concat "\n" [
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
    "<configuration>"
    "  <packageSources>"
    "    <clear />"
    "    <add key=\"unreachable\" value=\"http://127.0.0.1:9/v3/index.json\" />"
    "  </packageSources>"
    "</configuration>"
    "" ]

let private rebuildVerdictBudget = TimeSpan.FromTicks(TestTimeouts.heavyVerdictBudget.Ticks * 2L)

[<Tests>]
let tests =
  Integration.hostList "A landing of production source in a compiled project (real daemon, real processes)" [

    testTask "WHY - source in a compiled library lands when its tests pass and is refused with the first compiler error or the failing tests when they do not, and a refused landing leaves the integration worktree at its head" {
      gitAvailable () |> Expect.isTrue "git is installed"
      let world = Directory.CreateTempSubdirectory("cohort-source-lab-").FullName
      let lab = Path.Combine(world, "lab")
      let memberCheckout = Path.Combine(world, "member")
      let secondRepository = Path.Combine(world, "lab-second")
      let neutral = Path.Combine(world, "neutral")
      let dataDir = Path.Combine(world, "data")
      for dir in [ lab; neutral; dataDir; Path.Combine(lab, "Lab.Core"); Path.Combine(lab, "Lab.Tests") ] do Directory.CreateDirectory dir |> ignore
      let! _ = git lab [ "init"; "--quiet"; "-b"; "main" ]
      let! _ = git lab [ "config"; "user.email"; "cohort-lab@example.com" ]
      let! _ = git lab [ "config"; "user.name"; "Cohort Lab" ]
      File.WriteAllText(Path.Combine(lab, "Lab.Core", "Lab.Core.fsproj"), coreProject)
      File.WriteAllText(Path.Combine(lab, "Lab.Core", "Greeting.fs"), greetingFile false "hello, friend" "hello")
      File.WriteAllText(Path.Combine(lab, "Lab.Tests", "Lab.Tests.fsproj"), testsProject ())
      File.WriteAllText(Path.Combine(lab, "Lab.Tests", "Tests.fs"), testsFile false)
      File.WriteAllText(Path.Combine(lab, "Lab.Tests", "Program.fs"), programFile)
      File.Copy(Path.Combine(repoRoot, "global.json"), Path.Combine(lab, "global.json"))
      File.WriteAllText(Path.Combine(lab, ".gitignore"), "bin/\nobj/\n")
      let! _ = git lab [ "add"; "-A" ]
      let! _ = git lab [ "commit"; "--quiet"; "-m"; "base: the lab projects" ]
      let! baseSha = git lab [ "rev-parse"; "HEAD" ]
      let! _ = git lab [ "worktree"; "add"; "--quiet"; "--detach"; memberCheckout; baseSha ]
      let! _ = git world [ "clone"; "--quiet"; lab; secondRepository ]

      let! daemon, port = startDaemonIn neutral dataDir
      try
        use! first = connect port
        use! second = connect port
        let! _ = callTool first "join_cohort" [ "agentName", box "first"; "role", box "Implementer"; "working_directory", box lab ]
        let! _ = callTool second "join_cohort" [ "agentName", box "second"; "role", box "Implementer"; "working_directory", box lab ]
        let! _ = callTool first "leave_cohort" [ "agentName", box "first"; "working_directory", box lab ]
        let! looked = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Look lab)
        let secondId =
          match looked with
          | Ok (reply: CohortSeatRoute.SeatReply) -> (List.head reply.Present).Id
          | Error failure -> failtestf "look at the seat: %s" (MemberMintCommand.MintFailure.describe failure)
        let! seated = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Seat(lab, secondId))
        match seated with
        | Ok _ -> ()
        | Error failure -> failtestf "seat the conductor: %s" (MemberMintCommand.MintFailure.describe failure)

        let! configured =
          callToolPatient second "set_integration_ref"
            [ "agentName", box "second"; "integrationRef", box "HEAD"; "working_directory", box lab ]
        configured |> Expect.stringContains "the integration is configured" "session="
        let integrationBranch = field "branch=" configured
        let integrationWorktree = field "worktree=" configured

        let! minted =
          McpCallIntegrationTests.runSagefs
            [ "SAGEFS_DATA_DIR", dataDir ]
            [ McpCallIntegrationTests.memberTokenVariable; McpCallIntegrationTests.operatorTokenVariable ]
            [ "member"; "mint"; "--mcp-port"; string port; "--role"; "Implementer"; "--dir"; memberCheckout; "--print-env"; McpCallIntegrationTests.memberTokenVariable ]
        let token = McpCallIntegrationTests.mintedToken minted

        // A claim a refused landing leaves behind is still the member's: the next landing names it again.
        let claims = Collections.Generic.Dictionary<string, string * string>()
        let claimOn (path: string) : Task<string * string> =
          task {
            match claims.TryGetValue path with
            | true, held -> return held
            | false, _ ->
              let! claimed =
                asMember port token "acquire_claim"
                  [ "agentName", "member"; "scope", sprintf "file:%s" path; "purpose", "lab edit"; "working_directory", memberCheckout ]
              let text = succeeded "the member claims the file" claimed
              let m = Regex.Match(text, @"Acquired claim (\S+) over .*\(fence=(\d+)\)")
              m.Success |> Expect.isTrue (sprintf "the claim is reported: %s" text)
              claims[path] <- (m.Groups[1].Value, m.Groups[2].Value)
              return claims[path]
          }

        let land (branch: string) (startPoint: string) (edits: (string * string) list) (statement: string) : Task<string> =
          task {
            let! held = edits |> List.map fst |> List.map claimOn |> Task.WhenAll
            let! _ = git memberCheckout [ "checkout"; "--quiet"; "-B"; branch; startPoint ]
            for path, text in edits do File.WriteAllText(Path.Combine(memberCheckout, path), text)
            let! _ = git memberCheckout [ "add"; "-A" ]
            let! _ = git memberCheckout [ "commit"; "--quiet"; "-m"; statement ]
            let! sha = git memberCheckout [ "rev-parse"; "HEAD" ]
            let! queued =
              asMember port token "request_landing"
                [ "agentName", "member"
                  "claims", held |> Array.map (fun (id, fence) -> sprintf "%s:%s" id fence) |> String.concat ","
                  "commits", sha
                  "statement", statement
                  "working_directory", memberCheckout ]
            let text = succeeded "the landing is queued" queued
            let m = Regex.Match(text, @"Landing (\S+) queued")
            m.Success |> Expect.isTrue (sprintf "the landing is queued: %s" text)
            return m.Groups[1].Value
          }

        let awaitSettled (landingId: string) : Task<string> =
          task {
            let last = ref ""
            let! settled =
              SageFs.Tests.TestInfrastructure.waitForAsync (int rebuildVerdictBudget.TotalMilliseconds) (fun () -> task {
                let! run = asMember port token "get_cohort_status" [ "working_directory", memberCheckout ]
                last.Value <- run.Out
                match landingLine run.Out landingId with
                | Some line -> return line.Contains "state=Landed" || line.Contains "state=Blocked"
                | None -> return false })
            match settled with
            | true -> return (landingLine last.Value landingId).Value
            | false -> return failtestf "landing %s did not settle. Last status:\n%s" landingId last.Value
          }

        let integrationTip () = git lab [ "rev-parse"; sprintf "refs/heads/%s" integrationBranch ]
        let worktreeHead () = git integrationWorktree [ "rev-parse"; "HEAD" ]
        // A landing that landed released the claims it named; one that was refused left them with the member.
        let landed (name: string) (paths: string list) (line: string) =
          line |> Expect.stringContains (sprintf "%s landed. Line: %s" name line) "state=Landed"
          for path in paths do claims.Remove path |> ignore

        // 1. A change to a source file of the library, with a test that reads it: lands.
        let! id1 = land "source-good" baseSha [ "Lab.Core/Greeting.fs", greetingFile false "hello there, friend" "hello" ] "the warm greeting says there"
        let! line1 = awaitSettled id1
        landed "a source change whose tests pass" [ "Lab.Core/Greeting.fs" ] line1
        let! tip1 = integrationTip ()

        // 2. A DU gains a case and a function handles it: lands.
        let! id2 = land "source-du" tip1 [ "Lab.Core/Greeting.fs", greetingFile true "hello there, friend" "hello" ] "the tone gains a loud case"
        let! line2 = awaitSettled id2
        landed "a DU change" [ "Lab.Core/Greeting.fs" ] line2
        let! tip2 = integrationTip ()

        // 3. A source file that does not compile: refused, and the status quotes the first compiler error and the file.
        let! id3 = land "source-broken" tip2 [ "Lab.Core/Greeting.fs", (greetingFile true "hello there, friend" "hello").Replace("\"HELLO\"", "42") ] "a type error in the library"
        let! line3 = awaitSettled id3
        line3 |> Expect.stringContains (sprintf "the broken landing is refused. Line: %s" line3) "state=Blocked"
        line3 |> Expect.stringContains (sprintf "it is recorded as a compile error in the landing, the member's to fix. Line: %s" line3) "does not compile"
        line3.Contains "verification inconclusive" |> Expect.isFalse (sprintf "it is not recorded as an inconclusive verification. Line: %s" line3)
        line3 |> Expect.stringContains (sprintf "it names the file. Line: %s" line3) "Greeting.fs"
        line3 |> Expect.stringContains (sprintf "it quotes the compiler's error code. Line: %s" line3) "FS0001"
        let! tip3 = integrationTip ()
        tip3 |> Expect.equal "the integration branch did not move" tip2
        let! head3 = worktreeHead ()
        head3 |> Expect.equal "the integration worktree is back at the integration head" tip3

        // 3b. A restore that cannot reach its feed (what an offline machine sees) is the environment's: the landing is refused as
        // inconclusive with the restore error, and the member is not told to fix their commit.
        let! idOffline =
          land "source-offline" tip3
            [ "Lab.Core/Lab.Core.fsproj", coreProjectWithAPackage
              "nuget.config", unreachableFeed ] "a package from a feed that cannot be reached"
        let! lineOffline = awaitSettled idOffline
        lineOffline |> Expect.stringContains (sprintf "the landing is refused. Line: %s" lineOffline) "state=Blocked"
        lineOffline |> Expect.stringContains (sprintf "it is recorded as an inconclusive verification. Line: %s" lineOffline) "verification inconclusive"
        lineOffline |> Expect.stringContains (sprintf "it quotes the restore error. Line: %s" lineOffline) "NU1"
        lineOffline.Contains "does not compile" |> Expect.isFalse (sprintf "it is not blamed on the commit. Line: %s" lineOffline)
        lineOffline.Contains "fix it in your commit" |> Expect.isFalse (sprintf "the member is not told to fix their commit. Line: %s" lineOffline)
        let! tipOffline = integrationTip ()
        tipOffline |> Expect.equal "the integration branch did not move" tip3
        let! headOffline = worktreeHead ()
        headOffline |> Expect.equal "the integration worktree is back at the integration head" tipOffline

        // 4. Source whose tests fail: refused, naming the failing test, and the worktree is at the head again.
        let! id4 = land "source-failing" tip3 [ "Lab.Core/Greeting.fs", greetingFile true "hello there, friend" "goodbye" ] "the plain greeting says goodbye"
        let! line4 = awaitSettled id4
        line4 |> Expect.stringContains (sprintf "the landing that breaks a test is refused. Line: %s" line4) "state=Blocked"
        let! tip4 = integrationTip ()
        tip4 |> Expect.equal "the integration branch did not move" tip3
        let! head4 = worktreeHead ()
        head4 |> Expect.equal "the integration worktree is back at the integration head" tip4

        // 5. A test-only landing, behind two refused landings: lands (the session was left at the head, not on a failed commit).
        let! id5 = land "tests-only" tip4 [ "Lab.Tests/Tests.fs", testsFile true ] "one more test"
        let! line5 = awaitSettled id5
        landed "a test-only change after refused landings" [ "Lab.Tests/Tests.fs" ] line5
        let! tip5 = integrationTip ()

        // 6. Source again, after all of that: lands.
        let! id6 = land "source-again" tip5 [ "Lab.Core/Greeting.fs", greetingFile true "hello again, friend" "hello" ] "the warm greeting says again"
        let! line6 = awaitSettled id6
        landed "source after refused landings" [ "Lab.Core/Greeting.fs" ] line6
        let! tip6 = integrationTip ()
        let! shown = git lab [ "show"; sprintf "%s:Lab.Core/Greeting.fs" tip6 ]
        shown |> Expect.stringContains "the integration holds the last change" "hello again, friend"

        // P16: set_integration_ref while a landing is queued is refused and the landing lands.
        let! idHeld = land "source-held" tip6 [ "Lab.Core/Greeting.fs", greetingFile true "hello held, friend" "hello" ] "the warm greeting says held"
        let! refused =
          callToolPatient second "set_integration_ref"
            [ "agentName", box "second"; "integrationRef", box "HEAD"; "working_directory", box lab ]
        refused |> Expect.stringContains "the call is refused and says how to take a landing back" "withdraw_landing"
        refused |> Expect.stringContains "it names the landing in front" idHeld
        let! lineHeld = awaitSettled idHeld
        landed "the landing that was in the queue when the call came" [ "Lab.Core/Greeting.fs" ] lineHeld
        Directory.Exists integrationWorktree |> Expect.isTrue "the integration worktree was never touched"

        // P16: with the queue empty the integration moves to a second repository: it gets a worktree path of its own and the
        // first repository's worktree is removed from the first repository.
        let! moved =
          callToolPatient second "set_integration_ref"
            [ "agentName", box "second"; "integrationRef", box "HEAD"; "working_directory", box secondRepository ]
        moved |> Expect.stringContains "the integration is configured for the second repository" "session="
        field "worktree=" moved |> Expect.notEqual "the second repository has its own worktree path" integrationWorktree
        Directory.Exists integrationWorktree |> Expect.isFalse "the first repository's worktree is gone"
        let! listed = git lab [ "worktree"; "list"; "--porcelain" ]
        (listed: string).Contains(integrationWorktree: string) |> Expect.isFalse "and git no longer lists it in the first repository"

        // C6: a generated page needs no claim.
        let! generated =
          asMember port token "acquire_claim"
            [ "agentName", "member"; "scope", "file:docs/roadmap.md"; "purpose", "regenerate the roadmap"; "working_directory", memberCheckout ]
        (succeeded "the claim on a generated page answers" generated)
        |> Expect.stringContains "it says the page is generated and needs no claim" "generated"
      finally
        killProcess daemon
        try Directory.Delete(world, true) with _ -> ()
    }
  ]
