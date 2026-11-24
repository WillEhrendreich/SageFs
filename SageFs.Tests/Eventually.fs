/// The ONE wait primitive for a test that reads a system which is still settling.
///
/// A passing run ends the wait the instant the fact appears. The deadline bounds the FAILURE path only:
/// load can make a pass slower, but it can never turn a pass into a fail. Every transient failure is a
/// typed infrastructure fact, never a verdict about the program under test, so a retry can never make
/// a wrong program look right.
module SageFs.Tests.Eventually

open System
open System.Diagnostics
open System.Threading.Tasks

/// An infrastructure fact: the thing being read is not available YET. This is never a program verdict.
[<RequireQualifiedAccess>]
type Transient =
  /// The process exists but its FSI host has not finished warming.
  | WarmingUp of detail: string
  /// A route or port is not accepting connections because the process is starting or restarting.
  | NotListening of detail: string
  /// The endpoint is not registered yet.
  | NoRoute of detail: string
  /// A process was asked to start and has not reported its port yet.
  | SpawnIncomplete of detail: string
  /// A read that the harness knows is transient but has no finer name.
  | Other of detail: string

  member this.Detail =
    match this with
    | WarmingUp d
    | NotListening d
    | NoRoute d
    | SpawnIncomplete d
    | Other d -> d

/// What one read says. `Ok (Some value)` is the fact; `Ok None` is "not yet";
/// `Error transient` is an infrastructure failure that must not be mistaken for a verdict.
type Read<'a> = Result<'a option, Transient>

/// A wait that ran out of budget. Distinct from an assertion failure: the gate can tell a machine that
/// never settled from a program that did the wrong thing.
type WaitExpired =
  { Description: string
    Budget: TimeSpan
    LastTransient: Transient option
    Observations: int }

  override this.ToString() =
    let last =
      match this.LastTransient with
      | Some t -> sprintf " Last transient: %s" t.Detail
      | None -> ""
    sprintf "the following did not become true within %O: %s (%d observations).%s" this.Budget this.Description this.Observations last

exception WaitTimedOut of WaitExpired

/// Classify a read failure. Only the shapes below are retried; every other exception is a verdict and
/// propagates to the assertion, so a retry can never hide a real bug.
module Transient =
  let ofException (ex: exn) : Transient option =
    let text = ex.Message
    let has (parts: string list) = parts |> List.forall (fun p -> text.Contains(p, StringComparison.OrdinalIgnoreCase))
    if has [ "connection refused" ] then Some(Transient.NotListening text)
    elif has [ "still warming up" ] || has [ "warming up" ] then Some(Transient.WarmingUp text)
    elif has [ "before reporting port" ] || has [ "no port" ] then Some(Transient.SpawnIncomplete text)
    elif has [ "404" ] || has [ "not found" ] then Some(Transient.NoRoute text)
    elif has [ "empty answer" ] then Some(Transient.Other text)
    elif has [ "did not answer within" ] then Some(Transient.WarmingUp text)
    else None

  let ofReason (detail: string) (kind: string) : Transient =
    match kind with
    | "warming" -> Transient.WarmingUp detail
    | "port" -> Transient.NotListening detail
    | "route" -> Transient.NoRoute detail
    | "spawn" -> Transient.SpawnIncomplete detail
    | _ -> Transient.Other detail

/// A probe that fails with a known transient is treated as "not yet"; any other exception escapes.
let private safeProbe (probe: unit -> Task<Read<'a>>) (): Task<Read<'a>> =
  task {
    try
      return! probe ()
    with ex ->
      match Transient.ofException ex with
      | Some t -> return Error t
      | None -> return raise ex
  }

/// Jittered exponential backoff. Jitter stops a gate's shards from polling in lockstep when a service
/// comes back; the random source is only used for delays, never for an assertion.
module private Backoff =
  let private rng = Random()

  let delay (attempt: int) : TimeSpan =
    let capMax = 500
    let baseMax = min capMax (25 * (pown 2 (min attempt 5)))
    let jitterMs = rng.Next(0, max 1 (baseMax / 2 + 1))
    TimeSpan.FromMilliseconds(float (baseMax / 2 + jitterMs))

/// Wait until `probe` returns the fact, or the deadline expires.
/// `probe` must be idempotent: it is called repeatedly and only the returned value is used.
let await (budget: TimeSpan) (describe: unit -> string) (probe: unit -> Task<Read<'a>>) : Task<'a> =
  task {
    let sw = Stopwatch.StartNew()
    let mutable last: Transient option = None
    let mutable observations = 0
    let mutable answer = ValueNone
    while answer.IsNone && sw.Elapsed < budget do
      let! read = safeProbe probe ()
      observations <- observations + 1
      match read with
      | Ok(Some value) -> answer <- ValueSome value
      | Ok None -> last <- None
      | Error t -> last <- Some t
      if answer.IsNone then do! Task.Delay(Backoff.delay observations)
    return
      match answer with
      | ValueSome value -> value
      | ValueNone ->
        raise (WaitTimedOut { Description = describe (); Budget = budget; LastTransient = last; Observations = observations })
  }

/// Wait until a boolean predicate holds.
let awaitTrue (budget: TimeSpan) (describe: unit -> string) (probe: unit -> Task<bool>) : Task<unit> =
  await budget describe (fun () -> task {
    let! ok = probe ()
    return if ok then Ok(Some()) else Ok None })

/// Lift a one-shot function that returns an option into a probe.
let ofOption (get: unit -> Task<'a option>) : unit -> Task<Read<'a>> =
  fun () -> task {
    let! value = get ()
    return Ok value }

/// The budget for a correctness wait: derived from one named source so a loaded machine cannot turn a
/// passing run into a failure. Re-exported here so callers never choose their own number.
let correctnessBudget = SageFs.Tests.TestTimeouts.heavyVerdictBudget
