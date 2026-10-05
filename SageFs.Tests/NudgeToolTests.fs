/// `nudge_value`, the MCP door onto the nudge engine: what the reply tells a
/// caller, how the session's files become the files a call may touch, and who
/// may call it. The reply tests render real runs; the session tests start a real
/// worker HTTP server that reports a real project file list; the identity tests
/// ask the same gate every other tool that changes things goes through.
module SageFs.Tests.NudgeToolTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Cohort
open SageFs.McpTools
open SageFs.Server
open SageFs.Server.McpTools
open SageFs.WorkerProtocol
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.TweakLog
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.Nudge
open SageFs.Simulation
open SageFs.Simulation.NudgeWorld

let json (text: string) : JsonElement = JsonDocument.Parse(text).RootElement.Clone()
let field (e: JsonElement) (name: string) : JsonElement = e.GetProperty name
let text (e: JsonElement) (name: string) : string = (field e name).GetString()
let strings (e: JsonElement) : string list = [ for item in e.EnumerateArray() -> item.GetString() ]

let receipt : Receipt =
  { File = sourcePath
    Address = gravity
    Before = "9.8"
    After = "12.5"
    HashAfter = contentHash "12.5"
    FileHashBefore = contentHash tuningSource
    FileHashAfter = contentHash (tuningSource.Replace("9.8", "12.5"))
    EventId = 7 }

let ran (outcome: NudgeOutcome) (notes: RunNote list) : Result<Ran, NudgeRefusal> = Ok { Outcome = outcome; Notes = notes }

let allNotes = [ RunNote.TornJournalTailRemoved; RunNote.UnlandedWriteMarkedUndone 3; RunNote.ExpressionNotTypeChecked; RunNote.FileNotWatched ]

