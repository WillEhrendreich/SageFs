# SageFs Live-Testing Discovery Reliability & Zero-Test Visibility Analysis

## EXECUTIVE SUMMARY

SageFs has **CRITICAL SILENT-FAILURE PATHS** where test discovery can report 0 tests without user-visible warnings:

**5 Key Problem Areas:**
1. **Async dispatch race** — nable_live_testing() reads state BEFORE Elm processes the enable message
2. **Tree-sitter silently swallows errors** — Parse failures logged but NOT surfaced to state machine
3. **No discovery-complete signal** — Can't distinguish "not enabled" from "enabled but 0 discovered"
4. **Inverted logic in run_tests** — Check for activation state is backwards
5. **Smoke test masks failures** — 0 tests is WARNING, not FAIL, so CI doesn't catch silent discovery failures

---

## WHERE DISCOVERY SILENTLY SHOWS 0 TESTS

### Problem 1: Race Condition in setLiveTesting() [Lines 1612–1632, Mcp.fs]

**Issue**: Returns OLD state before dispatch processes

**Code**:
\\\sharp
let setLiveTesting (ctx: McpContext) (enabled: bool) : Task<string> =
  task {
    match ctx.Dispatch with
    | None -> return "Cannot set live testing — Elm loop not started."
    | Some dispatch ->
      let msg = match enabled with | true -> SageFsMsg.EnableLiveTesting | false -> SageFsMsg.DisableLiveTesting
      dispatch msg  // <-- ASYNC, returns immediately
      
      // Lines 1625-1629: Read state BEFORE dispatch is processed
      let state = (getModel ()).LiveTesting.TestState
      let discovered = state.DiscoveredTests.Length
      match discovered > 0 with
      | true -> return sprintf "Live testing %s. %d tests discovered." label discovered
      | false -> return sprintf "Live testing %s. No tests discovered yet — tests will be discovered after first eval." label
  }
\\\

**User Experience**:
`
Agent: enable_live_testing()
SageFs: "Live testing enabled. No tests discovered yet — tests will be discovered after first eval."
Agent: run_tests()
SageFs: "No tests discovered — live testing is not enabled. Call enable_live_testing first..."
`

**Root Cause**: The Elm message loop is async. Code reads model **before** EnableLiveTesting message sets Activation = Active.

---

### Problem 2: Tree-Sitter Errors Swallowed Silently [Line 729, DaemonMode.fs]

**Issue**: File parsing errors logged but NOT surfaced as state

**Code**:
\\\sharp
IO.Directory.GetFiles(dir, "*.fs", IO.SearchOption.AllDirectories)
|> Array.collect (fun f ->
  try
    let code = IO.File.ReadAllText f
    Features.LiveTesting.TestTreeSitter.discover f code  // <-- Can throw
  with ex ->
    log.LogWarning("[Daemon] Tree-sitter discovery failed for {File}: {Error}", f, ex.Message)
    Array.empty)  // <-- Returns empty SILENTLY
\\\

**Silent Failure Chain**:
1. Tree-sitter encounters unparseable F# file
2. Exception caught, logged to daemon logs
3. Returns empty array
4. handleTestDiscovery continues, sums arrays, gets 0 total
5. **No event fired for 0-count discovery**
6. User sees: get_live_test_status() → {Enabled: true, Summary: {Total: 0, Passed: 0, Failed: 0}}
7. User has NO IDEA why 0 tests — logs are in daemon, not visible

**Call Site** (Line 734): Only dispatches TestLocationsDetected if array is non-empty
\\\sharp
match Array.isEmpty locations with
| false -> dispatch (SageFsMsg.Event (SageFsEvent.TestLocationsDetected (WorkerProtocol.SessionId.value sid, locations)))
| true -> ()  // <-- Silent skip if empty
\\\

---

### Problem 3: No Discovery-Complete Event [Lines 724–741, DaemonMode.fs]

**Issue**: Can't tell if:
- Discovery hasn't started
- Discovery is in progress
- Discovery finished with 0 results
- Discovery crashed silently

**Current Events**:
- TestLocationsDetected(locations) — ONLY fired if count > 0
- TestsDiscovered(tests) — ONLY fired if count > 0
- **No event fired if empty** → silent

