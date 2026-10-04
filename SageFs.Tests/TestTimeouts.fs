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

  // --- member capability tokens (CapabilityTests) ---
  /// A token's lifetime in the capability tests: one agent run.
  let tokenRun = System.TimeSpan.FromHours 1.
  /// Two runs: a lifetime that is wider than a minter holding one run's worth.
  let tokenTwoRuns = System.TimeSpan.FromHours 2.
  /// A short token: it expires inside the test's own clock, before its lease window does.
  let tokenShort = System.TimeSpan.FromMinutes 20.
  /// One minute past a limit (a token lifetime cap, a lease window, the absolute warmup bound), so a test crosses
  /// the limit and not merely touches it.
  let pastALimit = System.TimeSpan.FromMinutes 1.

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
  /// Ceiling on a child process that reads a 4 MB assembly twice, diffs and writes a delta for it five times, and
  /// has the runtime apply it (DeltaChild `bench`). The first pass has the JIT in it and a loaded runner is slower.
  let bigAssemblyDelta = secs 120.
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
  /// A measurement of how long a save takes to be served: finer than the thing measured, so the poll is not the number.
  let pollMeasure = ms 10.
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
  /// Ceiling on ONE measured edit reaching its verdict on the live-testing stream. A healthy edit takes
  /// about a second; reaching this means the daemon stopped answering, and it is generous because a cold
  /// runner pays for a type-check cache and a JIT that nobody warmed. A passing run never waits it out.
  let liveTestingVerdictCeiling = secs 30.
  /// Ceiling on an evaluated verdict being confirmed by a real build: the quiet window, a rebuild that
  /// restarts the worker, and a run against what it built. A passing run never waits it out. It outlasts the
  /// product's own deadline for a confirmation (`Timeouts.liveTestConfirmationDeadline`) by the margin of one
  /// verdict, so a confirmation that never answers is reported by the daemon on the row (`BuildUnanswered`)
  /// and not by this wait giving up a moment before the daemon does.
  let buildConfirmation = SageFs.Timeouts.liveTestConfirmationDeadline + liveTestingVerdictCeiling
  /// How long a journey that has put Hello.fs back waits for the daemon to start judging the restored text
  /// before it concludes the restore changed nothing. A restore that does change something is judged within a
  /// second (a type-check and an eval), so this is several times that; only a restore that changes nothing
  /// ever waits it out.
  let liveTestingRestoreProbe = secs 5.
  /// Regression bound on the 95th percentile of keystroke-to-verdict. First measurement: p95 742ms over
  /// 20 edits on a 16-thread Ryzen 7 5800XT, Linux, a daemon owned by the runner and other jobs running
  /// on the same machine. The bound is about four times that, because a cold or shared CI runner is slower
  /// and a gate that flakes gets deleted; it still fails on the regressions that matter, which cost whole
  /// seconds (a build or a worker restart on the keystroke path).
  let liveTestingKeystrokeP95Bound = secs 3.
  /// Regression bound on the 95th percentile of save-to-green. First measurement: p95 709ms over 20 saves
  /// on the same machine and in the same conditions. Four times that, for the same reason.
  let liveTestingSaveToGreenP95Bound = secs 3.
  /// Regression bound on the 95th percentile of hot reload save-to-served (a patched save: the first byte
  /// of the write to the first response carrying the new body, polled every `pollTight`). Measured: p95 298
  /// to 353ms over eight runs of 20 saves each (372ms with eight busy loops beside it) on a 16-thread Ryzen
  /// 7 5800XT, Linux, a daemon owned by the runner and other jobs on the same machine; 200ms of that is the
  /// file watcher's debounce, a fixed timer. The bound is about four times the worst p95, because a cold or
  /// shared CI runner is slower and a gate that flakes gets deleted; it still fails on what matters, a save
  /// that takes whole seconds (a compile that fell back to the whole file, or a restart where a patch used to be).
  let hotReloadPatchServedP95Bound = secs 1.5
  /// Regression bound on the 95th percentile of hot reload save-to-confirmed (the daemon's verdict reaching
  /// `Patched`, which needs the new code to have run). Measured: p95 338 to 392ms over the same eight runs
  /// (418ms under load). Close to four times the worst, for the same reason.
  let hotReloadPatchConfirmedP95Bound = secs 1.5
  /// Regression bound on the 95th percentile of save-to-served for a save to an app `run_app` runs, which
  /// SageFs rebuilds and relaunches (a build, a new worker, a new FSI session, a warm-up, then the app).
  /// Measured: p95 7.7 to 16.2s over eight runs of 20 saves (p50 7.1 to 10.0s), and the spread follows the
  /// machine's load: the two runs that ended with a load average of 14 or more were the 15 and 16s ones. About four times
  /// the worst, because a build and a process start get slower on a cold runner by more than a request does.
  let hotReloadRestartServedP95Bound = secs 60.
  /// Regression bound on the 95th percentile of save-to-served for the same save on the default route, where
  /// the running process takes it as a metadata delta (an edit, a delta build, `ApplyUpdate`, the app serving
  /// the new body). Measured twice, on a 16-thread Ryzen 7 5800XT, Linux, a daemon of its own and other jobs
  /// on the machine: p50 1.64s and 1.55s, p95 2.25s and 2.23s over 20 saves each (min 1.50s, max 2.29s), and
  /// the earlier fixture runs the docs quote were 1.8 to 2.6s. The saves fall on two levels, about 1.55s and 2.2s. The bound is about
  /// four times the worst p95 seen, because a build gets slower on a cold runner by more than a request does;
  /// it still fails when the route falls back to a restart (6.9 to 8.1s here), which `Sample.checkRoute`
  /// also catches by name.
  let hotReloadDeltaServedP95Bound = secs 10.
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

  // The restart policy the deterministic simulations run under. `RestartPolicy.defaultPolicy` is NOT
  // used: its startup-crash window is a wait for the machine, scaled by `SAGEFS_MACHINE_TIER` (10s on
  // `Fast`, 50s on `Constrained`), so a simulation built on it changes meaning with the machine it runs
  // on. A 20s gap is "spaced" against 10s and "rapid" against 50s. These values are what the product
  // ships on a `Fast` machine, written out here so no tier can move them.

  /// How many restarts the simulation policy allows before it gives up.
  let simMaxRestarts = 5
  /// How many restarts a loop of startup crashes gets before the simulation policy gives up.
  let simStartupCrashMaxRestarts = 3
  /// The delay before the first restart; each later one doubles it.
  let simBackoffBase = secs 1.
  /// The longest delay between restarts.
  let simBackoffMax = secs 30.
  /// Restarts older than this are forgotten, so a quiet stretch past it starts a new window.
  let simResetWindow = System.TimeSpan.FromMinutes 5.
  /// A crash within this long of the previous restart is a startup crash.
  let simStartupCrashWindow = secs 10.

  /// The restart policy every simulation test builds its scenarios from. A test that needs a different
  /// ceiling writes `{ TestTimeouts.simRestartPolicy with MaxRestarts = ... }`.
  let simRestartPolicy : SageFs.RestartPolicy.Policy =
    { MaxRestarts = simMaxRestarts
      BackoffBase = simBackoffBase
      BackoffMax = simBackoffMax
      ResetWindow = simResetWindow
      StartupCrashWindow = simStartupCrashWindow
      StartupCrashMaxRestarts = simStartupCrashMaxRestarts }

  /// `policy` with every duration replaced by the fixed simulation value, so a policy that came from
  /// somewhere tier-scaled (a generator that reads `RestartPolicy.defaultPolicy`) runs the same on every
  /// machine. The counts are the caller's own. Every field is named, so a duration added to the policy is
  /// a compile error here until someone decides what the simulation value for it is.
  let pinSimDurations (policy: SageFs.RestartPolicy.Policy) : SageFs.RestartPolicy.Policy =
    { MaxRestarts = policy.MaxRestarts
      BackoffBase = simBackoffBase
      BackoffMax = simBackoffMax
      ResetWindow = simResetWindow
      StartupCrashWindow = simStartupCrashWindow
      StartupCrashMaxRestarts = policy.StartupCrashMaxRestarts }

  /// `scenario` with its policy pinned (`pinSimDurations`). The generators in `SageFs.Simulation`
  /// build their scenarios from `RestartPolicy.defaultPolicy`, so every simulation test that takes a
  /// scenario from them passes it through here before it runs.
  let pinSimScenario (scenario: SageFs.Simulation.Scenario.Scenario) : SageFs.Simulation.Scenario.Scenario =
    { scenario with Policy = pinSimDurations scenario.Policy }

  // Crash scenarios for the deterministic simulation. These are inputs to the scenario, not
  // waits, and they are chosen against `simRestartPolicy` (a crash within its 10s startup window
  // is a startup crash; a gap over its 5 minute reset window starts a new window).

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
  /// report, a timer firing, a callback, a file watcher callback, a status notification from a
  /// background task). Five seconds absorbs a starved thread pool.
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
  /// status bar, a connection that is retried, the text a running app printed to the dashboard
  /// (a read costs a page render, and half a second would only add load).
  let slowPoll = System.TimeSpan.FromSeconds 1.

  // Settles: real time a test lets pass because the effect under test needs it.

  /// A real wall-clock gap so a timestamp taken after it is strictly later than one
  /// taken before it, not the same tick.
  let clockGap = System.TimeSpan.FromMilliseconds 30.

  /// A moment for a server to start on a slow request before the test sends the next one.
  let workStartSettle = System.TimeSpan.FromMilliseconds 200.

  /// How long a test watches for an answer that must NOT come yet (a caller parked on a session
  /// that is still rebuilding). The session manager answers a parked caller in the same step that
  /// makes the answer true, with no timer, so an answer that was going to arrive wrongly arrives
  /// within a few scheduling turns. This is long enough for that and short enough not to be felt.
  let absentAnswerWindow = System.TimeSpan.FromMilliseconds 300.

  /// A pause for a file watcher to re-arm after a failed eval's burst of events.
  let watcherRearmSettle = System.TimeSpan.FromSeconds 1.

  /// A margin so small that it only separates "at a boundary" from "just past it", and a
  /// rounding tolerance for a jittered span.
  let boundaryMargin = System.TimeSpan.FromMilliseconds 1.

  /// How far past a bound a scenario puts a value it needs to be unambiguously beyond: a clock past a lease or a
  /// queued ask's ttl, an elapsed past an inactivity window.
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

  /// The deadline a test gives the live-values walk for one binding whose getter never returns.
  /// Real time but tiny: the case waits on the walk giving up, and a longer one only slows it down.
  let blockedGetterBudget = System.TimeSpan.FromMilliseconds 150.

  /// How long a test gives a getter that was told to stop (Thread.Interrupt) to leave its wait.
  /// Real time but small: the case waits on the getter's own signal, this only bounds a getter that ignores it.
  let interruptGrace = System.TimeSpan.FromMilliseconds 300.

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

  // --- tests L to R ---
  // A ceiling is how long a wait may take before the test calls it failed. A test that reaches
  // one has failed; it is never how a passing test finishes. Where a wait ends the moment the
  // thing it waits on happens, a generous ceiling costs a passing test nothing.

  // `patience` (20 s) is defined once, in the A to K section, and used here too.

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

  /// How long a `get_session_status` or `run_tests` call asks the daemon to hold it open for a
  /// warming session or a running test run. It is the daemon's own cap on that wait, so asking for
  /// more would only be clamped, and the daemon does the waiting so the test never polls.
  let daemonHeldWait = System.TimeSpan.FromSeconds 60.

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

  /// How many seconds one `get_session_status` call holds the request open for a warming session.
  /// The tool clamps anything above its own ceiling (60), so a longer wait is a loop of these.
  let sessionStatusLongWaitSeconds = 60

  /// A console app started by run_app printing its first line to the dashboard.
  let appOutputAppears = System.TimeSpan.FromSeconds 60.

  /// A running app printing the edited text after a save. A rebuild and a restart are allowed
  /// before the new text shows, so this is longer than the first line.
  let appOutputAfterSave = System.TimeSpan.FromSeconds 180.

  /// How long the tool layer's read caches (list_tests, coverage_intel, and the narrative
  /// cache behind explain_test_failure and diagnose) may lag a state change the status endpoint
  /// already confirmed. Waits poll and end early.
  let toolCacheCatchUp = System.TimeSpan.FromSeconds 15.

  // ---- half B ----
  // Files L to Z. The same rules as above: named for what the wait or the input is FOR, and a
  // ceiling is never how a passing test finishes.

  let private mins (n: float) = System.TimeSpan.FromMinutes n
  let private days (n: float) = System.TimeSpan.FromDays n

  // Waits on a spawned process.

  /// The ceiling on a `where code` lookup finishing once its first line has been read.
  let shimLookupExit = secs 3.

  // Pid reuse and the start-time fence (OwnerMonitor).

  /// How much later (or earlier) a process that reused a dead owner's pid started than the
  /// owner did: a day, which is unambiguously a different process and not read jitter.
  let pidReuseGap = days 1.
  /// The jitter the fence tolerance has to exceed, so two reads of one process's start never
  /// disagree. Real cross-process skew is microseconds; this is far above it.
  let readJitterCeiling = ms 10.
  /// The shortest gap a real pid reuse leaves between two processes that held the same pid
  /// (sequential pid allocation takes seconds to hours to wrap). The fence tolerance has to
  /// stay below it.
  let shortestRealPidReuseGap = mins 1.

  // Bounds and windows a test hands to the code under test.

  /// A save-confirmation bound that differs from the product's `Timeouts.patchConfirmation`, to
  /// show the announcer hands on the bound it is given and does not read its own.
  let patchBoundOther = secs 7.
  /// A restart policy's reset window made short, so the window cases need a minute or two of
  /// fake time. Everything else in the policy is the product's.
  let restartResetWindowShort = mins 1.
  /// The grace period every `Watchdog.decide` case runs with. The cases put the daemon's last
  /// start inside it or beyond it, so they are written as fractions and multiples of it.
  let watchdogGracePeriod = secs 10.
  /// How long a save waited for the compiler before it was dropped, as the compiler-busy event
  /// reports it.
  let compilerQueueWaited = secs 60.
  /// The eval budget a save ran out of, as the timed-out event reports it.
  let reloadEvalBudget = mins 5.
  /// How much older than the build a source file is when it was written well before it.
  let sourceWrittenBeforeBuildBy = mins 5.
  /// How much newer than the build a source file is when it was edited right after it: any
  /// positive gap counts, so the smallest whole second.
  let sourceEditedAfterBuildBy = secs 1.
  /// A friction retention window made short enough that a handful of rows crosses it. Not the
  /// product's default.
  let smallRetentionWindow = days 5.

  // Fakes that finish after a tiny, real delay.

  /// A fake async step that yields once before it throws, so the exception arrives on a
  /// continuation and not on the calling stack.
  let yieldBeforeFault = ms 1.
  /// A fire-and-forget task started on the thread pool gets this long to run or to log
  /// before the test looks at what it did. There is no signal to wait on for "it was swallowed".
  let fireAndForgetSettle = ms 100.
  /// The fast side of a race between two asyncs, finishing well before `slowWork`.
  let fastRaceSide = ms 10.

  // A server a test runs that holds a connection or a reply.

  /// A worker that is still busy: it sends nothing more for far longer than any wait the
  /// test makes, so only the proxy's own window or a cancel can end the read.
  let workerSilentFor = secs 10.
  /// The gap between lines of a slow, steady stream. Six of them run past `streamWindowSteady`
  /// as a whole, while each gap stays well inside it.
  let streamLineGap = ms 100.
  /// An eval that outlasts the worker's one-second status threshold by a wide margin, even on a
  /// loaded runner.
  let evalOutlastingStatusProbe = secs 3.
  /// A worker whose HTTP server is wedged: the proxy never answers within the test.
  let hungWorkerReply = secs 60.
  /// How long a stop of that hung worker may take before the case calls it a hang. Half the proxy's hang,
  /// derived from it and not guessed: a stop that waited on the hung proxy takes at least `hungWorkerReply`,
  /// so it can never fit under this, while a healthy stop (its own bound is a sum of waits, about 5.5 seconds
  /// on an idle machine) has a wide margin however loaded the machine is. A flat ten seconds was both: it did
  /// not follow the hang, and a healthy stop lost to it when the gate shared the machine with other builds.
  let hungWorkerStopBudget = System.TimeSpan.FromTicks(hungWorkerReply.Ticks / 2L)

  // A real VS Code window, driven over its debug port.

  /// The editor's debug port answering after launch.
  let editorPortReady = secs 30.
  /// The editor's debug port released after the old instance was killed, before the next launch
  /// reuses it.
  let editorPortReleased = secs 10.
  /// The quick-input palette appearing or closing after the keystroke that toggles it.
  let paletteToggle = secs 5.
  /// The palette closing when a close was already asked for and only the render is left.
  let paletteClosedAlready = secs 2.
  /// A panel's text (the output channel) showing up once the panel has rendered.
  let panelRenders = secs 10.
  /// Quick Open listing a project file. Indexing is asynchronous and a cold window is slow.
  let quickOpenIndexes = secs 15.
  /// The time the quick-input's fuzzy matcher needs to settle on the typed text. It cannot be
  /// polled: any page evaluation there steals focus from the quick-input, so the Enter that
  /// follows lands on the editor and the command silently never runs.
  let matcherSettle = ms 800.
  /// A daemon started for a VS Code journey on a real project reaching a Ready session on a cold
  /// CI runner (restore, build, warmup).
  let vscodeSessionReady = secs 300.

  // A floor a test holds the product to.

  /// The least time `SageFsConfig.WorkerStartupTimeoutMs` may allow a worker to start in. A cold
  /// worker on a loaded machine needs this long, so a default below it would fail good starts.
  let workerStartupFloor = secs 30.
  // ---- half A ----
  // Waits, budgets and clock steps for the tests in the first half of the alphabet (A to
  // LiveTesting). A passing test never waits out a ceiling: reaching one means the test failed.
  let private minutes (n: float) = System.TimeSpan.FromMinutes n

  /// How far either side of a boundary (a lease window, the point a cohort went silent) a clock tick
  /// lands, to be clearly inside it or clearly past it.
  let clockMargin = minutes 1.
  /// How long after the lease window opens a landing settles in the retention scene: still inside
  /// the window of the departure tick that follows.
  let landingSettleAfterLease = secs 30.

  /// Turn a wait into the whole milliseconds an API that takes an int wants.
  let asMs (t: System.TimeSpan) : int = int t.TotalMilliseconds

  // Process waits.

  /// A short-lived helper process the test ran to completion (git, a CLI call), or a process it
  /// just killed, finishing and being gone. A cold runner takes seconds.
  let childExit = secs 5.
  /// A helper or a killed process tree that takes real work to finish or tear down: a daemon with
  /// its workers, a hot reload app host, a git checkout.
  let childExitSlow = secs 15.
  /// Let the async readers on a process's output drain after it exited, so the log is complete.
  let readerFlush = secs 1.

  // Waits and holds inside a test's own scenario.

  /// The ceiling on a daemon's memory settling back to within tolerance of its baseline after the
  /// sessions that grew it are stopped.
  let rssSettleBudget = secs 30.
  /// How long a test effect holds itself in flight, so every effect of a burst is running at once
  /// and only the concurrency cap can limit them.
  let effectInFlightHold = ms 50.
  /// The delay inside a delayed-message effect: long enough that the dispatch arrives from a
  /// later continuation, not from inside the effect call.
  let delayedMsgDelay = ms 10.
  /// How long a fake test runner takes to report results after it started, so the run is
  /// observably in flight in between.
  let fakeRunReportDelay = ms 20.

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

  // ---- half B ----

  /// A timing field a fabricated record carries without anything having been measured: the
  /// eval stats of a session that has not evaluated, a phase that was not timed. Never read back.
  let unmeasuredMs = 0L

  /// An opened namespace or module in a fabricated warmup context, with no time worth reporting.
  let instantOpenMs = 0.0

  /// The average, minimum and maximum eval time of a fabricated status that has to tell its three
  /// fields apart: all different and all non-zero, so a swapped or dropped field fails a
  /// round trip. They are not read as durations.
  let evalAvgMs = 150L
  let evalMinMs = 5L
  let evalMaxMs = 1000L

  /// How long a made-up cell eval took in a binding record. Never read back.
  let cellEvalMs = 1.5

  /// A treemap entry's duration, a plain float. The case checks it comes back as given.
  let treemapEntryMs = 42.5

  /// How long a made-up session ran before it was stopped, in a manifest case about how old a
  /// stopped entry is. Only the age of the stop matters, not this.
  let sessionRanFor = System.TimeSpan.FromHours 1.

  /// The eval durations the stats cases record. Totals, minimums, maximums and averages are
  /// derived from these, so each case states what it expects in terms of the inputs. The slow
  /// one is twice the fast one, and the slowest three times: the average of the fast and the
  /// slowest is the slow.
  let evalFast = System.TimeSpan.FromMilliseconds 100.
  let evalSlow = System.TimeSpan.FromMilliseconds 200.
  let evalSlowest = System.TimeSpan.FromMilliseconds 300.

  /// One eval in a timeline that is far longer than the rest, so a case can check the sparkline
  /// scales to the window it shows and not to this outlier.
  let outlierEvalMs = 10_000L

  /// The durations of three stored test results, all different so a round trip that mixes the
  /// entries up fails. The last is a timed-out result, which reports about the time it ran.
  let storedPassMs = 12u
  let storedFailMs = 34u
  let storedTimeoutMs = 5000u

  /// How long the end-to-end dispatch case says its passing result took. Never read back.
  let dispatchedResult = usualResult

  /// How long a short pipeline stage (the tree-sitter parse) took in a made-up timing record.
  let shortStage = System.TimeSpan.FromMilliseconds 10.

  /// How long a stage clearly longer than `shortStage` took, for a case that needs the two apart.
  let longerStage = System.TimeSpan.FromMilliseconds 200.

  /// The duration of a result that a round-trip case checks comes back unchanged, or that a formatting case
  /// feeds in and then looks for in the output. It is neither zero nor one of the shared values above, so a
  /// value lost on the way reads back different.
  let roundTripDuration = System.TimeSpan.FromMilliseconds 42.

  /// An eval timeline entry a case feeds the timeline. Every entry starts at zero, so only the
  /// duration (and the order the entries are recorded in) differ between the entries of a case.
  let timelineEntry (cellId: int) (durationMs: int64) (status: SageFs.Features.EvalTimeline.EvalStatus)
    : SageFs.Features.EvalTimeline.TimelineEntry =
    { CellId = cellId; StartMs = unmeasuredMs; DurationMs = durationMs; Status = status }

  /// An eval timeline entry that starts `startMs` after the first, for a case that reads the
  /// start offsets back.
  let timelineEntryStartingAt (startMs: int64) (cellId: int) (durationMs: int64) (status: SageFs.Features.EvalTimeline.EvalStatus)
    : SageFs.Features.EvalTimeline.TimelineEntry =
    { CellId = cellId; StartMs = startMs; DurationMs = durationMs; Status = status }

  /// A warmup phase breakdown a case spells out phase by phase.
  let warmupPhaseTiming (scanSourceFilesMs: int64) (scanAssembliesMs: int64) (openNamespacesMs: int64) (totalMs: int64)
    : SageFs.WarmUp.WarmupPhaseTiming =
    { ScanSourceFilesMs = scanSourceFilesMs
      ScanAssembliesMs = scanAssembliesMs
      OpenNamespacesMs = openNamespacesMs
      TotalMs = totalMs }

  /// A warmup phase breakdown where only the total matters to the case: no single phase was timed.
  let warmupTotalOnly (totalMs: int64) : SageFs.WarmUp.WarmupPhaseTiming =
    warmupPhaseTiming unmeasuredMs unmeasuredMs unmeasuredMs totalMs

  /// The total a warmup-boundary case says warmup took, which the case then checks was not
  /// inflated by work that came after it.
  let warmupBoundaryMs = 120L

  /// A start time, in ticks, a case gives an owner process. The fence cases add skews to it, so
  /// the value only has to be far from zero and from overflow.
  let ownerStartTicks = 1_000_000_000L

  // ---- half A ----
  // Made-up durations for the tests in the first half of the alphabet (A to LiveTesting). The
  // helpers build each value from a number once, here.
  let private ms (n: float) = System.TimeSpan.FromMilliseconds n
  let private secs (n: float) = System.TimeSpan.FromSeconds n
  let private mins (n: float) = System.TimeSpan.FromMinutes n
  let private hours (n: float) = System.TimeSpan.FromHours n
  let private days (n: float) = System.TimeSpan.FromDays n

  // Elapsed times and timestamps that say "nothing ran" or "nobody set this".

  /// The elapsed time of work that did not run, as the float a view model carries.
  let notRunMs : float = SageFs.Timeouts.notRun.TotalMilliseconds
  /// The elapsed time of work that did not run, as the whole milliseconds a protocol record carries.
  let notRunMsInt64 : int64 = int64 SageFs.Timeouts.notRun.TotalMilliseconds
  /// Where an entry sits on an eval timeline: at its origin. No case reads the offset back.
  let timelineOrigin : int64 = notRunMsInt64
  /// A created-at that nobody wrote, the same value the persisted types start from.
  let unwrittenAtMs : int64 = System.DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds()
  /// A created-at the golden file case pins (2024-03-02 UTC), so the bytes it writes never change.
  let goldenCreatedAtMs : int64 = 1709337600000L

  // Durations a stored test result carries through a write and a read. Only that they come
  // back unchanged matters.

  /// A stored result's duration in a case that never reads it back.
  let savedMsTiny : uint32 = 1u
  /// A stored result's duration in a case that round-trips it.
  let savedResultMs : uint32 = 100u
  /// A second stored result's duration, different from `savedResultMs` so two entries tell apart.
  let savedResultMsOther : uint32 = 200u
  /// Four durations that differ, for a file holding four entries that must not be mixed up.
  let savedMsDistinct : uint32 array = [| 1u; 2u; 3u; 4u |]
  /// The span a stalled or timed-out result carries through a write and read. The format must keep
  /// any span; this one is only chosen to be non-zero and to survive being compared whole.
  let stalledSpan = secs 30.

  // Eval durations a case feeds to a statistic and then reads the answer of.

  /// One of two durations whose mean is `meanOfLowAndHigh`.
  let meanLow = ms 2.
  /// The other of the two, above `meanLow`.
  let meanHigh = ms 4.
  /// Exactly halfway between `meanLow` and `meanHigh`: what averaging the two must give.
  let meanOfLowAndHigh = ms 3.
  /// A binding's eval duration in a case that never reads it back.
  let bindingEvalMs : float = 1.0
  /// An eval duration with a fractional part, so a round trip shows it is kept whole.
  let fractionalElapsed = ms 12.5
  let fractionalElapsedMs : float = fractionalElapsed.TotalMilliseconds
  /// The total a traced eval reports in the event case list. Never read back.
  let tracedTotalMs : float = 5.0
  /// The duration of an eval history entry nobody reads.
  let evalEntryMs : int64 = 1L
  /// How long a failed open attempt says it took. Never read back.
  let failedOpenMs : float = 1.0
  /// How long the second pending rebuild's tree-sitter stage took. Not the first rebuild's value,
  /// so a mix-up between the two would show. Never read back.
  let secondRebuildTreeSitterStage = ms 7.
  /// How long the second pending rebuild's FCS stage took. Never read back.
  let secondRebuildFcsStage = ms 12.
  /// The durations of the five sample tests in the test filter bar cases. They differ so the
  /// size order of the entries shows.
  let treemapMs : float array = [| 100.0; 200.0; 50.0; 10.0; 5.0 |]

  // How long a signal stayed off baseline.

  /// A signal off baseline for a short while: not a long-held verdict.
  let sustainedBriefly = mins 1.
  /// A signal off baseline long enough to count as held.
  let sustainedLong = mins 2.

  // Uptimes and ages.

  /// A daemon uptime no assertion depends on: a daemon that is not brand new.
  let uptimeUnread = mins 10.
  /// An uptime the formatter prints in minutes only.
  let uptimeMinutesLabel = mins 45.
  /// An uptime the formatter prints as hours and minutes.
  let uptimeHoursLabel = hours 2.5
  /// An uptime the formatter prints as days and hours.
  let uptimeDaysLabel = days 1.5
  /// A time since the last pass short enough to read "just now".
  let sinceUnderAMinute = secs 30.
  /// A time since the last pass the narrative prints in hours.
  let sinceHours = hours 2.
  /// A time since the last pass the narrative panel renders as "7 minutes ago".
  let sinceMinutesRendered = mins 7.
  /// How long ago another checkout touched a file: recent enough to be worth an advisory.
  let otherCheckoutTouchedAgo = mins 2.
  /// A later tool call in the same session, so two calls have different times.
  let laterToolCall = secs 10.

  // Ttls the daemon ownership cases choose against.

  /// A ttl long enough that a second of idle time is nowhere near it. Also a ttl that only has to
  /// be present, whatever its length.
  let ttlLong = mins 30.
  /// A daemon that has been idle for a very long time.
  let idleVeryLong = days 1.
  /// A ttl the caller gave explicitly.
  let ttlExplicit = mins 5.
  /// A ttl of an hour: the exact value in the reported bug, far above the activity window cap.
  let ttlHuge = mins 60.
  /// A ttl under the activity window cap, which must never be inflated past itself.
  let ttlUnderCap = secs 30.
  /// A ttl a case puts the idle time exactly on.
  let ttlBoundary = secs 5.
  /// Ttls from a second to a day, to show the activity window never exceeds the ttl it serves.
  let ttlsAcrossTheRange = [ secs 1.; mins 1.; mins 2.; mins 5.; hours 1.; hours 24. ]
  /// What `--ttl 500ms` parses to.
  let parsedMillis = ms 500.
  /// What `--ttl 30m` parses to.
  let parsedMinutes = mins 30.
  /// What `--ttl 1h` parses to.
  let parsedHour = hours 1.
  /// What `2h` parses to.
  let parsedHours = hours 2.
  /// What a bare `90` parses to: seconds.
  let parsedBare = secs 90.

  // Scenario inputs.

  /// How far inside a boundary a lease case puts a caller who is still asking (inside the window a queued ask
  /// stays in line for): any positive margin does, this one is far above the clock's resolution.
  let insideTheBoundary = System.TimeSpan.FromSeconds 30.

  /// A pass of virtual time (in seconds) longer than any lease lives, so every abandoned lease is
  /// past its ttl. Chosen against `Timeouts.leaseTtlRunApp`, the longest.
  let passPastEveryLease : int = int (SageFs.Timeouts.leaseTtlRunApp.TotalSeconds * 1.5)
  /// A pass of virtual time (in seconds) long enough that a queued ask which was never repeated has missed its
  /// retry window by more than the grace the pool allows, and still shorter than `Timeouts.leaseAskStaleAfter`,
  /// so the ask has been given up on without having been dropped from the line.
  let passPastAGoneWaiter : int = int (SageFs.ExpensiveWorkLease.askGrace.TotalSeconds * 2.0)
  /// The timeout seconds a held config carries through a migration. Only `Retries` is read back.
  let heldConfigTimeoutSeconds : int = 30
  /// How far a browser's clock is set from the real time (ten minutes, either way) in the journeys
  /// that prove the disconnect banner does not trust the client clock.
  let clockSkewMs : int64 = 600_000L
  /// How far a browser page's clock jumps when the page was suspended or throttled (a laptop lid, a
  /// background tab): a minute, which is far past any staleness budget, so any check that reads the
  /// gap as an outage would claim one.
  let suspendedPageMs : int64 = 60_000L

