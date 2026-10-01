/// `send_fsharp_code` hands the session whatever it is given, and FSI refuses some
/// things that are perfectly normal at the top of a source file: a `namespace`
/// line, a dotted `module A.B` name, a `module X` line with no `=`. The refusal it
/// gives is a parser message ("Invalid module name", "Unexpected keyword
/// 'namespace' in interaction") that points at nothing an agent can act on, while
/// the answer is already in the tool: send the file with `file_path` and
/// `eval_mode=file` and SageFs wraps it in the right module. These tests pin what
/// counts as such a declaration, measured against the real FSI (a single-name
/// `module X =` IS legal there, a dotted one is not).
module SageFs.Tests.EvalPreflightTests

open Expecto
open Expecto.Flip
open SageFs

let private declarationOf code = EvalPreflight.topLevelDeclaration code

[<Tests>]
let tests =
  testList "EvalPreflight.topLevelDeclaration" [

    testCase "WHY — a namespace line is refused by FSI outright, so it is named as a namespace declaration" <| fun _ ->
      declarationOf "namespace Probe.Ns\n\ntype T = A | B"
      |> Expect.equal "namespace" (TopLevelDeclaration.Namespace "Probe.Ns")

    testCase "WHY — a dotted module name fails in FSI even with an equals sign, so it is a declaration" <| fun _ ->
      declarationOf "module Probe.Inner =\n  let z = 1"
      |> Expect.equal "dotted with =" (TopLevelDeclaration.Module "Probe.Inner")

    testCase "WHY — the file-level form, a module line with no equals sign, is a declaration too" <| fun _ ->
      declarationOf "module Probe.Inner\n\nlet answer = 42"
      |> Expect.equal "file-level dotted" (TopLevelDeclaration.Module "Probe.Inner")
      declarationOf "module Probe\n\nlet answer = 42"
      |> Expect.equal "file-level single" (TopLevelDeclaration.Module "Probe")

    testCase "WHY — a single-name module with an equals sign is legal FSI and must never be flagged" <| fun _ ->
      declarationOf "module Probe =\n  let z = 1"
      |> Expect.equal "legal nested module" TopLevelDeclaration.NoDeclaration

    testCase "WHY — ordinary code, including a later mention of the word module, is not a declaration" <| fun _ ->
      declarationOf "let answer = 42"
      |> Expect.equal "plain let" TopLevelDeclaration.NoDeclaration
      declarationOf "let f () =\n  // module Not.A.Declaration\n  1"
      |> Expect.equal "keyword not at the start" TopLevelDeclaration.NoDeclaration

    testCase "WHY — blank lines, comments and attributes before the declaration do not hide it" <| fun _ ->
      declarationOf "\n// the file header\n\n[<AutoOpen>]\nmodule Probe.Inner\n\nlet a = 1"
      |> Expect.equal "skips header lines" (TopLevelDeclaration.Module "Probe.Inner")

    testCase "WHY — accessibility and rec modifiers are part of the declaration, not a reason to miss it" <| fun _ ->
      declarationOf "module internal Probe.Inner\nlet a = 1"
      |> Expect.equal "internal" (TopLevelDeclaration.Module "Probe.Inner")
      declarationOf "module rec Probe.Inner\nlet a = 1"
      |> Expect.equal "rec" (TopLevelDeclaration.Module "Probe.Inner")

    testCase "WHY — empty or whitespace code has no declaration" <| fun _ ->
      declarationOf "" |> Expect.equal "empty" TopLevelDeclaration.NoDeclaration
      declarationOf "  \n \n" |> Expect.equal "blank" TopLevelDeclaration.NoDeclaration

    testCase "WHY — the hint names the declaration and the route that works (file_path with eval_mode=file), and the other way out" <| fun _ ->
      let hint = EvalPreflight.hint (TopLevelDeclaration.Module "Probe.Inner")
      hint |> Expect.stringContains "names the module" "Probe.Inner"
      hint |> Expect.stringContains "points at file_path" "file_path"
      hint |> Expect.stringContains "points at file mode" "eval_mode"
      EvalPreflight.hint (TopLevelDeclaration.Namespace "Probe.Ns")
      |> Expect.stringContains "names the namespace" "Probe.Ns"
  ]

/// A context whose session operations all throw, so a test can prove a call never reached a session.
let private untouchableCtx () : SageFs.McpTools.McpContext =
  let boom name = failwithf "%s must not be called: the preflight answers before any session is involved" name
  let ops : SageFs.SessionManagementOps =
    { SageFs.SessionManagementOps.stub with
        GetProxy = fun _ -> boom "GetProxy"
        GetSessionInfo = fun _ -> boom "GetSessionInfo"
        GetAllSessions = fun () -> boom "GetAllSessions" }
  { FrictionStore = None
    DiagnosticsChanged = (Event<SageFs.Features.DiagnosticsStore.T>()).Publish
    StateChanged = None
    SessionOps = ops
    SessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortOwner = None
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

[<Tests>]
let wiringTests =
  testList "send_fsharp_code preflight" [

    testTask "WHY — a namespace line comes back as an error result that says what works, without ever reaching a session" {
      let tools = SageFs.Server.McpTools.SageFsTools(untouchableCtx (), Microsoft.Extensions.Logging.Abstractions.NullLogger<SageFs.Server.McpTools.SageFsTools>.Instance)
      let! (result: ModelContextProtocol.Protocol.CallToolResult) = tools.send_fsharp_code("claude", "namespace Probe.Ns\n\ntype T = A | B")
      result.IsError |> Expect.equal "an error result" (System.Nullable true)
      let text = result.Content |> Seq.map (fun block -> (block :?> ModelContextProtocol.Protocol.TextContentBlock).Text) |> String.concat ""
      text |> Expect.stringContains "names the route that works" "file_path"
    }
  ]
