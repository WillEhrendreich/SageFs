/// One absolute deadline for a gate pass, shared by every tier and every nested wait.
///
/// Lifted from Molina's TimeoutBudget: a load-aware factor from /proc/loadavg, capped at 8x and never
/// below the declared value, plus SAGEFS_TEST_DEADLINE_UNIX propagated so an inner wait gets a fraction
/// of what is left instead of starting its own clock. A nested wait can never outlive its caller.
module SageFs.Tests.LoadBudget

open System
open System.IO

[<Literal>]
let DeadlineEnvironment = "SAGEFS_TEST_DEADLINE_UNIX"

[<Literal>]
let FactorEnvironment = "SAGEFS_TEST_LOAD_FACTOR"

/// Cap from Molina's LoadFactor: beyond 8x the machine is not merely loaded, it is broken, and waiting
/// longer hides that. Below 1x a loaded machine would turn a pass into a fail.
let private maxFactor = 8.0

/// The load factor for one CPU count. Pure so the ratchet can test it with synthetic readings.
let factorFor (loadAverage: float) (cores: int) : float =
  let c = max 1 cores |> float
  let perCore = loadAverage / c
  min maxFactor (max 1.0 perCore)

/// Read the machine's one-minute load average. Unreadable means 0: no evidence of load.
let currentLoadAverage () : float =
  try
    match File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries) with
    | [| first; _; _; _; _ |] ->
      match Double.TryParse(first, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
      | true, v -> v
      | _ -> 0.0
    | _ -> 0.0
  with _ -> 0.0

let currentCores () = Environment.ProcessorCount

/// The factor used for this run: an explicit override wins, otherwise the measured load.
let currentFactor () : float =
  match Environment.GetEnvironmentVariable FactorEnvironment with
  | null | "" -> factorFor (currentLoadAverage ()) (currentCores ())
  | text ->
    match Double.TryParse(text, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
    | true, v when v >= 1.0 -> min maxFactor v
    | _ -> 1.0

/// One deadline for the whole pass. CI publishes it; a developer running the assembly by hand gets
/// the same single ceiling from the runner, so no individual wait has to invent its own.
let private defaultPassBudget = TimeSpan.FromMinutes 90.

let ensureDeadline () =
  match Environment.GetEnvironmentVariable DeadlineEnvironment with
  | null | "" ->
    let at = DateTimeOffset.UtcNow + defaultPassBudget
    Environment.SetEnvironmentVariable(DeadlineEnvironment, string (at.ToUnixTimeSeconds()))
  | _ -> ()

/// The single deadline for this pass, if one was propagated.
let deadline () : DateTimeOffset option =
  match Environment.GetEnvironmentVariable DeadlineEnvironment with
  | null | "" -> None
  | text ->
    match Int64.TryParse text with
    | true, seconds -> Some(DateTimeOffset.FromUnixTimeSeconds seconds)
    | _ -> None

/// Time left before the shared deadline, floored at zero.
let remaining () : TimeSpan =
  match deadline () with
  | None -> TimeSpan.MaxValue
  | Some d -> let left = d - DateTimeOffset.UtcNow in if left < TimeSpan.Zero then TimeSpan.Zero else left

/// A wait's budget when the runner has published a pass deadline: a fraction of what is left.
/// There is no private per-wait clock here. If no deadline was published (a developer running a
/// single case by hand), the wait has no ceiling; the runner's own bound applies in CI.
let share (fraction: float) : TimeSpan =
  let f = min 1.0 (max 0.0 fraction)
  match deadline () with
  | None -> TimeSpan.MaxValue
  | Some d ->
    let left = max TimeSpan.Zero (d - DateTimeOffset.UtcNow)
    TimeSpan.FromTicks(int64 (float left.Ticks * f))

/// A child wait's budget: the smaller of its share of the pass deadline and the load-scaled
/// declared value. Kept for waits whose product has its own bound; integration waits use `share`.
let innerBudget (fraction: float) (declared: TimeSpan) : TimeSpan =
  let scaled = TimeSpan.FromTicks(int64 (float declared.Ticks * currentFactor ()))
  let s = share fraction
  if scaled < s then scaled else s
