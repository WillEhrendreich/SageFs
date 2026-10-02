/// The Neovim lemming and the Neovim tour: the process plumbing around the F# driver
/// (scripts/lemmings/ui/nvim, `LemDrive.dll nvim ...`). Everything that is a decision about Neovim
/// (keys, screens, the oracle) is the driver's; this starts and stops the sandbox, tmux and the driver
/// server, and runs cmdc.
///
/// Two sandboxes, on purpose:
///   editor sandbox   tmux server + Neovim + a plain shell. Only the workspace is writable (and not its
///                    .git); see EditorSandbox.fs. The tmux socket lives under out/tmux and is never
///                    mounted into the lemming's sandbox, so the lemming cannot ask tmux for a window.
///   lemming sandbox  cmdc (Sandbox.fs). Here the workspace is READ-ONLY and the only way a file
///                    changes is through the editor. It sees the driver's unix socket (out/ipc), the
///                    driver tool at /lem/drive and, when the task allows it, the plugin README at
///                    /lem/docs. Its .mcp.json is overlaid with an empty server list: a UI lemming
///                    drives the editor, it does not call MCP.
module LemRun.NvimRun

open System
open System.IO
open System.Text.Json
open LemDrive
open LemRun.Failure
open LemScore.Types

// ---- where things are ---------------------------------------------------------------------------

let private nvimBin () = Env.varOr "SAGEFS_LEMMING_NVIM" (Path.Combine(Env.home, ".local", "share", "bob", "nvim-bin", "nvim"))
let private pluginDir () = Env.varOr "SAGEFS_LEMMING_NVIM_PLUGIN" (Path.Combine(Env.home, "Work", "sagefs.nvim"))
let private parserDir () = Env.varOr "LEM_TS_PARSER_DIR" (Path.Combine(Env.home, ".local", "share", "nvim", "site"))
let private nvimDir = Path.Combine(Env.lemDir, "ui", "nvim")
let private driverStartSeconds () = Env.intVarOr "LEM_DRIVER_START_SECONDS" 90
let private editorStopWait = TimeSpan.FromSeconds 10.0
let private driverStopWait = TimeSpan.FromSeconds 10.0

/// Whether a run is a lemming or a tour: a tour needs no model, no cmdc and no credential.
type Mode =
  | LemmingRun
  | TourRun

// ---- preflight ----------------------------------------------------------------------------------

