module SageFs.Tests.McpProjectLoadingUxTests

/// sagefs-roast.md #16 items 1/2/3/4/5/6/7 — the MCP `create_session`/
/// `get_available_projects`/`get_fsi_status` UX defects:
///   #1 create_session applied no path validation at all (McpServer.fs's
///      /api/sessions/create did, via `SessionPathValidation.validateSessionCreateRequest`).
///   #2 `projects=[]` is documented as a bare REPL but the worker still
///      auto-discovers a project sitting directly in the working directory.
///   #3 a bare session id told an agent nothing about what would load.
///   #4 `get_available_projects`'s `working_directory` description claimed
///      session-routing behavior the tool does not have (it only scans a
///      directory).
///   #5 `formatAvailableProjects` printed "Start the daemon with: SageFs"
///      from a call the running daemon just served.
///   #6 create_session silently defaulted an unrecognized workflow to
///      Interactive instead of rejecting it the way switch_workflow does.
///   #7 the MCP duplicate-session pre-check warned on ANY project overlap,
///      disagreeing with the manager's exact-match rule.
open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkflowTypes

module ParseCreateSessionWorkflowTests =

  [<Tests>]
  let tests =
    testList "CreateSessionUx.parseCreateSessionWorkflow" [

      testCase "WHY — a blank workflow means 'not specified' and defaults to Interactive, because that's the documented default for an omitted argument" <| fun _ ->
        CreateSessionUx.parseCreateSessionWorkflow ""
        |> Expect.equal "blank should default to Interactive" (Ok SessionWorkflow.Interactive)

      testCase "WHY — whitespace-only is also treated as blank" <| fun _ ->
        CreateSessionUx.parseCreateSessionWorkflow "   "
        |> Expect.equal "whitespace should default to Interactive" (Ok SessionWorkflow.Interactive)

      testCase "WHY — a recognized alias parses to its workflow" <| fun _ ->
        CreateSessionUx.parseCreateSessionWorkflow "livetesting"
        |> Expect.equal "should parse to LiveTesting" (Ok SessionWorkflow.LiveTesting)

      testCase "WHY — recognized aliases are case-insensitive, matching switch_workflow" <| fun _ ->
        CreateSessionUx.parseCreateSessionWorkflow "INTERACTIVE"
        |> Expect.equal "should parse case-insensitively" (Ok SessionWorkflow.Interactive)

      testCase "WHY — an unrecognized non-blank value is REJECTED, not silently defaulted (Finding #6: this was the actual bug — create_session used to call ofString, which defaults)" <| fun _ ->
        match CreateSessionUx.parseCreateSessionWorkflow "hotreoad" with
        | Error msg ->
          msg |> Expect.stringContains "should name the bad value" "hotreoad"
          msg |> Expect.stringContains "should list valid values" "interactive"
        | Ok w ->
          failwithf "expected a typo to be rejected, got Ok %A" w

      testCase "WHY — the rejection text is EXACTLY formatUnknownWorkflowError's text, so create_session and switch_workflow never drift" <| fun _ ->
        CreateSessionUx.parseCreateSessionWorkflow "bogus"
        |> Expect.equal "should reuse the shared formatter" (Error (CreateSessionUx.formatUnknownWorkflowError "bogus"))
    ]

module FormatUnknownWorkflowErrorTests =

  [<Tests>]
  let tests =
    testList "CreateSessionUx.formatUnknownWorkflowError" [
      testCase "WHY — names the offending value and lists all three valid aliases, matching switch_workflow's own wording" <| fun _ ->
        let msg = CreateSessionUx.formatUnknownWorkflowError "typo123"
        msg |> Expect.stringContains "should echo the bad value" "typo123"
        msg |> Expect.stringContains "should mention interactive" "interactive"
        msg |> Expect.stringContains "should mention livetesting" "livetesting"
        msg |> Expect.stringContains "should mention hotreload" "hotreload"
    ]

module IsExactDuplicateSessionTests =

  [<Tests>]
  let tests =
    testList "CreateSessionUx.isExactDuplicateSession" [

      testCase "WHY — identical working directory and identical sorted project list IS a duplicate, matching the manager's own tryFindDuplicate rule" <| fun _ ->
        CreateSessionUx.isExactDuplicateSession [ "A.fsproj"; "B.fsproj" ] "/repo" [ "B.fsproj"; "A.fsproj" ] "/repo"
        |> Expect.isTrue "sort order should not matter, exact set should match"

      testCase "WHY — a working directory is compared ordinal case-insensitively, matching the manager" <| fun _ ->
        CreateSessionUx.isExactDuplicateSession [ "A.fsproj" ] "/Repo" [ "A.fsproj" ] "/repo"
        |> Expect.isTrue "directory case should not matter"

      testCase "WHY — a SUPERSET request ([A;B] against an existing [A]-only session) is NOT an exact duplicate — the manager would create it (Finding #7's actual bug: the old rule blocked this)" <| fun _ ->
        CreateSessionUx.isExactDuplicateSession [ "A.fsproj"; "B.fsproj" ] "/repo" [ "A.fsproj" ] "/repo"
        |> Expect.isFalse "a partial overlap must not be treated as a duplicate"

      testCase "WHY — a SUBSET request ([A] against an existing [A;B] session) is likewise NOT an exact duplicate" <| fun _ ->
        CreateSessionUx.isExactDuplicateSession [ "A.fsproj" ] "/repo" [ "A.fsproj"; "B.fsproj" ] "/repo"
        |> Expect.isFalse "a partial overlap in the other direction must not be treated as a duplicate either"

      testCase "WHY — the same project set in a DIFFERENT working directory is not a duplicate" <| fun _ ->
        CreateSessionUx.isExactDuplicateSession [ "A.fsproj" ] "/repo-one" [ "A.fsproj" ] "/repo-two"
        |> Expect.isFalse "different working directories must not collide"

      testCase "WHY — disjoint project sets in the same directory are not a duplicate" <| fun _ ->
        CreateSessionUx.isExactDuplicateSession [ "A.fsproj" ] "/repo" [ "B.fsproj" ] "/repo"
        |> Expect.isFalse "disjoint projects must not be treated as a duplicate"

      testCase "WHY — two bare (no-project) requests in the same directory ARE an exact duplicate" <| fun _ ->
        CreateSessionUx.isExactDuplicateSession [] "/repo" [] "/repo"
        |> Expect.isTrue "empty project lists in the same directory are the exact same request"
    ]

