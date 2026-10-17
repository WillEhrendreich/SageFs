/// An idle dashboard must not be pushed again. The stream compares the full-shell HTML it rendered with
/// the HTML it last sent (and `SnapshotRenderGuard` compares the snapshot first), and skips the push when
/// they match. That only works when the HTML is a pure function of real state. A label that counts the
/// clock ("up 5m", "last: 3m ago") made two renders of the SAME idle state differ the moment a minute
/// turned, so the next unrelated event pushed the whole `#main` for nothing.
///
/// These cases hold the clock still and move it: the same state, rendered a clock step apart, must be the
/// same bytes. The ticking itself lives in the browser (`ElapsedClock`).
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
  /// One idle daemon sampled once, `uptime` after it started. Only the uptime differs between samples.
  let healthAt (uptime: TimeSpan) : HealthSnapshot =
    { DaemonPid = 1
      DaemonPort = 37749
      Uptime = uptime
      Version = "0.0.0"
      SessionSummaries = []
      LiveTestingSummary = None
      MemoryMB = 128
      Anomalies = []
      GcDumpOutcome = None
      MemoryPressure = SageFs.MemoryPressure.Normal
      SupervisorHealth = SageFs.SupervisorHealth.Healthy }

  /// A daemon start no case compares with a clock: any moment will do, as long as it is not the epoch's 0.
  let startMs = DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()

  let earlier = FixtureDurations.uptimeUnread
  let later = FixtureDurations.uptimeUnread + FixtureDurations.idleClockStep

  let queriesAt (uptime: TimeSpan) : DashboardQueries =
    { mkQueries (ref 0) [] with GetDaemonHealth = fun () -> Some (healthAt uptime) }

[<Tests>]
let tests =
  testList "an idle dashboard renders the same bytes, so the stream has nothing to push" [

    testCase "WHY — the daemon health bar does not render the clock, because 'up 10m' became 'up 12m' between two renders of one idle daemon" <| fun () ->
      let bar uptime =
        renderDaemonHealth (DaemonHealthView.fromSnapshot DateTimeOffset.UnixEpoch (Idle.healthAt uptime)) |> renderNode
      bar Idle.later |> Expect.equal "two samples of one idle daemon render the same bytes" (bar Idle.earlier)

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
        renderDaemonHealth (DaemonHealthView.fromSnapshot (DateTimeOffset.FromUnixTimeMilliseconds Idle.startMs) (Idle.healthAt Idle.earlier))
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
      let shell uptime = task {
        let! snap = buildNoSessionSnapshotWithSessions (Idle.queriesAt uptime) (mkInfra ()) []
        return renderMainContent snap |> renderNode }
      let! first = shell Idle.earlier
      let! second = shell Idle.later
      second |> Expect.equal "the shell for the same idle state is the same bytes" first
    }

    testTask "WHY — the whole session shell is byte-identical a clock step later, for the same reason" {
      let sid = WorkerProtocol.SessionId.validate "0a000001" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
      let shell uptime = task {
        let! snap, _, _, _ = buildDashboardSnapshotWithSessions (Idle.queriesAt uptime) (mkInfra ()) sid sid "" "default" None []
        return renderMainContent snap |> renderNode }
      let! first = shell Idle.earlier
      let! second = shell Idle.later
      second |> Expect.equal "the shell for the same idle state is the same bytes" first
    }

    testTask "WHY — the render guard skips a snapshot of the same idle state a clock step later, so renderMainContent never runs for it" {
      let build uptime = buildNoSessionSnapshotWithSessions (Idle.queriesAt uptime) (mkInfra ()) []
      let! first = build Idle.earlier
      let! second = build Idle.later
      let memory =
        match SnapshotRenderGuard.decide SnapshotRenderGuard.RenderMemory.initial first with
        | SnapshotRenderGuard.Decision.Render remembered -> remembered
        | SnapshotRenderGuard.Decision.Skip -> failtest "the first snapshot has nothing to match, so it must render"
      match SnapshotRenderGuard.decide memory second with
      | SnapshotRenderGuard.Decision.Skip -> ()
      | SnapshotRenderGuard.Decision.Render _ -> failtest "an idle snapshot a clock step later was rendered again"
    }
  ]
