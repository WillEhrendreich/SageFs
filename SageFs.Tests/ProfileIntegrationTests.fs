module SageFs.Tests.ProfileIntegrationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The outcome gate of the `profile` tool, over a real MCP connection to a real daemon this suite spawns, with a real isolated
/// FSI host under it and the real `dotnet-trace` recording that host. The known hot function is `hotFib`, a recursive function
/// the eval defines and then runs for a few seconds; the report has to name it, first, as the user's own code.
///
/// The allocation half is the second case: a record type the eval defines and allocates 64 MB of, which the report has to name
/// first by type and place at the function that allocated it. An unknown mode is refused before a session is looked up, so the
/// code does not run, and the third case holds that to its word. Each case has a daemon of its own, which takes its session
/// with it when the case ends.

let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let private callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, CancellationToken.None)
    return textOf result
  }

/// "<id>\nRequested target: ..." is what a created session answers; the id is the first word.
let private sessionIdOf (created: string) : string = created.Split([| ' '; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries).[0]

/// A bare session in an empty directory, Ready. The status call waits on the daemon's own event, so nothing here polls.
let private readySession (client: McpClient) : Task<string> =
  task {
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-profile-it", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    let! created = callTool client "create_bare_session" [ "working_directory", box dir ]
    let sid = sessionIdOf created
    let! status = callTool client "get_session_status" [ "session_id", box sid; "wait_seconds", box 60 ]
    status |> Expect.stringContains "the session reached Ready" "\"lifecycle\":\"Ready\""
    return sid
  }

/// Run `body` against a Ready session of a daemon of its own. The daemon is killed when the body ends, and its sessions with it.
let private inSession (body: McpClient -> string -> Task<unit>) : Task<unit> =
  SageFs.Tests.CohortMcpToolsIntegrationTests.withDaemon (fun port ->
    task {
      use! client = SageFs.Tests.CohortMcpToolsIntegrationTests.connect port
      let! sid = readySession client
      do! body client sid
    })

/// Like `inSession`, with `environment` on the daemon (and so on its worker and the session's host).
let private inSessionWith (environment: (string * string) list) (body: McpClient -> string -> Task<unit>) : Task<unit> =
  task {
    let! daemon, port = SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith environment
    try
      use! client = SageFs.Tests.CohortMcpToolsIntegrationTests.connect port
      let! sid = readySession client
      do! body client sid
    finally
      SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon daemon
  }

let private hotLoop =
  "let rec hotFib n = if n < 2 then n else hotFib (n - 1) + hotFib (n - 2);;\nlet total = [ for _ in 1 .. 40 -> hotFib 32 ] |> List.sum;;\nprintfn \"total=%d\" total;;"

/// A record type the eval defines, and a function that allocates 64 MB of it (5 x 400,000 x 32 bytes) with an array of them each time.
let private allocLoop =
  "type Payload = { Id: int; Name: string };;\nlet makeThings () = Array.init 400000 (fun i -> { Id = i; Name = \"x\" }) |> Array.length;;\nlet made = [ for _ in 1 .. 5 -> makeThings () ] |> List.sum;;\nprintfn \"made=%d\" made;;"

/// The first row under a heading, so a test reads what leads a table and not what is somewhere in it.
let private firstRowUnder (heading: string) (text: string) : string option =
  let lines = text.Split('\n')
  lines |> Array.tryFindIndex (fun l -> l.StartsWith heading) |> Option.bind (fun i -> lines |> Array.tryItem (i + 1))

[<Tests>]
let tests =
  Integration.hostList "profile tool over a real daemon, host and recorder" [

    testTask "profile names the hot function of a real eval, first, as the user's own code" {
      do!
        inSession (fun client sid ->
          task {
            let! reply = callTool client "profile" [ "agentName", box "profile-it"; "code", box hotLoop; "session_id", box sid ]
            reply |> Expect.stringContains "the eval's own output is in the reply" "total="
            reply |> Expect.stringContains "the report is the cpu one" "--- profile (cpu) ---"
            (firstRowUnder "Methods by self time" reply |> Option.defaultValue "")
            |> Expect.stringContains "the hot function leads the methods by self time" "hotFib"
            (firstRowUnder "Your code by total time" reply |> Option.defaultValue "")
            |> Expect.stringContains "and it is counted as the user's code" "FSI"
            reply |> Expect.stringContains "what was not counted is said" "Not counted above"
            reply.Length |> fun n -> (n <= 7000) |> Expect.isTrue "the reply stays bounded"
          })
    }

    testTask "profile still attaches when the daemon's TMPDIR is too long for the host's diagnostics socket" {
      // 70 characters of directory name under the usual temp: more than the 63 a socket path leaves room for (found in the release
      // gate, whose tier directories are 85). The host's TMPDIR is spelled through a short link, so its socket keeps its name.
      let longTmp = Path.Combine(Path.GetTempPath(), "sagefs-profile-longtmp", Guid.NewGuid().ToString "N", String('t', 70))
      Directory.CreateDirectory longTmp |> ignore
      try
        do!
          inSessionWith [ "TMPDIR", longTmp ] (fun client sid ->
            task {
              let! reply = callTool client "profile" [ "agentName", box "profile-it"; "code", box hotLoop; "session_id", box sid ]
              reply |> Expect.stringContains "the profile was made, not refused" "--- profile (cpu) ---"
              (firstRowUnder "Methods by self time" reply |> Option.defaultValue "")
              |> Expect.stringContains "and it names the hot function" "hotFib"
            })
      finally
        Directory.Delete(Path.GetDirectoryName longTmp |> Path.GetDirectoryName, true)
        // The link the host's TMPDIR went through now points at nothing; it is a file name to delete, in /tmp where it was made.
        File.Delete("/tmp/" + SageFs.Features.DiagnosticsSocket.linkName longTmp)
    }

    testTask "profile mode=alloc names the known allocating type first, and the function that allocated it" {
      do!
        inSession (fun client sid ->
          task {
            let! reply = callTool client "profile" [ "agentName", box "profile-it"; "code", box allocLoop; "mode", box "alloc"; "session_id", box sid ]
            reply |> Expect.stringContains "the eval's own output is in the reply" "made="
            reply |> Expect.stringContains "the report is the allocation one" "--- profile (alloc) ---"
            (firstRowUnder "Bytes by type" reply |> Option.defaultValue "")
            |> Expect.stringContains "the record type the eval allocates 64 MB of leads the bytes by type" "Payload"
            (firstRowUnder "Bytes by type at the nearest frame of yours" reply |> Option.defaultValue "")
            |> Expect.stringContains "and it is placed at the function that allocated it" "makeThings"
          })
    }

    testTask "an unknown mode is refused by name and the code does not run" {
      do!
        inSession (fun client sid ->
          task {
            let! reply = callTool client "profile" [ "agentName", box "profile-it"; "code", box "let ranUnderMemory = 1;;"; "mode", box "memory"; "session_id", box sid ]
            reply |> Expect.stringContains "refused by name" "mode 'memory' is not one of cpu, alloc"
            let! probe = callTool client "send_fsharp_code" [ "agentName", box "profile-it"; "code", box "ranUnderMemory;;"; "session_id", box sid ]
            (probe.Contains "val it: int = 1") |> Expect.isFalse "the refused code never ran, so the name has no value"
          })
    }

    testTask "a host that ends while it is profiled gives HostGone and the crash, never a hang or an empty table" {
      do!
        inSession (fun client sid ->
          task {
            let! reply = callTool client "profile" [ "agentName", box "profile-it"; "code", box "System.Environment.Exit 3;;"; "session_id", box sid ]
            reply |> Expect.stringContains "the profile says the host ended" "ended while it was recorded"
            (reply.Contains "Methods by self time") |> Expect.isFalse "and there is no table read from a dead process"
          })
    }
  ]