/// Values a case pins because the number IS the expectation: the case is about a bound, a
/// default or a window, and says so in its name. A case that merely needs a duration to build a
/// value takes it from `FixtureDurations`, and a case that waits takes it from `TestTimeouts`.
module PinnedDurations =
  let private ms (n: float) = System.TimeSpan.FromMilliseconds n
  let private secs (n: float) = System.TimeSpan.FromSeconds n
  let private mins (n: float) = System.TimeSpan.FromMinutes n
  let private hours (n: float) = System.TimeSpan.FromHours n

  // The bounds `ValidTimeout.create` accepts and refuses, on both sides of each edge.

  /// Under the one-second floor: refused.
  let belowTimeoutFloor = ms 500.
  /// Exactly the one-second floor: accepted.
  let timeoutFloor = secs 1.
  /// Below zero: refused.
  let negativeTimeout = secs (-1.)
  /// Exactly the ten-minute ceiling: accepted.
  let timeoutCeiling = mins 10.
  /// Past the ten-minute ceiling: refused.
  let aboveTimeoutCeiling = mins 11.
  /// A per-test timeout under the floor, which `setPerTestTimeout` must refuse.
  let perTestTimeoutBelowFloor = ms 100.

  // Values `Timeouts` is documented to hold, pinned so that changing one is a decision.

  /// `Timeouts.healthCheck`.
  let healthCheck = secs 2.
  /// `Timeouts.processNormalExit`.
  let processNormalExit = secs 3.
  /// `Timeouts.sseKeepAlive`.
  let sseKeepAlive = hours 24.
  /// `Timeouts.workerHttpRequest`, and the cap `ValidTimeout` allows.
  let workerHttpRequest = mins 10.

  // Scenario clocks fed to the deterministic simulations, in virtual seconds.

  /// The save interval the two hand-picked periodic-save reproductions run under. Their t=5,
  /// t=10, t=12 narrative is written against it. The product's own cadence is
  /// `Timeouts.manifestSaveInterval`.
  let reproSaveIntervalSeconds = 5.0

  /// The base delay the backoff range cases are written against: their ranges (50 to 150, 100 to
  /// 300, 150 to 450) are fractions and multiples of it. It is the case's own, not the product's.
  let retryBaseMs = 100<SageFs.Measures.ms>
  /// A base delay so small that the jitter range rounds to nothing, so the delay is exactly it.
  let retryBaseNoJitterMs = 1<SageFs.Measures.ms>

  /// The open time of the namespace in the surface JSON snapshot. The snapshot text spells it
  /// as `"durationMs":1.5`, so the value is part of what the case pins.
  let surfaceWireOpenedMs = 1.5

