module SageFs.Core.Features.RestartSubjectDecision

// ── Which subject a set of reasons must restart ───────────────────────
//
// WHY this exists: the scope was computed, put into the user-facing message,
// and then DROPPED. `AppRunner.requireRestart` takes the `ReloadChange` list,
// never the `RestartReason` list, so the actor never saw a `Scoped` scope and
// restarted the whole worker anyway — while telling the user "the rest of the
// app keeps running". A claim with no consequence is the same defect class as
// the ones this release has been removing.
//
// The decision is a pure fold so it can be property-tested against the
// invariant that actually matters:
//
//   a single `Everything` reason forces a whole-worker restart, ALWAYS.
//
// That is not a preference. One reason that genuinely cannot be scoped means
// some value laid out by the old definition is still live somewhere, and a
// partial restart would leave it in place — strictly worse than a full one. So
// the safe direction is also the correct one, and the fold refuses to be
// clever about it.

open SageFs
open SageFs.Features

/// The subject a set of restart reasons must act on.
///
/// `Worker` is today's behaviour and the safe fallback. `UnitScope` is only
/// reached when EVERY reason is individually scoped, so it can never be
/// produced by a partly-understood situation.
type Decision =
  /// Restart the whole worker: at least one reason could not be scoped.
  | WholeWorker of because: string list
  /// Restart exactly one unit: every reason named the same one.
  | OneUnit of unit: string
  /// More than one unit is affected. The worker is still restarted, because
  /// SageFs rebuilds and relaunches a project, not a single DI registration —
  /// so this is reported honestly as "more than one unit" rather than being
  /// silently narrowed to the first.
  | SeveralUnits of units: string list

/// The scoped unit of a reason, when it has one.
let private scopedUnitOf (reason: ReloadOutcome.RestartReason) =
  match reason with
  | ReloadOutcome.RestartReason.TypeShapeChanged (_, RestartScope.Scoped unit) -> Some unit
  | _ -> None

/// The reason a reason cannot be scoped, when it cannot.
/// WHY every case is listed rather than a catch-all: the compiler caught two
/// I had missed, and a catch-all here would silently read a new reason as
/// "scopeable". The one `None` below is the ONLY reason that may be scoped.
let private unscopeableOf (reason: ReloadOutcome.RestartReason) =
  match reason with
  | ReloadOutcome.RestartReason.TypeShapeChanged (typeName, RestartScope.Everything) ->
    Some(sprintf "'%s' changed shape and no unit declares it, so some live value may still use the old definition" typeName)
  | ReloadOutcome.RestartReason.MutableModuleState binding ->
    Some(sprintf "'%s' IS the app's live state; a scoped restart would not reset it" binding)
  | ReloadOutcome.RestartReason.MutableStateTypeChanged (binding, _, _) ->
    Some(sprintf "'%s' is live state whose type changed, so the whole process must come back" binding)
  | ReloadOutcome.RestartReason.StartupComputedValue binding ->
    Some(sprintf "'%s' was computed once at process start, so nothing running can be re-pointed" binding)
  | ReloadOutcome.RestartReason.SignatureChanged declaration ->
    Some(sprintf "'%s' changed signature, and its callers hold the old method" declaration)
  | ReloadOutcome.RestartReason.ValueCopiedByApp (binding, holder) ->
    Some(sprintf "the running app kept a copy of '%s' in %s, so a scoped restart would leave the old copy in place" binding holder)
  | ReloadOutcome.RestartReason.ValueUntraceable (binding, why) ->
    Some(sprintf "'%s' changed and SageFs cannot check where its copies went (%s), so nothing can be scoped safely" binding why)
  | ReloadOutcome.RestartReason.NewDeclaration name ->
    Some(sprintf "'%s' did not exist when the app started, so there is nothing running to re-point" name)
  | ReloadOutcome.RestartReason.NotYetSupported shape ->
    Some(sprintf "'%s' is not understood well enough to scope a restart to part of the app" shape)
  | ReloadOutcome.RestartReason.PatchIneffective declaration ->
    Some(sprintf "'%s' was inlined into its caller, so only a re-launch picks the edit up" declaration)
  | ReloadOutcome.RestartReason.UnverifiedCopy declaration ->
    Some(sprintf "'%s' changed and SageFs cannot check where its copies went, so nothing can be scoped safely" declaration)
  | ReloadOutcome.RestartReason.ClosureShapeChanged (declaration, _) ->
    Some(sprintf "'%s' holds closures the running app already built, so only a re-launch gives them the new shape" declaration)
  | ReloadOutcome.RestartReason.InstanceLayoutChanged (typeName, _) ->
    Some(sprintf "live instances of '%s' were laid out without the new field, so a scoped restart could leave one behind" typeName)
  | ReloadOutcome.RestartReason.GenericInstantiationsUnknown (declaration, _) ->
    Some(sprintf "'%s' is generic, and a scoped restart could leave an instantiation SageFs cannot list on the old body" declaration)
  | ReloadOutcome.RestartReason.GenericTypeMember typeName ->
    Some(sprintf "live instances of the generic type '%s' were built by the old code, so a scoped restart could leave one behind" typeName)
  | ReloadOutcome.RestartReason.TypeShapeChanged (_, RestartScope.Scoped _) -> None

let ofReasons (reasons: ReloadOutcome.RestartReason list) : Decision =
  match reasons with
  | [] -> WholeWorker [ "no reason was produced, so the whole worker restarts" ]
  | _ ->
    let unscopeable = reasons |> List.choose unscopeableOf

    match unscopeable with
    | [] ->
      let units =
        reasons
        |> List.choose scopedUnitOf
        |> List.distinct
      // Every reason was a scoped type change, so the list is non-empty.
      match units with
      | [ single ] -> OneUnit single
      | several -> SeveralUnits several
    | blocked -> WholeWorker blocked

/// Whether the decision restarts the whole worker. The single question every
/// consumer actually needs, so it is one call rather than a pattern match.
let restartsWholeWorker (d: Decision) =
  match d with
  | WholeWorker _ -> true
  // SeveralUnits restarts the whole worker too: SageFs rebuilds a PROJECT, not
  // one DI registration, so there is no narrower action available. The DU
  // records that several units were involved (so the user is told), while the
  // action is the same as WholeWorker. Only OneUnit actually narrows.
  | SeveralUnits _ -> true
  | OneUnit _ -> false

/// The subject to hand the restart policy, which is what it ultimately needs.
let toSubject (d: Decision) : GranularRestart.RestartSubject =
  match d with
  | OneUnit unit -> GranularRestart.RestartSubject.UnitScope unit
  | WholeWorker _
  | SeveralUnits _ -> GranularRestart.RestartSubject.Worker

/// What to tell the user, in the same voice as the reason text: say which
/// subject is affected and WHY, so a scoped restart is visibly different
/// rather than an unexplained partial behaviour.
let describe (d: Decision) : string =
  match d with
  | WholeWorker because -> String.concat "; " because
  | OneUnit unit -> sprintf "restart unit '%s'; everything else keeps running" unit
  | SeveralUnits units ->
    sprintf
      "restart the app: %s all changed shape, and SageFs rebuilds a project rather than one registration"
      (String.concat ", " units)
