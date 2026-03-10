# DAEMON HEALTH PANEL - QUICK REFERENCE GUIDE

## FILE PATHS & LINE NUMBERS

### 1. DaemonHealth.fs
Path: C:\Code\Repos\SageFs\SageFs.Core\Features\DaemonHealth.fs

SessionHealthStatus (6-12):
  Ready, Evaluating, WarmingUp, Faulted, Stopped

OverallHealth (15-19):
  Healthy, Degraded, Unhealthy

SessionHealthSummary (22-28):
  SessionId: string
  ProjectName: string
  Status: SessionHealthStatus
  EvalCount: int
  LastActivity: DateTimeOffset

LiveTestHealthSummary (31-36):
  TotalTests: int
  Passed: int
  Failed: int
  Running: int

HealthSnapshot (39-47) - **THE MAIN DATA TYPE**
  DaemonPid: int
  DaemonPort: int
  Uptime: TimeSpan
  Version: string
  SessionSummaries: SessionHealthSummary list
  LiveTestingSummary: LiveTestHealthSummary option
  MemoryMB: int

Module Functions (49-127):
  overallStatus (52-60) - Takes HealthSnapshot, returns OverallHealth
  healthEmoji (62-65) - Takes OverallHealth, returns emoji string
  healthLabel (67-70) - Takes OverallHealth, returns label string
  sessionStatusLabel (72-77) - Takes SessionHealthStatus, returns label
  sessionStatusEmoji (79-84) - Takes SessionHealthStatus, returns emoji
  formatUptime (87-93) - Takes TimeSpan, returns formatted string
  formatSummary (96-126) - Takes HealthSnapshot, returns multi-line summary

### 2. SseWriter.fs
Path: C:\Code\Repos\SageFs\SageFs.Core\SseWriter.fs

formatSseEvent (15-21):
  Signature: (eventType: string) (data: string) → string
  Handles newlines in data per SSE spec

injectSessionId (49-56):
  Signature: (sessionId: string option) (json: string) → string
  Prepends {"SessionId":"...", to JSON if Some, else no-op

formatEvalTimelineEvent (246-255):
  Pattern example for health event formatting

### 3. DashboardFragments.fs
Path: C:\Code\Repos\SageFs\SageFs\DashboardFragments.fs

renderEvalStats (131-145):
  PATTERN TO FOLLOW for renderDaemonHealth
  - Input: EvalStatsView
  - Root: Elem.div [id=DomIds.EvalStats, class="meta"]
  - Content: Inline stats with optional sparkline
  - ~15 lines of code

renderMainContent (743-800):
  Entry point for all dashboard rendering
  App header section at line ~756-767
  Call renderEvalStats at line 760

### 4. Dashboard.fs
Path: C:\Code\Repos\SageFs\SageFs\Dashboard.fs

pushState function (225-373):
  Line 240-243: Parallelize tasks with Task.WhenAll
  Line 250-251: Get timeline stats and create view model
  Line 355-372: Construct DashboardSnapshot
  Line 373: Push SSE morph

GetEvalTimeline pattern (250-251):
  let timelineStats = q.GetEvalTimeline()  // SYNC call!
  let evalStatsView = EvalStatsView.fromStats stats timelineStats

### 5. DashboardTypes.fs
Path: C:\Code\Repos\SageFs\SageFs\DashboardTypes.fs

DomIds module (17-44):
  Add DaemonHealth="daemon-health" around line 45

EvalStatsView template (140-166):
  type with Count, AvgMs, MinMs, MaxMs, Sparkline, P50Ms, P95Ms
  fromStats module function combines EvalStats + TimelineStats

DashboardQueries (380-402):
  All query functions defined here
  GetEvalTimeline at line 401

DashboardSnapshot (427-444):
  All fields for atomic rendering
  EvalStats at line 433

---

## IMPLEMENTATION CHANGES

### Change 1: DashboardTypes.fs - Add DaemonHealth constant
Location: Line ~45 in DomIds module

Add:
  let [<Literal>] DaemonHealth = "daemon-health"

### Change 2: DashboardTypes.fs - Add DaemonHealthView type
Location: After EvalStatsView (after line 166)

Add:
  type DaemonHealthView = {
    OverallStatus: string
    Version: string
    Uptime: string
    MemoryMB: int
    DaemonPid: int
    DaemonPort: int
    SessionCount: int
    FaultedCount: int
    LiveTestingSummary: LiveTestHealthSummary option
  }

  module DaemonHealthView =
    let fromSnapshot (snap: Features.DaemonHealth.HealthSnapshot) : DaemonHealthView =
      let health = Features.DaemonHealth.overallStatus snap
      let emoji = Features.DaemonHealth.healthEmoji health
      let label = Features.DaemonHealth.healthLabel health
      { OverallStatus = sprintf "%s %s" emoji label
        Version = snap.Version
        Uptime = Features.DaemonHealth.formatUptime snap.Uptime
        MemoryMB = snap.MemoryMB
        DaemonPid = snap.DaemonPid
        DaemonPort = snap.DaemonPort
        SessionCount = snap.SessionSummaries.Length
        FaultedCount = snap.SessionSummaries 
                       |> List.filter (fun s -> s.Status = Features.DaemonHealth.SessionHealthStatus.Faulted) 
                       |> List.length
        LiveTestingSummary = snap.LiveTestingSummary }

### Change 3: DashboardTypes.fs - Add to DashboardQueries
Location: Line 402, after GetEvalTimeline

Add:
  /// Read current DaemonHealth snapshot from shared state.
  GetDaemonHealth: unit -> Features.DaemonHealth.HealthSnapshot

### Change 4: DashboardTypes.fs - Add to DashboardSnapshot
Location: Line 444, after BindingsPanel

Add:
  DaemonHealth: DaemonHealthView

### Change 5: DashboardFragments.fs - Create renderDaemonHealth
Location: After renderEvalStats (around line 146)

Add:
  let renderDaemonHealth (health: DaemonHealthView) =
    Elem.div [ Attr.id DomIds.DaemonHealth; Attr.class' "meta" ] [
      Text.raw (sprintf "%s · v%s · up %s · %dMB" 
        health.OverallStatus health.Version health.Uptime health.MemoryMB)
      match health.SessionCount with
      | 0 -> ()
      | count ->
        Elem.span [ Attr.class' "daemon-sessions meta" ] [
          Text.raw (sprintf " · %d sessions" count)
        ]
        match health.FaultedCount with
        | 0 -> ()
        | faulted ->
          Elem.span [ Attr.class' "daemon-faulted meta"; Attr.style "color: #ff6b6b;" ] [
            Text.raw (sprintf " (%d faulted)" faulted)
          ]
    ]

### Change 6: DashboardFragments.fs - Add call in renderMainContent
Location: Line 760, after renderEvalStats call

Add:
  renderDaemonHealth snap.DaemonHealth

Full context around line 760:
  Elem.div [ Attr.class' "flex-row"; Attr.style "gap: 0.75rem; align-items: center;" ] [
    renderSessionStatus snap.SessionState snap.SessionId snap.WorkingDir snap.WarmupProgress
    renderEvalStats snap.EvalStats
    renderDaemonHealth snap.DaemonHealth  // ← ADD THIS
    snap.ThemePicker
    ...
  ]

### Change 7: Dashboard.fs - Query and create view model
Location: After line 251 (after GetEvalTimeline)

Add:
  let healthSnap = q.GetDaemonHealth()
  let daemonHealthView = DaemonHealthView.fromSnapshot healthSnap

### Change 8: Dashboard.fs - Add to DashboardSnapshot construction
Location: Line 372, after BindingsPanel

Add:
  DaemonHealth = daemonHealthView

Full context around lines 370-372:
  ThemeVars = renderThemeVars themeName
  BindingsPanel = bindingsPanel
  DaemonHealth = daemonHealthView   // ← ADD THIS
}

