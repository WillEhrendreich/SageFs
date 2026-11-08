module SageFs.Tests.SettingsRetiredTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-retired-" + Guid.NewGuid().ToString("N").[..7])
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally try Directory.Delete(dir, true) with _ -> ()

let private repoRoot = RepoPaths.repoPathFull [||]

/// Every key a `settings.json` could hold for a row of the env-backed families on master 4285a037, before any wait
/// stopped being an override. Kept as a literal so that dropping a row can never silently stop a saved file loading.
let private keysBeforeTheRetirement : string list =
  [ "daemon.restartCount"; "daemon.supervised"; "daemon.updateCheckDisabled"
    "data.cohortLedgerRetentionDays"; "data.finishedWorktreeGraceDays"; "data.frictionMaxAgeDays"
    "data.frictionMaxAggregateVersions"; "data.frictionMaxRows"; "data.gateLogsKept"; "data.gatePassRecordsKept"
    "data.gateRetentionDays"; "data.hostCacheMaxAgeDays"; "data.tempRunMaxAgeDays"; "data.workerLogMaxAgeDays"
    "devreload.editor"; "hotreload.devReload"; "hotreload.metadataDelta"
    "limits.listMembersMax"; "limits.listReferencesMax"; "limits.referencesProjectCache"
    "limits.referencesResidentCeilingMb"; "limits.sessionManagerQueueCapacity"
    "waits.abandonedSessionHours"; "waits.agentCleanupSeconds"; "waits.ambientSdkProbeSeconds"
    "waits.appHostAppearSeconds"; "waits.appHostStartSeconds"; "waits.buildTimeoutMinutes"; "waits.cohortReaperSeconds"
    "waits.cohortSettleSeconds"; "waits.confirmAnswerSeconds"; "waits.daemonSessionsProbeSeconds"
    "waits.dashboardHeartbeatSeconds"; "waits.dashboardStaleAfterSeconds"; "waits.dataPruneMinutes"
    "waits.dotnetSdkQuerySeconds"; "waits.frictionPostSeconds"; "waits.fsiHostStartupSeconds"; "waits.gcDumpCaptureSeconds"
    "waits.gitQuickSeconds"; "waits.gitRebaseMinutes"; "waits.gitWorktreeAddMinutes"; "waits.hostBuildMinutes"
    "waits.hotReloadCompileBudgetMinutes"; "waits.hotReloadCompileQueueSeconds"; "waits.hygieneSweepMinutes"
    "waits.idleSessionMinutes"; "waits.jupyterWarmupSeconds"; "waits.machineTier"; "waits.manifestSaveSeconds"
    "waits.orphanSweepMinutes"; "waits.ownerPollSeconds"; "waits.patchConfirmSeconds"; "waits.rebuildReadySeconds"
    "waits.referencesCacheRetentionSeconds"; "waits.referencesCacheSweepSeconds"; "waits.referencesCheckSeconds"
    "waits.stopGracefulSeconds"; "waits.stopSessionSeconds"; "waits.supervisorWedgeSeconds"
    "waits.targetFrameworkEvaluationSeconds"; "waits.testBrowserWarmupSeconds"; "waits.testDaemonReadySeconds"
    "waits.testWebAppBuildSeconds"; "waits.testWebAppPortSeconds"; "waits.testWorkerRestartSeconds"
    "waits.updateCheckIntervalMinutes"; "waits.updateCheckTimeoutSeconds"; "waits.warmupInactivitySeconds"
    "waits.warmupMaxMinutes"; "waits.warmupReadyPollSeconds"; "waits.watcherSyncSeconds"; "waits.workerHttpReadSeconds"
    "waits.workerHttpRequestMinutes"; "waits.workerProbeIntervalSeconds"; "waits.workerProbeTimeoutSeconds"
    "waits.workerProxyWaitSeconds"; "waits.workerReadyPollSeconds"; "waits.workerStartupTimeoutMs" ]

let private rowKeys : Set<string> =
  EnvBackedRows.descriptors |> List.map (fun d -> d.Key) |> Set.ofList

let private rowVariables : Set<string> =
  EnvBackedRows.descriptors |> List.choose (fun d -> ExternalSource.variable d.External) |> Set.ofList

let private retiredKeys : Set<string> = RetiredSettings.all |> List.map (fun r -> r.Key) |> Set.ofList

