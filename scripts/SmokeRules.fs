/// The pure decisions behind scripts/smoke-test.fsx: how its arguments read, what each check's evidence means,
/// and how the evidence is worded. No IO, no clock, nothing past FSharp.Core and the BCL, so SageFs.Tests
/// compiles this same file and tests it (SmokeRulesTests.fs), and the script only does the talking to a daemon.
/// It must stay standalone: no `Timeouts`, no `SageFs.Json` (see StandaloneSourceTests).
module SmokeRules

open System

// ── arguments ────────────────────────────────────────────────────────────────

type SmokeOptions =
  { Sample: string
    DaemonTimeoutSeconds: int
    SessionWarmupSeconds: int
    Port: int
    DiagnosticsDir: string }

/// The sample the smoke test builds and opens a session on: small, standalone, not the repo's own big test project.
let defaultSample = "samples/from-csharp/SageFs.Samples.FromCSharp/SageFs.Samples.FromCSharp.fsproj"

let defaults : SmokeOptions =
  { Sample = defaultSample
    DaemonTimeoutSeconds = 15
    SessionWarmupSeconds = 45
    Port = 37749
    DiagnosticsDir = "smoke-diagnostics" }

type ArgError =
  | UnknownOption of string
  | MissingValue of option: string
  | NotAWholeNumber of option: string * value: string

let describeArgError = function
  | UnknownOption o -> sprintf "unknown option %s" o
  | MissingValue o -> sprintf "%s needs a value" o
  | NotAWholeNumber (o, v) -> sprintf "%s needs a whole number, not '%s'" o v

let usage =
  "usage: dotnet fsi scripts/smoke-test.fsx -- [--sample <fsproj>] [--daemon-timeout <seconds>] [--session-warmup <seconds>] [--port <n>] [--diagnostics-dir <dir>]"

let parseArgs (argv: string list) : Result<SmokeOptions, ArgError> =
  let number (option: string) (value: string) (apply: int -> SmokeOptions -> SmokeOptions) (rest: string list) (options: SmokeOptions) go =
    match Int32.TryParse value with
    | true, n -> go rest (apply n options)
    | _ -> Error(NotAWholeNumber(option, value))
  let rec go (argv: string list) (options: SmokeOptions) : Result<SmokeOptions, ArgError> =
    match argv with
    | [] -> Ok options
    | "--sample" :: value :: rest -> go rest { options with Sample = value }
    | "--diagnostics-dir" :: value :: rest -> go rest { options with DiagnosticsDir = value }
    | "--daemon-timeout" :: value :: rest -> number "--daemon-timeout" value (fun n o -> { o with DaemonTimeoutSeconds = n }) rest options go
    | "--session-warmup" :: value :: rest -> number "--session-warmup" value (fun n o -> { o with SessionWarmupSeconds = n }) rest options go
    | "--port" :: value :: rest -> number "--port" value (fun n o -> { o with Port = n }) rest options go
    | [ ("--sample" | "--diagnostics-dir" | "--daemon-timeout" | "--session-warmup" | "--port") as option ] -> Error(MissingValue option)
    | other :: _ -> Error(UnknownOption other)
  go argv defaults

// ── steps and verdicts ───────────────────────────────────────────────────────

/// The checks, in the order they run. The names are what the summary and the diagnostics call them.
type Step =
  | Dotnet
  | SagefsOnPath
  | DaemonStart
  | ApiVersion
  | Completions
  | TestsDiscovery
  | RunTests
  | MarkAllStale

let stepName = function
  | Dotnet -> "dotnet"
  | SagefsOnPath -> "sagefs-path"
  | DaemonStart -> "daemon-start"
  | ApiVersion -> "api-version"
  | Completions -> "completions"
  | TestsDiscovery -> "tests-discovery"
  | RunTests -> "run-tests"
  | MarkAllStale -> "mark-all-stale"

type Verdict =
  | Pass of string
  | Fail of string

let verdictWord = function
  | Pass _ -> "PASS"
  | Fail _ -> "FAIL"

let verdictMessage = function
  | Pass m
  | Fail m -> m

/// .NET 10 or newer is what the tool needs. `dotnet --version` prints like `11.0.100-rc.1.26425.128`.
let dotnetVerdict (versionText: string) : Verdict =
  match Int32.TryParse((versionText.Trim().Split '.').[0]) with
  | true, major when major >= 10 -> Pass(sprintf ".NET %s found" (versionText.Trim()))
  | true, _ -> Fail(sprintf ".NET %s found, need 10+" (versionText.Trim()))
  | _ -> Fail(sprintf "could not read a .NET version from '%s'" (versionText.Trim()))

// ── live testing discovery ───────────────────────────────────────────────────

/// What `GET /api/live-testing/status` says about test discovery. `Unreported` is an older daemon that says nothing.
type DiscoveryState =
  | ReadyWithTests
  | ReadyZeroTests
  | Disabled
  | Discovering
  | Unreported
  | Unrecognised of string

