/// The memory numbers reach the browser only through the heartbeat's telemetry patches, so the decision
/// "which samples does this connection still need" and the order "write, then remember" are what keep a
/// readout from going blank. These cases hold that decision and that order still: a connection sends every
/// sample once, resends only what moved, and never marks a sample sent that the write did not deliver.
module SageFs.Tests.DashboardTelemetrySendTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Server.DashboardTypes
open SageFs.Server.Dashboard

module Samples =
  let daemon = { Signal = TelemetrySignals.DaemonMemoryMb; Megabytes = 128 }
  let worker = { Signal = "workerRssMb_0a2b3c4d"; Megabytes = 310 }
  let both = [ daemon; worker ]

  /// A write that records what it was asked to patch and always lands.
  let recording (sent: Collections.Generic.List<TelemetrySample>) (sample: TelemetrySample) : Task =
    sent.Add sample
    Task.CompletedTask

  /// A write that lands for every signal except the one named, which throws as a broken patch does.
  let failingOn (signal: string) (sent: Collections.Generic.List<TelemetrySample>) (sample: TelemetrySample) : Task =
    match sample.Signal = signal with
    | true -> raise (InvalidOperationException "patch failed")
    | false -> recording sent sample

  let sentOf (result: TelemetrySendResult) : Map<string, int> =
    match result with
    | TelemetrySendResult.Sent lastSent -> lastSent
    | TelemetrySendResult.WriteFailed (lastSent, _) -> lastSent

[<Tests>]
let tests =
  testList "the heartbeat sends each telemetry sample once, and only the ones that moved" [

    testCase "WHY — a connection that has sent nothing needs every sample, because the first heartbeat must fill the readout" <| fun () ->
      TelemetrySend.unsent Map.empty Samples.both
      |> Expect.equal "the first call sends everything" Samples.both

    testCase "WHY — a sample whose value has not moved is not resent, because a quiet daemon should put nothing on the wire" <| fun () ->
      let lastSent = Map [ Samples.daemon.Signal, Samples.daemon.Megabytes; Samples.worker.Signal, Samples.worker.Megabytes ]
      TelemetrySend.unsent lastSent Samples.both
      |> Expect.isEmpty "nothing is due"

    testCase "WHY — a sample that moved is sent again and the one that did not is not" <| fun () ->
      let lastSent = Map [ Samples.daemon.Signal, Samples.daemon.Megabytes; Samples.worker.Signal, Samples.worker.Megabytes ]
      let moved = { Samples.daemon with Megabytes = Samples.daemon.Megabytes + 3 }
      TelemetrySend.unsent lastSent [ moved; Samples.worker ]
      |> Expect.equal "only the daemon moved" [ moved ]

    testTask "WHY — a delivered sample is remembered, so the next heartbeat with the same value sends nothing" {
      let sent = Collections.Generic.List<TelemetrySample>()
      let! first = TelemetrySend.sendUnsent (Samples.recording sent) Map.empty Samples.both
      let! second = TelemetrySend.sendUnsent (Samples.recording sent) (Samples.sentOf first) Samples.both
      sent |> List.ofSeq |> Expect.equal "each sample was written exactly once" Samples.both
      Samples.sentOf second |> Expect.equal "the table still holds both values" (Samples.sentOf first)
    }

    testTask "WHY — a write that fails leaves its sample unmarked, because the marker was set before the write and a failed patch was never retried" {
      let sent = Collections.Generic.List<TelemetrySample>()
      let! failed = TelemetrySend.sendUnsent (Samples.failingOn Samples.worker.Signal sent) Map.empty Samples.both
      Samples.sentOf failed
      |> Map.containsKey Samples.worker.Signal
      |> Expect.isFalse "the failed sample is not marked sent"
      Samples.sentOf failed
      |> Map.containsKey Samples.daemon.Signal
      |> Expect.isTrue "the sample that landed before it is marked sent"
      TelemetrySend.unsent (Samples.sentOf failed) Samples.both
      |> Expect.equal "the next heartbeat retries the failed sample" [ Samples.worker ]
    }

    testTask "WHY — a failed write is returned as a failure, because the caller must be able to say so once instead of swallowing it at Debug" {
      let sent = Collections.Generic.List<TelemetrySample>()
      let! failed = TelemetrySend.sendUnsent (Samples.failingOn Samples.daemon.Signal sent) Map.empty Samples.both
      match failed with
      | TelemetrySendResult.WriteFailed (_, error) -> error.Message |> Expect.equal "it names the error" "patch failed"
      | TelemetrySendResult.Sent _ -> failtest "a failed write reported as sent"
    }
  ]
