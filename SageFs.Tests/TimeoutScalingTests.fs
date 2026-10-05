module SageFs.Tests.TimeoutScalingTests

/// Every duration in `SageFs.Core/Timeouts.fs` is one of two kinds, and which is written where it is
/// declared. A WAIT FOR THE MACHINE (a start, a build, a warm-up, a ready state, a restart, an exit)
/// takes longer on a slower machine, so it is scaled by the tier (`forMachine`, `envOrDefaultMachine`,
/// `envOrDefaultMachineMinutes`). Anything else stays as written, and has a reason.
///
/// This is the ratchet: a duration that is in neither table fails, so a new wait is decided on the
/// day it is added instead of discovered on someone's old laptop. There is no table for "a wait for
/// the machine that does not scale", because such a thing is the bug this work fixed.
open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs

/// Why a duration is NOT scaled by the machine. A closed set, and none of them is "a wait for the machine".
[<RequireQualifiedAccess>]
type FixedBecause =
  /// A cadence: how often something is looked at.
  | Poll
  /// A pause that lets a burst of events settle into one, tuned to typing and saving.
  | Debounce
  /// A person's own pace: how long a reader tolerates, a timer a person watches.
  | Human
  /// Who is around, and for how long something is kept or leased.
  | Presence
  /// A retry spacing, or the advice a refused caller is given about when to ask again.
  | Backoff
  /// A budget against a remote server, which the machine does not decide.
  | Network
  /// A constant both ends of a protocol agree on, or a keep-alive for a stream.
  | Protocol
  /// A threshold about the user's code or the product's judgement, not about how long a start takes.
  | Threshold
  /// Read only so an old setting keeps parsing, or no time at all.
  | Inert
  /// A bound before a kill: when it is spent the caller kills the process, so a longer bound only delays the
  /// kill. Measured: `stop_session` costs 5.1 to 5.7 s on every tier, so the bound is always spent in full.
  | BoundBeforeKill
  /// A deadline a test harness picks, not a product wait.
  | TestHarness

let private repoRoot = RepoPaths.repoPathFull [||]
let private lines = File.ReadAllLines(Path.Combine(repoRoot, "SageFs.Core", "Timeouts.fs"))

