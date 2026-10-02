/// Pure decisions behind what a window says about a hot reload save.
///
/// WHY — measured with the tour harness on a running Falco app: after a save the window showed
/// nothing about the patch. The daemon already says what a save did, in the `ReloadReported` state
/// event and as `lastReload` and `replFreshness` on every session (docs/sse-events.md,
/// docs/hot-reload.md): a patch is `PatchPending` (applied, the new body has not been seen running),
/// then `Patched` or `NeverEntered`; a save can also restart the app (`Restarted`, `RestartRequired`
/// with its cause), fail to compile, do nothing, or keep a live value. `mechanism` says whether a patch
/// reached the process as a `metadata-delta` or a `detour`, and `replFreshness` says whether the REPL
/// now holds the build from before the patch.
///
/// Two surfaces, each total over the report: a status bar item that always shows the latest verdict,
/// and ONE non-modal message per save, on the verdict that ends the save. `PatchPending` and a save
/// still compiling are quiet, because `Patched` or `NeverEntered` follows them within the daemon's
/// bound.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/ReloadReportContractTests.fsx).
module SageFs.Vscode.ReloadReportPure

/// What a finished save did, by the daemon's own outcome tokens (`ReloadCase.token`).
[<RequireQualifiedAccess>]
type Outcome =
  | Patched
  | PatchPending
  | NeverEntered
  | Restarted
  | NoEffect
  | RestartRequired
  | CompileFailed
  | KeptLiveState
  /// A token this client does not know. Named, never judged.
  | Unrecognised of token: string

module Outcome =
  let ofWire (token: string) : Outcome =
    match token with
    | "Patched" -> Outcome.Patched
    | "PatchPending" -> Outcome.PatchPending
    | "NeverEntered" -> Outcome.NeverEntered
    | "Restarted" -> Outcome.Restarted
    | "NoEffect" -> Outcome.NoEffect
    | "RestartRequired" -> Outcome.RestartRequired
    | "CompileFailed" -> Outcome.CompileFailed
    | "KeptLiveState" -> Outcome.KeptLiveState
    | other -> Outcome.Unrecognised other

/// How a patch reached the running process. Read from the `mechanism` field, never from the words of
/// the message.
[<RequireQualifiedAccess>]
type Mechanism =
  /// A delta applied to the assembly an app started with `run_app` runs from.
  | MetadataDelta
  /// A method re-pointed in the reload agent's process.
  | Detour
  /// The verdict is not a patch (a restart, a compile failure, nothing changed).
  | NotAPatch
  | Unrecognised of name: string

module Mechanism =
  let ofWire (name: string) : Mechanism =
    match name with
    | "metadata-delta" -> Mechanism.MetadataDelta
    | "detour" -> Mechanism.Detour
    | "" -> Mechanism.NotAPatch
    | other -> Mechanism.Unrecognised other

  /// The words a person reads, and `None`-free: a verdict that is not a patch has none to say.
  let describe (mechanism: Mechanism) : string =
    match mechanism with
    | Mechanism.MetadataDelta -> "metadata delta"
    | Mechanism.Detour -> "detour"
    | Mechanism.NotAPatch -> ""
    | Mechanism.Unrecognised name -> name

/// Whether the REPL holds the build the running app was patched past.
[<RequireQualifiedAccess>]
type Freshness =
  | InSync
  /// `savesSince` patches since the REPL's build, touching `declarations`.
  | BehindApp of savesSince: int * declarations: string list * message: string
  /// An older daemon sends none.
  | NotReported

module Freshness =
  let ofWire (state: string) (savesSince: int) (declarations: string list) (message: string) : Freshness =
    match state with
    | "InSync" -> Freshness.InSync
    | "BehindApp" -> Freshness.BehindApp(savesSince, declarations, message)
    | _ -> Freshness.NotReported

/// The fields a report carries on the wire, as strings and ints, so the Fable edge fills one record and
/// everything past it is a DU.
type ReportWire = {
  State: string
  File: string
  Outcome: string
  Patched: int
  Considered: int
  Message: string
  SuggestedAction: string
  Mechanism: string
}