/// The stage timings the live-testing cycle-timing cases feed in. Nothing waits for these. The
/// cases that format a timing state the text they must produce ("TS:0.8ms | FCS:142ms |
/// Run:87ms (12)"), so the value is part of what the case pins, and each name carries it:
/// `treeSitter0p8ms` is the tree-sitter stage at 0.8 ms, `fcs142ms` the FCS stage at 142 ms,
/// `run87ms` the execution stage at 87 ms. A stage name is shared by every case that uses that
/// stage at that value.
module StageTimings =
  let private ms (n: float) = System.TimeSpan.FromMilliseconds n

  let treeSitter0p5ms = ms 0.5
  let treeSitter0p8ms = ms 0.8
  let treeSitter1ms = ms 1.0
  let treeSitter1p2ms = ms 1.2
  let treeSitter1p5ms = ms 1.5

  let fcs50ms = ms 50.0
  let fcs85ms = ms 85.0
  let fcs100ms = ms 100.0
  let fcs142ms = ms 142.0

  let run30ms = ms 30.0
  let run42ms = ms 42.0
  let run50ms = ms 50.0
  let run87ms = ms 87.0

/// Where a case puts the clock when it drives the live-testing debounce by hand. Every offset is
/// from the case's own `t0`, and each is written against the product's two debounce windows, so
/// a case that says "just past the tree-sitter window" still means that if the window moves.
module DebounceClock =
  let private ms (n: float) = System.TimeSpan.FromMilliseconds n

  /// How long after a keystroke the tree-sitter parse is due.
  let treeSitterDelay = SageFs.Timeouts.liveTestTreeSitterDebounce
  /// How long after a keystroke the FCS type check is due.
  let fcsDelay = SageFs.Timeouts.liveTestFcsDebounce
  /// `treeSitterDelay` as the debounce channel's own unit.
  let treeSitterDelayMs = SageFs.Features.LiveTesting.TestCycleDebounce.treeSitterDelayMs
  /// `fcsDelay` as the debounce channel's own unit.
  let fcsDelayMs = int fcsDelay.TotalMilliseconds * 1<SageFs.Measures.ms>

  /// The smallest step past a window: due, with nothing to spare.
  let private pastBy = ms 1.
  /// The first moment the tree-sitter parse is due.
  let pastTreeSitter = treeSitterDelay + pastBy
  /// The first moment the FCS type check is due.
  let pastFcs = fcsDelay + pastBy

  /// Part way through the tree-sitter window, when nothing is due yet. Also where a second edit
  /// lands when a case switches files part way through the first edit's window.
  let partWayThroughTreeSitter = ms 30.

  /// The gap between keystrokes in a burst. It is shorter than the tree-sitter window, so every
  /// keystroke restarts it.
  let keyGap = ms 20.
  /// When keystroke number `n` of a burst lands (the first, number 0, lands at t0).
  let keyAt (n: int) = keyGap * float n

  /// Part way through the FCS window of the second edit in the restarted-window case: past the
  /// window the first edit started, short of the one the second edit restarted.
  let insideRestartedFcs = ms 352.
  /// Where the second edit lands in the restarted-window case, after the first window's
  /// tree-sitter parse has already fired.
  let restartingEditAt = ms 100.
  /// A tick long after both windows have fired and the cycle has gone idle.
  let wellAfterBothWindows = ms 500.

