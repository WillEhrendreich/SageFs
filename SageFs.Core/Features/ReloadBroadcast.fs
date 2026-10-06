/// The ONE place a `ReloadOutcome` becomes something a browser sees.
///
/// `ReloadOutcome` decides what a save did to the running process;
/// `DevReloadEvent` is what a page is told. Keeping the translation in a single
/// module is not tidiness — it is the fix. The bug this subsystem exists to
/// prevent was two surfaces each deciding "should I refresh?" from numbers of
/// their own: the detour middleware counted detoured methods, the save pipeline
/// counted something else, and a save that re-pointed a handful of incidental
/// BCL-signature helpers — and none of the handlers the user had edited — still
/// told the browser to refresh.
///
/// So: nothing constructs a terminal `DevReloadEvent` except `eventOf`, and
/// `eventOf` derives "refresh?" from `ReloadOutcome.shouldRefreshBrowser` alone.
module SageFs.Features.ReloadBroadcast

open System
open SageFs
open SageFs.Features.ReloadOutcome

/// The outcome type and its companion module share a name, and this module
/// lives in the same namespace as the module that holds both — so the functions
/// get an unambiguous abbreviation rather than a resolution coin-flip.
module Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

// Planner refusal → the shape of the change the user made is NOT translated
// here. `ReloadPlanning.ReloadChange.restartReason` owns it, and callers use
// that directly: two mappings would be two vocabularies for one refusal, which
// is the class of bug this whole subsystem exists to stop.

/// The count a restart-shaped outcome reports: one per reason, because each
/// reason is one changed definition that did not reach the running process.
/// Zero reasons still reads honestly ("0 of 0"), and never claims a patch.
let private consideredFor (reasons: RestartReason list) = List.length reasons

/// The case name a client branches on, taken straight from the DU so it cannot
/// drift from the type. A stable token, unlike the prose beside it.
let private caseName (outcome: ReloadOutcome) =
  match outcome with
  | ReloadOutcome.AssetsRebuilt _ -> "AssetsRebuilt"
  | ReloadOutcome.Patched _ -> "Patched"
  | ReloadOutcome.Restarted _ -> "Restarted"
  | ReloadOutcome.NoEffect _ -> "NoEffect"
  | ReloadOutcome.RestartRequired _ -> "RestartRequired"
  | ReloadOutcome.CompileFailed _ -> "CompileFailed"
  | ReloadOutcome.KeptLiveState _ -> "KeptLiveState"
  | ReloadOutcome.PatchPending _ -> "PatchPending"
  | ReloadOutcome.NeverEntered _ -> "NeverEntered"
  // A delta's stages are named as a detour's are, so a client that branches on the stage keeps working; what is new is
  // the report's `mechanism`.
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Pending _) -> "PatchPending"
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Patched _) -> "Patched"
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.NeverEntered _) -> "NeverEntered"

let private refusalCaseName (reason: RestartReason) =
  match reason with
  | RestartReason.StartupComputedValue _ -> "StartupComputedValue"
  | RestartReason.MutableModuleState _ -> "MutableModuleState"
  | RestartReason.MutableStateTypeChanged _ -> "MutableStateTypeChanged"
  | RestartReason.SignatureChanged _ -> "SignatureChanged"
  | RestartReason.TypeShapeChanged _ -> "TypeShapeChanged"
  | RestartReason.NewDeclaration _ -> "NewDeclaration"
  | RestartReason.NotYetSupported _ -> "NotYetSupported"
  | RestartReason.PatchIneffective _ -> "PatchIneffective"
  | RestartReason.ValueCopiedByApp _ -> "ValueCopiedByApp"
  | RestartReason.ValueUntraceable _ -> "ValueUntraceable"
  | RestartReason.UnverifiedCopy _ -> "UnverifiedCopy"
  | RestartReason.ClosureShapeChanged _ -> "ClosureShapeChanged"
  | RestartReason.InstanceLayoutChanged _ -> "InstanceLayoutChanged"
  | RestartReason.GenericInstantiationsUnknown _ -> "GenericInstantiationsUnknown"
  // The emitter's own cause is the token: a client reads `VirtualSignatureChanged`, not a wrapper.
  | RestartReason.RudeEdit cause -> SageFs.Features.MetadataDelta.RudeCause.caseName cause
  | RestartReason.MetadataDeltaUnavailable _ -> "MetadataDeltaUnavailable"
