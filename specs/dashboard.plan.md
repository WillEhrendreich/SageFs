# SageFs Dashboard Test Plan

## Application Overview

The SageFs Dashboard is a developer tool for interactive F# code evaluation via FSI (F# Interactive). It provides a web-based interface for writing and executing F# code, viewing results, managing FSI sessions, and monitoring diagnostics. The dashboard uses real-time Server-Sent Events (SSE) via Datastar for live updates and includes keyboard shortcuts for efficient workflow.

## Test Scenarios

### 1. Code Evaluation

**Seed:** `seed.spec.ts`

#### 1.1. evaluate-simple-expression

**File:** `tests/code-evaluation/evaluate-simple-expression.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Click on the F# code textarea to focus it
    - expect: The textarea should be focused and ready for input
  3. Type 'let x = 1 + 1' into the textarea
    - expect: The code should appear in the textarea
    - expect: The character count should show '1 lines, 13 chars'
  4. Click the '▶ Eval' button
    - expect: The code should be evaluated
    - expect: The result 'val x: int = 2' should appear below the textarea
    - expect: The textarea should be cleared

#### 1.2. evaluate-with-keyboard-shortcut

**File:** `tests/code-evaluation/evaluate-with-keyboard-shortcut.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Click on the F# code textarea and type 'printfn "Hello, World!"'
    - expect: The code should appear in the textarea with correct character count
  3. Press Ctrl+Enter
    - expect: The code should be evaluated
    - expect: The result 'val it: unit = ()' should appear
    - expect: The textarea should be cleared

#### 1.3. evaluate-code-with-console-output

**File:** `tests/code-evaluation/evaluate-code-with-console-output.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type 'printfn "Test output"' into the textarea and evaluate it
    - expect: The evaluation result should appear
    - expect: The Output panel should display 'Test output' with a timestamp

#### 1.4. evaluate-multiline-code

**File:** `tests/code-evaluation/evaluate-multiline-code.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type multi-line F# code: 'let add x y =\n  x + y\nadd 5 3'
    - expect: The code should appear on multiple lines in the textarea
    - expect: Character count should reflect multiple lines
  3. Click the '▶ Eval' button
    - expect: The code should evaluate successfully
    - expect: The result 'val it: int = 8' should appear

#### 1.5. evaluate-code-with-errors

**File:** `tests/code-evaluation/evaluate-code-with-errors.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type 'let x = undefinedVariable' into the textarea
    - expect: The code should appear in the textarea
  3. Click the '▶ Eval' button
    - expect: An error message should appear
    - expect: The error should indicate 'Operation could not be completed due to earlier error' or similar
    - expect: The Diagnostics panel may show error details

#### 1.6. evaluate-with-syntax-error

**File:** `tests/code-evaluation/evaluate-with-syntax-error.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type invalid F# syntax like 'let x =' (incomplete expression)
    - expect: The code should appear in the textarea
  3. Click the '▶ Eval' button
    - expect: An error message should be displayed
    - expect: The Diagnostics panel should show syntax error details if available

#### 1.7. evaluate-empty-textarea

**File:** `tests/code-evaluation/evaluate-empty-textarea.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Ensure textarea is empty (0 lines)
    - expect: The textarea should be empty
  3. Click the '▶ Eval' button
    - expect: The button should handle empty input gracefully
    - expect: No evaluation should occur or an appropriate message should appear

#### 1.8. character-count-updates

**File:** `tests/code-evaluation/character-count-updates.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Click on the textarea
    - expect: Character count should show '0 lines'
  3. Type 'let a = 1'
    - expect: Character count should update to show '1 lines, 9 chars'
  4. Press Enter and type 'let b = 2'
    - expect: Character count should update to show '2 lines' with appropriate character count
  5. Delete all text
    - expect: Character count should return to '0 lines'

#### 1.9. consecutive-evaluations

**File:** `tests/code-evaluation/consecutive-evaluations.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type 'let x = 5' and evaluate it
    - expect: The result 'val x: int = 5' should appear
  3. Type 'let y = x + 3' and evaluate it
    - expect: The result 'val y: int = 8' should appear
    - expect: Variable 'x' should still be in scope from the previous evaluation
  4. Type 'x + y' and evaluate it
    - expect: The result 'val it: int = 13' should appear

### 2. Output Panel

**Seed:** `seed.spec.ts`

#### 2.1. view-output-from-printfn

**File:** `tests/output-panel/view-output-from-printfn.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type 'printfn "Line 1"' and evaluate it
    - expect: The Output panel should show 'Line 1' with a timestamp
  3. Type 'printfn "Line 2"' and evaluate it
    - expect: The Output panel should show both 'Line 1' and 'Line 2' with timestamps