/// Names everything this harness needs and cannot find, all at once, before a run directory, a daemon
/// call or a model call exists. Exits 4 with one line per missing thing and how to get it; the same
/// list is the Prerequisites section of ui/nvim/README.md.
let preflight (mode: Mode) : unit =
  let missing = Collections.Generic.List<string>()
  for tool in [ "bwrap"; "tmux"; "git"; "dotnet" ] do
    if (Proc.which tool).IsNone then missing.Add(sprintf "%s is not on PATH (install it with your package manager; bubblewrap is the package that provides bwrap)" tool)
  if (Proc.which "dotnet").IsSome then
    let sdks = Proc.run (Proc.spec "dotnet" [ "--list-sdks" ]) None
    if not (sdks.Stdout.Split('\n') |> Array.exists (fun l -> l.StartsWith "11.")) then
      missing.Add(sprintf "no .NET 11 SDK (the LemScore tool targets net11.0): dotnet --list-sdks shows %s" (String.Join(",", sdks.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun l -> l.Split(' ')[0]))))
    let runtimes = Proc.run (Proc.spec "dotnet" [ "--list-runtimes" ]) None
    if not (runtimes.Stdout.Split('\n') |> Array.exists (fun l -> l.StartsWith "Microsoft.NETCore.App 10.")) then
      missing.Add "no .NET 10 runtime (the Neovim driver LemDrive.dll targets net10.0)"
  let nvim = nvimBin ()
  if not (File.Exists nvim) then
    missing.Add(sprintf "no Neovim at %s (install one with bob, or point SAGEFS_LEMMING_NVIM at a Neovim 0.10 or newer; the Neovim the runs were made with is a 0.13 nightly)" nvim)
  let parser = parserDir ()
  if not (File.Exists(Path.Combine(parser, "parser", "fsharp.so"))) then
    missing.Add(sprintf "no F# tree-sitter parser at %s/parser/fsharp.so (:TSInstall fsharp in a Neovim with nvim-treesitter, or set LEM_TS_PARSER_DIR to a site directory that has parser/fsharp.so and queries/fsharp)" parser)
  if not (Directory.Exists(Path.Combine(parser, "queries", "fsharp"))) then
    missing.Add(sprintf "no F# tree-sitter queries at %s/queries/fsharp" parser)
  let plugin = pluginDir ()
  if not (File.Exists(Path.Combine(plugin, "lua", "sagefs", "init.lua"))) then
    missing.Add(sprintf "no sagefs.nvim checkout at %s (git clone https://github.com/WillEhrendreich/sagefs.nvim there, or set SAGEFS_LEMMING_NVIM_PLUGIN)" plugin)
  // Directories the sandbox binds: bwrap refuses to start when one is missing.
  if not (Directory.Exists "/run/systemd/resolve") then
    missing.Add "no /run/systemd/resolve (the sandbox binds it for DNS, so it needs systemd-resolved)"
  if mode = LemmingRun then
    if not (Directory.Exists(Path.Combine(Env.home, ".dotnet"))) then
      missing.Add(sprintf "no %s/.dotnet (the lemming sandbox binds the SDK from there; install dotnet with dotnet-install into it)" Env.home)
    match Proc.which "cmdc" with
    | Some cmdc ->
      if not ((Proc.realPath cmdc).StartsWith(Path.Combine(Env.home, ".local", "share", "mise") + "/")) then
        missing.Add(sprintf "cmdc is at %s, outside ~/.local/share/mise, which is the only toolchain directory the lemming sandbox binds (install it with mise)" cmdc)
    | None -> missing.Add "cmdc (Command Code) is not on PATH (install it with mise: it must live under ~/.local/share/mise)"
    if not (Directory.Exists(Path.Combine(Env.home, ".local", "share", "mise"))) then
      missing.Add(sprintf "no %s/.local/share/mise (the lemming sandbox binds cmdc's toolchain from there)" Env.home)
    let auth = Path.Combine(Env.home, ".commandcode", "auth.json")
    if not (File.Exists auth && FileInfo(auth).Length > 0L) then
      missing.Add(sprintf "no %s: log in with cmdc once, outside the harness" auth)
  // Reachability only. The harness never starts a daemon: it is the one the person running it has open in the dashboard.
  match LemScore.SharedDaemon.health LemScore.SharedDaemon.defaultMcpPort with
  | Ok _ -> ()
  | Error _ ->
    missing.Add(sprintf "no SageFs daemon answers on localhost:%d. This harness never starts one. Start the dev daemon yourself from a SageFs checkout, in its own terminal: dotnet build SageFs -c Release, then dotnet SageFs/bin/Release/net11.0/SageFs.dll --no-resume" LemScore.SharedDaemon.defaultMcpPort)
  match missing.Count with
  | 0 -> ()
  | n ->
    eprintfn "lem-nvim: cannot start, %d thing(s) missing:" n
    missing |> Seq.iter (eprintfn "  - %s")
    fail (ToolchainMissing "the Neovim harness is missing what it needs (listed above)")

// ---- the driver, built once ---------------------------------------------------------------------

