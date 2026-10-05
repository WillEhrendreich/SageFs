// Who is still on the OLD method after a save re-signed or removed a function.
//
// A function whose signature changed is a NEW method to the running app: the callers saved with it move onto it, and the
// old method stays in the process for whatever still holds it. A caller in ANOTHER file of the project is not saved with
// it, so it keeps calling the old method until that file is saved too. The build would not pass until it is, so the
// window is short, but while it is open the old behavior runs and nothing said so.
//
// This is the state that says so. It is a closed set, a session's worker holds one ledger of it, every reload report
// carries the ledger's current state, and it clears exactly when a caller's declaration is patched. Pure: the decisions
// about WHO calls what live in `CallerCheck`, and the wire is read by clients that do not know the planner.
namespace SageFs.Features.CallerState

open System
open System.IO
open System.Text.Json

/// What a save did to the declaration that stranded its callers.
[<RequireQualifiedAccess>]
type SignatureCause =
  /// Its header changed, so the saved code is a new method.
  | ReSigned
  /// It is gone from the file (a rename is a removal of the old name). The old method stays in the process.
  | Removed

/// Why a caller was found by name rather than by the compiler's own symbol resolution.
[<RequireQualifiedAccess>]
type NameOnlyReason =
  /// The worker holds no compiler options for the project the saved file belongs to.
  | NoProjectOptions
  /// The compiler had not answered when the bound passed.
  | CompilerTimedOut of bound: TimeSpan
  /// The compiler could not check the project.
  | CompilerFailed of why: string
  /// The compiler checked the project and could not resolve this use (an error on its line), so it stays a caller until
  /// something proves it is not.
  | UseNotResolved
  /// A name that is gone from the saved file resolves to nothing, so only its spelling can be searched for.
  | DeclarationRemoved

/// How a caller was found. A name match can over-report (a same-named function elsewhere) and never under-reports.
[<RequireQualifiedAccess>]
type SiteEvidence =
  /// The compiler resolved the use to the declaration that was re-signed.
  | ResolvedByCompiler
  | MatchedByName of why: NameOnlyReason

/// One call that is still on the old method.
type CallSite = {
  /// The caller's file, as the worker knows it (an absolute path).
  File: string
  /// The line of the call (1-based).
  Line: int
  /// The qualified declaration that holds the call, or empty for code outside any declaration. A save of the file clears
  /// the site when it patches this declaration.
  Caller: string
  Evidence: SiteEvidence
}

/// A save's change to one declaration, which leaves callers behind.
type SignatureEdit = {
  /// The qualified name: module path, containers, name.
  Declaration: string
  Cause: SignatureCause
  /// The file that declares it.
  File: string
}

/// Why callers could not be listed at all.
[<RequireQualifiedAccess>]
type UncheckedReason =
  /// No project is loaded for the saved file, so there are no other files to read.
  | ProjectNotLoaded
  /// One of the project's other files could not be read.
  | SourceUnreadable of file: string * detail: string
  /// The name is not an identifier (an operator, say), so there is nothing to search for.
  | NotSearchableByName of name: string

/// The answer for one declaration.
[<RequireQualifiedAccess>]
type CallersCheck =
  | NoCallers
  | Callers of first: CallSite * rest: CallSite list
  | NotChecked of UncheckedReason

/// A declaration whose callers are still on the old method. Head and rest, so it is never built with nobody in it.
type PendingCallers = {
  Edit: SignatureEdit
  First: CallSite
  Rest: CallSite list
}

/// A declaration whose callers nobody could list.
type UncheckedCallers = {
  Unresolved: SignatureEdit
  Why: UncheckedReason
}

/// Everything a worker is holding. Two lists, because "these are on the old method" and "we could not tell" are different
/// facts and both stay until something ends them.
type CallerLedger = {
  Pending: PendingCallers list
  Unchecked: UncheckedCallers list
}