[<Tests>]
let renderTests =
  testList "nudge_value reply" [
    testCase "WHY - a landed write is told with its outcome token, the address as typed, both texts and the hash to ask for next" <| fun _ ->
      let reply = McpNudge.render (ran (NudgeOutcome.Written receipt) []) |> json
      text reply "outcome" |> Expect.equal "the outcome token" (NudgeOutcome.token (NudgeOutcome.Written receipt))
      text reply "address" |> Expect.equal "the address a caller can send back" (NudgeAddress.format gravity)
      text reply "before" |> Expect.equal "before" "9.8"
      text reply "after" |> Expect.equal "after" "12.5"
      text reply "hashAfter" |> Expect.equal "the hash a next write must carry" (contentHash "12.5")
      (field reply "eventId").GetInt32() |> Expect.equal "the journal record" 7
      (field reply "notes").GetArrayLength() |> Expect.equal "no notes" 0

    testCase "WHY - undo and redo are told as their own outcomes, so a caller can tell a step back from a write" <| fun _ ->
      let tokens =
        [ NudgeOutcome.Written receipt; NudgeOutcome.Undone receipt; NudgeOutcome.Redone receipt; NudgeOutcome.Unchanged(gravity, "9.8") ]
        |> List.map (fun outcome -> text (json (McpNudge.render (ran outcome []))) "outcome")
      tokens |> List.distinct |> List.length |> Expect.equal "four distinct tokens" 4

    testCase "WHY - a refusal carries its token, the rule and the next action, and is marked refused" <| fun _ ->
      let refusal = NudgeRefusal.SourceMoved(contentHash "9.8", contentHash "9.81", "9.81")
      let reply = McpNudge.render (Error refusal) |> json
      text reply "outcome" |> Expect.equal "refused" "Refused"
      text reply "refusal" |> Expect.equal "the case token" (NudgeRefusal.token refusal)
      text reply "rule" |> Expect.equal "the rule" (NudgeRefusal.rule refusal)
      text reply "nextAction" |> Expect.equal "the next action" (NudgeRefusal.nextAction refusal)

    testCase "WHY - every note a run can carry is told by its own token" <| fun _ ->
      let reply = McpNudge.render (ran (NudgeOutcome.Written receipt) allNotes) |> json
      field reply "notes" |> strings |> Expect.equal "one token per note, in order" (allNotes |> List.map RunNote.token)
      (allNotes |> List.map RunNote.token |> List.distinct |> List.length) |> Expect.equal "tokens are distinct" allNotes.Length
      FSharpType.GetUnionCases typeof<RunNote> |> Array.length |> Expect.equal "the list above covers every note" allNotes.Length

    testCase "WHY - an inspection lists each value with its address, text, hash and kind, and says how many there were" <| fun _ ->
      let world = create ()
      let reply = McpNudge.render (inspectAll world) |> json
      text reply "outcome" |> Expect.equal "inspected" "Inspected"
      text reply "fileHash" |> Expect.equal "the whole file's hash" (contentHash tuningSource)
      let items = field reply "items" |> fun e -> [ for item in e.EnumerateArray() -> item ]
      let expected = addressesOf tuningSource |> Result.defaultValue []
      items |> List.map (fun i -> text i "address") |> Expect.equal "every address, in the engine's order, as typed text" (expected |> List.map NudgeAddress.format)
      for item in items do
        text item "hash" |> Expect.equal "the hash of its text" (contentHash (text item "text"))
      let gravityItem = items |> List.find (fun i -> text i "address" = NudgeAddress.format gravity)
      text gravityItem "kind" |> Expect.equal "a knob" "Knob"
      text gravityItem "valueKind" |> Expect.equal "of the float kind, so a caller knows how to write it" (LiteralKindName.toToken LiteralKindName.Real)
      let formula = items |> List.find (fun i -> text i "address" = NudgeAddress.format jumpVelocity)
      text formula "kind" |> Expect.equal "a formula" "Formula"
      text reply "listing" |> Expect.equal "complete" "Complete"
      ((field reply "undoSteps").GetInt32(), (field reply "redoSteps").GetInt32()) |> Expect.equal "no history yet" (0, 0)

    testCase "WHY - an inspected value says where it sits (line, column, endLine, endColumn) so a client never searches the file for its text" <| fun _ ->
      let world = create ()
      let reply = McpNudge.render (inspectAll world) |> json
      let items = field reply "items" |> fun e -> [ for item in e.EnumerateArray() -> item ]
      let lines = tuningSource.Split '\n'
      let sliceOf (item: JsonElement) : string =
        let line = (field item "line").GetInt32()
        let column = (field item "column").GetInt32()
        let endLine = (field item "endLine").GetInt32()
        let endColumn = (field item "endColumn").GetInt32()
        match line = endLine with
        | true -> lines.[line - 1].Substring(column, endColumn - column)
        | false ->
          let first = lines.[line - 1].Substring column
          let middle = [ for n in line .. endLine - 2 -> lines.[n] ]
          let last = lines.[endLine - 1].Substring(0, endColumn)
          String.Join("\n", [ first ] @ middle @ [ last ])
      for item in items do
        sliceOf item |> Expect.equal (sprintf "the span of %s holds exactly its text" (text item "address")) (text item "text")
      let gravityItem = items |> List.find (fun i -> text i "address" = NudgeAddress.format gravity)
      ((field gravityItem "line").GetInt32(), (field gravityItem "column").GetInt32())
      |> Expect.equal "lines are the parser's own (1-based) and columns are 0-based, as the diagnostics wire has them" (3, 14)

    testCase "WHY - a literal's value comes typed by its kind, so a client reads a number as a number and a union case as its name" <| fun _ ->
      let world = create ()
      let reply = McpNudge.render (inspectAll world) |> json
      let items = field reply "items" |> fun e -> [ for item in e.EnumerateArray() -> item ]
      let valueOf (address: TweakAddress) = field (items |> List.find (fun i -> text i "address" = NudgeAddress.format address)) "value"
      (valueOf gravity).ValueKind |> Expect.equal "a real is a JSON number" JsonValueKind.Number
      (valueOf gravity).GetDouble() |> Expect.equal "its value" 9.8
      (valueOf maxHealth).ValueKind |> Expect.equal "an integer is a JSON number" JsonValueKind.Number
      (valueOf maxHealth).GetInt64() |> Expect.equal "its value" 100L
      (valueOf difficulty).GetString() |> Expect.equal "a union case is its name" "Easy"
      (valueOf label).GetString() |> Expect.equal "text is its content, without the quotes the source spells it with" "A"

    testCase "WHY - a formula has no value to read, so it carries no value field at all, not a null a client would mistake for a literal" <| fun _ ->
      let world = create ()
      let reply = McpNudge.render (inspectAll world) |> json
      let formula = field reply "items" |> fun e -> [ for item in e.EnumerateArray() -> item ] |> List.find (fun i -> text i "address" = NudgeAddress.format jumpVelocity)
      let mutable found = JsonElement()
      formula.TryGetProperty("value", &found) |> Expect.isFalse "no value on a formula"

    testCase "WHY - every kind of literal has the JSON type its kind promises, and a real with no JSON number is null rather than a crash" <| fun _ ->
      let kindOf (value: LiteralValue) = (McpNudge.valueJson value |> fun node -> if isNull node then JsonValueKind.Null else JsonDocument.Parse(node.ToJsonString()).RootElement.ValueKind)
      kindOf (LiteralValue.Bool true) |> Expect.equal "a boolean" JsonValueKind.True
      kindOf (LiteralValue.Bool false) |> Expect.equal "a boolean" JsonValueKind.False
      kindOf (LiteralValue.Integer 7L) |> Expect.equal "an integer" JsonValueKind.Number
      kindOf (LiteralValue.Real 1.5) |> Expect.equal "a real" JsonValueKind.Number
      kindOf (LiteralValue.Real Double.PositiveInfinity) |> Expect.equal "no JSON number for infinity" JsonValueKind.Null
      kindOf (LiteralValue.Real Double.NaN) |> Expect.equal "no JSON number for NaN" JsonValueKind.Null
      kindOf (LiteralValue.Char 'x') |> Expect.equal "a character is one-character text" JsonValueKind.String
      kindOf (LiteralValue.Text "t") |> Expect.equal "text" JsonValueKind.String
      kindOf (LiteralValue.Case "Hard") |> Expect.equal "a case is its name" JsonValueKind.String
  ]

