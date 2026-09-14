namespace SageFs.Features

open System
open System.IO
open SageFs
open SageFs.Cohort
open SageFs.MemberTable

/// Portable JSONL export/import of a cohort ledger (sagefs-multiagent-vision.md
/// §5.2, Phase 1 item 18a: the foundation of `sagefs record`/`play` — "For every
/// checked-in SageFs.Tests/cohorts/*.ledger.jsonl, replaying the recorded
/// commands reconstructs the same state").
///
/// One self-contained JSON object per line, using the SAME
/// `JsonFSharpConverter`-configured codec `SageFs/CohortLedger.fs` (the SQLite
/// port) already proves round-trips every `CohortCommand`/`CohortEvent` DU
/// shape (`WorkerProtocol.Serialization`). Serializing the WHOLE
/// `LedgerEntry<MemberId>` per line — rather than a hand-rolled line record —
/// was verified lossless (`CohortLedgerExportTests.fs`'s round-trip property)
/// because none of `LedgerEntry`'s three "tricky" fields need special handling
/// through this codec:
/// - `Seq: int64<ledgerSeq>` erases to a plain `System.Int64` at runtime (units
///   of measure are compile-time only), so it serializes as an ordinary JSON
///   number and deserializes back into the measured field with no converter.
/// - `Clock: DateTime` round-trips through System.Text.Json's default ISO-8601
///   converter, which preserves `DateTimeKind` (ledger clocks are always UTC).
/// - `Entropy: byte[]` round-trips as a base64 string, System.Text.Json's
///   default `byte[]` encoding.
///
/// So this module stays a thin, honest wrapper: no bespoke wire schema to keep
/// in sync with `Cohort.fs`, no second place a new `CohortCommand`/
/// `CohortEvent` case has to be taught to serialize.
module CohortLedgerExport =

  /// One `LedgerEntry<MemberId>` per line, `\n`-separated. No file IO here —
  /// `exportToFile` below is the thin wrapper for that.
  let toJsonl (entries: LedgerEntry<MemberId> list) : string =
    entries
    |> List.map WorkerProtocol.Serialization.serialize<LedgerEntry<MemberId>>
    |> String.concat "\n"

  /// Fail closed (never silently drops a bad line): splits on `\n`, skips
  /// blank lines (including a trailing one — a file written by `toJsonl` plus
  /// an editor's final newline is still valid), and deserializes every
  /// non-blank line in order. The first line that fails to parse aborts the
  /// whole import and names its 1-based line number and the reason — a
  /// corrupt tail must never produce a silently-truncated ledger. Never
  /// throws: every deserialization failure is caught and turned into `Error`.
  let fromJsonl (jsonl: string) : Result<LedgerEntry<MemberId> list, string> =
    if String.IsNullOrEmpty jsonl then
      Ok []
    else
      jsonl.Split '\n'
      |> Array.mapi (fun i line -> i + 1, line)
      |> Array.filter (fun (_, line) -> not (String.IsNullOrWhiteSpace line))
      |> Array.fold
        (fun acc (lineNo, line) ->
          match acc with
          | Error _ -> acc
          | Ok entries ->
            try
              let entry = WorkerProtocol.Serialization.deserialize<LedgerEntry<MemberId>> (line.Trim())
              Ok(entry :: entries)
            with ex ->
              Error(sprintf "line %d: %s" lineNo ex.Message))
        (Ok [])
      |> Result.map List.rev

  /// Convenience wrapper — the codec itself (`toJsonl`/`fromJsonl`) stays pure
  /// string<->entries; this just owns the file write.
  let exportToFile (path: string) (entries: LedgerEntry<MemberId> list) : unit =
    File.WriteAllText(path, toJsonl entries)

  /// Convenience wrapper — see `exportToFile`.
  let importFromFile (path: string) : Result<LedgerEntry<MemberId> list, string> =
    File.ReadAllText path |> fromJsonl