let private refusalOf (reason: RestartReason) : DevReload.ReloadRefusal =
  { Case = refusalCaseName reason
    Message = RestartReason.describe reason
    SuggestedAction = RestartReason.remedy reason }

let private reasonsIn (outcome: ReloadOutcome) =
  match outcome with
  | ReloadOutcome.NoEffect(_, reasons)
  | ReloadOutcome.Restarted reasons
  | ReloadOutcome.RestartRequired reasons -> reasons
  | ReloadOutcome.Patched _
  | ReloadOutcome.PatchPending _
  | ReloadOutcome.NeverEntered _
  | ReloadOutcome.KeptLiveState _
  | ReloadOutcome.ByMetadataDelta _
  | ReloadOutcome.AssetsRebuilt _
  | ReloadOutcome.CompileFailed _ -> []

let private keptReport (k: KeptValue) : DevReload.KeptStateReport =
  { Binding = k.Binding; KeptValue = k.KeptValue; NewInitializer = k.NewInitializer }

let private keptIn (outcome: ReloadOutcome) : DevReload.KeptStateReport list =
  match outcome with
  | ReloadOutcome.KeptLiveState(_, _, first, rest) -> first :: rest |> List.map keptReport
  | ReloadOutcome.PatchPending(_, _, kept)
  | ReloadOutcome.NeverEntered(_, _, _, _, kept) -> kept |> List.map keptReport
  | ReloadOutcome.Patched _
  | ReloadOutcome.ByMetadataDelta _
  | ReloadOutcome.NoEffect _
  | ReloadOutcome.Restarted _
  | ReloadOutcome.RestartRequired _
  | ReloadOutcome.AssetsRebuilt _
  | ReloadOutcome.CompileFailed _ -> []

/// Where callers in other files stand right now, read when a report is built. A worker sets it once, to a function over its
/// own ledger (`CallerLedger`), because one worker is one process with one set of old methods; the default says nothing is
/// pending, which is what a process that has re-signed nothing says too. A report is built from whatever this holds at that
/// moment, so every event of a save carries the state the save left, the confirmation after the patch included.
let standingCallers : (unit -> CallerState.CallersState) ref = ref (fun () -> CallerState.CallersState.CallersCurrent)

/// A report's words with the callers' sentence after them, when there is one. A client that shows only `message` shows it.
let private messageWithCallers (callers: CallerState.CallersState) (message: string) : string =
  match CallerState.CallersState.describe callers, CallerState.CallersState.remedy callers with
  | "", _ -> message
  | news, "" -> sprintf "%s\nCallers in other files: %s" message news
  | news, remedy -> sprintf "%s\nCallers in other files: %s\n→ %s" message news remedy

/// The action a report suggests. When the save changed the process and callers are on an old method, the callers' remedy
/// comes first: the outcome's own ("exercise the changed code") is a way to confirm a patch, and a caller that still runs
/// the old method is the thing to do next. When nothing changed (a refusal, a compile failure) the outcome's own stays,
/// because there the save itself has to be fixed first; the callers' sentence is in the message either way.
let private actionWithCallers (callers: CallerState.CallersState) (changedTheProcess: bool) (outcomeRemedy: string) : string =
  match changedTheProcess, CallerState.CallersState.remedy callers, outcomeRemedy with
  | true, callersRemedy, _ when callersRemedy <> "" -> callersRemedy
  | _, callersRemedy, "" -> callersRemedy
  | _, _, own -> own

