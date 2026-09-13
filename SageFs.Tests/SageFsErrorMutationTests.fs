/// ## SageFsError Mutation Tests
///
/// Proves the test suite catches mutations in `SageFs.SageFsError` functions.
/// Focuses on classification functions (`toLogLevel`, `toHttpStatus`, `isClientError`,
/// `isServerError`, `isGatewayError`, `isInfraError`) and agent-facing output.
///
/// Each case asserts EXACT equality against the correct value (not merely
/// inequality with one hand-picked wrong value) so a mutant that returns any
/// other wrong value is killed too.
module SageFsErrorMutationTests

open Expecto
open Expecto.Flip
open SageFs

// ── Test Fixtures ──────────────────────────────────────────────────────────

let sessionNotFound = SageFsError.SessionNotFound "abc"
let evalFailed = SageFsError.EvalFailed "bad code"
let portInUse = SageFsError.PortInUse 37749
let workerTimeout = SageFsError.WorkerTimeout("s1", "eval", 30.0)
let noActiveSessions = SageFsError.NoActiveSessions
let unexpected = SageFsError.Unexpected (System.Exception("boom"))
let daemonNotRunning = SageFsError.DaemonNotRunning
let toolNotAvailable = SageFsError.ToolNotAvailable("send", SessionState.WarmingUp, ["get_status"])
let daemonStartFailed = SageFsError.DaemonStartFailed "port bound"
let restartLimitExceeded = SageFsError.RestartLimitExceeded(10, 5.0)
let workerSpawnFailed = SageFsError.WorkerSpawnFailed "SDK missing"
let sessionCreationFailed = SageFsError.SessionCreationFailed "bad path"
let hotReloadFailed = SageFsError.HotReloadFailed("src/foo.fs", "syntax error")

// ── Mutation Tests ─────────────────────────────────────────────────────────

