namespace SageFs

// ── Does a restart actually need a REBUILD? ─────────────────────────────
//
// WHY this exists, and why it is not the same question as "which unit
// changed": a per-boundary BUILD is impossible. `SessionBuild.runBuildAsync`
// is ONE `dotnet build` on ONE project, and F# compilation is whole-assembly.
// There is no narrower build to ask for, so "make the scoped restart cheaper"
// cannot be answered by narrowing the build.
//
// What CAN be answered is whether a build is needed at all. A boundary that
// holds NO LIVE INSTANCE of the changed type has nothing laid out by the old
// definition, so respawning the worker is sufficient and the build — the
// expensive part — is skipped. That is the honest win available today, and
// claiming a narrower build would be a lie the build system would refute.
//
// THE FAILURE DIRECTION IS THE WHOLE DESIGN. `Unknown` is its own value, not
// a synonym for "probably fine": a stale value holding the old type's layout
// in a process that believes it restarted is worse than a slow restart. So
// anything SageFs cannot establish pays the build.

/// Whether anything alive still holds the OLD shape of a type.
[<RequireQualifiedAccess>]
type Liveness =
  /// At least one live instance exists, laid out by the old definition. The
  /// assembly must be rebuilt or that instance stays wrong.
  | HoldsLiveInstances of boundary: string
  /// The boundary is registered but nothing alive holds the type: every
  /// instance was already replaced, or none was ever constructed.
  | NoLiveInstances of boundary: string
  /// Nobody can say. Distinct from both, because it costs a build.
  | Unknown of because: string

/// What a restart must actually do.
[<RequireQualifiedAccess>]
type RestartAction =
  /// Respawn the worker without rebuilding. Correct only when nothing alive
  /// holds the old shape.
  | RespawnOnly of because: string
  /// Rebuild the project and relaunch. F# is whole-assembly, so this is the
  /// only width a build can have.
  | RebuildProject of because: string

[<RequireQualifiedAccess>]
module RestartCost =

  /// Decide the action. The `subject` is deliberately NOT consulted: how wide
  /// the restart is and whether it needs a build are different questions, and
  /// conflating them is how a "scoped restart" ends up claiming a cheaper
  /// build than the toolchain can perform.
  let decide (liveness: Liveness) : RestartAction =
    match liveness with
    | Liveness.NoLiveInstances b ->
      RestartAction.RespawnOnly(
        sprintf "'%s' holds no live instance, so respawning the worker is enough" b)
    | Liveness.HoldsLiveInstances b ->
      RestartAction.RebuildProject(
        sprintf "'%s' holds a live instance laid out by the old definition, so the assembly must be rebuilt" b)
    | Liveness.Unknown why ->
      RestartAction.RebuildProject(sprintf "liveness is unknown (%s), so pay the build rather than risk a stale value" why)

  /// Whether the action rebuilds. One question, so a caller cannot ask it two
  /// ways and get two answers.
  let rebuilds (action: RestartAction) =
    match action with
    | RestartAction.RebuildProject _ -> true
    | RestartAction.RespawnOnly _ -> false
