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
open SageFs.Features.Tweak.ExpressionEdit
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
    let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
    let sameFile (a: string) (b: string) = String.Equals(a, b, comparison)
    let full =
      try Some(IO.Path.GetFullPath(raw, owned.WorkingDirectory))
      with _ -> None
    match full |> Option.bind (fun path -> owned.Paths |> Set.toList |> List.tryFind (sameFile path)) with
    | None -> Error NotOwnedWhy.NotAmongProjectFiles
    | Some path ->
      match kindOf path with
      | FileKind.RegularFile ->
        let watching = if owned.Watched |> Set.exists (sameFile path) then WatchStatus.Watched else WatchStatus.NotWatched
        Ok { FullPath = path; OwnerSession = owned.Session; Watching = watching }
      | FileKind.SymbolicLink -> Error NotOwnedWhy.IsASymbolicLink
      | FileKind.Missing
      | FileKind.NotAFile -> Error NotOwnedWhy.NotARegularFile

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
  let toToken (action: NudgeAction) : string =
    match action with
    | NudgeAction.Inspect -> "inspect"
    | NudgeAction.Set -> "set"
    | NudgeAction.Undo -> "undo"
    | NudgeAction.Redo -> "redo"

[<RequireQualifiedAccess>]
type NudgeField =
  | File
  | Address
  | Seen
  | Value

[<RequireQualifiedAccess>]
module NudgeField =
  let toToken (field: NudgeField) : string =
    match field with
    | NudgeField.File -> "file"
    | NudgeField.Address -> "address"
    | NudgeField.Seen -> "seen"
    | NudgeField.Value -> "value"

/// What the caller says the value should become: a literal (`13.2`, `true`, `Hard`),
/// read as the kind the literal already is, or an expression (`gravity * 2.0`).
[<RequireQualifiedAccess>]
type NudgeValue =
  | LiteralText of string
  | ExpressionText of string

/// The content hash of the expression as the caller last saw it (from `Inspect`).
/// A write is refused when the expression no longer hashes to this.
type SeenHash = private SeenHash of string

/// Text that is not a hash.
[<RequireQualifiedAccess>]
type SeenHashFault = NotSha256 of given: string

