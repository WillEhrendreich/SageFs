/// The daemon's half of the live-bindings knob: reading what the session's files say about each row, and writing a value back
/// through the nudge door. The decisions are `BindingTweak` and `BindingTweakRows` (pure, in Core); this is the disk, the clock
/// and the memory of what the dashboard did.
///
/// READING. A row is mapped by its binding's name against the session's own files, so the files have to be read. They are read
/// as little as the question allows: a file's declared names are scanned once per change (a cheap text pass), and only a file that
/// declares a name the pane is asked about is parsed, through the door's own `inspect` (the same function the `nudge_value` tool
/// answers with). Both are cached against the file's stamp, so a push that changes nothing costs one stat per file.
///
/// WRITING. Every write goes through `Nudge.parse` and `Nudge.execute`, which are what `nudge_value` is made of: ownership (a
/// project file of this session), the seen hash, the per-file lock, the journal, the atomic replace. This module adds no way to
/// reach a file that the tool does not have.
module SageFs.Server.LiveBindingsTweakService

open System
open System.Collections.Concurrent
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open SageFs
open SageFs.Features
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows

// ── what a request says ──

/// What the row's control asked for. A literal is read as the kind the literal already is; an expression is any F#.
[<RequireQualifiedAccess>]
type TweakVerb =
  | SetLiteral of text: string
  | SetExpression of text: string
  /// A number moved by a count of steps (negative is down): a drag, the buttons and the arrow keys are all just a count. The
  /// literal to write is worked out here, from the number the row showed, by `Stepping.literalAfter`.
  | Steps of count: int
  | Undo
  | Redo

