module SageFs.Tests.McpToolErrorBodyTests

/// sagefs-roast.md Finding #2: MCP tool bodies returned prose, not
/// structured errors. `withEchoOutcome`/`withEchoOutcomeNoAwaitRecord`'s
/// `Some err` outcome used to be returned to the client as ordinary,
/// non-`IsError` success text — an agent could not even reliably detect
/// the failure, let alone act on a stable case token. These tests prove:
/// (1) `structuredToolErrorResult` (McpServer.fs) — pure, given a
///     `SageFsError` — builds a `CallToolResult` with `IsError = true` and a
///     `StructuredContent` block whose JSON carries case/message/
///     suggestedAction from `SageFsError.toJson`.
/// (2) `withEchoOutcome`/`withEchoOutcomeNoAwaitRecord` (McpTools.fs) now
///     raise a `SageFsErrorException` carrying the exact typed error when
///     the tool body reports `Some err`, instead of swallowing it into a
///     plain "Error: ..." success string.
///
/// Finding #13: the MCP `/api/sessions/create` route fed `workingDirectory`/
/// `projects` straight into session creation with no path-safety check at
/// all, unlike the dashboard's own `resolveSessionProjects`.
/// `validateSessionCreateRequest` (McpServer.fs) — also pure aside from
/// filesystem probes — closes that gap.
module StructuredToolErrorResultTests =
  open System
  open System.Text.Json
  open Expecto
  open Expecto.Flip
  open SageFs
  open SageFs.Server.McpServer

  [<Tests>]
  let tests =
    testList "structuredToolErrorResult" [

      test "sets IsError to true" {
        let err = SageFsError.SessionNotFound "sess-123"
        let result = structuredToolErrorResult err
        result.IsError.HasValue
        |> Expect.isTrue "IsError should be set"
        result.IsError.Value
        |> Expect.isTrue "IsError should be true"
      }

      test "content includes the human-readable describeForAgent text" {
        let err = SageFsError.SessionNotFound "sess-123"
        let result = structuredToolErrorResult err
        result.Content.Count
        |> fun c -> (c, 0)
        |> Expect.isGreaterThan "should have at least one content block"
        let textBlock = result.Content.[0] :?> ModelContextProtocol.Protocol.TextContentBlock
        textBlock.Text
        |> Expect.equal "text should be the agent-facing description" (SageFsError.describeForAgent err)
      }

      test "StructuredContent parses to case, message, and suggestedAction matching the algebra" {
        let err = SageFsError.WorkerTimeout("sess-abc", "eval", 30.0)
        let result = structuredToolErrorResult err
        result.StructuredContent.HasValue
        |> Expect.isTrue "StructuredContent should be set"
        let root = result.StructuredContent.Value
        root.GetProperty("case").GetString()
        |> Expect.equal "case should match the DU case name" "WorkerTimeout"
        root.GetProperty("message").GetString()
        |> Expect.equal "message should match describe" (SageFsError.describe err)
        root.GetProperty("suggestedAction").GetString()
        |> Expect.equal "suggestedAction should match the algebra" (SageFsError.suggestedAction err)
        root.GetProperty("fields").GetProperty("sessionId").GetString()
        |> Expect.equal "fields should carry the case's own data" "sess-abc"
      }

      test "different error cases produce different structured case tokens" {
        let a = structuredToolErrorResult SageFsError.NoActiveSessions
        let b = structuredToolErrorResult (SageFsError.DaemonNotRunning)
        a.StructuredContent.Value.GetProperty("case").GetString()
        |> Expect.notEqual "cases should differ"
             (b.StructuredContent.Value.GetProperty("case").GetString())
      }
    ]

