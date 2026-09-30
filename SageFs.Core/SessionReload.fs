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
  | Patched
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

module ReloadPayloadError =
  let describe (error: ReloadPayloadError) : string =
    match error with
    | ReloadPayloadError.NotJson detail -> sprintf "not JSON: %s" detail
    | ReloadPayloadError.UnknownEventType eventType -> sprintf "unknown reload event type '%s'" eventType
    | ReloadPayloadError.NoOutcome -> "a finished reload event has no outcome"
    | ReloadPayloadError.UnknownOutcome token -> sprintf "unknown reload outcome '%s'" token

module ReloadCase =
  /// The worker's own token for each outcome (`ReloadBroadcast` writes them from
  /// the `ReloadOutcome` case names). One spelling per case.
  let token (case: ReloadCase) : string =
    match case with
    | ReloadCase.Patched -> "Patched"
    | ReloadCase.Restarted -> "Restarted"
    | ReloadCase.NoEffect -> "NoEffect"
    | ReloadCase.RestartRequired -> "RestartRequired"
    | ReloadCase.CompileFailed -> "CompileFailed"
    | ReloadCase.KeptLiveState -> "KeptLiveState"

  /// Every case, so a parser and a test can walk them. The token function above
  /// is exhaustive, so a new case cannot be added without a token.
  let all : ReloadCase list =
    [ ReloadCase.Patched; ReloadCase.Restarted; ReloadCase.NoEffect
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
  /// The `type` cue is the browser overlay's: `compiling`, `reload`, `restarted`,
  /// `noeffect` and `failed` are the five events a save can produce, and `none`
  /// is what a poll returns before the first save resolves. Anything else, or a
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
      let finished () : Result<SessionReload, ReloadPayloadError> =
        match root.TryGetProperty "outcome" with
        | true, value when value.ValueKind = JsonValueKind.String ->
          ReloadCase.ofToken (value.GetString())
          |> Result.map (fun case ->
            SessionReload.Finished
              { Case = case
                Patched = count "patched"
                Considered = count "considered"
                Message = text "message"
                SuggestedAction = text "suggestedAction" })
        | _ -> Result.Error ReloadPayloadError.NoOutcome
      match text "type" with
      | "none" -> Result.Ok SessionReload.NoReloadYet
      | "compiling" ->
        (match text "file" with
         | "" -> Result.Ok (SessionReload.Compiling None)
         | file -> Result.Ok (SessionReload.Compiling (Some file)))
      | "reload" | "restarted" | "noeffect" | "failed" -> finished ()
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
           suggestedAction = facts.SuggestedAction |}

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