[<Tests>]
let nudgeWithTests =
  testList "nudge_value flow" [
    testTask "WHY - a set through the reply path writes the file, and the same hash a second time is refused as stale" {
      let world = create ()
      let locks = FileLocks()
      let set literal =
        McpNudge.nudgeWith owned world.Ports locks
          { Action = "set"; File = sourcePath; Address = NudgeAddress.format gravity; Seen = contentHash "9.8"; Literal = literal; Expression = "" }
      let! first = set "12.5"
      text (json first) "outcome" |> Expect.equal "written" "Written"
      source world |> Expect.stringContains "the file holds it" "let gravity = 12.5"
      let! second = set "13.5"
      text (json second) "refusal" |> Expect.equal "stale, not overwritten" "SourceMoved"
      source world |> Expect.stringContains "still the first value" "let gravity = 12.5"
    }

    testTask "WHY - a request that is not a nudge is refused by name before anything runs" {
      let world = create ()
      let! reply = McpNudge.nudgeWith owned world.Ports (FileLocks()) { Action = "wiggle"; File = sourcePath; Address = ""; Seen = ""; Literal = ""; Expression = "" }
      text (json reply) "refusal" |> Expect.equal "unknown action" (NudgeRefusal.token (NudgeRefusal.UnknownAction "wiggle"))
    }

    testTask "WHY - inspect then set then undo through the reply path puts the original bytes back" {
      let world = create ()
      let original = world.Disk.BytesOf sourcePath
      let locks = FileLocks()
      let call action address seen literal =
        McpNudge.nudgeWith owned world.Ports locks { Action = action; File = sourcePath; Address = address; Seen = seen; Literal = literal; Expression = "" }
      let! looked = call "inspect" "" "" ""
      let hash = field (json looked) "items" |> fun e -> [ for i in e.EnumerateArray() -> i ] |> List.find (fun i -> text i "address" = NudgeAddress.format maxHealth) |> fun i -> text i "hash"
      let! _ = call "set" (NudgeAddress.format maxHealth) hash "150"
      source world |> Expect.stringContains "nudged" "MaxHealth = 150"
      let! undone = call "undo" "" "" ""
      text (json undone) "outcome" |> Expect.equal "undone" "Undone"
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical" original
    }
  ]

