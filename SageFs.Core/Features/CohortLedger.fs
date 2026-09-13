namespace SageFs.Features

open SageFs.Cohort

/// The port `CohortOwner` (Features/CohortOwner.fs) uses to persist and
/// replay a cohort's ledger (Slice 1, cohort-integration-plan.md D1/D3).
/// `LedgerEntry` is `Cohort.fs`'s own type (Cohort.fs:802) — it is NEVER
/// redefined here, so the owner, the in-memory port, and the SQLite port
/// (SageFs/CohortLedger.fs, the daemon layer) all agree on one shape by
/// construction.
module CohortLedger =

  /// `Append`/`ReadAll` are the only two operations the owner needs: it never
  /// deletes or mutates a recorded entry (`Cohort.fs`'s ledger is append-only
  /// by design, §5.2). An implementation must make `Append` durable before it
  /// returns — the owner replies to its caller only after `Append` returns.
  type LedgerPort<'m> = {
    Append: LedgerEntry<'m> -> unit
    /// Every entry recorded so far, in `Seq` order.
    ReadAll: unit -> LedgerEntry<'m> list
  }

  /// An in-memory ledger for tests and for a daemon with no persistence
  /// configured. Not durable across process restarts — `ReadAll` after the
  /// process exits sees nothing, same as never having recorded anything.
  module InMemory =
    let create<'m> () : LedgerPort<'m> =
      let store = ResizeArray<LedgerEntry<'m>>()
      let sync = obj ()
      {
        Append = fun entry -> lock sync (fun () -> store.Add entry)
        ReadAll = fun () -> lock sync (fun () -> List.ofSeq store)
      }
