/// The door onto the live-tweak engine: nudge one value in a source file the
/// session owns, and write the result back as just that expression's range.
///
/// The engine (`TweakAddress`, `LiteralEdit`, `ExpressionEdit`, `TweakLog`) says
/// HOW to find an expression, replace it in place and undo it. Nothing called it
/// from production before this. This module adds what the engine never had: the
/// request a caller sends, the files a session may touch, the order of the disk
/// steps, and one named reason for every refusal.
///
/// The order of a write is the safety argument:
///  1. read the file and the journal, and settle any write the last crash left
///     unfinished (`reconcile`);
///  2. decide, purely, what the new file text and the journal record are;
///  3. append the journal record (durable), THEN replace the file by rename.
/// A crash between 3's two steps leaves a record for a write that did not land,
/// which the next call finds and marks undone. A crash before the append leaves
/// nothing. There is no state where the file changed and no record says how to
/// put it back.
///
/// Refusals are `NudgeRefusal` cases, each carrying the facts, with one function
/// for the rule it broke and one for what the caller does next.
module SageFs.Features.Tweak.Nudge

open System
open System.Threading.Tasks
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.TweakLog
open SageFs.Features.Tweak.NudgeAddress
open SageFs.Features.Tweak.NudgeIo

// ── what the session owns ──

/// The source files a session owns: the files of the projects it loaded, which is
/// the list the session's worker reports. `Paths` are full, normalized paths.
/// `Watched` is the subset hot reload is watching right now, so a write to one of
/// those reaches the running app; a write to another changes the file and nothing
/// else until hot reload is turned on.
type OwnedFiles =
  { Session: string
    WorkingDirectory: string
    Paths: Set<string>
    Watched: Set<string> }

/// Whether hot reload is watching an owned file.
[<RequireQualifiedAccess>]
type WatchStatus =
  | Watched
  | NotWatched

[<RequireQualifiedAccess>]
type NotOwnedWhy =
  /// Not a file of any project the session loaded.
  | NotAmongProjectFiles
  /// A symbolic link. A rename over it would replace the link, not the file it names.
  | IsASymbolicLink
  /// Not a regular file (a directory, a device).
  | NotARegularFile

/// A path the session owns, built only by `Ownership.tryOwn`.
type OwnedFile = private { FullPath: string; OwnerSession: string; Watching: WatchStatus }

[<RequireQualifiedAccess>]
module Ownership =
  let pathOf (file: OwnedFile) : string = file.FullPath
  let sessionOf (file: OwnedFile) : string = file.OwnerSession
  let watchOf (file: OwnedFile) : WatchStatus = file.Watching

  /// Resolve `raw` (absolute, or relative to the session's working directory, with
  /// `..` collapsed) and accept it only when it is one of the session's project
  /// files and a regular file. Anything else names why not.
  let tryOwn (owned: OwnedFiles) (kindOf: string -> FileKind) (raw: string) : Result<OwnedFile, NotOwnedWhy> =
    failwith "not built yet"

// ── the request ──

[<RequireQualifiedAccess>]
type NudgeAction =
  | Inspect
  | Set
  | Undo
  | Redo

[<RequireQualifiedAccess>]
module NudgeAction =
  let all : NudgeAction list = [ NudgeAction.Inspect; NudgeAction.Set; NudgeAction.Undo; NudgeAction.Redo ]
  let toToken (action: NudgeAction) : string = failwith "not built yet"

[<RequireQualifiedAccess>]
type NudgeField =
  | File
  | Address
  | Seen
  | Value

[<RequireQualifiedAccess>]
module NudgeField =
  let toToken (field: NudgeField) : string = failwith "not built yet"

/// What the caller says the value should become: a literal (`13.2`, `true`, `Hard`),
/// read as the kind the literal already is, or an expression (`gravity * 2.0`).
[<RequireQualifiedAccess>]
type NudgeValue =
  | LiteralText of string
  | ExpressionText of string

/// The content hash of the expression as the caller last saw it (from `Inspect`).
/// A write is refused when the expression no longer hashes to this.
type SeenHash = private SeenHash of string

[<RequireQualifiedAccess>]
module SeenHash =
  /// Sha256 hex, as `Inspect` reports it: 64 hexadecimal characters.
  let tryParse (raw: string) : Result<SeenHash, string> = failwith "not built yet"
  let value (SeenHash hash) : string = hash

[<RequireQualifiedAccess>]
type InspectTarget =
  | WholeFile
  | OneAddress of TweakAddress

/// A request that has been parsed: every field present and well-formed, the file
/// one the session owns. Nothing downstream re-checks any of it.
[<RequireQualifiedAccess>]
type NudgeRequest =
  | Inspect of file: OwnedFile * target: InspectTarget
  | Set of file: OwnedFile * address: TweakAddress * seen: SeenHash * value: NudgeValue
  | Undo of file: OwnedFile
  | Redo of file: OwnedFile

/// The request as it arrives from a wire: strings, any of them possibly empty.
type RawNudge =
  { Action: string
    File: string
    Address: string
    Seen: string
    Literal: string
    Expression: string }

