/// The per-run `source` on `test_run_completed`: the wire gap this file exists for.
///
/// MEASURED BEFORE THIS FILE EXISTED (the audit that opened the work): the Neovim
/// plugin routes `test_run_completed` (`lua/sagefs/events.lua:13`, `sse.lua:99`,
/// `sse.lua:112`) and reads a per-run `source` at `lua/sagefs/testing.lua:1048`, but
/// NO F# source emitted an event with that name at all — `grep -rn
/// "test_run_completed" --include=*.fs` resolved to `SseParityTests.fs` alone. The
/// plugin therefore always took its degradation branch, and its fast path (skip the
/// whole-list `/api/sessions` re-read after a finished run) was dead code against every
/// shipped daemon.
///
/// WHY A SEPARATE FILE, and why these tests are about EMISSION rather than tolerance:
/// the gap was precisely that nothing asserted that the field is ever WRITTEN. A test
/// that only fed the decoder a payload without `source` would have kept passing the
/// whole time the daemon said nothing, which is exactly the state that was shipped.
/// Every case below either reads a `source` back out of a frame the daemon actually
/// produced, or pins the ABSENT case the plugin's own degradation specs feed.
///
/// WHAT THE FIELD IS, and what it is NOT: `SourceState` answers "is the build this
/// session runs behind the files on disk" — DISK AHEAD OF BUILD. It is not
/// `LiveTestDecision`'s `Trust`/`Freshness`, which answers "was the code edited since
/// this run was dispatched" — a DIFFERENT fact that can be true while the disk is in
/// sync, and vice versa. The two live in the same payload on purpose (a client can
/// read both), and `SourceStateProbe.fs` says so at the type it returns.
module SageFs.Tests.RunSourceWireTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.ProjectLoading
open SageFs.Server
open SageFs.Server.McpServer