[<RequireQualifiedAccess>]
module SeenHash =
  /// Sha256 hex, as `Inspect` reports it: 64 hexadecimal characters.
  let tryParse (raw: string) : Result<SeenHash, SeenHashFault> =
    let trimmed = if isNull raw then "" else raw.Trim()
    match trimmed.Length = 64 && trimmed |> Seq.forall Char.IsAsciiHexDigit with
    | true -> Ok(SeenHash(trimmed.ToLowerInvariant()))
    | false -> Error(SeenHashFault.NotSha256 trimmed)
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
  let toToken (kind: LiteralKindName) : string =
    match kind with
    | LiteralKindName.Boolean -> "Boolean"
    | LiteralKindName.Integer -> "Integer"
    | LiteralKindName.Real -> "Real"
    | LiteralKindName.Character -> "Character"
    | LiteralKindName.Text -> "Text"
    | LiteralKindName.UnionCase -> "UnionCase"

  /// The kind a literal value is, so a caller knows how to write one back.
  let ofValue (value: LiteralValue) : LiteralKindName =
    match value with
    | LiteralValue.Bool _ -> LiteralKindName.Boolean
    | LiteralValue.Integer _ -> LiteralKindName.Integer
    | LiteralValue.Real _ -> LiteralKindName.Real
    | LiteralValue.Char _ -> LiteralKindName.Character
    | LiteralValue.Text _ -> LiteralKindName.Text
    | LiteralValue.Case _ -> LiteralKindName.UnionCase

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
  /// The history expects one text at the address and the file holds another, so the step (undo or
  /// redo) would clobber a later edit. Carries what the history has there, what is there now, and
  /// what the step would put back.
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
  let token (refusal: NudgeRefusal) : string =
    match refusal with
    | NudgeRefusal.NoSessionToAct _ -> "NoSessionToAct"
    | NudgeRefusal.ProjectFilesUnknown _ -> "ProjectFilesUnknown"
    | NudgeRefusal.UnknownAction _ -> "UnknownAction"
    | NudgeRefusal.MissingField _ -> "MissingField"
    | NudgeRefusal.ValueGivenTwice -> "ValueGivenTwice"
    | NudgeRefusal.NotOwned _ -> "NotOwned"
    | NudgeRefusal.AddressTextInvalid _ -> "AddressTextInvalid"
    | NudgeRefusal.SeenHashInvalid _ -> "SeenHashInvalid"
    | NudgeRefusal.AddressGone _ -> "AddressGone"
    | NudgeRefusal.AddressMoved _ -> "AddressMoved"
    | NudgeRefusal.SourceMoved _ -> "SourceMoved"
    | NudgeRefusal.NotALiteral _ -> "NotALiteral"
    | NudgeRefusal.LiteralNotReadable _ -> "LiteralNotReadable"
    | NudgeRefusal.ValueKindMismatch _ -> "ValueKindMismatch"
    | NudgeRefusal.ExpressionDoesNotParse _ -> "ExpressionDoesNotParse"
    | NudgeRefusal.BlockedByOpenConflict _ -> "BlockedByOpenConflict"
    | NudgeRefusal.NothingToUndo -> "NothingToUndo"
    | NudgeRefusal.NothingToRedo -> "NothingToRedo"
    | NudgeRefusal.UndoDiverged _ -> "UndoDiverged"
    | NudgeRefusal.HistoryRefused _ -> "HistoryRefused"
    | NudgeRefusal.FileUnreadable _ -> "FileUnreadable"
    | NudgeRefusal.JournalFailed _ -> "JournalFailed"
    | NudgeRefusal.JournalAtBudget _ -> "JournalAtBudget"
    | NudgeRefusal.WriteFailed _ -> "WriteFailed"
    | NudgeRefusal.FileBusy _ -> "FileBusy"

  let describeResolve (error: ResolveError) : string =
    match error with
    | ResolveError.BindingRemoved address -> sprintf "the binding %s is not in the file any more" (format address)
    | ResolveError.PathGone address -> sprintf "the expression %s no longer has the shape the address named" (format address)
    | ResolveError.ParseFailed message -> sprintf "the file does not parse: %s" message

  let describeJournal (fault: JournalFault) : string =
    match fault with
    | JournalFault.Io io -> sprintf "the journal could not be read or written (%s)" (describeFault io)
    | JournalFault.Unreadable reason -> sprintf "the journal file is damaged (%s)" reason
    | JournalFault.Untrusted _ -> "the journal on disk belongs to another build or another file"

  let describeHistory (error: RollbackError) : string =
    match error with
    | RollbackError.NoSuchOperation id -> sprintf "the journal has no operation %d" id
    | RollbackError.NotAnOperation id -> sprintf "journal event %d is not a write, so there is nothing to undo" id
    | RollbackError.AddressGone why -> sprintf "the write's address no longer resolves (%s)" (describeResolve why)
    | RollbackError.BlockedByOpenConflict address -> sprintf "%s has an open conflict" (format address)

  let kindHint (kind: LiteralKindName) : string =
    match kind with
    | LiteralKindName.Boolean -> "true or false"
    | LiteralKindName.Integer -> "a whole number such as 150"
    | LiteralKindName.Real -> "a number such as 13.2"
    | LiteralKindName.Character -> "a single character"
    | LiteralKindName.Text -> "any text"
    | LiteralKindName.UnionCase -> "a case name that starts with a capital letter, such as Hard"

  /// The rule the request broke, in a sentence.
  let rule (refusal: NudgeRefusal) : string =
    match refusal with
    | NudgeRefusal.NoSessionToAct message -> message
    | NudgeRefusal.ProjectFilesUnknown reason -> sprintf "I cannot tell which files this session owns, so I will not touch one: %s" reason
    | NudgeRefusal.UnknownAction given -> sprintf "'%s' is not an action nudge_value has." given
    | NudgeRefusal.MissingField field -> sprintf "The %s field is needed for this action and was empty." (NudgeField.toToken field)
    | NudgeRefusal.ValueGivenTwice -> "Both literal and expression were given. A nudge is one or the other."
    | NudgeRefusal.NotOwned(path, NotOwnedWhy.NotAmongProjectFiles) -> sprintf "%s is not a source file of any project this session loaded." path
    | NudgeRefusal.NotOwned(path, NotOwnedWhy.IsASymbolicLink) -> sprintf "%s is a symbolic link. A rename over it would replace the link, not the file it points at." path
    | NudgeRefusal.NotOwned(path, NotOwnedWhy.NotARegularFile) -> sprintf "%s is not a regular file (it is missing, or it is a directory)." path
    | NudgeRefusal.AddressTextInvalid AddressTextRefusal.Empty -> "The address was empty."
    | NudgeRefusal.AddressTextInvalid(AddressTextRefusal.NoBinding text) -> sprintf "'%s' has no binding name in it." text
    | NudgeRefusal.AddressTextInvalid(AddressTextRefusal.UnknownStep step) -> sprintf "'%s' is not a step an address can take." step
    | NudgeRefusal.AddressTextInvalid(AddressTextRefusal.BadIndex step) -> sprintf "'%s' needs a whole number after the dot." step
    | NudgeRefusal.SeenHashInvalid given -> sprintf "'%s' is not a hash. It has to be 64 hexadecimal characters." given
    | NudgeRefusal.AddressGone why -> sprintf "The address does not resolve any more: %s." (describeResolve why)
    | NudgeRefusal.AddressMoved(address, candidate) ->
      sprintf "%s no longer resolves, but an expression with the same hash is now at %s." (format address) (format candidate)
    | NudgeRefusal.SourceMoved(seen, actual, currentText) ->
      sprintf "The expression changed since you inspected it. It is now %s (hash %s, and you saw hash %s)." currentText actual seen
    | NudgeRefusal.NotALiteral text -> sprintf "The expression here is %s, not a literal, so it cannot be nudged as one." text
    | NudgeRefusal.LiteralNotReadable(kind, given) ->
      sprintf "'%s' is not a %s, which is the kind of the literal here." given (LiteralKindName.toToken kind)
    | NudgeRefusal.ValueKindMismatch reason -> reason
    | NudgeRefusal.ExpressionDoesNotParse reason -> sprintf "That is not an F# expression: %s" reason
    | NudgeRefusal.BlockedByOpenConflict address -> sprintf "%s has an open conflict, so no write or undo goes there until it is resolved." (format address)
    | NudgeRefusal.NothingToUndo -> "This door has written nothing to this file that is left to undo."
    | NudgeRefusal.NothingToRedo -> "There is no undone write to put back."
    | NudgeRefusal.UndoDiverged(wrote, now, before) ->
      sprintf "The expression is not what the history expects. The history has %s there, it is now %s, and this step would put %s back. Doing that would overwrite a later edit." wrote now before
    | NudgeRefusal.HistoryRefused error -> sprintf "The journal cannot be applied to the file as it is now: %s." (describeHistory error)
    | NudgeRefusal.FileUnreadable fault -> sprintf "The file could not be read (%s)." (describeFault fault)
    | NudgeRefusal.JournalFailed fault -> sprintf "Nothing was written: %s." (describeJournal fault)
    | NudgeRefusal.JournalAtBudget(events, limit) ->
      sprintf "This session's journal for the file holds %d events, the most it keeps (%d), so no new write is taken." events limit
    | NudgeRefusal.WriteFailed fault -> sprintf "The file could not be replaced (%s), so it is unchanged." (describeFault fault)
    | NudgeRefusal.FileBusy waited -> sprintf "Another nudge to this file did not finish within %.1f seconds." waited.TotalSeconds

  /// What the caller does next, in a sentence.
  let nextAction (refusal: NudgeRefusal) : string =
    match refusal with
    | NudgeRefusal.NoSessionToAct _ -> "Call list_sessions or get_session_status to see which session this directory routes to, and pass working_directory for the one you mean."
    | NudgeRefusal.ProjectFilesUnknown _ -> "Call get_session_status: the session needs a running worker. Retry once it is Ready."
    | NudgeRefusal.UnknownAction _ ->
      sprintf "Use one of: %s." (NudgeAction.all |> List.map NudgeAction.toToken |> String.concat ", ")
    | NudgeRefusal.MissingField NudgeField.File -> "Pass the source file, as an absolute path or relative to the session's working directory."
    | NudgeRefusal.MissingField NudgeField.Address -> "Call this tool with action=inspect on the file and pass one of the addresses it lists."
    | NudgeRefusal.MissingField NudgeField.Seen -> "Pass the hash action=inspect reported for that address as seen."
    | NudgeRefusal.MissingField NudgeField.Value -> "Pass literal (a value of the literal's own kind) or expression (an F# expression)."
    | NudgeRefusal.ValueGivenTwice -> "Send only literal, or only expression."
    | NudgeRefusal.NotOwned _ -> "Pass a source file of one of the session's projects. action=inspect on such a file lists what can be nudged in it."
    | NudgeRefusal.AddressTextInvalid _ -> "Write the address as Module.Path.binding/step/step, exactly as action=inspect reports it."
    | NudgeRefusal.SeenHashInvalid _ -> "Copy the hash from action=inspect for this address."
    | NudgeRefusal.AddressGone _ -> "Call this tool with action=inspect on the file for the addresses it has now. Nothing was changed."
    | NudgeRefusal.AddressMoved(_, candidate) ->
      sprintf "If %s is the one you meant, send the write again with that address and the same seen hash. Nothing was changed." (format candidate)
    | NudgeRefusal.SourceMoved _ -> "Look at the current text, run action=inspect again, and send the write with the new hash if you still want it. Nothing was changed."
    | NudgeRefusal.NotALiteral _ -> "Send it as expression instead, or inspect the file for a literal address inside it."
    | NudgeRefusal.LiteralNotReadable(kind, _) -> sprintf "Send %s." (kindHint kind)
    | NudgeRefusal.ValueKindMismatch _ -> "Send a value of the literal's own kind."
    | NudgeRefusal.ExpressionDoesNotParse _ -> "Fix the expression and send it again. Nothing was written."
    | NudgeRefusal.BlockedByOpenConflict _ -> "No tool resolves a conflict yet. Write to a different address, or edit the file by hand."
    | NudgeRefusal.NothingToUndo -> "Nothing to do. action=inspect shows how much history there is and where undo stands."
    | NudgeRefusal.NothingToRedo -> "Nothing to do. A write made after an undo starts a new history, so an earlier undo cannot be redone past it."
    | NudgeRefusal.UndoDiverged _ -> "Edit the expression by hand to what you want, or nudge it again. The file was not changed."
    | NudgeRefusal.HistoryRefused _ -> "Run action=inspect for the file's current addresses. The journal entry cannot be applied to the file as it is, and nothing was changed."
    | NudgeRefusal.FileUnreadable _ -> "Check the path and its permissions, then try again. Nothing was changed."
    | NudgeRefusal.JournalFailed(JournalFault.Untrusted _) ->
      "Move the file's journal aside (it is under the tweaks folder in SageFs's data directory) and try again."
    | NudgeRefusal.JournalFailed(JournalFault.Unreadable _) -> "Move the damaged journal aside (it is under the tweaks folder in SageFs's data directory) and try again."
    | NudgeRefusal.JournalFailed(JournalFault.Io _) -> "Check that SageFs's data directory is writable and has room, then try again."
    | NudgeRefusal.JournalAtBudget _ -> "Undo and redo still work. Start a new session to get a fresh journal for this file."
    | NudgeRefusal.WriteFailed _ -> "Check that the file is writable and the disk has room, then try again. The failed write is marked undone in the journal."
    | NudgeRefusal.FileBusy _ -> "Try again in a moment."

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
    /// How many writes undo can step back through, and how many undos redo can put back.
    UndoSteps: int
    RedoSteps: int }

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
  let token (outcome: NudgeOutcome) : string =
    match outcome with
    | NudgeOutcome.Inspected _ -> "Inspected"
    | NudgeOutcome.Written _ -> "Written"
    | NudgeOutcome.Unchanged _ -> "Unchanged"
    | NudgeOutcome.Undone _ -> "Undone"
    | NudgeOutcome.Redone _ -> "Redone"

