module SageFs.Middleware.HotReloading

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices

open SageFs.ProjectLoading
open SageFs.Utils
open SageFs.AppState
open SageFs.DevReload
open SageFs.Features.LiveTesting
open SageFs.Middleware.HotReloadCore

/// Detect top-level function bindings (not value bindings).
/// Function bindings have parameters between the name and '=':
///   let f () = ...      → function (unit param)
///   let f x y = ...     → function (named params)
///   let f (x: int) = .. → function (typed params)
///   let x = 42          → value (no params)
///   let h : Type = ...  → value (type annotation, no params)
///
/// Chesterton's fence: accepts INDENTED function bindings too (module-member
/// functions like `  let greeting () = ...` inside `module Greeting =`).
/// The hot-reload pipeline transforms module-declared files by indenting the
/// module body, so the detour target `greeting` is indented. Requiring column-0
/// meant module-nested functions never got NoInlining, the JIT inlined them
/// into the route closure, and Harmony had nothing to detour — the running app
/// kept serving the old value (P0 hot-reload gap). Local `let f x =` inside a
/// function body is also a method and getting NoInlining is harmless.
let isTopLevelFunctionBinding (line: string) =
  let trimmed = line.TrimStart()
  match not (trimmed.StartsWith("let ", System.StringComparison.Ordinal)) || trimmed.StartsWith("let!", System.StringComparison.Ordinal) with
  | true -> false
  | false ->
    let mutable s = trimmed.Substring(4).TrimStart()
    for m in ["private "; "internal "; "public "; "inline "; "rec "; "mutable "] do
      match s.StartsWith(m, System.StringComparison.Ordinal) with
      | true -> s <- s.Substring(m.Length).TrimStart()
      | false -> ()
    match s.IndexOf('=') with
    | -1 -> false
    | eqIdx ->
      let beforeEq = s.Substring(0, eqIdx).Trim()
      beforeEq.Contains("(") || (beforeEq.Contains(" ") && not (beforeEq.Contains(":")))

/// Detect static member method definitions (not properties).
/// The F# compiler inlines simple static member bodies at the IL level,
/// eliminating the call instruction entirely and making Harmony detours invisible.
let isStaticMemberFunction (line: string) =
  let trimmed = line.TrimStart()
  trimmed.StartsWith("static member ", System.StringComparison.Ordinal) &&
    let afterKw = trimmed.Substring("static member ".Length).TrimStart()
    match afterKw.IndexOf('('), afterKw.IndexOf('=') with
    | parenIdx, eqIdx when parenIdx >= 0 && (eqIdx < 0 || parenIdx < eqIdx) -> true
    | _, eqIdx when eqIdx > 0 ->
      let beforeEq = afterKw.Substring(0, eqIdx).Trim()
      beforeEq.Contains(" ") && not (beforeEq.Contains(":"))
    | _ -> false

/// Strip `let` keyword + modifiers, returning the remaining text after the name.
/// Used by multi-line binding detection to identify function signatures that span lines.
/// Accepts indented bindings (module-member functions) — see
/// isTopLevelFunctionBinding for why column-0 is not required.
let private startsLetBinding (line: string) =
  let trimmed = line.TrimStart()
  match trimmed.StartsWith("let ", System.StringComparison.Ordinal)
        && not (trimmed.StartsWith("let!", System.StringComparison.Ordinal)) with
  | false -> None
  | true ->
    let mutable s = trimmed.Substring(4).TrimStart()
    for m in ["private "; "internal "; "public "; "inline "; "rec "; "mutable "] do
      match s.StartsWith(m, System.StringComparison.Ordinal) with
      | true -> s <- s.Substring(m.Length).TrimStart()
      | false -> ()
    Some s

/// Detect multi-line function bindings where params span multiple lines:
///   let handler
///       (ctx: HttpContext)
///       (next: RequestDelegate) =
///       task { ... }
/// Scans up to 5 lines forward from a `let` line to find the `=`.
let isMultiLineFunctionBinding (lines: string[]) (idx: int) : bool =
  match startsLetBinding lines.[idx] with
  | None -> false
  | Some afterName ->
    match afterName.Contains("=") with
    | true -> false // single-line — handled by isTopLevelFunctionBinding
    | false ->
      let maxLookahead = 5
      let mutable found = false
      let mutable combined = afterName
      let mutable i = idx + 1
      while i < lines.Length && i <= idx + maxLookahead && not found do
        let nextLine = lines.[i].TrimStart()
        combined <- combined + " " + nextLine
        match nextLine.Contains("=") with
        | true -> found <- true
        | false -> ()
        i <- i + 1
      match found with
      | false -> false
      | true ->
        match combined.IndexOf('=') with
        | -1 -> false
        | eqIdx ->
          let beforeEq = combined.Substring(0, eqIdx).Trim()
          beforeEq.Contains("(") || (beforeEq.Contains(" ") && not (beforeEq.Contains(":")))

