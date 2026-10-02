/// Turns one run's stream plus the harness's own measurements into an outcome from the
/// closed set and a `fellOver` list. Pure: no files, no network, so every rule is a test.
module LemScore.Classify

open System
open System.Text.Json
open System.Text.RegularExpressions
open LemScore.Types
open LemScore.CmdcStream

/// Facts the harness measured itself, outside the sandbox.
type RunFacts =
  { Stream: RunStream
    CmdcExit: int
    Oracle: OracleVerdict
    /// Output of the oracle, kept as evidence when it fails.
    OracleOutput: string
    Cleanup: Cleanup
    /// Sessions the lemming left under its run directory (read before the cleanup stopped them).
    ResidueSessions: string list }

type ProviderKind =
  | RateLimited
  | InsufficientCredits
  | ConnectionFailed
  | ServerFailed

module ProviderKind =
  let toString (k: ProviderKind) : string =
    match k with
    | RateLimited -> "rate-limited"
    | InsufficientCredits -> "insufficient-credits"
    | ConnectionFailed -> "connection"
    | ServerFailed -> "server"

type Assessment =
  { Outcome: Outcome
    Reason: string
    Provider: ProviderKind option
    FellOver: FellOver list }

// ---- tool and error vocabulary -----------------------------------------------------------

let stageOfTool (tool: string) : Stage =
  match tool with
  | "create_project_session" | "create_solution_session" | "create_bare_session" -> SessionCreate
  | "get_session_status" -> SessionWarmup
  | "send_fsharp_code" | "check_fsharp_code" | "get_recent_fsi_events" | "cancel_eval" -> Eval
  | "hard_reset_fsi_session" | "reset_fsi_session" -> Reload
  | "run_tests" | "list_tests" | "targeted_verify" | "explain_test_failure" | "coverage_intel" -> Tests
  | "acquire_full_build_lease" | "acquire_test_suite_lease" | "acquire_run_app_lease" | "release_work_lease" -> Lease
  | "stop_session" -> Cleanup
  | _ -> OtherTool

let private errorPrefixes = [ "Error"; "Failed"; "Cannot"; "Blocked"; "Rejected"; "Unable"; "Invalid" ]

/// SageFs' agent-facing errors end with " → Next: <action>" (SageFsError.describeForAgent).
let private nextHint = " → Next: "

/// Plain "success: false" or a non-null "error" in a JSON reply.
let private jsonSaysFailed (text: string) : bool =
  match text.TrimStart().StartsWith "{" with
  | false -> false
  | true ->
    try
      use doc = JsonDocument.Parse text
      let root = doc.RootElement
      let successFalse =
        match root.TryGetProperty "success" with
        | true, v -> v.ValueKind = JsonValueKind.False
        | _ -> false
      let errorSet =
        match root.TryGetProperty "error" with
        | true, v -> v.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(v.GetString()))
        | _ -> false
      successFalse || errorSet
    with :? JsonException -> false

/// Did this SageFs MCP call fail? An errored tool, a refused one, or a reply that is
/// phrased as a refusal. A plain F# compile error inside an eval is the lemming's own
/// bug and is not counted here.
let isSagefsError (call: ToolCall) : bool =
  match call.Outcome with
  | ToolErrored | ToolDeniedByPolicy -> true
  | ToolNeverFinished -> false
  | ToolSucceeded ->
    let text = call.Text.TrimStart()
    errorPrefixes |> List.exists (fun p -> text.StartsWith(p, StringComparison.OrdinalIgnoreCase))
    || call.Text.Contains nextHint
    || jsonSaysFailed call.Text

let sagefsCalls (stream: RunStream) : ToolCall list =
  stream.Calls |> List.filter isSagefsCall

let private firstLine (text: string) : string =
  text.Split('\n') |> Array.tryHead |> Option.defaultValue "" |> _.Trim()

let private clip (limit: int) (text: string) : string =
  match text.Length > limit with
  | true -> text.Substring(0, limit) + "..."
  | false -> text