let private fixedTable : (string * FixedBecause) list =
  let all (reason: FixedBecause) (names: string list) = names |> List.map (fun n -> n, reason)
  all FixedBecause.Poll
    [ "dllLockRetryDelay"; "hostBuildLockPoll"; "cohortLandingPoll"; "workerReadyPoll"; "ownerLivenessPoll"
      "sessionWatcherSyncInterval"; "sessionStatusPoll"; "stdioBridgeProbeInterval"; "jupyterReceivePoll"
      "liveTestTickActive"; "liveTestTickIdle"; "dashboardPollInterval"; "sseEventInterval"; "workerReloadRelayRetry"
      "workerProxyFirstDelay"; "workerHealthProbeInterval"; "watchdogInterval"; "supervisorCheckInterval"
      "ttlCheckFloor"; "ttlCheckCeiling"; "agentActivityCleanupInterval"; "cohortReaperInterval"; "debugAttachLook"
      "orphanTempDirSweepInterval"; "logFlushInterval"; "evalHeartbeatInterval"; "manifestSaveInterval"
      "compileTimerRedraw"; "stdioFlush"; "scheduledGraceDelay" ]
  @ all FixedBecause.Debounce
      [ "liveTestTreeSitterDebounce"; "liveTestFcsDebounce"; "liveTestFcsDebounceMax"; "liveTestWatcherDebounce"
        "fileWatchDebounce"; "doubleCompileGuard"; "liveTestConfirmationQuiet"; "testSseThrottle"
        "legacyStateStreamCoalesce" ]
  @ all FixedBecause.Human
      [ "autoReloadThreshold"; "longCompileWarning"; "reloadConnectTimeout"; "reloadCountResetWindow"
        "hotLoopWindow"; "patchConfirmation" ]
  @ all FixedBecause.Presence
      [ "cohortLeaseWindow"; "cohortSettledRetention"; "agentPresenceEviction"; "agentActivityFresh"
        "memoryIdleShedAfter"; "leaseTtlRunApp"; "nestedCheckoutDaemonTtl"; "idleSessionThreshold"
        "frictionResetThrashWindow"; "frictionHardResetAfterCreateWindow"; "leaseAskStaleAfter"
        "capabilityDefaultLifetime"; "capabilityMaxLifetime" ]
  @ all FixedBecause.Backoff
      [ "restartBaseBackoff"; "retryBaseDelay"; "restartMaxBackoff"; "restartCountResetWindow"
        "leaseRetryAfterNormal"; "leaseRetryAfterTight"; "leaseRetryAfterCritical"; "leaseMinRetryAfter" ]
  @ all FixedBecause.Network [ "updateCheckFetch"; "updateCheckInterval"; "frictionReportPost" ]
  @ all FixedBecause.Protocol
      [ "statusWaitCap"; "sseKeepAlive"; "debugContinuePark"; "appChangeAwait"; "dashboardHeartbeat"
        "dashboardStaleAfter"; "dashboardProbe"; "dashboardWorkerDataTtl"; "legacyStateStreamKeepAlive"; "reloadStreamHeartbeat"
        "processStartTimeTolerance"; "fileWriteTimeTolerance" ]
  @ all FixedBecause.Threshold [ "impactP95Acceptable"; "impactP95Investigate"; "memberEvaluationGrace"; "scaledWaitCeiling"; "nudgeFileLock" ]
  @ all FixedBecause.BoundBeforeKill
      [ "processNormalExit"; "processKillVerify"; "stderrDrainGrace"; "fsiHostExitReport"; "fsiHostShutdownGrace"
        "stopKillExit"; "workerShutdownDelay" ]
  @ all FixedBecause.Inert [ "legacyWorkerStartup"; "notRun" ]
  @ all FixedBecause.TestHarness
      [ "integrationDaemonReady"; "integrationWorkerRestart"; "browserJourneyWarmup"; "webAppHotReloadBuild"
        "webAppPortReady" ]

/// The waits for the machine that are declared with a scaling helper.
let private machineScaled : Set<string> =
  set [ "warmupAbsoluteMax"; "warmupInactivityLimit"; "softResetCancellation"; "initSessionCancellation"
        "fsiHostStartup"; "dotnetSdkQuery"; "ambientSdkProbe"; "targetFrameworkEvaluation"; "gcDumpCapture"
        "fsiAvailabilityProbe"; "stopGracefulExit"; "workerHttpRead"; "workerHttpRequest"
        "gateStatusProbe"; "healthCheck"; "shutdownHttpClient"; "daemonSessionsProbe"; "perTestTimeoutFallback"
        "liveTestConfirmationDeadline"; "liveValueBindingBudget"; "memberEvaluationDeadline"
        "liveTestWatcherShutdown"; "rebuildReadyWait"; "buildCompletion"; "hostBuildRun"; "gitQuick"; "gitRebase"
        "gitWorktreeAdd"; "cohortIntegrationSettle"; "testRunAwaitSlack"; "leaseTtlSessionCreate"
        "leaseTtlTestSuite"; "frictionSlowFirstSuccess"; "compileQueueWait"; "compileBudget"; "reloadPlanningCheck"
        "appHostAppearGrace"; "appHostStart"; "appHostStop"; "appEntryFinishGrace"; "appRunnerShutdown"
        "debugHold"; "debugAttachGrace"; "restartStartupCrashWindow"; "watchdogGracePeriod"; "workerHealthProbeTimeout"
        "workerEndpointFetch"; "workerWarmupContextFetch"; "outputCommitWait"; "gracefulShutdownWatchdog"
        "shutdownManifestCommit"; "testCycleTimerStop"; "cacheSaveTimerStop"; "workerHttpServerStop"
        "startupDelay"; "stopSessionMailboxTimeout"; "supervisorWedgeAfter"
        "sessionDispose"; "warmupReadyPollMax"; "workerProxyRegister"; "daemonStartWait" ]