/// Numbers that are not durations at all but that the timeout ratchet reads as milliseconds: byte
/// sizes, counts, seed bounds and clock readings. Each is named for what it stands for.
module TestMagnitudes =
  /// One decimal gigabyte, the unit a fabricated machine's memory is written in.
  let gigabyte = 1_000_000_000L
  /// One decimal megabyte.
  let megabyte = 1_000_000L
  /// The upper bound of the seeds a simulation property draws.
  let seedBound = 1_000_000
  /// The count of probes a coverage bitmap case packs.
  let packedProbeCount = 10_000
  /// The panel width the dashboard's test status grid is laid out inside.
  let treemapPanelWidthPx = 320.0
  /// The panel height the old duration treemap was fixed at.
  let treemapPanelHeightPx = 180.0
  /// The cell size the status grid never shrinks below, so no test renders as an
  /// unreadable sub-pixel speck however many tests the session has.
  let treemapMinCellPx = 3.0
  /// The cell size the status grid never grows beyond, so a small run is not one
  /// enormous block that swamps the panel.
  let treemapMaxCellPx = 12.0
  /// The minimum width the old treemap wanted before it would draw a test's name.
  let treemapMinLabelWidthPx = 28.0
  /// The minimum height the old treemap wanted before it would draw a test's name.
  let treemapMinLabelHeightPx = 14.0
  /// The real total duration a 14,360-test session reported across its timed tests.
  let treemapPanelTotalMs = 238.0
  /// How many of those tests carried that duration.
  let treemapPanelPassedCount = 76
  /// How many reads a tracker case has been asked to see before it calls a loop hot.
  let readCountNeverReached = 1_000_000
  /// A pending backlog so deep that no honest bound lets it through.
  let absurdBacklog = 1_000_000
  /// The most entries a directory walk may visit in a case that is about depth, not entries.
  let walkEntriesNotReached = 50_000
  /// How many times the tokenizer runs in the throughput case.
  let tokenizeRuns = 50_000
  /// A log entry far larger than the log's cap.
  let entryLargerThanCap = 10_000
  /// How many fixture hosts one test process runs at once (`HostSlots`): each is a real host, an FSI session and an app,
  /// about a core and a half while it starts, so this is bounded by what a machine runs beside the other tiers, not by
  /// what is quick. Three is what the parity rows always ran.
  let concurrentHosts = 3
  /// The longest run capture the test runner keeps, in characters.
  let runCaptureCap = 200_000
  /// A byte budget for the aggressive tweak-log retention policy.
  let tweakLogByteBudget = 100_000L
  /// The stride between the made-up tick values a generator draws. `Int32.MaxValue` strides
  /// stay below `DateTime.MaxValue`, so every drawn value is a legal date.
  let tickStride = 1_000_000_000L
  /// Milliseconds in one second, to write an expected gap in milliseconds from the seconds the
  /// case fed in.
  let msPerSecond = 1000
  /// The span, in whole seconds, the manifest property draws a made-up creation time from.
  let manifestSecondsLow = 1_000_000
  let manifestSecondsHigh = 2_000_000
  /// Milliseconds in one minute, to turn a drawn count of minutes into an age.
  let msPerMinute = 60_000L
  /// A creation time, in unix milliseconds, a roundtrip case gives a manifest. Any real-looking
  /// reading works; it only has to come back unchanged.
  let manifestCreatedAtMs = 1709500000000L
  /// A manifest creation time at the unix epoch, for a case that is not about the time.
  let epochMs = 0L
  /// The first moment after the epoch: a manifest whose creation time is set but plainly made up.
  let oneMsAfterEpoch = 1L
  /// A made-up manifest creation time a case only needs to see come back unchanged.
  let fixedCreatedAtMs = 1234567890L

