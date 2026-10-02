/// LemScore: the F# command line behind scripts/lemmings/lib-cmd.sh.
///
///   LemScore free-model <model>              refuse (exit 2) unless the live catalog marks it FREE
///   LemScore run-id <root> <model> <task>    the next <model-short>-<task>-<nn> under root
///   LemScore daemon-check [--port N]         gate on the shared daemon; prints KEY=VALUE lines
///   LemScore cleanup --workdir D --out F     read residue, stop exactly those sessions, verify
///                    [--own ID,ID]             ids the harness made for the run: stopped, not residue
///   LemScore version-skew --daemon V --bridge V  warn when the bridge is not the daemon's build
///   LemScore sessions-under --workdir D     list the shared daemon's sessions under a directory
///   LemScore watch --workdir D --out F --stop-file S
///                                            record the sessions the dashboard API shows under D until S exists
///   LemScore replace-exact --file F --find S --replace S
///                                            replace exactly one match, or refuse and say why
///   LemScore expect --file F --pattern RE ...  every pattern must match the answer file
///   LemScore dll-version <path>              product version of a built SageFs.dll
///   LemScore version-skew --daemon V --bridge V
///                                            same|skewed|unknown: do the two come from the same commit
///   LemScore prune [id ...] [--older-than-days N] [--root R] [--dry-run]
///                                            delete finished runs under the run root
///   LemScore score --run-dir D ...           write D/out/summary.json
///
/// Exit codes: 0 ok, 2 refused (with the reason on stderr), 3 the shared daemon will not do.
module LemScore.Program

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open LemScore.Types
open LemScore.CmdcStream
open LemScore.Classify
open LemScore.SharedDaemon
open LemScore.Summary

module ExitCode =
  let ok = 0
  let usage = 1
  let refused = 2
  let daemonWontDo = 3

/// `--flag value` pairs after the subcommand.
let private flags (args: string list) : Map<string, string> =
  args
  |> List.chunkBySize 2
  |> List.choose (function [ k; v ] when k.StartsWith "--" -> Some (k.Substring 2, v) | _ -> None)
  |> Map.ofList

let private need (m: Map<string, string>) (key: string) : Result<string, string> =
  match Map.tryFind key m with
  | Some v -> Ok v
  | None -> Error (sprintf "missing --%s" key)

let private freeModelCommand (args: string list) : int =
  match args with
  | model :: rest ->
    let m = flags rest
    let catalogText =
      match Map.tryFind "catalog-file" m with
      | Some path -> Ok (File.ReadAllText path)
      | None -> Catalog.liveText ()
    match catalogText |> Result.bind (fun t -> Catalog.checkFree (Catalog.parse t) model) with
    | Ok entry ->
      printfn "%s is FREE: %s" entry.Id entry.Description
      ExitCode.ok
    | Error why ->
      eprintfn "refused: %s" why
      ExitCode.refused
  | [] ->
    eprintfn "usage: LemScore free-model <model>"
    ExitCode.usage

let private daemonCheckCommand (args: string list) : int =
  let m = flags args
  let port = Map.tryFind "port" m |> Option.map int |> Option.defaultValue defaultMcpPort
  let wait = not (Map.containsKey "no-wait" m)
  match useShared port wait with
  | Error why ->
    eprintfn "refused: %s" why
    ExitCode.daemonWontDo
  | Ok ready ->
    printfn "LEM_PORT=%d" port
    printfn "LEM_DASH_PORT=%d" (dashboardPortFor port)
    printfn "LEM_DAEMON_VERSION=%s" ready.Status.CoreVersion
    printfn "LEM_MEM_PRESSURE_START=%s" ready.Status.MemoryPressure
    printfn "LEM_MEM_AVAIL_START=%d" ready.Status.AvailableBytes
    printfn "LEM_LEASES_START=%d" ready.Status.ActiveLeases
    ExitCode.ok

