module SageFs.Features.MessageJournal

open System
open SageFs

/// Severity level for journal entries.
[<RequireQualifiedAccess>]
type JournalLevel =
  | Debug
  | Info
  | Warn
  | Error

module JournalLevel =
  let label = function
    | JournalLevel.Debug -> "DEBUG"
    | JournalLevel.Info -> "INFO"
    | JournalLevel.Warn -> "WARN"
    | JournalLevel.Error -> "ERROR"

  let severity = function
    | JournalLevel.Debug -> 0
    | JournalLevel.Info -> 1
    | JournalLevel.Warn -> 2
    | JournalLevel.Error -> 3

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

  /// Record a new entry.
  let record (level: JournalLevel) (source: string) (message: string) (journal: Journal) : Journal =
    let entry = {
      Timestamp = DateTimeOffset.UtcNow
      Level = level
      Source = source
      Message = message
    }
    { Buffer = RingBuffer.push entry journal.Buffer }

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
      ErrorCount = all |> List.filter (fun e -> e.Level = JournalLevel.Error) |> List.length
      Evicted = RingBuffer.evictedCount journal.Buffer }