module FormatCreateSessionReplyTests =

  [<Tests>]
  let tests =
    testList "CreateSessionUx.formatCreateSessionReply" [

      testCase "WHY — the session id is always present so callers can still extract it" <| fun _ ->
        let reply = CreateSessionUx.formatCreateSessionReply "abcd1234" [ "App.fsproj" ] None
        reply |> Expect.stringContains "should include the session id" "abcd1234"

      testCase "WHY — a non-empty request states exactly what was requested" <| fun _ ->
        let reply = CreateSessionUx.formatCreateSessionReply "abcd1234" [ "App.fsproj"; "Tests.fsproj" ] None
        reply |> Expect.stringContains "should list the requested projects" "App.fsproj, Tests.fsproj"

      testCase "WHY — an empty request (projects=[]) says auto-discovery will run, NOT that the REPL is guaranteed empty (Finding #2/#3)" <| fun _ ->
        let reply = CreateSessionUx.formatCreateSessionReply "abcd1234" [] None
        reply |> Expect.stringContains "should mention auto-discovery" "auto-discover"
        Expect.isFalse "must not claim a guaranteed-empty/no-project REPL" (reply.Contains("no project") || reply.Contains("scratch REPL"))

      testCase "WHY — always points at get_fsi_status to confirm what actually loaded, since the reply cannot know that yet" <| fun _ ->
        let reply = CreateSessionUx.formatCreateSessionReply "abcd1234" [ "App.fsproj" ] None
        reply |> Expect.stringContains "should direct the agent to confirm via get_fsi_status" "get_fsi_status"

      testCase "WHY — a detection hint, when present, is appended rather than dropped" <| fun _ ->
        let reply = CreateSessionUx.formatCreateSessionReply "abcd1234" [ "App.fsproj" ] (Some "💡 some hint")
        reply |> Expect.stringContains "should include the hint" "💡 some hint"

      testCase "WHY — no hint means no stray hint text appears" <| fun _ ->
        let reply = CreateSessionUx.formatCreateSessionReply "abcd1234" [ "App.fsproj" ] None
        Expect.isFalse "should not contain a hint marker with no hint supplied" (reply.Contains("💡"))
    ]

module FormatLoadedProjectsLineTests =

  [<Tests>]
  let tests =
    testList "McpAdapter.formatLoadedProjectsLine" [

      testCase "WHY — an empty resolved-projects list says nothing was resolved yet, distinguishing it from an error" <| fun _ ->
        let line = McpAdapter.formatLoadedProjectsLine []
        line |> Expect.stringContains "should explain nothing resolved yet" "none resolved"

      testCase "WHY — resolved projects are shown by file name, not full path (matches the existing 'Projects:' field's style)" <| fun _ ->
        let roles : SageFs.ProjectLoading.ClassifiedProject list =
          [ { Path = "/repo/src/App.fsproj"; Role = SageFs.ProjectLoading.ProjectRole.Executable; PackageRefs = [] }
            { Path = "/repo/tests/App.Tests.fsproj"; Role = SageFs.ProjectLoading.ProjectRole.Test; PackageRefs = [] } ]
        let line = McpAdapter.formatLoadedProjectsLine roles
        line |> Expect.equal "should list both file names" "App.fsproj, App.Tests.fsproj"
    ]

module FormatAvailableProjectsHintTests =

  [<Tests>]
  let tests =
    testList "McpAdapter.formatAvailableProjects" [

      testCase "WHY — never claims the daemon needs starting, since this text is emitted BY the running daemon serving the call (Finding #5)" <| fun _ ->
        let text = McpAdapter.formatAvailableProjects "/repo" [| "App.fsproj" |] [||] 0
        Expect.isFalse "must not tell the agent to start what is already running" (text.Contains("Start the daemon"))

      testCase "WHY — still points at create_session so the discovery step leads somewhere" <| fun _ ->
        let text = McpAdapter.formatAvailableProjects "/repo" [| "App.fsproj" |] [||] 0
        text |> Expect.stringContains "should still mention create_session" "create_session"
    ]