[<RequireQualifiedAccess>]
module TweakVerb =
  /// The tokens a control stages in the page's signals.
  let [<Literal>] LiteralToken = "set"
  let [<Literal>] ExpressionToken = "expression"
  let [<Literal>] StepsToken = "steps"
  let [<Literal>] UndoToken = "undo"
  let [<Literal>] RedoToken = "redo"

  let token (verb: TweakVerb) : string =
    match verb with
    | TweakVerb.SetLiteral _ -> LiteralToken
    | TweakVerb.SetExpression _ -> ExpressionToken
    | TweakVerb.Steps _ -> StepsToken
    | TweakVerb.Undo -> UndoToken
    | TweakVerb.Redo -> RedoToken

  let ofToken (token: string) (value: string) : Result<TweakVerb, string> =
    match token with
    | LiteralToken -> Ok(TweakVerb.SetLiteral value)
    | ExpressionToken -> Ok(TweakVerb.SetExpression value)
    | StepsToken ->
      match Int32.TryParse(value, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
      | true, count -> Ok(TweakVerb.Steps count)
      | false, _ -> Error(sprintf "'%s' is not a count of steps" value)
    | UndoToken -> Ok TweakVerb.Undo
    | RedoToken -> Ok TweakVerb.Redo
    | other -> Error(sprintf "'%s' is not something a row can ask for" other)

  /// What the verb does to the file, once a step count has become a literal.
  let writeKindOf (verb: TweakVerb) : WriteKind =
    match verb with
    | TweakVerb.SetLiteral text
    | TweakVerb.SetExpression text -> WriteKind.SetTo text
    | TweakVerb.Steps count -> WriteKind.SetTo(sprintf "%+d steps" count)
    | TweakVerb.Undo -> WriteKind.UndoStep
    | TweakVerb.Redo -> WriteKind.RedoStep

/// One action on one row, as the page staged it: which row, the expression the row was showing (its file, address, hash and
/// text, which is what makes a stale click refusable), and what to do.
type TweakRequest =
  { Row: RowKey
    File: string
    Address: string
    Seen: string
    SeenText: string
    Verb: TweakVerb }

[<RequireQualifiedAccess>]
type RequestFault =
  | NoRow
  | NoFile
  | UnknownVerb of string
  | BadAddress of string

module TweakRequest =
  let describe (fault: RequestFault) : string =
    match fault with
    | RequestFault.NoRow -> "the request names no row"
    | RequestFault.NoFile -> "the request names no file"
    | RequestFault.UnknownVerb why -> why
    | RequestFault.BadAddress why -> why

  /// The row a client names: its binding, then its record fields, joined by `/`.
  let rowOfText (text: string) : Result<RowKey, RequestFault> =
    match text.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
    | [] -> Error RequestFault.NoRow
    | binding :: labels -> Ok { Binding = binding; Labels = labels }

  let parse (row: string) (file: string) (address: string) (seen: string) (seenText: string) (verb: string) (value: string) : Result<TweakRequest, RequestFault> =
    match rowOfText row, String.IsNullOrWhiteSpace file with
    | Error fault, _ -> Error fault
    | Ok _, true -> Error RequestFault.NoFile
    | Ok key, false ->
      match TweakVerb.ofToken verb value with
      | Error why -> Error(RequestFault.UnknownVerb why)
      | Ok parsed -> Ok { Row = key; File = file; Address = address; Seen = seen; SeenText = seenText; Verb = parsed }

  let rawOf (request: TweakRequest) : RawNudge =
    let raw action : RawNudge = { Action = action; File = request.File; Address = request.Address; Seen = request.Seen; Literal = ""; Expression = "" }
    match request.Verb with
    | TweakVerb.SetLiteral text -> { raw (NudgeAction.toToken NudgeAction.Set) with Literal = text }
    | TweakVerb.SetExpression text -> { raw (NudgeAction.toToken NudgeAction.Set) with Expression = text }
    // A step count is resolved against the row before it gets here (`Service.resolve`); a count that reaches the door unresolved
    // has no literal, and the door says so.
    | TweakVerb.Steps _ -> raw (NudgeAction.toToken NudgeAction.Set)
    | TweakVerb.Undo -> raw (NudgeAction.toToken NudgeAction.Undo)
    | TweakVerb.Redo -> raw (NudgeAction.toToken NudgeAction.Redo)

// ── the service ──

/// A file's size and last write, which together say whether it changed. Cheap to read, and what the caches are keyed on.
[<RequireQualifiedAccess>]
type FileStamp =
  | Stamped of length: int64 * modified: int64
  | Unstamped

module FileStamp =
  /// The revision a stamp says: the file's size and last write, which change whenever it is written.
  let revision (stamp: FileStamp) : FileRevision =
    match stamp with
    | FileStamp.Stamped(length, modified) -> FileRevision.Revised(sprintf "%d:%d" length modified)
    | FileStamp.Unstamped -> FileRevision.Unrevised

/// Whether the worker says hot reload watches a file (its `/hotreload` list carries a flag per file).
[<RequireQualifiedAccess>]
type HotReloadWatch =
  | Watched
  | NotWatched

module HotReloadWatch =
  let ofFlag (watched: bool) : HotReloadWatch =
    match watched with
    | true -> HotReloadWatch.Watched
    | false -> HotReloadWatch.NotWatched

/// The service's own input and output, so a test can stand in for the clock and the disk.
type Env =
  { Ports: Ports
    Locks: FileLocks
    Stamp: string -> FileStamp
    Now: unit -> int64 }

module Env =
  let stampOf (path: string) : FileStamp =
    try
      let info = FileInfo path
      match info.Exists with
      | true -> FileStamp.Stamped(info.Length, info.LastWriteTimeUtc.Ticks)
      | false -> FileStamp.Unstamped
    with _ -> FileStamp.Unstamped

  /// The real disk, a real clock, the daemon's shared per-file locks and journals under `tweaksDir`.
  let production (tweaksDir: string) : Env =
    { Ports = McpNudge.productionPorts tweaksDir
      Locks = McpNudge.locks
      Stamp = stampOf
      Now = fun () -> DateTime.UtcNow.Ticks }

type Inspected =
  { FileStamp: FileStamp
    JournalStamp: FileStamp
    Names: Set<string>
    /// The names declared anywhere in the session's files when this was read. A binding built by a call of one of them has its whole
    /// right-hand side read, so a name declared later in another file asks for another look.
    Known: Set<string>
    File: FileInspection }

type Service =
  { Env: Env
    Memories: ConcurrentDictionary<string, Memory>
    Declared: ConcurrentDictionary<string, FileStamp * Set<string>>
    Inspected: ConcurrentDictionary<string, Inspected>
    /// The calls a file makes, counted again only when the file's stamp moved.
    Applied: ConcurrentDictionary<string, FileStamp * ApplicationCounts> }

module Service =
  let create (env: Env) : Service =
    { Env = env
      Memories = ConcurrentDictionary<string, Memory>(StringComparer.Ordinal)
      Declared = ConcurrentDictionary<string, FileStamp * Set<string>>(StringComparer.Ordinal)
      Inspected = ConcurrentDictionary<string, Inspected>(StringComparer.Ordinal)
      Applied = ConcurrentDictionary<string, FileStamp * ApplicationCounts>(StringComparer.Ordinal) }

  let memoryOf (service: Service) (sessionId: string) : Memory =
    match service.Memories.TryGetValue sessionId with
    | true, memory -> memory
    | false, _ -> Memory.empty

  let update (service: Service) (sessionId: string) (change: Memory -> Memory) : unit =
    service.Memories.AddOrUpdate(sessionId, (fun _ -> change Memory.empty), (fun _ memory -> change memory)) |> ignore

  /// A session that is gone takes its memory with it.
  let forget (service: Service) (sessionId: string) : unit = service.Memories.TryRemove sessionId |> ignore

  /// The files a session owns, as the dashboard's worker data lists them.
  let ownedOf (sessionId: string) (workingDirectory: string) (files: (string * HotReloadWatch) list) : OwnedFiles =
    let full (path: string) = Path.GetFullPath(path, workingDirectory)
    { Session = sessionId
      WorkingDirectory = workingDirectory
      Paths = files |> List.map (fun (path, _) -> full path) |> Set.ofList
      Watched =
        files
        |> List.choose (fun (path, watch) ->
          match watch with
          | HotReloadWatch.Watched -> Some(full path)
          | HotReloadWatch.NotWatched -> None)
        |> Set.ofList }

  let isFSharpSource (path: string) : bool =
    let extension = Path.GetExtension path
    String.Equals(extension, ".fs", StringComparison.OrdinalIgnoreCase) || String.Equals(extension, ".fsx", StringComparison.OrdinalIgnoreCase)

  // The names a file declares at the start of a line: `let`, `let mutable`, `let rec`, `let inline`, `and`, with their access words.
  let declaration =
    Regex(@"^[ \t]*(?:let|and)[ \t]+(?:(?:rec|mutable|inline|private|internal|public)[ \t]+)*(?<name>[A-Za-z_][A-Za-z0-9_']*)", RegexOptions.Compiled ||| RegexOptions.Multiline)

  let declaredNames (text: string) : Set<string> =
    declaration.Matches text |> Seq.map (fun m -> m.Groups.["name"].Value) |> Set.ofSeq

  /// The names a file declares, read again only when the file's stamp moved.
  let declaredIn (service: Service) (path: string) : Set<string> =
    let stamp = service.Env.Stamp path
    match service.Declared.TryGetValue path with
    | true, (cachedStamp, names) when cachedStamp = stamp -> names
    | _ ->
      let names =
        match service.Env.Ports.Files.ReadBytes path with
        | Ok bytes -> declaredNames (Text.Encoding.UTF8.GetString bytes)
        | Error _ -> Set.empty
      service.Declared.[path] <- (stamp, names)
      names

  /// Run one raw request through the door, as `nudge_value` does: parse against the files this session owns, then execute under the file's lock.
  let door (service: Service) (owned: OwnedFiles) (raw: RawNudge) : Task<Result<Ran, NudgeRefusal>> =
    match parse owned service.Env.Ports.Files.KindOf raw with
    | Error refusal -> Task.FromResult(Error refusal)
    | Ok request -> execute service.Env.Ports service.Env.Locks Timeouts.nudgeFileLock request

  let inspectRaw (path: string) (address: string) : RawNudge =
    { Action = NudgeAction.toToken NudgeAction.Inspect; File = path; Address = address; Seen = ""; Literal = ""; Expression = "" }

  let itemsOf (path: string) (ran: Ran) : Result<SourceRef list * Inspection, string> =
    match ran.Outcome with
    | NudgeOutcome.Inspected inspection -> Ok(inspection.Items |> List.map (SourceRef.ofItem path), inspection)
    | other -> Error(sprintf "the door answered an inspect with %s" (NudgeOutcome.token other))

  /// The calls a file makes, read again only when the file's stamp moved. A file that cannot be read is not counted, and says so.
  let appliedIn (service: Service) (path: string) : ApplicationCounts =
    let stamp = service.Env.Stamp path
    match service.Applied.TryGetValue path with
    | true, (cachedStamp, counts) when cachedStamp = stamp -> counts
    | _ ->
      let counts =
        match service.Env.Ports.Files.ReadBytes path with
        | Ok bytes -> CallFollow.applicationsIn (Text.Encoding.UTF8.GetString bytes)
        | Error fault -> ApplicationCounts.NotCounted fault.Reason
      service.Applied.[path] <- (stamp, counts)
      counts

  /// The bindings of a file that the door lists no point of but a file builds with a call of a name the session declares: the
  /// addresses of their whole right-hand sides, to be read so the call can be followed.
  let callBuiltIn (service: Service) (path: string) (names: Set<string>) (known: Set<string>) : TweakAddress list =
    match service.Env.Ports.Files.ReadBytes path with
    | Error _ -> []
    | Ok bytes ->
      match TweakAddress.declaredBindings (Text.Encoding.UTF8.GetString bytes) with
      | Ok declared -> CallFollow.callBuiltToRead names known declared
      | Error _ -> []

  /// Inspect one file through the door, with the whole right-hand side of each of `names` the listing carries only parts of, and of
  /// each of `names` the file builds with a call of one of `known`.
  ///
  /// `fileStamp` and `journalStamp` are the stamps taken BEFORE anything is read, and they are what the inspection is cached under. A
  /// stamp taken after the reading can belong to a file that was written meanwhile (the knob's own write runs while the page renders
  /// the row it just started): old content under the new stamp would be served as current until the next write, and the row would say
  /// the file holds a value it no longer does. Stamped first, a file written during the read is simply read again next time.
  let inspectFile
    (service: Service)
    (owned: OwnedFiles)
    (names: Set<string>)
    (known: Set<string>)
    (path: string)
    (fileStamp: FileStamp)
    (journalStamp: FileStamp)
    : Task<Result<Inspected, string>> =
    task {
      let! listed = door service owned (inspectRaw path "")
      match listed with
      | Error refusal -> return Error(NudgeRefusal.rule refusal)
      | Ok ran ->
        match itemsOf path ran with
        | Error why -> return Error why
        | Ok(items, inspection) ->
          let mutable wholes = []
          let mutable failure = None
          for address in (SourceIndex.wholesToRead names items @ callBuiltIn service path names known) |> List.distinct do
            match failure with
            | Some _ -> ()
            | None ->
              let! answered = door service owned (inspectRaw path (NudgeAddress.format address))
              match answered with
              | Error refusal -> failure <- Some(NudgeRefusal.rule refusal)
              | Ok whole ->
                match itemsOf path whole with
                | Ok(found, _) -> wholes <- wholes @ found
                | Error why -> failure <- Some why
          match failure with
          | Some why -> return Error why
          | None ->
            let watching =
              match Ownership.tryOwn owned service.Env.Ports.Files.KindOf path with
              | Ok file -> Ownership.watchOf file
              | Error _ -> WatchStatus.NotWatched
            // Which bindings the file declares `let mutable`: the listing does not say, and a live value cannot. A file the shell cannot
            // read again, or that does not parse, adds none, and the rows of its bindings then read as declared plainly.
            let mutables =
              match service.Env.Ports.Files.ReadBytes path with
              | Error _ -> Set.empty
              | Ok bytes ->
                match TweakAddress.mutableBindings (Text.Encoding.UTF8.GetString bytes) with
                | Ok names -> names
                | Error _ -> Set.empty
            match inspection.Listing with
            | Listing.Truncated(shown, total) ->
              return Error(sprintf "the file has %d tweakable points and one listing holds %d, so a binding may be missing from it" total shown)
            | Listing.Complete ->
              return
                Ok
                  { FileStamp = fileStamp
                    JournalStamp = journalStamp
                    Names = names
                    Known = known
                    File =
                      { File = path
                        Watching = watching
                        Items = items
                        Wholes = wholes
                        MutableBindings = mutables
                        UndoSteps = inspection.UndoSteps
                        RedoSteps = inspection.RedoSteps
                        Revision = FileStamp.revision fileStamp } }
    }

  /// What the session's files say, for the names asked about. A file that does not declare any of them is not parsed; one that does is
  /// parsed once per change (its file or its journal moving), and again only when a new name is asked about.
  let inspectAsked
    (service: Service)
    (owned: OwnedFiles)
    (known: Set<string>)
    (sources: string list)
    (names: Set<string>)
    : Task<FileInspection list * (string * string) list> =
    task {
      let candidates = sources |> List.filter (fun path -> not (Set.isEmpty (Set.intersect names (declaredIn service path))))
      let mutable files = []
      let mutable unreadable = []
      for path in candidates do
        let fileStamp = service.Env.Stamp path
        let journalStamp =
          match Ownership.tryOwn owned service.Env.Ports.Files.KindOf path with
          | Ok file -> service.Env.Stamp(service.Env.Ports.JournalPathOf file)
          | Error _ -> FileStamp.Unstamped
        match service.Inspected.TryGetValue path with
        | true, cached when cached.FileStamp = fileStamp && cached.JournalStamp = journalStamp && Set.isSubset names cached.Names && Set.isSubset known cached.Known ->
          files <- files @ [ cached.File ]
        | cachedBefore ->
          let wanted =
            match cachedBefore with
            | true, cached when cached.FileStamp = fileStamp -> Set.union names cached.Names
            | _ -> names
          let! result = inspectFile service owned wanted known path fileStamp journalStamp
          match result with
          | Ok inspected ->
            service.Inspected.[path] <- inspected
            files <- files @ [ inspected.File ]
          | Error why ->
            service.Inspected.TryRemove path |> ignore
            unreadable <- unreadable @ [ path, why ]
      return files, unreadable
    }

  /// What the session's files say, for the names asked about, and for the callees of the bindings among them that a file builds with a
  /// call (a second look, only when there are such callees not already asked about). The calls of every owned file are counted only
  /// then, because only a followed call needs the number.
  let indexFor (service: Service) (owned: OwnedFiles) (names: Set<string>) : Task<SourceIndex> =
    task {
      let sources = owned.Paths |> Set.toList |> List.filter isFSharpSource
      let known = sources |> List.map (declaredIn service) |> Set.unionMany
      let! firstFiles, firstUnreadable = inspectAsked service owned known sources names
      let callees = firstFiles |> List.collect (fun file -> file.Wholes) |> CallFollow.calleeNamesOf
      let! files, unreadable =
        match Set.isSubset callees names with
        | true -> Task.FromResult(firstFiles, firstUnreadable)
        | false -> inspectAsked service owned known sources (Set.union names callees)
      let applied =
        match Set.isEmpty callees with
        | true -> ApplicationCounts.NotCounted "no row of the pane is built by a call of a function of the session"
        | false -> CallFollow.combineCounts (sources |> List.map (fun path -> path, appliedIn service path))
      return { Files = files; Unreadable = unreadable; Applied = applied }
    }

  /// Where a project keeps the range and step a person chose for each number, read and checked by the one module every writer shares.
  let limitsPathOf (workingDirectory: string) : string = TweakLimitsFile.pathOf workingDirectory

  let limitsIn (service: Service) (workingDirectory: string) : TweakLimitsFile.Read =
    TweakLimitsFile.read service.Env.Ports.Files workingDirectory

  /// The choices a view draws from: the file's, or none while it cannot be read (the write path says why it refuses).
  let limitsBookFor (service: Service) (workingDirectory: string) : LimitsBook =
    match limitsIn service workingDirectory with
    | TweakLimitsFile.Read.Chosen book -> book
    | TweakLimitsFile.Read.Unreadable _ -> LimitsBook.empty

  /// The rows for a session's walked values, from what its files say now and what the dashboard did to each row. A row waits for the
  /// reload verdict of its write for as long as a save waits for its compile (`Timeouts.compileQueueWait`): past that no verdict is
  /// coming, and the row says so.
  let viewFor
    (service: Service)
    (sessionId: string)
    (owned: OwnedFiles)
    (snapshot: LiveValueTree.LiveValueSnapshot)
    (current: SessionReload)
    : Task<TweakView> =
    task {
      match snapshot.Bindings with
      | [] -> return TweakView.none
      | _ ->
        let! index = indexFor service owned (namesOf snapshot)
        let limits = limitsBookFor service owned.WorkingDirectory
        let view = Rows.build index snapshot (memoryOf service sessionId) current (service.Env.Now()) Timeouts.compileQueueWait limits
        // Remember which rows are in step with their file now, so a later difference can be told from a file edit.
        update service sessionId (Memory.observe index view)
        return view
    }

  /// The rows for a session whose file list the dashboard may not have. A list that is not known makes every row say the files are
  /// unknown, never that no file holds it.
  let viewForFiles
    (service: Service)
    (sessionId: string)
    (workingDirectory: string)
    (files: Result<(string * bool) list, string>)
    (snapshot: LiveValueTree.LiveValueSnapshot)
    (current: SessionReload)
    : Task<TweakView> =
    match files with
    | Ok listed ->
      let owned = ownedOf sessionId workingDirectory (listed |> List.map (fun (path, watched) -> path, HotReloadWatch.ofFlag watched))
      viewFor service sessionId owned snapshot current
    | Error reason ->
      let unknown : SourceIndex = { SourceIndex.empty with Unreadable = [ "the session's project files", reason ] }
      Task.FromResult(Rows.build unknown snapshot (memoryOf service sessionId) current (service.Env.Now()) Timeouts.compileQueueWait (limitsBookFor service workingDirectory))

  /// The expression the row was showing when it was clicked, as the page reported it. The cached inspection has the real one
  /// (with its span and kind) when the hash still matches; otherwise it is what the page said, with no position.
  let attemptedFrom (service: Service) (row: RowKey) (file: string) (addressText: string) (seen: string) (seenText: string) : SourceRef =
    let known =
      match service.Inspected.TryGetValue file with
      | true, cached ->
        cached.File.Items @ cached.File.Wholes
        |> List.tryFind (fun item -> SourceRef.addressText item = addressText && item.Hash = seen)
      | false, _ -> None
    match known with
    | Some item -> item
    | None ->
      let address =
        match NudgeAddress.tryParse addressText with
        | Ok parsed -> parsed
        | Error _ -> { ModulePath = []; BindingName = row.Binding; Path = RowKey.steps row }
      let kind =
        match readLiteralText seenText with
        | Ok(value, _) -> ItemKind.Knob value
        | Error _ -> ItemKind.Formula
      { File = file
        Address = address
        Text = seenText
        Hash = seen
        Span = { Line = 0; Column = 0; EndLine = 0; EndColumn = 0 }
        Kind = kind }

  let attemptedOf (service: Service) (request: TweakRequest) : SourceRef =
    attemptedFrom service request.Row request.File request.Address request.Seen request.SeenText

  /// A step count becomes the literal it means, from the number the row showed. Anything else is already what the door takes.
  let resolve (limits: TweakLimitsFile.Read) (attempted: SourceRef) (request: TweakRequest) : Result<TweakRequest, NudgeRefusal> =
    match request.Verb with
    | TweakVerb.Steps count ->
      match limits with
      | TweakLimitsFile.Read.Unreadable reason -> Error(NudgeRefusal.ValueOutOfRange reason)
      | TweakLimitsFile.Read.Chosen book ->
        let chosen = LimitsBook.forRow book (RowKey.text request.Row)
        match Stepping.literalAfterWithin chosen (Control.ofSource attempted) count with
        | Ok text -> Ok { request with Verb = TweakVerb.SetLiteral text }
        | Error(StepRefusal.OutsideLimits(landing, lowest, highest)) ->
          Error(
            NudgeRefusal.ValueOutOfRange(
              sprintf "%s is outside the range chosen for %s (%s)." (landing.ToString("R", Globalization.CultureInfo.InvariantCulture)) (RowKey.text request.Row) (NumberLimits.rangeText lowest highest)
            )
          )
        | Error StepRefusal.OutOfRange ->
          Error(NudgeRefusal.ValueKindMismatch(sprintf "%s is not a number a step can move, or the step leaves the numbers a literal can spell." attempted.Text))
    | TweakVerb.SetLiteral _
    | TweakVerb.SetExpression _
    | TweakVerb.Undo
    | TweakVerb.Redo -> Ok request

  /// Do what a row asked. The row shows `Writing` at once, then the door's answer (a landed write, or a refusal that stays on the row);
  /// `changed` tells the page's stream to draw both. `currentReload` is the session's reload verdict as of the call, which is the
  /// baseline the row waits to see move.
  let act
    (service: Service)
    (sessionId: string)
    (owned: OwnedFiles)
    (currentReload: unit -> SessionReload)
    (changed: unit -> unit)
    (request: TweakRequest)
    : Task<unit> =
    task {
      let attempted = attemptedOf service request
      let baseline = currentReload ()
      match resolve (limitsIn service owned.WorkingDirectory) attempted request with
      | Error refusal ->
        update service sessionId (Memory.finished request.Row attempted (Error refusal) baseline (service.Env.Now()))
        changed ()
      | Ok resolved ->
        update service sessionId (Memory.started resolved.Row (TweakVerb.writeKindOf resolved.Verb) attempted)
        changed ()
        let! result = door service owned (TweakRequest.rawOf resolved)
        update service sessionId (Memory.finished resolved.Row attempted result baseline (service.Env.Now()))
        changed ()
    }

  /// Put a value into the session for a row, or put the file's value back, and remember what the row holds now. The statement is
  /// evaluated the way the Evaluate box does it (`evalCode`), so the session's own answer, a value or an error, is the Output pane's
  /// job. A typed value the literal cannot take is refused with the door's own rule and stays on the row. A value equal to the file's
  /// ends the row's tweak. `changed` tells the page's stream to draw, whatever came back.
  let push
    (service: Service)
    (sessionId: string)
    (evalCode: string -> Task<Result<string, string>>)
    (changed: unit -> unit)
    (row: RowKey)
    (attempted: SourceRef)
    (via: PushVia)
    (verb: SageFs.Server.LiveBindingsTweakView.PushVerb)
    (typed: string)
    : Task<unit> =
    task {
      let planned =
        match verb with
        | SageFs.Server.LiveBindingsTweakView.PushVerb.Push -> PushCode.plan via row attempted.Text typed
        | SageFs.Server.LiveBindingsTweakView.PushVerb.Revert -> PushCode.revert via row attempted.Text
      match planned with
      | Error refusal ->
        update service sessionId (Memory.finished row attempted (Error refusal) SessionReload.NoReloadYet (service.Env.Now()))
        changed ()
      | Ok plan ->
        update service sessionId (Memory.forgetRow row)
        let! answer = evalCode plan.Code
        match answer with
        | Ok _ ->
          match plan.Effect with
          | PushCode.PushEffect.MatchesFile -> update service sessionId (Memory.unpushed row)
          | PushCode.PushEffect.StartsTweak -> update service sessionId (Memory.pushed row plan.Printed)
        // The session refused the statement (a type error, a name that is not there). Its own message is in the Output pane, and
        // the row still shows what it showed.
        | Error _ -> ()
        changed ()
    }

  /// Put the file's value back for several rows in ONE evaluation, as ONE interaction (`PushCode.oneInteraction`): the dashboard's eval
  /// runs only the first interaction of the text it is given, so one `;;` per statement would revert the first row and leave the
  /// rest. One eval is also one read of the session's values afterwards, and the session changes in one step. A row whose file text
  /// is not a literal is refused on its row.
  let revertMany
    (service: Service)
    (sessionId: string)
    (evalCode: string -> Task<Result<string, string>>)
    (changed: unit -> unit)
    (rows: (RowKey * SourceRef * PushVia) list)
    : Task<unit> =
    task {
      let planned =
        rows
        |> List.choose (fun (row, attempted, via) ->
          match PushCode.revert via row attempted.Text with
          | Ok plan -> Some(row, plan)
          | Error refusal ->
            update service sessionId (Memory.finished row attempted (Error refusal) SessionReload.NoReloadYet (service.Env.Now()))
            None)
      match planned with
      | [] -> ()
      | _ ->
        let! answer = evalCode (planned |> List.map (fun (_, plan) -> plan.Code) |> PushCode.oneInteraction)
        match answer with
        | Ok _ ->
          for row, _ in planned do
            update service sessionId (Memory.unpushed row)
        | Error _ -> ()
      changed ()
    }

  /// Writes of the limits file are one at a time: each reads the file, changes one row and replaces it, so two at once would lose one.
  let private limitsGate = obj ()

  /// Keep or clear one row's range and step, from the three texts a person typed. Answers what the editor should say afterwards:
  /// nothing when the choice was kept, the reason when it was not. A file that cannot be read is never replaced: the person's other
  /// choices are in it.
  let setLimits (service: Service) (workingDirectory: string) (row: RowKey) (texts: LimitsTexts) : string =
    match NumberLimits.ofTexts texts with
    | Error refusal -> NumberLimits.describe refusal
    | Ok chosen ->
      lock limitsGate (fun () ->
        match limitsIn service workingDirectory with
        | TweakLimitsFile.Read.Unreadable reason -> reason
        | TweakLimitsFile.Read.Chosen book ->
          match TweakLimitsFile.save service.Env.Ports.Files workingDirectory (LimitsBook.set (RowKey.text row) chosen book) with
          | Ok() -> ""
          | Error fault -> sprintf "The range could not be saved: %s" fault.Reason)