/// The whole truth about a save, in the shape every client reads. Built here
/// and nowhere else, so the browser overlay, the Neovim plugin and an editor
/// extension necessarily say the same thing about the same save.
let reportWith (callers: CallerState.CallersState) (outcome: ReloadOutcome) : DevReload.ReloadReport =
  let patched, considered =
    match outcome with
    | ReloadOutcome.Patched(patched, considered) -> patched, considered
    | ReloadOutcome.NoEffect(considered, _) -> 0, considered
    | ReloadOutcome.Restarted reasons
    | ReloadOutcome.RestartRequired reasons -> 0, consideredFor reasons
    | ReloadOutcome.AssetsRebuilt _ -> 0, 0
    | ReloadOutcome.CompileFailed _ -> 0, 0
    | ReloadOutcome.KeptLiveState(patched, considered, _, _) -> patched, considered
    // Applied is not live: a pending patch has had nothing confirmed yet.
    | ReloadOutcome.PatchPending(_, considered, _) -> 0, considered
    | ReloadOutcome.NeverEntered(_, _, entered, considered, _) -> entered, considered
    | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Pending(_, considered, _)) -> 0, considered
    | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Patched(patched, considered)) -> patched, considered
    | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.NeverEntered(_, _, entered, considered)) -> entered, considered
  { Outcome = caseName outcome
    Mechanism = PatchMechanism.wireName (Outcome.mechanismOf outcome)
    Patched = patched
    Considered = considered
    Message = messageWithCallers callers (Outcome.describeForUser outcome)
    SuggestedAction = actionWithCallers callers (Outcome.processChanged outcome) (Outcome.remedy outcome |> Option.defaultValue "")
    Reasons = reasonsIn outcome |> List.map refusalOf
    Kept = keptIn outcome
    Declarations =
      match outcome with
      | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Pending(_, _, declarations)) -> declarations
      | _ -> []
    Callers = CallerState.CallersState.toJson callers }

/// `reportWith`, for the callers state this process holds now.
let reportOf (outcome: ReloadOutcome) : DevReload.ReloadReport = reportWith (standingCallers.Value ()) outcome

/// The single translation. `refreshes` on the result always equals
/// `ReloadOutcome.shouldRefreshBrowser` on the input — that equality is pinned
/// by a test, and it is what stops the two from drifting apart again.
let eventWith (callers: CallerState.CallersState) (outcome: ReloadOutcome) : DevReload.DevReloadEvent =
  let report = reportWith callers outcome
  match outcome with
  | ReloadOutcome.AssetsRebuilt(sourceFile, assetCount, contentHash) ->
    DevReload.AssetsRebuilt(report, sourceFile, assetCount, contentHash)
  | ReloadOutcome.PatchPending _
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Pending _) -> DevReload.Applied report
  | ReloadOutcome.NeverEntered _
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.NeverEntered _) -> DevReload.NeverEntered report
  | ReloadOutcome.Patched _
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Patched _) -> DevReload.Patched report
  | ReloadOutcome.Restarted _ -> DevReload.Restarted report
  | ReloadOutcome.NoEffect _
  | ReloadOutcome.RestartRequired _ -> DevReload.NotApplied report
  // The structured diagnostics that make the browser overlay clickable are
  // attached by the caller that HAS them (`broadcastEvalFailure` in the worker);
  // an outcome carries only the summary.
  | ReloadOutcome.CompileFailed summary -> DevReload.CompilationFailed(summary, report, [])
  // A kept value alone changes nothing the page would fetch, so it closes the
  // overlay without a refresh. With a patch alongside it, the patch refreshes.
  | ReloadOutcome.KeptLiveState(patched, _, _, _) when patched > 0 -> DevReload.Patched report
  | ReloadOutcome.KeptLiveState _ -> DevReload.NotApplied report

/// `eventWith`, for the callers state this process holds now.
let eventOf (outcome: ReloadOutcome) : DevReload.DevReloadEvent = eventWith (standingCallers.Value ()) outcome

/// Deliver an event through the broadcast API that matches its case. Every
/// terminal event a save produces goes through here, so `broadcastPatched`'s
/// "a patch of nothing is not a refresh" guard is never bypassed.
let broadcastEvent (evt: DevReload.DevReloadEvent) =
  match evt with
  | DevReload.Compiling fileName -> DevReload.broadcastCompiling fileName
  | DevReload.Applied report -> DevReload.broadcastApplied report
  | DevReload.AssetsRebuilt(report, sourceFile, assetCount, contentHash) ->
    DevReload.broadcastAssetsRebuilt report sourceFile assetCount contentHash
  | DevReload.Patched report -> DevReload.broadcastPatched report
  | DevReload.NeverEntered report -> DevReload.broadcastNeverEntered report
  | DevReload.Restarted report -> DevReload.broadcastRestarted report
  | DevReload.NotApplied report -> DevReload.broadcastNotApplied report
  | DevReload.CompilationFailed(summary, report, diagnostics) ->
    DevReload.broadcastCompilationFailed summary report diagnostics

/// Tell every connected client what this save did. The only terminal broadcast
/// a save pipeline should make.
let broadcastOutcome (outcome: ReloadOutcome) = broadcastEvent (eventOf outcome)