// ── a real worker, a real project file ──

let startWorker (projectFile: string) (watched: bool) : Task<WorkerHttpTransport.HttpWorkerServer> =
  let state = if watched then HotReloadState.watchAll [ projectFile ] HotReloadState.empty else HotReloadState.empty
  WorkerHttpTransport.startServer
    (fun _ -> async { return WorkerResponse.WorkerShuttingDown })
    (ref state)
    Features.KeptState.Access.none
    [ projectFile ]
    (fun () -> WarmupContext.empty)
    (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun })
    (fun () -> HostAgent.AgentAnswered HostAgent.NoCoverage)
    0

let contextFor (workingDirectory: string) (status: SessionLifecycleStatus) (hasSession: bool) : McpContext =
  let sid = SessionId.newId ()
  let sessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- SessionId.value sid
  let info : SessionInfo =
    { Id = sid
      Name = None
      Projects = [ "Tuning.fsproj" ]
      WorkingDirectory = workingDirectory
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = status
      Workflow = WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet
      Freshness = SageFs.ReplFreshness.InSync }
  let ops =
    { SessionManagementOps.stub with
        GetSessionInfo = fun _ -> Task.FromResult(if hasSession then Some info else None)
        GetAllSessions = fun () -> Task.FromResult(if hasSession then [ info ] else [])
        GetProxy = fun _ -> Task.FromResult(if hasSession then Some(fun _ -> async { return WorkerResponse.WorkerShuttingDown }) else None) }
  { FrictionStore = None
    DiagnosticsChanged = Event<Features.DiagnosticsStore.T>().Publish
    StateChanged = None
    SessionOps = ops
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

let withProject (body: string -> string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-nudgetool-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory dir |> ignore
  let file = Path.Combine(dir, "Tuning.fs")
  File.WriteAllText(file, tuningSource)
  try body dir file
  finally (try Directory.Delete(dir, true) with _ -> ())

let rawSet (file: string) (address: TweakAddress) (seen: string) (literal: string) : RawNudge =
  { Action = "set"; File = file; Address = NudgeAddress.format address; Seen = contentHash seen; Literal = literal; Expression = "" }

[<Tests>]
let sessionTests =
  testList "nudge_value and the session's files" [
    testCase "WHY - the worker's file list becomes the owned set: every listed file, and the watched ones marked" <| fun _ ->
      let listed = """{"files":[{"path":"/p/A.fs","watched":true},{"path":"/p/B.fs","watched":false}],"watchedCount":1,"kept":[]}"""
      match McpNudge.ownedFilesFromJson "s1" "/p" listed with
      | Ok owned ->
        owned.Paths |> Expect.equal "both files are owned" (Set.ofList [ Path.GetFullPath "/p/A.fs"; Path.GetFullPath "/p/B.fs" ])
        owned.Watched |> Expect.equal "only the watched one is marked" (Set.ofList [ Path.GetFullPath "/p/A.fs" ])
        owned.Session |> Expect.equal "the session" "s1"
      | Error refusal -> failtestf "refused: %s" (NudgeRefusal.token refusal)

    testCase "WHY - an answer that is not a file list is refused as unknown, never read as 'owns nothing' or 'owns everything'" <| fun _ ->
      for answer in [ ""; "not json"; "{}"; """{"files":"x"}""" ] do
        match McpNudge.ownedFilesFromJson "s1" "/p" answer with
        | Error(NudgeRefusal.ProjectFilesUnknown _) -> ()
        | other -> failtestf "%A: expected ProjectFilesUnknown, got %A" answer other

    testTask "WHY - the files a session owns are its project's files, and the ones hot reload watches are marked" {
      let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-nudgetool-%s" (Guid.NewGuid().ToString("N")))
      let file = Path.Combine(dir, "Tuning.fs")
      use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker file true
      let ctx = contextFor dir (SessionLifecycleStatus.Ready { Pid = 42; Port = Some(Uri(server.BaseUrl).Port) }) true
      match! McpNudge.ownedFilesOf ctx (Some dir) with
      | Ok owned ->
        owned.Paths |> Set.contains (Path.GetFullPath file) |> Expect.isTrue "the project file is owned"
        owned.Watched |> Set.contains (Path.GetFullPath file) |> Expect.isTrue "and watched"
        owned.WorkingDirectory |> Expect.equal "relative paths resolve against the session's directory" dir
      | Error refusal -> failtestf "refused: %s" (NudgeRefusal.token refusal)
    }

    testTask "WHY - a nudge through the session writes the real file, journals it under the tweaks directory, and undo restores the bytes" {
      let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-nudgetool-%s" (Guid.NewGuid().ToString("N")))
      Directory.CreateDirectory dir |> ignore
      let file = Path.Combine(dir, "Tuning.fs")
      File.WriteAllText(file, tuningSource)
      let tweaks = Path.Combine(dir, "tweaks")
      try
        use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker file true
        let ctx = contextFor dir (SessionLifecycleStatus.Ready { Pid = 42; Port = Some(Uri(server.BaseUrl).Port) }) true
        let original = File.ReadAllBytes file
        let! set = McpNudge.nudgeValueIn tweaks ctx dir (rawSet file gravity "9.8" "12.5")
        text (json set) "outcome" |> Expect.equal "written" "Written"
        File.ReadAllText file |> Expect.equal "only that literal changed on the real disk" (tuningSource.Replace("9.8", "12.5"))
        Directory.GetFiles(tweaks, "*.events", SearchOption.AllDirectories).Length |> Expect.equal "one journal, outside the repo's source" 1
        Directory.GetFiles(dir, "*.tmp").Length |> Expect.equal "no temp file left beside the source" 0
        let! undone = McpNudge.nudgeValueIn tweaks ctx dir { Action = "undo"; File = file; Address = ""; Seen = ""; Literal = ""; Expression = "" }
        text (json undone) "outcome" |> Expect.equal "undone" "Undone"
        File.ReadAllBytes file |> Expect.equal "byte-identical to before the nudge" original
      finally
        (try Directory.Delete(dir, true) with _ -> ())
    }

    testTask "WHY - a project file hot reload is not watching is written, and the reply says the running app has not seen it" {
      let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-nudgetool-%s" (Guid.NewGuid().ToString("N")))
      Directory.CreateDirectory dir |> ignore
      let file = Path.Combine(dir, "Tuning.fs")
      File.WriteAllText(file, tuningSource)
      try
        use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker file false
        let ctx = contextFor dir (SessionLifecycleStatus.Ready { Pid = 42; Port = Some(Uri(server.BaseUrl).Port) }) true
        let! reply = McpNudge.nudgeValueIn (Path.Combine(dir, "tweaks")) ctx dir (rawSet file gravity "9.8" "12.5")
        let parsed = json reply
        text parsed "outcome" |> Expect.equal "still written" "Written"
        field parsed "notes" |> strings |> Expect.contains "the note" (RunNote.token RunNote.FileNotWatched)
      finally
        (try Directory.Delete(dir, true) with _ -> ())
    }

    testTask "WHY - a file outside the session's projects is refused as not owned, and is not touched" {
      let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-nudgetool-%s" (Guid.NewGuid().ToString("N")))
      Directory.CreateDirectory dir |> ignore
      let file = Path.Combine(dir, "Tuning.fs")
      let stranger = Path.Combine(dir, "Stranger.fs")
      File.WriteAllText(file, tuningSource)
      File.WriteAllText(stranger, tuningSource)
      try
        use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker file true
        let ctx = contextFor dir (SessionLifecycleStatus.Ready { Pid = 42; Port = Some(Uri(server.BaseUrl).Port) }) true
        let! reply = McpNudge.nudgeValueIn (Path.Combine(dir, "tweaks")) ctx dir (rawSet stranger gravity "9.8" "12.5")
        text (json reply) "refusal" |> Expect.equal "named" "NotOwned"
        File.ReadAllText stranger |> Expect.equal "untouched" tuningSource
      finally
        (try Directory.Delete(dir, true) with _ -> ())
    }

    testTask "WHY - with no session the call is refused as such, with the routing advice, not as an error" {
      let ctx = contextFor "/tmp/none" SessionLifecycleStatus.Stopped false
      let! reply = McpNudge.nudgeValueIn "/tmp/none/tweaks" ctx "/tmp/none" (rawSet "/tmp/none/Tuning.fs" gravity "9.8" "12.5")
      text (json reply) "refusal" |> Expect.equal "no session" "NoSessionToAct"
    }

    testTask "WHY - a session with no worker to ask cannot say what it owns, so the call is refused rather than guessed" {
      let ctx = contextFor "/tmp/none" (SessionLifecycleStatus.Ready { Pid = 42; Port = None }) true
      let! reply = McpNudge.nudgeValueIn "/tmp/none/tweaks" ctx "/tmp/none" (rawSet "/tmp/none/Tuning.fs" gravity "9.8" "12.5")
      text (json reply) "refusal" |> Expect.equal "project files unknown" "ProjectFilesUnknown"
    }

    testTask "WHY - the registered tool answers the same way: it is the door, not a copy of it" {
      let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-nudgetool-%s" (Guid.NewGuid().ToString("N")))
      Directory.CreateDirectory dir |> ignore
      let file = Path.Combine(dir, "Tuning.fs")
      File.WriteAllText(file, tuningSource)
      try
        use! (server: WorkerHttpTransport.HttpWorkerServer) = startWorker file true
        let ctx = contextFor dir (SessionLifecycleStatus.Ready { Pid = 42; Port = Some(Uri(server.BaseUrl).Port) }) true
        let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)
        let! reply = tools.nudge_value("wiggle", file, "", "", "", "", dir)
        text (json reply) "refusal" |> Expect.equal "the unknown action is named" "UnknownAction"
        File.ReadAllText file |> Expect.equal "nothing written" tuningSource
      finally
        (try Directory.Delete(dir, true) with _ -> ())
    }
  ]

// ── who may call it ──

let alice = MemberTable.MemberId.Minted "alice"
let asObserver = Authority.Member(alice, JoinableRole.Observer)
let asVerifier = Authority.Member(alice, JoinableRole.Verifier)
let asImplementer = Authority.Member(alice, JoinableRole.Implementer)

let grant (preset: Capability.RolePreset) : Capability.Grant =
  { Preset = preset
    Scope = (match Capability.ScopePrefix.tryParse "" with Ok p -> p | Error r -> failwithf "%A" r)
    Route = Capability.RouteBinding.BoundToSession "nudge-test-session"
    NotAfter = DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) }

