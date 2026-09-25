module SageFs.Tests.RetiredToolNameTests

/// WHY — SageFs retires MCP tool names, and a retired name surviving in a
/// LIVE agent-facing string is worse than a stale doc: the daemon itself
/// sends the agent to a tool that no longer exists. The Lemmings roast
/// proved it — every successful run was told to poll `get_fsi_status`
/// (run 03 `events.ndjson:215`, run 06 `:178`, run 07 `:1763`, run 09
/// `:187`), so the guidance was worse than useless.
///
/// The forbidden names are DATA (`SageFs.Affordances.RetiredTool`, a DU with
/// one exhaustive to-string function), and "live" is defined by the REAL
/// registered catalog discovered by reflection — a name is allowed only if a
/// `[<McpServerTool>]` method actually carries it. A future rename cannot
/// quietly satisfy this test, because the names it checks are the ones the
/// product itself declares retired.
///
/// Historical prose is deliberately NOT scanned: CHANGELOG entries, trial
/// reports, and archived internal notes legitimately name the old tool. What
/// is scanned is exactly the set of surfaces a running agent reads.
open System
open System.IO
open System.Reflection
open System.Text
open Expecto
open Expecto.Flip
open SageFs.Affordances

let private repoRoot =
  // Tests run from the test assembly's output directory; walk up to the
  // checkout root that owns Directory.Build.props. Never a hardcoded path.
  let rec up (dir: DirectoryInfo) =
    if File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) then
      dir.FullName
    elif isNull dir.Parent then
      failtest "could not locate the repository root from the test working directory"
    else
      up dir.Parent
  up (DirectoryInfo(Directory.GetCurrentDirectory()))

/// The registered catalog, from the same reflection the 60-tool contract uses.
let private registeredToolNames : Set<string> =
  typeof<SageFs.Server.McpTools.SageFsTools>.GetMethods(BindingFlags.Public ||| BindingFlags.Instance)
  |> Array.filter (fun m ->
    m.GetCustomAttributes(true)
    |> Array.exists (fun attr -> attr.GetType().Name = "McpServerToolAttribute"))
  |> Array.map (fun m -> m.Name)
  |> Set.ofArray

/// Live agent-facing surfaces: what a running agent is actually told.
let private liveSurfaces : (string * string) list =
  [ "SageFs/Mcp.fs", File.ReadAllText(Path.Combine(repoRoot, "SageFs", "Mcp.fs"))
    "SageFs/McpTools.fs", File.ReadAllText(Path.Combine(repoRoot, "SageFs", "McpTools.fs"))
    "SageFs/AgentGuidance.fs", File.ReadAllText(Path.Combine(repoRoot, "SageFs", "AgentGuidance.fs"))
    "skills/sagefs/SKILL.md", File.ReadAllText(Path.Combine(repoRoot, "skills", "sagefs", "SKILL.md"))
    "docs/agents.md", File.ReadAllText(Path.Combine(repoRoot, "docs", "agents.md"))
    "docs/mcp-tools.md", File.ReadAllText(Path.Combine(repoRoot, "docs", "mcp-tools.md")) ]

/// Strip `//` and `(* *)` comments so an internal note that mentions a retired
/// name does not masquerade as agent-facing guidance. String literals are kept
/// deliberately — those are the guidance.
let private stripComments (source: string) : string =
  let sb = StringBuilder()
  let mutable i = 0
  let mutable inLine = false
  let mutable inBlock = false
  let mutable inString = false

  while i < source.Length do
    let c = source.[i]
    let n = if i + 1 < source.Length then source.[i + 1] else '\000'

    if inLine then
      if c = '\n' then
        inLine <- false
        sb.Append(c) |> ignore
    elif inBlock then
      if c = '*' && n = '/' then
        inBlock <- false
        i <- i + 1
      elif c = '\n' then
        // keep line numbers meaningful in the failure output
        sb.Append(c) |> ignore
    elif inString then
      sb.Append(c) |> ignore
      // F# verbatim strings: "" inside a string is a literal quote, not a close
      if c = '"' && n = '"' then
        sb.Append(n) |> ignore
        i <- i + 1
      elif c = '"' then
        inString <- false
    else
      if c = '/' && n = '/' then
        inLine <- true
        i <- i + 1
      elif c = '/' && n = '*' then
        inBlock <- true
        i <- i + 1
      else
        if c = '"' then inString <- true
        sb.Append(c) |> ignore

    i <- i + 1

  sb.ToString()

let private retiredNames = RetiredTool.toolNames

[<Tests>]
let retiredToolNameTests = testList "retired MCP tool names" [

  testCase "WHY — the retired vocabulary is non-empty, so this suite can never pass by testing nothing" <| fun _ ->
    (retiredNames.Length, 0)
    |> Expect.isGreaterThan "the contract must have real names to forbid"

  testCase "WHY — every name declared retired really is absent from the registered catalog, so the two lists cannot drift" <| fun _ ->
    RetiredTool.all
    |> List.iter (fun tool ->
      let name = RetiredTool.toToolName tool
      registeredToolNames.Contains name
      |> Expect.isFalse (sprintf "'%s' is declared retired but is actually registered" name))

  testCase "WHY — no live agent-facing string tells an agent to call a retired tool" <| fun _ ->
    liveSurfaces
    |> List.iter (fun (surface, source) ->
      let code = stripComments source

      RetiredTool.all
      |> List.iter (fun tool ->
        let name = RetiredTool.toToolName tool

        // A retired name is FORBIDDEN when it appears in pointing context —
        // "Poll X", "Call X", "use X", "with X", "X reports" — which is how
        // the product instructs an agent. Merely saying the tool is retired
        // ("this compatibility formatter is no longer a registered MCP tool")
        // is correct and must stay allowed, or the doc that explains the
        // removal would itself be a violation.
        let guidancePatterns =
          [ sprintf "Poll %s" name
            sprintf "poll %s" name
            sprintf "Call %s" name
            sprintf "call %s" name
            sprintf "use %s" name
            sprintf "Use %s" name
            sprintf "using %s" name
            sprintf "with %s" name
            sprintf "%s reports" name
            sprintf "%s is polled" name
            sprintf "then %s" name
            sprintf "followed by %s" name
            sprintf "-> %s" name
            sprintf "next calls %s" name
            sprintf "Check %s" name
            sprintf "check %s" name ]

        guidancePatterns
        |> List.filter (fun pattern -> code.Contains pattern)
        |> List.iter (fun pattern ->
          Expect.isFalse
            (sprintf
              "%s tells an agent to use the retired tool '%s' (matched %A) — the product would send an agent to a tool that no longer exists"
              surface
              name
              pattern)
            (code.Contains pattern))))

  testCase "WHY — the replacements this product tells agents to use really are registered" <| fun _ ->
    RetiredTool.all
    |> List.iter (fun tool ->
      match RetiredTool.replacement tool with
      | Some replacement ->
        registeredToolNames.Contains replacement
        |> Expect.isTrue
          (sprintf "'%s' is advertised as the live replacement for '%s', so it must be registered" replacement (RetiredTool.toToolName tool))
      | None -> ())

  testCase "WHY — the registered catalog is exactly the declared gate domain, so a name that exists is also gated" <| fun _ ->
    // Ties the two independent sources of truth together: reflection (what
    // `tools/list` advertises) and Affordances (what the call-time gate knows).
    // A tool that is registered but ungated, or gated but unregistered, would
    // both be a contract break — and either could let a retired name through.
    let declared = declaredGateTools |> Set.ofList
    declared
    |> Expect.equal "the gate domain must cover every registered tool" registeredToolNames
]
