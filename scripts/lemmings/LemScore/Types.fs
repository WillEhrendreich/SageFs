/// The closed vocabularies of the lemming harness. Every set is a union with one
/// exhaustive to-string, so a new case that is not named in the summary will not compile.
module LemScore.Types

/// How a run ended. Closed: nothing outside this set is ever written to summary.json.
type Outcome =
  | Pass
  | PassWithRecovery
  | Fail
  | Blocked
  | Incomplete
  | ProviderQuota
  | MaxTurns
  | HarnessError

module Outcome =
  let toString (outcome: Outcome) : string =
    match outcome with
    | Pass -> "Pass"
    | PassWithRecovery -> "PassWithRecovery"
    | Fail -> "Fail"
    | Blocked -> "Blocked"
    | Incomplete -> "Incomplete"
    | ProviderQuota -> "ProviderQuota"
    | MaxTurns -> "MaxTurns"
    | HarnessError -> "HarnessError"

  let all : Outcome list =
    [ Pass; PassWithRecovery; Fail; Blocked; Incomplete; ProviderQuota; MaxTurns; HarnessError ]

  let tryParse (text: string) : Result<Outcome, string> =
    match all |> List.tryFind (fun o -> toString o = text) with
    | Some o -> Ok o
    | None -> Error (sprintf "'%s' is not one of the outcomes: %s" text (all |> List.map toString |> String.concat ", "))

/// Where in the lemming's journey a thing went wrong.
type Stage =
  | Preflight
  | Isolation
  | Registration
  | Adoption
  | SessionCreate
  | SessionWarmup
  | Eval
  | Reload
  | Tests
  | Lease
  | Cleanup
  | Budget
  | ToolSurface
  | Oracle
  | Editor
  | OtherTool

module Stage =
  let toString (stage: Stage) : string =
    match stage with
    | Preflight -> "Preflight"
    | Isolation -> "Isolation"
    | Registration -> "Registration"
    | Adoption -> "Adoption"
    | SessionCreate -> "SessionCreate"
    | SessionWarmup -> "SessionWarmup"
    | Eval -> "Eval"
    | Reload -> "Reload"
    | Tests -> "Tests"
    | Lease -> "Lease"
    | Cleanup -> "Cleanup"
    | Budget -> "Budget"
    | ToolSurface -> "ToolSurface"
    | Oracle -> "Oracle"
    | Editor -> "Editor"
    | OtherTool -> "OtherTool"

  let tryParse (text: string) : Result<Stage, string> =
    let all =
      [ Preflight; Isolation; Registration; Adoption; SessionCreate; SessionWarmup; Eval; Reload; Tests
        Lease; Cleanup; Budget; ToolSurface; Oracle; Editor; OtherTool ]
    match all |> List.tryFind (fun s -> toString s = text) with
    | Some s -> Ok s
    | None -> Error (sprintf "'%s' is not a stage" text)

/// One place the lemming fell over: where, what it looked like, and the proof.
type FellOver =
  { Stage: Stage
    Symptom: string
    Evidence: string }

/// What the harness's shared-daemon cleanup found and did. Closed.
type Cleanup =
  | Clean
  | ResidueStopped
  | CleanupFailed
  | CleanupNotRun

module Cleanup =
  let toString (cleanup: Cleanup) : string =
    match cleanup with
    | Clean -> "clean"
    | ResidueStopped -> "residue-stopped"
    | CleanupFailed -> "failed"
    | CleanupNotRun -> "not-run"

  let tryParse (text: string) : Result<Cleanup, string> =
    [ Clean; ResidueStopped; CleanupFailed; CleanupNotRun ]
    |> List.tryFind (fun c -> toString c = text)
    |> Option.map Ok
    |> Option.defaultValue (Error (sprintf "'%s' is not a cleanup result" text))

/// What the oracle said, as run by the harness outside the sandbox.
type OracleVerdict =
  | OracleNotRun
  | OraclePassed
  | OracleFailed of exitCode: int

/// How the lemming's own process ended.
type CmdcTeardown =
  | ExitedOnItsOwn
  | KilledByTimeout

module CmdcTeardown =
  let toString (t: CmdcTeardown) : string =
    match t with
    | ExitedOnItsOwn -> "exited"
    | KilledByTimeout -> "killed-by-timeout"

/// Which harness drove the lemming.
type Harness =
  | Cmdc
  | CmdcNvim
  | CmdcVscode

module Harness =
  let toString (h: Harness) : string =
    match h with
    | Cmdc -> "cmdc"
    | CmdcNvim -> "cmdc-nvim"
    | CmdcVscode -> "cmdc-vscode"

  let tryParse (text: string) : Result<Harness, string> =
    [ Cmdc; CmdcNvim; CmdcVscode ]
    |> List.tryFind (fun h -> toString h = text)
    |> Option.map Ok
    |> Option.defaultValue (Error (sprintf "'%s' is not a harness (cmdc, cmdc-nvim, cmdc-vscode)" text))

/// Command Code's documented headless exit codes (cmdc --help, "exit 8 on cap-hit"; the
/// table lives in its cli.mjs). Named here so no magic number reaches the classifier.
module CmdcExit =
  let success = 0
  let authError = 3
  let rateLimited = 5
  let connectionError = 6
  let serverError = 7
  let maxTurns = 8
  let insufficientCredits = 10
  /// `timeout(1)` exits 124 on its time limit and 137 when it had to send SIGKILL.
  let timeoutExits = [ 124; 137 ]