/// The driver (LemDrive.dll built from LemDriveNvim.fsproj), built into the store once per set of
/// sources and mounted read-only wherever it is used. A tool being edited can fail to build; a build
/// that already exists is still a known tool, so it is used and the run says so.
let ensureDriver (out: string) : string =
  let project = Env.varOr "LEM_DRIVE_PROJECT" (Path.Combine(nvimDir, "LemDriveNvim.fsproj"))
  let sources =
    Directory.GetFiles(Path.GetDirectoryName project |> Option.ofObj |> Option.defaultValue nvimDir, "*", SearchOption.TopDirectoryOnly)
    |> Array.filter (fun f -> f.EndsWith ".fs" || f.EndsWith ".fsproj")
    |> Array.toList
  match Store.ensureBuild Env.storeRoot Store.Drive project sources "lemdrive-nvim" with
  | Ok dir -> dir
  | Error why ->
    let earlier =
      let root = Store.kindDir Env.storeRoot Store.Drive
      match Directory.Exists root with
      | true ->
        Directory.GetDirectories root
        |> Array.filter (fun d -> Path.GetFileName(d).StartsWith "lemdrive-nvim-" && File.Exists(Path.Combine(d, "LemDrive.dll")) && not (d.EndsWith ".building"))
        |> Array.sortByDescending Directory.GetLastWriteTimeUtc
        |> Array.tryHead
      | false -> None
    match earlier with
    | Some dir ->
      say "LemDrive failed to build; using the earlier build"
      Directory.CreateDirectory out |> ignore
      File.WriteAllText(Path.Combine(out, "drive-build.warn"), "LemDrive failed to build; the earlier build was used\n")
      dir
    | None -> fail (ToolchainMissing (sprintf "LemDrive failed to build and there is no earlier build: %s" why))

// ---- the editor's own files for this run -----------------------------------------------------

/// init.lua, the F# tree-sitter parser, tmux config, an empty MCP file, and the docs the lemming may
/// read (the plugin README, when the task says so).
let stage (out: string) (wantReadme: bool) : unit =
  for d in [ "ui/ts/parser"; "ui/ts/queries"; "ipc"; "tmux"; "screens"; "shots"; "docs" ] do
    Directory.CreateDirectory(Path.Combine(out, d)) |> ignore
  File.Copy(Path.Combine(nvimDir, "init.lua"), Path.Combine(out, "ui", "init.lua"), true)
  File.Copy(Path.Combine(parserDir (), "parser", "fsharp.so"), Path.Combine(out, "ui", "ts", "parser", "fsharp.so"), true)
  match Store.copyTree (Path.Combine(parserDir (), "queries", "fsharp")) (Path.Combine(out, "ui", "ts", "queries", "fsharp")) with
  | Ok () -> ()
  | Error e -> fail (ToolchainMissing e)
  File.WriteAllText(Path.Combine(out, "ui", "tmux.conf"), "set -g exit-empty off\nset -g escape-time 0\n")
  File.WriteAllText(Path.Combine(out, "ui", "empty-mcp.json"), "{ \"mcpServers\": {} }\n")
  File.WriteAllText(Path.Combine(out, "ui", "plugin-dir"), pluginDir () + "\n")
  match wantReadme with
  | true -> File.Copy(Path.Combine(pluginDir (), "README.md"), Path.Combine(out, "docs", "README.md"), true)
  | false -> ()

// ---- the editor sandbox and the driver server ------------------------------------------------

let private editorOf (run: CmdRun.Run) (shared: CmdRun.Shared) : EditorSandbox.Editor =
  { Home = Env.home
    Workspace = run.Work
    Out = run.Out
    NvimBin = nvimBin ()
    PluginDir = pluginDir ()
    DaemonPort = shared.Port
    HasGit = Directory.Exists(Path.Combine(run.Work, ".git")) || File.Exists(Path.Combine(run.Work, ".git")) }

/// `tmux -D` is a foreground server with no session; the driver server then creates the nvim and shell
/// windows through its socket, so both run inside this sandbox, and it lives exactly as long as the tmux
/// server does.
let startEditorSandbox (run: CmdRun.Run) (shared: CmdRun.Shared) (label: string) : Proc.Running =
  let args =
    EditorSandbox.args (editorOf run shared) @ [ "tmux"; "-L"; label; "-f"; Path.Combine(run.Out, "ui", "tmux.conf"); "-D" ]
  Proc.startMerged (Proc.spec "bwrap" args) (Path.Combine(run.Out, "editor-sandbox.log"))

