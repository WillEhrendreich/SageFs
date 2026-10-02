/// Which processes a finished run left alive. The runner used to count every process whose
/// command line mentioned the run directory, so an unrelated shell waiting on the run's
/// summary file was reported as a leftover (teardown.txt said processesLeft=4 when nothing of
/// the run was alive). A process belongs to the run only on evidence of ownership: an exact
/// pid the runner started, a member of the session that pid led (the display server and the
/// window each lead one), or a process working inside the run directory (the workers of the
/// run's own SageFs sessions). The text of a command line is never evidence. The classifying
/// is pure; the reading of /proc is the only IO, at the bottom.
module LemDrive.Leftovers

open System
open System.IO

/// One process, as /proc reports it.
type Proc =
  { Pid: int
    Ppid: int
    Sid: int
    /// Where it works, "" when unreadable.
    Cwd: string
    Args: string }

/// What the run owns.
type Ownership =
  { RunDir: string
    /// Exact pids the runner started.
    Pids: int list
    /// Session ids led by those pids.
    Sids: int list
    /// The runner and its ancestors, never counted.
    Excluded: int list }

let private under (dir: string) (path: string) : bool =
  let root = dir.TrimEnd('/')
  path = root || path.StartsWith(root + "/", StringComparison.Ordinal)

/// Is this process the run's.
let owned (o: Ownership) (p: Proc) : bool =
  not (List.contains p.Pid o.Excluded)
  && (List.contains p.Pid o.Pids || List.contains p.Sid o.Sids || (p.Cwd <> "" && under o.RunDir p.Cwd))

/// The run's processes among all of them.
let find (o: Ownership) (procs: Proc list) : Proc list = procs |> List.filter (owned o)

/// How much of a command line a report line keeps.
[<Literal>]
let ArgsWidth = 160

/// One line for a report: pid, session, working directory, the start of the command line.
let describe (p: Proc) : string =
  let args = match p.Args.Length > ArgsWidth with | true -> p.Args.Substring(0, ArgsWidth) + " ..." | false -> p.Args
  sprintf "%d\tsid=%d\tcwd=%s\t%s" p.Pid p.Sid p.Cwd args

// ---- reading /proc -----------------------------------------------------------------------

/// "pid (comm) state ppid pgrp session ...": comm may hold spaces and parentheses, so the
/// fields are read after the LAST closing parenthesis.
let parseStat (stat: string) : (int * int) option =
  match stat.LastIndexOf ')' with
  | -1 -> None
  | close ->
    match stat.Substring(close + 1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) with
    | fields when fields.Length >= 4 ->
      match Int32.TryParse fields[1], Int32.TryParse fields[3] with
      | (true, ppid), (true, sid) -> Some(ppid, sid)
      | _ -> None
    | _ -> None

let private readProc (pid: int) : Proc option =
  try
    let dir = sprintf "/proc/%d" pid
    match parseStat (File.ReadAllText(dir + "/stat")) with
    | None -> None
    | Some(ppid, sid) ->
      let cwd =
        try (FileInfo(dir + "/cwd")).LinkTarget |> Option.ofObj |> Option.defaultValue ""
        with _ -> ""
      let args =
        try File.ReadAllText(dir + "/cmdline").Replace('\000', ' ').Trim()
        with _ -> ""
      Some { Pid = pid; Ppid = ppid; Sid = sid; Cwd = cwd; Args = args }
  with _ -> None

/// Every process that can be read now. One that exits while it is read is skipped.
let snapshot () : Proc list =
  Directory.GetDirectories "/proc"
  |> Array.choose (fun d -> match Int32.TryParse(Path.GetFileName d) with | true, pid -> Some pid | _ -> None)
  |> Array.choose readProc
  |> List.ofArray

/// This process and every ancestor of it.
let ancestry (procs: Proc list) (pid: int) : int list =
  let byPid = procs |> List.map (fun p -> p.Pid, p) |> Map.ofList
  let rec up (current: int) (seen: int list) =
    match Map.tryFind current byPid with
    | Some p when p.Ppid > 0 && not (List.contains p.Ppid seen) -> up p.Ppid (current :: seen)
    | _ -> current :: seen
  up pid []

/// The run's processes now, with the runner's own lineage left out.
let now (runDir: string) (pids: int list) (sids: int list) : Proc list =
  let all = snapshot ()
  let me = Environment.ProcessId
  find { RunDir = runDir; Pids = pids; Sids = sids; Excluded = ancestry all me } all