#### 2.2. clear-output-with-button

**File:** `tests/output-panel/clear-output-with-button.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute code that produces output (e.g., 'printfn "Test"')
    - expect: Output should appear in the Output panel
  3. Click the 'Clear' button in the Output panel header
    - expect: The Output panel should be cleared
    - expect: It should show 'No output yet'

#### 2.3. clear-output-with-keyboard-shortcut

**File:** `tests/output-panel/clear-output-with-keyboard-shortcut.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute code that produces output
    - expect: Output should appear in the Output panel
  3. Press Ctrl+L
    - expect: The Output panel should be cleared
    - expect: It should show 'No output yet'

#### 2.4. output-scrolling-with-many-lines

**File:** `tests/output-panel/output-scrolling-with-many-lines.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute code that generates multiple lines of output: 'for i in 1..20 do printfn "Line %d" i'
    - expect: The Output panel should display all 20 lines
    - expect: The panel should have a scrollbar if content exceeds visible area
    - expect: User should be able to scroll through the output

#### 2.5. output-with-error-messages

**File:** `tests/output-panel/output-with-error-messages.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Check the initial state of the Output panel
    - expect: The Output panel may show warmup messages or errors like 'Warmup failure'
    - expect: Error messages should be prefixed with '✗' and a timestamp

#### 2.6. output-persists-across-evaluations

**File:** `tests/output-panel/output-persists-across-evaluations.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute 'printfn "First"' and then 'printfn "Second"'
    - expect: Both 'First' and 'Second' should appear in the Output panel
    - expect: Output should accumulate across evaluations
    - expect: Newer output should appear after older output

### 3. Session Management

**Seed:** `seed.spec.ts`

#### 3.1. view-active-session-info

**File:** `tests/session-management/view-active-session-info.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Locate the Sessions panel
    - expect: The panel should show session status (e.g., 'Ready')
    - expect: It should display session ID (e.g., 'Session: session-9fa9b00b')
    - expect: It should show the number of loaded projects (e.g., 'Projects: 4')
    - expect: It should display the number of active connections (e.g., '🌐 2')

#### 3.2. reset-session

**File:** `tests/session-management/reset-session.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute 'let x = 100' to define a variable
    - expect: The variable should be defined successfully
  3. Click the '↻ Reset' button
    - expect: A message 'Reset: Session reset successfully' should appear
    - expect: The session should be reset
  4. Try to evaluate 'x + 1'
    - expect: An error should occur indicating 'x' is not defined
    - expect: This confirms the session was reset and variables were cleared

#### 3.3. hard-reset-session

**File:** `tests/session-management/hard-reset-session.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute some F# code to create session state
    - expect: Code should execute successfully
  3. Click the '⟳ Hard Reset' button
    - expect: The session should be hard reset
    - expect: A confirmation or status message should appear
    - expect: All session state should be completely cleared

#### 3.4. discover-projects-from-directory

**File:** `tests/session-management/discover-projects-from-directory.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Scroll to the 'Create Session' panel
  3. Click on the 'Working Directory' textbox and enter a valid directory path (e.g., 'C:\\Code\\Repos\\SageFs')
    - expect: The path should appear in the textbox
  4. Click the '🔍 Discover' button
    - expect: The system should scan the directory for F# projects
    - expect: A list of discovered projects should appear showing .fsproj files and .slnx solution files
    - expect: Each item should be prefixed with 📄 for projects or 📁 for solutions
    - expect: A message like 'Will use solution file. Click 'Create Session' to proceed.' should appear if a solution is found

#### 3.5. discover-projects-from-empty-directory

**File:** `tests/session-management/discover-projects-from-empty-directory.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Enter a directory path that contains no F# projects
    - expect: The path should appear in the textbox
  3. Click the '🔍 Discover' button
    - expect: An appropriate message should appear indicating no projects were found
    - expect: The system should handle this gracefully without errors