/// A wait that is not worth an override is derived from the machine tier or fixed, and the setting that used to
/// override it is retired: a saved value is ignored with a note, a variable is no longer read.
[<Tests>]
let retiredTests =
  testList "Settings retired — fewer overrides, every saved key still loads" [

    testCase "WHY — every key the Settings page could save before is still understood, as a row or as a retired setting, so no saved file stops loading" <| fun _ ->
      let lost = keysBeforeTheRetirement |> List.filter (fun key -> not (rowKeys.Contains key) && not (retiredKeys.Contains key))
      lost |> Expect.isEmpty "each earlier key is a row or retired"

    testCase "WHY — a row and a retired setting never share a key or a variable, so one name never means two things" <| fun _ ->
      Set.intersect rowKeys retiredKeys |> Expect.isEmpty "no shared key"
      let retiredVariables = RetiredSettings.all |> List.map (fun r -> r.Variable) |> Set.ofList
      Set.intersect rowVariables retiredVariables |> Expect.isEmpty "no shared variable"

    testCase "WHY — a settings file holding every earlier key resolves, and each retired key is ignored with a note that names it" <| fun _ ->
      withTempDir (fun dir ->
        for key in keysBeforeTheRetirement do
          SettingsStore.setKey (SettingsStore.globalPath dir) key "7" |> Expect.isOk (sprintf "%s writes" key)
        let resolved = SettingsBootstrap.resolveSavedWith (fun _ -> EnvUnset) dir EnvBackedRows.all
        let notes =
          resolved.Outcomes
          |> List.choose (function ImportOutcome.IgnoredRetired(key, note) -> Some (key, note) | _ -> None)
        notes |> List.map fst |> Set.ofList |> Expect.equal "every retired key has its note" retiredKeys
        for key, note in notes do
          let retired = RetiredSettings.all |> List.find (fun r -> r.Key = key)
          note |> Expect.stringContains (sprintf "%s names its variable" key) retired.Variable
        resolved.Outcomes
        |> List.exists (function ImportOutcome.IgnoredUnknown _ -> true | _ -> false)
        |> Expect.isFalse "no earlier key is unknown"
        retiredKeys
        |> Set.exists (fun key -> SettingsSnapshot.toPairs resolved.Snapshot |> List.exists (fun (_, text) -> text = key))
        |> Expect.isFalse "a retired key puts nothing in the snapshot")

    testCase "WHY — a key in the env-backed families that no row owns is ignored with a note, and a key of another family is left alone" <| fun _ ->
      withTempDir (fun dir ->
        SettingsStore.setKey (SettingsStore.globalPath dir) "waits.fooSeconds" "7" |> Expect.isOk "writes"
        SettingsStore.setKey (SettingsStore.globalPath dir) "daemon.mcpPort" "4000" |> Expect.isOk "writes"
        let resolved = SettingsBootstrap.resolveSavedWith (fun _ -> EnvUnset) dir EnvBackedRows.all
        resolved.Outcomes |> Expect.equal "only the unknown key of the family is reported" [ ImportOutcome.IgnoredUnknown "waits.fooSeconds" ])

    testCase "WHY — a retired variable a person still has set is named, with what decides that wait now, so nothing is ignored silently" <| fun _ ->
      let some = RetiredSettings.all |> List.head
      let found = RetiredSettings.inEnvironment (fun name -> match name = some.Variable with true -> EnvSet "5" | false -> EnvUnset)
      found |> Expect.equal "exactly the one that is set" [ some ]
      RetiredSettings.describe some |> Expect.stringContains "names the variable" some.Variable
      RetiredSettings.inEnvironment (fun _ -> EnvUnset) |> Expect.isEmpty "none set, none named"

    testCase "WHY — no retired variable is read any more, so a derived wait is not quietly overridable" <| fun _ ->
      let text =
        [ "Timeouts.fs"; "SageFsConfig.fs" ]
        |> List.collect (fun file -> File.ReadAllLines(Path.Combine(repoRoot, "SageFs.Core", file)) |> Array.toList)
        |> List.filter (fun line -> not (line.TrimStart().StartsWith "//"))
        |> String.concat "\n"
      let stillRead = RetiredSettings.all |> List.filter (fun r -> text.Contains (sprintf "\"%s\"" r.Variable)) |> List.map (fun r -> r.Variable)
      stillRead |> Expect.isEmpty "Timeouts.fs and SageFsConfig.fs do not read a retired variable"

    testCase "WHY — every retired setting says why, in words that name what decides the wait now" <| fun _ ->
      for r in RetiredSettings.all do
        let text = RetiredSettings.describe r
        text |> Expect.stringContains (sprintf "%s names its key" r.Key) r.Key
        (text.Length > r.Key.Length + r.Variable.Length + 40) |> Expect.isTrue (sprintf "%s gives a reason, got: %s" r.Key text)

    testCase "WHY — the env-backed rows are the short list a person plausibly sets, not one per wait" <| fun _ ->
      EnvBackedRows.descriptors |> List.length |> Expect.equal "rows (79 before: 71 editable and 8 read-only)" 45

    testCase "WHY — the Settings page draws one row per setting a person plausibly sets, and the count is the evidence for the short list" <| fun _ ->
      SettingsCatalog.all |> List.length |> Expect.equal "Core rows (89 before: 5 pilots, 79 env-backed, 5 others; master added hygiene.autoTidy after that count, and the starter tool list switch is the 57th)" 57
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
