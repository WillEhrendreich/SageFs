/// Seeing a patched function's NEW body run.
///
/// A save re-points the old function at the new body with a detour. The detour
/// landing proves nothing about what the app does: a caller that had the old
/// function inlined keeps running its own copy of the old body and never enters
/// the function whose entry point was re-pointed. The only evidence that a
/// patch is live is the new body running.
///
/// The detour therefore points at a STUB instead of at the new body. The stub
/// has the new body's exact signature, records an entry under a probe id, and
/// calls the new body. A patch is reported live only once its probe has been
/// entered.
///
/// Why a stub and not a Harmony prefix on the new body: the new body lives in
/// FSI's dynamic assembly, and a second Harmony patch on a method that a
/// detour also points at would have to survive the next save re-detouring it
/// (`ValueReadTracking.releaseBeforeDetour` exists because an unpatch rewrites
/// the method's entry and puts the old code back over a detour). A stub is a
/// method of its own. Nothing is patched twice, and a newer save simply stops
/// pointing at it.
///
/// BCL only, like `ValueReadTracking`: compiled into the isolated FSI host too.
module SageFs.Middleware.EntryProbes

open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Emit
open System.Threading.Tasks

/// One probe: the id the host counts entries under, and the function whose new
/// body it watches (the qualified name of the new method).
type EntryProbe = { Id: int64; Declaration: string }

/// What the host knows about one probe.
[<RequireQualifiedAccess>]
type ProbeStatus =
  /// The new body ran.
  | Entered
  /// A newer save re-pointed the same function onto a newer body before this
  /// one ran, so this body is no longer the one that will run.
  | Superseded
  /// Nothing has entered it (yet).
  | NotEntered

type ProbeSighting = { Probe: int64; Status: ProbeStatus }

/// The host's answer about a set of probes, one sighting per probe asked about.
type EntryReading = { Sightings: ProbeSighting list }

/// The process's probes. Thread-safe: entries arrive on whatever thread runs
/// the patched code, and waits run beside the eval thread.
///
/// A wait ends the moment every probe it watches has been sighted (entered or
/// superseded) or when the bound runs out, whichever is first. The bound is a
/// function so a test can fire it by hand.
[<Sealed>]
type ProbeRegistry(delay: TimeSpan -> Task) =
  let gate = obj ()
  let statuses = Dictionary<int64, ProbeStatus>()
  let declarations = Dictionary<int64, string>()
  let waiters = List<int64 list * TaskCompletionSource<unit>>()
  let mutable nextId = 0L

  static let shared = ProbeRegistry(fun bound -> Task.Delay bound)

  let statusOf (id: int64) : ProbeStatus =
    match statuses.TryGetValue id with
    | true, status -> status
    | false, _ -> ProbeStatus.NotEntered

  let allSighted (ids: int64 list) : bool =
    ids |> List.forall (fun id -> statusOf id <> ProbeStatus.NotEntered)

  let readLocked (ids: int64 list) : EntryReading =
    { Sightings = ids |> List.map (fun id -> { Probe = id; Status = statusOf id }) }

  /// Wake every wait whose probes have all been sighted. Called with the lock held.
  let wakeReady () =
    for ids, waiting in waiters.ToArray() do
      match allSighted ids with
      | true -> waiting.TrySetResult() |> ignore
      | false -> ()

  new() = ProbeRegistry(fun bound -> Task.Delay bound)

  /// The registry the stubs report to. A stub is a method, so it can only call a static.
  static member Shared : ProbeRegistry = shared

  /// A new probe for the function named `declaration`. Nothing is superseded
  /// until the patch it belongs to has landed (`Commit`).
  member _.Allocate(declaration: string) : EntryProbe =
    lock gate (fun () ->
      nextId <- nextId + 1L
      statuses.[nextId] <- ProbeStatus.NotEntered
      declarations.[nextId] <- declaration
      { Id = nextId; Declaration = declaration })

  /// The patch `probe` belongs to landed. Any earlier probe of the same
  /// function that never ran is superseded: the function's body is this one now.
  member _.Commit(probe: EntryProbe) : unit =
    lock gate (fun () ->
      for KeyValue(id, status) in Seq.toArray statuses do
        match id < probe.Id && status = ProbeStatus.NotEntered && declarations.[id] = probe.Declaration with
        | true -> statuses.[id] <- ProbeStatus.Superseded
        | false -> ()
      wakeReady ())

  /// The new body ran. Entering twice changes nothing, and a superseded probe stays superseded.
  member _.Enter(id: int64) : unit =
    lock gate (fun () ->
      match statuses.TryGetValue id with
      | true, ProbeStatus.NotEntered ->
        statuses.[id] <- ProbeStatus.Entered
        wakeReady ()
      | _ -> ())

  /// What is known right now. A probe id nobody allocated reads NotEntered.
  member _.Read(ids: int64 list) : EntryReading = lock gate (fun () -> readLocked ids)

  /// Wait until every probe has been sighted or `bound` passes, then report what is known.
  member _.Await(ids: int64 list, bound: TimeSpan) : Async<EntryReading> =
    async {
      let waiting = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let ready =
        lock gate (fun () ->
          match allSighted ids with
          | true -> true
          | false ->
            waiters.Add((ids, waiting))
            false)
      match ready with
      | true -> ()
      | false ->
        let! _ = Async.AwaitTask(Task.WhenAny(waiting.Task :> Task, delay bound))
        lock gate (fun () -> waiters.RemoveAll(fun (_, w) -> obj.ReferenceEquals(w, waiting)) |> ignore)
      return lock gate (fun () -> readLocked ids)
    }