let sageFsErrorMutationTests = testList "SageFsError mutations" [

  // ── toLogLevel ────────────────────────────────────────────────────────────

  testCase "WHY — toLogLevel_DaemonStartFailed_is_Critical — critical errors must not be downgraded" <| fun () ->
    SageFsError.toLogLevel daemonStartFailed
    |> Expect.equal "DaemonStartFailed must log at Critical" Microsoft.Extensions.Logging.LogLevel.Critical

  testCase "WHY — toLogLevel_PortInUse_is_Critical — port conflicts are critical" <| fun () ->
    SageFsError.toLogLevel portInUse
    |> Expect.equal "PortInUse must log at Critical" Microsoft.Extensions.Logging.LogLevel.Critical

  testCase "WHY — toLogLevel_EvalFailed_is_Error — eval failures are errors, not warnings" <| fun () ->
    SageFsError.toLogLevel evalFailed
    |> Expect.equal "EvalFailed must log at Error" Microsoft.Extensions.Logging.LogLevel.Error

  testCase "WHY — toLogLevel_SessionNotFound_is_Information — not-found is informational, not error" <| fun () ->
    SageFsError.toLogLevel sessionNotFound
    |> Expect.equal "SessionNotFound must log at Information" Microsoft.Extensions.Logging.LogLevel.Information

  testCase "WHY — toLogLevel_RestartLimitExceeded_is_Critical — restart limit is critical" <| fun () ->
    SageFsError.toLogLevel restartLimitExceeded
    |> Expect.equal "RestartLimitExceeded must log at Critical" Microsoft.Extensions.Logging.LogLevel.Critical

  // ── toHttpStatus ──────────────────────────────────────────────────────────

  testCase "WHY — toHttpStatus_SessionNotFound_is_404 — not-found must be 404" <| fun () ->
    SageFsError.toHttpStatus sessionNotFound
    |> Expect.equal "SessionNotFound must be 404" 404

  testCase "WHY — toHttpStatus_PortInUse_is_409 — port conflict must be 409" <| fun () ->
    SageFsError.toHttpStatus portInUse
    |> Expect.equal "PortInUse must be 409" 409

  testCase "WHY — toHttpStatus_WorkerTimeout_is_504 — timeout must be 504" <| fun () ->
    SageFsError.toHttpStatus workerTimeout
    |> Expect.equal "WorkerTimeout must be 504" 504

  testCase "WHY — toHttpStatus_NoActiveSessions_is_404 — empty sessions must be 404" <| fun () ->
    SageFsError.toHttpStatus noActiveSessions
    |> Expect.equal "NoActiveSessions must be 404" 404

  testCase "WHY — toHttpStatus_WorkerSpawnFailed_is_502 — spawn failure is a bad gateway" <| fun () ->
    SageFsError.toHttpStatus workerSpawnFailed
    |> Expect.equal "WorkerSpawnFailed must be 502" 502

  // ── isClientError ─────────────────────────────────────────────────────────

  testCase "WHY — isClientError_SessionNotFound_is_true — 404s are client errors" <| fun () ->
    SageFsError.isClientError sessionNotFound
    |> Expect.isTrue "SessionNotFound must be a client error"

  testCase "WHY — isClientError_EvalFailed_is_false — 500s are not client errors" <| fun () ->
    SageFsError.isClientError evalFailed
    |> Expect.isFalse "EvalFailed must not be a client error"

  testCase "WHY — isClientError_DaemonNotRunning_is_true — daemon down is client-actionable" <| fun () ->
    SageFsError.isClientError daemonNotRunning
    |> Expect.isTrue "DaemonNotRunning must be a client error"

  // ── isServerError ─────────────────────────────────────────────────────────

  testCase "WHY — isServerError_EvalFailed_is_true — eval failures are server errors" <| fun () ->
    SageFsError.isServerError evalFailed
    |> Expect.isTrue "EvalFailed must be a server error"

  testCase "WHY — isServerError_SessionNotFound_is_false — 404s are not server errors" <| fun () ->
    SageFsError.isServerError sessionNotFound
    |> Expect.isFalse "SessionNotFound must not be a server error"

  testCase "WHY — isServerError_DaemonStartFailed_is_true — daemon crashes are server errors" <| fun () ->
    SageFsError.isServerError daemonStartFailed
    |> Expect.isTrue "DaemonStartFailed must be a server error"

  // ── isGatewayError ────────────────────────────────────────────────────────

  testCase "WHY — isGatewayError_WorkerTimeout_is_true — timeouts are gateway errors" <| fun () ->
    SageFsError.isGatewayError workerTimeout
    |> Expect.isTrue "WorkerTimeout must be a gateway error"

  testCase "WHY — isGatewayError_EvalFailed_is_false — eval failures are not gateway errors" <| fun () ->
    SageFsError.isGatewayError evalFailed
    |> Expect.isFalse "EvalFailed must not be a gateway error"

  testCase "WHY — isGatewayError_WorkerSpawnFailed_is_true — spawn failure is a gateway error" <| fun () ->
    SageFsError.isGatewayError workerSpawnFailed
    |> Expect.isTrue "WorkerSpawnFailed must be a gateway error"

  // ── isInfraError ──────────────────────────────────────────────────────────

  testCase "WHY — isInfraError_PortInUse_is_true — port conflicts are infra errors" <| fun () ->
    SageFsError.isInfraError portInUse
    |> Expect.isTrue "PortInUse must be an infra error"

  testCase "WHY — isInfraError_EvalFailed_is_false — eval failures are not infra errors" <| fun () ->
    SageFsError.isInfraError evalFailed
    |> Expect.isFalse "EvalFailed must not be an infra error"

  testCase "WHY — isInfraError_RestartLimitExceeded_is_true — restart limit is an infra error" <| fun () ->
    SageFsError.isInfraError restartLimitExceeded
    |> Expect.isTrue "RestartLimitExceeded must be an infra error"

  // ── describeForAgent ──────────────────────────────────────────────────────

  testCase "WHY — describeForAgent_composes_describe_and_suggestedAction — agents need next steps" <| fun () ->
    let expected = sprintf "%s → Next: %s" (SageFsError.describe sessionNotFound) (SageFsError.suggestedAction sessionNotFound)
    SageFsError.describeForAgent sessionNotFound
    |> Expect.equal "describeForAgent must be \"<describe> → Next: <suggestedAction>\"" expected

  // ── Mutual exclusion: isClientError and isServerError ──────────────────────

  testCase "WHY — isClientError_and_isServerError_are_mutually_exclusive — classification must be consistent" <| fun () ->
    let allErrors = [sessionNotFound; evalFailed; portInUse; workerTimeout; noActiveSessions; unexpected; daemonNotRunning; daemonStartFailed; restartLimitExceeded]
    let violations = allErrors |> List.filter (fun e -> SageFsError.isClientError e && SageFsError.isServerError e)
    violations
    |> Expect.isEmpty "no error may be classified as both client and server"
]