[<RequireQualifiedAccess>]
module RunNote =
  /// One distinct, stable token per case.
  let token (note: RunNote) : string =
    match note with
    | RunNote.TornJournalTailRemoved -> "TornJournalTailRemoved"
    | RunNote.UnlandedWriteMarkedUndone _ -> "UnlandedWriteMarkedUndone"
    | RunNote.ExpressionNotTypeChecked -> "ExpressionNotTypeChecked"
    | RunNote.FileNotWatched -> "FileNotWatched"

type Ran =
  { Outcome: NudgeOutcome
    Notes: RunNote list }

// ── limits ──

[<RequireQualifiedAccess>]
module NudgeLimits =
  /// The most items one whole-file Inspect lists; past it the listing says so.
  let maxInspectedItems = 200

// ── the pure core ──

/// What undo and redo can do, read from the journal alone. `Applied` is the ids of the
/// writes that are in the file now, oldest first: undo takes the last. `Undone` is the
/// ids of the records that undid a write, newest first: redo takes the first.
///
/// A write puts itself on top of `Applied` and clears `Undone`, so a write after an undo
/// starts a new line of history. A rollback record flips the state of the write it
/// concerns: an undo of a write takes it out of `Applied`, a rollback of that undo (a
/// redo) puts it back, and a rollback of the redo takes it out again, which is what
/// the record that settles a crash can be.
///
/// This is not the engine's `UndoCursor`. That cursor walks the list of every write ever
/// made, so after "write, undo, write" a second undo walks back into the write that was
/// already undone and refuses it as diverged. The simulation found that on the first run.
type History = { Applied: int list; Undone: int list }

