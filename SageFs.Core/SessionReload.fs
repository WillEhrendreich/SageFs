namespace SageFs

open System
open System.Text.Json

/// What the worker last said a save did to the running process, as the daemon
/// records it on the session (`SessionInfo.Reload`).
///
/// A save is decided inside the worker: it re-evaluates the file, patches what
/// it can, and only then knows the outcome. The daemon's own file watcher fires
/// before any of that, so what it can honestly say is "the file changed". The
/// outcome used to reach only a browser tab served by the app being reloaded
/// (the overlay) and the worker's own log, so a console app, an agent or an
/// editor never learned that a patch had not landed. The worker already sends
/// every event on its `/__sagefs__/reload` stream; the daemon now keeps the last
/// one, and every surface reads it from the session.
///
/// It lives ahead of `WorkerProtocol` (whose `SessionInfo` carries it) and does
/// not use `DevReload`'s own types: `DevReload.fs` is compiled later and is also
/// embedded in the FSI host. The wire shape it reads is `DevReloadEvent.payloadJson`,
/// and a contract test pins the two together.
[<RequireQualifiedAccess>]
type ReloadCase =
  /// The new code has been seen running.
  | Patched
  /// Applied, and the new code has not been seen running yet. Resolves into
  /// `Patched` or `NeverEntered` for the same save.
  | PatchPending
  /// The bound passed and some re-pointed definitions' new code has not run.
  | NeverEntered
  | Restarted
  /// Processed, and nothing in the running process changed.
  | NoEffect
  /// The change cannot be applied without restarting the app.
  | RestartRequired
  /// The file did not compile; the app keeps serving the last code that did.
  | CompileFailed
  /// A live value was kept instead of reset (with or without a patch alongside).
  | KeptLiveState

/// Everything a client needs to report a save without re-deriving anything.
type ReloadFacts = {
  Case: ReloadCase
  /// How many of the definitions the save changed are now live.
  Patched: int
  /// How many definitions the save put in front of the process.
  Considered: int
  /// What happened AND what to do, in one string (the worker's own wording).
  Message: string
  /// What to do about it; empty when nothing is needed.
  SuggestedAction: string
  /// How a patch reached the process: a detour in the reload agent, or a metadata delta in the worker. `NoPatch` for a
  /// verdict that is not a patch (a restart, a compile failure, nothing changed).
  Mechanism: SageFs.Features.ReloadOutcome.PatchMechanism
  /// The declarations a patch applied in the worker's own process put in front of it, by the compiled names. Empty for any
  /// verdict that is not such a patch.
  Declarations: string list
  /// Whether callers in OTHER files are still on a method this save or an earlier one replaced or removed. The worker's own
  /// ledger, carried on every report, so it is `CallersCurrent` again once the callers' files have landed. A payload that
  /// says nothing about it is `CallersNotReported`, never `CallersCurrent`.
  Callers: SageFs.Features.CallerState.CallersState
}

[<RequireQualifiedAccess>]
type SessionReload =
  /// No save has resolved for this session yet. Its own case: a session nobody
  /// saved to must not be reported as a reload that succeeded.
  | NoReloadYet
  /// A save is being compiled. Not terminal: it always resolves into a `Finished`.
  | Compiling of file: string option
  | Finished of ReloadFacts

/// Why a payload from a worker could not be read as a reload event. A DU, so the
/// daemon can log the reason and a test can assert which one, without matching on
/// prose.
[<RequireQualifiedAccess>]
type ReloadPayloadError =
  | NotJson of detail: string
  | UnknownEventType of eventType: string
  | NoOutcome
  | UnknownOutcome of token: string
  /// The `callers` object is there and is not one this daemon can read.
  | BadCallers of detail: string

module ReloadPayloadError =
  let describe (error: ReloadPayloadError) : string =
    match error with
    | ReloadPayloadError.NotJson detail -> sprintf "not JSON: %s" detail
    | ReloadPayloadError.UnknownEventType eventType -> sprintf "unknown reload event type '%s'" eventType
    | ReloadPayloadError.NoOutcome -> "a finished reload event has no outcome"
    | ReloadPayloadError.UnknownOutcome token -> sprintf "unknown reload outcome '%s'" token
    | ReloadPayloadError.BadCallers detail -> sprintf "the callers state could not be read: %s" detail