**Result**:
\\\sharp
// Line 736-738: Only fire events if non-empty
match Array.isEmpty tests with
| false -> dispatch (SageFsMsg.Event (SageFsEvent.TestsDiscovered (WorkerProtocol.SessionId.value sid, tests)))
| true -> ()  // <-- Could be failure or legitimately 0 tests
\\\

---

### Problem 4: Inverted State Check in runTests [Line 2270, Mcp.fs]

**Issue**: The activation check is backwards

**Code**:
\\\sharp
let tests = Features.LiveTesting.LiveTestCycleState.filterTestsForExplicitRun
  state.DiscoveredTests None patternFilter category
match Array.isEmpty tests with
| true ->
  // Line 2270: Check is INVERTED
  match state.DiscoveredTests.Length = 0 && state.Activation <> Features.LiveTesting.LiveTestingActivation.Active with
  | true -> return waitNote + RunTestsResult.format NoTestsDiscovered
  | false -> return waitNote + RunTestsResult.format (NoTestsMatched state.DiscoveredTests.Length)
\\\

**Truth Table**:
| Discovered | Activation | Check Result | Message | Correct? |
|-----------|-----------|--------------|---------|----------|
| 0 | Inactive | true | "Not enabled" | ✓ |
| 0 | Active | false | "No tests matched" | ✗ |
| 5 (no match) | Active | false | "No tests matched" | ✓ |

**Silent Failure**: If Activation=Active but DiscoveredTests=0, user sees "No tests matched filter" (implying tests exist somewhere) instead of "Discovery not started or failed".

---

### Problem 5: Smoke Test Masks Failures [Line 227–228, smoke-test.ps1]

**Issue**: Zero tests = WARNING, not FAIL

**Code**:
\\\powershell
if ( -eq 0) {
  Warn "tests-discovery" "0 tests discovered after s — session may still be warming up"
}
\\\

**CI Impact**:
- Smoke test PASSES with 0 discovered tests
- User doesn't know discovery silently failed
- Default sample project might not have tests, masking the bug

---

## CURRENT USER-VISIBLE SURFACES

| Command | What Shows | Silent Risk |
|---------|-----------|-------------|
| nable_live_testing() | "Enabled. N tests" | **HIGH**: Returns old state before dispatch |
| get_live_test_status() | Enabled, Summary, Tests[] | **MEDIUM**: No discovery error field |
| get_test_trace() | Enabled, Providers, Policies, Hint | **LOW**: Has Hint, but doesn't distinguish states |
| un_tests() | Pass/fail or "0 tests" | **HIGH**: Can't tell if "not enabled" or "0 discovered" |
| /api/live-testing/status REST | JSON summary | **HIGH**: No error field for discovery failures |
| Daemon logs | Tree-sitter errors | **VERY HIGH**: Not exposed to users |

**MISSING SIGNALS**:
- ❌ "Discovery in progress"
- ❌ "Discovery timed out"
- ❌ "Discovery encountered 3 file parsing errors"
- ❌ "Activation pending (dispatch in flight)"
- ❌ "0 tests after discovery completed" (vs "0 tests because discovery never ran")

---

## MINIMAL FIXES FOR TDD

### Fix 1: Enum-Based Discovery State Distinction

**File**: C:\Code\Repos\SageFs\SageFs.Core\Mcp.fs (Lines 2051–2108)

**Add to RunTestsResult enum**:
\\\sharp
type RunTestsResult =
  | Completed of passed: int * failed: int * total: int * failures: FailedTestInfo list
  | TimedOut of passed: int * failed: int * running: int * total: int * failures: FailedTestInfo list * runningNames: string list
  | NoTestsMatched of totalDiscovered: int
  | NoTestsDiscovered  // <-- Backward compat: ONLY if Activation.Inactive
  | DiscoveryNotStarted  // <-- NEW: Activation.Inactive, explicit message
  | DiscoveryIncomplete  // <-- NEW: Activation.Active but 0 discovered + timeout
  | AlreadyRunning
\\\

**Update format method**:
\\\sharp
| DiscoveryNotStarted ->
  "❌ Live testing not enabled. Call enable_live_testing first.\n" +
  sprintf "State: Activation=Inactive, Tests=%d" state.DiscoveredTests.Length