let private writeResidue (path: string) (report: CleanupReport) : unit =
  use stream = new MemoryStream()
  use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
  let session (s: SessionInfo) =
    w.WriteStartObject()
    w.WriteString("id", s.Id)
    w.WriteString("status", s.Status)
    w.WriteString("workingDirectory", s.WorkingDirectory)
    w.WriteString("workflow", s.Workflow)
    w.WriteEndObject()
  w.WriteStartObject()
  w.WriteString("verdict", Cleanup.toString report.Verdict)
  w.WriteString("note", report.Note)
  w.WriteStartArray "found"
  report.Found |> List.iter session
  w.WriteEndArray()
  w.WriteStartArray "own"
  report.Own |> List.iter w.WriteStringValue
  w.WriteEndArray()
  w.WriteStartArray "stops"
  report.Stops
  |> List.iter (fun (id, r) ->
    w.WriteStartObject()
    w.WriteString("id", id)
    match r with
    | Ok () -> w.WriteString("result", "stopped")
    | Error e -> w.WriteString("result", e)
    w.WriteEndObject())
  w.WriteEndArray()
  w.WriteStartArray "remaining"
  report.Remaining |> List.iter session
  w.WriteEndArray()
  w.WriteEndObject()
  w.Flush()
  Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ".") |> ignore
  File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()))

let private writeSeen (path: string) (seen: SeenSession list) : unit =
  use stream = new MemoryStream()
  use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
  w.WriteStartArray()
  seen
  |> List.iter (fun s ->
    w.WriteStartObject()
    w.WriteString("id", s.Seen.Id)
    w.WriteString("status", s.Seen.Status)
    w.WriteString("firstStatus", List.tryHead s.Statuses |> Option.defaultValue s.Seen.Status)
    w.WriteStartArray "statuses"
    s.Statuses |> List.iter w.WriteStringValue
    w.WriteEndArray()
    w.WriteString("workingDirectory", s.Seen.WorkingDirectory)
    w.WriteString("firstSeenUtc", s.FirstSeenUtc.ToString "o")
    w.WriteEndObject())
  w.WriteEndArray()
  w.Flush()
  Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ".") |> ignore
  File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()))

/// Watches the shared daemon's sessions list for sessions under a run directory until the
/// stop file appears; writes what it saw. This is the proof that a lemming's work showed up
/// in the dashboard Will is watching.
let private watchCommand (args: string list) : int =
  let m = flags args
  match need m "workdir", need m "out", need m "stop-file" with
  | Ok workdir, Ok out, Ok stop ->
    match validRunDir workdir with
    | Error why ->
      eprintfn "refused: %s" why
      ExitCode.refused
    | Ok dir ->
      let port = Map.tryFind "port" m |> Option.map int |> Option.defaultValue defaultMcpPort
      let seen = watch port dir stop (writeSeen out)
      writeSeen out seen
      ExitCode.ok
  | Error e, _, _ | _, Error e, _ | _, _, Error e ->
    eprintfn "usage: LemScore watch --workdir D --out FILE --stop-file FILE [--port N] (%s)" e
    ExitCode.usage

let private cleanupCommand (args: string list) : int =
  let m = flags args
  match need m "workdir", need m "out" with
  | Ok workdir, Ok out ->
    let port = Map.tryFind "port" m |> Option.map int |> Option.defaultValue defaultMcpPort
    let owned =
      Map.tryFind "own" m
      |> Option.map (fun v -> v.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim() |> List.ofArray)
      |> Option.defaultValue []
    let report = cleanup port workdir owned
    writeResidue out report
    printfn "LEM_CLEANUP=%s" (Cleanup.toString report.Verdict)
    printfn "LEM_CLEANUP_NOTE=%s" report.Note
    ExitCode.ok
  | Error e, _ | _, Error e ->
    eprintfn "usage: LemScore cleanup --workdir D --out FILE [--port N] [--own ID,ID] (%s)" e
    ExitCode.usage