/// Classification of a binding for hot-reload purposes.
type BindingKind =
  | FunctionBinding     // let f x = ...
  | ValueBinding        // let x = 42
  | StaticMemberMethod  // static member F x = ...
  | MultiLineFunction   // let f\n  (x: int)\n  (y: int) = ...
  | Unknown             // not a binding line

/// Classify a source line (in context of surrounding lines) into a BindingKind.
let classifyBinding (lines: string[]) (idx: int) : BindingKind =
  let line = lines.[idx]
  match isStaticMemberFunction line with
  | true -> StaticMemberMethod
  | false ->
    match isTopLevelFunctionBinding line with
    | true -> FunctionBinding
    | false ->
      match isMultiLineFunctionBinding lines idx with
      | true -> MultiLineFunction
      | false ->
        let trimmed = line.TrimStart()
        match trimmed.StartsWith("let ", System.StringComparison.Ordinal)
              && not (trimmed.StartsWith("let!", System.StringComparison.Ordinal))
              && line = line.TrimStart() with
        | true -> ValueBinding
        | false -> Unknown

/// Whether a binding kind needs [<MethodImpl(NoInlining)>] for Harmony detours.
let needsNoInlining (kind: BindingKind) =
  match kind with
  | FunctionBinding | StaticMemberMethod | MultiLineFunction -> true
  | ValueBinding | Unknown -> false

/// Inject [<MethodImpl(MethodImplOptions.NoInlining)>] on top-level function bindings
/// (including multi-line signatures) and static member methods so Harmony detours work.
/// Without this, the F# compiler inlines simple static member bodies at the IL level,
/// and the JIT may inline short let-binding functions — both make Harmony's
/// entry-point detour invisible to callers.
let injectNoInlining (code: string) =
  let lines = code.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
  let injectionLines =
    match CompilationContext.noInliningTargets code with
    | CompilationContext.SyntaxTargets targets -> targets
    | CompilationContext.UnparsedFragment ->
      lines
      |> Array.mapi (fun idx _ -> idx, classifyBinding lines idx)
      |> Array.choose (fun (idx, kind) ->
        match needsNoInlining kind with
        | true -> Some idx
        | false -> None)
      |> Set.ofArray
  match injectionLines.IsEmpty with
  | true -> code
  | false ->
    // Chesterton's fence: skip injecting when the binding ALREADY carries a
    // [<MethodImpl(NoInlining)>] attribute on the line(s) directly above it.
    // User source files (like the hot-reload verification fixture) may declare
    // NoInlining themselves so the startup #load is not inlined into the route
    // closure; injecting a SECOND attribute on the watcher's re-eval makes FSI
    // fail with "MethodImplAttribute has AllowMultiple=false" — the save then
    // errors instead of hot-reloading (P0 gap).
    let isLineDirective (line: string) =
      let t = line.TrimStart()
      (t.Length > 2 && t.StartsWith("# ", StringComparison.Ordinal) && Char.IsDigit t.[2])
      || t.StartsWith("#line ", StringComparison.Ordinal)
    // Blank lines and line directives may sit between a binding and its attributes.
    let hasExistingAttribute (idx: int) =
      let mutable j = idx - 1
      let mutable found = false
      while j >= 0 && not found do
        let t = lines.[j].Trim()
        match t with
        | "" -> j <- j - 1
        | _ when isLineDirective lines.[j] -> j <- j - 1
        | _ ->
          if t.StartsWith("[<MethodImpl", StringComparison.Ordinal)
             || t.StartsWith("[<System.Runtime.CompilerServices.MethodImpl", StringComparison.Ordinal) then
            found <- true
          else
            j <- -1 // stop at the first non-blank, non-attribute line
      found
    // A line directive numbers the next physical line, so an attribute goes above
    // the directive; otherwise every line after it would be reported one too late.
    let insertionPoint (idx: int) =
      match idx > 0 && isLineDirective lines.[idx - 1] with
      | true -> idx - 1
      | false -> idx
    let attributeAt =
      injectionLines
      |> Seq.filter (hasExistingAttribute >> not)
      |> Seq.map (fun idx -> insertionPoint idx, lines.[idx].Length - lines.[idx].TrimStart().Length)
      |> Map.ofSeq
    let sb = System.Text.StringBuilder()
    sb.Append("open System.Runtime.CompilerServices\n") |> ignore
    for i in 0 .. lines.Length - 1 do
      match Map.tryFind i attributeAt with
      | Some indent ->
        sb.Append(System.String(' ', indent) + "[<MethodImpl(MethodImplOptions.NoInlining)>]\n") |> ignore
      | None -> ()
      sb.Append(lines.[i] + "\n") |> ignore
    sb.ToString()

