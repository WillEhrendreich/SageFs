## SAGEFS COLD-START DISCOVERY PATH — SYNTHESIS

### 1. MCP enable_live_testing → Elm Update Flow

**File**: C:\Code\Repos\SageFs\SageFs\McpTools.fs (line ~1190)
**Definition**: member _.enable_live_testing() : Task<string>

**File**: C:\Code\Repos\SageFs\SageFs.Core\Mcp.fs (line 1643)
**Implementation**: let setLiveTesting (ctx: McpContext) (enabled: bool) : Task<string>

Flow:
1. MCP tool calls enable_live_testing()
2. Calls setLiveTesting(ctx, true)
3. Dispatches SageFsMsg.EnableLiveTesting to Elm
4. Returns async response: "Live testing enabled. Discovery runs asynchronously..."
5. Notes cold-session priming requirement

**File**: C:\Code\Repos\SageFs\SageFs.Core\SageFsApp.fs (line 798)
**Handler**: | SageFsMsg.EnableLiveTesting ->

Update behavior:
- Sets LiveTestingActivation.Active
- If DiscoveredTests is NOT empty: triggers execution for those tests
- If DiscoveredTests IS empty: returns no effects (no-op)
- Returns recomputed model

---

### 2. Cold vs Warm Session Discovery Triggers

**Current Behavior**:
- **WARM**: Worker calls WorkerMessage.GetTestDiscovery on startup → SessionManager handles InitialTestDiscovery → populates DiscoveredTests
- **COLD** (enable_live_testing on empty session): 
  - Sets Activation=Active
  - DiscoveredTests is empty → no effects generated
  - NO discovery is triggered
  - Client must call eval (any expression) to trigger file change detection
  - Only keystroke/file-change → triggers FCS → triggers discovery indirectly

**Root Cause**: Discovery only runs when:
1. Worker sends it initially (warm startup)
2. File changes trigger FCS type check → afterTypeCheck may run tests
3. EnableLiveTesting never directly triggers discovery on cold session

---

### 3. Discovery State & Truthfulness Tracking

**File**: C:\Code\Repos\SageFs\SageFs.Core\Features\LiveTestingTypes.fs (line 1133)
**Type**: 	ype LiveTestDiscoveryState = Disabled | Discovering | ReadyZeroTests | ReadyWithTests of count

**File**: C:\Code\Repos\SageFs\SageFs.Core\Features\LiveTestingTypes.fs (line 1157)
**Key Function**: LiveTestState.requiresPrimingEval : LiveTestState -> bool

Implementation (line 1157-1162):
`sharp
let requiresPrimingEval (state: LiveTestState) =
    match state.Activation with
    | LiveTestingActivation.Inactive -> false
    | LiveTestingActivation.Active ->
        Array.isEmpty state.DiscoveredTests
        && state.LastDiscoveryTime <= System.DateTimeOffset.MinValue
`

Returns TRUE when:
- Activation is Active AND
- No tests discovered yet AND
- Discovery has never run (LastDiscoveryTime = MinValue)

**Wire Value** (line 1140-1144):
- Disabled → "disabled"
- Discovering → "discovering"
- ReadyZeroTests → "ready_zero_tests"
- ReadyWithTests n → "ready_with_tests"

---

### 4. Existing Tests

**File**: C:\Code\Repos\SageFs\SageFs.Tests\LiveTestingDiscoveryStateTests.fs

Core tests:
1. Line 40: "inactive state is disabled"
2. Line 46: "active state with no discovery timestamp is discovering"
3. Line 53: "active state with zero tests after discovery is ready_zero_tests"
4. Line 64: "active state with discovered tests is ready_with_tests"
5. Line 76: "wire values are stable"
6. Line 89: "discovering hint explains cold-session priming"

MCP Discovery Semantics (line 97):
7. Line 99: "enable_live_testing explains async cold-start discovery"
8. Line 108: "get_live_test_status exposes priming requirement while discovery is pending"
9. Line 124: "get_test_trace exposes priming requirement while discovery is pending"

Tests verify:
- DiscoveryState enum transitions
- Wire value stability
- MCP hints reference "no-op eval"
- DiscoveryRequiresEval flag surfaces in get_live_test_status / get_test_trace

---

### 5. Minimal Fix Points

**Option A**: Trigger discovery on enable_live_testing (cold start)
- **Location**: SageFsApp.fs line 798-821 (EnableLiveTesting handler)
- **Change**: When Activation transitions to Active AND DiscoveredTests is empty, generate a discovery effect
- **Challenge**: Discovery is a worker RPC (GetTestDiscovery), requires session ID routing

**Option B**: Auto-prime discovery via keystroke simulator
- **Location**: SageFsApp.fs or Elm loop
- **Change**: When EnableLiveTesting fires with empty DiscoveredTests, dispatch a synthetic FileContentChanged
- **Advantage**: Reuses existing hot-reload → FCS → discovery chain
- **Risk**: May trigger incomplete discovery if syntax is bad