/// The one batch payload these tests push through the formatter. Built from real
/// SageFs.Core types (no fakes), so what is asserted is a shape the daemon can produce.
module RunSourceFixtures =

  open SageFs.Features.LiveTesting

  let batch () : TestResultsBatchPayload =
    { Generation = RunGeneration 7
      Freshness = ResultFreshness.Fresh
      Completion = BatchCompletion.Complete(1, 1)
      Entries =
        [| { TestId = TestId.TestId "test-id-1"
             DisplayName = "should add"
             FullName = "Module.shouldAdd"
             Origin = TestOrigin.SourceMapped("src/Tests.fs", 10)
             Framework = TestFramework.Expecto
             Category = TestCategory.Unit
             CurrentPolicy = RunPolicy.OnEveryChange
             Status = TestRunStatus.Passed TestTimeouts.testElapsed
             PreviousStatus = TestRunStatus.Passed TestTimeouts.testElapsed
             Provenance = ResultProvenance.Compiled } |]
      Summary = { Total = 1; Passed = 1; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
      LastDecision = None }

// ── Fixtures: a real session, a real warmup, real disk state ─────────────

let private handle : WorkerHandle = { Pid = 1; Port = Some 5000 }

let private projectFile : string =
  let dir =
    Path.Combine(Path.GetTempPath(), "sagefs-runsource-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> try Directory.Delete(dir, true) with _ -> ())
  let fsproj = Path.Combine(dir, "MyApp.fsproj")
  File.WriteAllText(
    fsproj,
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>MyApp</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Lib.fs\" /></ItemGroup></Project>")
  File.WriteAllText(Path.Combine(dir, "Lib.fs"), "module MyApp.Lib\nlet x = 1\n")
  fsproj

let private mkSessionId (hex: string) : SessionId =
  match SessionId.validate hex with
  | Ok id -> id
  | Error e -> failwith e

let private sessId = mkSessionId "0a0b0c0d"

let private at = DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc)

let private projectOf (path: string) : ClassifiedProject =
  { Path = path
    Role = ProjectRole.Library
    PackageRefs = []
    LoadMode = LoadMode.Evaluated
    Build = SageFs.BuildOptimization.Unoptimized }

let private mkSessionInfo (project: string) : SessionInfo =
  { Id = sessId
    Name = None
    Projects = [ project ]
    WorkingDirectory = Path.GetDirectoryName project
    SolutionRoot = None
    Status = SessionLifecycleStatus.Ready handle
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CreatedAt = at
    LastActivity = at
    ActiveProject = None
    ProjectRoles = [ projectOf project ]
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = SageFs.ReplFreshness.InSync }

/// A warmup that reports it loaded `MyApp` FROM the build output the tests write, so
/// the probe compares real file stamps instead of answering "not inspectable".
let private mkWarmup (assemblyPath: string) (startedAt: DateTimeOffset) : WarmupContext =
  { SourceFilesScanned = 1
    AssembliesLoaded = [ { Name = "MyApp"; Path = assemblyPath; NamespaceCount = 1; ModuleCount = 1 } ]
    NamespacesOpened = []
    FailedOpens = []
    PhaseTiming = FixtureDurations.warmupTotalOnly 10L
    StartedAt = startedAt }

/// Write the build output with a real, OLD write time, so the files the project builds
/// from are unambiguously older than the build the worker loaded.
let private writeStaleBuildOutput (projectFile: string) : string =
  let dir = Path.GetDirectoryName projectFile
  let binDir = Path.Combine(dir, "bin")
  Directory.CreateDirectory binDir |> ignore
  let dll = Path.Combine(binDir, "MyApp.dll")
  File.WriteAllBytes(dll, Array.zeroCreate<byte> 1)
  File.SetLastWriteTimeUtc(dll, at)
  dll

/// The production SSE options (`McpServer.fs`'s `Json.optionsOf Json.standard`), so a
/// frame these tests read is spelled the way the daemon spells it.
let private daemonOpts () = Json.optionsOf SageFs.Json.standard

let private extractData (frame: string) : string =
  frame.Split('\n')
  |> Array.choose (fun line ->
    match line.StartsWith("data: ") with
    | true -> Some(line.Substring(6))
    | false -> None)
  |> String.concat "\n"

// ── Group 1: the emitter's own words ─────────────────────────────────────

[<Tests>]
let runSourceEmissionTests = testList "test_run_completed carries the run's source" [

  testCase "the payload a run produces CARRIES `source`, keyed lowercase, holding the SourceState wire shape" <| fun _ ->
    // THE assertion the gap was about: the emitted payload CONTAINS the field. Reading
    // it back out of a frame the real formatter produced is the only thing that proves
    // emission; nothing here feeds the decoder a payload and calls it a day.
    //
    // The verdict comes from the PRODUCTION producer, so this test fails if the wire
    // shape and the producer ever drift apart.
    let state = SageFs.SourceStateProbe.ofSessionRecord (Some (mkSessionInfo projectFile)) None
    let payload =
      SageFs.SseWriter.formatTestResultsBatchEvent
        (daemonOpts ())
        (Some (SessionId.value sessId))
        (RunSourceFixtures.batch ())
        (Some(SageFs.SourceState.toWire state))
    let data = extractData payload
    use doc = JsonDocument.Parse(data)
    let root = doc.RootElement
    let mutable found = Unchecked.defaultof<JsonElement>
    root.TryGetProperty("source", &found)
    |> Expect.isTrue
         (sprintf
           "the emitted test_run_completed payload must CARRY `source`; the payload was %s" data)
    let source = root.GetProperty("source")
    source.GetProperty("state").GetString()
    |> Expect.equal
         "the emitted state is the SourceState wire token the producer actually returned"
         "Unknown"

  testCase "the source field is ABSENT when the daemon could not answer, so the plugin's degradation branch is real" <| fun _ ->
    // The ABSENT case is not a special-case bolted on: it is what a reader gets when
    // there is no verdict, and the plugin's five degradation specs feed exactly this.
    // A reader must never receive `source: null` and read it as "nothing was stale".
    let payload =
      SageFs.SseWriter.formatTestRunCompletedEvent
        (daemonOpts ())
        (Some (SessionId.value sessId))
        (RunSourceFixtures.batch ())
        None
    use doc = JsonDocument.Parse(extractData payload)
    let root = doc.RootElement
    let mutable found = Unchecked.defaultof<JsonElement>
    root.TryGetProperty("source", &found)
    |> Expect.isFalse "no verdict means NO source field, never a null one a reader could mistake"
    // The rest of the run still says everything it always did.
    let mutable foundGen = Unchecked.defaultof<JsonElement>
    root.TryGetProperty("Generation", &foundGen)
    |> Expect.isTrue "the run's generation is still there — only `source` is missing"

  testCase "a Stale verdict rides the field with the changed files the plugin names" <| fun _ ->
    let payload =
      SageFs.SseWriter.formatTestRunCompletedEvent
        (daemonOpts ())
        (Some (SessionId.value sessId))
        (RunSourceFixtures.batch ())
        (Some (SageFs.SourceState.toWire
          (SageFs.SourceState.Stale
            [ { Path = "src/Ticker.fs"
                Because =
                  SageFs.StaleBecause.EditedAfterBuild(DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc), at) } ])))
    use doc = JsonDocument.Parse(extractData payload)
    let source = doc.RootElement.GetProperty("source")
    source.GetProperty("state").GetString() |> Expect.equal "stale token" "Stale"
    let changed = source.GetProperty("changedFiles")
    changed.GetArrayLength() |> Expect.equal "the one changed file is named" 1
    changed[0].GetProperty("path").GetString() |> Expect.equal "the file's path" "src/Ticker.fs"

  testCase "the frame is a real SSE frame: named event, one data line, SessionId injected" <| fun _ ->
    let frame =
      SageFs.SseWriter.formatTestRunCompletedEvent
        (daemonOpts ())
        (Some (SessionId.value sessId))
        (RunSourceFixtures.batch ())
        None
    frame |> Expect.stringStarts "event line" "event: test_run_completed\n"
    frame |> Expect.stringEnds "SSE frame terminator" "\n\n"
    (frame.Split('\n') |> Array.filter (fun l -> l.StartsWith("data: ")) |> Array.length)
    |> Expect.equal "one data line, so a client reads one JSON object" 1
    frame |> Expect.stringContains "the session id is injected" (SessionId.value sessId)
]

