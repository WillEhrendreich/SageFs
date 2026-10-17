module SageFs.CohortExportCommand

open System.IO
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger

/// `sagefs cohort-export`: the writer for the file `sagefs play` reads (COH-1).
///
/// WHY IT EXISTS. `CohortPlay.runPlay` replays a portable ledger file, but nothing wrote one, and
/// the daemon prunes a finished cohort's rows after `DataRetention.cohortLedgerRetention`, so
/// an export is the only way to keep a run. This reads the daemon's SQLite ledger OFFLINE (no
/// daemon, no wire change) and writes it with `CohortLedgerExport`, whole rows, entropy included:
/// replay mints claim and landing ids from the entropy, so a file without it replays as a
/// different cohort.
///
/// SHAPE. `parseArgs` and `renderExport` are pure (renderExport takes the ledger as an argument),
/// and `openLedger` / `run` are the thin IO around them.

/// The subcommand's name. The ONE place it is spelled: the CLI parse, the help text and the
/// messages all read this, so renaming it is a one-line change.
let commandName = "cohort-export"

[<RequireQualifiedAccess>]
type Flag =
  | Scope
  | DataDir

module Flag =
  let text (flag: Flag) : string =
    match flag with
    | Flag.Scope -> "--scope"
    | Flag.DataDir -> "--data-dir"

  let all : Flag list = [ Flag.Scope; Flag.DataDir ]

/// Which cohort to export. A ledger can hold several (one per repository); the caller either names
/// one or says nothing, and saying nothing only works when there is exactly one.
[<RequireQualifiedAccess>]
type ScopeRequest =
  | OnlyScope
  | Requested of text: string

/// Where the ledger lives: the daemon's data dir (which honours SAGEFS_DATA_DIR), or one the caller names.
[<RequireQualifiedAccess>]
type DataDirRequest =
  | DaemonDefault
  | Explicit of dir: string

type ExportArgs = {
  Scope: ScopeRequest
  DataDir: DataDirRequest
  OutputPath: string
}

[<RequireQualifiedAccess>]
type UsageProblem =
  | MissingOutputPath
  | FlagWithoutValue of Flag
  | FlagRepeated of Flag
  | UnknownFlag of text: string
  | ExtraArgument of text: string

[<RequireQualifiedAccess>]
type ExportError =
  | Usage of UsageProblem
  /// There is no ledger file at all; carries the path that was checked.
  | NoLedgerFile of lookedAt: string
  /// The file exists but holds no rows (the daemon prunes a finished cohort's rows).
  | LedgerHoldsNoRows of ledgerPath: string
  /// More than one cohort is stored and the caller did not say which.
  | ScopeRequired of available: CohortScope list
  | UnknownScope of requested: string * available: CohortScope list
  /// The text matched more than one stored scope; carries every candidate.
  | AmbiguousScope of requested: string * candidates: CohortScope list
  /// The scope has rows, but this build could not read any of them.
  | ScopeUnreadable of scope: CohortScope
  | LedgerUnreadable of ledgerPath: string * reason: string
  | WriteFailed of outputPath: string * reason: string

/// What `renderExport` decided to write.
type ExportPlan = {
  ChosenScope: CohortScope
  Entries: LedgerEntry<MemberId> list
  OutputPath: string
}

/// The ledger as the IO edge found it. `Absent` means no file, which is NOT the same as an empty
/// ledger and must not be turned into one by opening (creating) a database.
type LedgerRead =
  | Absent of lookedAt: string
  | Present of ledgerPath: string * port: LedgerPort<MemberId>

/// Whether the parser has read the one positional argument yet.
[<RequireQualifiedAccess>]
type OutputSeen =
  | NotYet
  | At of path: string

let parseArgs (args: string list) : Result<ExportArgs, ExportError> =
  // The accumulators are the request DUs themselves: `OnlyScope` and `DaemonDefault` ARE the
  // "not given" states, so there is no separate flag for "was it set", and `OutputSeen` says
  // whether the one positional has been read.
  let flagOf (text: string) : Flag list = Flag.all |> List.filter (fun f -> Flag.text f = text)
  let rec go (scope: ScopeRequest) (dataDir: DataDirRequest) (output: OutputSeen) (rest: string list) =
    match rest with
    | [] ->
      match output with
      | OutputSeen.NotYet -> Error(ExportError.Usage UsageProblem.MissingOutputPath)
      | OutputSeen.At path -> Ok { Scope = scope; DataDir = dataDir; OutputPath = path }
    | head :: tail ->
      match flagOf head, tail with
      | [ flag ], [] -> Error(ExportError.Usage(UsageProblem.FlagWithoutValue flag))
      | [ Flag.Scope ], value :: more ->
        match scope with
        | ScopeRequest.Requested _ -> Error(ExportError.Usage(UsageProblem.FlagRepeated Flag.Scope))
        | ScopeRequest.OnlyScope -> go (ScopeRequest.Requested value) dataDir output more
      | [ Flag.DataDir ], value :: more ->
        match dataDir with
        | DataDirRequest.Explicit _ -> Error(ExportError.Usage(UsageProblem.FlagRepeated Flag.DataDir))
        | DataDirRequest.DaemonDefault -> go scope (DataDirRequest.Explicit value) output more
      | _, _ when head.StartsWith("--", System.StringComparison.Ordinal) ->
        Error(ExportError.Usage(UsageProblem.UnknownFlag head))
      | _, _ ->
        match output with
        | OutputSeen.At _ -> Error(ExportError.Usage(UsageProblem.ExtraArgument head))
        | OutputSeen.NotYet -> go scope dataDir (OutputSeen.At head) tail
  go ScopeRequest.OnlyScope DataDirRequest.DaemonDefault OutputSeen.NotYet args

