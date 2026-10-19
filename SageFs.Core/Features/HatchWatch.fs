namespace SageFs.Features

open System
open SageFs

/// When the last use of the hatch was reported.
[<RequireQualifiedAccess>]
type HatchSeen =
  | NeverSeen
  | LastSeenAt of DateTimeOffset

/// How many times the hatch was used since `Since`, by kind. The sagefs-repl-guard hook reports each `dotnet build`,
/// `test`, `run` or `fsi` it lets through while the daemon is up, so "twenty agents all fell back today" is a number
/// the owner reads off `get_daemon_status` instead of finding out by asking.
type HatchLedger =
  { Since: DateTimeOffset
    Counts: Map<HatchUse, int>
    Last: HatchSeen }

module HatchLedger =
  let empty (since: DateTimeOffset) : HatchLedger =
    { Since = since; Counts = Map.empty; Last = HatchSeen.NeverSeen }

  /// One more use of the hatch, at `now`.
  let record (now: DateTimeOffset) (hatch: HatchUse) (ledger: HatchLedger) : HatchLedger =
    ledger

  let count (hatch: HatchUse) (ledger: HatchLedger) : int =
    ledger.Counts |> Map.tryFind hatch |> Option.defaultValue 0

  /// Every kind of use with its count, zeros included, in `HatchUse.all` order, so the wire shape does not depend on
  /// what happened to be seen.
  let rows (ledger: HatchLedger) : (HatchUse * int) list =
    HatchUse.all |> List.map (fun hatch -> hatch, count hatch ledger)

  let total (ledger: HatchLedger) : int =
    ledger.Counts |> Map.fold (fun sum _ n -> sum + n) 0

/// The one live `HatchLedger` for this daemon run: the stateful shell the HTTP route writes through and
/// `get_daemon_status` reads, the same split as `LeaseWatch` and its pure `ExpensiveWorkLease`.
module HatchWatch =
  let mutable private ledger = HatchLedger.empty (Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime() |> DateTimeOffset)
  let private gate = obj ()

  let record (hatch: HatchUse) : unit =
    lock gate (fun () -> ledger <- HatchLedger.record DateTimeOffset.UtcNow hatch ledger)

  let snapshot () : HatchLedger = lock gate (fun () -> ledger)
