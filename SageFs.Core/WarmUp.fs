module SageFs.WarmUp

open System

/// Individual FCS diagnostic captured during a warmup open failure.
type WarmupFcsDiagnostic = {
  Message: string
  Severity: string
  ErrorNumber: int
  FileName: string option
  StartLine: int
  EndLine: int
  StartColumn: int
  EndColumn: int
}

module WarmupFcsDiagnostic =
  /// One line of `FS%04d ... — message` for a diagnostic. When the
  /// diagnostic carries no source file (warm-up opens are evaluated from
  /// stdin, not a file — see `toWarmupDiagnostics`), the location segment is
  /// omitted entirely rather than rendered as a fake "unknown" placeholder:
  /// the absence of a location is a real fact, not something to paper over.
  let formatLine (d: WarmupFcsDiagnostic) : string =
    match d.FileName with
    | Some fn -> sprintf "FS%04d %s:%d:%d — %s" d.ErrorNumber fn d.StartLine d.StartColumn d.Message
    | None -> sprintf "FS%04d — %s" d.ErrorNumber d.Message

  /// FSI's own exception message for a failed interaction is a generic
  /// wrapper ("Operation could not be completed due to earlier error(s)")
  /// regardless of what actually went wrong — true whether the failing
  /// statement was the first in the submission or came after an earlier one
  /// in the same batch. The real reason lives in the diagnostics FCS
  /// reported alongside it. Prefer the first Error-severity diagnostic's own
  /// message; fall back to the exception message only when FSI genuinely
  /// captured no diagnostics at all (a host-level exception, not a compile
  /// error).
  let pickErrorMessage (exceptionMessage: string) (diagnostics: WarmupFcsDiagnostic list) : string =
    diagnostics
    |> List.tryFind (fun d -> d.Severity = "error")
    |> Option.map (fun d -> d.Message)
    |> Option.defaultValue exceptionMessage

/// Whether an openable entity is an F# module or a namespace.
[<RequireQualifiedAccess>]
type OpenableKind =
  | Module
  | Namespace

module OpenableKind =
  let label = function
    | OpenableKind.Module -> "module"
    | OpenableKind.Namespace -> "namespace"

  let ofBool isModule =
    match isModule with
    | true -> OpenableKind.Module
    | false -> OpenableKind.Namespace

  let toBool = function
    | OpenableKind.Module -> true
    | OpenableKind.Namespace -> false

/// Represents a namespace or module that was opened during warmup.
type OpenedBinding = {
  Name: string
  Kind: OpenableKind
  Source: string
  DurationMs: float
}

/// Rich failure info for a single open that did not succeed.
type WarmupOpenFailure = {
  Name: string
  Kind: OpenableKind
  ErrorMessage: string
  Diagnostics: WarmupFcsDiagnostic list
  RetryCount: int
  DurationMs: float
}

module WarmupOpenFailure =
  /// The sentinel name AppState.fs's discovery-time warnings use (a missing
  /// project assembly, a partially-loaded one, or "nothing found to open")
  /// when folded into the same failed-opens list the dashboard/TUI render.
  /// Its `ErrorMessage` IS the actionable explanation already, so it gets no
  /// separate suggestion below.
  [<Literal>]
  let DiscoveryWarningName = "(auto-open discovery)"

  /// What to do about a genuine per-name open failure — one FSI actually
  /// attempted and rejected, as opposed to a discovery-time warning. Honest
  /// about what SageFs can and cannot know: a failed `open` alone doesn't
  /// say whether the name is a typo, was never built, or names a
  /// nested/private definition that only resolves fully qualified — so the
  /// suggestion covers the mechanical fix (rebuild + reset) and names the
  /// one class of cause SageFs itself knows it can never auto-open.
  let suggestedAction (f: WarmupOpenFailure) : string option =
    match f.Name = DiscoveryWarningName with
    | true -> None
    | false ->
      Some (
        sprintf
          "Run 'dotnet build' for the project, then hard_reset_fsi_session. If '%s' still doesn't resolve after that, it is not a public, top-level, fully-qualified name in any loaded assembly — SageFs cannot auto-open a nested or private module by its short name; open it explicitly and fully qualified from your own code instead."
          f.Name)

/// Phase timing breakdown for warmup.
type WarmupPhaseTiming = {
  ScanSourceFilesMs: int64
  ScanAssembliesMs: int64
  OpenNamespacesMs: int64
  TotalMs: int64
}

