/// The delta route for an app SageFs started with `run_app`.
///
/// The app runs in this process, out of the reach of the reload agent, so a detour cannot change it. What
/// can: the project's own build, and the difference between that build and the assembly this process
/// loaded, handed to the runtime as a metadata delta. This is the part that does the doing, one save at a
/// time. What it decides comes from `DeltaSession`, which is pure and is what the simulation folds.
///
/// One `Session` per loaded module. A restart replaces the process and so the session; a second run of the
/// app in the same process continues the chain, because the module is the same patched module.
module SageFs.RunAppDelta

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open SageFs.Features.MetadataDelta
open SageFs.Features.PatchConfirmation
open SageFs.Middleware.EntryProbes

/// How long each part of a save took, in milliseconds. Measured so the route's cost is a number, not a feeling.
type SaveTimings =
  { BuildMs: float
    PrepareMs: float
    ApplyMs: float }

/// A save the runtime took.
type Landed =
  { /// The methods whose new bodies are watched, each with the probe that says it started.
    Watched: WatchedDecl list
    /// Methods the delta added. Nothing runs them until a caller does, so they are not watched.
    Added: int
    /// What a metadata-update handler threw while telling the process, if anything. The code is patched.
    HandlerFailures: string list
    Timings: SaveTimings }

/// What a save came to.
[<RequireQualifiedAccess>]
type SaveResult =
  /// The build is the one the process already runs.
  | Unchanged
  /// The app restarts, for these reasons.
  | Restart of first: Refusal * rest: Refusal list
  /// The project did not build. The process is as it was.
  | BuildFailed of message: string
  | Landed of Landed

/// Where the build of this assembly lands: the directory the shadow copy was made from.
let private builtPathOf (loadedPath: string) : Result<string, string> =
  match SageFs.ShadowCopy.tryReadOriginDir loadedPath with
  | Some originDir -> Result.Ok (Path.Combine(originDir, Path.GetFileName loadedPath))
  | None -> Result.Error (sprintf "%s is not a shadow copy, so the build it came from is not known" loadedPath)

let private probeTarget : ProbeTarget =
  ProbeTarget.ofMethod (typeof<EntryHooks>.GetMethod "Enter")

/// The chain, once there is one to start. A route that cannot be used has none.
[<RequireQualifiedAccess>]
type private ChainHolder =
  | NoChain
  | Started of DeltaChain

