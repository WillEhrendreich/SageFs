// TDD script for SSE Session Events
// Phase 1: Types, serialization, wire format
open System.Text.Json
open Expecto

// ═══════════════════════════════════════════════════════════════
// SessionEvent DU
// ═══════════════════════════════════════════════════════════════

type SessionEvent =
  | WarmupContextSnapshot of sessionId: string * context: SageFs.WarmupContext
  | HotReloadSnapshot of sessionId: string * watchedFiles: string list
  | HotReloadFileToggled of sessionId: string * file: string * watched: bool
  | SessionActivated of sessionId: string
  | SessionCreated of sessionId: string * projectNames: string list
  | SessionStopped of sessionId: string

let sessionEventType = "session"

module SessionEventSubtype =
  let warmupContextSnapshot = "warmup_context_snapshot"
  let hotReloadSnapshot = "hotreload_snapshot"
  let hotReloadFileToggled = "hotreload_file_toggled"
  let sessionActivated = "session_activated"
  let sessionCreated = "session_created"
  let sessionStopped = "session_stopped"

// ═══════════════════════════════════════════════════════════════
// Serialization
// ═══════════════════════════════════════════════════════════════

let serializeSessionEvent (opts: JsonSerializerOptions) (evt: SessionEvent) : string =
  match evt with
  | WarmupContextSnapshot (sid, ctx) ->
    let ctxJson = JsonSerializer.Serialize(ctx, opts)
    sprintf """{"type":"%s","sessionId":"%s","context":%s}"""
      SessionEventSubtype.warmupContextSnapshot sid ctxJson
  | HotReloadSnapshot (sid, files) ->
    let filesJson = JsonSerializer.Serialize(files, opts)
    sprintf """{"type":"%s","sessionId":"%s","watchedFiles":%s}"""
      SessionEventSubtype.hotReloadSnapshot sid filesJson
  | HotReloadFileToggled (sid, file, watched) ->
    sprintf """{"type":"%s","sessionId":"%s","file":"%s","watched":%s}"""
      SessionEventSubtype.hotReloadFileToggled sid file (if watched then "true" else "false")
  | SessionActivated sid ->
    sprintf """{"type":"%s","sessionId":"%s"}"""
      SessionEventSubtype.sessionActivated sid
  | SessionCreated (sid, projects) ->
    let projJson = JsonSerializer.Serialize(projects, opts)
    sprintf """{"type":"%s","sessionId":"%s","projectNames":%s}"""
      SessionEventSubtype.sessionCreated sid projJson
  | SessionStopped sid ->
    sprintf """{"type":"%s","sessionId":"%s"}"""
      SessionEventSubtype.sessionStopped sid

let formatSessionSseEvent (opts: JsonSerializerOptions) (evt: SessionEvent) : string =
  let json = serializeSessionEvent opts evt
  SageFs.SseWriter.formatSseEvent sessionEventType json

// ═══════════════════════════════════════════════════════════════
// HotReload → SessionEvent bridge (pure function)
// ═══════════════════════════════════════════════════════════════

/// Build the snapshot event from current hotreload state
let hotReloadToSnapshot (sessionId: string) (state: SageFs.HotReloadState.T) : SessionEvent =
  HotReloadSnapshot (sessionId, state.Watched |> Set.toList |> List.sort)

/// Build toggle event after a hotreload toggle
let hotReloadToggleEvent (sessionId: string) (file: string) (state: SageFs.HotReloadState.T) : SessionEvent =
  HotReloadFileToggled (sessionId, file, SageFs.HotReloadState.isWatched file state)

// ═══════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════

