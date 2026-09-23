/// What a saved change ACTUALLY did to the running process, and — when it could
/// not be applied — why, in terms of the user's own code, plus what to do.
///
/// This type exists because of a failure we shipped: a save that patched a few
/// incidental helpers but none of the handlers the user edited still broadcast
/// `Reload`, so the browser refreshed and served the old code. The tool reported
/// success; the user experienced "hot reload is broken".
///
/// Every mature hot-reload implementation surveyed (JVMTI, the Dart VM, Erlang,
/// Vite, React Fast Refresh) converged on the same three rules, and this module
/// encodes all three:
///
///   1. A refusal is TYPED and named after the SHAPE of the change — not after
///      the internal step that refused it. JVMTI has eight such codes; the Dart
///      VM defines a `ClassReasonForCancelling` subclass per shape.
///   2. The message CARRIES THE REMEDY. Every Dart VM refusal renders as
///      "<reason>.\nTry performing a hot restart instead."; React's names the
///      refactor that restores fast refresh.
///   3. "SUCCEEDED BUT HAD NO EFFECT" IS ITS OWN NAMED OUTCOME, WITH A COUNT.
///      Flutter prints "Reloaded 1 of 448 libraries", so "0 of 448" is visible
///      as a non-event. A generic "reload failed" is the anti-pattern every one
///      of these systems explicitly moved away from.
module SageFs.Features.ReloadOutcome

/// Why a saved change could not be re-pointed into the already-running process.
///
/// Named after the shape of the change the user made, because that is what they
/// can act on. "The detour planner found no matching parameter types" is true
/// and useless; "`routes` is computed once at startup" is actionable.
[<RequireQualifiedAccess>]
type RestartReason =
  /// A binding whose VALUE was computed during module initialisation, so the
  /// running app captured the finished result. Re-pointing methods cannot reach
  /// it. The Falco/Giraffe/Saturn `let routes = [...]` shape, and
  /// `let getHome : HttpHandler = Response.ofHtml (...)`.
  | StartupComputedValue of binding: string
  /// Module-level mutable state. Its value is live data, not code; carrying it
  /// forward would ignore a deliberate initialiser edit and resetting it would
  /// destroy the running state, so SageFs refuses to guess (the posture Flutter
  /// and Erlang both take).
  | MutableModuleState of binding: string
  /// A `let mutable` whose type changed. The live value has the old type, so
  /// keeping it isn't safe and there's nothing to carry.
  | MutableStateTypeChanged of binding: string * was: string * now: string
  /// The compiled signature changed, so the old and new methods are not the
  /// same method and no detour can pair them.
  | SignatureChanged of declaration: string
  /// A type's shape changed. Existing instances in the running process were
  /// laid out by the old definition.
  | TypeShapeChanged of typeName: string
  /// Something that did not exist when the process started. There is no
  /// original to re-point.
  | NewDeclaration of name: string
  /// A shape SageFs does not handle YET, as distinct from one it cannot handle.
  /// The Dart VM prefixes exactly this distinction with "Limitation: ", and it
  /// matters: one is a bug report worth filing, the other is physics.
  | NotYetSupported of shape: string
  /// The detour was ACCEPTED and the running native code did not change: the
  /// canary compared the method's JIT-compiled bytes either side of the patch
  /// and found them identical.
  ///
  /// This case exists because the canary used to be discarded. `DetourApplied`
  /// described `Ineffective` as "still counted as redirected (the canary is a
  /// warning signal, not a verdict)", so a method the canary had already proven
  /// unchanged was counted as landed — and the save was reported as
  /// "Hot reloaded 1 of 1" while the running process kept serving the old body.
  /// The canary is evidence about the running process, which is the only thing
  /// the count claims to describe, so it is a verdict here.
  | PatchIneffective of declaration: string
  /// An immutable value you redefined, and the running app kept a copy of the
  /// old one (a closure built at startup, a lazy, a field). A patch of its
  /// getter can't reach a copy. `holder` says who kept it and how.
  | ValueCopiedByApp of binding: string * holder: string
  /// An immutable value you redefined, and SageFs can't see where the running
  /// app's copies went (an optimized build, say), so it won't claim a patch.
  | ValueUntraceable of binding: string * why: string
  /// Harmony re-pointed A copy of this function, but nothing here proves it
  /// is the copy the running process actually calls: no `AppHolds` record of
  /// which copy the app captured, or a different one than the redirect moved.
  /// Reached only when there is no known-good build baseline to diff a save
  /// against (`ReloadRoute.ReevaluateWholeFile`), which is exactly the shape
  /// an `.SageFs/init.fsx`-started app takes during warmup: a body edit still
  /// gets detoured onto SOME same-named copy, and without the baseline-driven
  /// `confirmPatchAsOutcome` check that ordinary saves get, that redirect used
  /// to be counted as a landed patch outright. On a project where FSI's
  /// multiemit keeps several distinct copies of "the same" function alive at
  /// once, that is a guess dressed as a fact — the honest default is a
  /// restart, not a claim this can't back up.
  | UnverifiedCopy of declaration: string