/// Eval statistics and warmup timing records a case builds only because the type needs them.
module FixtureStats =
  /// A session that has run no evals.
  let noEvals : SageFs.Server.DashboardTypes.EvalStatsView =
    { Count = 0
      AvgMs = FixtureDurations.notRunMs
      MinMs = FixtureDurations.notRunMs
      MaxMs = FixtureDurations.notRunMs
      Sparkline = ""
      P50Ms = None
      P95Ms = None }
  /// Three evals that each took the same short time.
  let threeQuickEvals : SageFs.Server.DashboardTypes.EvalStatsView =
    { noEvals with Count = 3; AvgMs = 1.0; MinMs = 1.0; MaxMs = 1.0 }
  /// Seven evals with a mean between the quickest and the slowest.
  let sevenEvals : SageFs.Server.DashboardTypes.EvalStatsView =
    { noEvals with Count = 7; AvgMs = 42.0; MinMs = 1.0; MaxMs = 100.0 }
  /// The evals the stats snapshot renders.
  let snapshotEvals : SageFs.Server.DashboardTypes.EvalStatsView =
    { noEvals with Count = 42; AvgMs = 123.4; MinMs = 5.0; MaxMs = 1045.0 }
  /// Five evals with a wide spread between the fastest and the slowest.
  let wideSpread : SageFs.Server.DashboardTypes.EvalStatsView =
    { noEvals with Count = 5; AvgMs = 100.0; MinMs = 50.0; MaxMs = 200.0 }
  /// Three evals close together.
  let tightSpread : SageFs.Server.DashboardTypes.EvalStatsView =
    { noEvals with Count = 3; AvgMs = 100.0; MinMs = 80.0; MaxMs = 120.0 }
  /// Three evals close together with one slower tail.
  let tightSlowTail : SageFs.Server.DashboardTypes.EvalStatsView =
    { noEvals with Count = 3; AvgMs = 100.0; MinMs = 80.0; MaxMs = 150.0 }
  /// Many quick evals.
  let manyQuickEvals : SageFs.Server.DashboardTypes.EvalStatsView =
    { noEvals with Count = 42; AvgMs = 50.0; MinMs = 10.0; MaxMs = 100.0 }
  /// A warmup that ran no phase.
  let phaseTimingNotRun : SageFs.WarmUp.WarmupPhaseTiming =
    { ScanSourceFilesMs = FixtureDurations.notRunMsInt64
      ScanAssembliesMs = FixtureDurations.notRunMsInt64
      OpenNamespacesMs = FixtureDurations.notRunMsInt64
      TotalMs = FixtureDurations.notRunMsInt64 }
  /// A warmup whose phases report nothing but a short total.
  let phaseTimingBriefTotal : SageFs.WarmUp.WarmupPhaseTiming =
    { phaseTimingNotRun with TotalMs = 5L }