let private sessionsUnderCommand (args: string list) : int =
  let m = flags args
  match need m "workdir" with
  | Ok workdir ->
    let port = Map.tryFind "port" m |> Option.map int |> Option.defaultValue defaultMcpPort
    match validRunDir workdir, listSessions port with
    | Error why, _ ->
      eprintfn "refused: %s" why
      ExitCode.refused
    | _, Error why ->
      eprintfn "could not read the sessions list: %s" why
      ExitCode.daemonWontDo
    | Ok _, Ok all ->
      all |> List.filter (belongsTo workdir) |> List.iter (fun s -> printfn "%s %s" s.Id s.Status)
      ExitCode.ok
  | Error e ->
    eprintfn "usage: LemScore sessions-under --workdir D [--port N] (%s)" e
    ExitCode.usage

/// Replaces `find` with `replacement` in a file, and only if `find` occurs exactly once.
/// A match count other than one is refused and said out loud (no silent no-op).
let replaceExact (text: string) (find: string) (replacement: string) : Result<string, string> =
  match find with
  | "" -> Error "the text to find is empty"
  | _ ->
    let count = (text.Length - text.Replace(find, "").Length) / find.Length
    match count with
    | 1 -> Ok (text.Replace(find, replacement))
    | 0 -> Error (sprintf "no match for '%s'" find)
    | n -> Error (sprintf "%d matches for '%s'; refusing to guess which one" n find)

let private replaceExactCommand (args: string list) : int =
  let m = flags args
  match need m "file", need m "find", need m "replace" with
  | Ok file, Ok find, Ok replacement ->
    match replaceExact (File.ReadAllText file) find replacement with
    | Ok updated ->
      File.WriteAllText(file, updated)
      printfn "replaced exactly one match in %s" file
      ExitCode.ok
    | Error why ->
      eprintfn "refused: %s in %s" why file
      ExitCode.refused
  | Error e, _, _ | _, Error e, _ | _, _, Error e ->
    eprintfn "usage: LemScore replace-exact --file F --find S --replace S (%s)" e
    ExitCode.usage

/// Every `--pattern` must match the answer file; the misses are named. Used by oracles
/// that check a written answer.
let patternsMissing (text: string) (patterns: string list) : string list =
  patterns
  |> List.filter (fun p -> not (System.Text.RegularExpressions.Regex.IsMatch(text, p, System.Text.RegularExpressions.RegexOptions.IgnoreCase ||| System.Text.RegularExpressions.RegexOptions.Multiline)))

let private expectCommand (args: string list) : int =
  // --pattern may repeat, so it cannot go through the single-valued flag map.
  let pairs = args |> List.chunkBySize 2 |> List.choose (function [ k; v ] -> Some (k, v) | _ -> None)
  let patterns = pairs |> List.filter (fun (k, _) -> k = "--pattern") |> List.map snd
  match pairs |> List.tryFind (fun (k, _) -> k = "--file") |> Option.map snd with
  | None ->
    eprintfn "usage: LemScore expect --file F --pattern RE [--pattern RE ...]"
    ExitCode.usage
  | Some file when not (File.Exists file) ->
    eprintfn "refused: %s does not exist" file
    ExitCode.refused
  | Some file ->
    match patternsMissing (File.ReadAllText file) patterns with
    | [] ->
      printfn "all %d expected pattern(s) are in %s" patterns.Length file
      ExitCode.ok
    | missing ->
      missing |> List.iter (eprintfn "missing: %s")
      ExitCode.refused

let private runIdCommand (args: string list) : int =
  match args with
  | [ root; model; task ] ->
    printfn "%s" (Catalog.nextRunId root model task)
    ExitCode.ok
  | _ ->
    eprintfn "usage: LemScore run-id <root> <model> <task>"
    ExitCode.usage

/// Prints LEM_VERSION_SKEW=same|skewed|unknown for a daemon version and a bridge version
/// (either may be free text that contains one, like the first line of `sagefs --version`).
let private versionSkewCommand (args: string list) : int =
  let m = flags args
  match need m "daemon", need m "bridge" with
  | Ok daemon, Ok bridge ->
    printfn "LEM_VERSION_SKEW=%s" (Provenance.VersionMatch.toString (Provenance.compareBuilds daemon bridge))
    printfn "LEM_BRIDGE_VERSION=%s" (Provenance.versionOf bridge |> Option.defaultValue "unknown")
    ExitCode.ok
  | Error e, _ | _, Error e ->
    eprintfn "usage: LemScore version-skew --daemon V --bridge V (%s)" e
    ExitCode.usage

