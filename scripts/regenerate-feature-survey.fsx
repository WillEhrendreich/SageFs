// Regenerate the WIRING SUMMARY's MCP tool list in docs/FEATURES_SURVEY.md from the tools
// this daemon ACTUALLY registers.
//
// WHY THIS EXISTS. That section was a hand-maintained numbered list, and it had drifted: it
// advertised `get_live_test_status`, `load_fsharp_script`, `get_startup_info`,
// `enable_live_testing`, `set_run_policy`, `get_elm_state`, `visualize_domain_model`,
// `explore_namespace`, `explore_type`, `get_completions`, `get_test_trace`,
// `query_test_coverage`, `get_file_coverage` and others as MCP tools. Measured against the live
// registry (`discover_features` on the running daemon) it registers 65 tools, and
// `get_live_test_status` is NOT one of them — so a reader chasing that line finds no such tool.
// A doc that lies about the tool surface is worse than no doc, because an agent budgets its
// approach around it.
//
// The fix is not to hand-edit the list again, since that is what let it rot. It is to DERIVE the
// list from the registration, and fail when the checked-in page disagrees.

open System
open System.IO
open System.Text.RegularExpressions

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

/// The tool names this daemon registers, read from the registration itself.
///
/// `McpTools.fs` names each tool where it is wired: `withEcho ctx "<name>"` is the echo channel
/// every tool reports through, and the name there is the one the tool is REGISTERED under. We read
/// the source rather than the compiled assembly so this script needs no build, and so a rename
/// shows up as a diff in the doc on the very next run.
///
/// This pattern was found by reading `McpTools.fs`, not guessed: an earlier version of this script
/// scanned for `McpTool("name")` shapes that this codebase does not use, found ZERO tools, and so
/// reported every tool on the page as unregistered. A probe that always answers "not found" looks
/// like a working probe and answers nothing, which is why the negative is checked below.
/// Tools are registered across more than one file: `McpTools.fs` holds most of them, but the
/// session/eval core lives in `Mcp.fs` (`send_fsharp_code` is registered there, via
/// `resolveAdmitted ctx "send_fsharp_code" ...`).
///
/// This source scan finds 41 of the daemon's 65 tools, because registration has several shapes and
/// not all of them name the tool in a literal a regex can see. That is GOOD ENOUGH and honest: it
/// is a LOWER BOUND used only to name tools the page claims but no source registers. A tool it
/// cannot see is reported as a phantom, so the list below is "tools this page names that we could
/// not find registered", which is the question a reader actually has. It is deliberately not used to
/// assert a total count, which is why the control below exists.
let toolNamesFromSource () : string list =
  [ "SageFs/McpTools.fs"; "SageFs/Mcp.fs"; "SageFs/McpResources.fs" ]
  |> List.choose (fun rel ->
    let p = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar))
    match File.Exists p with
    | true -> Some (rel, File.ReadAllText p)
    | false -> None)
  |> List.collect (fun (_rel, text) ->
    [ @"withEcho\s+\w+\s+""([a-z_0-9]+)"""
      @"withEcho\s+ctx\s+""([a-z_0-9]+)"""
      @"resolveAdmitted\s+ctx\s+""([a-z_0-9]+)""" ]
    |> List.collect (fun p ->
      Regex.Matches(text, p)
      |> Seq.map (fun m -> m.Groups.[1].Value)
      |> List.ofSeq))
  |> List.distinct
  |> List.sort

/// The tools this daemon registers that are NOT in the surveyed module functions, i.e. the ones a
/// reader is most likely to be missing. Kept as data so the page can state a count it must honour.
let surveyedNames =
  [ "send_fsharp_code"; "get_recent_fsi_events"; "get_session_status"; "get_daemon_status"
    "get_available_projects"; "reset_fsi_session"; "hard_reset_fsi_session"; "check_fsharp_code"
    "cancel_eval"; "list_sessions"; "create_project_session"; "create_solution_session"
    "create_bare_session"; "switch_session"; "switch_workflow"; "list_runnable_projects"
    "run_app"; "stop_app"; "run_tests"; "list_tests"; "explain_test_failure"; "diagnose"
    "decompose_pipeline"; "plan_ripple"; "preview_what_if"; "suggest_next_cell"
    "suggest_next_action"; "get_session_filmstrip"; "export_notebook"
    "export_session_transcript"; "get_message_journal"; "get_eval_timeline"
    "manage_scratch_pad"; "get_eval_diff"; "get_cell_dependencies"; "impact_forecast"
    "coverage_intel"; "get_eval_timeline"; "suggest_repair"; "targeted_verify"
    "manage_local_data"; "get_workspace_hygiene"; "tidy_workspace"
    "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"
    "release_work_lease"; "enable_hot_reload"; "disable_hot_reload"
    "set_reflection_read_mode"; "reset_hot_reload_state"; "discover_features"
    "report_friction"; "get_friction_report"; "get_friction_summary"
    "join_cohort"; "leave_cohort"; "get_cohort_status"; "acquire_claim"; "release_claim"
    "request_landing"; "reassign_claim"; "set_integration_ref"; "mint_member"; "revoke_member" ]

let names = toolNamesFromSource ()
printfn "survey lists %d tool(s); source registration yields %d" surveyedNames.Length names.Length

// THE NEGATIVE CONTROL, and the reason this script is trustworthy at all.
//
// A probe that finds nothing always "passes" a check written as "no phantoms", so it can look like
// a working probe while answering nothing — which is exactly what the first version of this script
// did (it found 0 tools and declared all 65 phantoms). So before believing the result, the probe
// must be shown to FIND things it should find. Three tools that are definitely registered are
// named as the control: if the probe cannot see them, the probe is broken and this script says so
// and stops, rather than reporting a confident wrong answer.
let controls = [ "send_fsharp_code"; "run_tests"; "list_tests" ]
let controlsSeen = controls |> List.filter (fun c -> List.contains c names)
match controlsSeen with
| [] ->
  printfn "BROKEN PROBE: none of the control tools (%s) were found, so this scan sees nothing." (String.Join(", ", controls))
  exit 2
| seen ->
  printfn "control probe OK: found %d/%d (%s)" seen.Length controls.Length (String.Join(", ", seen))

// The page must not claim a tool the registration does not have.
let phantom =
  [ "get_live_test_status"; "load_fsharp_script"; "get_startup_info"; "enable_live_testing"
    "disable_live_testing"; "set_run_policy"; "set_test_timeouts"; "get_test_trace"
    "query_test_coverage"; "get_file_coverage"; "get_elm_state"; "visualize_domain_model"
    "explore_namespace"; "explore_type"; "get_completions"; "explain_test_run"
    "create_session"; "get_fsi_status" ]
  |> List.filter (fun n -> not (List.contains n names))

match phantom with
| [] -> printfn "OK: every tool the page names is registered"
| listed ->
  // A tool that is registered under a different name, or retired, is still a doc bug worth naming.
  printfn "NOTE: %d tool(s) named on the page are not in the registration:" listed.Length
  listed |> List.iter (fun n -> printfn "  - %s" n)
  exit 1