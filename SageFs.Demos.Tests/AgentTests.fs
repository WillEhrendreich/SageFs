/// RED-first proof for the Agent/MCP actor (demo-actors-plan.md §2.4): the
/// `agent-mcp` scenario is a real three-tool MCP exchange (never a bare
/// Quick Start session, §10), the wire-encoding convention
/// `Actors/Agent.fs` and `Scenarios.Agent.fs` share round-trips correctly,
/// the cell-agent dispatch seam actually routes the `"agent"` wire token to
/// this actor, and the arguments this actor sends the daemon are real,
/// resolved paths under a real repo root — never the literal
/// `{{REPO_ROOT}}` token (`Runtime.fs`'s substitution never reaches this
/// actor's wire channel — see `Actors/Agent.fs`'s own doc comment for why).
/// No cell/Xvfb/Chromium/daemon is spawned here, mirroring
/// `CellAgentTests.fs`'s own "prove the wiring without a live browser"
/// doctrine: a `Handle` built with `Unchecked.defaultof<IPage>` is a safe,
/// honest way to exercise `Observe`'s parsing/fail-closed paths, since a
/// malformed wire selector must return `false` before ever touching the
/// (nonexistent) `Page` or the network.
module SageFs.Demos.Tests.AgentTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.Actors

[<Tests>]
let tests =
  testList "Agent/MCP actor" [

    testCase "agent-mcp is registered, Agent-cliented, DashboardOnly-laid-out, and opens the real webapp sample (never a bare Quick Start session, §10)" <| fun _ ->
      let scenario = SageFs.Demos.Scenarios.Agent.agentMcp
      scenario.Id |> ScenarioId.value |> Expect.equal "scenario id (the CLI verb: record agent-mcp)" "agent-mcp"
      scenario.Client |> Expect.equal "client" Client.Agent
      scenario.Capability |> Expect.equal "capability" Capability.Agent
      scenario.Layout |> Expect.equal "layout (no editor/app pane for an MCP-only demo)" LayoutTemplate.DashboardOnly
      scenario.Sample |> Expect.equal "opens the real webapp sample, not a bare session" Sample.WebappDatastar
      scenario.Steps
      |> List.length
      |> Expect.equal "three real tool calls: create_project_session, get_session_status, send_fsharp_code" 3

    testCase "WHY — the agent-mcp demo calls only tools that are actually registered, because a retired name on the wire makes the demo unreproducible" <| fun _ ->
      SageFs.Demos.Scenarios.Agent.agentMcp.Steps
      |> List.iter (fun s ->
        match s.Expect with
        | Expectation.PageTextContains(selector, _) ->
          let wire = Agent.parseWire (selector + ":has-text(\"\")")
          match wire with
          | Some (_, tool) ->
            match McpTool.tryParseCurrent tool with
            | Some _ -> ()
            | None ->
              failtestf
                "the agent-mcp scenario put '%s' on the wire, which is not a live SageFs MCP tool (retired: %A)"
                tool
                McpTool.retiredValues
          | None -> ()
        | other -> failtestf "expected PageTextContains, got %A" other)

    testCase "every step's Expectation packs a real MCP tool name onto the wire, in order, never a canned expected string" <| fun _ ->
      let expectedTools =
        [ CurrentMcpTool.CreateProjectSession
          CurrentMcpTool.GetSessionStatus
          CurrentMcpTool.SendFsharpCode ]
        |> List.map (fun tool -> McpTool.value (McpTool.Current tool))

      SageFs.Demos.Scenarios.Agent.agentMcp.Steps
      |> List.iter (fun s ->
        match s.Expect with
        | Expectation.PageTextContains(selector, text) ->
          text |> Expect.equal "the actor decides its own expected-response check per tool — never a wire-carried canned string" ""
          selector |> Expect.stringContains "packs '<step>\\u001e<tool>' for Actors.Agent.parseWire" ""
        | other -> failtestf "expected PageTextContains, got %A" other)

      let toolsOnWire =
        SageFs.Demos.Scenarios.Agent.agentMcp.Steps
        |> List.choose (fun s ->
          match s.Expect with
          | Expectation.PageTextContains(selector, _) -> Agent.parseWire (selector + ":has-text(\"\")") |> Option.map snd
          | _ -> None)

      toolsOnWire |> Expect.equal "the exact three real MCP tools this scenario calls, in the order it calls them" expectedTools

    testCase "Actors.Agent.parseWire round-trips the '<step>\\u001e<tool>:has-text(\"\")' wire encoding Runtime.fs's wireStepOf actually produces" <| fun _ ->
      Agent.parseWire "1/3 create_sessioncreate_session:has-text(\"\")"
      |> Expect.equal "recovers (step, tool)" (Some("1/3 create_session", "create_session"))

    testCase "Actors.Agent.parseWire fails closed (None) on any selector that isn't this actor's own encoding — never a guess" <| fun _ ->
      Agent.parseWire "not-a-real-selector" |> Expect.equal "no separator, no match" None
      Agent.parseWire "[data-testid=session-output]:has-text(\"Ready\")" |> Expect.equal "a Dashboard-shaped selector is not this actor's encoding" None

    testCase "the cell-agent dispatch seam routes the 'agent' wire token to ActorId.Agent" <| fun _ ->
      SageFs.Demos.CellAgent.actorIdOfString "agent" |> Expect.equal "wire token 'agent' resolves" (Some ActorId.Agent)

    testCase "a malformed wire selector fails closed — Observe returns false without ever touching the daemon or a live Page" <| fun _ ->
      // A Page built from Unchecked.defaultof is never dereferenced here:
      // parseWire returns None BEFORE any Page/HTTP access, exactly like
      // CellAgentTests.fs's Dashboard.Handle test proves Id without ever
      // touching Playwright fields it wasn't given real values for.
      let handle: Agent.Handle =
        { Playwright = Unchecked.defaultof<_>
          Context = Unchecked.defaultof<_>
          Page = Unchecked.defaultof<_>
          Session = Agent.Rpc.create ()
          InitResult = ref None }

      let liveActor = Agent.toLiveActor handle
      liveActor.Id |> Expect.equal "reports ActorId.Agent — the dispatch seam actually routes to this actor" ActorId.Agent

      Async.RunSynchronously(liveActor.Observe "not-a-real-selector" 500.0)
      |> Expect.isFalse "a wire selector this actor never produced fails closed, not with a crash or a false positive"

    testCase "argumentsFor resolves REAL, existing paths under the real repo root — never a leaked {{REPO_ROOT}} token" <| fun _ ->
      // {{REPO_ROOT}} substitution (Runtime.fs's wireStepOf) never reaches
      // this actor's wire channel (Actors/Agent.fs's own doc comment) — this
      // actor must resolve the real root itself. Uses the SAME upward
      // "find SageFs.slnx" convention Runtime.fs's own findRepoRoot uses,
      // proving this test runs against a real checkout, not a fabricated
      // directory.
      match SageFs.Demos.Runtime.Core.findRepoRoot AppContext.BaseDirectory with
      | None -> failtest "could not find this checkout's own repo root (SageFs.slnx) — test environment is broken"
      | Some repoRoot ->

      match Agent.argumentsFor repoRoot (McpTool.current CurrentMcpTool.CreateProjectSession) with
      | Error e -> failtestf "expected Ok arguments for a real repo root, got Error %s" e
      | Ok args ->
        args
        |> List.iter (fun (_, v) -> v.Contains "{{" |> Expect.isFalse (sprintf "no unresolved template token in '%s'" v))

        args
        |> List.tryFind (fun (k, _) -> k = "project")
        |> Option.map snd
        |> Expect.equal
          "passes the real, existing .fsproj for the WebappDatastar sample"
          (Some(Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.WebappDatastar", "SageFs.Samples.WebappDatastar.fsproj")))

        args
        |> List.tryFind (fun (k, _) -> k = "project")
        |> Option.map snd
        |> Option.iter (fun p -> File.Exists p |> Expect.isTrue "the resolved .fsproj genuinely exists on disk")

    testCase "argumentsFor reports a tool with no recipe by name — fails loud, never a silent empty-args no-op" <| fun _ ->
      match SageFs.Demos.Runtime.Core.findRepoRoot AppContext.BaseDirectory with
      | None -> failtest "could not find this checkout's own repo root — test environment is broken"
      | Some repoRoot ->

      // `request_landing` is a live tool this legacy agent-mcp actor has no
      // argument recipe for, so it must fail loudly rather than call through
      // with no arguments.
      match Agent.argumentsFor repoRoot (McpTool.current CurrentMcpTool.RequestLanding) with
      | Ok args -> failtestf "expected Error for a tool with no recipe, got Ok %A" args
      | Error msg ->
        msg
        |> Expect.stringContains
          "names the tool it has no recipe for"
          (McpTool.value (McpTool.current CurrentMcpTool.RequestLanding))

    testCase "WHY — a retired tool name never parses as a live tool, so the wire can't carry one" <| fun _ ->
      McpTool.retiredValues
      |> List.iter (fun name ->
        McpTool.tryParseCurrent name
        |> Option.isNone
        |> Expect.isTrue (sprintf "retired tool '%s' must not resolve to a live MCP tool" name))
  ]
