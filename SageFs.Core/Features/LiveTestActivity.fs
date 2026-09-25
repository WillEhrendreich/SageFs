/// What live testing is doing right now, for one session — the single state every
/// surface (dashboard, VS Code, MCP) renders, instead of each rebuilding its own
/// from counts.
module SageFs.Features.LiveTestActivity

open SageFs
open SageFs.Features.LiveTesting

/// Every test in exactly one bucket, so the counts always add up to the total.
type TestTally = {
  Passed: int
  Failed: int
  Running: int
  Stale: int
  NotYetRun: int
  Skipped: int
  Disabled: int
}

/// Whether this session's tests are waiting on a rebuild before they re-run.
[<RequireQualifiedAccess>]
type RebuildProgress =
  | NotRebuilding
  | Rebuilding of testCount: int

/// What the daemon knows about one session's live testing.
type ActivityInput = {
  Activation: LiveTestingActivation
  Discovery: DiscoveryProgress
  Frameworks: string list
  Compile: CompileBlock
  Rebuild: RebuildProgress
  Statuses: TestRunStatus array
}

[<RequireQualifiedAccess>]
type LiveTestActivity =
  | Off
  | Discovering
  | DiscoveryFailed of reason: string
  | NoTestsFound of frameworks: string list
  | BlockedByCompileErrors of file: string * errorCount: int * lastResults: TestTally
  | BlockedByFailedRebuild of reason: string * lastResults: TestTally
  | Rebuilding of testCount: int * lastResults: TestTally
  | Running of TestTally
  | Settled of TestTally

module TestTally =
  let empty = { Passed = 0; Failed = 0; Running = 0; Stale = 0; NotYetRun = 0; Skipped = 0; Disabled = 0 }

  let total (t: TestTally) = t.Passed + t.Failed + t.Running + t.Stale + t.NotYetRun + t.Skipped + t.Disabled

  let private add (t: TestTally) (status: TestRunStatus) =
    match status with
    | TestRunStatus.Passed _ -> { t with Passed = t.Passed + 1 }
    | TestRunStatus.Failed _ -> { t with Failed = t.Failed + 1 }
    | TestRunStatus.Running -> { t with Running = t.Running + 1 }
    | TestRunStatus.Stale -> { t with Stale = t.Stale + 1 }
    | TestRunStatus.Detected
    | TestRunStatus.Queued -> { t with NotYetRun = t.NotYetRun + 1 }
    | TestRunStatus.Skipped _ -> { t with Skipped = t.Skipped + 1 }
    | TestRunStatus.PolicyDisabled -> { t with Disabled = t.Disabled + 1 }

  let ofStatuses (statuses: TestRunStatus array) : TestTally =
    Array.fold add empty statuses

  /// The non-empty buckets, most urgent first: "1 failed · 12 passed · 3 not yet run".
  let buckets (t: TestTally) : string =
    [ t.Failed, "failed"
      t.Running, "running"
      t.Passed, "passed"
      t.Stale, "stale"
      t.NotYetRun, "not yet run"
      t.Skipped, "skipped"
      t.Disabled, "disabled by policy" ]
    |> List.filter (fun (n, _) -> n > 0)
    |> List.map (fun (n, label) -> sprintf "%d %s" n label)
    |> String.concat " · "

  /// Like buckets, but a clean run reads as one.
  let describe (t: TestTally) : string =
    match t.Passed, total t with
    | 1, 1 -> "The 1 test passed"
    | passed, all when passed > 0 && passed = all -> sprintf "All %d tests passed" all
    | _ -> buckets t