/// Returns true if the FSI open error is benign — the module resolved but
/// can't be opened (e.g. RequireQualifiedAccess). Types are still accessible
/// via qualified paths, so this isn't a real failure.
let isBenignOpenError (errorMsg: string) : bool =
  errorMsg.Contains("RequireQualifiedAccess")

/// Result of attempting to open a single namespace/module.
type OpenAttemptResult =
  | OpenSuccess of durationMs: float
  | OpenFailed of errorMessage: string * diagnostics: WarmupFcsDiagnostic list * durationMs: float

/// WHY — warmup used to report success without checking that project references
/// actually loaded into the FSI AppDomain, producing "Ready" sessions where every
/// open failed with 'not defined' (friction report 2026-08). Because — comparing
/// expected assembly names against actually-loaded names classifies the session's
/// real state so callers can fault it honestly instead of lying Ready.
type AssemblyLoadVerification =
  | AllExpectedLoaded
  /// Some references did not load; names exactly as expected-but-absent.
  | PartiallyLoaded of missing: string list
  /// Not one expected assembly loaded — the session cannot do anything useful.
  | NothingLoaded

let classifyAssemblyLoad
  (expectedAssemblyNames: string list)
  (loadedAssemblyNames: string list)
  : AssemblyLoadVerification =
  match expectedAssemblyNames with
  | [] -> AllExpectedLoaded
  | first :: _ ->
    let loaded = loadedAssemblyNames |> Set.ofList
    let missing = expectedAssemblyNames |> List.filter (fun n -> not (Set.contains n loaded))
    match missing with
    | [] -> AllExpectedLoaded
    | m when m.Length = expectedAssemblyNames.Length -> NothingLoaded
    | m -> PartiallyLoaded m

[<Literal>]
let DefaultOpenBatchSize = 8

let private mkOpenedBinding name kind durationMs = {
  Name = name
  Kind = kind
  Source = "warmup"
  DurationMs = durationMs
}

let private mkWarmupFailure
  defaultError
  (firstErrors: System.Collections.Generic.Dictionary<string, string * WarmupFcsDiagnostic list>)
  (retryCounts: System.Collections.Generic.Dictionary<string, int>)
  (name, kind) = {
  Name = name
  Kind = kind
  ErrorMessage =
    match firstErrors.TryGetValue(name) with
    | true, (errorMessage, _) -> errorMessage
    | _ -> defaultError
  Diagnostics =
    match firstErrors.TryGetValue(name) with
    | true, (_, diagnostics) -> diagnostics
    | _ -> []
  RetryCount =
    match retryCounts.TryGetValue(name) with
    | true, count -> count
    | _ -> 0
  DurationMs = 0.0
}

let private openWithRetryRichCore
  (maxRounds: int)
  (attemptRound: (string * OpenableKind) list -> (string * OpenableKind * OpenAttemptResult) list)
  (names: (string * OpenableKind) list)
  : OpenedBinding list * WarmupOpenFailure list =
  let firstErrors = System.Collections.Generic.Dictionary<string, string * WarmupFcsDiagnostic list>()
  let retryCounts = System.Collections.Generic.Dictionary<string, int>()

  let rec loop round remaining acc =
    match round > maxRounds || List.isEmpty remaining with
    | true ->
      let failures =
        remaining
        |> List.map (mkWarmupFailure "max retries exceeded" firstErrors retryCounts)
      acc, failures
    | false ->
      for name, _ in remaining do
        retryCounts.[name] <-
          match retryCounts.TryGetValue(name) with
          | true, count -> count + 1
          | _ -> 1

      let results = attemptRound remaining

      let succeeded =
        results
        |> List.choose (fun (name, kind, result) ->
          match result with
          | OpenSuccess durationMs -> Some (mkOpenedBinding name kind durationMs)
          | OpenFailed _ -> None)

      let failed =
        results
        |> List.choose (fun (name, kind, result) ->
          match result with
          | OpenFailed (errorMessage, diagnostics, _) ->
            match firstErrors.ContainsKey(name) with
            | false -> firstErrors.[name] <- errorMessage, diagnostics
            | true -> ()
            Some (name, kind)
          | OpenSuccess _ -> None)

      match List.isEmpty succeeded with
      | true ->
        let failures =
          failed
          |> List.map (mkWarmupFailure "unknown" firstErrors retryCounts)
        acc, failures
      | false ->
        loop (round + 1) failed (acc @ succeeded)

  loop 1 names []