/// Does `text` name `scope`? A scope is named by its label (`machine`, `repo:<root>`,
/// `named:<key>`) or, for a repository or named scope, by the bare root path or key.
let namesScope (text: string) (scope: CohortScope) : bool =
  text = Scope.label scope
  || (match scope with
      | CohortScope.Machine -> false
      | CohortScope.Repository root -> Scope.trimEnd root = Scope.trimEnd text
      | CohortScope.Named key -> key = text)

/// Pure: the arguments and what the ledger holds in, the rows to write (or why not) out.
let renderExport (args: ExportArgs) (ledger: LedgerRead) : Result<ExportPlan, ExportError> =
  match ledger with
  | Absent lookedAt -> Error(ExportError.NoLedgerFile lookedAt)
  | Present(ledgerPath, port) ->
    let available = port.Scopes ()
    let chosen : Result<CohortScope, ExportError> =
      match available, args.Scope with
      | [], _ -> Error(ExportError.LedgerHoldsNoRows ledgerPath)
      | [ only ], ScopeRequest.OnlyScope -> Ok only
      | _, ScopeRequest.OnlyScope -> Error(ExportError.ScopeRequired available)
      | _, ScopeRequest.Requested text ->
        match available |> List.filter (namesScope text) with
        | [ one ] -> Ok one
        | [] -> Error(ExportError.UnknownScope(text, available))
        | many -> Error(ExportError.AmbiguousScope(text, many))
    chosen
    |> Result.bind (fun scope ->
      match port.ReadAll scope with
      | [] -> Error(ExportError.ScopeUnreadable scope)
      | entries -> Ok { ChosenScope = scope; Entries = entries; OutputPath = args.OutputPath })

let describeUsage (problem: UsageProblem) : string =
  match problem with
  | UsageProblem.MissingOutputPath -> "no output file given"
  | UsageProblem.FlagWithoutValue flag -> sprintf "%s needs a value" (Flag.text flag)
  | UsageProblem.FlagRepeated flag -> sprintf "%s was given twice" (Flag.text flag)
  | UsageProblem.UnknownFlag text -> sprintf "unknown option %s" text
  | UsageProblem.ExtraArgument text -> sprintf "unexpected extra argument %s (one output file only)" text

let usage : string =
  sprintf "usage: sagefs %s [%s <path>] [%s <dir>] <out.jsonl>" commandName (Flag.text Flag.Scope) (Flag.text Flag.DataDir)

let describe (error: ExportError) : string =
  let labels (scopes: CohortScope list) = scopes |> List.map (fun s -> sprintf "  %s" (Scope.label s)) |> String.concat "\n"
  match error with
  | ExportError.Usage problem -> sprintf "%s\n%s" (describeUsage problem) usage
  | ExportError.NoLedgerFile lookedAt ->
    sprintf "no cohort ledger found. Looked for %s. Nothing has been recorded there yet, or the daemon uses another data directory (%s <dir>)." lookedAt (Flag.text Flag.DataDir)
  | ExportError.LedgerHoldsNoRows ledgerPath ->
    sprintf "the cohort ledger at %s holds no rows. The daemon prunes a finished cohort's rows after its retention, so the run may already be gone." ledgerPath
  | ExportError.ScopeRequired available ->
    sprintf "the ledger holds %d cohorts; pick one with %s <label or path>:\n%s" available.Length (Flag.text Flag.Scope) (labels available)
  | ExportError.UnknownScope(requested, available) ->
    sprintf "no cohort in the ledger matches %s. It holds:\n%s" requested (labels available)
  | ExportError.AmbiguousScope(requested, candidates) ->
    sprintf "%s matches more than one cohort; use the full label:\n%s" requested (labels candidates)
  | ExportError.ScopeUnreadable scope ->
    sprintf "the %s cohort has rows, but this build could not read any of them (a version that wrote them is newer or older). Nothing was exported." (Scope.label scope)
  | ExportError.LedgerUnreadable(ledgerPath, reason) -> sprintf "could not read the cohort ledger at %s: %s" ledgerPath reason
  | ExportError.WriteFailed(outputPath, reason) -> sprintf "could not write %s: %s" outputPath reason

let dataDirOf (request: DataDirRequest) : string =
  match request with
  | DataDirRequest.DaemonDefault -> DaemonState.SageFsDir
  | DataDirRequest.Explicit dir -> dir

/// IO edge: look for the ledger file, and open it only if it is there. `CohortLedgerSqlite.create`
/// creates the database when it is missing, so asking it first would turn "no ledger" into an empty one.
let openLedger (ledgerPath: string) : LedgerRead =
  match File.Exists ledgerPath with
  | false -> Absent ledgerPath
  | true -> Present(ledgerPath, CohortLedgerSqlite.Sqlite.create ledgerPath)

/// Parse, read the ledger, decide, write. Returns the line to print. Never throws.
let run (args: string list) : Result<string, ExportError> =
  parseArgs args
  |> Result.bind (fun parsed ->
    let ledgerPath = LocalData.cohortLedgerPath (dataDirOf parsed.DataDir)
    try
      renderExport parsed (openLedger ledgerPath)
    with ex ->
      Error(ExportError.LedgerUnreadable(ledgerPath, ex.Message)))
  |> Result.bind (fun plan ->
    try
      CohortLedgerExport.exportToFile plan.OutputPath plan.Entries
      Ok(sprintf "exported %d entries of the %s cohort to %s" plan.Entries.Length (Scope.label plan.ChosenScope) plan.OutputPath)
    with ex ->
      Error(ExportError.WriteFailed(plan.OutputPath, ex.Message)))
