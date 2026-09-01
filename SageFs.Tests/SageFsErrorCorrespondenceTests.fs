/// ## SageFsError Correspondence Tests
///
/// Validates that the F# SageFsError satisfies the same properties proved in
/// `formal-verification/lean/FVSquad/SageFsError.lean`. Each test maps 1-to-1
/// to a Lean theorem.
module SageFsErrorCorrespondenceTests

open Expecto
open Expecto.Flip
open SageFs

// ── Test Fixtures ──────────────────────────────────────────────────────────

let allErrors : SageFsError list = [
  SageFsError.ToolNotAvailable ("x", SessionState.WarmingUp, ["y"])
  SageFsError.SessionNotFound "s1"
  SageFsError.NoActiveSessions
  SageFsError.AmbiguousSessions ["a"; "b"]
  SageFsError.SessionCreationFailed "path"
  SageFsError.SessionStopFailed ("s1", "reason")
  SageFsError.SessionSwitchFailed ("s1", "reason")
  SageFsError.WorkerCommunicationFailed ("s1", "x")
  SageFsError.WorkerSpawnFailed "sdk"
  SageFsError.WorkerTimeout ("s1", "eval", 30.0)
  SageFsError.WorkerHttpError ("s1", "/x", 500)
  SageFsError.PipeClosed
  SageFsError.EvalFailed "bad"
  SageFsError.ResetFailed "x"
  SageFsError.HardResetFailed "x"
  SageFsError.ScriptLoadFailed "x"
  SageFsError.CheckFailed "x"
  SageFsError.CompletionFailed ("s1", "x")
  SageFsError.CancelFailed "x"
  SageFsError.WarmupOpenFailed ("x", "x")
  SageFsError.WarmupContextFailed ("s1", "x")
  SageFsError.HotReloadFailed ("a.fs", "x")
  SageFsError.HotReloadStateError ("s1", "x")
  SageFsError.RestartLimitExceeded (5, 5.0)
  SageFsError.DaemonStartFailed "x"
  SageFsError.DaemonNotRunning
  SageFsError.PortInUse 37749
  SageFsError.SseConnectionError "x"
  SageFsError.JsonParseError ("ctx", "x")
  SageFsError.Unexpected (System.Exception("boom"))
]

// ── Group 1: mutual exclusion — Lean: client_not_server, client_not_gateway, etc. ─

let mutualExclusionTests =
  testList "mutual exclusion" [
    test "WHY — client_not_server — isClientError and isServerError never both true (Lean: client_not_server)" {
      for e in allErrors do
        (SageFsError.isClientError e && SageFsError.isServerError e)
        |> Expect.isFalse $"%A{e} should not be both client and server"
    }

    test "WHY — client_not_gateway — isClientError and isGatewayError never both true (Lean: client_not_gateway)" {
      for e in allErrors do
        (SageFsError.isClientError e && SageFsError.isGatewayError e)
        |> Expect.isFalse $"%A{e} should not be both client and gateway"
    }

    test "WHY — client_not_infra — isClientError and isInfraError never both true (Lean: client_not_infra)" {
      for e in allErrors do
        (SageFsError.isClientError e && SageFsError.isInfraError e)
        |> Expect.isFalse $"%A{e} should not be both client and infra"
    }

    test "WHY — server_not_gateway — isServerError and isGatewayError never both true (Lean: server_not_gateway)" {
      for e in allErrors do
        (SageFsError.isServerError e && SageFsError.isGatewayError e)
        |> Expect.isFalse $"%A{e} should not be both server and gateway"
    }

    test "WHY — server_not_infra — isServerError and isInfraError never both true (Lean: server_not_infra)" {
      for e in allErrors do
        (SageFsError.isServerError e && SageFsError.isInfraError e)
        |> Expect.isFalse $"%A{e} should not be both server and infra"
    }

    test "WHY — gateway_not_infra — isGatewayError and isInfraError never both true (Lean: gateway_not_infra)" {
      for e in allErrors do
        (SageFsError.isGatewayError e && SageFsError.isInfraError e)
        |> Expect.isFalse $"%A{e} should not be both gateway and infra"
    }
  ]

// ── Group 2: HTTP status mapping — Lean: client_http_status, etc. ──────────

