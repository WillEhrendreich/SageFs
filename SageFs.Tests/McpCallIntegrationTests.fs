module SageFs.Tests.McpCallIntegrationTests

/// `sagefs call` as a process, against a real daemon of its own (own port, own data directory, owned by this test
/// run). The point is identity. Every `sagefs call` is a new process and so a new MCP connection, and the daemon
/// ties a session to the identity that made it. A member token outranks the connection it arrives on, so the same
/// token is the same caller from one process to the next: a session one call makes is stopped by another. With no
/// token each call is a stranger, and `stop_session` is refused the way docs/mcp-tools.md says.

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpCall

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The checkout the spawned daemon runs in, so it is the checkout a token is minted for.
let daemonRepo : string = RepoPaths.repoPathFull [||]

type Run = { ExitCode: int; Out: string; Err: string }

/// `sagefs <args>` as a process. `env` is set after the shared spawn environment, and `without` names variables the
/// child must not inherit (a token this test run was started with is not the caller's).
let runSagefs (env: (string * string) list) (without: string list) (args: string list) : Task<Run> =
  task {
    let psi = ProcessStartInfo(TestInfrastructure.SageFsBinary.path ())
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.WorkingDirectory <- daemonRepo
    TestInfrastructure.SpawnEnvironment.apply psi
    for name in without do
      psi.Environment.Remove name |> ignore
    for name, value in env do
      psi.Environment[name] <- value
    for arg in args do
      psi.ArgumentList.Add arg
    use proc = Process.Start psi
    let out = proc.StandardOutput.ReadToEndAsync()
    let err = proc.StandardError.ReadToEndAsync()
    use bound = new CancellationTokenSource(TestTimeouts.patience * 3.)
    do! proc.WaitForExitAsync bound.Token
    let! o = out
    let! e = err
    return { ExitCode = proc.ExitCode; Out = o; Err = e }
  }

let memberTokenVariable = Capability.CapabilityTransport.bridgeEnvVar
let operatorTokenVariable = Capability.CapabilityTransport.operatorEnvVar

/// `sagefs call <args>` with no token of any kind in its environment, unless `token` names one.
let callAs (port: int) (token: string option) (args: string list) : Task<Run> =
  let tokenEnv = token |> Option.map (fun value -> memberTokenVariable, value) |> Option.toList
  runSagefs ([ "SAGEFS_MCP_PORT", string port ] @ tokenEnv) [ memberTokenVariable; operatorTokenVariable ] ("call" :: args)

let withDaemon (body: int -> string -> Task<unit>) : Task<unit> =
  task {
    let dataDir = Path.Combine(Path.GetTempPath(), "sagefs-test-call", Guid.NewGuid().ToString "N")
    let! proc, port = SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith [ "SAGEFS_DATA_DIR", dataDir ]
    try
      do! body port dataDir
    finally
      SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
      try Directory.Delete(dataDir, true) with _ -> ()
  }

/// The token `sagefs member mint --print-env` printed: the one export line on stdout.
let mintedToken (run: Run) : string =
  match Regex.Match(run.Out, @"export SAGEFS_MEMBER_TOKEN=(sfm_[A-Za-z0-9_\-]+)") with
  | m when m.Success -> m.Groups.[1].Value
  | _ -> failtestf "member mint did not print a token (exit %d): %s %s" run.ExitCode run.Out run.Err

let sessionIdIn (created: string) : string =
  match Regex.Match(created, "[0-9a-f]{8}") with
  | m when m.Success -> m.Value
  | _ -> failtestf "create_bare_session did not name a session: %s" created