/// The harness side of the driver, run OUTSIDE both sandboxes. Returns the running server, and whether
/// the editor is up and the plugin has put its status text on screen.
let startDriverServer (run: CmdRun.Run) (driveDll: string) (label: string) (openFile: string) : Proc.Running * bool =
  let ready = Path.Combine(run.Out, "ready")
  File.Delete ready
  File.Delete(Path.Combine(run.Out, "ipc", "drive.sock"))
  let args =
    [ driveDll; "nvim"; "serve"
      "--tmux-dir"; Path.Combine(run.Out, "tmux"); "--label"; label; "--socket"; Path.Combine(run.Out, "ipc", "drive.sock")
      "--out"; run.Out; "--workspace"; run.Work; "--nvim"; nvimBin (); "--init"; Path.Combine(run.Out, "ui", "init.lua")
      "--open"; openFile; "--ready"; ready ]
  let server = Proc.startMerged (Proc.spec "dotnet" args) (Path.Combine(run.Out, "driver-server.log"))
  let isReady () = File.Exists ready && FileInfo(ready).Length > 0L
  let waited = Proc.waitUntil (TimeSpan.FromSeconds (float (driverStartSeconds ()))) (fun () -> isReady () || not (Proc.isAlive server.Process))
  let up = waited && isReady () && File.ReadLines ready |> Seq.exists (fun l -> l.StartsWith "ready")
  server, up

/// Graceful first: ask tmux to end (nvim gets a hangup), wait, force only if it will not go.
let stopEditorSandbox (run: CmdRun.Run) (label: string) (sandbox: Proc.Running) : string =
  let kill = Proc.spec "tmux" [ "-L"; label; "kill-server" ] |> Proc.withEnv "TMUX_TMPDIR" (Path.Combine(run.Out, "tmux"))
  Proc.run kill None |> ignore
  match Proc.waitUntil editorStopWait (fun () -> not (Proc.isAlive sandbox.Process)) with
  | true -> "graceful"
  | false ->
    Proc.sendSignal sandbox.Process.Id Proc.Signal.term
    "forced"

/// Stops the driver server by its exact pid (SIGTERM lets it finish the call in flight).
let stopDriverServer (server: Proc.Running) : unit =
  match Proc.isAlive server.Process with
  | false -> ()
  | true ->
    Proc.sendSignal server.Process.Id Proc.Signal.term
    match Proc.waitUntil driverStopWait (fun () -> not (Proc.isAlive server.Process)) with
    | true -> ()
    | false -> Proc.sendSignal server.Process.Id Proc.Signal.kill

// ---- tasks ---------------------------------------------------------------------------------------

/// tasks/<task>.env: the fixture, the file to open, and whether the plugin README is shown.
type TaskSettings =
  { Fixture: string
    OpenFile: string
    Readme: bool }

/// The `.env` files are `NAME=value` lines (they used to be sourced by a shell, and still read the same).
let readTaskSettings (file: string) : TaskSettings =
  let values =
    File.ReadAllLines file
    |> Array.choose (fun line ->
      let l = line.Trim()
      match l.StartsWith "#" || not (l.Contains "=") with
      | true -> None
      | false -> Some (l.Substring(0, l.IndexOf '=').Trim(), l.Substring(l.IndexOf '=' + 1).Trim().Trim('"', '\'')))
    |> Map.ofArray
  let get key = Map.tryFind key values |> Option.defaultValue ""
  { Fixture = get "LEM_FIXTURE"; OpenFile = get "LEM_OPEN"; Readme = (get "LEM_README" = "yes") }

// ---- run-nvim-lemming ---------------------------------------------------------------------------

/// What the driver says goes to stderr, as the shell did, so stdout stays the summary.
let private driverToStderr (driveDll: string) (args: string list) : unit =
  let c = Proc.run (Proc.spec "dotnet" (driveDll :: "nvim" :: args) |> Proc.withEnv "DOTNET_NOLOGO" "1") None
  eprintf "%s%s" c.Stdout c.Stderr