/// A compile failure with the structured diagnostics the browser overlay needs
/// to render clickable file:line errors. Everything else about the outcome —
/// the wording and the remedy — still comes from `ReloadOutcome`.
let broadcastCompileFailure (summary: string) (diagnostics: DevReload.DevReloadDiagnostic list) =
  let outcome = SageFs.Features.ReloadOutcome.ReloadOutcome.CompileFailed summary
  DevReload.broadcastCompilationFailed summary (reportOf outcome) diagnostics

/// A terminal "nothing reached the running process" that no `ReloadOutcome`
/// case describes: the save never got as far as an outcome. Still carries a
/// case name and a remedy, because a client that cannot branch on it and a
/// user who cannot act on it are both stuck.
let private notApplied (case: string) (message: string) (remedy: string) : DevReload.DevReloadEvent =
  let callers = standingCallers.Value ()
  DevReload.NotApplied
    { Outcome = case
      Mechanism = ""
      Patched = 0
      Considered = 0
      Message = messageWithCallers callers (match remedy with | "" -> message | r -> sprintf "%s\n→ %s" message r)
      SuggestedAction = actionWithCallers callers false remedy
      Reasons = []
      Kept = []
      Declarations = []
      Callers = CallerState.CallersState.toJson callers }

/// A save whose declarations are byte-identical to what the running build
/// already has. Not a reload and not a failure: there is nothing to fetch and
/// nothing for the user to do, so no remedy is invented. Refreshing here would
/// be the byte-identical refresh this whole subsystem exists to prevent.
///
/// "Already current" is only said when no caller in another file is still on an old method: a save of a caller's file
/// with no edit moves nothing, and the report has to say so beside the callers' state, not contradict it.
let unchanged (fileName: string) : DevReload.DevReloadEvent =
  let message =
    match standingCallers.Value () with
    | CallerState.CallersState.CallersCurrent -> sprintf "%s saved — no declaration changed, so the running app is already current." fileName
    | CallerState.CallersState.CallersPending _
    | CallerState.CallersState.CallersNotChecked _
    | CallerState.CallersState.CallersNotReported -> sprintf "%s saved — no declaration changed, so nothing moved." fileName
  notApplied "Unchanged" message ""

let assetsUnchanged (fileName: string) : DevReload.DevReloadEvent =
  notApplied "Unchanged" (sprintf "%s built successfully. Served browser assets are unchanged." fileName) ""

/// A save that never reached the compiler because the previous save's compile
/// still holds it.
///
/// Chesterton's fence: this event is the difference between a bounded wait and
/// silence. One wedged eval used to hold the compilation semaphore forever,
/// which disabled hot reload for every other file for the rest of the session —
/// no log, no SSE, no signal of any kind. An agent debugging it concluded the
/// file watcher was dead. Whatever else happens, the user must be told.
let compilerBusy (fileName: string) (waited: TimeSpan) : DevReload.DevReloadEvent =
  notApplied
    "CompilerBusy"
    (sprintf
      "%s was not compiled: the previous hot-reload compile has held the compiler for over %.0fs."
      fileName
      waited.TotalSeconds)
    "Save again — the stuck compile is bounded and releases on its own, so the next save goes through."

/// The file watcher's OS-level buffer overflowed under `directory`: events
/// were lost and SageFs cannot know which files actually changed. It resets
/// the session as a precaution instead of guessing — the fix this event
/// exists for is that the reset used to happen in total silence, so a save
/// that got lost to the overflow never produced any outcome at all, hot
/// reload's own "nothing silent" rule broken by the one path meant to keep
/// FSI honest when it loses track of what happened.
let watcherOverflow (directory: string) : DevReload.DevReloadEvent =
  notApplied
    "WatcherOverflow"
    (sprintf
      "The file-watch buffer overflowed under %s, so SageFs can't tell exactly what changed there. It reset the session to bring it back in sync with disk."
      directory)
    "If you were mid-save, save that file again to make sure the change was picked up."

/// A save whose own re-evaluation ran past its budget and was abandoned. The
/// budget is what guarantees the compiler is handed back, so the NEXT save is
/// processed instead of being queued behind this one forever.
let evalTimedOut (fileName: string) (budget: TimeSpan) : DevReload.DevReloadEvent =
  notApplied
    "EvalTimedOut"
    (sprintf
      "%s did not finish compiling within %.0fs, so nothing was applied and the app is still serving the last good code."
      fileName
      budget.TotalSeconds)
    "Save again, or hard-reset the session if the file keeps timing out."
