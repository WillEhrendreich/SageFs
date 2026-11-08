/// A cohort for a repository the daemon was NOT started in, run end to end by real processes (P11, finding F16).
///
/// `sagefs install-local` starts the daemon in its install folder, so the daemon's own cohort is about that folder and the
/// repository people work in is a second cohort. Everything the cohort does has to work there: the operator seats a
/// conductor, the conductor configures the integration for the repository it names, a member holding a token minted for a
/// second checkout claims a file, commits, and lands, and a landing that breaks a test is refused.
///
/// The world is a throwaway git repository with a small Expecto project, a daemon on a port of its own with its own
/// SAGEFS_DATA_DIR and an EMPTY directory as its working directory (so it does not know the repository, which is what
/// reproduces the bug), the conductor on a real MCP connection, and the member and the operator through the `sagefs`
/// command line exactly as a person or a hook would run them.
module SageFs.Tests.CohortIntegrationLabTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

let repoRoot = RepoPaths.repoPathFull [||]

/// The Expecto the repo pins, read from the one place that pins it, so the lab restores from the same cache the repo does.
let expectoVersion (): string =
  let props = XDocument.Load(Path.Combine(repoRoot, "Directory.Packages.props"))
  props.Descendants(XName.Get "PackageVersion")
  |> Seq.find (fun e -> e.Attribute(XName.Get "Include").Value = "Expecto")
  |> fun e -> e.Attribute(XName.Get "Version").Value

let private labProject () : string =
  String.concat "\n" [
    "<Project Sdk=\"Microsoft.NET.Sdk\">"
    "  <PropertyGroup>"
    "    <TargetFramework>net11.0</TargetFramework>"
    "    <OutputType>Exe</OutputType>"
    "    <GenerateProgramFile>false</GenerateProgramFile>"
    "  </PropertyGroup>"
    "  <ItemGroup>"
    "    <Compile Include=\"Lab.fs\" />"
    "    <Compile Include=\"Tests.fs\" />"
    "    <Compile Include=\"Program.fs\" />"
    "  </ItemGroup>"
    "  <ItemGroup>"
    sprintf "    <PackageReference Include=\"Expecto\" Version=\"%s\" />" (expectoVersion ())
    "  </ItemGroup>"
    "</Project>"
    "" ]

let private labSource = "module Lab.Greeting\n\nlet greeting () : string = \"hello\"\n"

let private labTests =
  String.concat "\n" [
    "module Lab.Tests"
    ""
    "open Expecto"
    "open Expecto.Flip"
    ""
    "[<Tests>]"
    "let tests ="
    "  testList \"Lab\" ["
    "    test \"the greeting starts with hello\" {"
    "      Lab.Greeting.greeting () |> Expect.stringStarts \"the greeting opens with hello\" \"hello\""
    "    }"
    "  ]"
    "" ]

let private labProgram =
  String.concat "\n" [
    "module Lab.Program"
    ""
    "open Expecto"
    ""
    "[<EntryPoint>]"
    "let main argv = runTestsWithCLIArgs [] argv Lab.Tests.tests"
    "" ]

let private labGreeting (greeting: string) = sprintf "module Lab.Greeting\n\nlet greeting () : string = \"%s\"\n" greeting

let gitAvailable (): bool =
  try
    let psi = ProcessStartInfo("git", "--version", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    use proc = Process.Start psi
    proc.WaitForExit(TestTimeouts.childExit) |> ignore
    proc.HasExited && proc.ExitCode = 0
  with _ -> false

/// Plain git for the fixture and every oracle: never through the daemon's own git code.
let git (dir: string) (args: string list) : Task<string> =
  task {
    let psi = ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir)
    for a in args do psi.ArgumentList.Add a
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let! stdout = proc.StandardOutput.ReadToEndAsync()
    let! stderr = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync()
    match proc.ExitCode with
    | 0 -> return stdout.Trim()
    | code -> return failwithf "git %s failed (%d) in %s: %s" (String.concat " " args) code dir stderr
  }

let killProcess (proc: Process) =
  try
    if not proc.HasExited then
      proc.Kill(entireProcessTree = true)
      proc.WaitForExit TestTimeouts.childExit |> ignore
  with _ -> ()