#### 3.6. discover-with-invalid-directory

**File:** `tests/session-management/discover-with-invalid-directory.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Enter an invalid or non-existent directory path
    - expect: The path should appear in the textbox
  3. Click the '🔍 Discover' button
    - expect: An error message should appear indicating the directory is invalid or not found
    - expect: The system should handle this gracefully

#### 3.7. specify-projects-manually

**File:** `tests/session-management/specify-projects-manually.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Click on the 'Or enter project paths' textbox
    - expect: The textbox should be focused
  3. Type comma-separated project paths: 'Project1.fsproj, Project2.fsproj'
    - expect: The project paths should appear in the textbox
  4. Click the '➕ Create Session' button
    - expect: A new session should be created with the specified projects
    - expect: The Sessions panel should update to show the new session
    - expect: Session status should change to reflect the new session

#### 3.8. create-session-from-discovered-projects

**File:** `tests/session-management/create-session-from-discovered-projects.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Discover projects using a valid directory path
    - expect: Projects should be discovered and listed
  3. Click the '➕ Create Session' button
    - expect: A new session should be created using the discovered projects
    - expect: The Sessions panel should update with the new session information
    - expect: The new session should be ready for code evaluation

#### 3.9. create-session-without-projects

**File:** `tests/session-management/create-session-without-projects.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Ensure both the Working Directory and project paths textboxes are empty or invalid
  3. Click the '➕ Create Session' button
    - expect: The system should handle this gracefully
    - expect: An error message or validation should appear indicating that projects are required
    - expect: Or a session without projects should be created if that's valid

#### 3.10. view-sessions-when-none-exist

**File:** `tests/session-management/view-sessions-when-none-exist.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard with no active sessions
  2. Check the Sessions panel
    - expect: The panel should show 'No sessions' if no sessions are active
    - expect: Or it should show the default/primary session information

### 4. Diagnostics Panel

**Seed:** `seed.spec.ts`

#### 4.1. view-diagnostics-panel-when-empty

**File:** `tests/diagnostics-panel/view-diagnostics-panel-when-empty.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Locate the Diagnostics panel
    - expect: The panel should show 'No diagnostics' when there are no errors or warnings

#### 4.2. diagnostics-appear-after-compilation-error

**File:** `tests/diagnostics-panel/diagnostics-appear-after-compilation-error.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute code with a type error: 'let x: int = "string"'
    - expect: An error should occur during evaluation
    - expect: The Diagnostics panel should display compiler diagnostics
    - expect: Diagnostics should include error messages, line numbers, and error codes

#### 4.3. diagnostics-clear-after-successful-evaluation

**File:** `tests/diagnostics-panel/diagnostics-clear-after-successful-evaluation.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute code that produces diagnostics
    - expect: Diagnostics should appear in the Diagnostics panel
  3. Execute valid code that compiles successfully
    - expect: The Diagnostics panel should clear
    - expect: It should return to showing 'No diagnostics'

#### 4.4. view-multiple-diagnostics

**File:** `tests/diagnostics-panel/view-multiple-diagnostics.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute code with multiple errors: 'let x: int = "string"\nlet y = undefinedVar'
    - expect: Multiple diagnostic messages should appear in the Diagnostics panel
    - expect: Each diagnostic should be clearly separated
    - expect: The panel should be scrollable if there are many diagnostics

### 5. Connection Status

**Seed:** `seed.spec.ts`

#### 5.1. view-connected-status

**File:** `tests/connection-status/view-connected-status.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Check the connection status banner at the top of the page
    - expect: The banner should show '✅ Connected' with a green indicator
    - expect: The status should be prominently displayed

#### 5.2. view-disconnected-status

**File:** `tests/connection-status/view-disconnected-status.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Simulate a disconnect by stopping the server or interrupting the SSE connection
  3. Check the connection status banner
    - expect: The banner should update to show 'Disconnected' or similar status
    - expect: The indicator should change to reflect disconnection (possibly red or warning color)
    - expect: The status should update in real-time via SSE

#### 5.3. reconnection-after-disconnect

**File:** `tests/connection-status/reconnection-after-disconnect.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard and ensure it's connected
  2. Disconnect the server temporarily
    - expect: The connection status should show 'Disconnected'
  3. Restart the server
    - expect: The connection status should automatically update back to '✅ Connected'
    - expect: The dashboard should be functional again
    - expect: SSE connection should be re-established