[<Tests>]
let identityTests =
  testList "nudge_value and the identity gate" [
    testCase "WHY - the tool is declared to the gate, so an undeclared-tool refusal can never be what stops it" <| fun _ ->
      Affordances.ToolName.tryParse "nudge_value" |> Expect.equal "declared" (Some Affordances.ToolName.NudgeValue)
      Affordances.toolGate "nudge_value" |> Expect.equal "no session-state gate: the tool names its own refusals" (Some Affordances.ToolGate.AlwaysAvailable)
      Affordances.declaredGateTools |> Expect.contains "in the declared set" "nudge_value"

    testCase "WHY - an Observer and a Verifier are refused the tool that changes source files, and a working member is not" <| fun _ ->
      Affordances.checkAuthorityAllowed asObserver Affordances.ToolName.NudgeValue |> Expect.isError "an Observer only reads"
      Affordances.checkAuthorityAllowed asVerifier Affordances.ToolName.NudgeValue |> Expect.isError "a Verifier runs tests, it does not edit source"
      Affordances.checkAuthorityAllowed asImplementer Affordances.ToolName.NudgeValue |> Expect.isOk "a working member edits"

    testCase "WHY - a member token needs the Implementer role: every narrower role is refused, naming the class" <| fun _ ->
      for preset in [ Capability.RolePreset.Observer; Capability.RolePreset.Analysis; Capability.RolePreset.Verifier ] do
        match Capability.admitTool (grant preset) "nudge_value" with
        | Error(Capability.ToolRefusal.NotInRole(tool, toolClass, _)) ->
          tool |> Expect.equal "the tool" "nudge_value"
          toolClass |> Expect.equal "changing a session's state is a lifecycle act" Capability.ToolClass.SessionLifecycle
        | other -> failtestf "%A should be refused, got %A" preset other
      Capability.admitTool (grant Capability.RolePreset.Implementer) "nudge_value" |> Expect.isOk "an Implementer token may"

    testCase "WHY - the same decision the daemon's gate makes for any tool is made for this one" <| fun _ ->
      match ToolAuthorityGate.decide alice asObserver "nudge_value" with
      | ToolAuthorityGate.Decision.Refused(reason, nextAction) ->
        reason |> String.IsNullOrWhiteSpace |> Expect.isFalse "a reason"
        nextAction |> String.IsNullOrWhiteSpace |> Expect.isFalse "and what to do"
      | ToolAuthorityGate.Decision.Admitted -> failtest "an Observer was admitted"
      ToolAuthorityGate.decide alice asImplementer "nudge_value" |> Expect.equal "a working member" ToolAuthorityGate.Decision.Admitted

    testCase "WHY - the tool is registered, so tools/list and discovery carry it" <| fun _ ->
      RegisteredTools.describe typeof<SageFsTools> |> List.map (fun t -> t.Name) |> Expect.contains "registered" "nudge_value"
  ]