// ── Group 2: the probe the emitter is fed ─────────────────────────────────
//
// `McpServer.fs` reads the source at the two publish sites through
// `SourceStateProbe.ofSessionRecord` (the SAME reading `/api/sessions` and the MCP
// `list_sessions` tool use), and passes the answer in. These cases drive that probe
// against real files, so the value that lands on the wire is the value a real reading
// produces — not a hand-built `SourceState` that only a test ever constructed.

[<Tests>]
let runSourceProbeTests = testList "the reading the run's source comes from" [

  testCase "a session whose build is older than its sources reads Stale" <| fun _ ->
    let project = projectFile
    let dll = writeStaleBuildOutput project
    let info = mkSessionInfo project
    // The worker loaded the build at a time AFTER the files were written.
    let warmup = mkWarmup dll (DateTimeOffset(at))
    let state = SageFs.SourceStateProbe.ofSessionRecord (Some info) (Some warmup)
    let token = SageFs.SourceState.token state
    token
    |> Expect.stringStarts "Lib.fs was written after the build this session loaded" "Stale"

  testCase "the same reading written as a verdict is what the payload carries" <| fun _ ->
    // End-to-end over the real pieces: probe -> wire -> frame -> JSON. This is what
    // `McpServer.fs` does at its publish sites, and it is what proves the field the
    // plugin reads can actually be produced on a real disk state.
    let project = projectFile
    let dll = writeStaleBuildOutput project
    let info = mkSessionInfo project
    let warmup = mkWarmup dll (DateTimeOffset(at))
    let state = SageFs.SourceStateProbe.ofSessionRecord (Some info) (Some warmup)
    let frame =
      SageFs.SseWriter.formatTestRunCompletedEvent
        (daemonOpts ())
        (Some (SessionId.value sessId))
        (RunSourceFixtures.batch ())
        (Some (SageFs.SourceState.toWire state))
    use doc = JsonDocument.Parse(extractData frame)
    doc.RootElement.GetProperty("source").GetProperty("state").GetString()
    |> Expect.equal "the payload carries the state the probe actually read" (SageFs.SourceState.token state)

  testCase "a session the registry does not know reads Unknown, which is still an honest verdict — never InSync" <| fun _ ->
    let state = SageFs.SourceStateProbe.ofSessionRecord None None
    state
    |> Expect.equal
         "no record of the session means the build cannot be named, and Unknown is that answer"
         (SageFs.SourceState.Unknown (SageFs.UnknownReason.LoadTimeNotReported "the registry has no record of the session"))

  testCase "a run with no session id at all cannot be read against the disk, and the refusal names why" <| fun _ ->
    // The ABSENT case is decided HERE, not at the formatter: a run the daemon cannot
    // attribute to a session has no session to read a build from. That refusal is a
    // named DU case (not a string Result), and it is the only thing that reaches the
    // wire as "no `source` field at all".
    let verdict = SageFs.SourceStateProbe.runSourceOf None None
    verdict
    |> Expect.equal
         "an unattributable run is refused by name, so the payload carries no source"
         SageFs.SourceStateProbe.SourceSourceRefusal.NoSessionToRead

  testCase "a named session that is not in the registry is refused by name too" <| fun _ ->
    // A WIRED reader that knows nothing — `Some (fun () -> (None, None))`. Passing `None`
    // for the reader is a different question ("no way to read the registry was wired"),
    // which is an honest `Unknown`, not a claim that the session is unknown.
    SageFs.SourceStateProbe.runSourceOf (Some (SessionId.value sessId)) (Some (fun () -> (None, None)))
    |> Expect.equal
         "the registry has no record, so the probe is never attempted"
         SageFs.SourceStateProbe.SourceSourceRefusal.SessionNotKnown

  testCase "a session the registry knows and the probe can read produces a verdict, not a refusal" <| fun _ ->
    let project = projectFile
    let dll = writeStaleBuildOutput project
    let info = mkSessionInfo project
    let warmup = mkWarmup dll (DateTimeOffset(at))
    match SageFs.SourceStateProbe.runSourceOf (Some (SessionId.value sessId)) (Some (fun () -> Some info, Some warmup)) with
    | SageFs.SourceStateProbe.SourceSourceRefusal.Assessed state ->
      SageFs.SourceState.token state |> Expect.equal "a known, readable session is assessed, not refused" "Stale"
    | other -> failtestf "expected an assessed reading, got %A" other
]