let jsonOpts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let sessionEventTests = testList "SessionEvent" [

  testList "serialization" [
    test "WarmupContextSnapshot JSON" {
      let ctx : SageFs.WarmupContext = {
        SourceFilesScanned = 5; AssembliesLoaded = []; NamespacesOpened = []
        FailedOpens = []; WarmupDurationMs = 1234L
        StartedAt = System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero)
      }
      let json = serializeSessionEvent jsonOpts (WarmupContextSnapshot ("s1", ctx))
      Expect.stringContains json "\"type\":\"warmup_context_snapshot\"" "type"
      Expect.stringContains json "\"sessionId\":\"s1\"" "sessionId"
      Expect.stringContains json "\"sourceFilesScanned\":5" "sourceFiles"
      Expect.stringContains json "\"warmupDurationMs\":1234" "duration"
    }
    test "HotReloadSnapshot JSON" {
      let json = serializeSessionEvent jsonOpts (HotReloadSnapshot ("s2", ["a.fs"; "b.fs"]))
      Expect.stringContains json "\"type\":\"hotreload_snapshot\"" "type"
      Expect.stringContains json "\"watchedFiles\":[\"a.fs\",\"b.fs\"]" "files"
    }
    test "HotReloadFileToggled watched=true" {
      let json = serializeSessionEvent jsonOpts (HotReloadFileToggled ("s3", "x.fs", true))
      Expect.stringContains json "\"watched\":true" "watched"
    }
    test "HotReloadFileToggled watched=false" {
      let json = serializeSessionEvent jsonOpts (HotReloadFileToggled ("s3", "x.fs", false))
      Expect.stringContains json "\"watched\":false" "watched"
    }
    test "SessionActivated" {
      let json = serializeSessionEvent jsonOpts (SessionActivated "s4")
      Expect.stringContains json "\"type\":\"session_activated\"" "type"
    }
    test "SessionCreated" {
      let json = serializeSessionEvent jsonOpts (SessionCreated ("s5", ["A.fsproj"]))
      Expect.stringContains json "\"projectNames\":[\"A.fsproj\"]" "projects"
    }
    test "SessionStopped" {
      let json = serializeSessionEvent jsonOpts (SessionStopped "s6")
      Expect.stringContains json "\"type\":\"session_stopped\"" "type"
    }
  ]

  testList "SSE wire format" [
    test "event: session header" {
      let sse = formatSessionSseEvent jsonOpts (SessionActivated "s1")
      Expect.stringContains sse "event: session\n" "event line"
    }
    test "data: prefix" {
      let sse = formatSessionSseEvent jsonOpts (SessionActivated "s1")
      Expect.stringContains sse "data: {" "data line"
    }
    test "ends with \\n\\n" {
      let sse = formatSessionSseEvent jsonOpts (SessionActivated "s1")
      Expect.isTrue (sse.EndsWith("\n\n")) "trailing newlines"
    }
    test "payload parses as JSON" {
      let sse = formatSessionSseEvent jsonOpts (HotReloadSnapshot ("s2", ["f.fs"]))
      let dl = sse.Split('\n') |> Array.find (fun l -> l.StartsWith("data:"))
      let j = dl.Substring(6)
      let d = JsonDocument.Parse(j)
      Expect.equal (d.RootElement.GetProperty("type").GetString()) "hotreload_snapshot" "type"
      Expect.equal (d.RootElement.GetProperty("sessionId").GetString()) "s2" "sid"
    }
  ]

  testList "HotReload bridge" [
    test "hotReloadToSnapshot produces sorted file list" {
      let state = SageFs.HotReloadState.empty
                  |> SageFs.HotReloadState.watch "z.fs"
                  |> SageFs.HotReloadState.watch "a.fs"
      match hotReloadToSnapshot "s1" state with
      | HotReloadSnapshot (sid, files) ->
        Expect.equal sid "s1" "sessionId"
        Expect.equal files ["a.fs"; "z.fs"] "sorted files"
      | _ -> failtest "wrong case"
    }
    test "hotReloadToggleEvent after watch" {
      let state = SageFs.HotReloadState.empty |> SageFs.HotReloadState.watch "x.fs"
      match hotReloadToggleEvent "s1" "x.fs" state with
      | HotReloadFileToggled (_, _, watched) ->
        Expect.isTrue watched "should be watched"
      | _ -> failtest "wrong case"
    }
    test "hotReloadToggleEvent after unwatch" {
      let state = SageFs.HotReloadState.empty
      match hotReloadToggleEvent "s1" "x.fs" state with
      | HotReloadFileToggled (_, _, watched) ->
        Expect.isFalse watched "should not be watched"
      | _ -> failtest "wrong case"
    }
  ]
]

let exitCode = Expecto.Tests.runTestsWithCLIArgs [] [||] sessionEventTests
printfn "\n=== EXIT CODE: %d ===" exitCode
