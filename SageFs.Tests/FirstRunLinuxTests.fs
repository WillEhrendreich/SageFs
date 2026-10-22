/// What a first run on Linux trips over. A refused connection is the normal
/// answer to "is the daemon running", so the probe must not warn about it, and
/// the systemd unit we ship has to name flags the CLI really has.
module SageFs.Tests.FirstRunLinuxTests

open System
open System.IO
open System.Net.Http
open System.Net.Sockets
open System.Text.RegularExpressions
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

/// The README's Linux install block, one trimmed line per command.
let private readmeInstall () : string =
  let readme = File.ReadAllText(Path.Combine(repoRoot, "Readme.md"))
  let block = Regex.Match(readme, @"### Run it as a service \(Linux\)[\s\S]*?```bash\r?\n([\s\S]*?)```")
  Expect.isTrue "the README's Linux section has a bash install block" block.Success
  let install =
    block.Groups[1].Value.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun line -> line.Trim())
    |> Array.filter (fun line -> line <> "")
    |> String.concat "\n"
  Expect.isNotEmpty "the install block has commands" install
  install

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

      test "WorkingDirectory is a directory the unit itself creates, and one SageFs will watch from" {
        // A daemon started in $HOME hands $HOME out as a session root (a request that names no directory falls
        // back to the daemon's cwd), and SageFs refuses to watch a home directory, so a unit that put the
        // daemon there gave every such session no hot reload and no live testing.
        let service = (parseUnit (File.ReadAllText unitPath) |> Map.ofList).["Service"]
        let values key = service |> List.filter (fun (k, _) -> k = key) |> List.map snd
        let home = Path.Combine(Path.DirectorySeparatorChar.ToString(), "home", "someone")
        let stateHome = Path.Combine(home, ".local", "state")
        // The specifiers a user unit may use here: %h is the home directory, %S the state directory ($XDG_STATE_HOME).
        let expand (text: string) = text.Replace("%h", home).Replace("%S", stateHome)
        let workingDirectory = values "WorkingDirectory" |> List.exactlyOne |> expand
        FileWatcher.classifyWatchRoot home workingDirectory
        |> Expect.equal "the daemon's cwd is not the home directory or above it" FileWatcher.WatchRootVerdict.Watchable
        // StateDirectory=<name> is created by systemd under %S before the daemon starts.
        values "StateDirectory"
        |> List.map (fun name -> Path.Combine(stateHome, name))
        |> Expect.contains "the working directory is one the unit creates" workingDirectory
      }

      test "the README install gives the unit the install shell's PATH in a drop-in, because systemd's default has no npm and no dotnet" {
        // A user unit does not read the shell profile, and the daemon hands its PATH to every worker, so an init.fsx
        // that starts npm fails. A PATH written into the unit would be someone else's, so the unit sets none and the
        // install writes its own shell's into a drop-in systemd reads beside it.
        let service = (parseUnit (File.ReadAllText unitPath) |> Map.ofList).["Service"]
        service
        |> List.exists (fun (key, value) -> key = "Environment" && value.Contains "PATH=")
        |> Expect.isFalse "the shipped unit sets no PATH of its own"
        let install = readmeInstall ()
        install |> Expect.stringContains "the install creates the unit's drop-in folder" "mkdir -p ~/.config/systemd/user/sagefs.service.d"
        install
        |> Expect.stringContains "the drop-in sets PATH to the install shell's" """printf '[Service]\nEnvironment="PATH=%s"\n' "$PATH" > ~/.config/systemd/user/sagefs.service.d/path.conf"""
        install |> Expect.stringContains "systemd rereads the drop-in before the restart" "systemctl --user daemon-reload"
        install |> Expect.stringContains "a second install restarts the service, so it sees the new PATH" "systemctl --user restart sagefs"
      }

      test "the unit's comment carries the README's install block, line for line" {
        // The other tests read the README's install. The copy in the unit's comment is the one a reader of the
        // file follows, so it must not drift from the README's.
        let comment =
          File.ReadAllLines unitPath
          |> Array.takeWhile (fun line -> line.StartsWith "#")
          |> Array.map (fun line -> line.TrimStart('#').Trim())
          |> Array.filter (fun line -> line <> "")
          |> String.concat "\n"
        comment |> Expect.stringContains "the unit's comment repeats the README's install exactly" (readmeInstall ())
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
