/// The exact JSON text the SageFs.Core files that used to build their own
/// JsonSerializerOptions write, pinned before they moved onto `SageFs.Json`. A value that
/// has to be the same text on every runtime is pinned here, and these run in both the net11
/// and the net10 default tier.
module SageFs.Tests.JsonCoreFilesTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

let private lf (text: string) = text.Replace("\r\n", "\n")

let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-json-core-%s" (Guid.NewGuid().ToString "N"))
  Directory.CreateDirectory dir |> ignore
  try
    run dir
  finally
    Directory.Delete(dir, true)

let private clock = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let private daemonInfo (ownerPid: int option) (ownerStart: int64 option) : DaemonOwnership.DaemonInfoFile =
  { Pid = 4242
    StartTime = clock
    OwnerPid = ownerPid
    OwnerStart = ownerStart
    McpPort = 37749
    DashboardPort = 37750
    DataDir = "/data/dir" }

let private sessionInfo (branchDir: string) : SessionInfo =
  let id =
    match SessionId.validate "abcd1234" with
    | Ok id -> id
    | Error reason -> failwith reason
  { Id = id
    Name = Some "ignored"
    Projects = [ "Foo.fsproj" ]
    WorkingDirectory = branchDir
    SolutionRoot = None
    CreatedAt = clock
    LastActivity = clock
    Status = SessionLifecycleStatus.Ready { Pid = 1; Port = None }
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet }

let private expectedCacheLines =
  [ "{"
    "  \"fingerprint\": {"
    "    \"schemaVersion\": 5,"
    "    \"autoOpenNamespaces\": true,"
    "    \"fsiArgs\": ["
    "      \"--multiemit-\""
    "    ],"
    "    \"startupFiles\": [],"
    "    \"sourceFiles\": ["
    "      {"
    "        \"path\": \"/src/A.fs\","
    "        \"exists\": true,"
    "        \"length\": 10,"
    "        \"lastWriteTimeUtcTicks\": 5"
    "      }"
    "    ],"
    "    \"assemblyFiles\": [],"
    "    \"projectFiles\": ["
    "      {"
    "        \"path\": \"/src/A.fsproj\","
    "        \"exists\": true,"
    "        \"contentHash\": \"abc\""
    "      }"
    "    ]"
    "  },"
    "  \"sourceFilesScanned\": 1,"
    "  \"assembliesLoaded\": ["
    "    {"
    "      \"name\": \"A\","
    "      \"path\": \"/bin/A.dll\","
    "      \"namespaceCount\": 2,"
    "      \"moduleCount\": 1"
    "    }"
    "  ],"
    "  \"namesToOpen\": ["
    "    {"
    "      \"name\": \"A.Domain\","
    "      \"kind\": {"
    "        \"Case\": \"Module\""
    "      }"
    "    }"
    "  ],"
    "  \"projectFileNames\": ["
    "    \"A.fsproj\""
    "  ],"
    "  \"discoveryWarnings\": ["
    "    \"a warning\""
    "  ]"
    "}" ]