type private PruneOptions =
  { Ids: string list
    DryRun: bool
    Root: string
    OlderThanDays: float option }

/// ids, --dry-run, --root DIR and --older-than-days N in any order; anything else is refused.
let private parsePruneArgs (args: string list) : Result<PruneOptions, string> =
  let rec go (o: PruneOptions) (rest: string list) =
    match rest with
    | [] -> Ok { o with Ids = List.rev o.Ids }
    | "--dry-run" :: tail -> go { o with DryRun = true } tail
    | "--root" :: dir :: tail -> go { o with Root = dir } tail
    | "--older-than-days" :: n :: tail ->
      match Double.TryParse n with
      | true, d -> go { o with OlderThanDays = Some d } tail
      | false, _ -> Error (sprintf "--older-than-days needs a number, not '%s'" n)
    | flag :: _ when flag.StartsWith "--" -> Error (sprintf "unknown or incomplete option %s" flag)
    | id :: tail -> go { o with Ids = id :: o.Ids } tail
  go { Ids = []; DryRun = false; Root = "/tmp/lem"; OlderThanDays = None } args

/// Deletes finished runs under the run root, or says what it would delete.
let private pruneCommand (args: string list) : int =
  match parsePruneArgs args |> Result.bind (fun o -> Prune.plan o.Root DateTime.UtcNow o.Ids o.OlderThanDays |> Result.map (fun plan -> o, plan)) with
  | Error why ->
    eprintfn "refused: %s" why
    ExitCode.refused
  | Ok (options, plan) ->
    plan.Skipped |> List.iter (fun (id, why) -> eprintfn "kept %s: %s" id why)
    plan.Delete
    |> List.iter (fun r ->
      match options.DryRun with
      | true -> printfn "would delete %s (%s)" r.Path (Prune.humanBytes r.Bytes)
      | false ->
        Directory.Delete(r.Path, true)
        printfn "deleted %s (%s)" r.Path (Prune.humanBytes r.Bytes))
    printfn "%s %d run(s), %s" (if options.DryRun then "would free" else "freed") plan.Delete.Length (Prune.humanBytes (plan.Delete |> List.sumBy _.Bytes))
    ExitCode.ok

let private dllVersionCommand (args: string list) : int =
  match args with
  | [ path ] when File.Exists path ->
    let v = FileVersionInfo.GetVersionInfo path
    printfn "%s" (v.ProductVersion |> Option.ofObj |> Option.defaultValue "unknown")
    ExitCode.ok
  | _ ->
    eprintfn "usage: LemScore dll-version <path to SageFs.dll>"
    ExitCode.usage

// ---- score --------------------------------------------------------------------------------

let private readIfExists (path: string) : string option =
  match File.Exists path with
  | true -> Some (File.ReadAllText path)
  | false -> None

/// The ids of the sessions the cleanup found, read back from residue.json.
let parseResidueIds (json: string) : string list =
  try
    use doc = JsonDocument.Parse json
    match doc.RootElement.TryGetProperty "found" with
    | true, arr when arr.ValueKind = JsonValueKind.Array ->
      arr.EnumerateArray()
      |> Seq.choose (fun s ->
        match s.TryGetProperty "id" with
        | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
        | _ -> None)
      |> List.ofSeq
    | _ -> []
  with :? JsonException -> []