let historyOf (log: EventLog) : History =
  let events = log.Events |> List.map (fun e -> e.Id, e.Event) |> Map.ofList
  // How many rollbacks deep a record is, and which write it concerns: a write is 0 deep, an undo of it 1.
  let rec depthOf (id: int) : int * int =
    match Map.tryFind id events with
    | Some(TweakLogEvent.RolledBack target) ->
      let depth, write = depthOf target
      depth + 1, write
    | _ -> 0, id
  log.Events
  |> List.fold
    (fun history e ->
      match e.Event with
      | TweakLogEvent.TweakApplied _
      | TweakLogEvent.TweakSaved _ -> { Applied = history.Applied @ [ e.Id ]; Undone = [] }
      | TweakLogEvent.RolledBack target ->
        let depth, write = depthOf e.Id
        match depth % 2 with
        | 1 ->
          { Applied = history.Applied |> List.filter ((<>) write)
            Undone = e.Id :: (history.Undone |> List.filter ((<>) target)) }
        | _ ->
          { Applied = history.Applied @ [ write ]
            Undone = history.Undone |> List.filter ((<>) target) }
      | _ -> history)
    { Applied = []; Undone = [] }

/// Read the literal text as the kind the existing literal is.
let parseLiteralAs (existing: LiteralValue) (given: string) : Result<LiteralValue, NudgeRefusal> =
  let notReadable kind = Error(NudgeRefusal.LiteralNotReadable(kind, given))
  match existing with
  | LiteralValue.Bool _ ->
    match given with
    | "true" -> Ok(LiteralValue.Bool true)
    | "false" -> Ok(LiteralValue.Bool false)
    | _ -> notReadable LiteralKindName.Boolean
  | LiteralValue.Integer _ ->
    match Int64.TryParse(given, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Ok(LiteralValue.Integer v)
    | false, _ -> notReadable LiteralKindName.Integer
  | LiteralValue.Real _ ->
    // Source has no spelling for not-a-number or infinity, so they are not values a nudge can write.
    match Double.TryParse(given, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
    | true, v when Double.IsFinite v -> Ok(LiteralValue.Real v)
    | _ -> notReadable LiteralKindName.Real
  | LiteralValue.Char _ ->
    match given.Length = 1 with
    | true -> Ok(LiteralValue.Char given.[0])
    | false -> notReadable LiteralKindName.Character
  | LiteralValue.Text _ -> Ok(LiteralValue.Text given)
  | LiteralValue.Case _ ->
    match given.Length > 0 && Char.IsUpper given.[0] && given |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') with
    | true -> Ok(LiteralValue.Case given)
    | false -> notReadable LiteralKindName.UnionCase

let blank (text: string) : bool = String.IsNullOrWhiteSpace text

/// Turn the strings a wire carries into a request, or the first thing wrong with it.
/// The order of the checks is the order a caller fixes things in: the action, the
/// file, the address, the hash, then the value.
let parse (owned: OwnedFiles) (kindOf: string -> FileKind) (raw: RawNudge) : Result<NudgeRequest, NudgeRefusal> =
  let given = if isNull raw.Action then "" else raw.Action.Trim()
  let action = NudgeAction.all |> List.tryFind (fun a -> String.Equals(NudgeAction.toToken a, given, StringComparison.OrdinalIgnoreCase))
  let parseAddress () =
    NudgeAddress.tryParse raw.Address |> Result.mapError NudgeRefusal.AddressTextInvalid
  match action with
  | None -> Error(NudgeRefusal.UnknownAction given)
  | Some _ when blank raw.File -> Error(NudgeRefusal.MissingField NudgeField.File)
  | Some action ->
    match Ownership.tryOwn owned kindOf raw.File with
    | Error why -> Error(NudgeRefusal.NotOwned(raw.File, why))
    | Ok file ->
      match action with
      | NudgeAction.Undo -> Ok(NudgeRequest.Undo file)
      | NudgeAction.Redo -> Ok(NudgeRequest.Redo file)
      | NudgeAction.Inspect ->
        match blank raw.Address with
        | true -> Ok(NudgeRequest.Inspect(file, InspectTarget.WholeFile))
        | false -> parseAddress () |> Result.map (fun address -> NudgeRequest.Inspect(file, InspectTarget.OneAddress address))
      | NudgeAction.Set ->
        match blank raw.Address, blank raw.Seen, blank raw.Literal, blank raw.Expression with
        | true, _, _, _ -> Error(NudgeRefusal.MissingField NudgeField.Address)
        | false, true, _, _ -> Error(NudgeRefusal.MissingField NudgeField.Seen)
        | false, false, true, true -> Error(NudgeRefusal.MissingField NudgeField.Value)
        | false, false, false, false -> Error NudgeRefusal.ValueGivenTwice
        | false, false, literalBlank, _ ->
          match parseAddress (), SeenHash.tryParse raw.Seen with
          | Error refusal, _ -> Error refusal
          | Ok _, Error _ -> Error(NudgeRefusal.SeenHashInvalid raw.Seen)
          | Ok address, Ok seen ->
            let value = if literalBlank then NudgeValue.ExpressionText raw.Expression else NudgeValue.LiteralText raw.Literal
            Ok(NudgeRequest.Set(file, address, seen, value))

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
let journalBudget (log: EventLog) : Result<unit, NudgeRefusal> =
  let held = log.Events.Length
  match held >= TweakLogLimits.maxEventsPerSession with
  | true -> Error(NudgeRefusal.JournalAtBudget(held, TweakLogLimits.maxEventsPerSession))
  | false -> Ok()

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
  let seenHash = SeenHash.value seen
  let finish (resolved: ResolvedTweak) (newSource: string) =
    match newSource = source with
    | true -> Ok(SetPlan.NothingToChange(address, resolved.Text))
    | false ->
      match resolve newSource address with
      | Error e -> Error(NudgeRefusal.AddressGone e)
      | Ok written ->
        let event = TweakLogEvent.TweakSaved(address, resolved.Text, written.Text, written.Hash, contentHash source)
        let receipt : Receipt =
          { File = file
            Address = address
            Before = resolved.Text
            After = written.Text
            HashAfter = written.Hash
            FileHashBefore = contentHash source
            FileHashAfter = contentHash newSource
            EventId = log.NextId }
        Ok(SetPlan.Change { NewSource = newSource; Event = event; Receipt = receipt })
  let edit (resolved: ResolvedTweak) =
    match value with
    | NudgeValue.LiteralText text ->
      match readLiteral source address with
      | Error(LiteralError.NotALiteral shown) -> Error(NudgeRefusal.NotALiteral shown)
      | Error(LiteralError.Gone e) -> Error(NudgeRefusal.AddressGone e)
      | Ok literal ->
        match parseLiteralAs literal.Value text with
        | Error refusal -> Error refusal
        | Ok newValue ->
          match setLiteral source address newValue with
          | Ok newSource -> finish resolved newSource
          | Error(SetLiteralError.KindMismatch reason) -> Error(NudgeRefusal.ValueKindMismatch reason)
          | Error(SetLiteralError.NotALiteral shown) -> Error(NudgeRefusal.NotALiteral shown)
          | Error(SetLiteralError.Gone e) -> Error(NudgeRefusal.AddressGone e)
    | NudgeValue.ExpressionText text ->
      match setExpression source address (Some seenHash) text with
      | Ok newSource -> finish resolved newSource
      | Error(ExpressionEditError.Gone e) -> Error(NudgeRefusal.AddressGone e)
      | Error(ExpressionEditError.ParseFailed(ResolveError.ParseFailed reason)) -> Error(NudgeRefusal.ExpressionDoesNotParse reason)
      | Error(ExpressionEditError.ParseFailed other) -> Error(NudgeRefusal.ExpressionDoesNotParse(NudgeRefusal.describeResolve other))
      | Error(ExpressionEditError.HashMismatch(expected, actual, current)) -> Error(NudgeRefusal.SourceMoved(expected, actual, current))
  match canSave log address with
  | Error(RollbackError.BlockedByOpenConflict blocked) -> Error(NudgeRefusal.BlockedByOpenConflict blocked)
  | Error other -> Error(NudgeRefusal.HistoryRefused other)
  | Ok() ->
    match resolve source address with
    | Error(ResolveError.ParseFailed _ as e) -> Error(NudgeRefusal.AddressGone e)
    | Error e ->
      // The path stopped resolving. The same expression somewhere else in the file is an offer, never taken.
      match relocate source address seenHash with
      | RelocationResult.Relocated candidate -> Error(NudgeRefusal.AddressMoved(address, candidate))
      | RelocationResult.NoCandidate -> Error(NudgeRefusal.AddressGone e)
    | Ok resolved when resolved.Hash <> seenHash -> Error(NudgeRefusal.SourceMoved(seenHash, resolved.Hash, resolved.Text))
    | Ok resolved -> edit resolved

[<RequireQualifiedAccess>]
type MoveDirection =
  | Backward
  | Forward

/// Roll back the record `target` against the file as it is: the engine's `rollback`, which
/// applies only when the expression still hashes to what that record wrote, and says all
/// three texts when it does not. The record that describes the move is the engine's own
/// `RolledBack`, and the receipt is read from the effect it has.
let rollBackRecord (target: int) (file: string) (source: string) (log: EventLog) : Result<PlannedWrite, NudgeRefusal> =
  match rollback log Snapshot.empty target source with
  | Error(RollbackError.BlockedByOpenConflict address) -> Error(NudgeRefusal.BlockedByOpenConflict address)
  | Error other -> Error(NudgeRefusal.HistoryRefused other)
  | Ok(RollbackOutcome.Conflict(wrote, now, before)) -> Error(NudgeRefusal.UndoDiverged(wrote, now, before))
  | Ok(RollbackOutcome.Applied newSource) ->
    let event = TweakLogEvent.RolledBack target
    // The time stamped here is not used: the caller journals the record under its own clock.
    let next, logged = EventLog.append log 0L event
    match lastEffectOf next with
    | LastEffect.Effect(_, address, before, after) ->
      Ok
        { NewSource = newSource
          Event = event
          Receipt =
            { File = file
              Address = address
              Before = before
              After = after
              HashAfter = contentHash after
              FileHashBefore = contentHash source
              FileHashAfter = contentHash newSource
              EventId = logged.Id } }
    | LastEffect.NoEffects -> Error(NudgeRefusal.HistoryRefused(RollbackError.NoSuchOperation logged.Id))

/// Decide an undo or a redo against the file as it is and the journal as it is. Pure.
/// Undo rolls back the newest write that is still applied, and redo rolls back the
/// newest undo, so repeated undo walks back through exactly the writes that are in the
/// file, however many undos and writes came between.
let planMove (direction: MoveDirection) (file: string) (source: string) (log: EventLog) : Result<PlannedWrite, NudgeRefusal> =
  let history = historyOf log
  match direction with
  | MoveDirection.Backward ->
    match List.tryLast history.Applied with
    | None -> Error NudgeRefusal.NothingToUndo
    | Some target -> rollBackRecord target file source log
  | MoveDirection.Forward ->
    match history.Undone with
    | [] -> Error NudgeRefusal.NothingToRedo
    | newest :: _ -> rollBackRecord newest file source log

/// What a crash left behind, if anything: the journal says a write landed that the
/// file does not show. The answer is the event to append that marks it undone.
[<RequireQualifiedAccess>]
type Reconciliation =
  | Consistent
  | WriteNeverLanded of eventId: int * markUndone: TweakLogEvent

/// Look at the journal's newest write and at the file. If the journal says the file
/// holds the write's `after` text and the file still holds its `before` text, the
/// write never landed (a crash between the record and the rename), so the answer is
/// the record that marks it undone. If the file holds neither, someone else edited
/// it and the journal cannot say whose write that is, so nothing is touched.
let reconcile (source: string) (log: EventLog) : Reconciliation =
  match lastEffectOf log with
  | LastEffect.NoEffects -> Reconciliation.Consistent
  | LastEffect.Effect(id, address, before, after) ->
    match resolve source address with
    | Error _ -> Reconciliation.Consistent
    | Ok resolved when resolved.Hash = contentHash after -> Reconciliation.Consistent
    | Ok resolved when resolved.Hash = contentHash before -> Reconciliation.WriteNeverLanded(id, TweakLogEvent.RolledBack id)
    | Ok _ -> Reconciliation.Consistent

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

let fileOf (request: NudgeRequest) : OwnedFile =
  match request with
  | NudgeRequest.Inspect(file, _)
  | NudgeRequest.Set(file, _, _, _)
  | NudgeRequest.Undo file
  | NudgeRequest.Redo file -> file

/// A temp file a crash left beside a file is swept before the next call touches it.
let sweepStaleTemp (steps: FileSteps) (path: string) : unit =
  let temp = AtomicWrite.tempPathFor path
  match steps.KindOf temp with
  | FileKind.Missing -> ()
  | FileKind.RegularFile
  | FileKind.SymbolicLink
  | FileKind.NotAFile -> steps.Discard temp

/// Settle what a crash left behind before the request is looked at: if the journal's
/// newest write never reached the file, record it as undone, and say so.
let settle
  (strategy: Strategy)
  (ports: Ports)
  (path: string)
  (journal: string)
  (image: FileImage)
  (loaded: LoadedJournal)
  : Result<EventLog * RunNote list, NudgeRefusal> =
  let healed =
    match loaded.Healing with
    | TailHealing.TornTailRemoved -> [ RunNote.TornJournalTailRemoved ]
    | TailHealing.Intact -> []
  match strategy.Settle image.Text loaded.Log with
  | Reconciliation.Consistent -> Ok(loaded.Log, healed)
  | Reconciliation.WriteNeverLanded(id, markUndone) ->
    let logged = { Id = loaded.Log.NextId; At = ports.Now(); Event = markUndone }
    match Journal.append ports.Files path journal logged with
    | Error fault -> Error(NudgeRefusal.JournalFailed fault)
    | Ok() ->
      let log, _ = EventLog.append loaded.Log logged.At logged.Event
      Ok(log, healed @ [ RunNote.UnlandedWriteMarkedUndone id ])

/// The listing a whole-file or one-address inspect answers with.
let inspect (path: string) (text: string) (log: EventLog) (target: InspectTarget) : Result<NudgeOutcome, NudgeRefusal> =
  let item (resolved: ResolvedTweak) : InspectedItem =
    let kind =
      match readLiteralText resolved.Text with
      | Ok(value, _) -> ItemKind.Knob value
      | Error _ -> ItemKind.Formula
    { Address = resolved.Address; Text = resolved.Text; Hash = resolved.Hash; Kind = kind }
  let history = historyOf log
  let inspected (items: InspectedItem list) (listing: Listing) =
    Ok(
      NudgeOutcome.Inspected
        { File = path
          FileHash = contentHash text
          Items = items
          Listing = listing
          Journaled = log.Events.Length
          UndoSteps = history.Applied.Length
          RedoSteps = history.Undone.Length }
    )
  match target with
  | InspectTarget.OneAddress address ->
    match resolve text address with
    | Error e -> Error(NudgeRefusal.AddressGone e)
    | Ok resolved -> inspected [ item resolved ] Listing.Complete
  | InspectTarget.WholeFile ->
    match resolveAll text with
    | Error e -> Error(NudgeRefusal.AddressGone e)
    | Ok all ->
      let shown = all |> List.truncate NudgeLimits.maxInspectedItems
      let listing = if all.Length <= NudgeLimits.maxInspectedItems then Listing.Complete else Listing.Truncated(shown.Length, all.Length)
      inspected (shown |> List.map item) listing

/// Journal the planned write, then replace the file, in the order the strategy says.
/// In production the record is on disk before the file changes, so there is never a
/// changed file with no record that can put it back. A failed replace marks the
/// record undone straight away; if even that fails, the next call settles it.
let commit
  (strategy: Strategy)
  (ports: Ports)
  (path: string)
  (journal: string)
  (image: FileImage)
  (log: EventLog)
  (planned: PlannedWrite)
  : Result<Receipt, NudgeRefusal> =
  let logged = { Id = log.NextId; At = ports.Now(); Event = planned.Event }
  let bytes = FileImage.encode { image with Text = planned.NewSource }
  let record () = Journal.append ports.Files path journal logged |> Result.mapError NudgeRefusal.JournalFailed
  let replace () = strategy.Replace ports.Files path bytes |> Result.mapError NudgeRefusal.WriteFailed
  match strategy.Order with
  | StepOrder.JournalThenFile ->
    match record () with
    | Error refusal -> Error refusal
    | Ok() ->
      match replace () with
      | Ok() -> Ok planned.Receipt
      | Error refusal ->
        let marker = { Id = logged.Id + 1; At = ports.Now(); Event = TweakLogEvent.RolledBack logged.Id }
        let _marked = Journal.append ports.Files path journal marker
        Error refusal
  | StepOrder.FileThenJournalTwin ->
    match replace () with
    | Error refusal -> Error refusal
    | Ok() ->
      match record () with
      | Error refusal -> Error refusal
      | Ok() -> Ok planned.Receipt

/// Run one parsed request under `strategy`, with no locking.
let runWith (strategy: Strategy) (ports: Ports) (request: NudgeRequest) : Result<Ran, NudgeRefusal> =
  let file = fileOf request
  let path = Ownership.pathOf file
  let journal = ports.JournalPathOf file
  sweepStaleTemp ports.Files path
  sweepStaleTemp ports.Files journal
  let watchNotes =
    match Ownership.watchOf file with
    | WatchStatus.Watched -> []
    | WatchStatus.NotWatched -> [ RunNote.FileNotWatched ]
  let landed (outcome: Receipt -> NudgeOutcome) (notes: RunNote list) (receipt: Receipt) : Ran =
    { Outcome = outcome receipt; Notes = notes @ watchNotes }
  let readImage () =
    ports.Files.ReadBytes path
    |> Result.bind (FileImage.decode path)
    |> Result.mapError NudgeRefusal.FileUnreadable
  match readImage () with
  | Error refusal -> Error refusal
  | Ok image ->
    match Journal.load ports.Files path journal |> Result.mapError NudgeRefusal.JournalFailed with
    | Error refusal -> Error refusal
    | Ok loaded ->
      match settle strategy ports path journal image loaded with
      | Error refusal -> Error refusal
      | Ok(log, notes) ->
        let move direction outcome =
          planMove direction path image.Text log
          |> Result.bind (fun planned -> commit strategy ports path journal image log planned)
          |> Result.map (fun receipt -> landed outcome notes receipt)
        match request with
        | NudgeRequest.Inspect(_, target) ->
          inspect path image.Text log target |> Result.map (fun outcome -> { Outcome = outcome; Notes = notes })
        | NudgeRequest.Undo _ -> move MoveDirection.Backward NudgeOutcome.Undone
        | NudgeRequest.Redo _ -> move MoveDirection.Forward NudgeOutcome.Redone
        | NudgeRequest.Set(_, address, seen, value) ->
          let typeNote =
            match value with
            | NudgeValue.ExpressionText _ -> [ RunNote.ExpressionNotTypeChecked ]
            | NudgeValue.LiteralText _ -> []
          journalBudget log
          |> Result.bind (fun () -> planSet path image.Text log address seen value)
          |> Result.bind (fun plan ->
            match plan with
            | SetPlan.NothingToChange(unchangedAddress, text) ->
              Ok { Outcome = NudgeOutcome.Unchanged(unchangedAddress, text); Notes = notes }
            | SetPlan.Change planned ->
              commit strategy ports path journal image log planned
              |> Result.map (landed NudgeOutcome.Written (notes @ typeNote)))

/// Run one parsed request, with no locking. The caller holds the file's lock
/// (`execute` does); the simulation calls `runWith` directly, one step at a time.
let runUnlocked (ports: Ports) (request: NudgeRequest) : Result<Ran, NudgeRefusal> = runWith production ports request

/// Per-file locks, so two nudges to one file never interleave their read, journal
/// and rename. A lock wait that exceeds `wait` is refused as `FileBusy`.
type FileLocks() =
  let gates = Collections.Concurrent.ConcurrentDictionary<string, Threading.SemaphoreSlim>(StringComparer.Ordinal)
  let keyOf (path: string) = if OperatingSystem.IsWindows() then path.ToLowerInvariant() else path

  /// The work runs on the thread pool, so a caller holding the lock never blocks the caller that asked.
  member _.WithLock(path: string, wait: TimeSpan, work: unit -> Result<Ran, NudgeRefusal>) : Task<Result<Ran, NudgeRefusal>> =
    task {
      let gate = gates.GetOrAdd(keyOf path, fun _ -> new Threading.SemaphoreSlim(1, 1))
      let! entered = gate.WaitAsync wait
      match entered with
      | false -> return Error(NudgeRefusal.FileBusy wait)
      | true ->
        try
          return! Task.Run(fun () -> work ())
        finally
          gate.Release() |> ignore
    }

/// Run one request under the lock of the file it names.
let execute (ports: Ports) (locks: FileLocks) (wait: TimeSpan) (request: NudgeRequest) : Task<Result<Ran, NudgeRefusal>> =
  locks.WithLock(Ownership.pathOf (fileOf request), wait, fun () -> runUnlocked ports request)

/// TWIN: runs the request with no lock, for the concurrency test to show the lock matters.
let executeWithoutLockTwin (ports: Ports) (request: NudgeRequest) : Task<Result<Ran, NudgeRefusal>> =
  Task.Run(fun () -> runUnlocked ports request)
