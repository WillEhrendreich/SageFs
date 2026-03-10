module SageFs.Tests.McpWireProtocolTests

/// Tests that pin the MCP wire protocol surface: tool registration
/// completeness, SessionEvent JSON shapes, and SSE frame format.
/// Build wire protocol tests next to PluginContractTests (per expert consensus).

open System
open System.Reflection
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WarmUp
open SageFs.SessionEvents

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
  test "all expected tools are registered via [<McpServerTool>]" {
    let expected = set [
      "cancel_eval"
      "check_fsharp_code"
      "coverage_intel"
      "create_session"
      "decompose_pipeline"
      "diagnose"
      "disable_live_testing"
      "enable_live_testing"
      "explain_test_failure"
      "explain_test_run"
      "export_notebook"
      "export_session_transcript"
      "explore_namespace"
      "explore_type"
      "get_available_projects"
      "get_completions"
      "get_elm_state"
      "get_eval_diff"
      "get_eval_timeline"
      "get_file_coverage"
      "get_fsi_status"
      "get_live_test_status"
      "get_message_journal"
      "get_session_filmstrip"
      "get_test_trace"
      "get_recent_fsi_events"
      "get_startup_info"
      "hard_reset_fsi_session"
      "impact_forecast"
      "list_sessions"
      "load_fsharp_script"
      "manage_scratch_pad"
      "plan_ripple"
      "preview_what_if"
      "query_test_coverage"
      "reset_fsi_session"
      "run_tests"
      "send_fsharp_code"
      "set_run_policy"
      "set_test_timeouts"
      "stop_session"
      "suggest_next_action"
      "suggest_next_cell"
      "switch_session"
      "visualize_domain_model"
    ]
    let actual =
      findMcpToolMethods ()
      |> Array.map (fun m -> m.Name)
      |> set
    actual |> Expect.equal "registered tools match expected set" expected
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
    PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 0L }
    StartedAt = DateTimeOffset.UtcNow }

[<Tests>]
let sessionEventSerializationTests = testList "SessionEvent serialization" [
  test "WarmupContextSnapshot has correct type and sessionId" {
    let evt = WarmupContextSnapshot("sess-1", { minimalWarmup with SourceFilesScanned = 5; PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 100L } })
    let json = serializeSessionEvent evt
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
        NamespacesOpened = [{ Name = "System"; IsModule = false; Source = "auto"; DurationMs = 0.0 }]
        FailedOpens = [{ Name = "Bad"; IsModule = false; ErrorMessage = "err"; Diagnostics = []; RetryCount = 1; DurationMs = 0.0 }]
    })
    let json = serializeSessionEvent evt
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
    let json = serializeSessionEvent (HotReloadSnapshot("s1", ["a.fs"; "b.fs"]))
    expectJsonField json "type" "hotreload_snapshot"
    let doc = JsonDocument.Parse(json)
    let files = doc.RootElement.GetProperty("watchedFiles")
    files.GetArrayLength() |> Expect.equal "2 files" 2
    files.[0].GetString() |> Expect.equal "first file" "a.fs"
  }

  test "HotReloadFileToggled serializes toggle state" {
    let json = serializeSessionEvent (HotReloadFileToggled("s1", "x.fs", false))
    expectJsonField json "type" "hotreload_file_toggled"
    expectJsonField json "file" "x.fs"
    let doc = JsonDocument.Parse(json)
    doc.RootElement.GetProperty("watched").GetBoolean()
    |> Expect.isFalse "not watched"
  }

  test "SessionActivated has type and sessionId" {
    let json = serializeSessionEvent (SessionActivated "abc")
    expectJsonField json "type" "session_activated"
    expectJsonField json "sessionId" "abc"
  }

  test "SessionCreated serializes project names" {
    let json = serializeSessionEvent (SessionCreated("s1", ["P1"; "P2"]))
    expectJsonField json "type" "session_created"
    let doc = JsonDocument.Parse(json)
    let projects = doc.RootElement.GetProperty("projectNames")
    projects.GetArrayLength() |> Expect.equal "2 projects" 2
    projects.[1].GetString() |> Expect.equal "second project" "P2"
  }

  test "SessionStopped has type and sessionId" {
    let json = serializeSessionEvent (SessionStopped "s-dead")
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
      let json = serializeSessionEvent evt
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("type").GetString()
      |> Expect.isNotNull "type field exists"
  }
]
