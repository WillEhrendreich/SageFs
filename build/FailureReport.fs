/// The first failure of a gate run, as something a person or an agent can act on without opening a log.
///
/// A red tier used to say `exit=2` and a 60 line tail; the reason sat in a tier log, behind ANSI colour, until the
/// tier ended minutes later. Expecto prints the case, its message and its stack frames the moment a case fails, so
/// the information is there at second 15. This module reads it as it streams: `step` takes one line at a time, so a
/// pipeline can report the failure while the tier is still running, and `parseAll` is the same thing over a whole
/// log, so the two agree by construction.
///
/// Pure, so the pipeline script and the tests share it. Loaded by ci-pipeline.fsx (`#load`).
module SageFs.Build.FailureReport

open System
open System.Text.RegularExpressions

/// A case that failed or errored, as Expecto printed it.
type Outcome =
  | Failed
  | Errored

type Failure =
  { /// The full name exactly as Expecto prints it (the hierarchy joined with dots).
    Name: string
    Outcome: Outcome
    /// Everything between the header line and the first stack frame, verbatim.
    Message: string list
    /// Every stack frame line, verbatim, in order.
    Frames: string list }

/// A stack frame inside the repository, as a path relative to the checkout.
type Location = { File: string; Line: int }

let ansi = Regex("\x1b\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled)
let stripAnsi (text: string) = ansi.Replace(text, "")

/// `[E] 2026-10-04T17:04:56.89+00:00: <full name> failed in 00:00:00.023. ` or `... errored in 00:00:15.36 [Expecto]`.
let header =
  Regex(@"^\[E\] \S+: (?<name>.*?) (?<kind>failed|errored) in \S+?\.?\s*(\[Expecto\])?\s*$", RegexOptions.Compiled)

/// A log line that starts something new, so the block before it is over: Expecto's own `[I] <timestamp>`
/// lines and the daemon's `info:` / `warn:` / `fail:` lines.
let startsSomethingNew =
  Regex(@"^(\[[A-Z]{1,3}\] \d{4}-\d\d-\d\dT|(info|warn|fail|crit|dbug|trce): )", RegexOptions.Compiled)

let isFrame (line: string) = line.TrimStart().StartsWith("at ", StringComparison.Ordinal)

/// What a streaming reader is holding: nothing, or a block still being read.
type Detector =
  | Idle
  | Reading of name: string * outcome: Outcome * message: string list * frames: string list

let finish name outcome (message: string list) (frames: string list) : Failure =
  { Name = name; Outcome = outcome; Message = List.rev message; Frames = List.rev frames }

let outcomeOf (m: Match) =
  match m.Groups["kind"].Value with
  | "errored" -> Errored
  | _ -> Failed

/// Feed one line (ANSI already stripped). Returns the new state and a failure when this line ended a block.
let step (state: Detector) (line: string) : Detector * Failure option =
  let m = header.Match line
  match state with
  | Idle ->
    match m.Success with
    | true -> Reading (m.Groups["name"].Value, outcomeOf m, [], []), None
    | false -> Idle, None
  | Reading (name, outcome, message, frames) ->
    match startsSomethingNew.IsMatch line with
    | true ->
      let failure = finish name outcome message frames
      // The line that ended the block may itself start another.
      match m.Success with
      | true -> Reading (m.Groups["name"].Value, outcomeOf m, [], []), Some failure
      | false -> Idle, Some failure
    | false ->
      match isFrame line with
      | true -> Reading (name, outcome, message, line :: frames), None
      | false ->
        match frames with
        | [] -> Reading (name, outcome, line :: message, frames), None
        | _ when line.TrimStart().StartsWith("---", StringComparison.Ordinal) || String.IsNullOrWhiteSpace line ->
          Reading (name, outcome, message, frames), None
        | _ ->
          // A non-frame line after the frames: the block is over.
          Idle, Some (finish name outcome message frames)

/// The block still being held when the log ends (or goes quiet).
let flush (state: Detector) : Failure option =
  match state with
  | Idle -> None
  | Reading (name, outcome, message, frames) -> Some (finish name outcome message frames)

/// Every failure in a whole log, in order. The streaming `step` and this agree by construction.
let parseAll (lines: string list) : Failure list =
  let state, found =
    lines
    |> List.fold (fun (state, found) line ->
      match step state (stripAnsi line) with
      | next, Some failure -> next, failure :: found
      | next, None -> next, found) (Idle, [])
  (match flush state with
   | Some f -> f :: found
   | None -> found)
  |> List.rev

let frameLocation =
  Regex(@" in (?<path>.+?):line (?<line>\d+)\s*\]?\s*$", RegexOptions.Compiled)

/// The first frame whose file is inside the checkout, as a repo-relative path. Frames from FSharp.Core and
/// the runtime carry paths of the machine that built them, so they never match the checkout and are skipped.
/// A frame with no `in file:line` (an F# closure can lack one) is never given a made-up line.
let topRepoFrame (checkout: string) (frames: string list) : Location option =
  let root = checkout.TrimEnd('/') + "/"
  frames
  |> List.tryPick (fun frame ->
    let m = frameLocation.Match frame
    match m.Success && m.Groups["path"].Value.StartsWith(root, StringComparison.Ordinal) with
    | true -> Some { File = m.Groups["path"].Value.Substring root.Length; Line = int m.Groups["line"].Value }
    | false -> None)

/// The command that runs this one case and nothing else, from the tier's own arguments. `--shard k/n` is dropped
/// (a shard only chooses which suites a process owns; the filter already names the case) and `--filter` carries
/// the name exactly as Expecto printed it (dot-joined). Not `--filter-test-case`: that matches leaf names only and
/// can run nothing and exit 0. A filtered run reports NarrowedRun, and a name that matches nothing reports
/// NothingRan (exit 3), so a wrong name cannot look green.
let reproduceCommand (dll: string) (tierArgs: string) (name: string) : string =
  let tokens = tierArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
  let rec dropShard (ts: string list) =
    match ts with
    | "--shard" :: _ :: rest -> dropShard rest
    | t :: rest -> t :: dropShard rest
    | [] -> []
  let flags = dropShard tokens |> String.concat " "
  sprintf "dotnet %s %s --filter '%s'" dll flags (name.Replace("'", "'\\''"))

/// What the last green run said about this case.
type History =
  | PassedBefore of sha: string
  | NotRecorded

/// Prefix of every report line, so the pipeline's console filter shows them and nothing else needs a pattern.
let linePrefix = "FAILURE "

let render (tier: string) (checkout: string) (reproduce: string) (history: History) (f: Failure) : string list =
  let where =
    match topRepoFrame checkout f.Frames with
    | Some l -> sprintf "%s:%d" l.File l.Line
    | None -> "no file:line in the stack (an F# closure frame can lack one)"
  let outcome =
    match f.Outcome with
    | Failed -> "failed"
    | Errored -> "errored"
  let historyText =
    match history with
    | PassedBefore sha -> sprintf "passed in the last green run (%s)" sha
    | NotRecorded -> "not recorded: no per-case ledger yet"
  let message =
    match f.Message with
    | [] -> [ "(no message)" ]
    | lines -> lines |> List.map (fun l -> l.TrimEnd())
  [ yield sprintf "first failure, tier %s" tier
    yield sprintf "case:    %s" f.Name
    yield sprintf "outcome: %s" outcome
    yield "message:"
    yield! message |> List.map (fun l -> "  " + l)
    yield sprintf "where:   %s" where
    yield sprintf "rerun:   %s" reproduce
    yield sprintf "history: %s" historyText ]
  |> List.map (fun l -> linePrefix + l)
