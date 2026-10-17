/// An idle dashboard must not be pushed again. The stream compares the full-shell HTML it rendered with
/// the HTML it last sent (and `SnapshotRenderGuard` compares the snapshot first), and skips the push when
/// they match. That only works when the HTML is a pure function of real state. A label that counts the
/// clock ("up 5m", "last: 3m ago") made two renders of the SAME idle state differ the moment a minute
/// turned, so the next unrelated event pushed the whole `#main` for nothing.
///
/// The same goes for the memory numbers ("181MB" -> "184MB"): measured on an idle daemon, they were the
/// one thing that differed between consecutive pushes once the clock text was gone, because the daemon's
/// own resident set moves on nearly every render. A number that moves with no change of state is
/// telemetry, so it travels as a signal patch (`TelemetrySignals`), never inside the HTML.
///
/// These cases hold the state still and move the clock and the memory: the same state, rendered a clock
/// step apart with different samples, must be the same bytes. The ticking itself lives in the browser
/// (`ElapsedClock`).
module SageFs.Tests.DashboardIdleStabilityTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.Dashboard
open SageFs.Tests.DashboardRenderGateTests

module private Idle =
  /// One idle daemon sampled once, `uptime` after it started, using `memoryMb`. Only these two differ
  /// between samples.
  let healthAt (uptime: TimeSpan) (memoryMb: int) : HealthSnapshot =
    { DaemonPid = 1
      DaemonPort = 37749
      Uptime = uptime
      Version = "0.0.0"
      SessionSummaries = []
      LiveTestingSummary = None
      MemoryMB = memoryMb
      Anomalies = []
      GcDumpOutcome = None
      MemoryPressure = SageFs.MemoryPressure.Normal
      SupervisorHealth = SageFs.SupervisorHealth.Healthy }

  /// A daemon start no case compares with a clock: any moment will do, as long as it is not the epoch's 0.
  let startMs = DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()

  let earlier = FixtureDurations.uptimeUnread
  let later = FixtureDurations.uptimeUnread + FixtureDurations.idleClockStep

  /// The resident memory (MB) the daemon reported at each sample: it grew a little, as it does.
  let memoryEarlier = 128
  let memoryLater = 131

  /// The daemon's first sample, and the one a clock step later with its memory moved.
  let sampleEarlier = healthAt earlier memoryEarlier
  let sampleLater = healthAt later memoryLater

  let queriesAt (sample: HealthSnapshot) : DashboardQueries =
    { mkQueries (ref 0) [] with GetDaemonHealth = fun () -> Some sample }

