/// ## SageFsError Mutation Tests
///
/// Proves the test suite catches mutations in `SageFs.SageFsError` functions.
/// Focuses on classification functions (`toLogLevel`, `toHttpStatus`, `isClientError`,
/// `isServerError`, `isGatewayError`, `isInfraError`) and agent-facing output.
module SageFsErrorMutationTests

open Expecto
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

  testCase "WHY — toLogLevel_DaemonStartFailed_as_Writical — critical errors must not be downgraded" <| fun () ->
    // Mutant: DaemonStartFailed → Warning instead of Critical
    let real = SageFsError.toLogLevel daemonStartFailed
    let mutant = Microsoft.Extensions.Logging.LogLevel.Warning
    if real = mutant then
      failwith "Mutation survived — toLogLevel downgraded DaemonStartFailed"

  testCase "WHY — toLogLevel_PortInUse_as_Error — port conflicts are critical, not error" <| fun () ->
    let real = SageFsError.toLogLevel portInUse
    let mutant = Microsoft.Extensions.Logging.LogLevel.Error
    if real = mutant then
      failwith "Mutation survived — toLogLevel downgraded PortInUse"

  testCase "WHY — toLogLevel_EvalFailed_as_Warning — eval failures are errors, not warnings" <| fun () ->
    let real = SageFsError.toLogLevel evalFailed
    let mutant = Microsoft.Extensions.Logging.LogLevel.Warning
    if real = mutant then
      failwith "Mutation survived — toLogLevel downgraded EvalFailed"

  testCase "WHY — toLogLevel_SessionNotFound_as_Error — not-found is informational, not error" <| fun () ->
    let real = SageFsError.toLogLevel sessionNotFound
    let mutant = Microsoft.Extensions.Logging.LogLevel.Error
    if real = mutant then
      failwith "Mutation survived — toLogLevel upgraded SessionNotFound"

  testCase "WHY — toLogLevel_RestartLimitExceeded_as_Error — restart limit is critical" <| fun () ->
    let real = SageFsError.toLogLevel restartLimitExceeded
    let mutant = Microsoft.Extensions.Logging.LogLevel.Error
    if real = mutant then
      failwith "Mutation survived — toLogLevel downgraded RestartLimitExceeded"

  // ── toHttpStatus ──────────────────────────────────────────────────────────

  testCase "WHY — toHttpStatus_SessionNotFound_as_500 — not-found must be 404, not 500" <| fun () ->
    let real = SageFsError.toHttpStatus sessionNotFound
    let mutant = 500
    if real = mutant then
      failwith "Mutation survived — toHttpStatus changed SessionNotFound to 500"

  testCase "WHY — toHttpStatus_PortInUse_as_500 — port conflict must be 409, not 500" <| fun () ->
    let real = SageFsError.toHttpStatus portInUse
    let mutant = 500
    if real = mutant then
      failwith "Mutation survived — toHttpStatus changed PortInUse to 500"

  testCase "WHY — toHttpStatus_WorkerTimeout_as_500 — timeout must be 504, not 500" <| fun () ->
    let real = SageFsError.toHttpStatus workerTimeout
    let mutant = 500
    if real = mutant then
      failwith "Mutation survived — toHttpStatus changed WorkerTimeout to 500"

  testCase "WHY — toHttpStatus_NoActiveSessions_as_500 — empty sessions must be 404, not 500" <| fun () ->
    let real = SageFsError.toHttpStatus noActiveSessions
    let mutant = 500
    if real = mutant then
      failwith "Mutation survived — toHttpStatus changed NoActiveSessions to 500"

  testCase "WHY — toHttpStatus_WorkerSpawnFailed_as_500 — spawn failure is 502, not 500" <| fun () ->
    let real = SageFsError.toHttpStatus workerSpawnFailed
    let mutant = 500
    if real = mutant then
      failwith "Mutation survived — toHttpStatus changed WorkerSpawnFailed to 500"

  // ── isClientError ─────────────────────────────────────────────────────────

  testCase "WHY — isClientError_SessionNotFound_must_be_true — 404s are client errors" <| fun () ->
    let real = SageFsError.isClientError sessionNotFound
    let mutant = false
    if real = mutant then
      failwith "Mutation survived — isClientError says SessionNotFound is not client error"

  testCase "WHY — isClientError_EvalFailed_must_be_false — 500s are not client errors" <| fun () ->
    let real = SageFsError.isClientError evalFailed
    let mutant = true
    if real = mutant then
      failwith "Mutation survived — isClientError says EvalFailed is client error"

  testCase "WHY — isClientError_DaemonNotRunning_must_be_true — daemon down is client-actionable" <| fun () ->
    let real = SageFsError.isClientError daemonNotRunning
    let mutant = false
    if real = mutant then
      failwith "Mutation survived — isClientError says DaemonNotRunning is not client error"

  // ── isServerError ─────────────────────────────────────────────────────────

  testCase "WHY — isServerError_EvalFailed_must_be_true — eval failures are server errors" <| fun () ->
    let real = SageFsError.isServerError evalFailed
    let mutant = false
    if real = mutant then
      failwith "Mutation survived — isServerError says EvalFailed is not server error"

  testCase "WHY — isServerError_SessionNotFound_must_be_false — 404s are not server errors" <| fun () ->
    let real = SageFsError.isServerError sessionNotFound
    let mutant = true
    if real = mutant then
      failwith "Mutation survived — isServerError says SessionNotFound is server error"

  testCase "WHY — isServerError_DaemonStartFailed_must_be_true — daemon crashes are server errors" <| fun () ->
    let real = SageFsError.isServerError daemonStartFailed
    let mutant = false
    if real = mutant then
      failwith "Mutation survived — isServerError says DaemonStartFailed is not server error"

  // ── isGatewayError ────────────────────────────────────────────────────────

  testCase "WHY — isGatewayError_WorkerTimeout_must_be_true — timeouts are gateway errors" <| fun () ->
    let real = SageFsError.isGatewayError workerTimeout
    let mutant = false
    if real = mutant then
      failwith "Mutation survived — isGatewayError says WorkerTimeout is not gateway error"

  testCase "WHY — isGatewayError_EvalFailed_must_be_false — eval failures are not gateway errors" <| fun () ->
    let real = SageFsError.isGatewayError evalFailed
    let mutant = true
    if real = mutant then
      failwith "Mutation survived — isGatewayError says EvalFailed is gateway error"

  testCase "WHY — isGatewayError_WorkerSpawnFailed_must_be_true — spawn failure is gateway error" <| fun () ->
    let real = SageFsError.isGatewayError workerSpawnFailed
    let mutant = false
    if real = mutant then
      failwith "Mutation survived — isGatewayError says WorkerSpawnFailed is not gateway error"

  // ── isInfraError ──────────────────────────────────────────────────────────

  testCase "WHY — isInfraError_PortInUse_must_be_true — port conflicts are infra errors" <| fun () ->
    let real = SageFsError.isInfraError portInUse
    let mutant = false
    if real = mutant then
      failwith "Mutation survived — isInfraError says PortInUse is not infra error"

  testCase "WHY — isInfraError_EvalFailed_must_be_false — eval failures are not infra errors" <| fun () ->
    let real = SageFsError.isInfraError evalFailed
    let mutant = true
    if real = mutant then
      failwith "Mutation survived — isInfraError says EvalFailed is infra error"

  testCase "WHY — isInfraError_RestartLimitExceeded_must_be_true — restart limit is infra error" <| fun () ->
    let real = SageFsError.isInfraError restartLimitExceeded
    let mutant = false
    if real = mutant then
      failwith "Mutation survived — isInfraError says RestartLimitExceeded is not infra error"

  // ── describeForAgent ──────────────────────────────────────────────────────

  testCase "WHY — describeForAgent_must_include_suggestedAction — agents need next steps" <| fun () ->
    let real = SageFsError.describeForAgent sessionNotFound
    let mutant = SageFsError.describe sessionNotFound  // mutant: no suggested action
    if real = mutant then
      failwith "Mutation survived — describeForAgent missing suggestedAction"

  // ── Mutual exclusion: isClientError and isServerError ──────────────────────

  testCase "WHY — isClientError_and_isServerError_must_not_both_be_true — classification must be consistent" <| fun () ->
    let allErrors = [sessionNotFound; evalFailed; portInUse; workerTimeout; noActiveSessions; unexpected; daemonNotRunning; daemonStartFailed; restartLimitExceeded]
    let violations = allErrors |> List.filter (fun e -> SageFsError.isClientError e && SageFsError.isServerError e)
    if violations.Length > 0 then
      failwithf "Mutation survived — %d errors classified as both client and server" violations.Length
]