#### 5.4. functionality-during-disconnect

**File:** `tests/connection-status/functionality-during-disconnect.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Disconnect the server
  3. Try to evaluate F# code
    - expect: The evaluation should fail gracefully
    - expect: An appropriate error message should appear
    - expect: The UI should indicate that the connection is required for evaluation

### 6. Keyboard Shortcuts

**Seed:** `seed.spec.ts`

#### 6.1. view-keyboard-shortcuts-help

**File:** `tests/keyboard-shortcuts/view-keyboard-shortcuts-help.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Click the '⌨ Help' button in the Evaluate panel
    - expect: A keyboard shortcuts help table should appear
    - expect: It should show 'Ctrl+Enter - Evaluate code'
    - expect: It should show 'Tab - Insert 2 spaces (in editor)'
    - expect: It should show 'Ctrl+L - Clear output'

#### 6.2. toggle-keyboard-shortcuts-help

**File:** `tests/keyboard-shortcuts/toggle-keyboard-shortcuts-help.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Click the '⌨ Help' button
    - expect: The keyboard shortcuts table should appear
  3. Click the '⌨ Help' button again
    - expect: The keyboard shortcuts table should be hidden
    - expect: The help should toggle on/off

#### 6.3. f1-opens-help

**File:** `tests/keyboard-shortcuts/f1-opens-help.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Press F1 key
    - expect: The keyboard shortcuts help should appear
    - expect: This provides quick access to help documentation

#### 6.4. tab-inserts-spaces

**File:** `tests/keyboard-shortcuts/tab-inserts-spaces.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Click on the F# code textarea to focus it
  3. Type 'let x =' and press Enter
  4. Press Tab key
    - expect: Two spaces should be inserted at the cursor position
    - expect: The cursor should move forward by 2 spaces
    - expect: This facilitates proper F# indentation

#### 6.5. multiple-tab-presses

**File:** `tests/keyboard-shortcuts/multiple-tab-presses.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Click on the textarea and press Tab key 3 times
    - expect: Six spaces (2 spaces × 3 presses) should be inserted
    - expect: The cursor should be indented appropriately

#### 6.6. ctrl-enter-evaluates

**File:** `tests/keyboard-shortcuts/ctrl-enter-evaluates.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type 'let result = 42' into the textarea
  3. Press Ctrl+Enter
    - expect: The code should be evaluated without clicking the Eval button
    - expect: The result should appear below the textarea
    - expect: This provides a faster workflow for developers

#### 6.7. ctrl-l-clears-output

**File:** `tests/keyboard-shortcuts/ctrl-l-clears-output.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute code that produces output to populate the Output panel
  3. Press Ctrl+L
    - expect: The Output panel should be cleared immediately
    - expect: The panel should show 'No output yet'

### 7. Eval Stats

**Seed:** `seed.spec.ts`

#### 7.1. view-eval-statistics

**File:** `tests/eval-stats/view-eval-statistics.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Locate the 'Eval Stats' panel on the right sidebar
    - expect: The panel should display the total number of evaluations (e.g., '4 evals')
    - expect: It should show average evaluation time (e.g., 'avg 314ms')
    - expect: It should show minimum evaluation time (e.g., 'min 107ms')
    - expect: It should show maximum evaluation time (e.g., 'max 611ms')

#### 7.2. stats-update-after-evaluation

**File:** `tests/eval-stats/stats-update-after-evaluation.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Note the current eval count in the Eval Stats panel
  3. Execute 'let x = 1 + 1'
    - expect: The eval count should increment by 1
    - expect: The average, min, and max times should update based on the new evaluation
    - expect: Stats should update in real-time

#### 7.3. stats-reflect-multiple-evaluations

**File:** `tests/eval-stats/stats-reflect-multiple-evaluations.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute several pieces of F# code in succession
  3. Check the Eval Stats panel after each evaluation
    - expect: The eval count should increase with each evaluation
    - expect: Average time should be calculated correctly
    - expect: Min and max times should reflect the fastest and slowest evaluations
    - expect: Statistics should accurately represent the evaluation performance

#### 7.4. stats-after-session-reset

**File:** `tests/eval-stats/stats-after-session-reset.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute some code to generate eval stats
  3. Click the '↻ Reset' button to reset the session
    - expect: Check if eval stats are preserved or reset
    - expect: The behavior should be consistent with the reset functionality