module ValidateSessionCreateRequestTests =
  open System
  open System.IO
  open Expecto
  open Expecto.Flip
  open SageFs
  open SageFs.Server.McpServer

  let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally
      try Directory.Delete(dir, true) with _ -> ()

  [<Tests>]
  let tests =
    testList "validateSessionCreateRequest" [

      test "rejects a non-existent working directory with UnsafeSessionPath" {
        let missing = Path.Combine(Path.GetTempPath(), "sagefs-does-not-exist-" + Guid.NewGuid().ToString("N"))
        match validateSessionCreateRequest missing [] with
        | Error (SageFsError.UnsafeSessionPath(path, _)) ->
          path |> Expect.equal "path should be the offending working directory" missing
        | Error other ->
          failwithf "expected UnsafeSessionPath, got %A" other
        | Ok () ->
          failwith "expected rejection of a non-existent directory"
      }

      test "rejects an empty working directory with UnsafeSessionPath" {
        match validateSessionCreateRequest "" [] with
        | Error (SageFsError.UnsafeSessionPath _) -> ()
        | other -> failwithf "expected UnsafeSessionPath, got %A" other
      }

      // `/` passed "exists and not UNC", so a session could watch every file on the machine, and every later containment
      // check (load-script, eval-file) was relative to a root that contains everything.
      test "WHY - refuses the filesystem root, with the reason and where to start instead" {
        let root = Path.GetPathRoot(Path.GetTempPath())
        match validateSessionCreateRequest root [] with
        | Error (SageFsError.UnsafeSessionPath(path, reason)) ->
          path |> Expect.equal "names the root" root
          reason |> Expect.stringContains "says why" "filesystem root"
          reason |> Expect.stringContains "says what to do" "project's own directory"
        | other -> failwithf "expected UnsafeSessionPath, got %A" other
      }

      test "WHY - the home directory itself gets a warning, because its watchers cover every file the person has" {
        withTempDir (fun home ->
          SageFs.SessionPathValidation.noticeFor (SageFs.SessionPathValidation.HomeDir home) home
          |> Expect.equal "home itself" (SageFs.SessionPathValidation.SessionRootNotice.HomeDirectory home)
          let project = Path.Combine(home, "proj")
          Directory.CreateDirectory project |> ignore
          SageFs.SessionPathValidation.noticeFor (SageFs.SessionPathValidation.HomeDir home) project
          |> Expect.equal "a project under home is ordinary" SageFs.SessionPathValidation.SessionRootNotice.NoNotice
          SageFs.SessionPathValidation.SessionRootNotice.describe (SageFs.SessionPathValidation.SessionRootNotice.HomeDirectory home)
          |> Option.defaultValue ""
          |> Expect.stringContains "names the cost" "watch")
      }

      test "WHY - a session created in the home directory says in its reply that it gets no file events, because the notice used to be computed and printed nowhere" {
        withTempDir (fun home ->
          let reply = "Session 'abcd1234' created."
          let warned = SageFs.SessionPathValidation.SessionRootNotice.appendTo (SageFs.SessionPathValidation.noticeFor (SageFs.SessionPathValidation.HomeDir home) home) reply
          warned |> Expect.stringStarts "the reply itself comes first" reply
          warned |> Expect.stringContains "names the directory" home
          warned |> Expect.stringContains "says what the person loses" "no file events"
          let project = Path.Combine(home, "proj")
          Directory.CreateDirectory project |> ignore
          SageFs.SessionPathValidation.SessionRootNotice.appendTo (SageFs.SessionPathValidation.noticeFor (SageFs.SessionPathValidation.HomeDir home) project) reply
          |> Expect.equal "a project directory gets the reply alone" reply)
      }

      test "rejects a UNC-style working directory with UnsafeSessionPath" {
        match validateSessionCreateRequest @"\\attacker-host\share" [] with
        | Error (SageFsError.UnsafeSessionPath(_, reason)) ->
          reason |> Expect.stringContains "reason should mention UNC" "UNC"
        | other -> failwithf "expected UnsafeSessionPath, got %A" other
      }

      test "rejects a project path that escapes the working directory via .." {
        withTempDir (fun dir ->
          match validateSessionCreateRequest dir [ "../../etc/passwd" ] with
          | Error (SageFsError.UnsafeSessionPath _) -> ()
          | other -> failwithf "expected UnsafeSessionPath, got %A" other)
      }

      test "rejects an absolute project path outside the working directory" {
        withTempDir (fun dir ->
          let outside = Path.Combine(Path.GetTempPath(), "sagefs-outside-" + Guid.NewGuid().ToString("N"))
          Directory.CreateDirectory(outside) |> ignore
          try
            match validateSessionCreateRequest dir [ outside ] with
            | Error (SageFsError.UnsafeSessionPath(path, _)) ->
              path |> Expect.equal "should name the escaping path" outside
            | other -> failwithf "expected UnsafeSessionPath, got %A" other
          finally
            try Directory.Delete(outside, true) with _ -> ())
      }

      test "accepts an existing working directory with no projects" {
        withTempDir (fun dir ->
          validateSessionCreateRequest dir []
          |> Expect.isOk "an existing directory with no projects should be valid")
      }

      test "accepts a project path contained within the working directory" {
        withTempDir (fun dir ->
          let projFile = Path.Combine(dir, "App.fsproj")
          File.WriteAllText(projFile, "<Project />")
          validateSessionCreateRequest dir [ "App.fsproj" ]
          |> Expect.isOk "a project path inside the working directory should be valid")
      }

      test "accepts a contained project path that does not exist on disk (no throw)" {
        // The daemon resolves/locates projects itself, so a project NAME need
        // not exist at Combine(workingDir, name). The containment gate must
        // canonicalize such a path and accept it (it is inside workingDir) —
        // NOT throw. Regression guard: resolveRealSessionPath used to call
        // ResolveLinkTarget on the non-existent path, throwing
        // DirectoryNotFoundException, which surfaced as a spurious HTTP 500 from
        // /api/sessions/create (integration-host buffer-changed test).
        withTempDir (fun dir ->
          validateSessionCreateRequest dir [ "DoesNotExist.fsproj" ]
          |> Expect.isOk "a contained-but-missing project path must validate, not throw")
      }
    ]

module WithEchoOutcomeRaisesStructuredErrorTests =
  open System.Threading.Tasks
  open Expecto
  open Expecto.Flip
  open SageFs
  open SageFs.Server.McpTools
  open SageFs.Tests.TestInfrastructure

  [<Tests>]
  let tests =
    testList "withEchoOutcome raises SageFsErrorException on a reported blocker" [

      testTask "a Some err outcome is raised as SageFsErrorException, not returned as success text" {
        let ctx = sharedCtx ()
        let err = SageFsError.SessionNotFound "sess-xyz"
        let body : Task<string * SageFsError option> =
          Task.FromResult("Error: session not found", Some err)
        let! outcome =
          task {
            try
              let! _ = withEchoOutcome ctx "test_tool_finding2" body
              return Ok ()
            with
            | :? SageFsErrorException as sfEx -> return Error sfEx.Error
          }
        match outcome with
        | Error raisedErr ->
          raisedErr |> Expect.equal "the raised error should be the exact SageFsError the tool body reported" err
        | Ok () ->
          failwith "expected withEchoOutcome to raise SageFsErrorException for a reported blocker"
      }

      testTask "a None outcome still returns the tool's text normally" {
        let ctx = sharedCtx ()
        let body : Task<string * SageFsError option> =
          Task.FromResult("all good", None)
        let! result = withEchoOutcome ctx "test_tool_finding2_ok" body
        result |> Expect.equal "clean completions still return their text" "all good"
      }
    ]