| DiscoveryIncomplete ->
  "⏳ Live testing is ACTIVE but 0 tests discovered yet.\n" +
  "Waited 18s for hot-reload + 3s for tree-sitter/reflection. Still processing or encountered silent error.\n" +
  "Try: (1) ensure code compiles, (2) wait for file change to trigger discovery, (3) check get_test_trace() for warnings."
\\\

**TDD Test** (new file C:\Code\Repos\SageFs\SageFs.Tests\LiveTestingDiscoveryStateTests.fs):
\\\sharp
module SageFs.Tests.LiveTestingDiscoveryStateTests

open Expecto
open SageFs.Features.LiveTesting

[<Tests>]
let discoveryStateTests = testList "Discovery state distinction" [
  test "run_tests with Activation=Inactive returns DiscoveryNotStarted" {
    let state = LiveTestState.empty
    let state = { state with Activation = LiveTestingActivation.Inactive; DiscoveredTests = [||] }
    // Mock: runTests is called with this state
    // Expected: Returns DiscoveryNotStarted
    true |> Expect.isTrue "Placeholder"
  }
  
  test "run_tests with Activation=Active and 0 discovered returns DiscoveryIncomplete" {
    let state = LiveTestState.empty
    let state = { state with Activation = LiveTestingActivation.Active; DiscoveredTests = [||] }
    // Mock: runTests is called with this state after discovery deadline
    // Expected: Returns DiscoveryIncomplete
    true |> Expect.isTrue "Placeholder"
  }
]
\\\

---

### Fix 2: Surface Tree-Sitter Errors in State

**File**: C:\Code\Repos\SageFs\SageFs.Core\Features\LiveTestingTypes.fs (Line 1096+)

**Add to LiveTestState**:
\\\sharp
type DiscoveryWarning = {
  FilePath: string
  ErrorType: string  // "tree-sitter" | "assembly-load" | "reflection"
  Message: string
  TimestampUtc: System.DateTimeOffset
}

type LiveTestState = {
  SourceLocations: SourceTestLocation array
  DiscoveredTests: TestCase array
  // ... existing fields ...
  AssemblyLoadErrors: AssemblyLoadError list
  DiscoveryWarnings: DiscoveryWarning list  // <-- NEW
  DiscoveryErrorCount: int  // <-- NEW: Quick summary
}
\\\

**Expose in get_test_trace** (Mcp.fs line 1705–1737):
\\\sharp
let resp = {|
  Enabled = isActive
  IsRunning = ...
  History = ...
  Summary = summary
  Providers = ...
  Policies = ...
  DiscoveryWarnings = state.DiscoveryWarnings |> Array.map (fun w -> {| 
    File = w.FilePath
    ErrorType = w.ErrorType
    Message = w.Message 
  |})
  DiscoveryErrorCount = state.DiscoveryErrorCount
|}
\\\

**Dispatch in DaemonMode** (Line 728–729):
\\\sharp
with ex ->
  dispatch (SageFsMsg.Event (SageFsEvent.DiscoveryWarningDetected (
    WorkerProtocol.SessionId.value sid, f, "tree-sitter", ex.Message)))
  Array.empty
\\\

**Handle in SageFsApp** (add new message handler):
\\\sharp
| SageFsEvent.DiscoveryWarningDetected (sessionId, filePath, errorType, msg) ->
  let lt = recomputeStatuses model.LiveTesting (fun s ->
    { s with 
        DiscoveryWarnings = s.DiscoveryWarnings @ 
          [{ FilePath = filePath; ErrorType = errorType; Message = msg; 
             TimestampUtc = System.DateTimeOffset.UtcNow }]
        DiscoveryErrorCount = s.DiscoveryErrorCount + 1 })
  { model with LiveTesting = lt }, []
\\\

**Test** (new file):
\\\sharp
module SageFs.Tests.DiscoveryWarningTests