/// A daemon whose working directory is `neutralDir`, an empty directory that is no repository: the shape `install-local` gives.
let startDaemonIn (neutralDir: string) (dataDir: string) : Task<Process * int> =
  task {
    let port, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.WorkingDirectory <- neutralDir
    psi.ArgumentList.Add "--mcp-port"
    psi.ArgumentList.Add(string port)
    psi.ArgumentList.Add "--no-resume"
    let self = Process.GetCurrentProcess()
    psi.ArgumentList.Add "--owner-pid"
    psi.ArgumentList.Add(string self.Id)
    psi.ArgumentList.Add "--owner-start"
    psi.ArgumentList.Add(string (self.StartTime.ToUniversalTime().Ticks))
    psi.ArgumentList.Add "--ttl"
    psi.ArgumentList.Add "30m"
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
    let proc = Process.Start psi
    use client = new Net.Http.HttpClient()
    client.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
    client.Timeout <- TestTimeouts.httpProbe
    let! ready =
      SageFs.Tests.TestInfrastructure.waitForAsync
        (int Timeouts.integrationDaemonReady.TotalMilliseconds)
        (fun () -> task {
          try
            let! resp = client.GetAsync "/health"
            return int resp.StatusCode > 0
          with _ -> return false
        })
    match ready with
    | true -> return proc, port
    | false ->
      killProcess proc
      proc.Dispose()
      return failwithf "the lab daemon failed to start on port %d within %.0fs" port Timeouts.integrationDaemonReady.TotalSeconds
  }

let textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let callWithin (budget: TimeSpan) (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(budget)
    try
      let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
      return textOf result
    with :? OperationCanceledException when cts.IsCancellationRequested ->
      return failtestf "tool '%s' did not answer within %.0fs (test budget)" name budget.TotalSeconds
  }

let callTool = callWithin TestTimeouts.toolCall
let callToolPatient =callWithin TestTimeouts.toolCallThatRestarts

let field (marker: string) (text: string) : string =
  let m = Regex.Match(text, Regex.Escape marker + @"(\S+)")
  match m.Success with
  | true -> m.Groups[1].Value
  | false -> failwithf "the reply names no '%s': %s" marker text

/// The one line of `get_cohort_status` for a landing, with its continuation lines.
let landingLine (status: string) (landingId: string) : string option =
  let lines = status.Split('\n')
  match lines |> Array.tryFindIndex (fun (l: string) -> l.TrimStart().StartsWith(sprintf "- %s " landingId, StringComparison.Ordinal)) with
  | None -> None
  | Some first ->
    let continuation =
      lines
      |> Array.skip (first + 1)
      |> Array.takeWhile (fun (l: string) -> l.Length > 0 && Char.IsWhiteSpace l.[0] && not (l.TrimStart().StartsWith("- ", StringComparison.Ordinal)))
    Some (String.Join("\n", Array.append [| lines.[first] |] continuation))

/// `sagefs call <tool>` as the member whose token it carries, the way an agent's shell runs it.
let asMember (port: int) (token: string) (tool: string) (args: (string * string) list) : Task<McpCallIntegrationTests.Run> =
  McpCallIntegrationTests.callAs port (Some token) (tool :: (args |> List.collect (fun (name, value) -> [ "--" + name; value ])))

let succeeded (what: string) (run: McpCallIntegrationTests.Run) : string =
  run.ExitCode |> Expect.equal (sprintf "%s (stderr: %s)" what run.Err) 0
  run.Out

