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
  | ReloadOutcome.Patched _ -> "Patched"
  | ReloadOutcome.Restarted _ -> "Restarted"
  | ReloadOutcome.NoEffect _ -> "NoEffect"
  | ReloadOutcome.RestartRequired _ -> "RestartRequired"
  | ReloadOutcome.CompileFailed _ -> "CompileFailed"
  | ReloadOutcome.KeptLiveState _ -> "KeptLiveState"

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
  | ReloadOutcome.KeptLiveState _
  | ReloadOutcome.CompileFailed _ -> []

let private keptIn (outcome: ReloadOutcome) : DevReload.KeptStateReport list =
  match outcome with
  | ReloadOutcome.KeptLiveState(_, _, first, rest) ->
    first :: rest
    |> List.map (fun k -> ({ Binding = k.Binding; KeptValue = k.KeptValue; NewInitializer = k.NewInitializer } : DevReload.KeptStateReport))
  | ReloadOutcome.Patched _
  | ReloadOutcome.NoEffect _
  | ReloadOutcome.Restarted _
  | ReloadOutcome.RestartRequired _
  | ReloadOutcome.CompileFailed _ -> []

/// The whole truth about a save, in the shape every client reads. Built here
/// and nowhere else, so the browser overlay, the Neovim plugin and an editor
/// extension necessarily say the same thing about the same save.
let reportOf (outcome: ReloadOutcome) : DevReload.ReloadReport =
  let patched, considered =
    match outcome with
    | ReloadOutcome.Patched(patched, considered) -> patched, considered
    | ReloadOutcome.NoEffect(considered, _) -> 0, considered
    | ReloadOutcome.Restarted reasons
    | ReloadOutcome.RestartRequired reasons -> 0, consideredFor reasons
    | ReloadOutcome.CompileFailed _ -> 0, 0
    | ReloadOutcome.KeptLiveState(patched, considered, _, _) -> patched, considered
  { Outcome = caseName outcome
    Patched = patched
    Considered = considered
    Message = Outcome.describeForUser outcome
    SuggestedAction = Outcome.remedy outcome |> Option.defaultValue ""
    Reasons = reasonsIn outcome |> List.map refusalOf
    Kept = keptIn outcome }

/// The single translation. `refreshes` on the result always equals
/// `ReloadOutcome.shouldRefreshBrowser` on the input — that equality is pinned
/// by a test, and it is what stops the two from drifting apart again.
let eventOf (outcome: ReloadOutcome) : DevReload.DevReloadEvent =
  let report = reportOf outcome
  match outcome with
  | ReloadOutcome.Patched _ -> DevReload.Patched report
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

/// Deliver an event through the broadcast API that matches its case. Every
/// terminal event a save produces goes through here, so `broadcastPatched`'s
/// "a patch of nothing is not a refresh" guard is never bypassed.
let broadcastEvent (evt: DevReload.DevReloadEvent) =
  match evt with
  | DevReload.Compiling fileName -> DevReload.broadcastCompiling fileName
  | DevReload.Patched report -> DevReload.broadcastPatched report
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
  DevReload.NotApplied
    { Outcome = case
      Patched = 0
      Considered = 0
      Message = (match remedy with | "" -> message | r -> sprintf "%s\n→ %s" message r)
      SuggestedAction = remedy
      Reasons = []
      Kept = [] }

/// A save whose declarations are byte-identical to what the running build
/// already has. Not a reload and not a failure: there is nothing to fetch and
/// nothing for the user to do, so no remedy is invented. Refreshing here would
/// be the byte-identical refresh this whole subsystem exists to prevent.
let unchanged (fileName: string) : DevReload.DevReloadEvent =
  notApplied
    "Unchanged"
    (sprintf "%s saved — no declaration changed, so the running app is already current." fileName)
    ""

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
