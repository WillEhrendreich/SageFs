/// What SageFs tells agents about how to work: the MCP server instructions
/// every client gets on connect, and the prompts a user can fire to put a
/// drifting agent back on the REPL loop.
///
/// This is the short, always-on version of skills/sagefs/SKILL.md. Every
/// client pays for the instructions in tokens on every connection, so keep
/// them tight and point at the skill for the rest.
module SageFs.Server.AgentGuidance

open System.ComponentModel
open ModelContextProtocol.Server

/// Where the full rulebook lives.
[<Literal>]
let SkillPath = "skills/sagefs/SKILL.md"

[<Literal>]
let BackToTheReplPromptName = "back_to_the_repl"

[<Literal>]
let SageFsLoopPromptName = "sagefs_loop"

/// The loop, one line per step. The instructions and both prompts all
/// render these same lines, so they can't drift apart.
let loopSteps = [
  "RED: send_fsharp_code the smallest thing that shows the problem, and watch it fail."
  "GREEN: redefine it in the session until it passes. Small blocks, each statement ending in ;;."
  "Persist the working code to the .fs file."
  "hard_reset_fsi_session with rebuild=true, so the session runs the real file. Only after persisting or an .fsproj change, not after every eval."
  "Re-verify in the session, then commit."
  "Final gate: the full build and the unfiltered test suite, once, when you're done."
]

let firstMinute = [
  "Call get_fsi_status or list_sessions. Check the daemon's version against the repo. If it's behind, tell the user."
  "Sessions belong to a working directory, and a git worktree is its own boundary. Use your own session, and don't create a duplicate."
  "Build once, then create_session for your directory, then get_fsi_status until Ready. Creating it before the first build fails warmup with \"Not all DLLs are found\"."
]

let gotchas = [
  "\"Operation could not be completed due to earlier error\" means an earlier statement failed. Fix that statement. Don't reset the session."
  "If a bare Error or Ok in a Result match resolves to the wrong type (\"This union case does not take arguments\"), something in scope shadows it. Write Result.Error/Result.Ok."
  "Never #r a DLL the session already loaded from the project. You get two copies of every type, and the lock blocks rebuilds."
  "A filtered test run is never the acceptance check. Only an unfiltered run counts."
  "check_fsharp_code type-checks without running. cancel_eval stops a runaway eval, so don't reset for that either."
]

let private numbered (lines: string list) =
  lines |> List.mapi (fun i line -> sprintf "%d. %s" (i + 1) line)

let private bulleted (lines: string list) =
  lines |> List.map (sprintf "- %s")

/// The MCP ServerInstructions text.
let serverInstructions =
  String.concat "\n" [
    "SageFs is a live F# REPL with your project already loaded. An eval takes milliseconds and a dotnet build takes minutes, so the REPL is your inner loop. dotnet build/test/run is the final gate, run once when you're done."
    ""
    "First minute:"
    yield! numbered firstMinute
    ""
    "The loop:"
    yield! numbered loopSteps
    ""
    "Things that bite:"
    yield! bulleted gotchas
    ""
    "If the REPL fights you, don't fall back silently. Report the tool, the input and the exact error, then use dotnet for that one step only."
    "Never stop, restart or reinstall the user's SageFs daemon without asking. It's theirs, and other agents may be using it. stop_session the sessions you created when you're done."
    sprintf "The full rules are in %s in the SageFs repo. If you drift off the loop, the %s prompt puts you back on it." SkillPath BackToTheReplPromptName
  ]

/// The back_to_the_repl prompt text.
let backToTheRepl =
  String.concat "\n" [
    "You left the SageFs REPL loop. Stop what you're doing, and don't finish it the slow way first."
    ""
    "Name the step where you left the loop, and what pulled you off it (a dotnet build or test, a throwaway script, a guess about an API)."
    ""
    "The loop:"
    yield! numbered loopSteps
    ""
    "If the REPL was fighting you, report the tool, the input and the exact error instead of working around it."
    ""
    "Resume from the REPL now, at the step where you left."
  ]

/// The sagefs_loop prompt text: the whole always-on guidance, on demand.
let sageFsLoop = serverInstructions

[<McpServerPromptType>]
type SageFsPrompts() =

  [<McpServerPrompt(Name = BackToTheReplPromptName, Title = "Back to the REPL")>]
  [<Description("Put a drifting agent back on the SageFs REPL loop: name where it left, restate the loop, resume from the REPL.")>]
  static member BackToTheRepl() : string = backToTheRepl

  [<McpServerPrompt(Name = SageFsLoopPromptName, Title = "The SageFs loop")>]
  [<Description("The SageFs working loop in full: the first-minute checklist, the RED/GREEN/persist/rebuild loop, and the things that bite.")>]
  static member SageFsLoop() : string = sageFsLoop