/// (id, every status seen in order) of each session the watcher saw, read back from
/// sessions.seen.json. An older file with only `status` reads as a one-status history.
let parseSeen (json: string) : (string * string list) list =
  let text (el: JsonElement) = el.GetString() |> Option.ofObj |> Option.defaultValue ""
  try
    use doc = JsonDocument.Parse json
    match doc.RootElement.ValueKind with
    | JsonValueKind.Array ->
      doc.RootElement.EnumerateArray()
      |> Seq.choose (fun s ->
        match s.TryGetProperty "id", s.TryGetProperty "statuses", s.TryGetProperty "status" with
        | (true, i), (true, sts), _ when i.ValueKind = JsonValueKind.String && sts.ValueKind = JsonValueKind.Array ->
          Some (text i, sts.EnumerateArray() |> Seq.filter (fun v -> v.ValueKind = JsonValueKind.String) |> Seq.map text |> List.ofSeq)
        | (true, i), _, (true, st) when i.ValueKind = JsonValueKind.String && st.ValueKind = JsonValueKind.String -> Some (text i, [ text st ])
        | _ -> None)
      |> List.ofSeq
    | _ -> []
  with :? JsonException -> []

let private oracleVerdictOf (text: string) : Result<OracleVerdict, string> =
  match text with
  | "skip" -> Ok OracleNotRun
  | "0" -> Ok OraclePassed
  | other ->
    match Int32.TryParse other with
    | true, code -> Ok (OracleFailed code)
    | false, _ -> Error (sprintf "--oracle-exit must be an integer or 'skip', not '%s'" other)

let private snapshotOf (m: Map<string, string>) (suffix: string) : DaemonSnapshot option =
  match Map.tryFind ("mem-" + suffix) m, Map.tryFind ("avail-" + suffix) m with
  | Some pressure, Some avail ->
    let leases = Map.tryFind ("leases-" + suffix) m |> Option.bind (fun s -> match Int32.TryParse s with | true, n -> Some n | _ -> None) |> Option.defaultValue 0
    match Int64.TryParse avail with
    | true, bytes -> Some { Pressure = pressure; AvailableBytes = bytes; ActiveLeases = leases }
    | false, _ -> None
  | _ -> None

let private endSnapshot (port: int) : DaemonSnapshot option =
  match daemonStatus port with
  | Ok s -> Some { Pressure = s.MemoryPressure; AvailableBytes = s.AvailableBytes; ActiveLeases = s.ActiveLeases }
  | Error _ -> None

/// A summary the harness itself could not build still gets written, as HarnessError, so a
/// run never leaves without a verdict.
let private harnessErrorJson (id: string) (model: string) (task: string) (why: string) : string =
  let facts =
    { Stream = emptyStream; CmdcExit = -1; Oracle = OracleNotRun; OracleOutput = ""; Cleanup = CleanupNotRun; ResidueSessions = [] }
  let input =
    { Id = id; Model = model; Harness = Cmdc; SagefsVersion = "unknown"; DaemonVersion = "unknown"; BridgeVersion = "unknown"; RunDir = ""; Task = task; Seconds = 0
      Facts = facts; Teardown = ExitedOnItsOwn; DaemonStart = None; DaemonEnd = None; DashboardUrl = ""
      SessionsSeen = []; SandboxProcessesLeft = []; ChangedFiles = []; Extra = [ { Stage = Preflight; Symptom = "the harness could not score this run"; Evidence = why } ] }
  render input { Outcome = HarnessError; Reason = why; Provider = None; FellOver = [] }