/// `run-nvim-lemming <task> <free-model> [max-turns]` or `<task> <free-model> <run-id|auto> [max-turns]`
let runLemming (argv: string list) : int =
  match argv with
  | task :: model :: rest ->
    let isNumber (s: string) = s <> "" && s |> Seq.forall Char.IsDigit
    let idArg, turns =
      match rest with
      | [ t ] when isNumber t -> Env.varOr "LEM_RUN_ID" "auto", int t
      | [ id ] -> id, CmdRun.defaultTurns
      | [ id; t ] -> id, int t
      | _ -> Env.varOr "LEM_RUN_ID" "auto", CmdRun.defaultTurns
    let taskFile = Path.Combine(nvimDir, "tasks", task + ".md")
    let envFile = Path.Combine(nvimDir, "tasks", task + ".env")
    if not (File.Exists taskFile) then fail (Refused (sprintf "no task: %s" taskFile))
    if not (File.Exists envFile) then fail (Refused (sprintf "no task settings: %s" envFile))
    let settings = readTaskSettings envFile
    // Everything this needs, named at once, before anything is created or any model is called.
    preflight LemmingRun
    // Nothing is touched, and no model is called, until the model is confirmed FREE.
    CmdRun.assertFreeModel model
    let run = CmdRun.resolveRun idArg model task
    let label = "lem-" + run.Id
    let openFile = Path.Combine(run.Work, settings.OpenFile)
    let shared = CmdRun.useSharedDaemon ()
    Directory.CreateDirectory run.Work |> ignore
    Directory.CreateDirectory run.Out |> ignore
    say (sprintf "run %s: %s on %s/%s; dashboard http://localhost:%d/dashboard (working directory %s)" run.Id model settings.Fixture task shared.DashPort run.Work)
    // The workspace: only what a new user has.
    Workspace.copyFixture (Workspace.Named settings.Fixture) run.Work
    Workspace.setupFor task |> Option.iter (fun setup -> setup run.Work)
    File.Copy(Path.Combine(nvimDir, "LEMDRIVE.md"), Path.Combine(run.Work, "LEMDRIVE.md"), true)
    // The core sandbox installs the SageFs skill when this directory is absent. A UI lemming drives an
    // editor and has no MCP server, so the skill (all MCP tools) would only mislead it: an empty
    // directory makes the installer skip it.
    Directory.CreateDirectory(Path.Combine(run.Work, ".commandcode", "skills", "sagefs")) |> ignore
    // A session builds the project in place, and the daemon (not the lemming) writes bin/obj and
    // .SageFs. Without these the changed-files list would be mostly build output.
    Workspace.initRepository run.Work
      { UserName = "lemming"; Message = "lemming baseline"; Excludes = [ ".commandcode/"; ".mcp.json" ]
        GitignoreIfAbsent = [ "bin/"; "obj/"; ".SageFs/" ]; Tag = true }
    File.Copy(taskFile, Path.Combine(run.Out, "prompt.md"))
    // ---- the editor
    let driveDir = ensureDriver run.Out
    let driveDll = Path.Combine(driveDir, "LemDrive.dll")
    stage run.Out settings.Readme
    File.WriteAllText(Path.Combine(run.Out, "ui", "drive-dll"), driveDll + "\n")
    let sandbox = startEditorSandbox run shared label
    let server, editorUp = startDriverServer run driveDll label openFile
    let stopped = ref false
    let stopEditor () =
      match stopped.Value with
      | true -> ()
      | false ->
        stopped.Value <- true
        stopDriverServer server
        stopEditorSandbox run label sandbox |> ignore
    // If this is interrupted, the editor and the driver server are ended by exact pid and the run's sessions
    // are stopped by id, as on any other way out.
    use _interrupt =
      Proc.onInterrupt (fun () ->
        stopEditor ()
        CmdRun.cleanupSessions run shared.Port [] |> ignore)
    try
      let cmdc =
        match editorUp with
        | true ->
          say (sprintf "editor ready: %s" (File.ReadAllText(Path.Combine(run.Out, "ready")).Trim()))
          let editor = editorOf run shared
          let bwrap, env = EditorSandbox.lemmingExtras editor driveDir
          // ---- the run
          let result =
            CmdRun.runCmdc run (Path.Combine(run.Out, "prompt.md")) model turns shared (Bridge.none shared.DaemonVersion)
              { CmdRun.noExtras with Bwrap = bwrap; Env = env; Mounts = [ driveDir ] }
          // The last screen, then the daemon as the oracle needs to see it, before any cleanup.
          let screen = Proc.run (Proc.spec "dotnet" [ driveDll; "nvim"; "nvim-screen" ] |> Proc.withEnv "LEM_DRIVE_SOCKET" (Path.Combine(run.Out, "ipc", "drive.sock")) |> Proc.withEnv "DOTNET_NOLOGO" "1") None
          File.WriteAllText(Path.Combine(run.Out, "final-screen.txt"), screen.Stdout + screen.Stderr)
          let state = Proc.run (Proc.spec "dotnet" [ driveDll; "nvim"; "daemon-state"; "--run"; run.Dir ] |> Proc.withEnv "DOTNET_NOLOGO" "1") None
          File.WriteAllText(Path.Combine(run.Out, "daemon-state.out"), state.Stdout + state.Stderr)
          Some result
        | false ->
          let readyText = if File.Exists(Path.Combine(run.Out, "ready")) then File.ReadAllText(Path.Combine(run.Out, "ready")) else ""
          say (sprintf "editor did not come up: %s" (if readyText = "" then "no ready file" else readyText.Trim()))
          File.WriteAllText(Path.Combine(run.Out, "events.ndjson"), "")
          let evidence = JsonSerializer.Serialize(readyText, JsonSerializerOptions(Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
          File.WriteAllText(Path.Combine(run.Out, "fellover.extra.json"), sprintf "[{\"stage\":\"Editor\",\"symptom\":\"the editor sandbox did not come up\",\"evidence\":%s}]\n" evidence)
          Some { CmdRun.ExitCode = -1; CmdRun.Seconds = 0 }
      // ---- afterwards, outside the sandbox
      stopEditor ()
      let cleanupResult = CmdRun.cleanupSessions run shared.Port []
      CmdRun.writeChanged run Workspace.hardenedGit
      let oracleExit =
        match editorUp with
        | true ->
          let core = CmdRun.coreFor run [] None false
          let code = CmdRun.runOracle core run task
          driverToStderr driveDll [ "summarize"; "--run"; run.Dir; "--task"; task; "--plugin"; pluginDir () ]
          code
        | false -> "skip"
      CmdRun.score run task model CmdcNvim oracleExit shared (Bridge.none shared.DaemonVersion) cmdc cleanupResult |> ignore
      driverToStderr driveDll [ "annotate"; "--run"; run.Dir ]
      printf "%s" (File.ReadAllText(Path.Combine(run.Out, "summary.json")))
      CmdRun.finishRun run (CmdRun.retentionFromEnv ())
      0
    finally
      stopEditor ()
  | _ -> fail (MissingArgument "usage: run-nvim-lemming <task> <free-model> [max-turns]  |  <task> <free-model> <run-id|auto> [max-turns]")

// ---- run-nvim-tour -----------------------------------------------------------------------------

/// The first unused `tour-<name>-<nn>` under the run root.
let tourId (name: string) : string =
  let rec pick n =
    let id = sprintf "tour-%s-%02d" name n
    match Directory.Exists(Path.Combine(Env.lemRoot, id)) with
    | true -> pick (n + 1)
    | false -> id
  pick 1

/// `run-nvim-tour <tour-file> [fixture] [open-file] [run-id]`: a tour against a fresh Neovim on a fresh
/// copy of a fixture, with no lemming: the same editor sandbox, the same shared SageFs daemon, numbered
/// shots in <run-dir>/out/shots for a design reviewer. The run is named tour-<tour>-<nn>, so its sessions
/// are recognisable in the dashboard. Exits with the tour's own exit code.
let runTour (argv: string list) : int =
  match argv with
  | tour :: rest ->
    let fixture = match rest with f :: _ -> f | [] -> "demoenv"
    let openRelative = match rest with _ :: o :: _ -> o | _ -> "DemoEnv/DemoEnv.fs"
    let idArg = match rest with _ :: _ :: i :: _ -> i | _ -> "auto"
    if not (File.Exists tour) then fail (Refused (sprintf "no tour file: %s" tour))
    let name = Path.GetFileNameWithoutExtension tour
    if not (Directory.Exists(Path.Combine(Env.lemDir, "fixtures", fixture))) then fail (Refused (sprintf "no fixture: %s" fixture))
    // Everything this needs, named at once, before anything is created (a tour needs no model or cmdc).
    preflight TourRun
    let id = match idArg with "auto" -> tourId name | explicit -> explicit
    let run = CmdRun.runOf id
    if Directory.Exists run.Dir then fail (Refused (sprintf "run %s already exists: pick a new id" id))
    let label = "lem-" + id
    let openFile = Path.Combine(run.Work, openRelative)
    let shared = CmdRun.useSharedDaemon ()
    Directory.CreateDirectory run.Work |> ignore
    Directory.CreateDirectory run.Out |> ignore
    say (sprintf "tour %s: %s on %s; dashboard http://localhost:%d/dashboard (working directory %s)" id name fixture shared.DashPort run.Work)
    Workspace.copyFixture (Workspace.Named fixture) run.Work
    Workspace.initRepository run.Work { UserName = "tour"; Message = "tour baseline"; Excludes = []; GitignoreIfAbsent = []; Tag = false }
    let driveDir = ensureDriver run.Out
    let driveDll = Path.Combine(driveDir, "LemDrive.dll")
    stage run.Out false
    File.WriteAllText(Path.Combine(run.Out, "ui", "drive-dll"), driveDll + "\n")
    let sandbox = startEditorSandbox run shared label
    let stopped = ref false
    let stopEditor () =
      match stopped.Value with
      | true -> ()
      | false ->
        stopped.Value <- true
        stopEditorSandbox run label sandbox |> ignore
    // If this is interrupted, the tour driver and the editor are ended by exact pid, as on any other way out.
    let driver : Proc.Running option ref = ref None
    use _interrupt =
      Proc.onInterrupt (fun () ->
        driver.Value |> Option.iter (fun d -> Proc.terminate d.Process (TimeSpan.FromSeconds 5.0) |> ignore)
        stopEditor ()
        CmdRun.cleanupSessions run shared.Port [] |> ignore)
    try
      let tourExit =
        let running =
          Proc.startTee
            (Proc.spec "dotnet"
               [ driveDll; "nvim"; "tour"; tour
                 "--tmux-dir"; Path.Combine(run.Out, "tmux"); "--label"; label; "--out"; run.Out; "--workspace"; run.Work
                 "--nvim"; nvimBin (); "--init"; Path.Combine(run.Out, "ui", "init.lua"); "--open"; openFile ]
             |> Proc.withEnv "DOTNET_NOLOGO" "1")
            (Path.Combine(run.Out, "tour.out"))
        driver.Value <- Some running
        Proc.finish running
      stopEditor ()
      let cleanupResult = CmdRun.cleanupSessions run shared.Port []
      File.WriteAllText(Path.Combine(run.Out, "tour.json"), sprintf "{ \"tour\": \"%s\", \"exit\": %d, \"cleanup\": \"%s\" }\n" name tourExit (Cleanup.toString cleanupResult))
      say (sprintf "tour %s finished (exit %d): shots in %s, cleanup %s" id tourExit (Path.Combine(run.Out, "shots")) (Cleanup.toString cleanupResult))
      CmdRun.finishRun run (CmdRun.retentionFromEnv ())
      tourExit
    finally
      stopEditor ()
  | [] -> fail (MissingArgument "usage: run-nvim-tour <tour-file> [fixture] [open-file] [run-id]")