/// What a report says. Closed: a client that gets a payload with none of these has been told nothing, and that is its
/// own case.
[<RequireQualifiedAccess>]
type CallersState =
  /// Nothing is on an old method.
  | CallersCurrent
  /// These declarations have callers in other files that still run the old method, and these could not be checked.
  | CallersPending of first: PendingCallers * rest: PendingCallers list * unchecked: UncheckedCallers list
  /// Nothing is known to be pending, and these declarations' callers could not be checked.
  | CallersNotChecked of first: UncheckedCallers * rest: UncheckedCallers list
  /// The report carries no word on callers (a worker that predates the field). Not `CallersCurrent`: nobody said all is well.
  | CallersNotReported

/// Why a callers object could not be read. A closed set, so the daemon can log the reason and a test can assert which.
[<RequireQualifiedAccess>]
type CallersReadError =
  | NotJson of detail: string
  /// The `state` is not one this reader knows.
  | UnknownState of token: string
  /// A token in `field` is not one this reader knows.
  | UnknownToken of field: string * token: string
  /// A state that names declarations has none to name.
  | NothingListed of detail: string

module CallersReadError =
  let describe (error: CallersReadError) : string =
    match error with
    | CallersReadError.NotJson detail -> sprintf "not JSON: %s" detail
    | CallersReadError.UnknownState token -> sprintf "unknown callers state '%s'" token
    | CallersReadError.UnknownToken(field, token) -> sprintf "unknown %s '%s'" field token
    | CallersReadError.NothingListed detail -> detail

/// What happened that moves the ledger.
[<RequireQualifiedAccess>]
type LedgerEvent =
  /// A save re-signed or removed these declarations, and this is who calls each. An empty list is a save that left no
  /// callers behind, and records nothing. A declaration already in the ledger is replaced by the new answer.
  | Checked of checks: (SignatureEdit * CallersCheck) list
  /// A save of `file` patched these qualified declarations. A site clears when its caller is among them, or has no
  /// declaration and its file landed at all.
  | FileLanded of file: string * patched: string list
  /// The app restarted, so the process has no old methods.
  | AppRestarted

module CallerLedger =
  let empty : CallerLedger = { Pending = []; Unchecked = [] }

  let private sitesOf (p: PendingCallers) : CallSite list = p.First :: p.Rest

  let private stillOnOldMethod (file: string) (patched: string list) (site: CallSite) : bool =
    let cleared =
      String.Equals(site.File, file, StringComparison.Ordinal)
      && (String.IsNullOrEmpty site.Caller || List.contains site.Caller patched)
    not cleared

  let private recordCheck (ledger: CallerLedger) (edit: SignatureEdit, check: CallersCheck) : CallerLedger =
    let others =
      { Pending = ledger.Pending |> List.filter (fun p -> p.Edit.Declaration <> edit.Declaration)
        Unchecked = ledger.Unchecked |> List.filter (fun u -> u.Unresolved.Declaration <> edit.Declaration) }
    match check with
    | CallersCheck.NoCallers -> others
    | CallersCheck.Callers(first, rest) -> { others with Pending = others.Pending @ [ { Edit = edit; First = first; Rest = rest } ] }
    | CallersCheck.NotChecked why -> { others with Unchecked = others.Unchecked @ [ { Unresolved = edit; Why = why } ] }

  let apply (event: LedgerEvent) (ledger: CallerLedger) : CallerLedger =
    match event with
    | LedgerEvent.AppRestarted -> empty
    | LedgerEvent.Checked checks -> checks |> List.fold recordCheck ledger
    | LedgerEvent.FileLanded(file, patched) ->
      let pending =
        ledger.Pending
        |> List.choose (fun p ->
          match sitesOf p |> List.filter (stillOnOldMethod file patched) with
          | [] -> None
          | first :: rest -> Some { p with First = first; Rest = rest })
      { ledger with Pending = pending }

  let stateOf (ledger: CallerLedger) : CallersState =
    match ledger.Pending, ledger.Unchecked with
    | first :: rest, unchecked -> CallersState.CallersPending(first, rest, unchecked)
    | [], first :: rest -> CallersState.CallersNotChecked(first, rest)
    | [], [] -> CallersState.CallersCurrent

