// Follow the local release gate while it runs.
//
//   dotnet fsi scripts/gate-tail.fsx                 follow every tier of the newest gate run, live, one prefixed line each
//   dotnet fsi scripts/gate-tail.fsx -- --status     one screen: each tier, running or finished, its size and last line
//   dotnet fsi scripts/gate-tail.fsx -- <tier>       follow only the tiers whose log name contains <tier> (host-3of5, lt, hr)
//   dotnet fsi scripts/gate-tail.fsx -- --from-start replay each log from its first line before following
//   dotnet fsi scripts/gate-tail.fsx -- --all        include logs older than three hours (left over from earlier runs)
//
// Why this exists: `scripts/ship.fsx` and the gate run detached so they outlive the shell that started them, which
// means a shell list only shows the command. The output is in files; this reads them. It writes nothing and stops
// nothing, so Ctrl-C only ends the viewer, never the gate.
//
// Where the files are: the gate keeps one checkout per commit under ~/.local/share/sagefs-gate/checkout-<id>, and its
// tier logs in checkout-<id>.tiers/<tier>.log (one per tier or shard, written while the tier runs). Its own log of the
// whole pass is logs/<run>.log. The newest of those by write time is "the gate that is running".

open System
open System.IO
open System.Threading

let gateRoot =
  match Environment.GetEnvironmentVariable "SAGEFS_GATE_DIR" with
  | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share", "sagefs-gate")
  | dir -> dir

/// A tier log written within this long ago counts as running. The gate flushes as it goes, so a quiet minute is a
/// tier waiting on a build or a slow test, and it is still running.
let runningWindow = TimeSpan.FromMinutes 2.0

/// A log older than this belongs to an earlier run (the gate's own cap is 90 minutes), so it is left out unless
/// `--all` asks for it. The tiers directory keeps old shard logs from before a re-split, and they read as stuck tiers.
let staleAfter = TimeSpan.FromHours 3.0

/// How often the follower looks for new lines. A viewer, not a product wait: nothing depends on it.
let followInterval = TimeSpan.FromMilliseconds 700.0

type Args =
  { Status: bool
    FromStart: bool
    All: bool
    Tier: string }

let parseArgs (argv: string list) : Args =
  let flags = argv |> List.filter (fun a -> a.StartsWith "--")
  let known = [ "--status"; "--from-start"; "--all" ]
  match flags |> List.filter (fun f -> not (List.contains f known)) with
  | [] -> ()
  | unknown ->
    eprintfn "gate-tail: unknown option %s. Options: --status, --from-start, --all, or a tier name to follow." (String.concat ", " unknown)
    exit 2
  { Status = List.contains "--status" argv
    FromStart = List.contains "--from-start" argv
    All = List.contains "--all" argv
    Tier = argv |> List.filter (fun a -> not (a.StartsWith "--")) |> List.tryHead |> Option.defaultValue "" }

let newestTiersDir () : string option =
  match Directory.Exists gateRoot with
  | false -> None
  | true ->
    Directory.GetDirectories(gateRoot, "checkout-*.tiers")
    |> Array.sortByDescending (fun d ->
      match Directory.GetFiles(d, "*.log") with
      | [||] -> Directory.GetLastWriteTimeUtc d
      | logs -> logs |> Array.map File.GetLastWriteTimeUtc |> Array.max)
    |> Array.tryHead

/// `integration-host-3of5.log` -> `host-3of5`; the long prefix is the same on every line and hides the part that differs.
let shortName (file: string) : string =
  Path.GetFileNameWithoutExtension(file).Replace("integration-", "")

/// A rerun of a failed case is written beside its shard as `<shard>.rerun-N-M.log`; the follower shows those too.
let tierLogs (dir: string) (all: bool) (filter: string) : string list =
  let now = DateTime.UtcNow
  Directory.GetFiles(dir, "*.log")
  |> Array.filter (fun f -> all || now - File.GetLastWriteTimeUtc f <= staleAfter)
  |> Array.filter (fun f -> filter = "" || Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
  |> Array.sortBy Path.GetFileName
  |> List.ofArray

let lastLine (file: string) : string =
  try
    use stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    let window = int64 (min stream.Length 4096L)
    stream.Seek(-window, SeekOrigin.End) |> ignore
    use reader = new StreamReader(stream)
    let tail = reader.ReadToEnd().TrimEnd().Split('\n')
    match tail |> Array.tryLast with
    | Some line -> (if line.Length > 110 then line.Substring(0, 110) + "..." else line).TrimEnd()
    | None -> ""
  with ex -> sprintf "(unreadable: %s)" ex.Message

let showStatus (dir: string) (all: bool) (filter: string) : unit =
  let now = DateTime.UtcNow
  printfn "gate run: %s" dir
  for file in tierLogs dir all filter do
    let age = now - File.GetLastWriteTimeUtc file
    let state = if age <= runningWindow then "running " else "quiet   "
    printfn "%s %-26s %7.0f KB  %5.0fs ago  %s" state (shortName file) (float (FileInfo file).Length / 1024.0) age.TotalSeconds (lastLine file)

/// Prints what each log has gained since its recorded offset, one prefixed line each, and returns the new offsets.
let drain (offsets: Map<string, int64>) (files: string list) : Map<string, int64> =
  files
  |> List.fold
    (fun (acc: Map<string, int64>) file ->
      let start = Map.tryFind file acc |> Option.defaultValue 0L
      try
        use stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let from = if stream.Length < start then 0L else start
        stream.Seek(from, SeekOrigin.Begin) |> ignore
        use reader = new StreamReader(stream)
        let text = reader.ReadToEnd()
        // Only whole lines: a half-written line waits for the next pass instead of printing in two pieces.
        let complete = text.LastIndexOf '\n'
        match complete with
        | -1 -> acc
        | last ->
          let name = shortName file
          for line in text.Substring(0, last).Split('\n') do
            printfn "[%s] %s" name (line.TrimEnd '\r')
          Map.add file (from + int64 (Text.Encoding.UTF8.GetByteCount(text.Substring(0, last + 1)))) acc
      with ex ->
        eprintfn "gate-tail: could not read %s: %s" file ex.Message
        acc)
    offsets

let follow (dir: string) (args: Args) : unit =
  let initial =
    tierLogs dir args.All args.Tier
    |> List.map (fun f -> f, (if args.FromStart then 0L else (FileInfo f).Length))
    |> Map.ofList
  printfn "following %s%s (Ctrl-C ends this viewer, not the gate)" dir (if args.Tier = "" then "" else sprintf " [%s]" args.Tier)
  showStatus dir args.All args.Tier
  printfn "----"
  let rec loop (offsets: Map<string, int64>) =
    // A tier that starts later writes a new log; pick it up from its first line.
    let current = tierLogs dir args.All args.Tier
    let withNew =
      current |> List.fold (fun acc f -> if Map.containsKey f acc then acc else Map.add f 0L acc) offsets
    let next = drain withNew current
    Thread.Sleep followInterval
    loop next
  loop (if args.FromStart then Map.empty else initial)

let args = parseArgs (fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray)

match newestTiersDir () with
| None ->
  eprintfn "gate-tail: no gate run found under %s (set SAGEFS_GATE_DIR if the gate keeps its files elsewhere)." gateRoot
  exit 1
| Some dir ->
  match args.Status with
  | true -> showStatus dir args.All args.Tier
  | false -> follow dir args