/// The route for one loaded assembly. Saves are taken one at a time: a delta is prepared from the generation the
/// chain is at, so two saves in flight would prepare from the same one, and the second would be refused as stale.
[<Sealed>]
type Session private (assembly: Assembly, project: string, workingDir: string, builtPath: string, initial: Standing, initialChain: ChainHolder) =
  let gate = new SemaphoreSlim(1, 1)
  let mutable standing = initial
  let mutable chain = initialChain

  /// The assembly this session patches.
  member _.Assembly : Assembly = assembly

  member _.Standing : Standing = standing

  /// Capture the baseline: the module this process loaded, as it is now. What it can do is read from the runtime
  /// here, once, and again before every save (a debugger can attach later).
  static member Start(assembly: Assembly, project: string, workingDir: string) : Session =
    let running = assembly.ManifestModule.ModuleVersionId
    let gaps = DeltaApply.moduleGaps (DeltaApply.capability ()) assembly
    let unusable (why: string) = Session(assembly, project, workingDir, "", Standing.Unusable why, ChainHolder.NoChain)
    match DeltaSession.start running gaps, builtPathOf assembly.Location with
    | Standing.Unusable why, _ -> unusable why
    | _, Result.Error why -> unusable why
    | tracking, Result.Ok builtPath ->
      try
        let image = PeImage.OfFile assembly.Location
        match image.Mvid = running with
        | false -> unusable "the file this assembly was loaded from is not the module in memory"
        | true ->
          let chain = DeltaChain.Start(image, ProbeStripping.StripCoverageProbes CoverageProbe.hitSymbol)
          Session(assembly, project, workingDir, builtPath, tracking, ChainHolder.Started chain)
      with e -> unusable (sprintf "the loaded assembly cannot be read: %s" e.Message)

  /// Build the project and take what changed as a delta.
  member _.Save() : Async<SaveResult> =
    async {
      do! gate.WaitAsync() |> Async.AwaitTask
      try
        let running = assembly.ManifestModule.ModuleVersionId
        let capability = DeltaApply.capability ()
        let gaps = DeltaApply.moduleGaps capability assembly
        match DeltaSession.precheck standing running gaps, chain with
        | Result.Error refusal, _ -> return SaveResult.Restart(refusal, [])
        | Result.Ok _, ChainHolder.NoChain -> return SaveResult.Restart(Refusal.Unavailable "the delta chain was not started", [])
        | Result.Ok _, ChainHolder.Started current ->
          let watch = Stopwatch.StartNew()
          match! SageFs.SessionBuild.runBuildAsync [ project ] workingDir with
          | Result.Error err -> return SaveResult.BuildFailed (SageFs.SageFsError.describe err)
          | Result.Ok _ ->
            let buildMs = watch.Elapsed.TotalMilliseconds
            watch.Restart()
            let next = PeImage.OfFile builtPath
            // A save gets a fresh probe per method it patches, so a probe says "this save's body ran". Asked for more than
            // once while the delta is written, and the same method keeps its probe within one.
            let allocated = Dictionary<MethodId, EntryProbe>()
            let assign (method': MethodId) : int64 =
              match allocated.TryGetValue method' with
              | true, probe -> probe.Id
              | false, _ ->
                let probe = ProbeRegistry.Shared.Allocate(MethodId.describe method')
                allocated[method'] <- probe
                probe.Id
            let prepared = current.PrepareProbing(Guid.NewGuid(), next, EntryProbing.ProbeEntry(probeTarget, assign))
            let prepareMs = watch.Elapsed.TotalMilliseconds
            let preparation =
              match prepared with
              | PrepareOutcome.NothingChanged -> Preparation.NothingChanged
              | PrepareOutcome.Refused causes -> Preparation.Refused causes
              | PrepareOutcome.Ready delta -> Preparation.Ready delta.FromGeneration
            match DeltaSession.decide standing running gaps preparation, prepared with
            | SaveDecision.Unchanged, _ -> return SaveResult.Unchanged
            | SaveDecision.Restart(first, rest), _ -> return SaveResult.Restart(first, rest)
            | SaveDecision.Apply, (PrepareOutcome.NothingChanged | PrepareOutcome.Refused _) ->
              return SaveResult.Restart(Refusal.Unavailable "the route decided to apply a delta that was not prepared", [])
            | SaveDecision.Apply, PrepareOutcome.Ready delta ->
              match DeltaApply.check capability assembly delta.Payload, DeltaSession.decide standing running gaps (Preparation.Ready delta.FromGeneration) with
              | CapabilityCheck.Incapable found, _ ->
                return SaveResult.Restart(Refusal.Unavailable(found |> List.map DeltaApply.describeGap |> String.concat "; "), [])
              // Asked again at the moment of the call, against the standing as it is then: the saves are taken one at a time,
              // and this is what makes that a property of the decision and not only of the lock around it.
              | CapabilityCheck.Capable, SaveDecision.Restart(first, rest) -> return SaveResult.Restart(first, rest)
              | CapabilityCheck.Capable, SaveDecision.Unchanged -> return SaveResult.Unchanged
              | CapabilityCheck.Capable, SaveDecision.Apply ->
                watch.Restart()
                let outcome = DeltaApply.apply assembly delta.Payload
                let applyMs = watch.Elapsed.TotalMilliseconds
                let landing = DeltaSession.landingOf outcome
                standing <- DeltaSession.settle standing landing
                match landing with
                | Landing.DidNotLand why -> return SaveResult.Restart(Refusal.Unavailable why, [])
                | Landing.Landed
                | Landing.LandedWithHandlerFailures _ ->
                  chain <- ChainHolder.Started (current.Commit delta)
                  // This save's bodies are the ones that run now: an earlier probe of the same method that never ran is superseded.
                  for probe in allocated.Values do
                    ProbeRegistry.Shared.Commit probe
                  let failures =
                    match landing with
                    | Landing.LandedWithHandlerFailures found -> found
                    | _ -> []
                  let watched =
                    delta.Payload.Probes
                    |> List.map (fun (method', id) -> { Declaration = MethodId.describe method'; Probes = [ id ] })
                  return
                    SaveResult.Landed
                      { Watched = watched
                        Added = delta.Payload.AddedMethods.Length
                        HandlerFailures = failures
                        Timings = { BuildMs = buildMs; PrepareMs = prepareMs; ApplyMs = applyMs } }
      finally
        gate.Release() |> ignore
    }