module ReloadCase =
  /// The worker's own token for each outcome (`ReloadBroadcast` writes them from
  /// the `ReloadOutcome` case names). One spelling per case.
  let token (case: ReloadCase) : string =
    match case with
    | ReloadCase.Patched -> "Patched"
    | ReloadCase.PatchPending -> "PatchPending"
    | ReloadCase.NeverEntered -> "NeverEntered"
    | ReloadCase.Restarted -> "Restarted"
    | ReloadCase.NoEffect -> "NoEffect"
    | ReloadCase.RestartRequired -> "RestartRequired"
    | ReloadCase.CompileFailed -> "CompileFailed"
    | ReloadCase.KeptLiveState -> "KeptLiveState"

  /// Every case, so a parser and a test can walk them. The token function above
  /// is exhaustive, so a new case cannot be added without a token.
  let all : ReloadCase list =
    [ ReloadCase.Patched; ReloadCase.PatchPending; ReloadCase.NeverEntered; ReloadCase.Restarted; ReloadCase.NoEffect
      ReloadCase.RestartRequired; ReloadCase.CompileFailed; ReloadCase.KeptLiveState ]

  let ofToken (text: string) : Result<ReloadCase, ReloadPayloadError> =
    match all |> List.tryFind (fun case -> String.Equals(token case, text, StringComparison.Ordinal)) with
    | Some case -> Result.Ok case
    | None -> Result.Error (ReloadPayloadError.UnknownOutcome text)