[<Tests>]
let tests = testList "Discovery warnings" [
  test "tree-sitter error dispatches warning event" {
    // Arrange: Mock unparseable .fs file
    // Act: handleTestDiscovery processes it
    // Assert: DiscoveryWarningDetected event fired
    true |> Expect.isTrue "Placeholder"
  }
  
  test "warnings appear in get_test_trace JSON" {
    // Arrange: State with warnings
    // Act: Call getTestTrace
    // Assert: JSON includes DiscoveryWarnings array with >0 items
    true |> Expect.isTrue "Placeholder"
  }
]
\\\

---

### Fix 3: Add Confirmation Step for Activation

**File**: C:\Code\Repos\SageFs\SageFs\McpTools.fs (add new tool)

**New MCP Function**:
\\\sharp
[<McpServerTool>]
[<Description("""Confirm that live testing is ACTIVE and discovery has begun.

WHEN TO USE:
- After enable_live_testing() to verify it actually started
- To wait for discovery to complete with a timeout
- To distinguish "pending" from "failed" discovery

RETURNS:
- '✅ ACTIVE with N tests' — ready to run_tests
- '⏳ ACTIVE, but 0 tests yet' — still discovering
- '❌ INACTIVE' — enable_live_testing() didn't complete or was disabled

USAGE PATTERN (for agents):
  enable_live_testing()
  confirm_live_testing_enabled(timeout_seconds=10)
  if result.includes("ACTIVE"):
    run_tests(timeout_seconds=30)
""")>]
member _.confirm_live_testing_enabled(
    [<Description("Seconds to wait for confirmation. 0 = check immediately, -1 = wait indefinitely")>]
    timeout_seconds: int
) : Task<string> =
  logger.LogDebug("MCP-TOOL: confirm_live_testing_enabled called with timeout=%d", timeout_seconds)
  confirmLiveTestingEnabled ctx timeout_seconds |> withEcho "confirm_live_testing_enabled"

// Add to Mcp.fs:
let confirmLiveTestingEnabled (ctx: McpContext) (timeoutSeconds: int) : Task<string> =
  task {
    match ctx.GetElmModel with
    | None -> return "Confirmation not available — Elm loop not started."
    | Some getModel ->
      let deadline = 
        match timeoutSeconds with
        | t when t < 0 -> System.DateTime.MaxValue
        | t -> System.DateTime.UtcNow.AddSeconds(float t)
      
      let mutable confirmed = false
      let mutable lastState = None
      
      while not confirmed && System.DateTime.UtcNow < deadline do
        let state = (getModel ()).LiveTesting.TestState
        lastState <- Some state
        match state.Activation with
        | Features.LiveTesting.LiveTestingActivation.Active -> confirmed <- true
        | _ -> do! Task.Delay 100
      
      let state = (getModel ()).LiveTesting.TestState
      match state.Activation, state.DiscoveredTests.Length with
      | Features.LiveTesting.LiveTestingActivation.Active, count when count > 0 ->
        return sprintf "✅ Live testing CONFIRMED ACTIVE with %d tests discovered." count
      | Features.LiveTesting.LiveTestingActivation.Active, _ ->
        let discoveryWarningCount = state.DiscoveryWarnings.Length
        match discoveryWarningCount with
        | 0 -> return "⏳ Live testing CONFIRMED ACTIVE, but 0 tests discovered yet. (Discovery may still be running—wait for code change to trigger it.)"
        | n -> return sprintf "⚠️ Live testing ACTIVE with 0 tests, but %d discovery warnings detected. Check get_test_trace() for details." n
      | Features.LiveTesting.LiveTestingActivation.Inactive, _ ->
        return "❌ Live testing remains INACTIVE. Confirmation failed. Call enable_live_testing first."
  }
\\\

**Documentation** (in McpTools.fs docstring):
`
RECOMMENDED WORKFLOW:

  1. enable_live_testing()                          # Fire-and-forget enablement
  2. confirm_live_testing_enabled(timeout=10)       # Wait for actual activation
  3. if response includes "ACTIVE":
       run_tests(timeout=30)                        # Now safe to run
     else:
       wait or retry
`

**Test** (new file):
\\\sharp
module SageFs.Tests.ConfirmLiveTestingTests