module LiveTestActivity =
  let private frameworkName (framework: TestFramework) =
    match framework with
    | TestFramework.Expecto -> "Expecto"
    | TestFramework.XUnit -> "xUnit"
    | TestFramework.NUnit -> "NUnit"
    | TestFramework.MSTest -> "MSTest"
    | TestFramework.TUnit -> "TUnit"
    | TestFramework.Unknown name -> name

  let private providerName (provider: ProviderDescription) =
    match provider with
    | ProviderDescription.AttributeBased d -> frameworkName d.Name
    | ProviderDescription.Custom d -> frameworkName d.Name

  /// What the daemon knows about one session, read from that session's cycle.
  let activityInput (sessionId: string) (cycle: LiveTestCycleState) : ActivityInput =
    let state = cycle.TestState
    let discovery =
      match Map.tryFind sessionId state.SessionDiscovery with
      | Some progress -> progress
      | None -> DiscoveryProgress.NotRequested
    // Only this session's worker restarts for its rebuild; an unattributed one is the primary's.
    let rebuild =
      match cycle.PendingRebuild with
      | Some pending when pending.SessionId = Some sessionId || pending.SessionId = None ->
        RebuildProgress.Rebuilding pending.Tests.Length
      | _ -> RebuildProgress.NotRebuilding
    { Activation = state.Activation
      Discovery = discovery
      Frameworks = state.DetectedProviders |> List.map providerName |> List.distinct
      Compile = cycle.Compile
      Rebuild = rebuild
      Statuses = LiveTestState.statusEntriesForSession sessionId state |> Array.map (fun e -> e.Status) }

  /// Discovery states apply only while no tests are known, so a rediscovery never
  /// hides the latest results; a compile error holds the run back but keeps them,
  /// and a new rebuild outranks the last failed one because it is retrying.
  let decide (input: ActivityInput) : LiveTestActivity =
    let tally = TestTally.ofStatuses input.Statuses
    match input.Activation, input.Discovery, input.Statuses.Length, input.Compile, input.Rebuild with
    | LiveTestingActivation.Inactive, _, _, _, _ -> LiveTestActivity.Off
    | _, DiscoveryProgress.Failed reason, 0, _, _ -> LiveTestActivity.DiscoveryFailed reason
    | _, (DiscoveryProgress.InProgress | DiscoveryProgress.NotRequested), 0, _, _ -> LiveTestActivity.Discovering
    | _, DiscoveryProgress.Completed, 0, _, _ -> LiveTestActivity.NoTestsFound input.Frameworks
    | _, _, _, CompileBlock.CompileErrors (file, errorCount), _ -> LiveTestActivity.BlockedByCompileErrors (file, errorCount, tally)
    | _, _, _, _, RebuildProgress.Rebuilding testCount -> LiveTestActivity.Rebuilding (testCount, tally)
    | _, _, _, CompileBlock.RebuildFailed reason, _ -> LiveTestActivity.BlockedByFailedRebuild (reason, tally)
    | _ when tally.Running > 0 -> LiveTestActivity.Running tally
    | _ -> LiveTestActivity.Settled tally

  let private lastResults (tally: TestTally) =
    match TestTally.buckets tally with
    | "" -> "none yet"
    | listed -> listed

  /// The line of a failed build the user must act on: the first compiler error
  /// (file name only, whitespace runs collapsed) — not the build's header line
  /// or its hints; else the first line there is.
  let private rebuildHeadline (reason: string) =
    let lines =
      reason.Split '\n'
      |> Array.map (fun line -> System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"\s+", " "))
      |> Array.filter (fun line -> line <> "")
    let withFileNameOnly (line: string) =
      let errorAt = line.IndexOf(": error ", System.StringComparison.Ordinal)
      match line.LastIndexOf('(', errorAt) with
      | locationAt when locationAt > 0 ->
        match System.IO.Path.GetFileName(line.Substring(0, locationAt)) with
        | null
        | "" -> line
        | fileName -> fileName + line.Substring locationAt
      | _ -> line
    match lines |> Array.tryFind (fun line -> line.Contains(": error ", System.StringComparison.Ordinal)) with
    | Some error -> withFileNameOnly error
    | None ->
      match Array.tryHead lines with
      | Some first -> first
      | None -> "the rebuild failed"

  /// The one wording every surface uses.
  let describe (activity: LiveTestActivity) : string =
    match activity with
    | LiveTestActivity.Off -> "Live testing is off"
    | LiveTestActivity.Discovering -> "Looking for tests…"
    | LiveTestActivity.DiscoveryFailed reason -> sprintf "Could not discover tests: %s" reason
    | LiveTestActivity.NoTestsFound [] -> "No tests found — no test framework detected in this session"
    | LiveTestActivity.NoTestsFound frameworks -> sprintf "No tests found (%s detected)" (String.concat ", " frameworks)
    | LiveTestActivity.BlockedByCompileErrors (file, errorCount, tally) ->
      let name =
        match System.IO.Path.GetFileName file with
        | null -> file
        | fileName -> fileName
      let plural = match errorCount with 1 -> "" | _ -> "s"
      sprintf "Waiting for %s to compile (%d error%s) — showing the last good results: %s" name errorCount plural (lastResults tally)
    | LiveTestActivity.BlockedByFailedRebuild (reason, tally) ->
      sprintf "Tests could not re-run: %s — showing the last good results: %s" (rebuildHeadline reason) (lastResults tally)
    | LiveTestActivity.Rebuilding (testCount, tally) ->
      let plural = match testCount with 1 -> "" | _ -> "s"
      sprintf "Rebuilding to re-run %d test%s — showing the last results: %s" testCount plural (lastResults tally)
    | LiveTestActivity.Running tally -> sprintf "Running %d of %d tests…" tally.Running (TestTally.total tally)
    | LiveTestActivity.Settled tally -> TestTally.describe tally

  /// A stable snake_case name per activity, for clients to switch on.
  let wireKind (activity: LiveTestActivity) : string =
    match activity with
    | LiveTestActivity.Off -> "off"
    | LiveTestActivity.Discovering -> "discovering"
    | LiveTestActivity.DiscoveryFailed _ -> "discovery_failed"
    | LiveTestActivity.NoTestsFound _ -> "no_tests_found"
    | LiveTestActivity.BlockedByCompileErrors _ -> "blocked_by_compile_errors"
    | LiveTestActivity.BlockedByFailedRebuild _ -> "blocked_by_failed_rebuild"
    | LiveTestActivity.Rebuilding _ -> "rebuilding"
    | LiveTestActivity.Running _ -> "running"
    | LiveTestActivity.Settled _ -> "settled"

  /// The counts behind an activity; states reached before any test is known have none.
  let tallyOf (activity: LiveTestActivity) : TestTally =
    match activity with
    | LiveTestActivity.Running tally
    | LiveTestActivity.Settled tally
    | LiveTestActivity.Rebuilding (_, tally)
    | LiveTestActivity.BlockedByCompileErrors (_, _, tally)
    | LiveTestActivity.BlockedByFailedRebuild (_, tally) -> tally
    | LiveTestActivity.Off
    | LiveTestActivity.Discovering
    | LiveTestActivity.DiscoveryFailed _
    | LiveTestActivity.NoTestsFound _ -> TestTally.empty

  /// The wording for a status bar: as short as the state allows, the same words as describe.
  let shortLabel (activity: LiveTestActivity) : string =
    let plural count = match count with 1 -> "" | _ -> "s"
    match activity with
    | LiveTestActivity.Off -> "Live testing off"
    | LiveTestActivity.Discovering -> "Looking for tests…"
    | LiveTestActivity.DiscoveryFailed _ -> "Test discovery failed"
    | LiveTestActivity.NoTestsFound _ -> "No tests found"
    | LiveTestActivity.BlockedByCompileErrors (file, errorCount, _) ->
      let name =
        match System.IO.Path.GetFileName file with
        | null -> file
        | fileName -> fileName
      sprintf "%s: %d error%s" name errorCount (plural errorCount)
    | LiveTestActivity.BlockedByFailedRebuild _ -> "Tests could not re-run"
    | LiveTestActivity.Rebuilding (testCount, _) -> sprintf "Rebuilding %d test%s" testCount (plural testCount)
    | LiveTestActivity.Running tally -> sprintf "Running %d of %d" tally.Running (TestTally.total tally)
    | LiveTestActivity.Settled tally ->
      match tally.Passed, TestTally.total tally with
      | 1, 1 -> "1 passed"
      | passed, all when passed > 0 && passed = all -> sprintf "All %d passed" all
      | _ -> TestTally.buckets tally