type Finished = {
  Outcome: Outcome
  Patched: int
  Considered: int
  Message: string
  SuggestedAction: string
  Mechanism: Mechanism
}

[<RequireQualifiedAccess>]
type Report =
  /// No save has resolved for the session yet.
  | NoReloadYet
  /// A save is being compiled. Not terminal.
  | Compiling of file: string
  | Finished of Finished

let reportOfWire (wire: ReportWire) : Report =
  match wire.State with
  | "compiling" -> Report.Compiling wire.File
  | "finished" ->
    Report.Finished
      { Outcome = Outcome.ofWire wire.Outcome
        Patched = wire.Patched
        Considered = wire.Considered
        Message = wire.Message
        SuggestedAction = wire.SuggestedAction
        Mechanism = Mechanism.ofWire wire.Mechanism }
  | _ -> Report.NoReloadYet

[<RequireQualifiedAccess>]
type StatusTone =
  | Plain
  | Warning
  | Failing

type StatusView = {
  /// Carries a codicon token for the icon slot.
  Text: string
  /// Plain words only: VS Code prints a codicon token literally in a tooltip.
  Tooltip: string
  Tone: StatusTone
}

[<RequireQualifiedAccess>]
type StatusItem =
  | Hidden
  | Shown of StatusView

let private fileName (path: string) =
  path.Split([| '/'; '\\' |], System.StringSplitOptions.RemoveEmptyEntries)
  |> Array.tryLast
  |> Option.defaultValue path

/// The line about the REPL when a patch left it behind. It says what is true, what fixes it and what
/// that costs, because a hard reset that stops the running app is not something to do by accident.
let private behindLine (freshness: Freshness) : string =
  match freshness with
  | Freshness.BehindApp(saves, _, message) ->
    let saveWord = match saves with | 1 -> "save" | _ -> "saves"
    sprintf
      "The REPL is behind the app: %d %s since its build. %s Hard Reset (Rebuild) brings it level, and stops the running app."
      saves
      saveWord
      message
  | Freshness.InSync
  | Freshness.NotReported -> ""

let private joinLines (lines: string list) : string =
  lines |> List.filter (fun l -> not (System.String.IsNullOrWhiteSpace l)) |> String.concat "\n"

let private mechanismLine (mechanism: Mechanism) : string =
  match Mechanism.describe mechanism with
  | "" -> ""
  | words -> sprintf "Mechanism: %s." words

let private counts (f: Finished) = sprintf "%d/%d" f.Patched f.Considered

/// The status bar item for the latest verdict. Hidden before the first save.
let statusItem (report: Report) (freshness: Freshness) : StatusItem =
  match report with
  | Report.NoReloadYet -> StatusItem.Hidden
  | Report.Compiling file ->
    StatusItem.Shown
      { Text = sprintf "$(sync~spin) Hot reload: compiling %s" (fileName file)
        Tooltip = sprintf "Hot reload: compiling %s." (fileName file)
        Tone = StatusTone.Plain }
  | Report.Finished f ->
    let text, tone =
      match f.Outcome with
      | Outcome.PatchPending -> sprintf "$(sync~spin) Hot reload: applied %s, not run yet" (counts f), StatusTone.Plain
      | Outcome.Patched -> sprintf "$(check) Hot reload: patched %s" (counts f), StatusTone.Plain
      | Outcome.NeverEntered -> sprintf "$(warning) Hot reload: patched %s, never ran" (counts f), StatusTone.Warning
      | Outcome.Restarted -> "$(debug-restart) Hot reload: restarted", StatusTone.Plain
      | Outcome.NoEffect -> "$(circle-slash) Hot reload: no effect", StatusTone.Plain
      | Outcome.RestartRequired -> "$(error) Hot reload: restart required", StatusTone.Failing
      | Outcome.CompileFailed -> "$(error) Hot reload: compile failed", StatusTone.Failing
      | Outcome.KeptLiveState -> "$(check) Hot reload: kept live state", StatusTone.Plain
      | Outcome.Unrecognised token -> sprintf "$(question) Hot reload: %s" token, StatusTone.Plain
    StatusItem.Shown
      { Text = text
        Tooltip =
          joinLines
            [ sprintf "Hot reload: %s" f.Message
              (match System.String.IsNullOrWhiteSpace f.SuggestedAction with
               | true -> ""
               | false -> sprintf "Next: %s" f.SuggestedAction)
              mechanismLine f.Mechanism
              behindLine freshness ]
        Tone = tone }