module SessionReload =
  /// What a session says about its last save once its worker has been swapped for a new one.
  ///
  /// A verdict is about the process that produced it, so a swap clears it, with one
  /// exception: `Restarted` reports that the app was restarted, and the swap IS that
  /// restart, so wiping it would leave status saying nothing happened right after the
  /// save that caused it (seen live on a run_app ticker, where the pid changed and
  /// `lastReload` read null). Every other verdict, and an unfinished `Compiling`, goes.
  let afterWorkerSwap (reload: SessionReload) : SessionReload =
    match reload with
    | SessionReload.Finished facts when facts.Case = ReloadCase.Restarted -> reload
    | SessionReload.Finished _ | SessionReload.Compiling _ | SessionReload.NoReloadYet -> SessionReload.NoReloadYet

  /// The worker's SSE `data:` payload for one reload event (`{"type":...}`).
  ///
  /// The `type` cue is the browser overlay's: `compiling`, `pending`, `patched`,
  /// `neverentered`, `restarted`, `noeffect` and `failed` are the events a save
  /// can produce, and `none` is what a poll returns before the first save
  /// resolves. A patch is `pending` until its new code has been seen running,
  /// then `patched` or `neverentered`. Anything else, or a
  /// terminal event without the outcome token, is an Error that says why, so a
  /// worker that changes the shape is noticed rather than silently ignored.
  let ofPayloadJson (json: string) : Result<SessionReload, ReloadPayloadError> =
    try
      use doc = JsonDocument.Parse json
      let root = doc.RootElement
      let text (name: string) : string =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> ""
      let count (name: string) : int =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
        | _ -> 0
      // No `callers` field is a worker that said nothing, which is not a worker that said all is well.
      let callers () : Result<SageFs.Features.CallerState.CallersState, ReloadPayloadError> =
        match root.TryGetProperty "callers" with
        | true, value when value.ValueKind = JsonValueKind.Object ->
          SageFs.Features.CallerState.CallersState.ofElement value |> Result.mapError ReloadPayloadError.BadCallers
        | _ -> Result.Ok SageFs.Features.CallerState.CallersState.CallersNotReported
      let finished () : Result<SessionReload, ReloadPayloadError> =
        match root.TryGetProperty "outcome" with
        | true, value when value.ValueKind = JsonValueKind.String ->
          ReloadCase.ofToken (value.GetString())
          |> Result.bind (fun case ->
            callers ()
            |> Result.map (fun callersState ->
              SessionReload.Finished
                { Case = case
                  Patched = count "patched"
                  Considered = count "considered"
                  Message = text "message"
                  SuggestedAction = text "suggestedAction"
                  Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.ofWireName (text "mechanism")
                  Declarations =
                    match root.TryGetProperty "declarations" with
                    | true, names when names.ValueKind = JsonValueKind.Array ->
                      [ for n in names.EnumerateArray() do
                          if n.ValueKind = JsonValueKind.String then yield n.GetString() ]
                    | _ -> []
                  Callers = callersState }))
        | _ -> Result.Error ReloadPayloadError.NoOutcome
      match text "type" with
      | "none" -> Result.Ok SessionReload.NoReloadYet
      | "compiling" ->
        (match text "file" with
         | "" -> Result.Ok (SessionReload.Compiling None)
         | file -> Result.Ok (SessionReload.Compiling (Some file)))
      | "pending" | "patched" | "neverentered" | "restarted" | "noeffect" | "failed" -> finished ()
      | other -> Result.Error (ReloadPayloadError.UnknownEventType other)
    with :? JsonException as ex -> Result.Error (ReloadPayloadError.NotJson ex.Message)

  /// How the reload appears in every status shape and SSE event. Absent (null)
  /// before the first save resolves, like `lastRestart`, and never a success
  /// that did not happen.
  let toWire (reload: SessionReload) : obj | null =
    match reload with
    | SessionReload.NoReloadYet -> null
    | SessionReload.Compiling file ->
      box {| state = "compiling"; file = (match file with Some f -> f | None -> "") |}
    | SessionReload.Finished facts ->
      box
        {| state = "finished"
           outcome = ReloadCase.token facts.Case
           patched = facts.Patched
           considered = facts.Considered
           message = facts.Message
           suggestedAction = facts.SuggestedAction
           mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.wireName facts.Mechanism
           // `ofPayloadJson` has always READ `declarations` back out of a payload; this never
           // WROTE one, so every declaration a reload actually patched was lost on the way to a
           // client — the sibling `ReplFreshness.toWire` below emits the same field, which is what
           // made the omission visible. A client that asked "what changed" got an empty list, and
           // the round trip was not an identity.
           declarations = facts.Declarations |> List.toArray
           // Always a field, like `replFreshness`: a client never has to read an absence as "fine".
           callers = SageFs.Features.CallerState.CallersState.toElement facts.Callers |}

  /// One line for a person or an agent: what the save did, or that it is still
  /// compiling, or that nothing has been saved yet.
  let describe (reload: SessionReload) : string =
    match reload with
    | SessionReload.NoReloadYet -> "No hot reload yet."
    | SessionReload.Compiling None -> "Hot reload: compiling..."
    | SessionReload.Compiling (Some file) -> sprintf "Hot reload: compiling %s..." (IO.Path.GetFileName file)
    | SessionReload.Finished facts -> facts.Message

  /// The line the text status appends: nothing before the first save resolves.
  let statusLine (reload: SessionReload) : string =
    match reload with
    | SessionReload.NoReloadYet -> ""
    | resolved -> "\n" + describe resolved

/// Whether the REPL and live tests run the same build as the app.
///
/// A save to an app SageFs started with `run_app` can be patched into the worker's own process by a metadata delta. The app then
/// runs the new code, and the FSI host the REPL and live tests run in keeps the build from before it. That gap is not a footnote:
/// a REPL call to a function the save changed runs the OLD body, and nothing in the result says so unless this does. It is a
/// state the session carries, and it clears only when the FSI host is rebuilt or replaced.
[<RequireQualifiedAccess>]
type ReplFreshness =
  /// The REPL and live tests run what the app runs.
  | InSync
  /// The app was patched in place `savesSince` times since the FSI host was built, naming what changed (each once, in the order
  /// it was first saved).
  | BehindApp of savesSince: int * declarations: string list