[<Tests>]
let tests =
  Integration.hostList "sagefs call" [

    testTask "WHY - one token is one caller across processes: a session one call makes is stopped by the next, and with no token the daemon refuses it, saying so" {
      do!
        withDaemon (fun port dataDir ->
          task {
            // The tools, as an anonymous caller, with the identity said plainly.
            let! listed = callAs port None [ "--list" ]
            listed.ExitCode |> Expect.equal (sprintf "--list works (stderr: %s)" listed.Err) 0
            listed.Out |> Expect.stringStarts "who is calling comes first" "identity: anonymous"
            listed.Out |> Expect.stringContains "a tool the daemon has" "stop_session"

            // A wrong argument name fails, and names the real one.
            let! slip = callAs port None [ "stop_session"; "--sessionId"; "abc" ]
            slip.ExitCode |> Expect.equal "a name the tool does not have is refused" 1
            slip.Out |> Expect.equal "with nothing on stdout" ""
            slip.Err |> Expect.stringContains "naming the slip" "'--sessionId' is not a parameter of stop_session"
            slip.Err |> Expect.stringContains "and the real parameter" "session_id (string, required)"
            let! slipViaJson = callAs port None [ "stop_session"; "--json"; """{"sessionId":"abc"}""" ]
            slipViaJson.ExitCode |> Expect.equal "--json is held to the same names" 1

            // A token from `member mint --print-env`, the way a launcher gets one.
            let! minted =
              runSagefs
                [ "SAGEFS_DATA_DIR", dataDir ]
                [ memberTokenVariable; operatorTokenVariable ]
                [ "member"; "mint"; "--mcp-port"; string port; "--role"; "Implementer"; "--dir"; daemonRepo; "--print-env"; memberTokenVariable ]
            minted.ExitCode |> Expect.equal (sprintf "mint works (stderr: %s)" minted.Err) 0
            let token = mintedToken minted

            // A read-only tool, as the member.
            let! status = callAs port (Some token) [ "get_daemon_status" ]
            status.ExitCode |> Expect.equal (sprintf "status works (stderr: %s)" status.Err) 0
            status.Out |> Expect.stringContains "the tool's own answer on stdout" "daemonPid"
            let! memberListed = callAs port (Some token) [ "--list" ]
            memberListed.Out |> Expect.stringContains "the listing names the identity" "identity: a member token from SAGEFS_MEMBER_TOKEN"
            memberListed.Out.Contains token |> Expect.isFalse "and never the token"

            // A session one process makes is stopped by another, because both are the same member.
            let! created = callAs port (Some token) [ "create_bare_session"; "--working_directory"; daemonRepo ]
            created.ExitCode |> Expect.equal (sprintf "the session is created (stderr: %s)" created.Err) 0
            let first = sessionIdIn created.Out
            let! stopped = callAs port (Some token) [ "stop_session"; "--session_id"; first ]
            stopped.ExitCode |> Expect.equal (sprintf "the same token stops it from a new process (stderr: %s)" stopped.Err) 0
            let! afterStop = callAs port (Some token) [ "list_sessions" ]
            afterStop.Out.Contains first |> Expect.isFalse "and it is gone"

            // With no token the next process is a stranger, and the refusal is an error that says what to do.
            let! createdAgain = callAs port (Some token) [ "create_bare_session"; "--working_directory"; daemonRepo ]
            createdAgain.ExitCode |> Expect.equal (sprintf "a second session is created (stderr: %s)" createdAgain.Err) 0
            let second = sessionIdIn createdAgain.Out
            let! refused = callAs port None [ "stop_session"; "--session_id"; second ]
            refused.ExitCode |> Expect.equal "an anonymous stop is a failure" 1
            refused.Out |> Expect.equal "nothing on stdout" ""
            refused.Err |> Expect.stringContains "the daemon's full text" "was made by another member"
            refused.Err |> Expect.stringContains "and that the call was anonymous" "carried no member token"
            refused.Err |> Expect.stringContains "and what to do" "sagefs member mint"

            let! cleanup = callAs port (Some token) [ "stop_session"; "--session_id"; second ]
            cleanup.ExitCode |> Expect.equal (sprintf "the token stops it (stderr: %s)" cleanup.Err) 0
          })
    }

    testTask "WHY - a daemon that is not running gets the answer sagefs status gives, with the command to start one, and the call ends" {
      let port = fst (TestInfrastructure.TestPorts.reservePair ())
      let! run = callAs port None [ "--list" ]
      run.ExitCode |> Expect.equal "exit 1" 1
      run.Out |> Expect.equal "nothing on stdout" ""
      run.Err |> Expect.stringContains "says so" "No daemon running"
      run.Err |> Expect.stringContains "and how to start it" "claude mcp add sagefs -- sagefs mcp"
    }

    testTask "WHY - <tool> --help against a real daemon prints every parameter the tool's own registration declares, and marks the required ones" {
      do!
        withDaemon (fun port _ ->
          task {
            // The expected parameters come from the tool's registration, read by reflection, never from a literal.
            let declared (toolName: string) : (string * bool) list =
              let flags = Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.DeclaredOnly
              typeof<SageFs.Server.McpTools.SageFsTools>.GetMethods flags
              |> Array.find (fun m -> m.Name = toolName)
              |> fun m -> m.GetParameters() |> Array.map (fun p -> p.Name, not p.IsOptional) |> List.ofArray
            for toolName in [ "stop_session"; "get_session_status"; "create_project_session" ] do
              let! help = callAs port None [ toolName; "--help" ]
              help.ExitCode |> Expect.equal (sprintf "%s --help works (stderr: %s)" toolName help.Err) 0
              help.Out |> Expect.stringStarts "the tool and its sentence come first" (toolName + ": ")
              let lines = help.Out.Split '\n' |> List.ofArray
              let declaredParams = declared toolName
              declaredParams |> Expect.isNonEmpty (sprintf "%s declares parameters, so the check has something to read" toolName)
              for name, required in declaredParams do
                let line = lines |> List.tryFind (fun line -> line.StartsWith("  " + name + " ("))
                match line with
                | None -> failtestf "%s --help names no line for parameter %s:\n%s" toolName name help.Out
                | Some found -> found.Contains ", required" |> Expect.equal (sprintf "%s.%s is marked required exactly when it is" toolName name) required
              help.Out |> Expect.stringContains "points at the full manual" (sprintf "discover_features --tool %s" toolName)
            let! unknown = callAs port None [ "stop_sesion"; "--help" ]
            unknown.ExitCode |> Expect.equal "an unlisted name fails" 1
            unknown.Err |> Expect.stringContains "names the nearest tool" "stop_session"
            unknown.Err |> Expect.stringContains "and points at the list" "sagefs call --list"
          })
    }

    testTask "WHY - SageFs --help lists the call command" {
      let! help = runSagefs [] [] [ "--help" ]
      help.Out |> Expect.stringContains "the usage line" "SageFs call --list"
    }
  ]