/// Waits a browser journey gives the page, in the whole milliseconds Playwright and the journey
/// helpers take. Each wait ends the moment the page shows what it waits for, so a long one costs a
/// passing run nothing and only a failing one pays it.
module BrowserWaits =
  let private ms (t: System.TimeSpan) = TestTimeouts.asMs t

  /// A page state that is already true, or settles within a few frames: an element that must be
  /// absent, a scroll reaching the bottom, a connection flag that is already set.
  let pageProbe = ms TestTimeouts.patienceBrief
  /// Text or an element that the first server render, or the next SSE push, puts on the page.
  let pageRenders = ms TestTimeouts.patienceInProcess
  /// A panel or session card changing after the daemon handled a click, or the SSE stream
  /// attaching after a navigation.
  let panelUpdates = ms TestTimeouts.sseListen
  /// Something the daemon has to do work for: an eval's output reaching the output panel, a
  /// session reaching Ready, a panel appearing after a workflow switch.
  let daemonWork = ms TestTimeouts.requestPatience
  /// The first live testing results (a build and a run) reaching the panel once it is enabled.
  let liveTestsReport = ms TestTimeouts.readyBudget
  /// Live testing results after an edit: a rebuild and a rerun.
  let liveTestsRerun = ms TestTimeouts.loadedSessionsReady
  /// A hot reload session coming up, with its first build.
  let hotReloadBuild = ms TestTimeouts.workerSessionReady
  /// The running app answering a request, once its host is up.
  let appAnswers = ms TestTimeouts.sseListen
  /// The app host coming up and serving its first request after the workflow starts.
  let appStarts = ms TestTimeouts.readyBudget
  /// An edit reaching the running app after a hot reload.
  let hotReloadApplies = ms TestTimeouts.saveVerdict
  /// How long the journey waits for the app to serve before it saves the file again, in case the
  /// watcher missed the first save.
  let resaveAfter = ms TestTimeouts.sseListen
  /// A generous margin over the stale budget, so a loaded runner's scheduler jitter never
  /// false-fails the "became stale" assertion.
  let staleDetected = ms TestTimeouts.sseListen
  /// The banner clearing again after the daemon restarts.
  let reconnected = ms TestTimeouts.patience