let private scoreCommand (args: string list) : int =
  let m = flags args
  let get key = need m key
  match get "run-dir", get "task", get "model", get "harness", get "oracle-exit" with
  | Ok runDir, Ok task, Ok model, Ok harnessText, Ok oracleText ->
    let outDir = Path.Combine(runDir, "out")
    let id = Map.tryFind "id" m |> Option.defaultValue (Path.GetFileName(runDir.TrimEnd('/')))
    let port = Map.tryFind "port" m |> Option.map int |> Option.defaultValue defaultMcpPort
    let events = Path.Combine(outDir, "events.ndjson")
    let written =
      match Harness.tryParse harnessText, oracleVerdictOf oracleText, Cleanup.tryParse (Map.tryFind "cleanup" m |> Option.defaultValue "not-run") with
      | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e
      | Ok harness, Ok oracle, Ok cleanupResult ->
        let lines = if File.Exists events then File.ReadAllLines events |> Array.toSeq else Seq.empty
        let stream = ofLines lines
        let residueIds = readIfExists (Path.Combine(outDir, "residue.json")) |> Option.map parseResidueIds |> Option.defaultValue []
        let cmdcExit = Map.tryFind "cmdc-exit" m |> Option.bind (fun s -> match Int32.TryParse s with | true, n -> Some n | _ -> None) |> Option.defaultValue -1
        let facts =
          { Stream = stream
            CmdcExit = cmdcExit
            Oracle = oracle
            OracleOutput = readIfExists (Path.Combine(outDir, "oracle.out")) |> Option.defaultValue ""
            Cleanup = cleanupResult
            ResidueSessions = residueIds }
        let extra =
          match readIfExists (Path.Combine(outDir, "fellover.extra.json")) with
          | None -> Ok []
          | Some json -> parseExtraFellOver json
        match extra with
        | Error e -> Error e
        | Ok extraFell ->
          let changed =
            readIfExists (Path.Combine(outDir, "changed.txt"))
            |> Option.map (fun t -> t.Split('\n') |> Array.map _.Trim() |> Array.filter (fun l -> l <> "") |> List.ofArray)
            |> Option.defaultValue []
          let teardown = if CmdcExit.timeoutExits |> List.contains cmdcExit then KilledByTimeout else ExitedOnItsOwn
          let input =
            { Id = id; Model = model; Harness = harness
              SagefsVersion = Map.tryFind "sagefs-version" m |> Option.defaultValue "unknown"
              DaemonVersion = Map.tryFind "daemon-version" m |> Option.defaultValue "unknown"
              BridgeVersion = Map.tryFind "bridge-version" m |> Option.defaultValue "unknown"
              RunDir = runDir
              Task = task
              Seconds = Map.tryFind "seconds" m |> Option.bind (fun s -> match Int32.TryParse s with | true, n -> Some n | _ -> None) |> Option.defaultValue 0
              Facts = facts; Teardown = teardown
              DaemonStart = snapshotOf m "start"
              DaemonEnd = endSnapshot port
              DashboardUrl = sprintf "http://localhost:%d/dashboard" (dashboardPortFor port)
              SessionsSeen = readIfExists (Path.Combine(outDir, "sessions.seen.json")) |> Option.map parseSeen |> Option.defaultValue []
              SandboxProcessesLeft = readIfExists (Path.Combine(outDir, "sbx", "ps.txt")) |> Option.map leftoverProcesses |> Option.defaultValue []
              ChangedFiles = changed
              Extra = extraFell }
          Ok (render input (assess facts |> forHarness harness))
    let json =
      match written with
      | Ok j -> j
      | Error why -> harnessErrorJson id model task why
    Directory.CreateDirectory outDir |> ignore
    File.WriteAllText(Path.Combine(outDir, "summary.json"), json + "\n")
    printfn "%s" json
    ExitCode.ok
  | _ ->
    eprintfn "usage: LemScore score --run-dir D --task T --model M --harness H --oracle-exit N|skip [--cmdc-exit N --seconds N --sagefs-version V --daemon-version V --bridge-version V --cleanup C --mem-start P --avail-start B --leases-start N --port N]"
    ExitCode.usage

[<EntryPoint>]
let main argv =
  match List.ofArray argv with
  | "version-skew" :: rest -> versionSkewCommand rest
  | "free-model" :: rest -> freeModelCommand rest
  | "run-id" :: rest -> runIdCommand rest
  | "daemon-check" :: rest -> daemonCheckCommand rest
  | "cleanup" :: rest -> cleanupCommand rest
  | "watch" :: rest -> watchCommand rest
  | "sessions-under" :: rest -> sessionsUnderCommand rest
  | "replace-exact" :: rest -> replaceExactCommand rest
  | "expect" :: rest -> expectCommand rest
  | "dll-version" :: rest -> dllVersionCommand rest
  | "prune" :: rest -> pruneCommand rest
  | "score" :: rest -> scoreCommand rest
  | _ ->
    eprintfn "usage: LemScore free-model|run-id|daemon-check|cleanup|watch|sessions-under|replace-exact|expect|dll-version|version-skew|prune|score ..."
    ExitCode.usage
