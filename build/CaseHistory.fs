/// The gate's memory of which cases have been red, and what each turned out to be.
///
/// One JSON object per line, append-only, in a file beside the pass records (`case-history.jsonl` in the pass-record
/// directory). Each line is one classified red: the case, its tier, the commit it was red on, when, and the verdict
/// (FlakeClassification) with its rate. Keyed by case name and commit, so a case's history across commits is one
/// filter away, and a case that is flaky on one commit and deterministic on the next can be read as that.
///
/// It only ever records reds. A green case leaves no line, which is the point of a failure ledger and its limit: it
/// cannot say how often a case passed, only how often it was seen failing.
///
/// Pure, so the pipeline script and the tests share it. Loaded by ci-pipeline.fsx (`#load`).
module SageFs.Build.CaseHistory

open System
open System.IO
open System.Text
open System.Text.Json
open SageFs.Build.FlakeClassification

/// The file's name inside the pass-record directory.
let fileName = "case-history.jsonl"

/// How many earlier lines a report names before it says "and N older".
let historyShown = 5

type Entry =
  { Case: string
    Tier: string
    Commit: string
    At: string
    Result: Result<Verdict, Unclassifiable> }

/// The closed set of kinds a line can carry, with one exhaustive function to its name.
type Kind =
  | KindDeterministic
  | KindFlaky
  | KindNotReproduced
  | KindUnclassified

let allKinds = [ KindDeterministic; KindFlaky; KindNotReproduced; KindUnclassified ]

let kindName (kind: Kind) : string =
  match kind with
  | KindDeterministic -> "deterministic"
  | KindFlaky -> "flaky"
  | KindNotReproduced -> "not-reproduced"
  | KindUnclassified -> "unclassified"

/// One line of JSON, no newline in it.
let serialize (e: Entry) : string =
  let kind, failed, runs, rerun, reason =
    match e.Result with
    | Result.Ok (Deterministic n) -> KindDeterministic, n, n, 0, ""
    | Result.Ok (Flaky (f, n)) -> KindFlaky, f, n, 0, ""
    | Result.Ok (NotReproduced n) -> KindNotReproduced, 0, n, 0, ""
    | Result.Error NoReruns -> KindUnclassified, 0, 0, 0, ""
    | Result.Error (InvalidRerun (i, r)) -> KindUnclassified, 0, 0, i, r
  use stream = new MemoryStream()
  use w = new Utf8JsonWriter(stream)
  w.WriteStartObject()
  w.WriteString("case", e.Case)
  w.WriteString("tier", e.Tier)
  w.WriteString("commit", e.Commit)
  w.WriteString("at", e.At)
  w.WriteString("kind", kindName kind)
  w.WriteNumber("failed", failed)
  w.WriteNumber("runs", runs)
  w.WriteNumber("rerun", rerun)
  w.WriteString("reason", reason)
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(stream.ToArray())

/// One line back into an entry, or why it is not one.
let parse (line: string) : Result<Entry, string> =
  try
    use doc = JsonDocument.Parse line
    let root = doc.RootElement
    let text (name: string) = root.GetProperty(name).GetString()
    let number (name: string) = root.GetProperty(name).GetInt32()
    let kindText = text "kind"
    match allKinds |> List.tryFind (fun k -> kindName k = kindText) with
    | None -> Result.Error (sprintf "unknown kind '%s'" kindText)
    | Some kind ->
      let result =
        match kind with
        | KindDeterministic -> Result.Ok (Deterministic (number "runs"))
        | KindFlaky -> Result.Ok (Flaky (number "failed", number "runs"))
        | KindNotReproduced -> Result.Ok (NotReproduced (number "runs"))
        | KindUnclassified ->
          match number "rerun" with
          | 0 -> Result.Error NoReruns
          | i -> Result.Error (InvalidRerun (i, text "reason"))
      Result.Ok { Case = text "case"; Tier = text "tier"; Commit = text "commit"; At = text "at"; Result = result }
  with e -> Result.Error (sprintf "not a history line (%s)" e.Message)

let shortSha (sha: string) : string = sha.Substring(0, min 8 sha.Length)

/// What the history says about `case`, in a sentence, from the entries in file order (oldest first). A line that does
/// not parse is not history; it is skipped by the reader, never guessed at.
let summarize (entries: Entry list) (case: string) : string =
  match entries |> List.filter (fun e -> e.Case = case) with
  | [] -> "first time this case has been seen red in the gate's history"
  | seen ->
    let newestFirst = List.rev seen
    let shown =
      newestFirst
      |> List.truncate historyShown
      |> List.map (fun e -> sprintf "%s %s" (shortSha e.Commit) (shortName e.Result))
    let more =
      match newestFirst.Length - shown.Length with
      | 0 -> ""
      | n -> sprintf " (and %d older)" n
    sprintf "red before %d time(s), newest first: %s%s" seen.Length (String.concat "; " shown) more

/// Every parseable line of the file, in order. A missing file is no history; an unreadable line is skipped.
let read (path: string) : Entry list =
  match File.Exists path with
  | false -> []
  | true ->
    File.ReadAllLines path
    |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
    |> Array.choose (fun l ->
      match parse l with
      | Result.Ok entry -> Some entry
      | Result.Error _ -> None)
    |> List.ofArray

/// Append one entry. The file and its directory are created on the first one; nothing is ever rewritten.
let append (path: string) (entry: Entry) : unit =
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  File.AppendAllText(path, serialize entry + "\n")