[<RequireQualifiedAccess>]
type LiteralKindName =
  | Boolean
  | Integer
  | Real
  | Character
  | Text
  | UnionCase

[<RequireQualifiedAccess>]
module LiteralKindName =
  let toToken (kind: LiteralKindName) : string = failwith "not built yet"
  /// The kind a literal value is, so a caller knows how to write one back.
  let ofValue (value: LiteralValue) : LiteralKindName = failwith "not built yet"

// ── the refusals ──

[<RequireQualifiedAccess>]
type NudgeRefusal =
  /// The call did not resolve to a session that can be acted on (none, several, still starting).
  | NoSessionToAct of message: string
  /// The session's project files could not be read, so ownership cannot be decided.
  | ProjectFilesUnknown of reason: string
  | UnknownAction of given: string
  | MissingField of field: NudgeField
  /// Both `literal` and `expression` were given.
  | ValueGivenTwice
  | NotOwned of path: string * why: NotOwnedWhy
  | AddressTextInvalid of AddressTextRefusal
  | SeenHashInvalid of given: string
  /// The binding or the path inside it is no longer there, or the file does not parse.
  | AddressGone of ResolveError
  /// The path stopped resolving, but the same expression (by hash) is somewhere else
  /// in the file now. An offer, never taken.
  | AddressMoved of address: TweakAddress * candidate: TweakAddress
  /// The expression is there, but it is not the one the caller saw: its hash moved.
  | SourceMoved of seen: string * actual: string * currentText: string
  | NotALiteral of text: string
  /// The literal text is not a value of the literal's own kind (`abc` for an integer).
  | LiteralNotReadable of kind: LiteralKindName * given: string
  | ValueKindMismatch of reason: string
  | ExpressionDoesNotParse of reason: string
  | BlockedByOpenConflict of address: TweakAddress
  /// Nothing this door wrote to the file is left to undo. The door never compacts
  /// its journal, so reaching the oldest write is the start of history, not a
  /// retention limit.
  | NothingToUndo
  | NothingToRedo
  /// The expression is not what the undo wrote any more, so undoing would clobber a later edit.
  | UndoDiverged of wrote: string * now: string * before: string
  | HistoryRefused of RollbackError
  | FileUnreadable of FileFault
  | JournalFailed of JournalFault
  /// The journal holds as many events as one session may keep, so a new write is refused.
  | JournalAtBudget of events: int * limit: int
  /// The file was not replaced and is byte-identical to before.
  | WriteFailed of FileFault
  | FileBusy of waited: TimeSpan

[<RequireQualifiedAccess>]
module NudgeRefusal =
  /// One distinct, stable token per case: what a client branches on.
  let token (refusal: NudgeRefusal) : string = failwith "not built yet"
  /// The rule the request broke, in a sentence.
  let rule (refusal: NudgeRefusal) : string = failwith "not built yet"
  /// What the caller does next, in a sentence.
  let nextAction (refusal: NudgeRefusal) : string = failwith "not built yet"

// ── the outcomes ──

type Receipt =
  { File: string
    Address: TweakAddress
    Before: string
    After: string
    HashAfter: string
    FileHashBefore: string
    FileHashAfter: string
    EventId: int }

[<RequireQualifiedAccess>]
type ItemKind =
  /// A literal the caller can scrub, with its current value.
  | Knob of LiteralValue
  /// A formula or other expression, replaced whole by an `ExpressionText`.
  | Formula

type InspectedItem =
  { Address: TweakAddress
    Text: string
    Hash: string
    Kind: ItemKind }

[<RequireQualifiedAccess>]
type Listing =
  | Complete
  | Truncated of shown: int * total: int

type Inspection =
  { File: string
    FileHash: string
    Items: InspectedItem list
    Listing: Listing
    Journaled: int
    Cursor: UndoCursor }

[<RequireQualifiedAccess>]
type NudgeOutcome =
  | Inspected of Inspection
  | Written of Receipt
  /// The value already was that. Nothing was written and nothing was journaled.
  | Unchanged of address: TweakAddress * text: string
  | Undone of Receipt
  | Redone of Receipt

/// Something the call did or found that the caller should know, so nothing the
/// door does is silent.
[<RequireQualifiedAccess>]
type RunNote =
  | TornJournalTailRemoved
  /// The last journal record described a write that never reached the file (a
  /// crash between the record and the rename). It was marked undone.
  | UnlandedWriteMarkedUndone of eventId: int
  /// An expression was written after being parsed, not type-checked. The reload
  /// verdict is where a type error shows, and Undo puts the old text back.
  | ExpressionNotTypeChecked
  /// The file was changed, and hot reload is not watching it, so the running app
  /// has not picked the change up. Turn hot reload on to apply it.
  | FileNotWatched

[<RequireQualifiedAccess>]
module NudgeOutcome =
  /// One distinct, stable token per case.
  let token (outcome: NudgeOutcome) : string = failwith "not built yet"

[<RequireQualifiedAccess>]
module RunNote =
  /// One distinct, stable token per case.
  let token (note: RunNote) : string = failwith "not built yet"