/// Declared as another wait for the machine, so they scale through it.
let private derivedFromMachine : (string * string) list =
  [ "hostBuildLockWait", "hostBuildRun"
    "workflowSwitchRequest", "buildCompletion"
    "leaseTtlRebuild", "buildCompletion"
    "leaseTtlFullBuild", "buildCompletion"
    "callerCheck", "reloadPlanningCheck" ]

/// Declared as another fixed duration, or as no time at all, so there is no TimeSpan on their own line.
let private derivedFromFixed : Set<string> = set [ "cohortSettledRetention"; "notRun" ]

let private fixedNames : Set<string> = fixedTable |> List.map fst |> Set.ofList

let private letName = Regex(@"^  let (?:mutable )?(?:private )?(\w+)\b", RegexOptions.Compiled)
let private durationLine = Regex(@"TimeSpan\.From|envOrDefault|envOrDefaultMachine", RegexOptions.Compiled)

let private moduleStart = lines |> Array.findIndex (fun l -> l.StartsWith "module Timeouts")
let private moduleEnd = lines |> Array.findIndex (fun l -> l.StartsWith "/// How long SageFs keeps the local data")

/// The declaration text of a `let`: its own line, and the next when the value is on the line after a bare `=`.
let private declaration (index: int) : string =
  let line = lines.[index]
  match line.TrimEnd().EndsWith "=" with
  | true -> line + " " + lines.[index + 1]
  | false -> line

/// Every public duration constant in the Timeouts module, by name, with its declaration text.
let private declared : Map<string, string> =
  [ for i in moduleStart .. moduleEnd - 1 do
      let m = letName.Match lines.[i]
      let isPrivate = lines.[i].StartsWith "  let private" || lines.[i].StartsWith "  let mutable private"
      match m.Success && not isPrivate with
      | false -> ()
      | true ->
        let name = m.Groups.[1].Value
        let text = declaration i
        let isDuration = durationLine.IsMatch text || (derivedFromMachine |> List.exists (fun (n, _) -> n = name)) || derivedFromFixed.Contains name
        match isDuration with
        | true -> yield name, text
        | false -> () ]
  |> Map.ofList

let private usesScaling (text: string) =
  text.Contains "forMachine" || text.Contains "envOrDefaultMachine"