let hotReloadingMiddleware next (request, st: AppState) =
  let sessionAvailable = not (isNull (box st.Session))
  let hotReloadFlagEnabled =
    match sessionAvailable with
    | true ->
      match st.Session.ReadFlag "_SageFsHotReload" with
      | SageFs.FsiSession.FlagBound true -> true
      | _ -> false
    | false -> false

  let shouldTriggerReload (m: Map<string, obj>) =
    match hotReloadFlagEnabled, Map.tryFind "hotReload" m with
    | _, Some v when v = true -> true
    | true, None -> true
    | _ -> false

  // Only inject NoInlining attributes when hot-reload is enabled.
  // Without this gate, every eval gets unnecessary IL modifications.
  let request =
    match hotReloadFlagEnabled with
    | true -> { request with Code = injectNoInlining request.Code }
    | false -> request

  let response, st = next (request, st)

  // The agent lives where the user's code lives (this process, or the isolated FSI host): it registers the methods the
  // eval defined, detours them only when hot reload is on, and looks for tests. This middleware only asks.
  match response.EvaluationResult with
  | Error _ -> response, st
  | Ok _ ->
    match isNull (box st.Session) with
    | true -> response, st
    | false ->
      let detours =
        match hotReloadFlagEnabled with
        | true -> SageFs.HostAgent.DetourPolicy.ApplyDetours
        | false -> SageFs.HostAgent.DetourPolicy.RegisterOnly
      // The force flag only WIDENS scanning (the daemon asking eval-time discovery to catch a brand-new [<Tests>] value
      // that detoured no existing method); it never changes the result for a submission with no test value in it.
      let discovery =
        match Map.tryFind "liveTestRediscover" request.Args with
        | Some v when v = box true -> SageFs.HostAgent.DiscoveryPolicy.Forced
        | _ -> SageFs.HostAgent.DiscoveryPolicy.WhenChanged
      match st.Session.AfterEval { EvaluatedCode = response.EvaluatedCode; Detours = detours; Discovery = discovery } with
      | SageFs.HostAgent.AgentUnavailable reason ->
        Log.warn "[HotReloading] the session's agent is unavailable, so this eval was not reloaded or scanned for tests: %s" reason
        response, st
      | SageFs.HostAgent.AgentAnswered report ->
        // Who tells the browser what this eval did.
        //
        // Chesterton's fence — this is where the shipped bug lived. It used to
        // read "any method was detoured ⇒ broadcast a reload", which is a
        // count this middleware is in no position to interpret: a whole-file
        // re-evaluation detours whatever happens to match, so a save that
        // re-pointed a handful of incidental BCL-signature helpers and none of
        // the handlers the user edited still refreshed the page into the old
        // code.
        //
        // A save carries `hotReload` in its args because it came through the
        // file-watcher pipeline, and that pipeline knows what the user actually
        // changed (SageFs.Features.ReloadPlanning) — so it reports its own
        // outcome and this middleware stays quiet. An interactive eval has no
        // such pipeline, and for it "the methods this submission redefined" IS
        // the whole truth, so it is reported here, through the same
        // ReloadOutcome gate every other surface uses.
        match Map.containsKey "hotReload" request.Args, hotReloadFlagEnabled with
        | true, _ -> ()
        | false, false -> ()
        | false, true ->
          let updated = List.length report.UpdatedMethods
          SageFs.Features.ReloadBroadcast.broadcastOutcome
            (SageFs.Features.ReloadOutcome.ReloadOutcome.ofPatchCounts updated updated [])

        // Tests run where they live: the runner asks the session's agent, which tries interactively defined tests
        // first and the project's after.
        let session = st.Session
        let runTest (test: TestCase) : Async<TestResult> =
          async {
            match! session.RunTest test with
            | SageFs.HostAgent.AgentAnswered result -> return result
            | SageFs.HostAgent.AgentUnavailable _ -> return TestResult.NotRun
          }

        let metadata =
          match shouldTriggerReload request.Args with
          | true ->
            // Mechanical passthrough only — the file-watcher pipeline
            // (WorkerMain.fs) is where these become a user-facing
            // RestartReason. Torn/declined/never-landed bindings used to
            // stop here: `reloadedMethods` alone cannot tell a caller
            // "nothing changed" from "a mutable binding just tore".
            response.Metadata
              .Add("reloadedMethods", report.UpdatedMethods)
              // Canary-proven ineffective patches travel too, for the same
              // reason the torn/declined bindings do: without them the file
              // watcher counts a method the canary already proved unchanged as
              // landed, and reports "Hot reloaded 1 of 1" for a save the
              // running process ignored.
              .Add("hotReloadIneffectiveMethods", report.DetourReport.Ineffective)
              .Add("hotReloadRedirectedFromCompiled", report.DetourReport.RedirectedFromCompiled)
              .Add("hotReloadBindingOutcomes", report.DetourReport.Bindings)
              .Add("hotReloadDeclinedBindings", report.DetourReport.Declined)
          | false -> response.Metadata
        let metadata = metadata.Add("liveTestHookResult", report.LiveTest)
        let metadata = metadata.Add("liveTestRunTest", runTest)
        let metadata =
          match List.isEmpty report.AssemblyLoadErrors with
          | false -> metadata.Add("assemblyLoadErrors", report.AssemblyLoadErrors)
          | true -> metadata

        { response with Metadata = metadata }, st

