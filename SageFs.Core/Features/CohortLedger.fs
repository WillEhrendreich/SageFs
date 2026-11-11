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
    /// The `Seq` the next append for `scope` takes: one past the highest number the store holds for it, counting rows
    /// this build cannot read. `ReadAll` skips such a row and leaves it on disk, so a number taken from what `ReadAll`
    /// returned reused the skipped row's and every append after an upgrade hit the store's key.
    NextSeq: CohortScope -> int64<Measures.ledgerSeq>
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

      member _.NextSeq(scope: CohortScope) : int64<Measures.ledgerSeq> =
        let key = Scope.label scope
        lock sync (fun () ->
          match rows.TryGetValue key with
          | true, bucket when bucket.Count > 0 -> (bucket |> Seq.map (fun e -> e.Seq) |> Seq.max) + 1L<Measures.ledgerSeq>
          | _ -> 0L<Measures.ledgerSeq>)

      member _.Scopes() =
        lock sync (fun () ->
          rows.Keys |> Seq.sort |> Seq.map Cohort.scopeOfLabel |> List.ofSeq)

    /// A port over a brand-new, empty store — the isolation a test that wants a
    /// blank ledger needs.
    let create<'m> () : LedgerPort<'m> =
      let store = Store<'m>()
      { Append = store.Append
        ReadAll = store.ReadAll
        NextSeq = store.NextSeq
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
        NextSeq = typed.NextSeq
        Scopes = typed.Scopes }

  /// A read of the whole ledger that costs a lookup when nothing was appended.
  ///
  /// The dashboard reads the cohort ledger on every push of every open tab, and the SQLite port opens a connection and
  /// scans every row of the scope each time (one 10,000-row ledger and three tabs is thirty scans per model change). The
  /// ledger is append-only and the owner is its only writer, so a count of appends is a complete change marker: this wrapper
  /// bumps one per scope on `Append` and keeps the last list read for a scope until that scope's count moves.
  module ReadCache =

    /// `inner` with `ReadAll` answered from the last read of the scope while no entry has been appended to it since.
    /// `Append` goes to `inner` first, so a read that sees the new count also sees the durable row; the cache is dropped
    /// only after the append returned.
    ///
    /// A read that raced an append (the count moved while `inner.ReadAll` ran) is returned to its caller and not kept, so a
    /// list that may lack the new row is never served to the next one.
    let wrap<'m> (inner: LedgerPort<'m>) : LedgerPort<'m> =
      let sync = obj ()
      let appended = System.Collections.Generic.Dictionary<string, int64>()
      let cache = System.Collections.Generic.Dictionary<string, int64 * LedgerEntry<'m> list>()
      let appendsTo (key: string) : int64 =
        match appended.TryGetValue key with
        | true, count -> count
        | false, _ -> 0L
      let append (entry: LedgerEntry<'m>) : unit =
        let key = Scope.label (Cohort.scopeOf entry.Command)
        inner.Append entry
        lock sync (fun () ->
          appended.[key] <- appendsTo key + 1L
          cache.Remove key |> ignore)
      let readAll (scope: CohortScope) : LedgerEntry<'m> list =
        let key = Scope.label scope
        let marker, hit =
          lock sync (fun () ->
            let marker = appendsTo key
            match cache.TryGetValue key with
            | true, (seen, entries) when seen = marker -> marker, Some entries
            | _ -> marker, None)
        match hit with
        | Some entries -> entries
        | None ->
          let fresh = inner.ReadAll scope
          lock sync (fun () ->
            match appendsTo key = marker with
            | true -> cache.[key] <- (marker, fresh)
            | false -> ())
          fresh
      { Append = append
        ReadAll = readAll
        NextSeq = inner.NextSeq
        Scopes = inner.Scopes }