[<Tests>]
let tests =
  Integration.hostList "A cohort for a repository the daemon was not started in (real daemon, real processes)" [

    testTask "WHY - the operator seats a conductor, the conductor configures the integration for the repository it names, a member lands a change, and a landing that breaks a test is refused" {
      gitAvailable () |> Expect.isTrue "git is installed"
      let world = Directory.CreateTempSubdirectory("cohort-lab-").FullName
      let lab = Path.Combine(world, "lab")
      let memberCheckout = Path.Combine(world, "member")
      let neutral = Path.Combine(world, "neutral")
      let dataDir = Path.Combine(world, "data")
      for dir in [ lab; neutral; dataDir ] do Directory.CreateDirectory dir |> ignore
      let! _ = git lab [ "init"; "--quiet"; "-b"; "main" ]
      let! _ = git lab [ "config"; "user.email"; "cohort-lab@example.com" ]
      let! _ = git lab [ "config"; "user.name"; "Cohort Lab" ]
      File.WriteAllText(Path.Combine(lab, "Lab.fsproj"), labProject ())
      File.WriteAllText(Path.Combine(lab, "Lab.fs"), labSource)
      File.WriteAllText(Path.Combine(lab, "Tests.fs"), labTests)
      File.WriteAllText(Path.Combine(lab, "Program.fs"), labProgram)
      File.Copy(Path.Combine(repoRoot, "global.json"), Path.Combine(lab, "global.json"))
      File.WriteAllText(Path.Combine(lab, ".gitignore"), "bin/\nobj/\n")
      let! _ = git lab [ "add"; "-A" ]
      let! _ = git lab [ "commit"; "--quiet"; "-m"; "base: the lab project" ]
      let! baseSha = git lab [ "rev-parse"; "HEAD" ]
      let! _ = git lab [ "worktree"; "add"; "--quiet"; "--detach"; memberCheckout; baseSha ]

      let! daemon, port = startDaemonIn neutral dataDir
      try
        // Two connections are two members of the lab repository's cohort. The first in is the conductor.
        use! first = connect port
        use! second = connect port
        let! _ = callTool first "join_cohort" [ "agentName", box "first"; "role", box "Implementer"; "working_directory", box lab ]
        let! _ = callTool second "join_cohort" [ "agentName", box "second"; "role", box "Implementer"; "working_directory", box lab ]
        let! _ = callTool first "leave_cohort" [ "agentName", box "first"; "working_directory", box lab ]

        // The seat is vacant. A person (the operator credential) looks at it and seats the member who is here.
        let! looked = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Look lab)
        let look : CohortSeatRoute.SeatReply =
          match looked with
          | Ok reply -> reply
          | Error failure -> failtestf "look at the seat: %s" (MemberMintCommand.MintFailure.describe failure)
        look.Seat |> Expect.equal "the seat is vacant after the conductor left" "vacant"
        let secondId = (List.head look.Present).Id
        let! seated = CohortSeatCommand.call port dataDir (CohortSeatCommand.Ask.Seat(lab, secondId))
        match seated with
        | Ok (reply: CohortSeatRoute.SeatReply) -> reply.Seat |> Expect.equal "the seat is bound" "bound"
        | Error failure -> failtestf "seat the conductor: %s" (MemberMintCommand.MintFailure.describe failure)

        // A project the conductor names that is not in the repository is refused with where it looked, before anything is configured.
        let! unknownProject =
          callToolPatient second "set_integration_ref"
            [ "agentName", box "second"; "integrationRef", box "HEAD"; "working_directory", box lab; "projects", box "Nope/Nope.fsproj" ]
        unknownProject |> Expect.stringContains "a project that is not there is refused" "projects not found in the integration worktree"
        unknownProject |> Expect.stringContains "naming it" "Nope/Nope.fsproj"

        // THE CALL THAT WAS REFUSED: the conductor configures the integration for the repository it names. The daemon's own
        // cohort is about the empty directory it started in, where nobody is seated.
        let! configured =
          callToolPatient second "set_integration_ref"
            [ "agentName", box "second"; "integrationRef", box "HEAD"; "working_directory", box lab ]
        configured |> Expect.stringContains "the integration is configured, not refused" "Integration configured: head="
        configured |> Expect.stringContains "and its session started" "session="
        let integrationBranch = field "branch=" configured
        let integrationWorktree = field "worktree=" configured
        Directory.Exists integrationWorktree |> Expect.isTrue "the integration worktree exists on disk"
        let! tip0 = git lab [ "rev-parse"; sprintf "refs/heads/%s" integrationBranch ]
        tip0 |> Expect.equal "the integration branch starts at the repository's HEAD" baseSha

        // The status read for the lab names the integration; the daemon's own cohort does not.
        let! status = callTool second "get_cohort_status" [ "working_directory", box lab ]
        status |> Expect.stringContains "the lab's status shows its integration session" "Integration session:"
        status |> Expect.stringContains "and that it started" "(started)"

        // One member token, minted by the operator for a second checkout of the repository.
        let! minted =
          McpCallIntegrationTests.runSagefs
            [ "SAGEFS_DATA_DIR", dataDir ]
            [ McpCallIntegrationTests.memberTokenVariable; McpCallIntegrationTests.operatorTokenVariable ]
            [ "member"; "mint"; "--mcp-port"; string port; "--role"; "Implementer"; "--dir"; memberCheckout; "--print-env"; McpCallIntegrationTests.memberTokenVariable ]
        let token = McpCallIntegrationTests.mintedToken minted

        let claimOn (path: string) : Task<string * string> =
          task {
            let! claimed =
              asMember port token "acquire_claim"
                [ "agentName", "member"; "scope", sprintf "file:%s" path; "purpose", "lab edit"; "working_directory", memberCheckout ]
            let text = succeeded "the member claims the file" claimed
            let m = Regex.Match(text, @"Acquired claim (\S+) over .*\(fence=(\d+)\)")
            m.Success |> Expect.isTrue (sprintf "the claim is reported: %s" text)
            return m.Groups[1].Value, m.Groups[2].Value
          }

        let landEdit (greeting: string) (branch: string) (startPoint: string) : Task<string * string> =
          task {
            let! claimId, fence = claimOn "Lab.fs"
            let! _ = git memberCheckout [ "checkout"; "--quiet"; "-b"; branch; startPoint ]
            File.WriteAllText(Path.Combine(memberCheckout, "Lab.fs"), labGreeting greeting)
            let! _ = git memberCheckout [ "add"; "Lab.fs" ]
            let! _ = git memberCheckout [ "commit"; "--quiet"; "-m"; sprintf "the greeting is %s" greeting ]
            let! sha = git memberCheckout [ "rev-parse"; "HEAD" ]
            let! queued =
              asMember port token "request_landing"
                [ "agentName", "member"
                  "claims", sprintf "%s:%s" claimId fence
                  "commits", sha
                  "statement", sprintf "the greeting is %s" greeting
                  "working_directory", memberCheckout ]
            let text = succeeded "the landing is queued" queued
            let m = Regex.Match(text, @"Landing (\S+) queued")
            m.Success |> Expect.isTrue (sprintf "the landing is queued: %s" text)
            return m.Groups[1].Value, sha
          }

        let awaitSettled (landingId: string) : Task<string> =
          task {
            let last = ref ""
            let! settled =
              SageFs.Tests.TestInfrastructure.waitForAsync (int TestTimeouts.heavyVerdictBudget.TotalMilliseconds) (fun () -> task {
                let! run = asMember port token "get_cohort_status" [ "working_directory", memberCheckout ]
                last.Value <- run.Out
                match landingLine run.Out landingId with
                | Some line -> return line.Contains "state=Landed" || line.Contains "state=Blocked"
                | None -> return false })
            match settled with
            | true -> return (landingLine last.Value landingId).Value
            | false -> return failtestf "landing %s did not settle. Last status:\n%s" landingId last.Value
          }

        // A landing that is good: the greeting still starts with hello.
        let! goodId, goodSha = landEdit "hello, world" "member-good" baseSha
        let! goodLine = awaitSettled goodId
        goodLine |> Expect.stringContains (sprintf "the good landing landed. Line: %s" goodLine) "state=Landed"
        let! tipAfterGood = git lab [ "rev-parse"; sprintf "refs/heads/%s" integrationBranch ]
        (tipAfterGood <> baseSha) |> Expect.isTrue "the integration branch moved"
        let! changed = git lab [ "show"; sprintf "%s:Lab.fs" tipAfterGood ]
        changed |> Expect.stringContains "and holds the member's change" "hello, world"
        ignore goodSha

        // The trunk follows what lands in the cohort the integration serves, which is not the daemon's own cohort here: it says what
        // it did with the landing.
        let! trunkSpoke =
          SageFs.Tests.TestInfrastructure.waitForAsync (int TestTimeouts.saveVerdict.TotalMilliseconds) (fun () -> task {
            let! run = asMember port token "get_cohort_status" [ "working_directory", memberCheckout ]
            return run.Out.Contains(sprintf "trunk %s:" goodId) })
        trunkSpoke |> Expect.isTrue "the trunk reported the landing that landed in the lab cohort"

        // A landing that breaks the test: the greeting no longer starts with hello. The gate refuses it.
        let! badId, _ = landEdit "goodbye" "member-bad" tipAfterGood
        let! badLine = awaitSettled badId
        badLine |> Expect.stringContains (sprintf "the landing that breaks a test is refused. Line: %s" badLine) "state=Blocked"
        let! tipAfterBad = git lab [ "rev-parse"; sprintf "refs/heads/%s" integrationBranch ]
        tipAfterBad |> Expect.equal "the integration branch did not move for the refused landing" tipAfterGood
      finally
        killProcess daemon
        try Directory.Delete(world, true) with _ -> ()
    }
  ]