module RestartReason =

  /// What happened, in terms of the user's own code. No call to action — every
  /// surface words its own from `remedy`.
  let describe =
    function
    | RestartReason.StartupComputedValue binding ->
      sprintf "'%s' is computed once when the module loads, so the running app captured the finished value" binding
    | RestartReason.MutableModuleState binding ->
      sprintf "'%s' is mutable module state, which is live data rather than code" binding
    | RestartReason.MutableStateTypeChanged(binding, was, now) ->
      sprintf "'%s' changed type from %s to %s, so its live value can't carry over" binding was now
    | RestartReason.SignatureChanged decl ->
      sprintf "'%s' changed signature, so it is no longer the same method the running app calls" decl
    | RestartReason.TypeShapeChanged typeName ->
      sprintf "the shape of type '%s' changed, and the running app holds values laid out by the old definition" typeName
    | RestartReason.NewDeclaration name ->
      sprintf "'%s' did not exist when the app started, so there is nothing running to re-point" name
    | RestartReason.NotYetSupported shape ->
      sprintf "Limitation: SageFs does not re-point %s yet" shape
    | RestartReason.PatchIneffective decl ->
      sprintf
        "'%s' was re-pointed but the running code did not change, so the app is still executing the old body"
        decl
    | RestartReason.ValueCopiedByApp(binding, holder) ->
      sprintf "the running app kept a copy of '%s', and a patch can't reach a copy: %s" binding holder
    | RestartReason.ValueUntraceable(binding, why) ->
      sprintf "SageFs can't see where the running app's copies of '%s' went, so it won't claim a patch: %s" binding why
    | RestartReason.UnverifiedCopy declaration ->
      sprintf
        "SageFs re-pointed a copy of '%s', but this file has no build baseline to confirm it's the copy the running app calls"
        declaration

  /// What the user can actually do. Never empty — a refusal a user cannot act
  /// on is a dead end, and this is the field that stops it being one.
  let remedy =
    function
    | RestartReason.StartupComputedValue binding ->
      sprintf
        "Restart the app to pick it up. To make '%s' reloadable, make it a function of its input — 'let getHome (ctx: HttpContext) = ...' is re-pointed on every request; 'let getHome : HttpHandler = ...' is built once at startup."
        binding
    | RestartReason.MutableModuleState binding ->
      sprintf
        "Restart the app to re-run the initialiser for '%s'. SageFs will not carry the old value forward or reset it, because both silently lose something: carrying it forward ignores your edit, resetting it destroys live state."
        binding
    | RestartReason.MutableStateTypeChanged(binding, _, _) ->
      sprintf "Restart the app to start '%s' over with its new type. The running app is left exactly as it was until you do." binding
    | RestartReason.SignatureChanged _
    | RestartReason.TypeShapeChanged _
    | RestartReason.NewDeclaration _ ->
      "Restart the app — this change takes effect when the process starts."
    | RestartReason.NotYetSupported _ ->
      "Restart the app to pick this change up. If this shape matters to you, it is worth reporting — it is unimplemented, not impossible."
    | RestartReason.PatchIneffective decl ->
      sprintf
        "Restart the app to pick it up. The usual cause is that '%s' was inlined into its caller before the edit, so the caller holds its own copy of the old body and there is no entry point left to re-point. Marking it [<MethodImpl(MethodImplOptions.NoInlining)>] keeps it reloadable."
        decl
    | RestartReason.ValueCopiedByApp(binding, _) ->
      sprintf
        "Restart the app to pick it up. A value only reloads while nothing has kept a copy of it: code that reads '%s' when it's needed (a function of its input) gets the new value, code that stored it at startup or cached it doesn't."
        binding
    | RestartReason.ValueUntraceable _ ->
      "Restart the app to pick it up. Build through SageFs (it builds with -p:Optimize=false) and a redefined value can be checked and patched."
    | RestartReason.UnverifiedCopy _ ->
      "Restart the app to be sure this change takes effect. This file has no known-good build baseline yet — rebuild the project and hard_reset_fsi_session (rebuild=true) so future saves to it can be confirmed precisely."

