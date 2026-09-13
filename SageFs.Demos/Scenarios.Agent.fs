/// `agent-mcp` (demo-actors-plan.md §2.4, matrix #17): a real, three-tool
/// MCP exchange against the cell's own daemon — `create_session` opens the
/// SAME real `WebappDatastar` sample every other scenario proves against
/// (never a bare Quick Start session with nothing real to evaluate, §10),
/// `get_fsi_status` is genuinely polled until the daemon itself reports
/// "State: Ready", and `send_fsharp_code` evaluates a live expression
/// (`List.sum [ 1 .. 10 ]`, the SAME expression `repl-dashboard` already
/// proves against a real session — see `Actors/Agent.fs`'s `argumentsFor`
/// doc comment for why the project's OWN qualified state is deliberately
/// NOT used here: a real, live `record agent-mcp` run reproduced a genuine
/// session-warmup race in the product's own auto-open pipeline, out of this
/// island's scope to fix) — each step's Expectation is proven by a REAL MCP
/// JSON-RPC response, never a DOM string: see `Actors/Agent.fs`'s own doc
/// comment for exactly how a non-input `LiveActor.Observe` receives and
/// executes this.
module SageFs.Demos.Scenarios.Agent

open SageFs.Demos.Domain

/// Packs `"<step label><tool name>"` into `PageTextContains`'s
/// selector field — the one opaque, freeform string the Agent actor's
/// `Observe` receives on the wire (`Actors/Agent.fs`'s `parseWire`/
/// `argumentsFor`/`expectedSubstringFor` do the real work of resolving
/// arguments and checking the real response; nothing here is a DOM
/// selector or CSS). `text` is always `""`: the actor decides its own
/// expected-response check per tool internally (an unpredictable session
/// id vs. a specific, evidence-backed status/eval string) rather than the
/// wire carrying a canned expected string a real response could never be
/// pinned to in advance.
let private mcpStep (label: string) (toolName: string) : Expectation =
  Expectation.PageTextContains(sprintf "%s%s" label toolName, "")

/// This step's Action carries no click/type target at all — there is
/// nothing to click for a step whose real substance is an MCP tool call —
/// so `Action.Await` (already the domain's own "no click, just wait for
/// something real to happen" shape, exactly what `lt-dashboard`'s step 4
/// uses) is the honest fit. The carried `Signal` is not yet read by
/// `Runtime.fs`'s `wireStepOf` for any `Action.Await` case (documentation
/// only, today) — `SessionReady` is the closest existing case to "the real
/// MCP exchange for this step has genuinely completed".
let private mcpAction: Action = Action.Await Signal.SessionReady

let agentMcp: Scenario =
  { Id = ScenarioId.ofRaw "agent-mcp"
    Capability = Capability.Agent
    Client = Client.Agent
    App = AppKind.NoApp
    Sample = Sample.WebappDatastar
    Layout = LayoutTemplate.DashboardOnly
    Steps =
      [ { Caption = Caption.mk "1/3 · create_session opens a real project — a genuine MCP tool call"
          Action = mcpAction
          Expect = mcpStep "1/3 create_session" "create_session"
          Dwell = Dwell.medium }
        { Caption = Caption.mk "2/3 · get_fsi_status — polled live until the daemon itself reports Ready"
          Action = mcpAction
          Expect = mcpStep "2/3 get_fsi_status" "get_fsi_status"
          Dwell = Dwell.medium }
        { Caption = Caption.mk "3/3 · send_fsharp_code evaluates a live expression on the real session"
          Action = mcpAction
          Expect = mcpStep "3/3 send_fsharp_code" "send_fsharp_code"
          Dwell = Dwell.long } ]
    Cost = CostClass.web
    Masks = [] }

let scenarios: Scenario list = [ agentMcp ]
