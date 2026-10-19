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
  let count (hatch: HatchUse) (ledger: HatchLedger) : int =
    ledger.Counts |> Map.tryFind hatch |> Option.defaultValue 0

  let record (now: DateTimeOffset) (hatch: HatchUse) (ledger: HatchLedger) : HatchLedger =
    { ledger with
        Counts = ledger.Counts |> Map.add hatch (count hatch ledger + 1)
        Last = HatchSeen.LastSeenAt now }

  /// Every kind of use with its count, zeros included, in `HatchUse.all` order, so the wire shape does not depend on
  /// what happened to be seen.
  let rows (ledger: HatchLedger) : (HatchUse * int) list =
    HatchUse.all |> List.map (fun hatch -> hatch, count hatch ledger)

  let total (ledger: HatchLedger) : int =
    ledger.Counts |> Map.fold (fun sum _ n -> sum + n) 0

/// The `hatch` section of `get_daemon_status`: when counting began, how many uses, when the last one was, and a row
/// for every kind. The keys are written in `SageFs.Json`'s order, which is alphabetical.
module HatchWire =
  let section (ledger: HatchLedger) =
    {| since = ledger.Since
       total = HatchLedger.total ledger
       lastRecordedAt =
         match ledger.Last with
         | HatchSeen.NeverSeen -> box null
         | HatchSeen.LastSeenAt at -> box at
       uses =
         HatchLedger.rows ledger
         |> List.map (fun (hatch, count) ->
           let declaration, repl = HatchUse.toTokens hatch
           {| declaration = declaration; repl = repl; count = count |})
         |> List.toArray |}

/// The one live `HatchLedger` for this daemon run: the stateful shell the HTTP route writes through and
/// `get_daemon_status` reads, the same split as `LeaseWatch` and its pure `ExpensiveWorkLease`.
module HatchWatch =
  let mutable private ledger = HatchLedger.empty (Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime() |> DateTimeOffset)
  let private gate = obj ()

  let record (hatch: HatchUse) : unit =
    lock gate (fun () -> ledger <- HatchLedger.record DateTimeOffset.UtcNow hatch ledger)

  let snapshot () : HatchLedger = lock gate (fun () -> ledger)