[<Tests>]
let tests =
  testList "Timeout scaling by machine tier" [

    testCase "WHY — the parse found the durations, so the comparisons below cannot pass by finding nothing" <| fun _ ->
      (declared |> Map.count > 100) |> Expect.isTrue "Timeouts.fs yields over a hundred duration constants"

    testCase "WHY — every duration is decided: it is a wait for the machine or it is fixed with a reason, and none is in both" <| fun _ ->
      let both = Set.intersect machineScaled fixedNames |> Set.toList
      both |> Expect.equal "no name is in both tables" []
      let known = Set.unionMany [ machineScaled; fixedNames; derivedFromMachine |> List.map fst |> Set.ofList ]
      let undecided = declared |> Map.toList |> List.map fst |> List.filter (fun n -> not (known.Contains n))
      undecided |> Expect.equal "every duration in Timeouts.fs is in a table (add the new one, with its reason, to the right one)" []
      let gone = known |> Set.toList |> List.filter (fun n -> not (declared.ContainsKey n))
      gone |> Expect.equal "every name in a table is still declared (remove the deleted one)" []

    testCase "WHY — a wait for the machine is declared with a scaling helper, so it cannot be left at its Fast value by accident" <| fun _ ->
      let unscaled = machineScaled |> Set.toList |> List.filter (fun n -> not (usesScaling declared.[n]))
      unscaled |> Expect.equal "each wait for the machine uses forMachine, envOrDefaultMachine or envOrDefaultMachineMinutes" []

    testCase "WHY — a fixed duration never uses a scaling helper, so the table and the code say the same thing" <| fun _ ->
      let scaled = fixedNames |> Set.toList |> List.filter (fun n -> usesScaling declared.[n])
      scaled |> Expect.equal "a fixed duration is declared with a bare TimeSpan or envOrDefault" []

    testCase "WHY — a derived wait is declared from a wait for the machine, so it scales through it" <| fun _ ->
      for name, source in derivedFromMachine do
        machineScaled.Contains source |> Expect.isTrue (sprintf "%s names %s, a wait for the machine" name source)
        declared.[name].Contains source |> Expect.isTrue (sprintf "%s is declared from %s" name source)

    testCase "WHY — the number of waits for the machine only goes up when a table says so (a ratchet on the count, with the reason a wait is fixed in the table above)" <| fun _ ->
      machineScaled |> Set.count |> Expect.equal "waits for the machine (60 + debugAttachGrace: a debugger process attaching to the host takes longer on a slower machine)" 61
      fixedTable |> List.length |> Expect.equal "fixed durations (97 + capabilityDefaultLifetime and capabilityMaxLifetime, which are presence: a run's token is trusted for a stated time, + debugAttachLook, a poll: the runtime raises no attach event)" 100

    testCase "WHY — each machine constant in the running process equals its written value scaled for the process's tier, so the wiring is real and not only the text" <| fun _ ->
      let timeouts = typeof<ValidTimeout>.Assembly.GetType "SageFs.Timeouts"
      let numeric = Regex(@"(?:envOrDefaultMachine|envOrDefaultMachineMinutes)\s+""(SAGEFS_[A-Z0-9_]+)""\s+([0-9_.]+)|forMachine \(TimeSpan\.From(Seconds|Minutes|Milliseconds)\(([0-9.]+)\)\)")
      let checkedNames =
        [ for name in machineScaled do
            let text = declared.[name]
            let m = numeric.Match text
            match m.Success with
            | false -> failtestf "%s: could not read its written value from: %s" name text
            | true ->
              let variable = m.Groups.[1].Value
              match variable <> "" && not (isNull (Environment.GetEnvironmentVariable variable)) with
              | true -> () // the person who set the variable chose the number
              | false ->
                let baseline =
                  match m.Groups.[1].Success with
                  | true ->
                    let value = Double.Parse(m.Groups.[2].Value.Replace("_", ""), Globalization.CultureInfo.InvariantCulture)
                    (match text.Contains "envOrDefaultMachineMinutes" with
                     | true -> TimeSpan.FromMinutes value
                     | false -> TimeSpan.FromSeconds value)
                  | false ->
                    let value = Double.Parse(m.Groups.[4].Value, Globalization.CultureInfo.InvariantCulture)
                    (match m.Groups.[3].Value with
                     | "Minutes" -> TimeSpan.FromMinutes value
                     | "Milliseconds" -> TimeSpan.FromMilliseconds value
                     | _ -> TimeSpan.FromSeconds value)
                let property = timeouts.GetProperty(name, BindingFlags.Public ||| BindingFlags.Static)
                let actual = property.GetValue null :?> TimeSpan
                let expected = MachineTier.scaleWait Timeouts.scaledWaitCeiling Timeouts.machineTier baseline
                match actual = expected with
                | true -> yield name
                | false -> failtestf "%s is %A, written as %A, so on tier %A it should be %A" name actual baseline Timeouts.machineTier expected ]
      (checkedNames |> List.length > 40) |> Expect.isTrue "most waits for the machine were compared"

    testCase "WHY — the tier a process runs on is the one its environment names, so a worker told a tier by the daemon scales the same way" <| fun _ ->
      Timeouts.machineTier |> Expect.equal "the process's tier" (MachineTier.current ())
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
