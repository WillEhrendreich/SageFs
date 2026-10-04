/// Same-commit, same-binary reuse of a green test tier.
///
/// A gate that was red for ONE flaky tier used to rerun all thirteen (14 to 19 minutes) to learn what the twelve green
/// ones had already said. A pass record is what a green tier leaves behind: the exact inputs it ran on and the verdict
/// it earned. A later run of the SAME commit with byte-identical binaries is the same statement a fresh run would
/// make, except for flakiness, so it may take the record instead, and says so in the trust table
/// (`Trusted (reused from <time>)`), never silently. `--fresh` forces a run.
///
/// Nothing is cached across commits: 80 test files read the source tree, so no tier's inputs are knowable cheaply.
/// Everything here fails CLOSED: a record that cannot be read, a tree that is not clean, a hash that moved by one
/// byte, a tier that was not green: the tier runs.
///
/// Pure, so the pipeline script, the simulation and the tests read the same rules. Loaded by ci-pipeline.fsx
/// (`#load`) and compiled into SageFs.Simulation.
namespace SageFs.Build

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// What a tier's verdict depends on. Every field is exact (a name, a version or a SHA-256), and the tier runs again
/// when any one of them differs from the record's.
type PassInputs =
  { /// The commit. Pins every source file, and the 80 tests that read the source tree.
    Sha: string
    /// The tier's ledger name (`--integration-host[2/5]`, `default-net10`): framework and shard included.
    Tier: string
    /// The test assembly's own arguments.
    Args: string
    /// `net10.0` or `net11.0`.
    Framework: string
    /// `dotnet --version`, which global.json resolves.
    Sdk: string
    /// SHA-256 of the test assembly the tier ran.
    TestAssembly: string
    /// One hash over the product and test-bin assemblies the tier loads (see `PassHashes.closure`).
    Closure: string
    /// What decided which suites this tier ran: a hash of the suite-duration table a sharded tier partitions by,
    /// empty for a tier that is not sharded. Two runs that partition differently ran different suites.
    Partition: string }

/// One green tier's record.
type PassRecord =
  { Key: string
    Inputs: PassInputs
    /// When it ran, as the pipeline wrote it (ISO 8601).
    RecordedAt: string
    Seconds: float
    Verdict: string
    Registered: string
    Ran: string
    Passed: string
    Failed: string
    Errored: string
    Ignored: string
    Detail: string
    /// The row the tier's process wrote to the trust ledger, verbatim, so a reused tier's row is the original.
    LedgerRow: string }

/// Whether the run was asked to ignore every record.
[<RequireQualifiedAccess>]
type Freshness =
  | Reuse
  | Fresh

[<RequireQualifiedAccess>]
type TreeState =
  | Clean
  | Dirty

/// Where records live: nowhere (CI, a plain `dotnet fsi ci-pipeline.fsx`) or a directory the local gate names.
[<RequireQualifiedAccess>]
type Store =
  | Disabled
  | At of directory: string

[<RequireQualifiedAccess>]
type Eligibility =
  | Eligible
  | Ineligible of reason: string

/// What the store held for this tier and commit.
[<RequireQualifiedAccess>]
type Stored =
  | Absent
  | Present of PassRecord
  | Unreadable of reason: string

[<RequireQualifiedAccess>]
type RunBecause =
  | ForcedFresh
  | NoStore
  | DirtyTree
  | NotEligible of reason: string
  | NoRecord
  | UnreadableRecord of reason: string
  | NotGreen of verdict: string
  | InputsChanged of fields: string list

[<RequireQualifiedAccess>]
type Decision =
  | RunTier of RunBecause
  | ReuseRecord of PassRecord

