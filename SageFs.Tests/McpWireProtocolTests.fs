module SageFs.Tests.McpWireProtocolTests

/// Tests that pin the MCP wire protocol surface: tool registration
/// completeness, SseEvent JSON shapes, and SSE frame format.
/// Build wire protocol tests next to PluginContractTests (per expert consensus).

open System
open System.Reflection
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WarmUp
open SageFs.Server

// ─── Tool Registration ────────────────────────────────────────────

let private findMcpToolMethods () =
  let sageFsAsm =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.find (fun a -> a.GetName().Name = "SageFs")
  let mcpAttrType =
    sageFsAsm.GetReferencedAssemblies()
    |> Array.collect (fun an ->
      try
        let a = Assembly.Load(an)
        a.GetTypes()
        |> Array.filter (fun t -> t.Name = "McpServerToolAttribute")
      with _ -> [||])
    |> Array.head
  sageFsAsm.GetTypes()
  |> Array.collect (fun t ->
    t.GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly))
  |> Array.filter (fun m -> m.GetCustomAttributes(mcpAttrType, false).Length > 0)

[<Tests>]
let mcpToolRegistrationTests = testList "MCP tool registration" [
  test "the registered tools are exactly the closed set of tool names" {
    // The expected side is the ToolName union (SageFs.Core/Affordances.fs), which the gate, the routing kinds and the
    // tool classes are all keyed on. A hand-kept list here went stale the moment a tool landed (list_members), so the
    // two independent declarations are compared instead: a tool added to one and not the other fails here.
    let expected = SageFs.Affordances.ToolName.allToolNames |> set
    let actual =
      findMcpToolMethods ()
      |> Array.map (fun m -> m.Name)
      |> set
    actual |> Expect.equal "registered tools are the closed set" expected
  }

  test "no tool has an empty description" {
    let descAttrType = typeof<System.ComponentModel.DescriptionAttribute>
    let methods = findMcpToolMethods ()
    for m in methods do
      let desc =
        m.GetCustomAttributes(descAttrType, false)
        |> Array.tryHead
        |> Option.map (fun a -> (a :?> System.ComponentModel.DescriptionAttribute).Description)
      match desc with
      | Some d when String.IsNullOrWhiteSpace(d) ->
        failwithf "Tool %s has empty description" m.Name
      | None ->
        failwithf "Tool %s has no [<Description>] attribute" m.Name
      | _ -> ()
  }
]

// ─── SessionEvent Serialization ───────────────────────────────────

let private expectJsonField (json: string) (field: string) (expected: string) =
  let doc = JsonDocument.Parse(json)
  let actual = doc.RootElement.GetProperty(field).GetString()
  actual |> Expect.equal (sprintf "field '%s'" field) expected

let private minimalWarmup : WarmupContext =
  { SourceFilesScanned = 0
    AssembliesLoaded = []
    NamespacesOpened = []
    FailedOpens = []
    PhaseTiming = FixtureDurations.warmupTotalOnly FixtureDurations.unmeasuredMs
    StartedAt = DateTimeOffset.UtcNow }

