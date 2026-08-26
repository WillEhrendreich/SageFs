module SageFs.Tests.SupervisorPortHandshakeTests

open Expecto

/// Phase 0 RED: prove there is no verified port handshake today.
///
/// The plan requires: the supervisor reports "host ready" ONLY after reading
/// and validating the host's WORKER_PORT= stdout line (loopback URL, live
/// process). A host that dies before printing the port must never be reported
/// ready — the session must fail with WorkerSpawnFailed, not hang.
///
/// Today: no `Supervisor` module exists; the daemon's SessionManager awaits
/// the port line directly. These tests reference the planned public API and
/// are compile-RED until Phase 1 creates `SageFs.Core.Supervisor`.
[<Tests>]
let tests =
  testList "Supervisor port handshake (RED)" [

    testCase "ready is only reported for a validated loopback port" <| fun _ ->
      // A host stdout line that is NOT a valid loopback URL must be rejected.
      let stdoutLine = "WORKER_PORT=http://evil.example.com:9999"

      let result =
        Supervisor.validateHostReadyLine
          Supervisor.hostReadyLine
          stdoutLine

      match result with
      | Error msg ->
        Expect.stringContains
          msg
          "loopback"
          "non-loopback host must be rejected as not loopback"
      | Ok _ -> failtest "non-loopback host URL must not be accepted"

    testCase "valid loopback port is accepted as ready" <| fun _ ->
      let stdoutLine = "WORKER_PORT=http://127.0.0.1:54321"

      let result =
        Supervisor.validateHostReadyLine
          Supervisor.hostReadyLine
          stdoutLine

      match result with
      | Ok url ->
        Expect.equal
          url
          "http://127.0.0.1:54321"
          "valid loopback URL should be returned as ready"
      | Error msg -> failtestf "valid loopback URL rejected: %s" msg

    testCase "host exiting before printing the port surfaces WorkerSpawnFailed" <| fun _ ->
      // The supervisor must translate "host died before ready" into the
      // existing session-manager failure path (WorkerSpawnFailed), not a hang.
      let outcome =
        Supervisor.hostExitedBeforeReady
          (Supervisor.HostExitBeforeReady 0xC0000135)

      match outcome with
      | Error (SageFs.SageFsError.WorkerSpawnFailed reason) ->
        Expect.stringContains
          reason
          "before reporting ready"
          "reason should explain the host died before ready"
      | Error e -> failtestf "expected WorkerSpawnFailed, got %A" e
      | Ok () -> failtest "host-exited-before-ready must be an error, not success"
  ]
