/// What a first run on Linux trips over. A refused connection is the normal
/// answer to "is the daemon running", so the probe must not warn about it, and
/// the systemd unit we ship has to name flags the CLI really has.
module SageFs.Tests.FirstRunLinuxTests

open System
open System.IO
open System.Net.Http
open System.Net.Sockets
open Expecto
open Expecto.Flip
open SageFs

let private refusedException () =
  let socket = SocketException(int SocketError.ConnectionRefused)
  HttpRequestException("Connection refused (localhost:39990)", socket)

let private repoRoot =
  let rec up (dir: DirectoryInfo) =
    match dir with
    | null -> failwith "repo root (the folder holding Readme.md and contrib/) not found above the test assembly"
    | d when File.Exists(Path.Combine(d.FullName, "Readme.md")) && Directory.Exists(Path.Combine(d.FullName, "contrib")) -> d.FullName
    | d -> up d.Parent
  up (DirectoryInfo AppContext.BaseDirectory)

/// `[Section]` headers and `Key=value` lines, the INI shape systemd unit files use.
let private parseUnit (text: string) : (string * (string * string) list) list =
  let lines =
    text.Split('\n')
    |> Array.map (fun l -> l.Trim())
    |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#") && not (l.StartsWith ";"))
  let folder (sections: (string * (string * string) list) list) (line: string) =
    match line.StartsWith "[" && line.EndsWith "]" with
    | true -> (line.Trim('[', ']'), []) :: sections
    | false ->
      match sections, line.IndexOf '=' with
      | (name, pairs) :: rest, i when i > 0 -> (name, pairs @ [ line.Substring(0, i), line.Substring(i + 1) ]) :: rest
      | _ -> failwithf "line is neither a section header nor Key=value: %s" line
  lines |> Array.fold folder [] |> List.rev

[<Tests>]
let tests =
  testList "first run on Linux" [
    testList "probe failure classification" [
      test "a refused connection is classified as Refused" {
        DaemonState.classifyProbeFailure (refusedException ())
        |> Expect.equal "refused" DaemonState.ProbeFailure.Refused
      }

      test "a refused connection inside an AggregateException is still Refused" {
        DaemonState.classifyProbeFailure (AggregateException(refusedException ()))
        |> Expect.equal "refused, unwrapped" DaemonState.ProbeFailure.Refused
      }

      test "a timeout is Unexpected and carries its message" {
        match DaemonState.classifyProbeFailure (System.Threading.Tasks.TaskCanceledException("The request was canceled")) with
        | DaemonState.ProbeFailure.Unexpected message ->
          message |> Expect.stringContains "keeps the message" "canceled"
        | DaemonState.ProbeFailure.Refused -> failtest "a timeout means something answered slowly or not at all, never a refusal"
      }

      test "a host that does not resolve is Unexpected" {
        let socket = SocketException(int SocketError.HostNotFound)
        match DaemonState.classifyProbeFailure (HttpRequestException("no such host", socket)) with
        | DaemonState.ProbeFailure.Unexpected _ -> ()
        | DaemonState.ProbeFailure.Refused -> failtest "only ConnectionRefused is the expected not-running answer"
      }
    ]

    testSequenced <| testList "contrib/systemd/sagefs.service" [
      let unitPath = Path.Combine(repoRoot, "contrib", "systemd", "sagefs.service")

      test "exists and parses as an INI-style unit with the three sections" {
        Expect.isTrue "the unit file is shipped" (File.Exists unitPath)
        let sections = parseUnit (File.ReadAllText unitPath) |> List.map fst
        sections |> Expect.equal "Unit, Service, Install in order" [ "Unit"; "Service"; "Install" ]
      }

      test "is a user unit that restarts on failure and starts at login" {
        let sections = parseUnit (File.ReadAllText unitPath) |> Map.ofList
        sections.["Service"] |> List.contains ("Restart", "on-failure") |> Expect.isTrue "restarts on failure"
        sections.["Install"] |> List.contains ("WantedBy", "default.target") |> Expect.isTrue "user units hang off default.target"
      }

      test "ExecStart runs the installed tool with a flag the CLI help really lists" {
        let sections = parseUnit (File.ReadAllText unitPath) |> Map.ofList
        let execStart = sections.["Service"] |> List.find (fun (k, _) -> k = "ExecStart") |> snd
        let parts = execStart.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        parts.[0] |> Expect.stringEnds "the installed global tool path" ".dotnet/tools/sagefs"
        let flags = parts |> Array.skip 1
        Expect.isNonEmpty "ExecStart passes at least one flag" flags
        let original = Console.Out
        use writer = new StringWriter()
        Console.SetOut writer
        let help =
          try
            Program.main [| "--help" |] |> ignore
            writer.ToString()
          finally
            Console.SetOut original
        for flag in flags do
          help |> Expect.stringContains (sprintf "--help lists %s" flag) flag
      }
    ]
  ]