// ── Group 3: honesty about what this field is ────────────────────────────

[<Tests>]
let runSourceShapeTests = testList "the run's source is the disk/build fact, not the edit-since-dispatch fact" [

  testCase "the payload carries both facts, as differently named fields, so no reader can conflate them" <| fun _ ->
    // `Freshness`/`LastDecision.Trust` is LiveTestDecision's fact: the code was EDITED
    // SINCE THIS RUN WAS DISPATCHED. `source` is SourceState's fact: the DISK IS AHEAD
    // OF THE BUILD. They are true together, apart, or not at all. Putting them on the
    // same payload is deliberate; putting them under one name would not be.
    let payload =
      SageFs.SseWriter.formatTestRunCompletedEvent
        (daemonOpts ())
        (Some (SessionId.value sessId))
        { RunSourceFixtures.batch () with Freshness = SageFs.Features.LiveTesting.ResultFreshness.Fresh }
        (Some (SageFs.SourceState.toWire (SageFs.SourceState.InSync(at, TestMagnitudes.packedProbeCount))))
    use doc = JsonDocument.Parse(extractData payload)
    let root = doc.RootElement
    let mutable foundFresh = Unchecked.defaultof<JsonElement>
    root.TryGetProperty("Freshness", &foundFresh)
    |> Expect.isTrue "the edit-since-dispatch fact keeps its own field"
    let mutable foundSrc = Unchecked.defaultof<JsonElement>
    root.TryGetProperty("source", &foundSrc)
    |> Expect.isTrue "the disk-ahead-of-build fact is `source`, and is not spelled like the other"
    let fresh = root.GetProperty("Freshness")
    fresh.GetProperty("Case").GetString() |> Expect.equal "Freshness is the ResultFreshness DU" "Fresh"

  testCase "an InSync source says how many files it compared, so a green run over a known build is not merely asserted" <| fun _ ->
    let payload =
      SageFs.SseWriter.formatTestRunCompletedEvent
        (daemonOpts ())
        (Some (SessionId.value sessId))
        (RunSourceFixtures.batch ())
        (Some (SageFs.SourceState.toWire (SageFs.SourceState.InSync(at, TestMagnitudes.packedProbeCount))))
    use doc = JsonDocument.Parse(extractData payload)
    let source = doc.RootElement.GetProperty("source")
    source.GetProperty("filesChecked").GetInt32()
    |> Expect.equal "the comparison count rides with the verdict" TestMagnitudes.packedProbeCount

  testCase "apiVersion did NOT move: a field added to an existing event is not a shape change" <| fun _ ->
    // Adding a field to an existing SSE event is additive, not a breaking change: every
    // client that read the event before still reads it the same way, and a client that
    // does not know `source` ignores it. `EndpointContracts.apiVersion`'s own doc says
    // it moves when endpoints are added/removed or request/response shapes change
    // BREAKINGLY. So it stays put, and `compat.lua`'s closed 3..3 range stays true.
    EndpointContracts.apiVersion
    |> Expect.equal "the wire contract version is unchanged by an additive field" 3
]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant runSourceEmissionTests |> ignore
do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant runSourceShapeTests |> ignore