**Option C**: Explicit no-op eval flow (current smoke-test approach)
- **Location**: Client-side (MCP caller)
- **Current**: Smoke test sends "1+1;;" after enable_live_testing
- **Problem**: Requires external coordination; clients often miss this
- **Benefit**: Minimal code change, explicit user action

**Recommended**: Option A + clearer UX
- Add effect handler for discovery request
- Wire discovery to session RPC
- Update MCP response to include progress-polling guidance

---

### 6. smoke-test.ps1 Discovery Priming

**File**: C:\Code\Repos\SageFs\scripts\smoke-test.ps1

**Lines 325-347**: Live Testing Enable + Prime Eval

`powershell
# Step 1: Enable live testing
Write-Host "  Enabling live testing..." -ForegroundColor DarkGray
Invoke-RestMethod -Method Post -Uri "/api/live-testing/enable" -TimeoutSec 5

# Step 2: Prime discovery with no-op eval
Write-Host "  Priming live testing with a no-op eval..." -ForegroundColor DarkGray
 = [PSCustomObject]@{
    code              = "1+1;;"
    working_directory = 
} | ConvertTo-Json

 = Invoke-RestMethod -Method Post -Uri "/exec" 
    -Body  
    -ContentType "application/json" 
    -TimeoutSec 15
`

**Lines 349-375**: Discovery Polling
- Polls /api/live-testing/status every 2 seconds for 30 seconds max
- Waits for DiscoveryState in ["ready_with_tests", "ready_zero_tests", "disabled"]
- Falls back to discovered count > 0 if state field missing

**Key**: Test priming is EXPLICIT and NECESSARY. Without eval, discovery doesn't start.

---

### 7. Concrete Test Additions

**Test 1**: "enable_live_testing on cold session with no tests triggers discovery effect"
- Setup: Empty session, Active=false, DiscoveredTests=[]
- Action: Dispatch EnableLiveTesting
- Assert: Effects include a discovery-triggering signal or eventual transition to Discovering state
- File: LiveTestingDiscoveryStateTests.fs

**Test 2**: "get_live_test_status DiscoveryRequiresEval is true until discovery completes"
- Setup: Active session, no discovered tests, no LastDiscoveryTime
- Action: Call getLiveTestStatus
- Assert: JSON DiscoveryRequiresEval=true, DiscoveryState="discovering"
- File: LiveTestingDiscoveryStateTests.fs (already exists, line 108)

**Test 3**: "cold session with enable_live_testing should expose synthetic keystroke entry point"
- Setup: Session created, EnableLiveTesting dispatched, DiscoveredTests empty
- Action: Verify that no keystroke/file-change is required to transition from Discovering
- Assert: DiscoveryState eventually → ReadyZeroTests or ReadyWithTests without user input
- File: LiveTestingDiscoveryStateTests.fs (NEW)

**Test 4**: "enable_live_testing response differs between warm (tests cached) and cold (empty)"
- Setup: Two contexts, one with cached tests, one empty
- Action: Call setLiveTesting on both
- Assert: Warm returns count; cold returns "requires no-op eval" or equivalent
- File: LiveTestingDiscoveryStateTests.fs (already exists, line 99)

**Test 5**: "smoke-test priming eval triggers discovery"
- Setup: Fresh session, enabled live testing
- Action: Execute "1+1;;"
- Assert: Discovery state transitions from Discovering to ReadyZeroTests/ReadyWithTests
- File: Tier0CorrectnessTests.fs or HttpApiIntegrationTests.fs (NEW)

---

### Summary Table

| Component | File | Type/Function | Current Behavior |
|-----------|------|---------------|------------------|
| **MCP Tool** | McpTools.fs:~1190 | nable_live_testing() | Dispatches EnableLiveTesting msg |
| **MCP Impl** | Mcp.fs:1643 | setLiveTesting | Returns async; notes cold priming |
| **Elm Update** | SageFsApp.fs:798 | EnableLiveTesting | No-op if tests empty (ISSUE) |
| **Discovery State** | LiveTestingTypes.fs:1133 | LiveTestDiscoveryState | Tracks Disabled/Discovering/Ready* |
| **Priming Check** | LiveTestingTypes.fs:1157 | equiresPrimingEval | TRUE if Active ∧ empty ∧ never-run |
| **Status API** | Mcp.fs:1557 | getLiveTestStatus | Exposes DiscoveryRequiresEval flag |
| **Trace API** | Mcp.fs:1738 | getTestTrace | Exposes DiscoveryState + Hint |
| **Smoke Test** | smoke-test.ps1:325-347 | Priming section | Sends "1+1;;" after enable |
| **Test Suite** | LiveTestingDiscoveryStateTests.fs | 9 tests | Covers state transitions + MCP semantics |