// ---- rebuild wait ----

/// Durations the rebuild readiness wait tests choose on purpose.
module RebuildWaitTimeouts =
  /// A deadline the test means to hit. Short, so the one test that waits it out stays quick.
  let deadlineToHit = System.TimeSpan.FromMilliseconds 150.

  /// A deadline the test must not hit. A passing test never waits this out, and reaching it
  /// would mean the wait under test never finished.
  let deadlineNotHit = System.TimeSpan.FromSeconds 60.

  /// How long a test lets the wait sit parked before it checks that nothing polled. The
  /// removed poll ran every 50 ms for the first second, so this window would have held
  /// several polls. Nothing can be awaited here, so it is a fixed settle.
  let quietWindow = System.TimeSpan.FromMilliseconds 300.

  /// How long a test lets a cancelled or superseded rebuild have to wrongly report. It has
  /// no signal to wait for, because the right outcome is that nothing happens.
  let nothingReportedWindow = System.TimeSpan.FromMilliseconds 200.

// ---- workspace hygiene ----

/// Ages the workspace hygiene tests choose on purpose. Each is a position on the retention ladder (what a cache
/// keeps, and what a worktree is), not a wait: nothing sleeps for them, the tests say how old a thing is.
module HygieneAges =
  /// Last touched a week ago: old enough to look abandoned, newer than any cache retention the tests use.
  let aWeek = System.TimeSpan.FromDays 7.

  /// A retention longer than `aWeek`, so a thing last touched a week ago is still inside it.
  let longerThanAWeek = System.TimeSpan.FromDays 30.

  /// A retention shorter than `aWeek`, so a thing last touched a week ago is past it.
  let shorterThanAWeek = System.TimeSpan.FromDays 3.

  /// The gate's own retention (14 days), for a test of a gate checkout whose owner is gone.
  let gateRetention = System.TimeSpan.FromDays 14.

  /// A retention of one day, for a temp run.
  let oneDay = System.TimeSpan.FromDays 1.

  /// Far past every retention in the product.
  let ancient = System.TimeSpan.FromDays 90.

  /// How far ahead of the real clock a scan believes it is, so a thing made just now counts as past its retention.
  let clockSkewPastRetention = System.TimeSpan.FromDays 30.

  /// Written to a quarter of an hour ago: a run that just crashed, inside every retention.
  let justNow = System.TimeSpan.FromMinutes 15.