[<RequireQualifiedAccess>]
type Severity =
  | Info
  | Warning
  | Error

/// What a message offers. Captions are verbs; the remedy sentence is in the body.
[<RequireQualifiedAccess>]
type NoticeAction =
  | ShowOutput
  | HardResetRebuild

module NoticeAction =
  let caption (action: NoticeAction) : string =
    match action with
    | NoticeAction.ShowOutput -> "Show Output"
    | NoticeAction.HardResetRebuild -> "Hard Reset (Rebuild)"

type NoticeBody = {
  Severity: Severity
  Text: string
  Actions: NoticeAction list
}

[<RequireQualifiedAccess>]
type Notice =
  /// Nothing to say yet: the verdict is not the end of the save.
  | Quiet
  | Show of NoticeBody

/// The one message for a save, on the verdict that ends it. A `PatchPending` and a save still compiling
/// are quiet: `Patched` or `NeverEntered` follows within the daemon's bound.
let noticeFor (report: Report) (freshness: Freshness) : Notice =
  match report with
  | Report.NoReloadYet
  | Report.Compiling _ -> Notice.Quiet
  | Report.Finished f ->
    let behind = behindLine freshness
    let actionsWhenBehind =
      match freshness with
      | Freshness.BehindApp _ -> [ NoticeAction.HardResetRebuild ]
      | Freshness.InSync
      | Freshness.NotReported -> []
    let body severity (lines: string list) (actions: NoticeAction list) =
      Notice.Show { Severity = severity; Text = joinLines lines; Actions = actions }
    let remedy = if System.String.IsNullOrWhiteSpace f.SuggestedAction then "" else f.SuggestedAction
    match f.Outcome with
    | Outcome.PatchPending -> Notice.Quiet
    | Outcome.Patched ->
      let how =
        match Mechanism.describe f.Mechanism with
        | "" -> ""
        | words -> sprintf " (%s)" words
      body
        Severity.Info
        [ sprintf "Hot reload: patched %d of %d changed definition(s)%s. The new code ran." f.Patched f.Considered how
          behind ]
        actionsWhenBehind
    | Outcome.NeverEntered ->
      body Severity.Warning [ sprintf "Hot reload: %s" f.Message; remedy; behind ] ([ NoticeAction.ShowOutput ] @ actionsWhenBehind)
    | Outcome.Restarted ->
      // The daemon's message already says it restarted ("Restarted the app: ..."), so it is not said twice.
      let said =
        match System.String.IsNullOrWhiteSpace f.Message with
        | true -> "the app was restarted."
        | false -> f.Message
      body Severity.Info [ sprintf "Hot reload: %s" said; remedy ] [ NoticeAction.ShowOutput ]
    | Outcome.NoEffect -> body Severity.Info [ sprintf "Hot reload: %s" f.Message; remedy ] []
    | Outcome.RestartRequired ->
      body Severity.Warning [ sprintf "Hot reload: %s" f.Message; remedy ] [ NoticeAction.ShowOutput ]
    | Outcome.CompileFailed ->
      body
        Severity.Error
        [ sprintf "Hot reload: compile failed, and the app keeps serving the last code that compiled. %s" f.Message; remedy ]
        [ NoticeAction.ShowOutput ]
    | Outcome.KeptLiveState -> body Severity.Info [ sprintf "Hot reload: %s" f.Message; remedy ] []
    | Outcome.Unrecognised token ->
      body Severity.Info [ sprintf "Hot reload: the daemon reported %s. %s" token f.Message ] [ NoticeAction.ShowOutput ]