module PassRecord =
  /// The format version in a record's first line. A record of another version is unreadable, so it is rerun.
  let formatVersion = "sagefs-pass-record 1"

  let sha256Hex (bytes: byte array) : string = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

  let sha256OfText (text: string) : string = sha256Hex (Encoding.UTF8.GetBytes text)

  /// Field name and value, in a fixed order. The one place that says what the key is made of.
  let fieldsOf (i: PassInputs) : (string * string) list =
    [ "sha", i.Sha
      "tier", i.Tier
      "args", i.Args
      "framework", i.Framework
      "sdk", i.Sdk
      "testAssembly", i.TestAssembly
      "closure", i.Closure
      "partition", i.Partition ]

  /// SHA-256 of the canonical text of every input: a name and a value per line, so a value can never slide into the
  /// next field's place.
  let key (i: PassInputs) : string =
    fieldsOf i |> List.map (fun (name, value) -> sprintf "%s=%s" name value) |> String.concat "\n" |> sha256OfText

  /// The names of the inputs that differ, for the line the pipeline prints when it reruns a tier.
  let changedFields (recorded: PassInputs) (current: PassInputs) : string list =
    List.zip (fieldsOf recorded) (fieldsOf current)
    |> List.choose (fun ((name, was), (_, now)) ->
      match was = now with
      | true -> None
      | false -> Some name)

  /// A record only counts for a verdict that was fully green.
  let isGreen (r: PassRecord) : bool =
    r.Verdict = "Trusted" && r.Failed = "0" && r.Errored = "0"

  /// The rules. Order matters and every branch that is not an exact, green match is a rerun.
  let decide
    (freshness: Freshness)
    (store: Store)
    (tree: TreeState)
    (eligibility: Eligibility)
    (current: PassInputs)
    (stored: Stored)
    : Decision =
    match freshness, store, tree, eligibility with
    | Freshness.Fresh, _, _, _ -> Decision.RunTier RunBecause.ForcedFresh
    | _, Store.Disabled, _, _ -> Decision.RunTier RunBecause.NoStore
    | _, _, TreeState.Dirty, _ -> Decision.RunTier RunBecause.DirtyTree
    | _, _, _, Eligibility.Ineligible reason -> Decision.RunTier(RunBecause.NotEligible reason)
    | Freshness.Reuse, Store.At _, TreeState.Clean, Eligibility.Eligible ->
      match stored with
      | Stored.Absent -> Decision.RunTier RunBecause.NoRecord
      | Stored.Unreadable reason -> Decision.RunTier(RunBecause.UnreadableRecord reason)
      | Stored.Present record ->
        match record.Key = key record.Inputs, record.Key = key current with
        | false, _ -> Decision.RunTier(RunBecause.UnreadableRecord "the record's key is not the hash of its own inputs")
        | true, false -> Decision.RunTier(RunBecause.InputsChanged(changedFields record.Inputs current))
        | true, true ->
          match isGreen record with
          | false -> Decision.RunTier(RunBecause.NotGreen record.Verdict)
          | true -> Decision.ReuseRecord record

  /// What the trust table says for a tier that took a record.
  let reusedVerdict (record: PassRecord) : string = sprintf "Trusted (reused from %s)" record.RecordedAt

  /// Why a tier ran, in one line, for the pipeline's output.
  let describeRun (because: RunBecause) : string =
    match because with
    | RunBecause.ForcedFresh -> "--fresh"
    | RunBecause.NoStore -> "no pass-record store"
    | RunBecause.DirtyTree -> "the working tree is not clean"
    | RunBecause.NotEligible reason -> reason
    | RunBecause.NoRecord -> "no record for this commit"
    | RunBecause.UnreadableRecord reason -> sprintf "record unreadable (%s)" reason
    | RunBecause.NotGreen verdict -> sprintf "the record's verdict was %s" verdict
    | RunBecause.InputsChanged fields -> sprintf "changed: %s" (String.concat ", " fields)

  // ── the file format: line oriented, no JSON, so reading one is total ───────────────────────────────────────────

  let newlineless(text: string) = text.Replace("\r", " ").Replace("\n", " ")

  /// The last line of a record: a hash of every line before it. A file cut short, edited or overwritten with part
  /// of another record does not match its own end line, so it is unreadable and the tier runs.
  let endField = "end"

  let body(r: PassRecord) : string =
    [ yield formatVersion
      yield sprintf "key %s" r.Key
      for name, value in fieldsOf r.Inputs -> sprintf "in.%s %s" name (newlineless value)
      yield sprintf "recordedAt %s" (newlineless r.RecordedAt)
      yield sprintf "seconds %s" (r.Seconds.ToString("R", Globalization.CultureInfo.InvariantCulture))
      yield sprintf "verdict %s" (newlineless r.Verdict)
      yield sprintf "registered %s" (newlineless r.Registered)
      yield sprintf "ran %s" (newlineless r.Ran)
      yield sprintf "passed %s" (newlineless r.Passed)
      yield sprintf "failed %s" (newlineless r.Failed)
      yield sprintf "errored %s" (newlineless r.Errored)
      yield sprintf "ignored %s" (newlineless r.Ignored)
      yield sprintf "detail %s" (newlineless r.Detail)
      yield sprintf "ledger %s" (newlineless r.LedgerRow) ]
    |> String.concat "\n"
    |> fun text -> text + "\n"

  let serialize (r: PassRecord) : string =
    let text = body r
    text + sprintf "%s %s\n" endField (sha256OfText text)

  /// The fields of a record whose integrity was already checked.
  let parseLines(lines: string array) : Result<PassRecord, string> =
      let fields =
        lines
        |> Array.skip 1
        |> Array.choose (fun line ->
          match line.IndexOf ' ' with
          | -1 -> None
          | cut -> Some(line.Substring(0, cut), line.Substring(cut + 1)))
        |> Map.ofArray
      let get name =
        match Map.tryFind name fields with
        | Some value -> Result.Ok value
        | None -> Result.Error(sprintf "no %s line" name)
      let inputs =
        let field name = get ("in." + name)
        match field "sha", field "tier", field "args", field "framework", field "sdk", field "testAssembly", field "closure", field "partition" with
        | Result.Ok sha, Result.Ok tier, Result.Ok args, Result.Ok framework, Result.Ok sdk, Result.Ok testAssembly, Result.Ok closure, Result.Ok partition ->
          Result.Ok
            { Sha = sha; Tier = tier; Args = args; Framework = framework; Sdk = sdk
              TestAssembly = testAssembly; Closure = closure; Partition = partition }
        | _ -> Result.Error "an input line is missing"
      match inputs, get "key", get "recordedAt", get "seconds", get "verdict", get "registered", get "ran", get "passed", get "failed", get "errored", get "ignored", get "detail", get "ledger" with
      | Result.Ok inputs, Result.Ok key, Result.Ok at, Result.Ok seconds, Result.Ok verdict, Result.Ok registered, Result.Ok ran, Result.Ok passed, Result.Ok failed, Result.Ok errored, Result.Ok ignored, Result.Ok detail, Result.Ok ledger ->
        match Double.TryParse(seconds, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, seconds ->
          Result.Ok
            { Key = key; Inputs = inputs; RecordedAt = at; Seconds = seconds; Verdict = verdict; Registered = registered
              Ran = ran; Passed = passed; Failed = failed; Errored = errored; Ignored = ignored; Detail = detail
              LedgerRow = ledger }
        | false, _ -> Result.Error "seconds is not a number"
      | Result.Error e, _, _, _, _, _, _, _, _, _, _, _, _ -> Result.Error e
      | _ -> Result.Error "a line is missing"

  /// Total: anything that is not exactly a record of this format is an `Error` with the reason. The end line is
  /// checked first, so a record that was cut short, edited or half overwritten is never read as a smaller record.
  let parse (text: string) : Result<PassRecord, string> =
    let text = text.Replace("\r\n", "\n")
    let lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    let endPrefix = endField + " "
    match Array.tryHead lines, Array.tryLast lines with
    | None, _ -> Result.Error "empty"
    | Some first, _ when first <> formatVersion -> Result.Error(sprintf "format %s, expected %s" first formatVersion)
    | Some _, Some last when not (last.StartsWith endPrefix) -> Result.Error "cut short: no end line"
    | Some _, Some last ->
      let endHash = last.Substring endPrefix.Length
      let upToEnd = text.Substring(0, text.LastIndexOf(endPrefix, StringComparison.Ordinal))
      match sha256OfText upToEnd = endHash with
      | false -> Result.Error "the end line does not match what is above it"
      | true -> parseLines lines
    | Some _, None -> Result.Error "empty"

  /// A record for a green tier that just ran on `inputs`.
  let make (inputs: PassInputs) (recordedAt: string) (seconds: float) (verdict: string) (registered, ran, passed, failed, errored, ignored) (detail: string) (ledgerRow: string) : PassRecord =
    { Key = key inputs; Inputs = inputs; RecordedAt = recordedAt; Seconds = seconds; Verdict = verdict
      Registered = registered; Ran = ran; Passed = passed; Failed = failed; Errored = errored; Ignored = ignored
      Detail = detail; LedgerRow = ledgerRow }

  // ── hashing what a tier loads ─────────────────────────────────────────────────────────────────────────────

  let hashFile (path: string) : string =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

  /// Directory names whose contents are never part of a tier's closure: tooling a tier downloads or unpacks, which
  /// is neither built here nor loaded as a product assembly.
  let excludedDirectoryNames = [ ".playwright"; ".vscode-test" ]

  /// Every `*.dll` under `directories` (relative to `root`), as a sorted `relativePath hash` list.
  let closureEntries (root: string) (directories: string list) : (string * string) list =
    let rec dlls (directory: string) : string list =
      match Directory.Exists directory with
      | false -> []
      | true ->
        let here = Directory.GetFiles(directory, "*.dll") |> List.ofArray
        let below =
          Directory.GetDirectories directory
          |> Array.filter (fun d -> not (List.contains (Path.GetFileName d) excludedDirectoryNames))
          |> List.ofArray
          |> List.collect dlls
        here @ below
    directories
    |> List.collect (fun relative -> dlls (Path.Combine(root, relative)))
    |> List.map (fun full -> Path.GetRelativePath(root, full).Replace('\\', '/'), hashFile full)
    |> List.sortBy fst

  /// One hash over the closure: change one byte of one assembly, or add or remove one, and it changes.
  let closureHash (root: string) (directories: string list) : string =
    closureEntries root directories
    |> List.map (fun (path, hash) -> sprintf "%s %s" path hash)
    |> String.concat "\n"
    |> sha256OfText

  // ── the store ────────────────────────────────────────────────────────────────────────────────────────────

  let recordPath (directory: string) (sha: string) (tierFile: string) : string =
    Path.Combine(directory, sha, tierFile + ".pass")

  let read (path: string) : Stored =
    match File.Exists path with
    | false -> Stored.Absent
    | true ->
      try
        match parse (File.ReadAllText path) with
        | Result.Ok record -> Stored.Present record
        | Result.Error reason -> Stored.Unreadable reason
      with e -> Stored.Unreadable e.Message

  /// Written through a temp file and a rename, so a record is either whole or absent, never half of one.
  let write (path: string) (record: PassRecord) : unit =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let temp = path + ".tmp"
    File.WriteAllText(temp, serialize record)
    File.Move(temp, path, true)

  /// How many commits' records are kept. Older ones are removed when a run starts.
  let keepCommits = 12

  let prune (directory: string) : unit =
    match Directory.Exists directory with
    | false -> ()
    | true ->
      Directory.GetDirectories directory
      |> Array.sortByDescending (fun d -> Directory.GetLastWriteTimeUtc d)
      |> Array.skip (min keepCommits (Directory.GetDirectories directory).Length)
      |> Array.iter (fun d -> try Directory.Delete(d, true) with _ -> ())