/// What a stub calls. Static, because IL can only call a static from a dynamic method.
type EntryHooks =
  static member Enter(probe: int64) : unit = ProbeRegistry.Shared.Enter probe

/// Why no stub could be built for a function.
[<RequireQualifiedAccess>]
type StubFailure =
  /// Only non-generic methods of a class (or static ones) are detoured, so only those get a stub.
  | NotDetourable of declaration: string
  /// The runtime refused the method's signature or the IL.
  | CouldNotEmit of declaration: string * detail: string

module StubFailure =
  let describe (failure: StubFailure) : string =
    match failure with
    | StubFailure.NotDetourable declaration -> sprintf "%s is not a non-generic method of a class" declaration
    | StubFailure.CouldNotEmit(declaration, detail) -> sprintf "%s: %s" declaration detail

let private stubs = List<DynamicMethod>()

/// A method with `target`'s exact signature that records an entry under `probe`
/// and then calls `target`. A detour can point at it in place of the target.
/// Non-generic targets only, which is what hot reload detours. An Error says why
/// no stub could be built; the caller then detours straight at the target and that
/// function can never be confirmed.
///
/// An INSTANCE target gets a static stub that takes the instance as its first
/// argument, which is how an instance method is called anyway, so a detour from
/// one instance method to the stub passes `this` straight through. The stub calls
/// the target with `call`, never `callvirt`: the instance it is handed is of the
/// OLD type (that is the detour's point), and a virtual dispatch on it would land
/// on the old method again, which is detoured back to the stub.
let stubFor (probe: EntryProbe) (target: MethodInfo) : Result<MethodInfo, StubFailure> =
  try
    let detourable =
      not target.IsGenericMethod
      && (target.IsStatic || (not (isNull target.DeclaringType) && not target.DeclaringType.IsValueType))
    match detourable with
    | false -> Result.Error(StubFailure.NotDetourable probe.Declaration)
    | true ->
      let declared = target.GetParameters() |> Array.map (fun p -> p.ParameterType)
      let parameters =
        match target.IsStatic with
        | true -> declared
        | false -> Array.append [| target.DeclaringType |] declared
      let stub =
        DynamicMethod(
          sprintf "sagefs-entry-probe:%s" probe.Declaration,
          target.ReturnType,
          parameters,
          typeof<EntryHooks>.Module,
          true
        )
      let il = stub.GetILGenerator()
      il.Emit(OpCodes.Ldc_I8, probe.Id)
      il.Emit(OpCodes.Call, typeof<EntryHooks>.GetMethod "Enter")
      for i in 0 .. parameters.Length - 1 do
        il.Emit(OpCodes.Ldarg, int16 i)
      il.Emit(OpCodes.Call, target)
      il.Emit OpCodes.Ret
      // A detour points at the stub's code, which lives only as long as the stub does.
      lock stubs (fun () -> stubs.Add stub)
      Result.Ok(stub :> MethodInfo)
  with ex -> Result.Error(StubFailure.CouldNotEmit(probe.Declaration, sprintf "%s: %s" (ex.GetType().Name) ex.Message))