### 8. Real-time Updates

**Seed:** `seed.spec.ts`

#### 8.1. sse-updates-connection-status

**File:** `tests/real-time-updates/sse-updates-connection-status.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Verify the connection status shows '✅ Connected'
    - expect: The status should be updated via SSE/Datastar
  3. Monitor for any server-side events that change connection status
    - expect: Status updates should appear without page refresh
    - expect: Real-time updates should be seamless

#### 8.2. sse-updates-session-info

**File:** `tests/real-time-updates/sse-updates-session-info.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Open the dashboard in a second browser tab
    - expect: Both tabs should show the same initial session information
  3. Create a new session or modify session state in one tab
    - expect: The Sessions panel in both tabs should update to reflect the change
    - expect: Real-time synchronization should occur via SSE

#### 8.3. sse-updates-eval-results

**File:** `tests/real-time-updates/sse-updates-eval-results.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute F# code
    - expect: The evaluation result should appear in real-time
    - expect: Output should stream to the Output panel via SSE
    - expect: No page refresh should be required

#### 8.4. sse-maintains-state-during-long-evaluation

**File:** `tests/real-time-updates/sse-maintains-state-during-long-evaluation.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Execute code that takes a long time to evaluate: 'System.Threading.Thread.Sleep(5000); 42'
    - expect: The dashboard should remain responsive
    - expect: The connection status should remain '✅ Connected' throughout
    - expect: The result should appear after the evaluation completes
    - expect: SSE connection should be maintained during the long operation

### 9. UI and Layout

**Seed:** `seed.spec.ts`

#### 9.1. page-loads-successfully

**File:** `tests/ui-and-layout/page-loads-successfully.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Verify the page loads without errors
    - expect: The page title should be 'SageFs Dashboard'
    - expect: The page should display the header '🧙 SageFs Dashboard v0.4.13.0' or similar version
    - expect: All panels should be visible: Output, Evaluate, Sessions, Diagnostics, Create Session, and Eval Stats
    - expect: The connection status banner should appear at the top

#### 9.2. all-panels-are-visible

**File:** `tests/ui-and-layout/all-panels-are-visible.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Verify the presence and visibility of all UI panels
    - expect: Output panel should be visible in the top-left
    - expect: Evaluate panel should be visible with textarea and buttons
    - expect: Sessions panel should display session information
    - expect: Diagnostics panel should be present
    - expect: Create Session panel should show input fields for project discovery
    - expect: Eval Stats panel should appear in the right sidebar

#### 9.3. responsive-layout-on-smaller-screens

**File:** `tests/ui-and-layout/responsive-layout-on-smaller-screens.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Resize the browser window to a smaller width (e.g., 768px)
    - expect: The layout should adapt to the smaller screen size
    - expect: All panels should remain accessible
    - expect: Content should not be cut off or overlapping
    - expect: Scrollbars should appear where necessary

#### 9.4. scrolling-in-panels

**File:** `tests/ui-and-layout/scrolling-in-panels.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Generate enough content in the Output panel to exceed its visible area
    - expect: The Output panel should display a scrollbar
    - expect: User should be able to scroll through all content
    - expect: The panel should not expand beyond its designated area
  3. Generate enough diagnostics to exceed the Diagnostics panel area
    - expect: The Diagnostics panel should be scrollable
    - expect: All diagnostics should remain accessible

#### 9.5. version-number-displayed

**File:** `tests/ui-and-layout/version-number-displayed.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Check the main header
    - expect: The version number should be displayed (e.g., 'v0.4.13.0')
    - expect: The version should be clearly visible in the header

#### 9.6. button-states-and-feedback

**File:** `tests/ui-and-layout/button-states-and-feedback.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Hover over various buttons (Eval, Reset, Clear, etc.)
    - expect: Buttons should show hover states (cursor: pointer)
    - expect: Visual feedback should indicate interactivity
  3. Click a button and observe its active state
    - expect: The button should show an active state when clicked
    - expect: Visual feedback should confirm the action

#### 9.7. panel-headers-are-clear

