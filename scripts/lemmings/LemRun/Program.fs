/// LemRun: the process plumbing of the lemming harness, one command per documented entry point.
///
///   LemRun run-cmd <fixture> <task> <free-model> <run-id|auto> [max-turns]   run-lemming-cmd
///   LemRun run-ui <vscode|nvim> <task> <free-model> [max-turns]              run-ui-lemming
///   LemRun run-nvim <task> <free-model> [run-id|auto] [max-turns]            run-nvim-lemming
///   LemRun nvim-tour <tour-file> [fixture] [open-file] [run-id]              run-nvim-tour
///   LemRun run-vscode <task> <free-model> [max-turns]                        run-vscode-lemming
///   LemRun vscode-tour <tour-file>...                                        run-vscode-tour
///   LemRun legacy-run <fixture> <task> <model> <run-id> [max-turns]          run-lemming (Claude Code)
///   LemRun legacy-score <events> <residue> --id ID ...                       score (Claude Code)
///   LemRun oracle <task> <run-dir>                  run a task's oracle against a finished run
///   LemRun prune [id ...] [--older-than-days N] [--root R] [--dry-run] [--whole] [--builds]
///   LemRun supervise --ps-dir D -- <command...>     (inside the sandbox: runs cmdc, lists survivors)
///
/// Exit codes are the documented ones (Failure.fs): 0 done, 1 a required argument is missing,
/// 2 refused, 3 the shared daemon will not do, 4 the build or toolchain is missing, 5 and 6 are the
/// editor runners' (a leak onto the real desktop, the daemon restarted).
module LemRun.Program

open System
open System.IO
open LemRun.Failure

let private usageRunCmd = "usage: run-lemming-cmd <fixture> <task> <free-model> <run-id|auto> [max-turns]"

let private parseTurns (text: string) : int =
  match Int32.TryParse text with
  | true, n when n >= 1 -> n
  | _ -> fail (Refused (sprintf "max-turns needs a whole number of at least 1, not '%s'" text))

let private runCmd (argv: string list) : int =
  match argv with
  | fixture :: task :: model :: id :: rest ->
    let turns = match rest with [] -> CmdRun.defaultTurns | [ t ] -> parseTurns t | _ -> fail (Refused usageRunCmd)
    CmdRun.run
      { Fixture = Workspace.parseFixture fixture; Task = task; Model = model; IdArg = id; Turns = turns }
      (CmdRun.modeFromEnv ())
      (CmdRun.retentionFromEnv ())
  | _ -> fail (MissingArgument usageRunCmd)

/// Runs a task's oracle against a run directory that is still whole (it needs w/ and out/), and
/// prints what it said. For checking an oracle against a run, or an oracle against its old shell form.
let private oracle (argv: string list) : int =
  match argv with
  | [ task; runDir ] ->
    match Oracles.forTask task with
    | None -> fail (Refused (sprintf "no oracle for task '%s'" task))
    | Some _ ->
      let run = CmdRun.runOf (Path.GetFileName(runDir.TrimEnd('/')))
      let run = { run with Dir = runDir; Work = Path.Combine(runDir, "w"); Out = Path.Combine(runDir, "out") }
      let code = CmdRun.runOracle (CmdRun.coreFor run [] None false) run task
      printfn "%s" (File.ReadAllText(Path.Combine(run.Out, "oracle.out")).TrimEnd('\n'))
      (match Int32.TryParse code with | true, n -> n | _ -> 0)
  | _ -> fail (MissingArgument "usage: LemRun oracle <task> <run-dir>")

/// `prune`, with one addition: --builds also removes stored builds nobody has used for a while.
let private prune (argv: string list) : int =
  let builds = List.contains "--builds" argv
  let rest = argv |> List.filter (fun a -> a <> "--builds")
  // `--root R`, --dry-run and --whole only qualify a selection; with nothing selected, there is no run to prune.
  let rec selects (args: string list) =
    match args with
    | [] -> false
    | "--root" :: _ :: tail -> selects tail
    | ("--dry-run" | "--whole") :: tail -> selects tail
    | _ -> true
  let collectBuilds () =
    [ Store.Bridge; Store.Drive ]
    |> List.collect (Store.collect Env.storeRoot)
    |> List.iter (fun gone -> printfn "removed the unused stored build %s" gone)
  match builds, selects rest with
  | true, false ->
    collectBuilds ()
    0
  | true, true ->
    let code = LemScore.Program.pruneCommand rest
    collectBuilds ()
    code
  | false, _ -> LemScore.Program.pruneCommand rest

/// Inside the sandbox: runs the command and lists what is still alive when it exits.
let private supervise (argv: string list) : int =
  match argv with
  | "--ps-dir" :: dir :: "--" :: command -> Sandbox.supervise dir command
  | _ -> 2

/// One disposable trial where a Command Code lemming on a FREE model drives a real editor that has the
/// SageFs plugin, as a client of the ONE shared daemon. This only picks the runner for the editor; each
/// editor's runner is its own module, so the two never share code that could break the other.
let private runUi (argv: string list) : int =
  match argv with
  | editor :: (_ :: _ :: _ as rest) ->
    match editor with
    | "vscode" | "vsc" -> VscRun.runLemming rest
    | "nvim" | "neovim" -> NvimRun.runLemming rest
    | other ->
      eprintfn "run-ui-lemming: unknown editor '%s' (vscode or nvim)" other
      2
  | _ ->
    eprintfn "usage: run-ui-lemming <vscode|nvim> <task> <model> [max-turns]"
    2

let private run (argv: string list) : int =
  match argv with
  | "run-cmd" :: rest -> runCmd rest
  | "run-ui" :: rest -> runUi rest
  | "run-nvim" :: rest -> NvimRun.runLemming rest
  | "nvim-tour" :: rest -> NvimRun.runTour rest
  | "run-vscode" :: rest -> VscRun.runLemming rest
  | "vscode-tour" :: rest -> VscRun.runTours rest
  | "legacy-run" :: rest -> Legacy.runCommand rest
  | "legacy-score" :: rest -> Legacy.scoreCommand rest
  | "oracle" :: rest -> oracle rest
  | "prune" :: rest -> prune rest
  | "supervise" :: rest -> supervise rest
  | _ ->
    eprintfn "usage: LemRun run-cmd|run-ui|run-nvim|nvim-tour|run-vscode|vscode-tour|legacy-run|legacy-score|oracle|prune|supervise ..."
    1

[<EntryPoint>]
let main argv =
  try
    // The supervisor runs inside the sandbox, where the checkout is not mounted; everything else needs it.
    match List.ofArray argv with
    | "supervise" :: _ -> ()
    | _ -> if Env.lemDir = "" then fail (ToolchainMissing "could not find scripts/lemmings above the binary (set LEM_DIR)")
    run (List.ofArray argv)
  with
  | Stop failure ->
    eprintfn "lem: %s" (describe failure)
    exitCodeOf failure
  | ex ->
    // Not one of the documented failures: say what it was, with a code of its own (EX_SOFTWARE).
    eprintfn "lem: unexpected failure: %s" (ex.ToString())
    70
