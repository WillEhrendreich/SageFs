module SageFs.Features.MessageJournal

open System
open SageFs

/// Severity level for journal entries.
[<RequireQualifiedAccess>]
type JournalLevel =
  | Debug
  | Info
  | Warn
  | Failure

module JournalLevel =
  let label = function
    | JournalLevel.Debug -> "DEBUG"
    | JournalLevel.Info -> "INFO"
    | JournalLevel.Warn -> "WARN"
    | JournalLevel.Failure -> "ERROR"

  let severity = function
    | JournalLevel.Debug -> 0
    | JournalLevel.Info -> 1
    | JournalLevel.Warn -> 2
    | JournalLevel.Failure -> 3

  /// The level of a recorded eval, from the result text the eval path stored. The history keeps only that text,
  /// and the eval path marks a failed eval by starting it with "Error:" (Mcp.fs), the same mark the eval
  /// timeline reads. A warning has no mark of its own there, so an eval that did not fail is Info.
  let ofEvalResult (result: string) : JournalLevel =
    match result.StartsWith("Error:", StringComparison.Ordinal) with
    | true -> JournalLevel.Failure
    | false -> JournalLevel.Info

/// A single journal entry with timestamp, level, source, and message.
type JournalEntry = {
  Timestamp: DateTimeOffset
  Level: JournalLevel
  Source: string
  Message: string
}

module JournalEntry =
  let format (entry: JournalEntry) : string =
    sprintf "[%s] %s [%s] %s"
      (entry.Timestamp.ToString("HH:mm:ss.fff"))
      (JournalLevel.label entry.Level)
      entry.Source
      entry.Message

/// Aggregate stats for a journal.
type JournalStats = {
  Total: int
  DebugCount: int
  InfoCount: int
  WarnCount: int
  ErrorCount: int
  Evicted: int64
}

/// Immutable ring-buffer-backed message journal.
type Journal = {
  Buffer: RingBuffer.RingBuffer<JournalEntry>
}

module Journal =
  /// Create a journal with the given capacity.
  let create (capacity: int) : Journal =
    { Buffer = RingBuffer.create capacity }

  /// An entry stamped with the clock now.
  let entryNow (level: JournalLevel) (source: string) (message: string) : JournalEntry =
    { Timestamp = DateTimeOffset.UtcNow
      Level = level
      Source = source
      Message = message }

  /// Record a new entry.
  let record (level: JournalLevel) (source: string) (message: string) (journal: Journal) : Journal =
    { Buffer = RingBuffer.push (entryNow level source message) journal.Buffer }

  /// Record entries that already carry their own time and level, oldest first, with one copy of the ring.
  /// A journal built from history uses this, so each entry keeps the moment it happened.
  let recordEntries (entries: JournalEntry seq) (journal: Journal) : Journal =
    { Buffer = RingBuffer.pushAll entries journal.Buffer }

  /// Record one entry per message, oldest message first, all at one level and
  /// source, every one stamped with the clock now. For messages happening now;
  /// never for history, whose entries have their own times (`recordEntries`).
  let recordAll (level: JournalLevel) (source: string) (messages: string seq) (journal: Journal) : Journal =
    { Buffer = RingBuffer.pushAll (messages |> Seq.map (entryNow level source)) journal.Buffer }

  /// Number of entries currently in the journal.
  let count (journal: Journal) : int =
    RingBuffer.count journal.Buffer

  /// All entries, newest first.
  let entries (journal: Journal) : JournalEntry list =
    RingBuffer.toList journal.Buffer

  /// Filter entries by exact level.
  let filterByLevel (level: JournalLevel) (journal: Journal) : JournalEntry list =
    entries journal |> List.filter (fun e -> e.Level = level)

  /// Filter entries by minimum severity level.
  let filterByMinLevel (minLevel: JournalLevel) (journal: Journal) : JournalEntry list =
    let minSev = JournalLevel.severity minLevel
    entries journal |> List.filter (fun e -> JournalLevel.severity e.Level >= minSev)

  /// Filter entries by source.
  let filterBySource (source: string) (journal: Journal) : JournalEntry list =
    entries journal |> List.filter (fun e -> e.Source = source)

  /// Format all entries as a multi-line string.
  let formatAll (journal: Journal) : string =
    entries journal
    |> List.map JournalEntry.format
    |> String.concat "\n"

  /// Aggregate statistics.
  let stats (journal: Journal) : JournalStats =
    let all = entries journal
    { Total = all.Length
      DebugCount = all |> List.filter (fun e -> e.Level = JournalLevel.Debug) |> List.length
      InfoCount = all |> List.filter (fun e -> e.Level = JournalLevel.Info) |> List.length
      WarnCount = all |> List.filter (fun e -> e.Level = JournalLevel.Warn) |> List.length
      ErrorCount = all |> List.filter (fun e -> e.Level = JournalLevel.Failure) |> List.length
      Evicted = RingBuffer.evictedCount journal.Buffer }