// ---- machine tier and start escalation ----

/// Durations the machine tier and start escalation tests choose on purpose. They are the tests' own
/// numbers: the production ones depend on the tier the test process happens to run on.
module StartEscalationTimeouts =
  let private secs (n: float) = System.TimeSpan.FromSeconds n
  let private mins (n: float) = System.TimeSpan.FromMinutes n

  /// How long a first attempt may be silent in the cases below. It matches what a `Fast` machine is
  /// given, so the cases read like the field report.
  let silenceAllowance = secs 30.

  /// The longest a whole attempt may take in the cases below.
  let absoluteBound = mins 10.

  /// A healthy start that needs more than `silenceAllowance`: what the Phenom II X4 needed for a cold
  /// FSI host build (about 40 s, measured on 2026-10-01).
  let slowHealthyStart = secs 40.

  /// The shortest start the teeth property uses: 1.3 times the 30 s a first attempt is allowed, so it always needs a
  /// second attempt.
  let slowStartFloor = secs 39.

  /// A start that outlasts the first two attempts' allowances (30 s and 60 s) but not the third's.
  let verySlowStart = secs 100.

  /// A start on a quiet, fast machine.
  let quickStart = secs 5.

  /// How long a start has taken on this machine, as a history that has seen many.
  let learnedStart = secs 90.

  /// How much the learned starts vary.
  let learnedDeviation = secs 10.

  /// A made-up elapsed time for an estimate's first observation.
  let firstObservation = secs 12.

  /// A made-up later observation, longer than `firstObservation`.
  let laterObservation = secs 20.

  /// How long a real child process may stay silent in the tests that run the real `awaitWorkerPort`.
  /// Short on purpose: for those tests the allowance has to EXPIRE, so a long one only slows them.
  let shortSilence = System.TimeSpan.FromMilliseconds 400.

  /// The absolute bound given to those tests: far past the silence allowance, never reached.
  let shortAbsolute = System.TimeSpan.FromSeconds 30.

/// The nudge door's tests. The wait is made to EXPIRE in the test that uses it, so a long one only slows it.
module NudgeTimeouts =
  /// A file-lock wait short enough that the test of a refused wait (`FileBusy`) ends quickly.
  let nudgeShortLockWait = System.TimeSpan.FromMilliseconds 150.