/// A `let mutable` whose initializer you edited while the app was running. The
/// app kept its live value (rule 3 of the state spec), and this is what the
/// save says about it.
type KeptValue = {
  /// The qualified binding, e.g. `StateFixture.State.tuned`.
  Binding: string
  /// `%A` of the live value it kept, as the save found it.
  KeptValue: string
  /// The edited initializer's source text. It runs on reset, not before.
  NewInitializer: string
}

/// What a save did to the process that is already running.
///
/// Construct through `ofPatchCounts` rather than directly, so `Patched(0, n)` —
/// "succeeded, changed nothing", the exact lie this type exists to prevent — is
/// unrepresentable.
[<RequireQualifiedAccess>]
type ReloadOutcome =
  /// At least one method was re-pointed; the running process now serves the new
  /// code for those. Both numbers are reported so a partial reload is visible
  /// as partial rather than as success.
  | Patched of patched: int * considered: int
  /// The save was processed, nothing could be re-pointed, and SageFs does not
  /// own the app's lifetime — so the user has to act. This is the case that
  /// used to broadcast a plain `Reload`.
  | NoEffect of considered: int * reasons: RestartReason list
  /// Nothing could be re-pointed, and SageFs restarted the app itself. The
  /// running process IS current; the user need do nothing.
  | Restarted of reasons: RestartReason list
  /// Nothing could be re-pointed, SageFs cannot restart the app for the user
  /// (it did not start it), and here is what to do.
  | RestartRequired of reasons: RestartReason list
  /// The file did not compile. The running app is untouched and still serving
  /// the last code that did compile — which is a feature, and worth saying.
  | CompileFailed of summary: string
  /// You edited the initializer of live state, and the app KEPT the live value
  /// instead of throwing it away. Anything else the save changed was patched
  /// as usual (`patched` of `considered`, which counts the kept bindings too).
  /// Head and rest: there's always at least one kept binding, or this is some
  /// other outcome.
  | KeptLiveState of patched: int * considered: int * first: KeptValue * rest: KeptValue list