**File:** `tests/ui-and-layout/panel-headers-are-clear.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Review all panel headers
    - expect: Each panel should have a clear, descriptive heading (Output, Evaluate, Sessions, Diagnostics, Create Session, Eval Stats)
    - expect: Headers should be formatted consistently
    - expect: Headers should use appropriate heading levels for accessibility

### 10. Edge Cases and Error Handling

**Seed:** `seed.spec.ts`

#### 10.1. very-long-code-input

**File:** `tests/edge-cases/very-long-code-input.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type or paste a very long piece of F# code (e.g., 1000+ lines)
    - expect: The textarea should accept the input
    - expect: Character count should update correctly
    - expect: The textarea should be scrollable
    - expect: Evaluation should work or provide appropriate feedback about code size limits

#### 10.2. special-characters-in-code

**File:** `tests/edge-cases/special-characters-in-code.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type code with special characters: 'let message = "Hello 👋 World! ♥️"'
    - expect: Special characters and emojis should be handled correctly
    - expect: The code should evaluate successfully
    - expect: Character count should handle multi-byte characters correctly

#### 10.3. concurrent-evaluations

**File:** `tests/edge-cases/concurrent-evaluations.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type code and click Eval button
    - expect: Evaluation should start
  3. Immediately type new code and click Eval again before the first evaluation completes
    - expect: The system should handle concurrent evaluation requests gracefully
    - expect: Either evaluations should be queued or the second request should cancel the first
    - expect: No errors or crashes should occur

#### 10.4. rapid-consecutive-button-clicks

**File:** `tests/edge-cases/rapid-consecutive-button-clicks.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type some code in the textarea
  3. Click the Eval button rapidly multiple times in succession
    - expect: The system should handle rapid clicks gracefully
    - expect: Buttons should be disabled during processing or implement debouncing
    - expect: No duplicate evaluations should occur
    - expect: The UI should remain stable

#### 10.5. paste-large-code-block

**File:** `tests/edge-cases/paste-large-code-block.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Copy a large block of F# code to clipboard and paste it into the textarea using Ctrl+V
    - expect: The entire code block should be pasted
    - expect: Character count should update correctly
    - expect: The textarea should handle the paste operation smoothly
    - expect: Performance should remain acceptable

#### 10.6. code-with-infinite-loop

**File:** `tests/edge-cases/code-with-infinite-loop.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Type code with an infinite loop: 'while true do printfn "loop"'
    - expect: The code should be sent for evaluation
  3. Monitor the dashboard behavior
    - expect: The system should have timeout mechanisms
    - expect: Or it should provide a way to cancel/interrupt evaluation
    - expect: The dashboard should not become completely unresponsive
    - expect: An error or timeout message should eventually appear

#### 10.7. invalid-path-in-create-session

**File:** `tests/edge-cases/invalid-path-in-create-session.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Enter special characters or invalid path syntax in the Working Directory field: '||||invalid|||'
    - expect: The field should accept the input
  3. Click the Discover button
    - expect: An appropriate error message should appear
    - expect: The system should handle invalid paths gracefully without crashing

#### 10.8. empty-project-paths-field

**File:** `tests/edge-cases/empty-project-paths-field.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Leave the project paths field empty
  3. Click the '➕ Create Session' button
    - expect: The system should validate the input
    - expect: An error message or validation should indicate that projects are required
    - expect: Or it should create a session without projects if that's valid

#### 10.9. malformed-project-paths

**File:** `tests/edge-cases/malformed-project-paths.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Enter malformed project paths: 'not-a-path, ///invalid///'
    - expect: The input should be accepted in the field
  3. Click the '➕ Create Session' button
    - expect: The system should validate the paths
    - expect: Appropriate error messages should appear for invalid paths
    - expect: The system should handle errors gracefully

#### 10.10. network-interruption-during-eval

**File:** `tests/edge-cases/network-interruption-during-eval.spec.ts`

**Steps:**
  1. Navigate to http://localhost:37750/dashboard
  2. Start evaluating code
  3. Simulate network interruption (disconnect network or pause connection)
    - expect: The connection status should update to 'Disconnected'
    - expect: The evaluation should fail with an appropriate error
    - expect: The system should handle the interruption gracefully
    - expect: Upon reconnection, the dashboard should recover and allow new evaluations