let httpStatusTests =
  testList "HTTP status" [
    test "WHY — client_http_status — isClientError → 4xx status (Lean: client_http_status)" {
      for e in allErrors do
        if SageFsError.isClientError e then
          let status = SageFsError.toHttpStatus e
          (status, 500) |> Expect.isLessThan $"client error %A{e} should have 4xx, got {status}"
    }

    test "WHY — gateway_http_status — isGatewayError → 5xx status (Lean: gateway_http_status)" {
      for e in allErrors do
        if SageFsError.isGatewayError e then
          let status = SageFsError.toHttpStatus e
          (status, 499) |> Expect.isGreaterThan $"gateway error %A{e} should have 5xx, got {status}"
    }

    test "WHY — status_400_exists — ToolNotAvailable → 400 (Lean: status_400_exists)" {
      SageFsError.toHttpStatus (SageFsError.ToolNotAvailable ("x", SessionState.WarmingUp, ["y"]))
      |> Expect.equal "ToolNotAvailable should be 400" 400
    }

    test "WHY — status_404_exists — SessionNotFound → 404 (Lean: status_404_exists)" {
      SageFsError.toHttpStatus (SageFsError.SessionNotFound "s1")
      |> Expect.equal "SessionNotFound should be 404" 404
    }

    test "WHY — status_409_exists — PortInUse → 409 (Lean: status_409_exists)" {
      SageFsError.toHttpStatus (SageFsError.PortInUse 37749)
      |> Expect.equal "PortInUse should be 409" 409
    }

    test "WHY — status_500_exists — EvalFailed → 500 (Lean: status_500_exists)" {
      SageFsError.toHttpStatus (SageFsError.EvalFailed "x")
      |> Expect.equal "EvalFailed should be 500" 500
    }

    test "WHY — status_502_exists — PipeClosed → 502 (Lean: status_502_exists)" {
      SageFsError.toHttpStatus SageFsError.PipeClosed
      |> Expect.equal "PipeClosed should be 502" 502
    }

    test "WHY — status_504_exists — WorkerTimeout → 504 (Lean: status_504_exists)" {
      SageFsError.toHttpStatus (SageFsError.WorkerTimeout ("s1", "eval", 30.0))
      |> Expect.equal "WorkerTimeout should be 504" 504
    }
  ]

// ── Group 3: HTTP status bijection — Lean: http_409_iff_infra, http_504_iff_gateway_timeout ─

let httpStatusBijectionTests =
  testList "HTTP status bijection" [
    test "WHY — http_409_iff_infra — 409 ↔ isInfraError (Lean: http_409_iff_infra)" {
      for e in allErrors do
        let isInfra = SageFsError.isInfraError e
        let is409 = SageFsError.toHttpStatus e = 409
        (isInfra = is409)
        |> Expect.isTrue $"%A{e}: isInfra should match 409"
    }

    test "WHY — http_504_iff_gateway_timeout — 504 ↔ WorkerTimeout (Lean: http_504_iff_gateway_timeout)" {
      for e in allErrors do
        let isTimeout = match e with SageFsError.WorkerTimeout _ -> true | _ -> false
        let is504 = SageFsError.toHttpStatus e = 504
        (isTimeout = is504)
        |> Expect.isTrue $"%A{e}: isTimeout should match 504"
    }
  ]

// ── Group 4: log level mapping — Lean: infra_log_critical, gateway_not_info, client_not_critical ─

let logLevelTests =
  testList "log level" [
    test "WHY — infra_log_critical — isInfraError → Critical log level (Lean: infra_log_critical)" {
      for e in allErrors do
        if SageFsError.isInfraError e then
          SageFsError.toLogLevel e
          |> Expect.equal $"infra %A{e} should be Critical" Microsoft.Extensions.Logging.LogLevel.Critical
    }

    test "WHY — gateway_not_info — isGatewayError → not Information (Lean: gateway_not_info)" {
      for e in allErrors do
        if SageFsError.isGatewayError e then
          SageFsError.toLogLevel e
          |> Expect.notEqual $"gateway %A{e} should not be Information" Microsoft.Extensions.Logging.LogLevel.Information
    }

    test "WHY — client_not_critical — isClientError → not Critical (Lean: client_not_critical)" {
      for e in allErrors do
        if SageFsError.isClientError e then
          SageFsError.toLogLevel e
          |> Expect.notEqual $"client %A{e} should not be Critical" Microsoft.Extensions.Logging.LogLevel.Critical
    }
  ]

// ── All tests combined ──────────────────────────────────────────────────────

let sageFsErrorCorrespondenceTests =
  testList "SageFsError Correspondence (F# vs Lean)" [
    mutualExclusionTests
    httpStatusTests
    httpStatusBijectionTests
    logLevelTests
  ]