[<Tests>]
let sessionEventSerializationTests = testList "SessionEvent serialization" [
  test "WarmupContextSnapshot has correct type and sessionId" {
    let evt = WarmupContextSnapshot("sess-1", { minimalWarmup with SourceFilesScanned = 5; PhaseTiming = FixtureDurations.warmupTotalOnly 100L })
    let json = SseEvent.toJson evt
    expectJsonField json "type" "warmup_context_snapshot"
    expectJsonField json "sessionId" "sess-1"
    let doc = JsonDocument.Parse(json)
    let ctx = doc.RootElement.GetProperty("context")
    ctx.GetProperty("sourceFilesScanned").GetInt32()
    |> Expect.equal "files scanned" 5
    ctx.GetProperty("warmupDurationMs").GetInt64()
    |> Expect.equal "duration" 100L
  }

  test "WarmupContextSnapshot serializes assemblies and namespaces" {
    let evt = WarmupContextSnapshot("s", {
      minimalWarmup with
        AssembliesLoaded = [{ Name = "A"; Path = "/a.dll"; NamespaceCount = 10; ModuleCount = 3 }]
        NamespacesOpened = [{ Name = "System"; Kind = OpenableKind.Namespace; Source = "auto"; DurationMs = FixtureDurations.instantOpenMs }]
        FailedOpens = [{ Name = "Bad"; Kind = OpenableKind.Namespace; ErrorMessage = "err"; Diagnostics = []; RetryCount = 1; DurationMs = FixtureDurations.instantOpenMs }]
    })
    let json = SseEvent.toJson evt
    let doc = JsonDocument.Parse(json)
    let ctx = doc.RootElement.GetProperty("context")
    let asms = ctx.GetProperty("assembliesLoaded")
    asms.GetArrayLength() |> Expect.equal "1 assembly" 1
    asms.[0].GetProperty("name").GetString() |> Expect.equal "name" "A"
    asms.[0].GetProperty("namespaceCount").GetInt32() |> Expect.equal "ns count" 10
    let nss = ctx.GetProperty("namespacesOpened")
    nss.GetArrayLength() |> Expect.equal "1 namespace" 1
    nss.[0].GetProperty("isModule").GetBoolean() |> Expect.isFalse "not module"
    let fails = ctx.GetProperty("failedOpens")
    fails.GetArrayLength() |> Expect.equal "1 failed" 1
    fails.[0].GetProperty("name").GetString() |> Expect.equal "fail name" "Bad"
  }

  test "HotReloadSnapshot serializes watched files" {
    let json = SseEvent.toJson (HotReloadSnapshot("s1", ["a.fs"; "b.fs"]))
    expectJsonField json "type" "hotreload_snapshot"
    let doc = JsonDocument.Parse(json)
    let files = doc.RootElement.GetProperty("watchedFiles")
    files.GetArrayLength() |> Expect.equal "2 files" 2
    files.[0].GetString() |> Expect.equal "first file" "a.fs"
  }

  test "HotReloadFileToggled serializes toggle state" {
    let json = SseEvent.toJson (HotReloadFileToggled("s1", "x.fs", false))
    expectJsonField json "type" "hotreload_file_toggled"
    expectJsonField json "file" "x.fs"
    let doc = JsonDocument.Parse(json)
    doc.RootElement.GetProperty("watched").GetBoolean()
    |> Expect.isFalse "not watched"
  }

  test "SessionActivated has type and sessionId" {
    let json = SseEvent.toJson (SessionActivated "abc")
    expectJsonField json "type" "session_activated"
    expectJsonField json "sessionId" "abc"
  }

  test "SessionCreated serializes project names" {
    let json = SseEvent.toJson (SessionCreated("s1", ["P1"; "P2"]))
    expectJsonField json "type" "session_created"
    let doc = JsonDocument.Parse(json)
    let projects = doc.RootElement.GetProperty("projectNames")
    projects.GetArrayLength() |> Expect.equal "2 projects" 2
    projects.[1].GetString() |> Expect.equal "second project" "P2"
  }

  test "SessionStopped has type and sessionId" {
    let json = SseEvent.toJson (SessionStopped "s-dead")
    expectJsonField json "type" "session_stopped"
    expectJsonField json "sessionId" "s-dead"
  }

  test "every SessionEvent variant produces valid JSON with type field" {
    let events = [
      WarmupContextSnapshot("s", minimalWarmup)
      HotReloadSnapshot("s", [])
      HotReloadFileToggled("s", "f", true)
      SessionActivated "s"
      SessionCreated("s", [])
      SessionStopped "s"
    ]
    for evt in events do
      let json = SseEvent.toJson evt
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("type").GetString()
      |> Expect.isNotNull "type field exists"
  }
]

// ─── The daemon's event echo ──────────────────────────────────────

let private textBlocks (result: ModelContextProtocol.Protocol.CallToolResult) : string list =
  [ for block in result.Content do
      match block with
      | :? ModelContextProtocol.Protocol.TextContentBlock as text -> yield text.Text
      | _ -> () ]

let private answering (answer: string) : ModelContextProtocol.Protocol.CallToolResult =
  let result = ModelContextProtocol.Protocol.CallToolResult()
  result.Content.Add(ModelContextProtocol.Protocol.TextContentBlock(Text = answer))
  result

[<Tests>]
let eventEchoTests = testList "MCP tool reply and the daemon's event echo" [
  test "the echo is its own content block after the answer, so a JSON answer in the first block is valid JSON" {
    let answer = """{"outcome":"Written","after":"12.5","notes":[]}"""
    let echoed = McpServer.withEventEcho [| "✓ warmup complete"; "state: output=10 diags=0" |] (answering answer)
    match textBlocks echoed with
    | [ first; echo ] ->
      first |> Expect.equal "the answer is exactly what the tool said" answer
      JsonDocument.Parse(first).RootElement.GetProperty("outcome").GetString() |> Expect.equal "it parses on its own" "Written"
      echo |> Expect.stringContains "the echo says what it is" "SageFs events since last call"
      echo |> Expect.stringContains "and carries every event" "warmup complete"
      echo |> Expect.stringContains "all of them" "state: output=10 diags=0"
    | other -> failtestf "expected the answer and one echo block, got %d blocks: %A" other.Length other
  }

  test "no events leaves the tool's result as it was: one block, no echo" {
    let echoed = McpServer.withEventEcho [||] (answering "{}")
    textBlocks echoed |> Expect.equal "just the answer" [ "{}" ]
  }

  test "the echo keeps every block the tool made, in order, and only adds one after them" {
    let result = answering "answer"
    result.Content.Add(ModelContextProtocol.Protocol.TextContentBlock(Text = "second"))
    let echoed = McpServer.withEventEcho [| "one" |] result
    match textBlocks echoed with
    | [ "answer"; "second"; echo ] -> echo |> Expect.stringContains "added last" "one"
    | other -> failtestf "expected the two blocks the tool made and then the echo, got %A" other
  }
]