[<Tests>]
let tests =
  testList "Core JSON files" [

    testList "WorkerProtocol.Serialization (the daemon-to-worker wire)" [

      testCase "WHY — a union with no fields is written as an adjacent tag with camelCase keys around it, the form the worker reads" <| fun _ ->
        WorkerProtocol.Serialization.serialize WorkerResponse.WorkerReady
        |> Expect.equal "WorkerReady" """{"type":"WorkerReady"}"""

      testCase "WHY — a union with fields carries them in `value`, so daemon and worker agree on where a field is" <| fun _ ->
        WorkerProtocol.Serialization.serialize (WorkerResponse.CompletionResult("r1", [ "a"; "b" ]))
        |> Expect.equal "CompletionResult" """{"type":"CompletionResult","value":["r1",["a","b"]]}"""

      testCase "WHY — an anonymous request body gets camelCase keys" <| fun _ ->
        WorkerProtocol.Serialization.serialize {| code = "1+1"; replyId = "r1" |}
        |> Expect.equal "request body" "{\"code\":\"1\\u002B1\",\"replyId\":\"r1\"}"

      testCase "WHY — on the worker wire an option is an adjacent-tagged union, None is null, and a map is an object" <| fun _ ->
        WorkerProtocol.Serialization.serialize {| present = Some 1; absent = (None: int option); meta = Map.ofList [ "k", "v" ] |}
        |> Expect.equal "option and map" """{"absent":null,"meta":{"k":"v"},"present":{"type":"Some","value":[1]}}"""

      testCase "WHY — a value written by serialize is read back equal by deserialize" <| fun _ ->
        let original = WorkerResponse.EvalResult("r1", Ok "x", [], Map.ofList [ "k", "v" ])
        WorkerProtocol.Serialization.serialize original
        |> WorkerProtocol.Serialization.tryDeserialize<WorkerResponse>
        |> Expect.equal "round trip" (Ok original)

      testCase "WHY — wire text that is not JSON, or not the shape asked for, is an Error that says why" <| fun _ ->
        [ WorkerProtocol.Serialization.tryDeserialize<WorkerResponse> "{ not json ]"
          WorkerProtocol.Serialization.tryDeserialize<WorkerResponse> "{\"type\":\"NoSuchCase\"}"
          WorkerProtocol.Serialization.tryDeserialize<WorkerResponse> "null" ]
        |> List.map (function
          | Ok _ -> "read"
          | Error (SageFsError.JsonParseError(_, reason)) when not (String.IsNullOrWhiteSpace reason) -> "error with a reason"
          | Error _ -> "some other error")
        |> Expect.equal "each bad payload is an Error with a reason" [ "error with a reason"; "error with a reason"; "error with a reason" ]

      testCase "WHY — deserialize, kept for callers that have not moved to tryDeserialize, raises a JsonException carrying the reason" <| fun _ ->
        Expect.throwsT<System.Text.Json.JsonException>
          "garbage raises"
          (fun () -> WorkerProtocol.Serialization.deserialize<WorkerResponse> "{ not json ]" |> ignore)
    ]

    testList "DaemonInfoFile (daemon-info.json)" [

      testCase "WHY — the file is indented, keys as written, an absent owner is null and a present one is its number" <| fun _ ->
        withTempDir (fun dir ->
          DaemonOwnership.DaemonInfoFile.write dir (daemonInfo (Some 99) None)
          File.ReadAllText(DaemonOwnership.DaemonInfoFile.path dir)
          |> lf
          |> Expect.equal "file text" "{\n  \"Pid\": 4242,\n  \"StartTime\": \"2026-01-01T00:00:00Z\",\n  \"OwnerPid\": 99,\n  \"OwnerStart\": null,\n  \"McpPort\": 37749,\n  \"DashboardPort\": 37750,\n  \"DataDir\": \"/data/dir\"\n}")

      testCase "WHY — a written file is read back equal, owner present and absent alike" <| fun _ ->
        withTempDir (fun dir ->
          for info in [ daemonInfo (Some 99) (Some 123456789L); daemonInfo None None ] do
            DaemonOwnership.DaemonInfoFile.write dir info
            DaemonOwnership.DaemonInfoFile.tryRead dir
            |> Expect.equal "round trip" (Some info))

      testCase "WHY — a file that is not JSON reads as no daemon, so a sweeper skips it instead of failing" <| fun _ ->
        withTempDir (fun dir ->
          File.WriteAllText(DaemonOwnership.DaemonInfoFile.path dir, "{ not json ]")
          DaemonOwnership.DaemonInfoFile.tryRead dir
          |> Expect.isNone "unreadable file")

      testCase "WHY — a registry entry is the same text as the primary file and is found by the enumeration" <| fun _ ->
        withTempDir (fun dir ->
          let info = daemonInfo (Some 99) None
          DaemonOwnership.registerSpawned dir info
          let entry = Path.Combine(DaemonOwnership.registryDir dir, "4242.json")
          File.ReadAllText entry |> lf |> Expect.stringContains "registry entry text" "\"OwnerPid\": 99"
          DaemonOwnership.enumerateKnownDaemonInfos dir
          |> List.map snd
          |> Expect.equal "enumerated" [ info ])
    ]

    testList "SettingsStore (settings.json)" [

      testCase "WHY — a layer file is an indented flat object, keys exactly as given" <| fun _ ->
        withTempDir (fun dir ->
          let path = SettingsStore.globalPath dir
          SettingsStore.setKey path "b.key" "two" |> Expect.isOk "set b"
          SettingsStore.setKey path "a.key" "one" |> Expect.isOk "set a"
          File.ReadAllText path
          |> lf
          |> Expect.equal "layer text" "{\n  \"a.key\": \"one\",\n  \"b.key\": \"two\"\n}")

      testCase "WHY — a layer file written by an earlier version reads back as the same map" <| fun _ ->
        withTempDir (fun dir ->
          let path = SettingsStore.globalPath dir
          File.WriteAllText(path, "{\n  \"a.key\": \"one\",\n  \"b.key\": \"two\"\n}")
          SettingsStore.readLayer path
          |> Expect.equal "layer" (Map.ofList [ "a.key", "one"; "b.key", "two" ]))

      testCase "WHY — a layer file that is JSON but not an object of strings reads as empty and setKey repairs it" <| fun _ ->
        withTempDir (fun dir ->
          let path = SettingsStore.globalPath dir
          File.WriteAllText(path, "[1, 2, 3]")
          SettingsStore.readLayer path |> Expect.isEmpty "wrong shape reads as empty"
          SettingsStore.setKey path "a.key" "one" |> Expect.isOk "set"
          SettingsStore.readLayer path |> Expect.equal "repaired" (Map.ofList [ "a.key", "one" ]))
    ]

    testList "WarmupReplayCache (warmup-replay-cache.json)" [

      let plan : WarmupReplayCache.ReplayPlan =
        { Fingerprint =
            { SchemaVersion = WarmupReplayCache.SchemaVersion
              AutoOpenNamespaces = true
              FsiArgs = [ "--multiemit-" ]
              StartupFiles = []
              SourceFiles = [ { Path = "/src/A.fs"; Exists = true; Length = 10L; LastWriteTimeUtcTicks = 5L } ]
              AssemblyFiles = []
              ProjectFiles = [ { Path = "/src/A.fsproj"; Exists = true; ContentHash = "abc" } ] }
          SourceFilesScanned = 1
          AssembliesLoaded = [ { Name = "A"; Path = "/bin/A.dll"; NamespaceCount = 2; ModuleCount = 1 } ]
          NamesToOpen = [ { Name = "A.Domain"; Kind = WarmUp.OpenableKind.Module } ]
          ProjectFileNames = [ "A.fsproj" ]
          DiscoveryWarnings = [ "a warning" ] }

      testCase "WHY — the cache is indented with camelCase keys and a union written by the F# converter" <| fun _ ->
        withTempDir (fun dir ->
          let path = Path.Combine(dir, "cache.json")
          WarmupReplayCache.save path plan
          File.ReadAllText path |> lf |> Expect.equal "cache text" (String.concat "\n" expectedCacheLines))

      testCase "WHY — a saved plan loads back equal" <| fun _ ->
        withTempDir (fun dir ->
          let path = Path.Combine(dir, "cache.json")
          WarmupReplayCache.save path plan
          WarmupReplayCache.tryLoad path |> Expect.equal "round trip" (Some plan))

      testCase "WHY — a cache file that is not JSON is a miss, so warmup rediscovers" <| fun _ ->
        withTempDir (fun dir ->
          let path = Path.Combine(dir, "cache.json")
          File.WriteAllText(path, "{ not json ]")
          WarmupReplayCache.tryLoad path |> Expect.isNone "unreadable cache")
    ]

    testList "SessionOperations.sessionsToJson" [

      testCase "WHY — the resource text is camelCase, an absent worktree branch is null, dates are ISO" <| fun _ ->
        let opts = Json.optionsOf Json.camelCase
        SessionOperations.sessionsToJson opts [ sessionInfo "/repo/checkout" ]
        |> Expect.equal
          "sessions text"
          """{"sessions":[{"createdAt":"2026-01-01T00:00:00Z","id":"abcd1234","lastActivity":"2026-01-01T00:00:00Z","name":"checkout","projects":["Foo.fsproj"],"status":"Ready","workflow":"REPL","workingDirectory":"/repo/checkout","worktreeBranch":null}]}"""
    ]

    testList "DevReload compile-failure payload" [

      let present : DevReload.DevReloadDiagnostic =
        { File = "App.fs"; Line = 10; EndLine = 10; Column = 5; EndColumn = 15
          Severity = "error"; DiagCode = Some "FS0001"; Message = "Type <mismatch>"
          SourceContext = Some [| "let a = 1"; "let b = \"x\"" |]; SourceContextStartLine = Some 9 }

      let absent : DevReload.DevReloadDiagnostic =
        { present with DiagCode = None; SourceContext = None; SourceContextStartLine = None }

      testCase "WHY — a present optional field is its bare value and an absent one is null, which is what devreload.js reads" <| fun _ ->
        DevReload.DevReloadEvent.payloadJson (DevReload.CompilationFailed("2 errors", DevReload.ReloadReport.none, [ present; absent ]))
        |> Expect.equal
          "failed payload"
          (String.concat
            ""
            [ "{\"type\":\"failed\",\"error\":\"2 errors\",\"outcome\":\"\",\"patched\":0,\"considered\":0,"
              "\"message\":\"\",\"suggestedAction\":\"\",\"reasons\":[],\"diagnostics\":["
              "{\"File\":\"App.fs\",\"Line\":10,\"EndLine\":10,\"Column\":5,\"EndColumn\":15,\"Severity\":\"error\","
              "\"DiagCode\":\"FS0001\",\"Message\":\"Type \\u003Cmismatch\\u003E\","
              "\"SourceContext\":[\"let a = 1\",\"let b = \\u0022x\\u0022\"],\"SourceContextStartLine\":9},"
              "{\"File\":\"App.fs\",\"Line\":10,\"EndLine\":10,\"Column\":5,\"EndColumn\":15,\"Severity\":\"error\","
              "\"DiagCode\":null,\"Message\":\"Type \\u003Cmismatch\\u003E\","
              "\"SourceContext\":null,\"SourceContextStartLine\":null}]}" ])
    ]

    testList "Features.KeptState JSON" [

      testCase "WHY — a reset outcome is one object whose strings are escaped the way the serializer escapes them" <| fun _ ->
        Features.KeptState.ResetOutcome.toJson (Features.KeptState.ResetOutcome.Reset("a\"b", "<1> é"))
        |> Expect.equal
          "reset outcome"
          "{\"outcome\":\"Reset\",\"binding\":\"a\\u0022b\",\"message\":\"Reset \\u0027a\\u0022b\\u0027: it\\u0027s \\u003C1\\u003E \\u00E9 now.\"}"

      testCase "WHY — the pending list is the kept values with the same field names the save's own report uses" <| fun _ ->
        Features.KeptState.pendingJson
          [ ({ Binding = "x"; KeptValue = "1"; NewInitializer = "2" } : Features.ReloadOutcome.KeptValue) ]
        |> Expect.equal "pending list" """[{"binding":"x","keptValue":"1","newInitializer":"2"}]"""
    ]

    testList "SseWriter payloads" [

      let camel = Json.optionsOf Json.camelCase
      let asWritten = Json.optionsOf Json.standard

      testCase "WHY — warmup_progress is the options' key style and nothing else" <| fun _ ->
        [ SseWriter.formatWarmupProgressEvent camel (Some "s1") 1 5 "Loading"
          SseWriter.formatWarmupProgressEvent asWritten None 1 5 "Loading" ]
        |> List.map lf
        |> Expect.equal
          "warmup_progress"
          [ "event: warmup_progress\ndata: {\"SessionId\":\"s1\",\"message\":\"Loading\",\"phase\":\"creating_fsi\",\"progress\":0.2,\"step\":1,\"total\":5}\n\n"
            "event: warmup_progress\ndata: {\"Message\":\"Loading\",\"Phase\":\"creating_fsi\",\"Progress\":0.2,\"Step\":1,\"Total\":5}\n\n" ]

      testCase "WHY — eval_started writes the session id into the payload" <| fun _ ->
        SseWriter.formatEvalStartedEvent asWritten (Some "s1") "/a.fs" 3
        |> lf
        |> Expect.equal "eval_started" "event: eval_started\ndata: {\"SessionId\":\"s1\",\"blockStartLine\":3,\"filePath\":\"/a.fs\"}\n\n"

      testCase "WHY — a union inside a payload is written by the F# converter" <| fun _ ->
        let summary : Features.LiveTesting.TestSummary =
          { Total = 1; Passed = 1; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
        let passedIn = TestTimeouts.testElapsed
        let entry : Features.LiveTesting.TestStatusEntry =
          { TestId = Features.LiveTesting.TestId.TestId "t1"
            DisplayName = "t 1"
            FullName = "M.t1"
            Origin = Features.LiveTesting.TestOrigin.ReflectionOnly
            Framework = Features.LiveTesting.TestFramework.Expecto
            Category = Features.LiveTesting.TestCategory.Unit
            CurrentPolicy = Features.LiveTesting.RunPolicy.OnEveryChange
            Status = Features.LiveTesting.TestRunStatus.Passed passedIn
            PreviousStatus = Features.LiveTesting.TestRunStatus.Passed passedIn; Provenance = Features.LiveTesting.ResultProvenance.Compiled }
        let payload : Features.LiveTesting.TestResultsBatchPayload =
          { Generation = Features.LiveTesting.RunGeneration 1
            Freshness = Features.LiveTesting.ResultFreshness.Fresh
            Completion = Features.LiveTesting.BatchCompletion.Complete (1, 1)
            Entries = [| entry |]
            Summary = summary
            LastDecision = None }
        [ SseWriter.formatTestResultsBatchEvent camel None payload
          SseWriter.formatTestResultsBatchEvent asWritten None payload ]
        |> List.map lf
        |> Expect.equal
          "test_results_batch"
          [ "event: test_results_batch\ndata: {\"completion\":{\"Case\":\"Complete\",\"Fields\":[1,1]},\"entries\":[{\"testId\":\"t1\",\"displayName\":\"t 1\",\"fullName\":\"M.t1\",\"origin\":{\"Case\":\"ReflectionOnly\"},\"framework\":{\"Case\":\"Expecto\"},\"category\":{\"Case\":\"Unit\"},\"currentPolicy\":{\"Case\":\"OnEveryChange\"},\"status\":{\"Case\":\"Passed\",\"Fields\":[\"00:00:00.0100000\"]},\"previousStatus\":{\"Case\":\"Passed\",\"Fields\":[\"00:00:00.0100000\"]}}],\"freshness\":{\"Case\":\"Fresh\"},\"generation\":1,\"lastDecision\":null,\"summary\":{\"total\":1,\"passed\":1,\"failed\":0,\"stale\":0,\"running\":0,\"disabled\":0,\"enabled\":true}}\n\n"
            "event: test_results_batch\ndata: {\"Completion\":{\"Case\":\"Complete\",\"Fields\":[1,1]},\"Entries\":[{\"TestId\":\"t1\",\"DisplayName\":\"t 1\",\"FullName\":\"M.t1\",\"Origin\":{\"Case\":\"ReflectionOnly\"},\"Framework\":{\"Case\":\"Expecto\"},\"Category\":{\"Case\":\"Unit\"},\"CurrentPolicy\":{\"Case\":\"OnEveryChange\"},\"Status\":{\"Case\":\"Passed\",\"Fields\":[\"00:00:00.0100000\"]},\"PreviousStatus\":{\"Case\":\"Passed\",\"Fields\":[\"00:00:00.0100000\"]}}],\"Freshness\":{\"Case\":\"Fresh\"},\"Generation\":1,\"LastDecision\":null,\"Summary\":{\"Total\":1,\"Passed\":1,\"Failed\":0,\"Stale\":0,\"Running\":0,\"Disabled\":0,\"Enabled\":true}}\n\n" ]
    ]
  ]
