/// Observed (passive) friction — detector engine.
///
/// See observed-friction-plan.md §(b)-(c). `prepare` fans the flat,
/// insertion-ordered `FrictionEvent` stream (FrictionSqlite.ReadEvents,
/// `ORDER BY id`) out into per-scope `PreparedStream`s; `detectAll` folds
/// every registered detector over every prepared stream.
///
/// This file wires the `detectors` registry to every detector STUB up
/// front (Brief B0) so Wave-1 briefs (B1-B6) never touch this file — each
/// fills in its own pre-registered stub in place, and a stub returning []
/// is a no-op until its brief lands.
module SageFs.Features.ObservedFriction

open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.ObservedFrictionTypes

/// Every event is stamped with this sentinel session pre-Brief-B9
/// (McpTools.fs:97, `SessionRef.create "mcp"`). Until B9 enriches the
/// substrate with real session/agent attribution, the only honest scope
/// for a "mcp"-stamped event is DaemonWide — never a fabricated per-session
/// split over data that carries no real session identity.
[<Literal>]
let private daemonWideSentinel = "mcp"

/// Split the flat store stream into per-scope PreparedStreams, preserving
/// the ORDER BY id order within each scope (List.groupBy is stable: a
/// group's elements stay in first-seen order). Pre-B9 every event has
/// Session="mcp" so this yields exactly one DaemonWide stream; post-B9 it
/// fans out per real Session with ZERO detector changes, because every
/// non-sentinel session maps straight to SignalScope.Session.
let prepare (events: FrictionEvent list) : PreparedStream list =
  events
  |> List.groupBy (fun e -> e.Session)
  |> List.map (fun (session, evs) ->
    let scope =
      if SessionRef.value session = daemonWideSentinel then
        SignalScope.DaemonWide
      else
        SignalScope.Session session
    { Scope = scope; Events = evs })

/// Every registered detector. A stub returning [] is a no-op until its
/// brief fills it in. NEVER edited by Wave-1 briefs — this registry
/// references every stub up front so B1-B6 stay file-disjoint: each brief
/// only ever touches its own `ObservedFriction.<Name>.fs` + test file.
let detectors : Detector list = [
  SageFs.Features.ObservedFrictionPolling.detect
  SageFs.Features.ObservedFrictionResetThrash.detect
  SageFs.Features.ObservedFrictionUnattributed.detect
  SageFs.Features.ObservedFrictionResolution.detect
  SageFs.Features.ObservedFrictionRepetition.detect
  SageFs.Features.ObservedFrictionInvalidState.detect
]

/// Fold every registered detector over every prepared stream. Pure — the
/// single public entry the read model (Brief B7) and dashboard (Brief B8)
/// call.
let detectAll (cfg: DetectorConfig) (events: FrictionEvent list) : DetectedSignal list =
  prepare events
  |> List.collect (fun stream -> detectors |> List.collect (fun detect -> detect cfg stream))
