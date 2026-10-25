module SageFs.Tests.CompletionFixtures

/// Builders for the completion answers a worker sends, so a test states the kind and the names it cares
/// about and not the record plumbing around them.
open SageFs.Features.AutoCompletion

let entry (kind: CompletionKind) (name: string) : CompletionEntry =
  { DisplayText = name; ReplacementText = name; Kind = kind }

/// Entries that share one kind.
let entriesOf (kind: CompletionKind) (names: string list) : CompletionEntry list =
  names |> List.map (entry kind)

/// A worker answer that listed exactly these entries.
let exactly (entries: CompletionEntry list) : CompletionAnswer<CompletionEntry> =
  CompletionAnswer.Answered(entries, CompletionTotal.Exact entries.Length)

/// A worker answer whose names all have one kind.
let namesOf (kind: CompletionKind) (names: string list) : CompletionAnswer<CompletionEntry> =
  exactly (entriesOf kind names)

/// A worker answer from names alone, when the test does not care about their kind.
let plainNames (names: string list) : CompletionAnswer<CompletionEntry> =
  namesOf CompletionKind.Variable names

/// The `UpTo` limit an editor uses, for tests that send a `GetCompletions`.
let editorLimit : CompletionLimit = CompletionLimit.UpTo EditorCandidates