// ── what the tool says about itself ──

let private nudgeMethod = typeof<SageFsTools>.GetMethod "nudge_value"

let private descriptionOf (attributes: Reflection.ICustomAttributeProvider) : string =
  match attributes.GetCustomAttributes(typeof<System.ComponentModel.DescriptionAttribute>, false) |> Array.tryHead with
  | Some(:? System.ComponentModel.DescriptionAttribute as d) -> d.Description
  | _ -> ""

[<Tests>]
let describedTests =
  testList "nudge_value describes what it does" [
    testCase "WHY - the tool says a file outside the session's projects is refused as NotOwned, and a project file hot reload does not watch is written with the FileNotWatched note, because that is what the code does" <| fun _ ->
      let description = descriptionOf nudgeMethod
      let notOwned = NudgeRefusal.token (NudgeRefusal.NotOwned("x", NotOwnedWhy.NotAmongProjectFiles))
      description |> Expect.stringContains "names the refusal for a file outside the projects" notOwned
      description |> Expect.stringContains "names the note for an unwatched project file" (RunNote.token RunNote.FileNotWatched)

    testCase "WHY - the file parameter does not claim the file must be watched, because an unwatched project file is accepted" <| fun _ ->
      let file = nudgeMethod.GetParameters() |> Array.find (fun p -> p.Name = "file")
      (descriptionOf file).Contains "watches" |> Expect.isFalse "no claim that the file must be one hot reload watches"
  ]