module WarmupProgressLine =
  [<Literal>]
  let Prefix = "WARMUP_PROGRESS="

  let private hasValidCounts step total =
    step > 0 && total > 0 && step <= total

  let private hasValidMessage (message: string) =
    not (String.IsNullOrWhiteSpace message)

  let tryFormatPayload step total (message: string) =
    match hasValidCounts step total && hasValidMessage message with
    | true -> Some (sprintf "%d/%d %s" step total message)
    | false -> None

  let tryFormatLine step total (message: string) =
    tryFormatPayload step total message
    |> Option.map (fun payload -> Prefix + payload)

  let tryParsePayload (payload: string) =
    match String.IsNullOrWhiteSpace payload with
    | true -> None
    | false ->
      match payload.IndexOf('/') with
      | slashIdx when slashIdx > 0 ->
        match payload.IndexOf(' ', slashIdx) with
        | spaceIdx when spaceIdx > slashIdx ->
          let message = payload.[spaceIdx + 1..]
          match
            Int32.TryParse(payload[..slashIdx - 1]),
            Int32.TryParse(payload[slashIdx + 1..spaceIdx - 1])
          with
          | (true, step), (true, total) when hasValidCounts step total && hasValidMessage message ->
            Some (step, total, message)
          | _ -> None
        | _ -> None
      | _ -> None

  let tryParseLine (line: string) =
    match line.StartsWith(Prefix, StringComparison.Ordinal) with
    | true -> tryParsePayload (line.Substring Prefix.Length)
    | false -> None

/// Opens names iteratively with rich failure info.
/// opener: tries to open a name+kind, returns OpenAttemptResult.
/// Returns (succeeded with timing, failures with diagnostics).
let openWithRetryRich
  (maxRounds: int)
  (opener: string -> OpenableKind -> OpenAttemptResult)
  (names: (string * OpenableKind) list)
  : OpenedBinding list * WarmupOpenFailure list =
  openWithRetryRichCore
    maxRounds
    (fun remaining ->
      remaining
      |> List.map (fun (name, kind) -> name, kind, opener name kind))
    names

/// Opens names in chunks to reduce per-open interpreter overhead.
/// When a chunk fails, each item is retried individually so failures stay attributable.
let openWithRetryRichBatched
  (maxRounds: int)
  (batchSize: int)
  (batchOpener: (string * OpenableKind) list -> OpenAttemptResult)
  (singleOpener: string -> OpenableKind -> OpenAttemptResult)
  (names: (string * OpenableKind) list)
  : OpenedBinding list * WarmupOpenFailure list =
  let normalizedBatchSize =
    match batchSize > 0 with
    | true -> batchSize
    | false -> 1

  let attemptChunk chunk =
    match chunk with
    | [] -> []
    | [ name, kind ] -> [ name, kind, singleOpener name kind ]
    | _ ->
      match batchOpener chunk with
      | OpenSuccess durationMs ->
        let durationPerName = durationMs / float chunk.Length
        chunk
        |> List.map (fun (name, kind) -> name, kind, OpenSuccess durationPerName)
      | OpenFailed _ ->
        chunk
        |> List.map (fun (name, kind) -> name, kind, singleOpener name kind)

  openWithRetryRichCore
    maxRounds
    (fun remaining ->
      remaining
      |> List.chunkBySize normalizedBatchSize
      |> List.collect attemptChunk)
    names

/// Legacy adapter: Opens names iteratively, retrying failures until convergence.
/// Returns (succeeded, permanentFailures) where permanentFailures = (name, firstError).
let openWithRetry
  (maxRounds: int)
  (opener: string -> Result<unit, string>)
  (names: string list)
  : string list * (string * string) list =
  let firstErrors = System.Collections.Generic.Dictionary<string, string>()
  let rec loop round remaining acc =
    match round > maxRounds || List.isEmpty remaining with
    | true ->
      (acc, remaining |> List.map (fun n ->
        match firstErrors.TryGetValue(n) with
        | true, e -> n, e
        | _ -> n, "max retries exceeded"))
    | false ->
      let results =
        remaining
        |> List.map (fun name -> name, opener name)
      let succeeded =
        results
        |> List.choose (fun (n, r) ->
          match r with Ok () -> Some n | _ -> None)
      let failed =
        results
        |> List.choose (fun (n, r) ->
          match r with
          | Error e ->
            match firstErrors.ContainsKey(n) with
            | false -> firstErrors.[n] <- e
            | true -> ()
            Some (n, e)
          | _ -> None)
      match List.isEmpty succeeded with
      | true ->
        (acc, failed |> List.map (fun (n, _) ->
          n, (match firstErrors.TryGetValue(n) with true, e -> e | _ -> "unknown")))
      | false ->
        loop (round + 1) (failed |> List.map fst) (acc @ succeeded)
  loop 1 names []
