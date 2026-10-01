namespace SageFs.Tests

open SageFs.ActorCreation
open SageFs.AppState
open SageFs.McpTools
open SageFs.WorkflowTypes
open System.Collections.Concurrent
open System.Threading

/// Timeouts a test chooses on purpose, named for what they are, so no test carries a bare number.
/// A test that needs the PRODUCTION value takes it from `SageFs.Timeouts`, never from a copy here.
module TestTimeouts =
  /// The grace a console-app test gives an entry point before the runner calls it a console app.
  /// Short on purpose: for that app the grace has to EXPIRE, so a long one only slows the test.
  /// Web-app tests must not use it, because a cold host build on a loaded runner outlasts it.
  let consoleAppGrace = System.TimeSpan.FromMilliseconds 500.

  // --- tests A to K ---
  // The helpers below build each value from a number once, here. Every call site names the value
  // by what it is for. A passing test never waits out a ceiling: reaching one means the test failed.
  let private ms (n: float) = System.TimeSpan.FromMilliseconds n
  let private secs (n: float) = System.TimeSpan.FromSeconds n
  let private hours (n: float) = System.TimeSpan.FromHours n

  // Ceilings on a single wait.

  /// Ceiling on a wait that crosses a real process or a real server (a spawned host exiting, a
  /// request through a started web host, a daemon failing a bad bind). A cold start on a loaded
  /// runner takes seconds, so this is generous.
  let patience = secs 20.
  /// Ceiling on a wait that completes in the test's own process (a task settling, a file watcher
  /// reporting, a long poll answering) but goes through the thread pool and can be starved.
  let patienceInProcess = secs 10.
  /// Ceiling on in-process work with no real I/O (an Elm loop reaching a model, a host stopping).
  let patienceBrief = secs 5.
  /// Ceiling on a wait that is a few message hops and nothing else.
  let patienceTight = secs 2.

  // Settles: a short, fixed window where nothing can be awaited.

  /// A window for in-process work to drain, or for a negative assertion ("nothing more
  /// happened") where waiting is the only way to look for an absent event.
  let settle = ms 50.
  /// A gap long enough for a stopwatch to read a positive elapsed time, and no longer.
  let measurableGap = ms 2.
  /// The delay inside a timed piece of work, when the test then asserts the measured time is at
  /// least this much.
  let timedWork = ms 20.
  /// Let a just-started daemon finish warmup (module init, JIT of the request pipeline) before
  /// its memory baseline is taken, so the baseline is not a mid-startup reading.
  let warmupSettle = secs 2.
  /// Let a just-started in-process server or file watcher begin before the first request or edit.
  let startSettle = secs 1.
  /// Let an SSE connection establish before the event it should carry is triggered.
  let connectSettle = ms 200.

  // Poll intervals: how often a loop looks again. Each is how fast the thing being waited on
  // can change, not how long the loop may run (that is a ceiling above or a budget below).

  /// An in-process flag flips within a few milliseconds (an Elm loop draining).
  let pollTight = ms 10.
  /// A timer thread flushes a batch within a few tens of milliseconds.
  let pollFlush = ms 20.
  /// A landing drives real `git` subprocesses, so each look costs real work.
  let pollLanding = ms 25.
  /// A local server that answers fast once it is up: a daemon's health during startup, or a
  /// route settling on a value.
  let pollQuick = ms 100.
  /// A page's DOM, or a file that another process still holds open.
  let pollPage = ms 200.
  /// A service, app or SSE connection coming up over a few seconds.
  let pollService = ms 250.
  /// A file the init profile writes, or a session changing workflow, both over several seconds.
  let pollMedium = ms 500.
  /// A session or a hot reload that takes many seconds to land.
  let pollSlow = secs 1.

  // Request timeouts on one HTTP call.

  /// One probe request to a local server, in a loop that retries it.
  let httpProbe = secs 5.
  /// One request that may have to start work first (create a session, serve a page).
  let httpRequest = secs 10.
  /// One request to a daemon that may be mid-build.
  let httpDaemon = secs 30.
  /// How long a test listens to an SSE stream for the event it triggered.
  let sseListen = secs 15.

  // Budgets for a whole wait that ends when something becomes ready.

  /// A daemon, session or discovery on the small sample becoming ready.
  let readyBudget = secs 60.
  /// Four sample sessions, each loaded and evaluated in turn, all reaching Ready.
  let loadedSessionsReady = secs 90.
  /// The daemon's own wall-clock save fires 60s after start and does not get faster on a faster
  /// runner, so both resume waits (the save becoming durable, the second daemon rebuilding the
  /// session) are a generous multiple of it.
  let daemonResumeCeiling = secs 150.
  /// A save verdict after 160,000 stack walks: the tiered reflection reads make this slower than
  /// a quiet app needs.
  let heavyVerdictBudget = secs 150.
  /// A real-git-backed landing reaching a terminal state. Generous relative to `pollLanding`.
  let landingBudget = secs 30.
  /// The default window an idle daemon's memory is watched over. `SAGEFS_IDLE_RSS_SOAK_MINUTES`
  /// widens it on purpose.
  let idleRssWindow = secs 60.
  /// How often the idle daemon's memory is sampled, so a failure shows the shape of the growth.
  let idleRssSampleInterval = secs 15.
  /// A hot-reload host printing the port its worker listens on.
  let workerPortReport = secs 120.
  /// A hot-reload host's session becoming Ready, including its first build.
  let workerSessionReady = secs 180.
  /// The running app answering its first request.
  let appFirstAnswer = secs 30.
  /// The worker reporting that a file is watched after watch-all.
  let watchRegistered = secs 10.
  /// A hot-reload save reaching its first verdict.
  let saveVerdict = secs 60.
  /// A patch ending in confirmed or never-entered. Outlasts `Timeouts.patchConfirmation`, the
  /// bound the worker itself gives the patched code to run.
  let patchOutcome = secs 40.

  // Bounds a test hands to the code under test.

  /// A bound the code under test never reaches because the test ends the wait first.
  let unreachedBound = hours 1.
  /// A probe bound short enough that a worker which never answers is refused inside the test.
  let hungProbeBound = ms 200.
  /// The ready bound a test hands to an orchestration whose fake host answers at once and
  /// ignores it, so only the code that passes the bound along is exercised.
  let handedReadyBound = secs 5.
  /// How long the host is asked to wait for entry probes in a test where the answer is
  /// already settled.
  let entryAwaitBound = ms 300.
  /// How long evaluated code sleeps when a test has to interrupt or kill it. Far longer than
  /// `patience`, so the test can only pass by ending the eval early.
  let runawayEval = secs 60.
  /// How soon after an eval starts a test cancels it, long before the eval would finish by itself.
  let cancelAfter = ms 700.

  // Crash scenarios for the deterministic simulation. These are inputs to the scenario, not
  // waits, and they are chosen against `RestartPolicy.defaultPolicy` (a crash within its 10s
  // StartupCrashWindow is a startup crash; a gap over its 5 minute ResetWindow starts a new window).

  /// Crashes spaced past the startup window and inside the reset window: none is a startup
  /// crash and all share one window.
  let crashGapSpaced = secs 20.
  /// A crash right after the last one, inside the startup window: the startup-crash circuit breaker.
  let crashGapRapid = secs 2.
  /// A quiet stretch longer than the reset window, so the restart count starts over.
  let quietPastResetWindow = secs 400.
  /// One tick of a clock that carries no event.
  let clockTick = secs 1.

  // --- tests S to Z ---

  // Ceilings. A test that reaches one has failed: a ceiling is never how a passing test
  // finishes, so a long one costs a green run nothing.

  // `patience` (20 s) is defined once, in the section above, and used here too.

  /// The ceiling on a wait for something a loaded machine still does within a few
  /// seconds: a host starting or stopping, a child process dying, a call parking.
  let shortPatience = System.TimeSpan.FromSeconds 10.

  /// The ceiling on an in-process event that normally lands in milliseconds (an outcome
  /// report, a timer firing, a callback). Five seconds absorbs a starved thread pool.
  let briefPatience = System.TimeSpan.FromSeconds 5.

  /// The ceiling on a cancellation or an expiry to take effect. It has to stay below the
  /// read windows those tests prove were NOT waited out.
  let cancelPatience = System.TimeSpan.FromSeconds 3.

  /// The ceiling on a call documented to return at once, or a probe of a local endpoint
  /// that should answer at once, while the work it started carries on in the background.
  let immediateReply = System.TimeSpan.FromSeconds 1.

  /// The ceiling on a real child process reaching a state that needs it to start up.
  let processStartPatience = System.TimeSpan.FromSeconds 60.

  /// The client timeout for one ordinary HTTP request to a local host that is already up.
  let requestPatience = System.TimeSpan.FromSeconds 30.

  /// The client timeout for a long-lived SSE read: the stream stays open for as long as
  /// the test reads it, so this bounds the first byte, not the whole stream.
  let streamReadPatience = System.TimeSpan.FromSeconds 60.

  /// How long a file write is retried when a just-killed process may still hold the file.
  let fileLockRetryPatience = System.TimeSpan.FromSeconds 15.

  /// The ceiling on `npm ci` for the VS Code extension: a cold install downloads the tree.
  let npmInstallPatience = System.TimeSpan.FromMinutes 5.

  /// The ceiling on an `npm run compile*` of the VS Code extension (Fable plus tsc, cold).
  let npmCompilePatience = System.TimeSpan.FromMinutes 10.

  // Poll intervals: the pause between two looks at a condition that has no event to wait on.

  /// Polling a value held in this process.
  let inProcessPoll = System.TimeSpan.FromMilliseconds 10.

  /// Polling an async condition (an actor's state, a task-returning probe).
  let asyncConditionPoll = System.TimeSpan.FromMilliseconds 50.

  /// Polling something outside the condition's own thread that changes within a second:
  /// a child process exiting, a served value flipping, a UI probe.
  let poll = System.TimeSpan.FromMilliseconds 100.

  /// Polling a local HTTP endpoint or a file the worker writes.
  let localHttpPoll = System.TimeSpan.FromMilliseconds 200.

  /// Polling text in a VS Code window, or a daemon that is still binding its port.
  let uiPoll = System.TimeSpan.FromMilliseconds 250.

  /// Polling a host that is still warming: each probe is a real round trip, and a cold
  /// runner takes a minute or more.
  let warmupPoll = System.TimeSpan.FromMilliseconds 500.

  /// Polling something that changes on the scale of seconds: a daemon's session list, a
  /// status bar, a connection that is retried.
  let slowPoll = System.TimeSpan.FromSeconds 1.

  // Settles: real time a test lets pass because the effect under test needs it.

  /// A real wall-clock gap so a timestamp taken after it is strictly later than one
  /// taken before it, not the same tick.
  let clockGap = System.TimeSpan.FromMilliseconds 30.

  /// A moment for a server to start on a slow request before the test sends the next one.
  let workStartSettle = System.TimeSpan.FromMilliseconds 200.

  /// A pause for a file watcher to re-arm after a failed eval's burst of events.
  let watcherRearmSettle = System.TimeSpan.FromSeconds 1.

  /// A margin so small that it only separates "at a boundary" from "just past it", and a
  /// rounding tolerance for a jittered span.
  let boundaryMargin = System.TimeSpan.FromMilliseconds 1.

  /// How far past a bound a scenario puts a value it needs to be unambiguously beyond.
  let pastBoundBy = System.TimeSpan.FromSeconds 1.

  /// How far past the restart grace the exhausted-restart-budget scenario checks: after
  /// the fifth restart spaced eleven seconds apart, before any window resets.
  let exhaustedBudgetCheckOffset = System.TimeSpan.FromSeconds 55.

  // Windows and deadlines a test hands to the code under test.

  /// An inactivity window that a stream the test serves ends well inside.
  let streamWindowCleanEnd = System.TimeSpan.FromMilliseconds 500.

  /// An inactivity window a stalled worker has to outlast so the proxy reports TimedOut.
  let streamWindowStalled = System.TimeSpan.FromMilliseconds 150.

  /// An inactivity window longer than the gap between lines of a slow, steady stream,
  /// shorter than the whole stream: it must be re-armed by every line, not run once.
  let streamWindowSteady = System.TimeSpan.FromMilliseconds 350.

  /// A read window that must NOT be what ends a cancelled run.
  let streamWindowNeverWaitedOut = System.TimeSpan.FromSeconds 10.

  /// An inactivity window that nothing in the test waits for.
  let inactivityWindowLong = System.TimeSpan.FromSeconds 30.

  /// An inactivity window nobody touches, so it expires inside the test.
  let inactivityWindowExpiring = System.TimeSpan.FromMilliseconds 50.

  /// How long a command may hold the session manager's loop before a test's fast supervisor
  /// watchdog calls it wedged. Real time but tiny: the wedge tests wait on the alarm, not a sleep.
  let watchdogWedgeAfter = System.TimeSpan.FromMilliseconds 150.

  /// How often a test's fast supervisor watchdog looks at the loop: a tenth of the wedge window.
  let watchdogCheckEvery = System.TimeSpan.FromMilliseconds 15.

  /// The timeout handed to a timer join. An idle timer has to be joined well inside it,
  /// not by sitting it out.
  let timerJoinTimeout = System.TimeSpan.FromSeconds 2.

  /// A deadline far shorter than the work it bounds, so the deadline fires.
  let deadlineTight = System.TimeSpan.FromMilliseconds 50.

  /// Work that takes far longer than `deadlineTight`, so only the deadline can end it.
  let slowWork = System.TimeSpan.FromSeconds 2.

  /// A deadline the work finishes well inside.
  let deadlineRoomy = System.TimeSpan.FromSeconds 5.

  /// The `wait_seconds` a get_session_status test asks for, below the cap.
  let statusWaitRequest = System.TimeSpan.FromSeconds 5.

  /// A valid per-test timeout a settings test applies and then restores.
  let validPerTestTimeout = System.TimeSpan.FromSeconds 7.

  /// A valid whole-run timeout a settings test applies and then restores.
  let validGlobalRunTimeout = System.TimeSpan.FromMinutes 3.

  /// An ordinary timeout inside the range ValidTimeout accepts, neither edge.
  let ordinaryValidTimeout = System.TimeSpan.FromSeconds 30.

  /// A timeout with an unremarkable value, to prove ValidTimeout hands back what it took.
  let roundTripTimeout = System.TimeSpan.FromSeconds 42.

  /// The freshness window handed to the agent presence check. The tests stamp the last tool
  /// call exactly this long before "now", and one tick later.
  let agentFreshnessWindow = System.TimeSpan.FromMinutes 10.

  /// How far beyond the update-check interval the last check was, in the test that proves a
  /// check past the interval is due.
  let updateCheckOverrun = System.TimeSpan.FromHours 1.

  /// A last check recent enough to be well inside the update-check interval.
  let updateCheckRecent = System.TimeSpan.FromMinutes 5.

  // Scenario durations: inputs to a value under test, never waited on.

  /// How long a test that passed took, where the value does not matter to the assertion.
  let testElapsed = System.TimeSpan.FromMilliseconds 10.

  /// A second elapsed, for a test that needs two results it can tell apart.
  let testElapsedOther = System.TimeSpan.FromMilliseconds 20.

  /// An elapsed that a formatting test feeds in and then looks for in the output.
  let reportedElapsed = System.TimeSpan.FromMilliseconds 42.

  /// How long a tree-sitter pass took in a diagnostics timing record.
  let treeSitterElapsed = System.TimeSpan.FromMilliseconds 5.

  /// How long an FCS pass took in a diagnostics timing record.
  let fcsElapsed = System.TimeSpan.FromMilliseconds 10.

  /// How long a reloaded file took to reload.
  let fileReloadElapsed = System.TimeSpan.FromMilliseconds 50.

  /// How long a warmup took, in a warmup-completed event.
  let warmupElapsed = System.TimeSpan.FromSeconds 2.

  /// How long a stream was silent before it was called stalled.
  let streamStalledAfter = System.TimeSpan.FromSeconds 30.

  /// The limit a timed-out test reports it hit.
  let testTimeLimit = System.TimeSpan.FromSeconds 5.

  /// How long ago a test last passed, in a failure narrative.
  let timeSinceLastPass = System.TimeSpan.FromMinutes 5.

  /// How long a session has gone unseen when the dashboard calls it stale.
  let sessionStaleFor = System.TimeSpan.FromMinutes 15.

  /// A session age so far past the idle threshold that only a state that is never idle
  /// can explain a non-idle reading.
  let sessionAgeFarPastIdle = System.TimeSpan.FromDays 30.

  /// A session whose last activity was a day ago, long past the idle threshold.
  let sessionAgeOneDay = System.TimeSpan.FromDays 1.

  /// A restart estimate a scenario puts into a transition cost record by hand. It is not the
  /// product's own estimate.
  let sampleRestartEstimate = System.TimeSpan.FromSeconds 8.

  /// The UTC offset a log timestamp carries (UTC+2).
  let sampleUtcOffset = System.TimeSpan.FromHours 2.

  // Simulation scenarios: virtual time. These are inputs the scenario feeds the clock, not
  // waits the test makes.

  /// The gap between crashes that is wider than the startup-crash window, so none counts
  /// as a startup crash.
  let crashSpacingPastStartupWindow = System.TimeSpan.FromSeconds 20.

  /// The gap between crashes that falls inside the startup-crash window.
  let crashSpacingInsideStartupWindow = System.TimeSpan.FromSeconds 2.

  /// A silence long enough that the warmup inactivity bound has fired.
  let warmupSilenceLong = System.TimeSpan.FromMinutes 5.

  /// An elapsed well inside both warmup bounds.
  let warmupElapsedInsideBounds = System.TimeSpan.FromMinutes 1.

  /// An elapsed early in a warmup, inside the inactivity bound.
  let warmupElapsedEarly = System.TimeSpan.FromSeconds 5.

  /// The short inactivity bound the progress property runs under.
  let warmupInactivityShort = System.TimeSpan.FromSeconds 5.

  /// How far the clock advances between progress reports, under the short bound.
  let warmupProgressStep = System.TimeSpan.FromSeconds 4.

  /// A single small clock advance before a stop.
  let clockAdvanceSmall = System.TimeSpan.FromSeconds 1.

  /// How far past the absolute warmup bound a scenario puts the elapsed time.
  let pastAbsoluteBy = System.TimeSpan.FromMinutes 1.

  // --- tests L to R ---
  // A ceiling is how long a wait may take before the test calls it failed. A test that reaches
  // one has failed; it is never how a passing test finishes. Where a wait ends the moment the
  // thing it waits on happens, a generous ceiling costs a passing test nothing.

  // `patience` (20 s) is defined once, in the A to K section, and used here too.

  /// Ceiling on one in-process event that arrives within milliseconds when it arrives at all
  /// (a file watcher callback, a status notification from a background task).
  let eventCeiling = System.TimeSpan.FromSeconds 5.

  /// How fast a tool call that must not wait on a background rebuild has to return. The call
  /// does no real work, so this only fails when it blocks.
  let promptReturn = System.TimeSpan.FromSeconds 1.

  /// How many of the owner monitor's own poll intervals a test waits for it to notice that
  /// the process it watches is gone. The monitor polls on `SageFs.OwnerMonitor.pollIntervalMs`
  /// (the worker's parent monitor uses the same value), so this follows the product's cadence.
  let monitorPollsAllowed = 5

  /// Ceiling on a process monitor noticing a dead or recycled pid and cancelling.
  let monitorNotice =
    System.TimeSpan.FromMilliseconds (float (SageFs.OwnerMonitor.pollIntervalMs * monitorPollsAllowed))

  /// How long a test lets an in-process subscriber callback fire, or lets the same callback
  /// show it does not fire a second time. There is no signal to wait on for "nothing happened".
  let callbackSettle = System.TimeSpan.FromMilliseconds 200.

  /// A pause for something just started on another thread (a watcher arming, an actor
  /// beginning to receive) before the test acts on it. Paired with a barrier where one exists.
  let threadStartSettle = System.TimeSpan.FromMilliseconds 50.

  /// Pause between retries of an IO call that fails for a moment because another process holds
  /// the file. The retry loop is bounded by its own deadline.
  let retryInterval = System.TimeSpan.FromMilliseconds 200.

  /// How often a wait re-reads a page or an HTTP endpoint it is waiting on. Sub-second so a
  /// state that is already true is seen at once; the wait ends on the first read that passes.
  let pollInterval = System.TimeSpan.FromMilliseconds 500.

  /// How often a wait re-reads a slower source (the text a running app printed to the
  /// dashboard), where a read costs a page render and half a second would only add load.
  let slowPollInterval = System.TimeSpan.FromSeconds 1.

  /// The wait a `run_tests` call asks for when the fake engine answers at once, so the call
  /// returns when the engine does and never after the whole wait.
  let runTestsWait = System.TimeSpan.FromSeconds 10.

  /// The wait a `run_tests` call asks for when the fake worker is silent for all of it. The
  /// call has to run out its wait to hand back a pending request id, so this is a delay the
  /// test pays in full: short on purpose.
  let runTestsSilentWait = System.TimeSpan.FromMilliseconds 50.

  /// One MCP tool call that does no build and no restart. The daemon answers in milliseconds
  /// once a session is Ready, so reaching this means the call is stuck.
  let toolCall = System.TimeSpan.FromSeconds 30.

  /// Slack added to a product bound when a test waits on a call that product bound limits.
  /// It covers the thread-pool and scheduling noise between the product giving up and the
  /// test seeing the answer.
  let boundSlack = System.TimeSpan.FromSeconds 30.

  /// One MCP tool call that may restart the session first (run_app moves an Interactive
  /// session into HotReload, hard_reset_fsi_session respawns the worker). Both are bounded by
  /// `SageFs.Timeouts.warmupReadyPollMax`, so the test waits that long plus slack.
  let toolCallThatRestarts = SageFs.Timeouts.warmupReadyPollMax + boundSlack

  /// One request from a test HTTP client to a daemon under load, where the daemon may be busy
  /// warming a session. Past this the request is stuck, not slow.
  let daemonRequest = System.TimeSpan.FromSeconds 60.

  /// One probe of the dashboard inside a poll loop. Short because the loop retries; a probe
  /// that hangs for longer only delays the next one.
  let dashboardProbe = System.TimeSpan.FromSeconds 5.

  /// The first line a spawned daemon writes to stdout. It needs real warmup time (ASP.NET Core
  /// startup), so this is generous; the read ends as soon as the line arrives.
  let daemonFirstOutput = System.TimeSpan.FromSeconds 90.

  /// A session on a small project reaching Ready after create, with build output already on disk.
  let sessionReady = System.TimeSpan.FromSeconds 60.

  /// A session coming back Ready after a worker respawn, or holding its project once Ready.
  /// Both are bounded by `SageFs.Timeouts.warmupReadyPollMax` on the product side, so a faulted
  /// session reports before this runs out.
  let sessionBackAfterRestart = System.TimeSpan.FromSeconds 90.

  /// A session reaching Ready when the create also builds the project cold (restore and compile).
  let sessionReadyColdBuild = System.TimeSpan.FromSeconds 240.

  /// A multi-target sample reaching Ready or Faulted after create, built cold for each target.
  let multiTargetSettle = System.TimeSpan.FromSeconds 180.

  /// A console app started by run_app printing its first line to the dashboard.
  let appOutputAppears = System.TimeSpan.FromSeconds 60.

  /// A running app printing the edited text after a save. A rebuild and a restart are allowed
  /// before the new text shows, so this is longer than the first line.
  let appOutputAfterSave = System.TimeSpan.FromSeconds 180.

  /// How long the tool layer's read caches (list_tests, coverage_intel, and the narrative
  /// cache behind explain_test_failure and diagnose) may lag a state change the status endpoint
  /// already confirmed. Waits poll and end early.
  let toolCacheCatchUp = System.TimeSpan.FromSeconds 15.

/// Durations a test makes up as data, not waits: how long a fabricated test result or pipeline
/// stage says it took. Nothing sleeps for them. A case that asserts on a duration (a formatter,
/// a sum) names its own inputs in its file; these are for the values a case builds only because
/// the type needs a duration, where the number is never read back.
module FixtureDurations =
  // --- tests L to R ---

  /// A result so fast that only the fact it finished matters.
  let fastResult = System.TimeSpan.FromMilliseconds 1.

  /// A short result, between `fastResult` and `usualResult`.
  let quickResult = System.TimeSpan.FromMilliseconds 3.

  /// The duration a made-up passing or failing result reports when no case cares.
  let usualResult = System.TimeSpan.FromMilliseconds 5.

  /// A result slower than `usualResult`, for cases that need two results to differ in time.
  let slowResult = System.TimeSpan.FromMilliseconds 10.