let symptomLimit = 200
let evidenceLimit = 400

/// The shell command of a non-MCP tool call (cmdc names its shell tool shell_command).
let shellCommand (call: ToolCall) : string option =
  match isSagefsCall call with
  | true -> None
  | false ->
    try
      use doc = JsonDocument.Parse call.Input
      match doc.RootElement.TryGetProperty "command" with
      | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
      | _ -> None
    with :? JsonException -> None

let private dotnetLoop = Regex(@"\bdotnet\s+(build|test|run|fsi|msbuild)\b", RegexOptions.Compiled)

let private isSuccessfulEval (call: ToolCall) : bool =
  isSagefsCall call && sagefsToolName call = "send_fsharp_code" && call.Outcome = ToolSucceeded && not (isSagefsError call)

// ---- provider ---------------------------------------------------------------------------

let private quotaWords = Regex(@"today's limit|usage limit|rate limit|insufficient credits|quota", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

let providerKind (facts: RunFacts) : ProviderKind option =
  let errText =
    (facts.Stream.Result |> Option.bind _.Error |> Option.toList) @ facts.Stream.RunErrors
    |> String.concat " "
  match facts.CmdcExit with
  | e when e = CmdcExit.rateLimited -> Some RateLimited
  | e when e = CmdcExit.insufficientCredits -> Some InsufficientCredits
  | e when e = CmdcExit.connectionError -> Some ConnectionFailed
  | e when e = CmdcExit.serverError -> Some ServerFailed
  | _ when quotaWords.IsMatch errText -> Some RateLimited
  | _ -> None

// ---- fellOver ---------------------------------------------------------------------------

let private errorFellOvers (calls: ToolCall list) : FellOver list =
  calls
  |> List.filter (fun c -> isSagefsCall c && isSagefsError c)
  |> List.groupBy (fun c -> stageOfTool (sagefsToolName c), clip symptomLimit (firstLine (if String.IsNullOrWhiteSpace c.Text then "(no text)" else c.Text)))
  |> List.map (fun ((stage, symptom), group) ->
    let first = List.head group
    let times = if group.Length > 1 then sprintf " (x%d)" group.Length else ""
    { Stage = stage
      Symptom = symptom + times
      Evidence = clip evidenceLimit (sprintf "%s at turn %d, input %s" (sagefsToolName first) first.Turn first.Input) })

let private deniedFellOvers (calls: ToolCall list) : FellOver list =
  calls
  |> List.filter (fun c -> c.Outcome = ToolDeniedByPolicy)
  |> List.map _.Name
  |> List.distinct
  |> List.map (fun name ->
    { Stage = ToolSurface
      Symptom = sprintf "called a tool that does not exist: %s" name
      Evidence = "Command Code refused it as not among the tools offered" })

let private faultedFellOvers (calls: ToolCall list) : FellOver list =
  calls
  |> List.filter (fun c -> isSagefsCall c && sagefsToolName c = "get_session_status" && c.Text.Contains "Faulted")
  |> List.tryHead
  |> Option.map (fun c ->
    { Stage = SessionWarmup
      Symptom = "a session reported Faulted"
      Evidence = clip evidenceLimit (sprintf "turn %d: %s" c.Turn (firstLine c.Text)) })
  |> Option.toList

/// Three identical calls in a row is a loop, whatever the tool.
let loopRun = 3

let private loopFellOvers (calls: ToolCall list) : FellOver list =
  calls
  |> List.windowed loopRun
  |> List.filter (fun w -> w |> List.forall (fun c -> c.Name = w.Head.Name && c.Input = w.Head.Input))
  |> List.map List.head
  |> List.distinctBy (fun c -> c.Name, c.Input)
  |> List.map (fun c ->
    { Stage = (if isSagefsCall c then stageOfTool (sagefsToolName c) else OtherTool)
      Symptom = sprintf "repeated the same %s call %d or more times" c.Name loopRun
      Evidence = clip evidenceLimit (sprintf "from turn %d, input %s" c.Turn c.Input) })

let private adoptionFellOvers (calls: ToolCall list) : FellOver list =
  let firstEvalIndex = calls |> List.tryFindIndex isSuccessfulEval
  let early =
    calls
    |> List.indexed
    |> List.filter (fun (i, c) ->
      match shellCommand c, firstEvalIndex with
      | Some cmd, Some first -> i < first && dotnetLoop.IsMatch cmd
      | Some cmd, None -> dotnetLoop.IsMatch cmd
      | None, _ -> false)
  match early, firstEvalIndex with
  | [], _ -> []
  | (_, c) :: _, evalIdx ->
    let which = match evalIdx with Some _ -> "before its first successful REPL eval" | None -> "and never made a successful REPL eval"
    [ { Stage = Adoption
        Symptom = sprintf "reached for dotnet in the shell %s" which
        Evidence = clip evidenceLimit (sprintf "turn %d: %s" c.Turn (shellCommand c |> Option.defaultValue "")) } ]

let private registrationFellOvers (facts: RunFacts) : FellOver list =
  match sagefsCalls facts.Stream with
  | [] ->
    let used = facts.Stream.Calls |> List.map _.Name |> List.distinct |> String.concat ", "
    [ { Stage = Registration
        Symptom = "never called a SageFs MCP tool"
        Evidence = clip evidenceLimit (sprintf "tools used: [%s]; final text: %s" used (facts.Stream.Result |> Option.map _.FinalText |> Option.defaultValue "")) } ]
  | _ -> []

let private budgetFellOver (outcome: Outcome) (stream: RunStream) : FellOver list =
  match outcome with
  | MaxTurns | Incomplete ->
    let calls = stream.Calls
    let firstSage = calls |> List.tryFind isSagefsCall |> Option.map _.Turn
    let firstEval = calls |> List.tryFind isSuccessfulEval |> Option.map _.Turn
    let show = Option.map string >> Option.defaultValue "never"
    [ { Stage = Budget
        Symptom = "ran out of turns or time before finishing"
        Evidence = sprintf "%d turns used; first SageFs call at turn %s; first successful eval at turn %s" stream.Turns (show firstSage) (show firstEval) } ]
  | _ -> []

let private cleanupFellOvers (facts: RunFacts) : FellOver list =
  let left =
    match facts.ResidueSessions with
    | [] -> []
    | ids ->
      [ { Stage = Cleanup
          Symptom = sprintf "left %d session(s) running on the shared daemon" ids.Length
          Evidence = String.concat ", " ids } ]
  let failed =
    match facts.Cleanup with
    | CleanupFailed ->
      [ { Stage = Cleanup
          Symptom = "the harness could not clear the lemming's sessions from the shared daemon"
          Evidence = "see out/residue.json" } ]
    | Clean | ResidueStopped | CleanupNotRun -> []
  left @ failed

let private oracleFellOvers (facts: RunFacts) : FellOver list =
  match facts.Oracle with
  | OracleFailed code ->
    [ { Stage = Oracle
        Symptom = sprintf "the oracle failed (exit %d)" code
        Evidence = clip evidenceLimit (facts.OracleOutput.Trim()) } ]
  | OraclePassed | OracleNotRun -> []

// ---- outcome ----------------------------------------------------------------------------

let private timedOut (facts: RunFacts) : bool =
  CmdcExit.timeoutExits |> List.contains facts.CmdcExit

let private streamIsEmpty (stream: RunStream) : bool =
  List.isEmpty stream.Calls && stream.Result.IsNone && stream.EndStopReason.IsNone && stream.SessionId.IsNone

/// Did the run need any recovering? A SageFs error, a refused tool, a session left on
/// the daemon, or running into the turn cap all count.
let needsRecovery (facts: RunFacts) : bool =
  let sage = sagefsCalls facts.Stream
  sage |> List.exists isSagefsError
  || facts.Stream.Calls |> List.exists (fun c -> c.Outcome = ToolErrored || c.Outcome = ToolDeniedByPolicy)
  || not (List.isEmpty facts.ResidueSessions)
  || facts.CmdcExit = CmdcExit.maxTurns

let private blockedBeforeEval (facts: RunFacts) : bool =
  let calls = facts.Stream.Calls
  let anyEval = calls |> List.exists isSuccessfulEval
  let setupFailed =
    calls
    |> List.exists (fun c ->
      isSagefsCall c && isSagefsError c
      && (match stageOfTool (sagefsToolName c) with SessionCreate | SessionWarmup -> true | _ -> false))
  let faulted = calls |> List.exists (fun c -> isSagefsCall c && c.Text.Contains "Faulted")
  not anyEval && (setupFailed || faulted)

let outcomeOf (facts: RunFacts) : Outcome * string * ProviderKind option =
  let provider = providerKind facts
  let oraclePassed = (facts.Oracle = OraclePassed)
  match provider with
  | Some kind when not oraclePassed ->
    ProviderQuota, sprintf "provider terminal error (%s), exit %d; not a SageFs result" (ProviderKind.toString kind) facts.CmdcExit, Some kind
  | _ ->
    match facts.CmdcExit with
    | e when e = CmdcExit.authError ->
      HarnessError, "Command Code could not authenticate inside the sandbox (exit 3): the credentials bind is wrong", None
    | _ when streamIsEmpty facts.Stream && not (timedOut facts) ->
      HarnessError, sprintf "cmdc produced no event stream (exit %d)" facts.CmdcExit, None
    | _ ->
      match facts.Oracle with
      | OraclePassed ->
        match needsRecovery facts with
        | true -> PassWithRecovery, "the oracle passed after at least one recovery", None
        | false -> Pass, "the oracle passed with no SageFs error and nothing left behind", None
      | OracleNotRun ->
        HarnessError, "no oracle ran, so there is no verdict to score", None
      | OracleFailed code ->
        match timedOut facts, facts.CmdcExit = CmdcExit.maxTurns with
        | _, true -> MaxTurns, sprintf "hit the turn cap and the oracle failed (exit %d)" code, None
        | true, _ -> Incomplete, "the harness timeout ended the run and the oracle failed", None
        | false, false ->
          match blockedBeforeEval facts with
          | true -> Blocked, "SageFs errored during session setup and no eval ever succeeded", None
          | false ->
            match facts.Stream.Result |> Option.map _.Subtype with
            | Some "success" -> Fail, sprintf "the lemming finished but the oracle failed (exit %d)" code, None
            | _ -> Incomplete, sprintf "the run ended without a clean finish and the oracle failed (exit %d)" code, None

let assess (facts: RunFacts) : Assessment =
  let outcome, reason, provider = outcomeOf facts
  let calls = facts.Stream.Calls
  let findings =
    registrationFellOvers facts
    @ errorFellOvers calls
    @ deniedFellOvers calls
    @ faultedFellOvers calls
    @ loopFellOvers calls
    @ adoptionFellOvers calls
    @ budgetFellOver outcome facts.Stream
    @ cleanupFellOvers facts
    @ oracleFellOvers facts
  { Outcome = outcome; Reason = reason; Provider = provider; FellOver = findings }

/// What an assessment says for the harness that produced the run. An editor lemming (VS Code
/// or Neovim) drives an editor, and the editor talks to SageFs; the lemming itself has no
/// reason to call a SageFs MCP tool, so "never called a SageFs MCP tool" would be on every
/// editor run and say nothing. Only the MCP lemming (`Cmdc`) keeps that finding.
let forHarness (harness: Harness) (assessment: Assessment) : Assessment =
  match harness with
  | Cmdc -> assessment
  | CmdcNvim
  | CmdcVscode ->
    { assessment with FellOver = assessment.FellOver |> List.filter (fun f -> f.Stage <> Registration) }