[<Tests>]
let tests =
  testList "an idle dashboard renders the same bytes, so the stream has nothing to push" [

    testCase "WHY — the daemon health bar does not render the clock, because 'up 10m' became 'up 12m' between two renders of one idle daemon" <| fun () ->
      let bar uptime =
        renderDaemonHealth (DaemonHealthView.fromSnapshot DateTimeOffset.UnixEpoch (Idle.healthAt uptime Idle.memoryEarlier)) |> renderNode
      bar Idle.later |> Expect.equal "two samples of one idle daemon render the same bytes" (bar Idle.earlier)

    testCase "WHY — the daemon health bar does not render its own memory, because '181MB' became '184MB' on nearly every push of an idle daemon and each one was a full morph" <| fun () ->
      let bar memoryMb =
        renderDaemonHealth (DaemonHealthView.fromSnapshot DateTimeOffset.UnixEpoch (Idle.healthAt Idle.earlier memoryMb)) |> renderNode
      bar Idle.memoryLater |> Expect.equal "two memory samples of one idle daemon render the same bytes" (bar Idle.memoryEarlier)

    testCase "WHY — a sidebar card does not render its worker's memory, for the same reason" <| fun () ->
      let info = SageFs.Tests.DashboardParsingTests.SidebarCards.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready []
      let card = sessionCardOf SageFs.Tests.DashboardParsingTests.SidebarCards.now None 0 SessionHealth.Healthy info
      let panel (rssMb: int64) =
        renderSessionsForSession "" [ { card with WorkerRssBytes = Some (rssMb * 1024L * 1024L) } ] false |> renderNode
      panel (int64 Idle.memoryLater) |> Expect.equal "two memory samples of one worker render the same bytes" (panel (int64 Idle.memoryEarlier))

    testCase "WHY — a sidebar session card does not render the clock, because 'up 5m / last: 3m ago' became '7m / 5m ago' two minutes later with nothing else changed" <| fun () ->
      let info = SageFs.Tests.DashboardParsingTests.SidebarCards.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready []
      let now = SageFs.Tests.DashboardParsingTests.SidebarCards.now
      let panel (at: DateTime) =
        renderSessionsForSession "" [ sessionCardOf at None 0 SessionHealth.Healthy info ] false |> renderNode
      panel (now + FixtureDurations.idleClockStep) |> Expect.equal "one session, rendered a clock step apart, is the same bytes" (panel now)

    testCase "WHY — the resume picker does not render how long ago a session was seen, because that is the render reading the clock" <| fun () ->
      let previous : PreviousSession =
        { Id = "0a2b3c4d"; WorkingDir = "/w"; Projects = []; LastSeen = DateTime.UtcNow - FixtureDurations.sinceHours }
      let html = renderSessionPicker [ previous ] |> renderNode
      // The expression the browser runs says 'ago' too, so look for a worked-out age, not the word.
      System.Text.RegularExpressions.Regex.IsMatch(html, @"\d+[mhd] ago") |> Expect.isFalse "no worked-out 'N ago' text from the server"

    testCase "WHY — the uptime is still on the bar, as an expression the browser counts from the moment the daemon started" <| fun () ->
      let html =
        renderDaemonHealth (DaemonHealthView.fromSnapshot (DateTimeOffset.FromUnixTimeMilliseconds Idle.startMs) Idle.sampleEarlier)
        |> renderNode
      html |> Expect.stringContains "a Ds.text expression" "data-text="
      html |> Expect.stringContains "counting from the start" (string Idle.startMs)

    testCase "WHY — a card still shows both ages, as expressions counting from the creation and the last activity" <| fun () ->
      let info = SageFs.Tests.DashboardParsingTests.SidebarCards.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready []
      let card = sessionCardOf SageFs.Tests.DashboardParsingTests.SidebarCards.now None 0 SessionHealth.Healthy info
      let html = renderSessionsForSession "" [ card ] false |> renderNode
      html |> Expect.stringContains "uptime label" "⏱ "
      html |> Expect.stringContains "last-activity label" "last: "
      (match card.Uptime, card.LastActivity with
       | Since.At created, Since.At lastActive ->
         html |> Expect.stringContains "counts from the creation" (string created)
         html |> Expect.stringContains "counts from the last activity" (string lastActive)
       | _ -> failtest "a built card always knows both moments")

    testTask "WHY — the whole no-session shell is byte-identical a clock step later, because the stream sends a morph only when this string changes" {
      let shell sample = task {
        let! snap = buildNoSessionSnapshotWithSessions (Idle.queriesAt sample) (mkInfra ()) []
        return renderMainContent snap |> renderNode }
      let! first = shell Idle.sampleEarlier
      let! second = shell Idle.sampleLater
      second |> Expect.equal "the shell for the same idle state is the same bytes" first
    }

    testTask "WHY — the whole session shell is byte-identical a clock step later, for the same reason" {
      let sid = WorkerProtocol.SessionId.validate "0a000001" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
      let shell sample = task {
        let! snap, _, _, _ = buildDashboardSnapshotWithSessions (Idle.queriesAt sample) (mkInfra ()) sid sid "" "default" None []
        return renderMainContent snap |> renderNode }
      let! first = shell Idle.sampleEarlier
      let! second = shell Idle.sampleLater
      second |> Expect.equal "the shell for the same idle state is the same bytes" first
    }

    testTask "WHY — the render guard skips a snapshot of the same idle state a clock step later, so renderMainContent never runs for it" {
      let build sample = buildNoSessionSnapshotWithSessions (Idle.queriesAt sample) (mkInfra ()) []
      let! first = build Idle.sampleEarlier
      let! second = build Idle.sampleLater
      let memory =
        match SnapshotRenderGuard.decide SnapshotRenderGuard.RenderMemory.initial first with
        | SnapshotRenderGuard.Decision.Render remembered -> remembered
        | SnapshotRenderGuard.Decision.Skip -> failtest "the first snapshot has nothing to match, so it must render"
      match SnapshotRenderGuard.decide memory second with
      | SnapshotRenderGuard.Decision.Skip -> ()
      | SnapshotRenderGuard.Decision.Render _ -> failtest "an idle snapshot a clock step later was rendered again"
    }
  ]
