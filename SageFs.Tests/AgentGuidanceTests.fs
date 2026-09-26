module SageFs.Tests.AgentGuidanceTests

/// What SageFs tells agents on connect (the MCP ServerInstructions) and the
/// back_to_the_repl / sagefs_loop prompts. The rules are pinned as data, one
/// rule per assertion, so rewording a sentence doesn't break a snapshot but
/// dropping a rule does.

open System
open System.Reflection
open Expecto
open Expecto.Flip
open ModelContextProtocol.Server
open SageFs.Server.AgentGuidance

let private contains (needle: string) (text: string) =
  text.Contains(needle, StringComparison.OrdinalIgnoreCase)

let private sentences (text: string) =
  text.Split([| '.'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

let private promptMethods =
  typeof<SageFsPrompts>.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
  |> Array.choose (fun m ->
    match m.GetCustomAttribute<McpServerPromptAttribute>() with
    | null -> None
    | attr -> Some(attr.Name, m))
  |> Map.ofArray

[<Tests>]
let serverInstructionsTests = testList "MCP server instructions" [

  testCase "WHY — they state the REPL loop, so an agent knows the loop from the first connection" <| fun _ ->
    [ "send_fsharp_code"; "create_project_session"; "create_solution_session"; "create_bare_session"
      "get_daemon_status"; "get_session_status"; "hard_reset_fsi_session"; "rebuild=true"; "final gate"
      "acquire_full_build_lease"; "acquire_test_suite_lease"; "release_work_lease" ]
    |> List.filter (fun needle -> not (contains needle serverInstructions))
    |> Expect.isEmpty "the loop's tools and the final gate should all be named"

  testCase "WHY — retired tool names never reappear in the always-on guidance" <| fun _ ->
    [ "get_fsi_status"; "get_startup_info"; "create_session" ]
    |> List.filter (fun needle -> contains needle serverInstructions)
    |> Expect.isEmpty "retired tools must not be advertised to a new connection"

  testCase "WHY — every loop step is in them, so the instructions and the prompts can't drift apart" <| fun _ ->
    loopSteps
    |> List.filter (fun step -> not (serverInstructions.Contains step))
    |> Expect.isEmpty "each step of the loop should appear"

  testCase "WHY — a worktree is called out as its own session boundary" <| fun _ ->
    serverInstructions |> contains "worktree" |> Expect.isTrue "the worktree rule should be there"

  testCase "WHY — the gotchas are there: earlier error, Result.Ok shadowing, #r, filtered runs" <| fun _ ->
    [ "earlier error"; "Result.Ok"; "#r"; "filtered test run" ]
    |> List.filter (fun needle -> not (contains needle serverInstructions))
    |> Expect.isEmpty "each gotcha should be named"

  testCase "WHY — REPL friction gets reported instead of silently worked around" <| fun _ ->
    serverInstructions |> contains "don't fall back silently" |> Expect.isTrue "the report-friction rule should be there"

  testCase "WHY — the editing rules are in the always-on guidance, because the rule an agent most needs is the one about NOT writing an unproven change" <| fun _ ->
    [ "before you write it to a file"; "sed -i"; "applyInOrder"; "assert that each replacement matched" ]
    |> List.filter (fun needle -> not (contains needle serverInstructions))
    |> Expect.isEmpty "the editing sequence should be stated on connect, not only in the skill file"

  testCase "WHY — every editing rule is a whole, unsubtracted line, so rewording one cannot quietly drop it" <| fun _ ->
    editing
    |> List.filter (fun rule -> not (serverInstructions.Contains rule))
    |> Expect.isEmpty "each editing rule should appear verbatim"

  testCase "WHY — the #load trap is named, because its error message points at a type incompatibility and not at the real cause" <| fun _ ->
    [ "#load"; "type-incompatibility"; "PURE file" ]
    |> List.filter (fun needle -> not (contains needle serverInstructions))
    |> Expect.isEmpty "the duplicate-#load trap should be named with its misleading symptom"

  testCase "WHY — the slow gate is told to run in the background and not to be re-rolled, because that is the most expensive habit an agent has" <| fun _ ->
    [ "BACKGROUND"; "don't poll"; "re-roll" ]
    |> List.filter (fun needle -> not (contains needle serverInstructions))
    |> Expect.isEmpty "the background-gate rule should be present"

  testCase "WHY — they never tell the agent to stop, restart or reinstall the user's daemon on its own" <| fun _ ->
    serverInstructions
    |> sentences
    |> List.filter (fun s -> contains "restart" s || contains "reinstall" s)
    |> List.filter (fun s -> not (contains "never" s && contains "without asking" s))
    |> Expect.isEmpty "every sentence about restarting or reinstalling must be a never-without-asking"

  testCase "WHY — they're platform neutral, because agents on Linux and macOS read them too" <| fun _ ->
    [ "PowerShell"; "Start-Process"; "terminal window"; "You OWN" ]
    |> List.filter (fun needle -> contains needle serverInstructions)
    |> Expect.isEmpty "no Windows-only process rules and no 'you own the daemon'"

  testCase "WHY — they point at the full skill and the back_to_the_repl prompt" <| fun _ ->
    [ SkillPath; BackToTheReplPromptName ]
    |> List.filter (fun needle -> not (serverInstructions.Contains needle))
    |> Expect.isEmpty "both pointers should be there"

  testCase "WHY — they stay short, because every client pays for them on every connection" <| fun _ ->
    // The budget was 3000 and was ALREADY nearly spent: this file measured
    // 2760 chars before the editing rules existed, leaving 240 chars of
    // headroom for four rules. Measured again at 3686 with them in.
    //
    // So the cap was raised rather than the rules trimmed to nothing. A rule
    // that does not fit is not a rule: the alternative was dropping the
    // editing discipline, which is the single most-ignored instruction in this
    // repo and the most expensive one to ignore. The cost is real but bounded
    // and paid once per connection; the cost of the omission was paid on
    // every source edit.
    //
    // 4200 is a ceiling, not a target. It exists so a future edit has to make
    // a deliberate choice about what to drop, rather than appending freely.
    (serverInstructions.Length, 4200) |> Expect.isLessThan "keep the always-on guidance under 4200 chars"
]

[<Tests>]
let promptTests = testList "MCP prompts" [

  testCase "WHY — back_to_the_repl and sagefs_loop are registered as MCP prompts, so clients list them" <| fun _ ->
    [ BackToTheReplPromptName; SageFsLoopPromptName ]
    |> List.filter (fun name -> not (promptMethods.ContainsKey name))
    |> Expect.isEmpty "both prompts should carry [<McpServerPrompt>]"

  testCase "WHY — the prompt type carries [<McpServerPromptType>], which WithPrompts<T> reflects over" <| fun _ ->
    typeof<SageFsPrompts>.GetCustomAttribute<McpServerPromptTypeAttribute>()
    |> isNull
    |> Expect.isFalse "SageFsPrompts should be an MCP prompt type"

  testCase "WHY — the SDK builds a prompt named back_to_the_repl from the method, the same way the server does" <| fun _ ->
    let prompt = McpServerPrompt.Create(promptMethods[BackToTheReplPromptName], (null: obj))
    prompt.ProtocolPrompt.Name |> Expect.equal "the protocol name is what the slash command uses" BackToTheReplPromptName

  testCase "WHY — back_to_the_repl says the agent left, asks where, restates the loop and says resume" <| fun _ ->
    let text = SageFsPrompts.BackToTheRepl()
    [ "You left the SageFs REPL loop"; "Name the step where you left"; "Resume from the REPL now" ]
    |> List.filter (fun needle -> not (text.Contains needle))
    |> Expect.isEmpty "each part of the nudge should be there"
    loopSteps
    |> List.filter (fun step -> not (text.Contains step))
    |> Expect.isEmpty "the loop should be restated in full"

  testCase "WHY — sagefs_loop carries the same guidance the instructions do" <| fun _ ->
    SageFsPrompts.SageFsLoop() |> Expect.equal "one source of truth" serverInstructions
]