module ReloadOutcome =

  /// The only way to build a patch result. A patch count of zero is not a
  /// success with a small number in it — it is a different outcome, and gets a
  /// different case.
  let ofPatchCounts (patched: int) (considered: int) (reasons: RestartReason list) : ReloadOutcome =
    match patched > 0 with
    | true -> ReloadOutcome.Patched(patched, considered)
    | false -> ReloadOutcome.NoEffect(considered, reasons)

  /// Did the running process change? The single question every caller actually
  /// has, answered once here rather than by each surface re-deriving it from
  /// counts — which is how the "broadcast Reload anyway" bug happened.
  let processChanged =
    function
    | ReloadOutcome.Patched _
    | ReloadOutcome.Restarted _ -> true
    // Keeping a value changes nothing the app serves. Only a patch alongside it does.
    | ReloadOutcome.KeptLiveState(patched, _, _, _) -> patched > 0
    | ReloadOutcome.NoEffect _
    | ReloadOutcome.RestartRequired _
    | ReloadOutcome.CompileFailed _ -> false

  /// A browser reload is honest only when the bytes it will fetch are new.
  /// Telling a page to refresh into identical code is the failure users read as
  /// "the tool is broken".
  let shouldRefreshBrowser = processChanged

  /// One line, always carrying the count when there was one — the
  /// "Reloaded 1 of 448 libraries" discipline, so a no-op is visible.
  let describe =
    function
    | ReloadOutcome.Patched(patched, considered) ->
      sprintf "Hot reloaded %d of %d changed definition(s)" patched considered
    | ReloadOutcome.NoEffect(considered, reasons) ->
      let why =
        match reasons with
        | [] -> "none of them can be re-pointed into the running process"
        | r :: _ -> RestartReason.describe r
      sprintf "No effect: 0 of %d changed definition(s) reached the running app — %s" considered why
    | ReloadOutcome.Restarted reasons ->
      let why =
        match reasons with
        | [] -> "the change takes effect at startup"
        | r :: _ -> RestartReason.describe r
      sprintf "Restarted the app: %s" why
    | ReloadOutcome.RestartRequired reasons ->
      let why =
        match reasons with
        | [] -> "the change takes effect at startup"
        | r :: _ -> RestartReason.describe r
      sprintf "Restart needed: %s" why
    | ReloadOutcome.CompileFailed summary ->
      sprintf "Not applied — the file did not compile, so the app is still serving the last good code: %s" summary
    | ReloadOutcome.KeptLiveState(patched, considered, first, rest) ->
      let kept =
        first :: rest
        |> List.map (fun k -> sprintf "kept '%s' = %s (your new initializer %s applies when you reset it)" k.Binding k.KeptValue k.NewInitializer)
        |> String.concat "; "
      match patched with
      | 0 -> sprintf "Live state kept: %s" kept
      | n -> sprintf "Hot reloaded %d of %d changed definition(s), and %s" n considered kept

  /// What to do next, when there is something to do. `None` means the outcome
  /// is already resolved and the user needs no instruction.
  let remedy =
    function
    | ReloadOutcome.Patched _
    | ReloadOutcome.Restarted _ -> None
    | ReloadOutcome.NoEffect(_, reasons)
    | ReloadOutcome.RestartRequired reasons ->
      match reasons with
      | [] -> Some "Restart the app to pick this change up."
      | r :: _ -> Some(RestartReason.remedy r)
    | ReloadOutcome.CompileFailed _ -> Some "Fix the compile error; the app reloads automatically once it builds."
    | ReloadOutcome.KeptLiveState _ ->
      Some "To run the new initializer, reset it from the Hot Reload panel on the dashboard or with the reset_hot_reload_state MCP tool. Otherwise there's nothing to do, the app kept going."

  /// The whole user-facing message: what happened, and what to do about it.
  let describeForUser (outcome: ReloadOutcome) : string =
    match remedy outcome with
    | None -> describe outcome
    | Some action -> sprintf "%s\n→ %s" (describe outcome) action

  /// Folds in reasons for things that were attempted and missed OUTSIDE the
  /// accounting `outcome` was built from — a mutable binding that was
  /// declined or could not be re-pointed, which a function-only patch count
  /// never saw. Only `NoEffect`/`RestartRequired` have a reasons list to
  /// extend, so `considered` grows with them there (the "0 of N" honesty the
  /// whole type exists for); `Patched` already reported real successes for
  /// the functions it counted, and gets no reasons field to lose them in —
  /// the same partial-visibility limit an ordinary missed function already
  /// has today. A binding that TORE, not merely missed, does not belong
  /// here: it forces a restart of its own rather than joining this list.
  let withExtraMisses (extra: RestartReason list) (outcome: ReloadOutcome) : ReloadOutcome =
    match extra with
    | [] -> outcome
    | _ ->
      match outcome with
      | ReloadOutcome.NoEffect(considered, reasons) ->
        ReloadOutcome.NoEffect(considered + List.length extra, reasons @ extra)
      | ReloadOutcome.RestartRequired reasons -> ReloadOutcome.RestartRequired(reasons @ extra)
      | ReloadOutcome.Patched _
      | ReloadOutcome.Restarted _
      | ReloadOutcome.KeptLiveState _
      | ReloadOutcome.CompileFailed _ -> outcome

  /// Folds the bindings a save KEPT into what its patch did. Only outcomes
  /// where the process is still the one running can keep anything: a patch
  /// that landed, or a save that patched nothing. A restart or a compile
  /// failure has nothing to keep, and a partial no-effect keeps its reasons
  /// (the kept bindings still show up in the worker's pending list).
  let withKept (kept: KeptValue list) (outcome: ReloadOutcome) : ReloadOutcome =
    match kept, outcome with
    | [], _ -> outcome
    | first :: rest, ReloadOutcome.Patched(patched, considered) ->
      ReloadOutcome.KeptLiveState(patched, considered + List.length kept, first, rest)
    | first :: rest, ReloadOutcome.NoEffect(0, []) -> ReloadOutcome.KeptLiveState(0, List.length kept, first, rest)
    | _ :: _, ReloadOutcome.NoEffect _
    | _ :: _, ReloadOutcome.Restarted _
    | _ :: _, ReloadOutcome.RestartRequired _
    | _ :: _, ReloadOutcome.CompileFailed _
    | _ :: _, ReloadOutcome.KeptLiveState _ -> outcome