---

## OPTIONAL: SseWriter.fs - Health event formatter
Location: Around line 289 (after formatDiagnosisReadyEvent)

Add:
  /// Format a daemon health snapshot as an SSE event string
  let formatDaemonHealthEvent (opts: JsonSerializerOptions) (sessionId: string option) (snap: Features.DaemonHealth.HealthSnapshot) : string =
    let status = Features.DaemonHealth.overallStatus snap
    let payload =
      {| DaemonPid = snap.DaemonPid
         DaemonPort = snap.DaemonPort
         Uptime = Features.DaemonHealth.formatUptime snap.Uptime
         Version = snap.Version
         OverallStatus = Features.DaemonHealth.healthLabel status
         UptimeMs = snap.Uptime.TotalMilliseconds
         MemoryMB = snap.MemoryMB
         SessionCount = snap.SessionSummaries.Length
         Sessions = snap.SessionSummaries 
                    |> List.map (fun s ->
                      {| SessionId = s.SessionId
                         ProjectName = s.ProjectName
                         Status = Features.DaemonHealth.sessionStatusLabel s.Status
                         EvalCount = s.EvalCount |})
         LiveTesting = snap.LiveTestingSummary 
                       |> Option.map (fun t ->
                         {| TotalTests = t.TotalTests
                            Passed = t.Passed
                            Failed = t.Failed
                            Running = t.Running |}) |}
    let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
    formatSseEvent "daemon_health" json

---

## SUMMARY OF CHANGES

File                        | Change Type      | Location        | Complexity
DashboardTypes.fs           | Add constant     | Line ~45        | 1 line
DashboardTypes.fs           | Add type+module  | Line ~167       | 25 lines
DashboardTypes.fs           | Add record field | Line 402        | 2 lines
DashboardTypes.fs           | Add record field | Line 444        | 1 line
DashboardFragments.fs       | Add function     | Line ~146       | 18 lines
DashboardFragments.fs       | Add call         | Line ~760       | 1 line
Dashboard.fs                | Add queries      | Line ~252       | 2 lines
Dashboard.fs                | Add field        | Line ~372       | 1 line
SseWriter.fs (optional)     | Add function     | Line ~289       | 30 lines

TOTAL: ~81 lines of new code across 3-4 files

KEY IMPLEMENTATION ORDER:
1. First: DashboardTypes.fs (types must exist before Dashboard.fs compiles)
2. Second: Dashboard.fs (queries and snapshot)
3. Third: DashboardFragments.fs (rendering)
4. Optional: SseWriter.fs (event formatting for push stream)
