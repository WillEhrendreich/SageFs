namespace SageFs.Features

open SageFs
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
  ///
  /// THE PORT IS SCOPE-KEYED, NOT THE CONNECTION. Every operation names the
  /// cohort it is about, and an implementation is expected to keep cohorts at
  /// different scopes APART: one SQLite file, one table, a `scope` column and a
  /// `WHERE scope = ?` filter (see `SageFs/CohortLedger.fs`). Before this, one
  /// port WAS one cohort for the daemon's whole lifetime, which is why an agent in
  /// one repository and an agent in another shared a conductor seat — the store,
  /// not just the reducer, encoded "there is only one".
  ///
  /// `Seq` stays cohort-local and dense, not per-file: a per-file counter would
  /// make two cohorts' ledgers interleave into gaps and break the dense
  /// `LedgerHead.Seq`/`CohortFrame.Version` contract the scrubber and the
  /// inspector read.
  type LedgerPort<'m> = {
    /// Record one entry, for the cohort named by the entry's command.
    Append: LedgerEntry<'m> -> unit
    /// Every entry recorded for `scope` so far, in `Seq` order.
    ReadAll: CohortScope -> LedgerEntry<'m> list
    /// The scopes this store actually holds rows for, sorted. A caller starting a
    /// cohort for a scope with no rows gets `[]` — the store is what says whether
    /// there is history, which is what makes `replayIn` a real reconstruction
    /// rather than a guess.
    Scopes: unit -> CohortScope list
  }

  /// An in-memory ledger for tests and for a daemon with no persistence
  /// configured. Not durable across process restarts — `ReadAll` after the
  /// process exits sees nothing, same as never having recorded anything.
  ///
  /// Keeps ONE store per scope, not one per port: two ports over the same
  /// process are two views of the same cohorts, exactly as the SQLite port is.
  /// `InMemory.shared` is that process-wide store; `InMemory.create` gives a
  /// port bound to a FRESH one, which is what a test that wants an empty ledger
  /// needs (two `create`s were two ledgers, which was the point).
  module InMemory =

    /// The process-wide store behind every `shared` port. A `Dictionary` keyed by
    /// canonical scope label (never by the DU value) so two spellings of one
    /// repository cannot end up in two buckets.
    type Store<'m>() =
      let rows = System.Collections.Generic.Dictionary<string, ResizeArray<LedgerEntry<'m>>>()
      let sync = obj ()

      member _.Append(entry: LedgerEntry<'m>) =
        let key = Scope.label (Cohort.scopeOf entry.Command)
        lock sync (fun () ->
          match rows.TryGetValue key with
          | true, bucket -> bucket.Add entry
          | false, _ -> rows.Add(key, ResizeArray([ entry ])))

      member _.ReadAll(scope: CohortScope) =
        let key = Scope.label scope
        lock sync (fun () ->
          match rows.TryGetValue key with
          | true, bucket -> List.ofSeq bucket
          | false, _ -> [])

      member _.Scopes() =
        lock sync (fun () ->
          rows.Keys |> Seq.sort |> Seq.map Cohort.scopeOfLabel |> List.ofSeq)

    /// A port over a brand-new, empty store — the isolation a test that wants a
    /// blank ledger needs.
    let create<'m> () : LedgerPort<'m> =
      let store = Store<'m>()
      { Append = store.Append
        ReadAll = store.ReadAll
        Scopes = store.Scopes }

    /// A port over the process-wide store. Two callers using `shared` are two
    /// views of one set of cohorts, which is what lets a test drive a cohort
    /// through one port and read it back through another.
    let private sharedStores = System.Collections.Generic.Dictionary<System.Type, obj>()
    let private sharedSync = obj ()

    let shared<'m> () : LedgerPort<'m> =
      let key = typeof<'m>
      let store =
        lock sharedSync (fun () ->
          match sharedStores.TryGetValue key with
          | true, existing -> existing
          | false, _ ->
            let fresh = Store<'m>() :> obj
            sharedStores.Add(key, fresh)
            fresh)
      let typed = store :?> Store<'m>
      { Append = typed.Append
        ReadAll = typed.ReadAll
        Scopes = typed.Scopes }