module SignatureCause =
  let token (cause: SignatureCause) : string =
    match cause with
    | SignatureCause.ReSigned -> "ReSigned"
    | SignatureCause.Removed -> "Removed"

  let all : SignatureCause list = [ SignatureCause.ReSigned; SignatureCause.Removed ]

  let ofToken (text: string) : SignatureCause option = all |> List.tryFind (fun c -> token c = text)

  /// How the cause reads in a sentence.
  let describe (cause: SignatureCause) : string =
    match cause with
    | SignatureCause.ReSigned -> "was re-signed"
    | SignatureCause.Removed -> "was removed"

module NameOnlyReason =
  let token (reason: NameOnlyReason) : string =
    match reason with
    | NameOnlyReason.NoProjectOptions -> "NoProjectOptions"
    | NameOnlyReason.CompilerTimedOut _ -> "CompilerTimedOut"
    | NameOnlyReason.CompilerFailed _ -> "CompilerFailed"
    | NameOnlyReason.UseNotResolved -> "UseNotResolved"
    | NameOnlyReason.DeclarationRemoved -> "DeclarationRemoved"

  /// The part of a reason that is not its token: seconds for a timeout, the compiler's own words for a failure.
  let detail (reason: NameOnlyReason) : string =
    match reason with
    | NameOnlyReason.CompilerTimedOut bound -> bound.TotalSeconds.ToString("R", Globalization.CultureInfo.InvariantCulture)
    | NameOnlyReason.CompilerFailed why -> why
    | NameOnlyReason.NoProjectOptions
    | NameOnlyReason.UseNotResolved
    | NameOnlyReason.DeclarationRemoved -> ""

  let ofToken (text: string) (detail: string) : NameOnlyReason option =
    match text with
    | "NoProjectOptions" -> Some NameOnlyReason.NoProjectOptions
    | "CompilerTimedOut" ->
      match Double.TryParse(detail, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
      | true, seconds -> Some(NameOnlyReason.CompilerTimedOut(TimeSpan.FromSeconds seconds))
      | false, _ -> None
    | "CompilerFailed" -> Some(NameOnlyReason.CompilerFailed detail)
    | "UseNotResolved" -> Some NameOnlyReason.UseNotResolved
    | "DeclarationRemoved" -> Some NameOnlyReason.DeclarationRemoved
    | _ -> None

  let describe (reason: NameOnlyReason) : string =
    match reason with
    | NameOnlyReason.NoProjectOptions -> "the project's compiler options are not available"
    | NameOnlyReason.CompilerTimedOut bound -> sprintf "the compiler did not answer within %.0fs" bound.TotalSeconds
    | NameOnlyReason.CompilerFailed why -> sprintf "the compiler could not check the project: %s" why
    | NameOnlyReason.UseNotResolved -> "the compiler could not resolve this use"
    | NameOnlyReason.DeclarationRemoved -> "the declaration is gone, so there is nothing to resolve to"

module UncheckedReason =
  let token (reason: UncheckedReason) : string =
    match reason with
    | UncheckedReason.ProjectNotLoaded -> "ProjectNotLoaded"
    | UncheckedReason.SourceUnreadable _ -> "SourceUnreadable"
    | UncheckedReason.NotSearchableByName _ -> "NotSearchableByName"

  /// The file for an unreadable source, the name for an unsearchable one.
  let subject (reason: UncheckedReason) : string =
    match reason with
    | UncheckedReason.ProjectNotLoaded -> ""
    | UncheckedReason.SourceUnreadable(file, _) -> file
    | UncheckedReason.NotSearchableByName name -> name

  let detail (reason: UncheckedReason) : string =
    match reason with
    | UncheckedReason.ProjectNotLoaded
    | UncheckedReason.NotSearchableByName _ -> ""
    | UncheckedReason.SourceUnreadable(_, why) -> why

  let ofToken (text: string) (subject: string) (detail: string) : UncheckedReason option =
    match text with
    | "ProjectNotLoaded" -> Some UncheckedReason.ProjectNotLoaded
    | "SourceUnreadable" -> Some(UncheckedReason.SourceUnreadable(subject, detail))
    | "NotSearchableByName" -> Some(UncheckedReason.NotSearchableByName subject)
    | _ -> None

  let describe (reason: UncheckedReason) : string =
    match reason with
    | UncheckedReason.ProjectNotLoaded -> "no project is loaded for this file, so there are no other files to read"
    | UncheckedReason.SourceUnreadable(file, why) -> sprintf "%s could not be read (%s)" (Path.GetFileName file) why
    | UncheckedReason.NotSearchableByName name -> sprintf "'%s' is not a name a search can find" name

module SiteEvidence =
  let token (evidence: SiteEvidence) : string =
    match evidence with
    | SiteEvidence.ResolvedByCompiler -> "ResolvedByCompiler"
    | SiteEvidence.MatchedByName _ -> "MatchedByName"

module CallersState =
  let token (state: CallersState) : string =
    match state with
    | CallersState.CallersCurrent -> "CallersCurrent"
    | CallersState.CallersPending _ -> "CallersPending"
    | CallersState.CallersNotChecked _ -> "CallersNotChecked"
    | CallersState.CallersNotReported -> "CallersNotReported"

  let private pendingOf (state: CallersState) : PendingCallers list =
    match state with
    | CallersState.CallersPending(first, rest, _) -> first :: rest
    | CallersState.CallersCurrent
    | CallersState.CallersNotChecked _
    | CallersState.CallersNotReported -> []

  let private uncheckedOf (state: CallersState) : UncheckedCallers list =
    match state with
    | CallersState.CallersPending(_, _, unchecked) -> unchecked
    | CallersState.CallersNotChecked(first, rest) -> first :: rest
    | CallersState.CallersCurrent
    | CallersState.CallersNotReported -> []

  let private sitesIn (p: PendingCallers) : CallSite list = p.First :: p.Rest

  let private whereIs (site: CallSite) : string =
    let place = sprintf "%s:%d" (Path.GetFileName site.File) site.Line
    let holder = match site.Caller with "" -> "" | caller -> sprintf " (%s)" caller
    let how =
      match site.Evidence with
      | SiteEvidence.ResolvedByCompiler -> ""
      | SiteEvidence.MatchedByName why -> sprintf " [matched by name: %s]" (NameOnlyReason.describe why)
    place + holder + how

  let private describePending (p: PendingCallers) : string =
    let all = sitesIn p
    let sites = all |> List.map whereIs |> String.concat ", "
    let who =
      match all with
      | [ _ ] -> "a caller in another file still calls"
      | many -> sprintf "%d callers in other files still call" many.Length
    sprintf "%s %s, and %s the old method: %s" p.Edit.Declaration (SignatureCause.describe p.Edit.Cause) who sites

  let private describeUnchecked (u: UncheckedCallers) : string =
    sprintf "the callers of %s were not checked: %s" u.Unresolved.Declaration (UncheckedReason.describe u.Why)

  /// What the state says, in a sentence a reader can act on without knowing the history. Empty when there is no news.
  let describe (state: CallersState) : string =
    match state with
    | CallersState.CallersCurrent -> ""
    | CallersState.CallersNotReported -> "This worker did not say whether callers in other files are still on an old method."
    | CallersState.CallersPending(first, rest, unchecked) ->
      let pending = first :: rest |> List.map describePending
      let notChecked = unchecked |> List.map describeUnchecked
      pending @ notChecked |> String.concat "; "
    | CallersState.CallersNotChecked(first, rest) -> first :: rest |> List.map describeUnchecked |> String.concat "; "

  let private filesToSave (pending: PendingCallers list) : string list =
    pending |> List.collect sitesIn |> List.map (fun s -> Path.GetFileName s.File) |> List.distinct

  /// What to do next. Empty when there is nothing to do.
  let remedy (state: CallersState) : string =
    match state with
    | CallersState.CallersCurrent
    | CallersState.CallersNotReported -> ""
    | CallersState.CallersPending(first, rest, _) ->
      let files = filesToSave (first :: rest) |> String.concat ", "
      sprintf
        "Save %s: each calls a function that no longer matches, and only a save moves a call onto the new method. If a call needs no edit, restart the app instead."
        files
    | CallersState.CallersNotChecked(first, rest) ->
      let names = first :: rest |> List.map (fun u -> u.Unresolved.Declaration) |> String.concat ", "
      sprintf "Check the callers of %s yourself, or restart the app so every caller starts on the new method." names

  // ── the wire ──────────────────────────────────────────────────────────────

  let private writeEdit (w: Utf8JsonWriter) (edit: SignatureEdit) =
    w.WriteString("declaration", edit.Declaration)
    w.WriteString("cause", SignatureCause.token edit.Cause)
    w.WriteString("declaredIn", edit.File)

  let private writeSite (w: Utf8JsonWriter) (site: CallSite) =
    w.WriteStartObject()
    w.WriteString("file", site.File)
    w.WriteNumber("line", site.Line)
    w.WriteString("caller", site.Caller)
    w.WriteString("evidence", SiteEvidence.token site.Evidence)
    match site.Evidence with
    | SiteEvidence.ResolvedByCompiler ->
      w.WriteString("nameOnlyReason", "")
      w.WriteString("nameOnlyDetail", "")
    | SiteEvidence.MatchedByName why ->
      w.WriteString("nameOnlyReason", NameOnlyReason.token why)
      w.WriteString("nameOnlyDetail", NameOnlyReason.detail why)
    w.WriteEndObject()

  let private writePending (w: Utf8JsonWriter) (p: PendingCallers) =
    w.WriteStartObject()
    writeEdit w p.Edit
    w.WriteStartArray "sites"
    for site in sitesIn p do
      writeSite w site
    w.WriteEndArray()
    w.WriteEndObject()

  let private writeUnchecked (w: Utf8JsonWriter) (u: UncheckedCallers) =
    w.WriteStartObject()
    writeEdit w u.Unresolved
    w.WriteString("why", UncheckedReason.token u.Why)
    w.WriteString("whySubject", UncheckedReason.subject u.Why)
    w.WriteString("whyDetail", UncheckedReason.detail u.Why)
    w.WriteEndObject()

  /// The JSON a report carries for the state: the token, the words (so a client that shows text shows the same text), and
  /// the structure (so a client that lists files can). One writer; the worker's SSE payload and the daemon's status both
  /// carry this object.
  let toJson (state: CallersState) : string =
    use stream = new MemoryStream()
    do
      use w = new Utf8JsonWriter(stream)
      w.WriteStartObject()
      w.WriteString("state", token state)
      w.WriteString("message", describe state)
      w.WriteString("suggestedAction", remedy state)
      w.WriteStartArray "pending"
      for p in pendingOf state do
        writePending w p
      w.WriteEndArray()
      w.WriteStartArray "notChecked"
      for u in uncheckedOf state do
        writeUnchecked w u
      w.WriteEndArray()
      w.WriteEndObject()
    Text.Encoding.UTF8.GetString(stream.ToArray())

  let private text (e: JsonElement) (name: string) : string =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
    | _ -> ""

  let private items (e: JsonElement) (name: string) : JsonElement list =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Array -> [ for item in v.EnumerateArray() -> item ]
    | _ -> []

  let private readEdit (e: JsonElement) : Result<SignatureEdit, CallersReadError> =
    match SignatureCause.ofToken (text e "cause") with
    | Some cause -> Ok { Declaration = text e "declaration"; Cause = cause; File = text e "declaredIn" }
    | None -> Error(CallersReadError.UnknownToken("cause", text e "cause"))

  let private readSite (e: JsonElement) : Result<CallSite, CallersReadError> =
    let line =
      match e.TryGetProperty "line" with
      | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
      | _ -> 0
    let evidence =
      match text e "evidence" with
      | "ResolvedByCompiler" -> Ok SiteEvidence.ResolvedByCompiler
      | "MatchedByName" ->
        match NameOnlyReason.ofToken (text e "nameOnlyReason") (text e "nameOnlyDetail") with
        | Some why -> Ok(SiteEvidence.MatchedByName why)
        | None -> Error(CallersReadError.UnknownToken("nameOnlyReason", text e "nameOnlyReason"))
      | other -> Error(CallersReadError.UnknownToken("evidence", other))
    evidence |> Result.map (fun ev -> { File = text e "file"; Line = line; Caller = text e "caller"; Evidence = ev })

  let private allOk (results: Result<'a, CallersReadError> list) : Result<'a list, CallersReadError> =
    results
    |> List.fold
      (fun acc r ->
        match acc, r with
        | Error e, _ -> Error e
        | Ok _, Error e -> Error e
        | Ok xs, Ok x -> Ok(xs @ [ x ]))
      (Ok [])

  let private readPending (e: JsonElement) : Result<PendingCallers, CallersReadError> =
    match readEdit e, items e "sites" |> List.map readSite |> allOk with
    | Ok edit, Ok(first :: rest) -> Ok { Edit = edit; First = first; Rest = rest }
    | Ok _, Ok [] -> Error(CallersReadError.NothingListed "a pending declaration has no sites")
    | Error e, _
    | _, Error e -> Error e

  let private readUnchecked (e: JsonElement) : Result<UncheckedCallers, CallersReadError> =
    match readEdit e, UncheckedReason.ofToken (text e "why") (text e "whySubject") (text e "whyDetail") with
    | Ok edit, Some why -> Ok { Unresolved = edit; Why = why }
    | Error e, _ -> Error e
    | Ok _, None -> Error(CallersReadError.UnknownToken("why", text e "why"))

  /// Reads the object `toJson` writes. The words are not read back: they are derived from the state, so what a daemon
  /// shows is always what this module would say, whatever wording the worker had.
  let ofElement (e: JsonElement) : Result<CallersState, CallersReadError> =
    match text e "state" with
    | "CallersCurrent" -> Ok CallersState.CallersCurrent
    | "CallersNotReported" -> Ok CallersState.CallersNotReported
    | "CallersPending" ->
      match items e "pending" |> List.map readPending |> allOk, items e "notChecked" |> List.map readUnchecked |> allOk with
      | Ok(first :: rest), Ok unchecked -> Ok(CallersState.CallersPending(first, rest, unchecked))
      | Ok [], _ -> Error(CallersReadError.NothingListed "CallersPending with nothing pending")
      | Error e, _
      | _, Error e -> Error e
    | "CallersNotChecked" ->
      match items e "notChecked" |> List.map readUnchecked |> allOk with
      | Ok(first :: rest) -> Ok(CallersState.CallersNotChecked(first, rest))
      | Ok [] -> Error(CallersReadError.NothingListed "CallersNotChecked with nothing unchecked")
      | Error e -> Error e
    | other -> Error(CallersReadError.UnknownState other)

  /// Reads the JSON text `toJson` writes. Empty text is a report with no word on callers.
  let ofJson (json: string) : Result<CallersState, CallersReadError> =
    match String.IsNullOrWhiteSpace json with
    | true -> Ok CallersState.CallersNotReported
    | false ->
      try
        use doc = JsonDocument.Parse json
        ofElement doc.RootElement
      with :? JsonException as ex -> Error(CallersReadError.NotJson ex.Message)

  /// The same object as a value a JSON serializer writes as it stands, for the daemon's status shapes.
  let toElement (state: CallersState) : JsonElement =
    use doc = JsonDocument.Parse(toJson state)
    doc.RootElement.Clone()
