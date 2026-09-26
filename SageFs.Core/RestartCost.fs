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

/// What a probe of the RUNNING APP came back with. The distinction that
/// matters is `Counted 0` versus `CouldNotCount`: "the app says it holds none"
/// skips a build, while "the probe could not tell" must pay one. Collapsing
/// them into an int would make a failed probe look like a free restart.
[<RequireQualifiedAccess>]
type ProbeOutcome =
  /// The app answered and the count is real.
  | Counted of live: int
  /// The probe could not run, or the type is not present in the running app.
  /// NOT zero, and never treated as zero.
  | CouldNotCount of because: string

[<RequireQualifiedAccess>]
module RestartCost =

  /// Map what the running app said into the liveness the cost decision wants.
  /// Named so the mapping is one auditable place rather than scattered
  /// conversions, and so a fourth probe source (a DI container, an agent
  /// registry) plugs in here.
  let livenessFromProbe (typeName: string) (outcome: ProbeOutcome) : Liveness =
    match outcome with
    | ProbeOutcome.Counted 0 ->
      Liveness.NoLiveInstances(sprintf "the running app reports no live %s" typeName)
    | ProbeOutcome.Counted n ->
      Liveness.HoldsLiveInstances(sprintf "the running app reports %d live %s" n typeName)
    | ProbeOutcome.CouldNotCount why ->
      Liveness.Unknown(sprintf "the probe for '%s' could not answer: %s" typeName why)

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

  /// Decide from a LIVENESS ANSWER — the primary seam.
  ///
  /// This exists because the earlier one took `string -> int option`, and that
  /// was wrong in a way this repo's own rules forbid: `None` collapsed "there
  /// is no registry", "nothing holds it" and "the source could not answer" into
  /// one absence. A railway result with a single `Error` is not a railway
  /// result — it is an option with a doc comment.
  ///
  /// It does NOT funnel through `decide`/`Liveness`, on purpose. Round-tripping
  /// an intent-carrying answer through a narrower type and back would throw away
  /// exactly the provenance that made the answer worth carrying, and would let
  /// the two drift apart. The domain states each case and acts on it directly.
  ///
  /// The restart's WIDTH is deliberately not consulted: how wide a restart is
  /// and whether it needs a build are different questions, and conflating them
  /// is how a "scoped restart" ends up claiming a cheaper build than the
  /// toolchain can perform.
  let decideFromLiveCount (answer: LiveCount) : RestartAction =
    match answer with
    | LiveCount.HeldBy boundaries ->
      RestartAction.RebuildProject(
        sprintf
          "%d boundary/boundaries still hold a value of the old shape (%s), so the assembly must be rebuilt"
          boundaries.Length
          (String.concat ", " boundaries))
    | LiveCount.HeldByNothing ->
      RestartAction.RespawnOnly "a liveness source was consulted and nothing holds the old shape, so respawning is enough"
    | LiveCount.Unconsulted because ->
      RestartAction.RebuildProject(
        sprintf "liveness was never established (%s), so pay the build rather than risk a stale value" because)
    | LiveCount.SourceFailed because ->
      RestartAction.RebuildProject(
        sprintf "the liveness source could not answer (%s), so pay the build rather than risk a stale value" because)

  /// Whether the action rebuilds. One question, so a caller cannot ask it two
  /// ways and get two answers.
  let rebuilds (action: RestartAction) =
    match action with
    | RestartAction.RebuildProject _ -> true
    | RestartAction.RespawnOnly _ -> false
