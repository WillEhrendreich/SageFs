namespace SageFs

open System.Threading.Tasks
open FuzzySharp
open SageFs.Features.AutoCompletion

/// Where a qualified name stops resolving. An agent holding a name that no longer resolves (renamed, moved, typed
/// from memory) is told which prefix the compiler does list, which segment it does not, and the names that sit
/// there instead. The walk takes the member lister as a function, so a test and a simulation run with no session.
module McpNamePath =

  [<RequireQualifiedAccess>]
  type NamePathResolution =
    /// Every segment resolved; `kind` is what the last segment's parent calls it.
    | Resolved of kind: CompletionKind
    /// The name is one segment and lists members, so it resolves with no parent to give it a kind.
    | ResolvedRoot
    /// The path resolves as far as `resolvedPrefix`. Its members are all listed, and `missing` is not among them.
    | StopsAt of resolvedPrefix: string * missing: string * nearest: string list
    /// As `StopsAt`, but the parent's list was cut at the wire ceiling, so `missing` may be past the cut.
    | StopsAtCutList of resolvedPrefix: string * missing: string * nearest: string list
    /// Not even the first segment lists any member.
    | NothingResolves of first: string
    /// A member list could not be fetched, so how far the path resolves is not known.
    | Unknown of at: string * reason: CompletionUnavailable

  /// The names closest to `wanted`, best first. A candidate under `SageFsConfig.NearestNameFloor` is noise and is
  /// dropped, so a path that resembles nothing there names nothing instead of the first few names alphabetically.
  let nearestNames (cap: int) (wanted: string) (candidates: string list) : string list =
    let lowered = wanted.ToLowerInvariant()
    candidates
    |> List.map (fun name -> name, Fuzz.Ratio(lowered, name.ToLowerInvariant()))
    |> List.filter (fun (_, score) -> score >= SageFsConfig.NearestNameFloor)
    |> List.sortBy (fun (name, score) -> -score, name)
    |> List.truncate (max 0 cap)
    |> List.map fst

  let segmentsOf (qualifiedName: string) : string list =
    qualifiedName.Split('.') |> List.ofArray

  /// Walk the name left to right, asking `membersOf` for each prefix, and stop at the first segment its parent
  /// does not list. One call per prefix, so a path of n segments costs n calls at most.
  let resolve
    (cap: int)
    (membersOf: string -> Task<CompletionAnswer<CompletionEntry>>)
    (qualifiedName: string)
    : Task<NamePathResolution> =
    // `prefix` resolved and listed `entries`; look for `next` among them.
    let rec step
      (prefix: string)
      (entries: CompletionEntry list)
      (total: CompletionTotal)
      (next: string)
      (following: string list)
      : Task<NamePathResolution> =
      task {
        match entries |> List.tryFind (fun e -> e.DisplayText = next) with
        | None ->
          let nearest = nearestNames cap next (entries |> List.map (fun e -> e.DisplayText))
          match total with
          | CompletionTotal.Exact _ -> return NamePathResolution.StopsAt(prefix, next, nearest)
          | CompletionTotal.AtLeast _ -> return NamePathResolution.StopsAtCutList(prefix, next, nearest)
        | Some entry ->
          match following with
          | [] -> return NamePathResolution.Resolved entry.Kind
          | after :: remaining ->
            let childPrefix = prefix + "." + next
            let! childAnswer = membersOf childPrefix
            match childAnswer with
            | CompletionAnswer.Unavailable reason -> return NamePathResolution.Unknown(childPrefix, reason)
            | CompletionAnswer.Answered([], _) -> return NamePathResolution.StopsAt(childPrefix, after, [])
            | CompletionAnswer.Answered(childEntries, childTotal) ->
              return! step childPrefix childEntries childTotal after remaining
      }
    task {
      match segmentsOf qualifiedName with
      | [] -> return NamePathResolution.NothingResolves qualifiedName
      | first :: rest ->
        let! rootAnswer = membersOf first
        match rootAnswer with
        | CompletionAnswer.Unavailable reason -> return NamePathResolution.Unknown(first, reason)
        | CompletionAnswer.Answered([], _) -> return NamePathResolution.NothingResolves first
        | CompletionAnswer.Answered(entries, total) ->
          match rest with
          | [] -> return NamePathResolution.ResolvedRoot
          | next :: following -> return! step first entries total next following
    }
