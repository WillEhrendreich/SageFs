# SAGEFS DAEMON HEALTH PANEL - EXPLORATION SUMMARY

## 1. DaemonHealth.fs - Complete Content
File: C:\Code\Repos\SageFs\SageFs.Core\Features\DaemonHealth.fs

### Types:
- SessionHealthStatus (Lines 6-12): Ready, Evaluating, WarmingUp, Faulted, Stopped
- OverallHealth (Lines 15-19): Healthy, Degraded, Unhealthy
- SessionHealthSummary (Lines 22-28): SessionId, ProjectName, Status, EvalCount, LastActivity
- LiveTestHealthSummary (Lines 31-36): TotalTests, Passed, Failed, Running
- HealthSnapshot (Lines 39-47): DaemonPid, DaemonPort, Uptime, Version, SessionSummaries, LiveTestingSummary, MemoryMB

### Functions:
- overallStatus (52-60): HealthSnapshot → OverallHealth
- healthEmoji (62-65): OverallHealth → string (🟢/🟡/🔴)
- healthLabel (67-70): OverallHealth → string
- sessionStatusLabel (72-77): SessionHealthStatus → string
- sessionStatusEmoji (79-84): SessionHealthStatus → string (✅/⚡/⏳/❌/⏹️)
- formatUptime (87-93): TimeSpan → string ("Xd Yh", "Xh Ym", "Xm")
- formatSummary (96-126): HealthSnapshot → string (multi-line summary)

## 2. SseWriter.fs - Key Functions
File: C:\Code\Repos\SageFs\SageFs.Core\SseWriter.fs

CORE FUNCTIONS:
- formatSseEvent (Lines 15-21): (eventType: string) (data: string) → string
  Formats SSE event, handles newlines per spec
  
- injectSessionId (Lines 49-56): (sessionId: string option) (json: string) → string
  Prepends {"SessionId":"value", to JSON if Some(sid), no-op if None
  
- formatSseEventMultiline (Lines 24-29): (eventType: string) (lines: string list) → string

PATTERN EXAMPLE - formatEvalTimelineEvent (Lines 246-255):
Creates anon record → JsonSerializer → injectSessionId → formatSseEvent
Returns: "event: eval_timeline\ndata: {..}\n\n"

## 3. DashboardFragments.fs - renderEvalStats Pattern
File: C:\Code\Repos\SageFs\SageFs\DashboardFragments.fs (Lines 131-145)

STRUCTURE:
- Input: EvalStatsView (view model)
- Root: Elem.div [id=DomIds.EvalStats, class="meta"]
- Content: Stats line with optional sparkline and percentiles
- Pattern match on empty string for sparkline, Option.defaultValue "—" for None fields

KEY PATTERN:
1. Single root <div> with unique ID from DomIds
2. "meta" CSS class for styling
3. Pattern matching on option/string fields
4. Text.raw with sprintf for display

## 4. Dashboard.fs - GetEvalTimeline Integration
File: C:\Code\Repos\SageFs\SageFs\Dashboard.fs (Lines 225-373)

FLOW IN pushState():
Line 240-243: Parallelize tasks with Task.WhenAll
Line 244-246: Extract results
Line 250-251: let timelineStats = q.GetEvalTimeline() [SYNC, not Task!]
Line 251: Create view model combining stats + timeline
Line 355-372: Construct DashboardSnapshot with all view models
Line 373: Push single SSE morph via renderMainContent

KEY INSIGHT: GetEvalTimeline is SYNCHRONOUS - reads from shared state

PATTERN FOR ADD GetDaemonHealth:
1. After GetEvalTimeline, add: let healthSnap = q.GetDaemonHealth()
2. Create view: let daemonHealthView = DaemonHealthView.fromSnapshot healthSnap
3. Add to DashboardSnapshot construction
4. In renderMainContent, call renderDaemonHealth snap.DaemonHealth

## 5. DashboardTypes.fs - Types and Records
File: C:\Code\Repos\SageFs\SageFs\DashboardTypes.fs

DashboardQueries (Lines 380-402):
Contains all query functions - ADD:
  GetDaemonHealth: unit → Features.DaemonHealth.HealthSnapshot

DashboardSnapshot (Lines 427-444):
Complete snapshot for atomic rendering - ADD:
  DaemonHealth: DaemonHealthView

EvalStatsView TEMPLATE (Lines 140-166):
- type EvalStatsView with Count, AvgMs, MinMs, MaxMs, Sparkline, P50Ms, P95Ms
- module EvalStatsView with fromStats combining EvalStats + TimelineStats

CREATE SIMILAR DaemonHealthView:
type DaemonHealthView = {
  OverallStatus: string      // emoji + label
  Version: string
  Uptime: string             // formatted
  MemoryMB: int
  DaemonPid: int
  DaemonPort: int
  SessionCount: int
  FaultedCount: int
  LiveTestingSummary: LiveTestHealthSummary option
}

module DaemonHealthView =
  let fromSnapshot (snap: HealthSnapshot) → DaemonHealthView
  - Use DaemonHealth.overallStatus, healthEmoji, healthLabel, formatUptime
  - Compute SessionCount from SessionSummaries.Length
  - Compute FaultedCount by filtering for Faulted status

DomIds Module (Lines 17-44):
ADD: let [<Literal>] DaemonHealth = "daemon-health"

renderMainContent (Lines 743-800):
In app-header Elem.div, after renderEvalStats snap.EvalStats:
ADD: renderDaemonHealth snap.DaemonHealth

## QUICK IMPLEMENTATION STEPS

1. DashboardTypes.fs:
   - Add DaemonHealth="daemon-health" to DomIds (around line 45)
   - Add DaemonHealthView type (after EvalStatsView, ~line 167)
   - Add DaemonHealthView.fromSnapshot module function
   - Add DaemonHealth field to DashboardSnapshot (~line 445)
   - Add GetDaemonHealth to DashboardQueries (~line 402)

2. DashboardFragments.fs:
   - Create renderDaemonHealth function (model after renderEvalStats, ~line 146)
   - Add call in renderMainContent after renderEvalStats (~line 760)

3. Dashboard.fs:
   - In pushState, after line 250-251, add GetDaemonHealth query and view model
   - Add DaemonHealth field to DashboardSnapshot construction (~line 372)

4. OPTIONAL - SseWriter.fs:
   - Create formatDaemonHealthEvent for push-only health events
   - Pattern: anon record → JsonSerializer → injectSessionId → formatSseEvent

## KEY INSIGHTS

- DaemonHealth module is PURE domain logic on HealthSnapshot
- View models (like EvalStatsView) bridge domain to rendering
- GetEvalTimeline and GetDaemonHealth are SYNCHRONOUS shared state reads
- One atomic SSE morph per pushState with entire rendered page
- renderEvalStats pattern: compact inline render, ~15 lines, fits in header
- formatSseEvent + injectSessionId pattern for all health SSE events
