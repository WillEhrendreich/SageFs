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
  "Call get_daemon_status and check the daemon's version against the repo. If it's behind, tell the user."
  "Call list_sessions. A session belongs to a working directory, and a git worktree is its own boundary. Use your own session, and don't create a duplicate."
  "Call get_available_projects, then choose one create_project_session, create_solution_session, or create_bare_session. SageFs builds missing generated state itself before creating the session."
  "Call get_session_status for that exact session until Ready. If it Faults, fix or report the named reason rather than waiting for it to change."
]

let gotchas = [
  "\"Operation could not be completed due to earlier error\" means an earlier statement failed. Fix that statement. Don't reset the session."
  "If a bare Error or Ok in a Result match resolves to the wrong type, something in scope shadows it. Write Result.Error/Result.Ok."
  "Never #r a DLL the session already loaded from the project: two copies of every type, and the lock blocks rebuilds."
  "Never #load a file from a project the session already loaded: same two-copies trap, and it surfaces as a misleading type-incompatibility error. #load only a PURE file."
  "Before an external full build, test suite, or run-app process, acquire acquire_full_build_lease, acquire_test_suite_lease, or acquire_run_app_lease, then release_work_lease when done. Sub-agents on one connection each pass their own agent_name."
  "A filtered test run is never the acceptance check. Only an unfiltered run counts. A filter matching nothing still reports Failed: 0 and exits 0 — read the TRUST line's ran= count."
  "Run the slow gate in a BACKGROUND agent and keep working. Don't poll it, and don't re-roll a full run to escape a flake."
  "check_fsharp_code type-checks without running. cancel_eval stops a runaway eval, so don't reset for that either."
]

/// Editing discipline, kept separate from `gotchas` because it is a SEQUENCE
/// rather than a trap: prove, then persist, then re-verify. It is also the
/// largest avoidable cost in a session, and the rule that has to survive a
/// drifted agent is the one about not writing an unproven change.
///
/// SHORT ON PURPOSE. `serverInstructions` is budgeted under 3000 chars
/// because every client pays for it in tokens on every connection, and the
/// budget was already nearly spent. Measured: this file at HEAD was 2760
/// chars, so a first draft that added 1501 would have blown it. Each line here
/// keeps one rule and drops its explanation — the reasoning lives in
/// skills/sagefs/SKILL.md, which is loaded on an F# task anyway.
let editing = [
  "Prove the change in the REPL before you write it to a file. An eval is under a second; a build that catches your misread type costs minutes, and catches it after you have already edited."
  "Edit with exact, targeted calls — read the region, replace that exact text. Never sed -i, python3 -c file rewrites, or broad regex bulk edits: one that silently matches nothing looks exactly like one that worked."
  "For a repeated mechanical change, write an .fsx, run it through SageFs, and assert that each replacement matched."
  "SageFs.Editing.applyInOrder is the total-or-reported version: a replacement applies or says why it didn't, and an ambiguous find refuses."
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
    "Editing (prove it, then write it):"
    yield! bulleted editing
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