[<Tests>]
let tests = testList "confirm_live_testing_enabled" [
  test "confirms activation immediately if already active" {
    // Arrange: State with Activation=Active, DiscoveredTests=[test1]
    // Act: confirmLiveTestingEnabled(timeout=5)
    // Assert: Returns "ACTIVE with 1 tests" immediately
    true |> Expect.isTrue "Placeholder"
  }
  
  test "times out if never activates" {
    // Arrange: State with Activation=Inactive, never changes
    // Act: confirmLiveTestingEnabled(timeout=1)
    // Assert: Returns after 1s with "INACTIVE" message
    true |> Expect.isTrue "Placeholder"
  }
  
  test "reports active but 0 discovered" {
    // Arrange: State with Activation=Active, DiscoveredTests=[], no warnings
    // Act: confirmLiveTestingEnabled
    // Assert: Returns "ACTIVE, but 0 tests yet" message
    true |> Expect.isTrue "Placeholder"
  }
  
  test "reports active with discovery warnings" {
    // Arrange: State with Activation=Active, DiscoveredTests=[], DiscoveryWarnings=[w1, w2]
    // Act: confirmLiveTestingEnabled
    // Assert: Returns "ACTIVE with 2 discovery warnings"
    true |> Expect.isTrue "Placeholder"
  }
]
\\\

---

### Fix 4: Update Smoke Test to Fail on 0 Discovered

**File**: C:\Code\Repos\SageFs\scripts\smoke-test.ps1 (Lines 227–228)

**Before**:
\\\powershell
if ( -eq 0) {
  Warn "tests-discovery" "0 tests discovered after s — session may still be warming up"
}
\\\

**After**:
\\\powershell
if ( -eq 0) {
  # Check if sample project structure suggests it SHOULD have tests
   = (Get-ChildItem "" -Filter "*.Tests.fs" -Recurse | Measure-Object).Count -gt 0
   = (Test-Path "\Tests") -or (Test-Path "\test")
   =  -or  -or  -match "test"
  
  if () {
    # If project structure suggests tests, 0 discovered is a FAILURE
    Fail "tests-discovery" "0 tests discovered after s. Sample project has test files but discovery found nothing. Check daemon logs for tree-sitter/reflection errors."
  } else {
    # Sample legitimately has no tests
    Warn "tests-discovery" "0 tests discovered after s — sample project may not have tests"
  }
}
\\\

**Effect**: CI now catches silent discovery failures in projects that SHOULD have tests.

---

## KEY FILES & LINE NUMBERS FOR TDD

| Component | File | Lines | Current Issue | Fix Type |
|-----------|------|-------|---|---|
| **Enable logic** | Mcp.fs | 1612–1632 | Race condition | Add confirm_live_testing_enabled() |
| **Run tests** | Mcp.fs | 2196–2282 | Inverted check | Add DiscoveryNotStarted + DiscoveryIncomplete enum |
| **Result enum** | Mcp.fs | 2051–2108 | No state distinction | Extend enum, update format() |
| **Tree-sitter** | DaemonMode.fs | 724–730 | Silent errors | Dispatch DiscoveryWarningDetected event |
| **Test state** | LiveTestingTypes.fs | 1096–1130 | No warning field | Add DiscoveryWarnings + DiscoveryErrorCount |
| **Elm handler** | SageFsApp.fs | 614–647 | No warning handling | Add event case for DiscoveryWarningDetected |
| **Test trace** | Mcp.fs | 1705–1737 | No error exposure | Include warnings in JSON |
| **Smoke test** | smoke-test.ps1 | 227–228 | Masks failures | Check project structure, fail if should have tests |

---

## SUMMARY: What Will Improve Diagnosability

| Issue | Symptom | Fix | TDD Seam |
|-------|---------|-----|----------|
| Activation race | "Enabled" but still disabled | confirm_live_testing_enabled() | Mock Elm dispatch delay |
| Tree-sitter silent | 0 tests, no reason | Surface warnings in get_test_trace() | Mock TestTreeSitter.discover() to throw |
| No discovery signal | Can't tell if running/failed | DiscoveryIncomplete enum + warnings | State machine tests |
| Inverted check | Wrong error message | Fix line 2270 boolean logic | Test both activation states |
| Smoke test mute | CI passes with broken discovery | Fail if project should have tests | Check-script filesystem structure |

All fixes maintain **backward compatibility** — new enum values and fields are additive.