let parseDiscoveryState (reported: string option) : DiscoveryState =
  match reported with
  | None -> Unreported
  | Some "ready_with_tests" -> ReadyWithTests
  | Some "ready_zero_tests" -> ReadyZeroTests
  | Some "disabled" -> Disabled
  | Some "discovering" -> Discovering
  | Some other -> Unrecognised other

let discoveryStateText = function
  | ReadyWithTests -> "ready_with_tests"
  | ReadyZeroTests -> "ready_zero_tests"
  | Disabled -> "disabled"
  | Discovering -> "discovering"
  | Unreported -> ""
  | Unrecognised other -> other

/// Whether polling can stop: discovery has an answer (even "none" or "off"), or an older daemon that reports no
/// state has shown some tests.
let discoveryFinished (state: DiscoveryState) (discovered: int) : bool =
  match state with
  | ReadyWithTests
  | ReadyZeroTests
  | Disabled -> true
  | Unreported -> discovered > 0
  | Discovering
  | Unrecognised _ -> false

/// The numbers a live-testing status carries, for the one-line summary in a failure.
type LiveTestingFacts =
  { State: string option
    Hint: string option
    Counts: (string * int) list }

let liveTestingSummary (facts: LiveTestingFacts option) : string =
  match facts with
  | None -> "no live-testing status available"
  | Some f ->
    let parts =
      [ yield! f.State |> Option.map (sprintf "state=%s") |> Option.toList
        yield! f.Hint |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.map (sprintf "hint=%s") |> Option.toList
        for (name, value) in f.Counts -> sprintf "%s=%d" name value ]
    match parts with
    | [] -> "live-testing status had no summary fields"
    | _ -> String.Join(", ", parts)

/// What the discovery poll found, after at most `elapsedSeconds` of waiting.
let discoveryVerdict (state: DiscoveryState) (discovered: int) (elapsedSeconds: int) (hint: string) (latest: string) : Verdict =
  match state with
  | ReadyWithTests -> Pass(sprintf "Discovery completed with %d tests after %ds" discovered elapsedSeconds)
  | ReadyZeroTests -> Fail(sprintf "Discovery completed with zero tests after %ds. %s Latest: %s" elapsedSeconds hint latest)
  | Disabled -> Fail(sprintf "Live testing remained disabled after enable request. %s Latest: %s" hint latest)
  | Discovering -> Fail(sprintf "Discovery did not complete within %ds. %s Latest: %s" elapsedSeconds hint latest)
  | Unreported
  | Unrecognised _ ->
    match discovered > 0 with
    | true -> Pass(sprintf "Discovery completed with %d tests after %ds" discovered elapsedSeconds)
    | false -> Fail(sprintf "Could not confirm discovery completion and 0 tests were discovered after %ds. Latest: %s" elapsedSeconds latest)

// ── sessions and completions ─────────────────────────────────────────────────

/// One session as `GET /api/sessions` reports it, reduced to what the smoke test reads.
type SessionFacts = { Label: string; Status: string }

let sessionStatusSummary (sessions: SessionFacts list) : string =
  match sessions with
  | [] -> "no sessions reported"
  | _ -> String.Join(", ", sessions |> List.map (fun s -> sprintf "%s=%s" s.Label s.Status))

/// What the warmup poll should do after one look at the sessions.
type WarmupLook =
  | SessionReady of id: string
  | AllFaulted
  | KeepWaiting

/// Ready as soon as any session is; give up early when there are sessions and every one of them has faulted,
/// since waiting out the timeout on a fault would only be slower.
let warmupLook (sessions: (string * string) list) : WarmupLook =
  match sessions |> List.tryFind (fun (_, status) -> status = "Ready") with
  | Some (id, _) -> SessionReady id
  | None ->
    match sessions <> [] && sessions |> List.forall (fun (_, status) -> status = "Faulted") with
    | true -> AllFaulted
    | false -> KeepWaiting

/// The shapes `POST /api/completions` has answered in: a bare array, an object with the items and a count, or
/// text (which is an error when it starts with `Error:`).
type CompletionsReply =
  | ItemArray of count: int
  | CountedObject of count: int
  | Text of string
  | Unexpected of string

let completionsVerdict (reply: CompletionsReply) : Verdict =
  match reply with
  | ItemArray n when n > 0 -> Pass(sprintf "Completions returned %d items" n)
  | CountedObject n when n > 0 -> Pass(sprintf "Completions returned %d items" n)
  | Text text when not (text.StartsWith("Error:", StringComparison.Ordinal)) -> Pass "Completions returned results"
  | ItemArray _
  | CountedObject _ -> Fail "Completions returned unexpected: no items"
  | Text text
  | Unexpected text -> Fail(sprintf "Completions returned unexpected: %s" text)