[<RequireQualifiedAccess>]
module ReplFreshness =

  /// How many declarations the warning names before it says how many more there are.
  let private namedInWarning = 5

  /// Fold what the worker said about a save into the state. Only a metadata delta puts the REPL behind: a detour patches the FSI
  /// host's own copy, and a restart, a failed compile or a save that changed nothing changes nothing the REPL runs. A save is counted
  /// when it is reported pending; its confirmation (or its never-entered) is the same save. A confirmation seen with no pending
  /// before it (a daemon that joined late) still puts the REPL behind, because this state must never be silent.
  let observe (freshness: ReplFreshness) (reload: SessionReload) : ReplFreshness =
    match reload with
    | SessionReload.Finished facts when facts.Mechanism = SageFs.Features.ReloadOutcome.PatchMechanism.MetadataDelta ->
      let merged (known: string list) = List.distinct (known @ facts.Declarations)
      match facts.Case, freshness with
      | ReloadCase.PatchPending, ReplFreshness.InSync -> ReplFreshness.BehindApp (1, merged [])
      | ReloadCase.PatchPending, ReplFreshness.BehindApp (saves, known) -> ReplFreshness.BehindApp (saves + 1, merged known)
      | (ReloadCase.Patched | ReloadCase.NeverEntered | ReloadCase.KeptLiveState), ReplFreshness.InSync ->
        ReplFreshness.BehindApp (1, merged [])
      | (ReloadCase.Patched | ReloadCase.NeverEntered | ReloadCase.KeptLiveState), ReplFreshness.BehindApp (saves, known) ->
        ReplFreshness.BehindApp (saves, merged known)
      | (ReloadCase.Restarted | ReloadCase.NoEffect | ReloadCase.RestartRequired | ReloadCase.CompileFailed), _ -> freshness
    | SessionReload.Finished _
    | SessionReload.Compiling _
    | SessionReload.NoReloadYet -> freshness

  /// A replaced worker is built from the current build, REPL included.
  let afterWorkerSwap (_: ReplFreshness) : ReplFreshness = ReplFreshness.InSync

  let private plural (n: int) (word: string) : string =
    match n with
    | 1 -> sprintf "1 %s" word
    | _ -> sprintf "%d %ss" n word

  /// What the state says, in a sentence a reader can act on without knowing the history.
  let describe (freshness: ReplFreshness) : string =
    match freshness with
    | ReplFreshness.InSync -> "The REPL and live tests run the same build as the app."
    | ReplFreshness.BehindApp (saves, declarations) ->
      let named =
        match declarations with
        | [] -> ""
        | names when names.Length <= namedInWarning -> sprintf " (%s)" (String.concat ", " names)
        | names -> sprintf " (%s, and %d more)" (names |> List.truncate namedInWarning |> String.concat ", ") (names.Length - namedInWarning)
      sprintf
        "The REPL is BEHIND the app: the app was patched in place on %s%s, and the REPL and live tests still run the build from before it, so a call to what changed runs the OLD code. hard_reset_fsi_session with rebuild=true brings them level, and it replaces the worker, so the running app stops with it and its in-memory state is lost (run_app starts it again)."
        (plural saves "save")
        named

  /// The warning a tool result carries, or nothing when the REPL is level.
  let banner (freshness: ReplFreshness) : string =
    match freshness with
    | ReplFreshness.InSync -> ""
    | ReplFreshness.BehindApp _ -> sprintf "WARNING: %s" (describe freshness)

  /// A tool's result, with the warning after it when the REPL is behind. The result stays first and whole.
  let annotate (freshness: ReplFreshness) (text: string) : string =
    match banner freshness with
    | "" -> text
    | warning -> sprintf "%s\n\n%s" text warning

  /// How the state appears in every status shape: always a field, never an absence a client has to read as "fine".
  let toWire (freshness: ReplFreshness) : obj =
    match freshness with
    | ReplFreshness.InSync -> box {| state = "InSync" |}
    | ReplFreshness.BehindApp (saves, declarations) ->
      box
        {| state = "BehindApp"
           savesSince = saves
           declarations = List.toArray declarations
           message = describe freshness |}