type Ran =
  { Outcome: NudgeOutcome
    Notes: RunNote list }

// ── limits ──

[<RequireQualifiedAccess>]
module NudgeLimits =
  /// The most items one whole-file Inspect lists; past it the listing says so.
  let maxInspectedItems = 200

// ── the pure core ──

/// The most recent settled position in a journal: where undo and redo stand.
let cursorOf (log: EventLog) : UndoCursor = failwith "not built yet"

/// Read the literal text as the kind the existing literal is.
let parseLiteralAs (existing: LiteralValue) (given: string) : Result<LiteralValue, NudgeRefusal> = failwith "not built yet"

/// Turn the strings a wire carries into a request, or the first thing wrong with it.
let parse (owned: OwnedFiles) (kindOf: string -> FileKind) (raw: RawNudge) : Result<NudgeRequest, NudgeRefusal> =
  failwith "not built yet"

/// What a decision says to do to the file: the whole new text, the one journal
/// record that describes it, and the receipt the caller gets back. Nothing has
/// been done yet.
type PlannedWrite =
  { NewSource: string
    Event: TweakLogEvent
    Receipt: Receipt }

[<RequireQualifiedAccess>]
type SetPlan =
  | NothingToChange of address: TweakAddress * text: string
  | Change of PlannedWrite

/// Whether the journal has room for another write. Only `Set` asks: undo and redo
/// always go through, so a full journal never traps someone who wants to go back.
let journalBudget (log: EventLog) : Result<unit, NudgeRefusal> = failwith "not built yet"

/// Decide a `Set` against the file as it is and the journal as it is. Pure: no
/// disk, no clock. The receipt's `EventId` is the id the journal will assign.
let planSet
  (file: string)
  (source: string)
  (log: EventLog)
  (address: TweakAddress)
  (seen: SeenHash)
  (value: NudgeValue)
  : Result<SetPlan, NudgeRefusal> =
  failwith "not built yet"

[<RequireQualifiedAccess>]
type MoveDirection =
  | Backward
  | Forward

/// Decide an undo or a redo against the file as it is and the journal as it is.
/// Pure. The engine does the work (`performUndo`, `performRedo`): this places its
/// refusals in `NudgeRefusal` and reads the cursor from the journal.
let planMove (direction: MoveDirection) (file: string) (source: string) (log: EventLog) : Result<PlannedWrite, NudgeRefusal> =
  failwith "not built yet"

/// What a crash left behind, if anything: the journal says a write landed that the
/// file does not show. The answer is the event to append that marks it undone.
[<RequireQualifiedAccess>]
type Reconciliation =
  | Consistent
  | WriteNeverLanded of eventId: int * markUndone: TweakLogEvent

let reconcile (source: string) (log: EventLog) : Reconciliation = failwith "not built yet"

// ── the shell ──

/// Everything the door needs from outside: the disk steps, a clock, and where a
/// file's journal lives.
type Ports =
  { Files: FileSteps
    Now: unit -> int64
    JournalPathOf: OwnedFile -> string }

/// Which of the two disk steps comes first. Production journals first.
[<RequireQualifiedAccess>]
type StepOrder =
  | JournalThenFile
  /// TWIN: replaces the file, then journals it. A crash between leaves a changed
  /// file no record can put back.
  | FileThenJournalTwin

/// The three choices a run makes that a simulation needs to be able to break,
/// to show its invariants notice. `production` is the only value the daemon uses.
type Strategy =
  { Order: StepOrder
    Settle: string -> EventLog -> Reconciliation
    Replace: FileSteps -> string -> byte[] -> Result<unit, FileFault> }

let production : Strategy =
  { Order = StepOrder.JournalThenFile
    Settle = reconcile
    Replace = AtomicWrite.write }

/// Run one parsed request under `strategy`, with no locking.
let runWith (strategy: Strategy) (ports: Ports) (request: NudgeRequest) : Result<Ran, NudgeRefusal> =
  failwith "not built yet"

/// Run one parsed request, with no locking. The caller holds the file's lock
/// (`execute` does); the simulation calls `runWith` directly, one step at a time.
let runUnlocked (ports: Ports) (request: NudgeRequest) : Result<Ran, NudgeRefusal> = runWith production ports request

/// Per-file locks, so two nudges to one file never interleave their read, journal
/// and rename. A lock wait that exceeds `wait` is refused as `FileBusy`.
type FileLocks() =
  member _.WithLock(path: string, wait: TimeSpan, work: unit -> Result<Ran, NudgeRefusal>) : Task<Result<Ran, NudgeRefusal>> =
    failwith "not built yet"

/// Run one request under the lock of the file it names.
let execute (ports: Ports) (locks: FileLocks) (wait: TimeSpan) (request: NudgeRequest) : Task<Result<Ran, NudgeRefusal>> =
  failwith "not built yet"

/// TWIN: runs the request with no lock, for the concurrency test to show the lock matters.
let executeWithoutLockTwin (ports: Ports) (request: NudgeRequest) : Task<Result<Ran, NudgeRefusal>> =
  Task.Run(fun () -> runUnlocked ports request)
