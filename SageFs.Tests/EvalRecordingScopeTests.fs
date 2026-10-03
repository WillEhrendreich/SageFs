/// An eval is recorded in the session it ran in, whichever door it came through.
///
/// `send_fsharp_code` (MCP) recorded into a per-session store, and `/exec` (what VS Code and Neovim
/// use) recorded only into the daemon-global one. The analysis tools read the per-session store, so
/// an editor's evals were invisible to them: `get_cell_dependencies` and `plan_ripple` answered
/// "no evals yet" about a session the editor had been evaluating in all day. Both doors now record
/// into the session's own store. The `/exec` door keeps feeding the global store too, because the
/// dashboard's SSE push reads that one.
module SageFs.Tests.EvalRecordingScopeTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools

let private ctxRecordingInto (globalWrites: int ref) : McpContext =
  { FrictionStore = None
    DiagnosticsChanged = (Event<SageFs.Features.DiagnosticsStore.T>()).Publish
    StateChanged = None
    SessionOps = SessionManagementOps.stub
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = Some (fun _ _ _ -> globalWrites.Value <- globalWrites.Value + 1)
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

let private freshSession () : string = Guid.NewGuid().ToString("N").Substring(0, 8)

/// What an MCP caller, bound to a transport connection, reads for a session.
let private recordedFor (ctx: McpContext) (sid: string) : int option =
  currentTransportSessionId.Value <- Some "eval-recording-scope-test"
  try featureStateForSession ctx sid |> Option.map (fun state -> state.History.Count)
  finally currentTransportSessionId.Value <- None

[<Tests>]
let tests =
  testList "an eval is recorded in the session it ran in" [

    testCase "WHY — an /exec eval (no MCP connection bound) lands in its session's store, so the analysis tools see an editor's evals" <| fun _ ->
      let ctx = ctxRecordingInto (ref 0)
      let sid = freshSession ()
      recordEvalForSession ctx sid "let edX = 1" "Result: val edX: int = 1" 3L
      recordedFor ctx sid |> Expect.equal "the session's own store holds the eval" (Some 1)

    testCase "WHY — the /exec door keeps feeding the global store the dashboard's push reads" <| fun _ ->
      let globalWrites = ref 0
      let ctx = ctxRecordingInto globalWrites
      recordEvalForSession ctx (freshSession ()) "let edY = 2" "Result: val edY: int = 2" 3L
      globalWrites.Value |> Expect.equal "one write to the global store" 1

    testCase "an MCP eval is recorded per session and does not also write the global store" <| fun _ ->
      let globalWrites = ref 0
      let ctx = ctxRecordingInto globalWrites
      let sid = freshSession ()
      currentTransportSessionId.Value <- Some "eval-recording-scope-test"
      try recordEvalForSession ctx sid "let mcpZ = 3" "Result: val mcpZ: int = 3" 3L
      finally currentTransportSessionId.Value <- None
      recordedFor ctx sid |> Expect.equal "the session's store holds it" (Some 1)
      globalWrites.Value |> Expect.equal "the global store was not written" 0

    testCase "two sessions' evals stay apart" <| fun _ ->
      let ctx = ctxRecordingInto (ref 0)
      let a, b = freshSession (), freshSession ()
      recordEvalForSession ctx a "let one = 1" "Result: val one: int = 1" 3L
      recordEvalForSession ctx a "let two = 2" "Result: val two: int = 2" 3L
      recordEvalForSession ctx b "let three = 3" "Result: val three: int = 3" 3L
      recordedFor ctx a |> Expect.equal "session a holds its two" (Some 2)
      recordedFor ctx b |> Expect.equal "session b holds its one" (Some 1)
  ]
