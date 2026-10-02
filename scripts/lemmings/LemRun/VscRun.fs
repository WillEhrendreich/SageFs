/// The VS Code lemming and the VS Code tour: the process plumbing around the F# driver
/// (scripts/lemmings/ui/LemDrive, `LemDrive.dll vsc|host|oracle|fellover ...`). Every decision that is
/// more than starting or stopping a process (settings, readiness, the oracle, scoring) is the
/// driver's or LemScore's; this starts Xvfb and VS Code, runs cmdc, and tears it all down.
///
/// What this starts, and the rule it follows for each:
///   Xvfb       on a display number it picks itself, NEVER 0 and NEVER -displayfd. On a Hyprland
///              machine the real desktop's Xwayland sits on :0 behind a socket in /tmp/.X11-unix, and
///              an Xvfb asked to find a free display took :0 and deleted the real socket file. So: a
///              number from a fixed high range, and only if no socket and no lock for it exist.
///   VS Code    the real /usr/share/code/code, inside bubblewrap, with its own pid namespace, a
///              cleared environment, a tmpfs HOME and only the run directory writable. It never sees
///              the real desktop's Wayland socket or any real profile.
/// Everything started is recorded by exact pid; nothing is ever ended by name.
///
/// What is protected, and how:
///   - the shared daemon on 37749 is never started, stopped or restarted here, and the extension's
///     autoStart is off. Only this run's own sessions are stopped, by id. The daemon's pid is read
///     before and after, so a stop by anything in the run shows up in teardown.txt.
///   - the lemming's own session: the harness creates a session for the run's `w` directory on the
///     shared daemon, waits for it, makes the window use it and proves that from the Sessions view. If
///     it cannot, the run does not start, so a lemming never evaluates in another agent's session.
///   - the real desktop: Xvfb on :80 and up (never :0), Wayland and every XDG variable cleared, VS Code
///     inside bubblewrap, and `hyprctl clients` compared before and after.
///
/// Residual risk, said plainly: the network is not isolated (the model API and the daemon are reached
/// over localhost). A lemming that goes looking can still reach the daemon's HTTP routes and VS Code's
/// CDP port (LEM_CDP_PORT is in its environment, the driver needs it). The guard, the shim, the unbound
/// keys and the read-only mounts keep a good-faith model from doing that by accident; they are not a
/// boundary against a hostile one.
module LemRun.VscRun

open System
open System.Net.Http
open System.IO
open System.Text.RegularExpressions
open LemRun.Failure
open LemScore.Types

// ---- named values -------------------------------------------------------------------------------

let private displayFirst () = Env.intVarOr "LEM_DISPLAY_FIRST" 80
let private displayLast () = Env.intVarOr "LEM_DISPLAY_LAST" 179
let private cdpPortFirst () = Env.intVarOr "LEM_CDP_PORT_FIRST" 39200
let private cdpPortLast () = Env.intVarOr "LEM_CDP_PORT_LAST" 39799
let private vscodeBinary () = Env.varOr "LEM_VSCODE_BIN" "/usr/share/code/code"
let private screenGeometry () = Env.varOr "LEM_SCREEN_GEOMETRY" "1600x1000x24"
let private xStartSeconds () = Env.intVarOr "LEM_X_START_SECONDS" 15
let private codeStopSeconds () = Env.intVarOr "LEM_CODE_STOP_SECONDS" 20
/// The run directory path, at most this long: VS Code's IPC socket is under <run>/.lem/vsc and a socket
/// path past about 107 characters fails with `listen EINVAL`.
let private maxRunPath () = Env.intVarOr "LEM_MAX_RUN_PATH" 75
let private cdpAnswerSeconds = 60
let private extensionReadySeconds = 90
let private xvfbStop = TimeSpan.FromSeconds 5.0
let private afterCmdc = TimeSpan.FromSeconds 2.0
let private leftoversWaitSeconds = 10
let private cdpPortTries = 300
let private tourTail = 25
let private x11Dir = "/tmp/.X11-unix"

// ---- the pieces that are pure --------------------------------------------------------------------

/// The sandbox VS Code (and nothing else) runs in. The run directory is writable (the window edits the
/// workspace and keeps its profile there), but four places inside it are laid over read-only, because
/// whatever runs in the window (a terminal that got past the driver's guard, an extension) must not
/// be able to rewrite the harness's evidence or the tools the lemming runs:
///   out/                                          the oracle's inputs: daemon-evals.tsv, timeline.ndjson, screens
///   bin/                                          the driver stubs the lemming runs, the sagefs shim
///   .lem/vsc/User/settings.json and keybindings.json   the guard's own settings
/// `overlays` are those that exist; `stored` are the builds the window runs (the driver, the bridge).
let windowArgs (home: string) (run: string) (display: string) (stored: string list) (overlays: string list) : string list =
  let roBind (p: string) = [ "--ro-bind"; p; p ]
  let setenv (name: string) (value: string) = [ "--setenv"; name; value ]
  let x = sprintf "%s/X%s" x11Dir (display.TrimStart ':')
  [ [ "--die-with-parent"; "--unshare-pid"; "--unshare-ipc"; "--unshare-uts" ]
    roBind "/usr" @ roBind "/etc"
    [ "--symlink"; "usr/bin"; "/bin"; "--symlink"; "usr/sbin"; "/sbin"; "--symlink"; "usr/lib"; "/lib"; "--symlink"; "usr/lib64"; "/lib64" ]
    [ "--proc"; "/proc"; "--dev"; "/dev"; "--tmpfs"; "/tmp" ]
    roBind x
    [ "--bind"; run; run ]
    (overlays |> List.collect roBind)
    roBind (Path.Combine(home, ".dotnet"))
    (stored |> List.collect roBind)
    [ "--clearenv" ]
    setenv "HOME" (Path.Combine(run, ".lem", "home"))
    setenv "XDG_CONFIG_HOME" (Path.Combine(run, ".lem", "home", ".config"))
    setenv "XDG_CACHE_HOME" (Path.Combine(run, ".lem", "home", ".cache"))
    setenv "XDG_DATA_HOME" (Path.Combine(run, ".lem", "home", ".local", "share"))
    setenv "XDG_STATE_HOME" (Path.Combine(run, ".lem", "home", ".local", "state"))
    setenv "TMPDIR" (Path.Combine(run, ".lem", "tmp"))
    setenv "DISPLAY" display
    setenv "DOTNET_ROOT" (Path.Combine(home, ".dotnet")) @ setenv "DOTNET_NOLOGO" "1" @ setenv "DOTNET_CLI_TELEMETRY_OPTOUT" "1"
    setenv "DOTNET_CLI_HOME" (Path.Combine(run, ".lem", "tmp")) @ setenv "NODE_NO_WARNINGS" "1"
    setenv "PATH" (sprintf "%s:%s:/usr/bin:/bin" (Path.Combine(run, "bin", "vsc-path")) (Path.Combine(home, ".dotnet")))
    [ "--chdir"; Path.Combine(run, "w") ] ]
  |> List.concat

/// The command line VS Code is started with. The window opens on the run's working directory, the same
/// directory the lemming works in, so the sessions the extension makes are keyed by it and show up
/// under its name in the dashboard.
let codeArgs (run: string) (cdpPort: int) (extensionDir: string) : string list =
  [ sprintf "--user-data-dir=%s" (Path.Combine(run, ".lem", "vsc")); sprintf "--extensions-dir=%s" (Path.Combine(run, ".lem", "vsc-ext"))
    sprintf "--extensionDevelopmentPath=%s" extensionDir
    sprintf "--remote-debugging-port=%d" cdpPort; "--remote-allow-origins=*"
    "--ozone-platform=x11"; "--no-sandbox"; "--disable-gpu"; "--disable-workspace-trust"
    "--new-window"; "--disable-updates"; "--skip-welcome"; "--skip-release-notes"
    Path.Combine(run, "w") ]

/// A fresh profile: no welcome page, no trust prompt, no updates, no telemetry, and the SageFs extension
/// pointed at the shared daemon's ports. autoStart is OFF on purpose: the extension must never start a
/// daemon of its own, because the daemon belongs to the person running the harness. The terminal is
/// disabled in two layers (the profile here, the unbound keys in keybindings.json).
let settingsJson (mcpPort: int) (dashboardPort: int) (extra: string option) : string =
  let tail = match extra with Some e -> sprintf ",\n  %s" e | None -> ""
  String.concat "\n"
    [ "{"
      "  \"security.workspace.trust.enabled\": false,"
      "  \"workbench.startupEditor\": \"none\","
      "  \"update.mode\": \"none\","
      "  \"extensions.autoCheckUpdates\": false,"
      "  \"telemetry.telemetryLevel\": \"off\","
      "  \"chat.disableAIFeatures\": true,"
      "  \"workbench.secondarySideBar.defaultVisibility\": \"hidden\","
      "  \"workbench.tips.enabled\": false,"
      sprintf "  \"sagefs.mcpPort\": %d," mcpPort
      sprintf "  \"sagefs.dashboardPort\": %d," dashboardPort
      "  \"sagefs.autoStart\": false,"
      "  \"terminal.integrated.profiles.linux\": { \"none\": { \"path\": \"/usr/bin/false\" } },"
      "  \"terminal.integrated.defaultProfile.linux\": \"none\","
      "  \"terminal.integrated.automationProfile.linux\": { \"path\": \"/usr/bin/false\" },"
      "  \"terminal.integrated.enablePersistentSessions\": false,"
      "  \"task.allowAutomaticTasks\": \"off\","
      sprintf "  \"debug.openDebug\": \"neverOpen\"%s" tail
      "}\n" ]

/// The keys that open a terminal or the developer tools are unbound, a second layer under the driver's
/// guard (Guard.fs). The file is laid over read-only in the window's sandbox.
let keybindingsJson =
  String.concat "\n"
    [ "["
      "  { \"key\": \"ctrl+`\", \"command\": \"-workbench.action.terminal.toggleTerminal\" },"
      "  { \"key\": \"ctrl+shift+`\", \"command\": \"-workbench.action.terminal.new\" },"
      "  { \"key\": \"ctrl+shift+c\", \"command\": \"-workbench.action.terminal.openNativeConsole\" },"
      "  { \"key\": \"ctrl+shift+i\", \"command\": \"-workbench.action.toggleDevTools\" }"
      "]\n" ]

/// The verbs the lemming types (vsc-snapshot, vsc-click, ...), each a call of the F# driver.
let toolVerbs = [ "snapshot"; "click"; "key"; "type"; "palette"; "open"; "wait"; "shot" ]

/// A one-line executable: the kernel runs `env -S` on it, so there is no shell script. `variables` are
/// set for the call, `command` is what runs, and the script's own arguments follow.
let launcherStub (variables: (string * string) list) (command: string list) : string =
  let assignment (k: string, v: string) = sprintf "%s=\"%s\"" k v
  sprintf "#!/usr/bin/env -S %s\n" (String.Join(" ", (variables |> List.map assignment) @ command))

/// Windows on the real desktop that were not there before, one line each, so a leak is a diff.
let newWindows (before: string list) (after: string list) : string list =
  let counts = before |> List.countBy id |> Map.ofList
  after
  |> List.countBy id
  |> List.collect (fun (line, n) -> List.replicate (max 0 (n - (Map.tryFind line counts |> Option.defaultValue 0))) line)

let private windowTitle = Regex(@"^Window [0-9a-f]+ -> ", RegexOptions.Compiled)

/// `hyprctl clients`, one line per real window.
let desktopWindows () : string list =
  match Proc.which "hyprctl" with
  | None -> []
  | Some _ ->
    (Proc.run (Proc.spec "hyprctl" [ "clients" ]) None).Stdout.Split('\n')
    |> Array.filter (fun l -> l.StartsWith "Window ")
    |> Array.map (fun l -> windowTitle.Replace(l, ""))
    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> Array.toList

// ---- the display and the window ---------------------------------------------------------------

let private lockDir = Env.lemRoot

let private freeDisplay () : int =
  let taken (n: int) =
    File.Exists(sprintf "%s/X%d" x11Dir n) || File.Exists(sprintf "/tmp/.X%d-lock" n)
    || Regex.IsMatch((Proc.run (Proc.spec "ss" [ "-xl" ]) None).Stdout, sprintf @"X%d\b" n)
  match [ displayFirst () .. displayLast () ] |> List.tryFind (taken >> not) with
  | Some n -> n
  | None -> fail (ToolchainMissing (sprintf "no free display number between %d and %d" (displayFirst ()) (displayLast ())))

let private cdpFree (port: int) : bool =
  not ((Proc.run (Proc.spec "ss" [ "-ltn"; sprintf "( sport = :%d )" port ]) None).Stdout.Contains "LISTEN")

let freeCdpPort () : int =
  let rng = Random()
  let rec pick tries =
    match tries >= cdpPortTries with
    | true -> fail (ToolchainMissing "no free CDP port")
    | false ->
      let p = cdpPortFirst () + rng.Next(cdpPortLast () - cdpPortFirst () + 1)
      match cdpFree p with
      | true -> p
      | false -> pick (tries + 1)
  pick 0

/// Choosing a display number and starting Xvfb on it is one step under a lock: the number is only taken
/// once Xvfb has opened its socket, so two runs that start together cannot both see the same number free.
/// Returns the display (":NN") and the running Xvfb.
let startXvfb (out: string) : Result<string * Proc.Running, string> =
  Directory.CreateDirectory lockDir |> ignore
  Store.withLock (Path.Combine(lockDir, ".display.lock")) (fun () ->
    let n = freeDisplay ()
    match n >= 1 with
    | false -> Error (sprintf "refusing display :%d" n)
    | true ->
      let xvfb = Proc.startMerged (Proc.spec "setsid" [ "Xvfb"; sprintf ":%d" n; "-screen"; "0"; screenGeometry (); "-nolisten"; "tcp" ]) (Path.Combine(out, "xvfb.log"))
      let socket = sprintf "%s/X%d" x11Dir n
      let up = Proc.waitUntil (TimeSpan.FromSeconds (float (xStartSeconds ()))) (fun () -> File.Exists socket || not (Proc.isAlive xvfb.Process))
      match up && File.Exists socket with
      | true -> Ok (sprintf ":%d" n, xvfb)
      | false -> Error (sprintf "Xvfb :%d did not start (see %s)" n (Path.Combine(out, "xvfb.log"))))

let private http = lazy (new HttpClient(Timeout = TimeSpan.FromSeconds 2.0))

/// Does the CDP endpoint answer?
let waitCdp (port: int) (seconds: int) : bool =
  Proc.waitUntil (TimeSpan.FromSeconds (float seconds)) (fun () ->
    try (http.Value.GetStringAsync(sprintf "http://127.0.0.1:%d/json/version" port).GetAwaiter().GetResult()).Contains "webSocketDebuggerUrl"
    with _ -> false)

/// Stops a process by its exact pid: TERM, wait, KILL only if it will not go.
let stopProcess (p: Proc.Running) (seconds: int) : Proc.Ended = Proc.terminate p.Process (TimeSpan.FromSeconds (float seconds))

// ---- what the window and the lemming are given -------------------------------------------------

let private driveProject = Path.Combine(Env.lemDir, "ui", "LemDrive", "LemDrive.fsproj")
let private extensionSource = Path.Combine(Env.repoRoot, "sagefs-vscode")

/// The compiled F# driver, built into the store once per set of sources and mounted read-only wherever
/// it is used, so another run's rebuild cannot change what a run in progress runs.
let ensureDriver () : string =
  let dir = Path.GetDirectoryName driveProject |> Option.ofObj |> Option.defaultValue Env.lemDir
  let sources =
    (Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly) |> Array.filter (fun f -> f.EndsWith ".fs" || f.EndsWith ".fsproj") |> Array.toList)
    @ [ Path.Combine(extensionSource, "package.json") ]
  match Store.ensureBuild Env.storeRoot Store.Drive driveProject sources "lemdrive-vsc" with
  | Ok built -> built
  | Error why -> fail (ToolchainMissing why)

/// The extension, built from this repo (npm run compile = Fable then esbuild) when dist is missing or
/// older than its sources, then copied into the run directory so a rebuild cannot change a running
/// trial. Only what a published extension ships is copied.
let prepareExtension (run: string) : string =
  let dest = Path.Combine(run, ".lem", "ext", "sagefs")
  let dist = Path.Combine(extensionSource, "dist", "Extension.js")
  let newest (path: string) =
    match Directory.Exists path with
    | true -> Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) |> Seq.map File.GetLastWriteTimeUtc |> Seq.fold max DateTime.MinValue
    | false -> File.GetLastWriteTimeUtc path
  let stale = not (File.Exists dist) || max (newest (Path.Combine(extensionSource, "src"))) (newest (Path.Combine(extensionSource, "package.json"))) > File.GetLastWriteTimeUtc dist
  match stale with
  | true ->
    say "building the VS Code extension"
    if not (Directory.Exists(Path.Combine(extensionSource, "node_modules"))) then
      match (Proc.run (Proc.spec "npm" [ "ci" ] |> Proc.inDir extensionSource) None).ExitCode with
      | 0 -> ()
      | _ -> fail (ToolchainMissing "npm ci failed in sagefs-vscode")
    let built = Proc.run (Proc.spec "npm" [ "run"; "compile" ] |> Proc.inDir extensionSource) None
    if built.ExitCode <> 0 then fail (ToolchainMissing (sprintf "the extension did not build:\n%s%s" built.Stdout built.Stderr))
  | false -> ()
  Directory.CreateDirectory dest |> ignore
  for name in [ "package.json"; "dist"; "icon.png"; "README.md"; "LICENSE"; "CHANGELOG.md"; "snippets" ] do
    let source = Path.Combine(extensionSource, name)
    match Directory.Exists source, File.Exists source with
    | true, _ -> Store.copyTree source (Path.Combine(dest, name)) |> Result.mapError (fun e -> fail (ToolchainMissing e)) |> ignore
    | _, true -> File.Copy(source, Path.Combine(dest, name), true)
    | _ -> ()
  dest

/// The profile and the keys that are unbound.
let writeProfile (run: string) (shared: CmdRun.Shared) : unit =
  for d in [ ".lem/vsc/User"; ".lem/vsc-ext"; ".lem/home"; ".lem/tmp" ] do Directory.CreateDirectory(Path.Combine(run, d)) |> ignore
  File.WriteAllText(Path.Combine(run, ".lem", "vsc", "User", "settings.json"), settingsJson shared.Port shared.DashPort (Env.var "LEM_VSC_EXTRA_SETTINGS"))
  File.WriteAllText(Path.Combine(run, ".lem", "vsc", "User", "keybindings.json"), keybindingsJson)

let private makeExecutable (path: string) : unit =
  File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)

/// The tools the lemming types: vsc-snapshot, vsc-click, ... Each is a one-line launcher of the F#
/// driver, written into <run>/bin/tools, which the sandbox binds read-only.
let installTools (run: string) (driveDll: string) : unit =
  let dir = Path.Combine(run, "bin", "tools")
  Directory.CreateDirectory dir |> ignore
  for verb in toolVerbs do
    let file = Path.Combine(dir, "vsc-" + verb)
    File.WriteAllText(file, launcherStub [] [ "dotnet"; driveDll; "vsc"; verb ])
    makeExecutable file

/// The `sagefs` on PATH inside the window (the extension and a terminal call it, as for a real user). It
/// runs the read-only verbs against the build this trial uses and refuses the ones that would stop, sweep
/// or start a daemon: the daemon is not the run's to stop.
let installShim (run: string) (driveDll: string) (bridge: Bridge.Bridge) : unit =
  let dir = Path.Combine(run, "bin", "vsc-path")
  Directory.CreateDirectory dir |> ignore
  let real =
    match bridge.Command with
    | "dotnet" :: dll :: _ -> sprintf "dotnet %s" dll
    | _ -> (Proc.which "sagefs" |> Option.defaultValue "sagefs")
  let file = Path.Combine(dir, "sagefs")
  File.WriteAllText(file, launcherStub [ "LEM_SAGEFS_REAL", real ] [ "dotnet"; driveDll; "shim"; "sagefs" ])
  makeExecutable file

// ---- the driver, as the harness calls it ---------------------------------------------------------

/// `dotnet LemDrive.dll <args>` with extra environment; the captured result.
let private drive (driveDll: string) (env: (string * string) list) (args: string list) : Proc.Captured =
  let spec = env |> List.fold (fun s (k, v) -> Proc.withEnv k v s) (Proc.spec "dotnet" (driveDll :: args))
  Proc.run spec None

/// The driver, with its output written to a file, as `> file 2>&1` did.
let private driveToFile (driveDll: string) (env: (string * string) list) (args: string list) (file: string) : int =
  let c = drive driveDll env args
  File.WriteAllText(file, c.Stdout + c.Stderr)
  c.ExitCode

// ---- run-vscode-lemming --------------------------------------------------------------------------

/// What the task changes about how the run is checked.
type TaskKind =
  /// ui-edit-reeval: the project's own suite is run afterwards in the lemming's sandbox (the restore needs the network).
  | RunsSuite
  /// ui-find-help: a question about the editor; no session of the lemming's own is expected.
  | NoSessionExpected
  | Ordinary

let taskKind (task: string) : TaskKind =
  match task with
  | "ui-edit-reeval" -> RunsSuite
  | "ui-find-help" -> NoSessionExpected
  | _ -> Ordinary

/// A run the harness cannot set up is scored HarnessError with the reason, after its window and its
/// sessions are gone, instead of leaving without a summary. No model has been called yet.
exception private HarnessDied of string

/// `run-vscode-lemming <task> <model> [max-turns]`
let runLemming (argv: string list) : int =
  match argv with
  | task :: model :: rest ->
    let turns = match rest with [ t ] -> (match Int32.TryParse t with | true, n when n >= 1 -> n | _ -> fail (Refused (sprintf "max-turns needs a whole number of at least 1, not '%s'" t))) | _ -> CmdRun.defaultTurns
    let uiDir = Path.Combine(Env.lemDir, "ui", "vscode")
    let taskFile = Path.Combine(uiDir, "tasks", task + ".md")
    if not (File.Exists taskFile) then fail (Refused (sprintf "unknown task '%s': see %s" task (Path.Combine(uiDir, "tasks"))))
    // Nothing is started, and no model is called, before these two pass.
    CmdRun.assertFreeModel model
    let shared = CmdRun.useSharedDaemon ()
    let daemonPidStart = CmdRun.daemonPid shared.Port
    // The run is named for the editor as well as the task, so the dashboard and the run root tell a VS
    // Code lemming from a Neovim one that ran the same task: <model>-vsc-<task>-<nn>.
    let runTask = "vsc-" + (if task.StartsWith "ui-" then task.Substring 3 else task)
    let run = CmdRun.resolveRun "auto" model runTask
    for d in [ run.Work; Path.Combine(run.Out, "screens"); Path.Combine(run.Out, "shots"); Path.Combine(run.Out, "final") ] do Directory.CreateDirectory d |> ignore
    File.WriteAllText(Path.Combine(run.Out, "timeline.ndjson"), "")
    say (sprintf "run %s  daemon %s  dashboard http://localhost:%d/dashboard" run.Id shared.DaemonVersion shared.DashPort)
    // The workspace: the project, and the editor's README. Nothing says how SageFs works.
    Workspace.copyFixture (Workspace.Named "demoenv") run.Work
    File.Copy(Path.Combine(uiDir, "VSCODE-TOOLS.md"), Path.Combine(run.Work, "VSCODE-TOOLS.md"), true)
    File.Copy(taskFile, Path.Combine(run.Out, "prompt.md"))
    let driveDir = ensureDriver ()
    let driveDll = Path.Combine(driveDir, "LemDrive.dll")
    let extension = prepareExtension run.Dir
    writeProfile run.Dir shared
    let bridge = Bridge.prepare (Bridge.sourceFromEnv ()) shared.DaemonVersion
    installTools run.Dir driveDll
    installShim run.Dir driveDll bridge
    let stored = driveDir :: (bridge.Mount |> Option.toList)
    let windowsBefore = desktopWindows ()
    File.WriteAllLines(Path.Combine(run.Out, "windows.before"), windowsBefore)
    let xvfb : Proc.Running option ref = ref None
    let code : Proc.Running option ref = ref None
    let codeStop = ref "none"
    let cleaned = ref false
    // The ids of sessions the harness made for this run: stopped with the rest, never counted as residue.
    let own : string list ref = ref []
    let teardownWindow () =
      code.Value |> Option.iter (fun c -> codeStop.Value <- Proc.Ended.toString (stopProcess c (codeStopSeconds ())))
      code.Value <- None
      xvfb.Value |> Option.iter (fun x -> stopProcess x 5 |> ignore)
      xvfb.Value <- None
    let scoreWith (cmdc: CmdRun.Cmdc option) (oracleExit: string) (cleanup: Cleanup) =
      CmdRun.score run runTask model CmdcVscode oracleExit shared bridge cmdc cleanup
    // If this is interrupted, the window goes and the run's sessions are stopped by id, as on any other way out.
    use _interrupt =
      Proc.onInterrupt (fun () ->
        teardownWindow ()
        if not cleaned.Value then CmdRun.cleanupSessions run shared.Port own.Value |> ignore)
    try
      try
        // The lemming's own session, made before the window opens: the extension binds a window to a
        // session on the shared daemon, and without this the one it picks is whichever other agent's
        // session heads the list. Stopped by id with the rest of this run's sessions, including when this
        // script dies before the lemming starts.
        let ownId =
          let c = drive driveDll [] [ "host"; "session"; "--port"; string shared.Port; "--run-dir"; run.Dir; "--out"; Path.Combine(run.Out, "session.id") ]
          File.WriteAllText(Path.Combine(run.Out, "session.out"), c.Stdout + c.Stderr)
          match c.ExitCode with
          | 0 ->
            say (File.ReadAllText(Path.Combine(run.Out, "session.out")).Trim())
            // Stopped with the run's other sessions at the end, but never counted as residue the lemming left.
            File.ReadAllText(Path.Combine(run.Out, "session.id")) |> String.filter (Char.IsWhiteSpace >> not)
          | _ -> raise (HarnessDied (sprintf "could not give the run its own session: %s" ((c.Stdout + c.Stderr).Trim())))
        own.Value <- [ ownId ]
        // Where the shared daemon's sessions stood before the lemming, to tell its evaluations from anyone else's.
        driveToFile driveDll [] [ "host"; "sessions"; "--port"; string shared.Port; "--out"; Path.Combine(run.Out, "sessions.before.tsv") ] (Path.Combine(run.Out, "sessions.before.out")) |> ignore
        // VS Code's own IPC socket lives under the user data dir, and a path past about 107 characters
        // makes it fail with `listen EINVAL` and no window. Refuse early with the reason.
        if run.Dir.Length > maxRunPath () then
          raise (HarnessDied (sprintf "%s is %d characters; VS Code's IPC socket needs the run path under %d" run.Dir run.Dir.Length (maxRunPath ())))
        let display, x =
          match startXvfb run.Out with
          | Ok started -> started
          | Error why ->
            say why
            raise (HarnessDied "no display")
        xvfb.Value <- Some x
        let cdp = freeCdpPort ()
        let overlays =
          [ Path.Combine(run.Dir, "out"); Path.Combine(run.Dir, "bin")
            Path.Combine(run.Dir, ".lem", "vsc", "User", "settings.json"); Path.Combine(run.Dir, ".lem", "vsc", "User", "keybindings.json") ]
          |> List.filter (fun p -> File.Exists p || Directory.Exists p)
        let window =
          windowArgs Env.home run.Dir display stored overlays @ [ vscodeBinary () ] @ codeArgs run.Dir cdp extension
        let c =
          Proc.start (Proc.spec "setsid" ("bwrap" :: window)) (Path.Combine(run.Out, "vscode.stdout")) (Path.Combine(run.Out, "vscode.stderr"))
        code.Value <- Some c
        File.WriteAllText(Path.Combine(run.Out, "window.pids"), sprintf "%s %d %d %d\n" display x.Process.Id c.Process.Id cdp)
        if not (waitCdp cdp cdpAnswerSeconds) then raise (HarnessDied "VS Code did not open its debug port")
        // Wait for the extension to show itself, and for its tree views to register, so the lemming starts
        // from an activated window.
        let ready = driveToFile driveDll [] [ "host"; "ready"; "--cdp-port"; string cdp; "--wait"; string extensionReadySeconds ] (Path.Combine(run.Out, "ready.snapshot.txt"))
        say (sprintf "extension showed itself: %s" (if ready = 0 then "yes" else "no"))
        if ready <> 0 then raise (HarnessDied (sprintf "the SageFs extension never activated; see %s" (Path.Combine(run.Out, "ready.snapshot.txt"))))
        // Attach the window to the run's own session and prove it. No lemming starts on a window that is
        // attached to anyone else's session.
        let bind = drive driveDll [] [ "host"; "bind"; "--cdp-port"; string cdp; "--run-dir"; run.Dir; "--port"; string shared.Port ]
        File.WriteAllText(Path.Combine(run.Out, "bind.out"), bind.Stdout + bind.Stderr)
        match bind.ExitCode with
        | 0 -> say ((bind.Stdout + bind.Stderr).Trim())
        | _ -> raise (HarnessDied ((bind.Stdout + bind.Stderr).Trim()))
        // Run the lemming. Its sandbox gets the tools, the transcript directories and the CDP port.
        let _, cmdcDir = CmdRun.locateCmdc ()
        let published = (match bridge.Command with "sagefs" :: _ -> true | _ -> false)
        // PATH: the tools, then the sagefs shim (the read-only verbs, refusing stop and the rest, the same
        // one the window has), then dotnet. The global tools directory, which holds the real `sagefs`, is not
        // on it and is masked out of the sandbox, unless the run uses the published tool as its bridge,
        // which needs it.
        let lemPath =
          match published with
          | true ->
            say "the published sagefs is this run's bridge, so the real sagefs (which can stop the daemon) is on the lemming's PATH"
            sprintf "%s/bin/tools:%s/.dotnet:%s/.dotnet/tools:%s:/usr/bin:/bin" run.Dir Env.home Env.home cmdcDir
          | false -> sprintf "%s/bin/tools:%s/bin/vsc-path:%s/.dotnet:%s:/usr/bin:/bin" run.Dir run.Dir Env.home cmdcDir
        let extras : CmdRun.Extras =
          { Bwrap =
              [ "--bind"; Path.Combine(run.Out, "screens"); Path.Combine(run.Out, "screens")
                "--bind"; Path.Combine(run.Out, "shots"); Path.Combine(run.Out, "shots")
                "--bind"; Path.Combine(run.Out, "timeline.ndjson"); Path.Combine(run.Out, "timeline.ndjson") ]
            Env =
              [ "PATH", lemPath; "LEM_CDP_PORT", string cdp; "LEM_SCREENS_DIR", Path.Combine(run.Out, "screens"); "LEM_RUN_DIR", run.Dir
                "LEM_TIMELINE", Path.Combine(run.Out, "timeline.ndjson"); "NODE_NO_WARNINGS", "1" ]
            Mounts = [ driveDir ]
            MaskDotnetTools = not published }
        // The daemon keeps no eval history another client can read, so the harness listens to its /events
        // stream for the whole run (the same stream the dashboard uses). The oracle reads the eval output of
        // the lemming's own sessions from what it records, and the fellOver list lines the records up against
        // the actions clock. Stopped by exact pid.
        let events =
          Proc.startMerged
            (Proc.spec "dotnet" [ driveDll; "host"; "events"; "--port"; string shared.Port; "--out"; Path.Combine(run.Out, "daemon-evals.tsv"); "--seconds"; string (Env.Limits.cmdcSeconds ()) ])
            (Path.Combine(run.Out, "daemon-evals.log"))
        let cmdc = CmdRun.runCmdc run (Path.Combine(run.Out, "prompt.md")) model turns shared bridge extras
        Threading.Thread.Sleep afterCmdc
        Proc.sendSignal events.Process.Id Proc.Signal.term
        // What the editor shows when the lemming is done, then the harness's own checks, BEFORE cleanup.
        drive driveDll [] [ "host"; "snapshot"; "--cdp-port"; string cdp; "--out"; Path.Combine(run.Out, "final.snapshot.txt") ] |> ignore
        drive driveDll [ "LEM_CDP_PORT", string cdp; "LEM_SCREENS_DIR", Path.Combine(run.Out, "final") ] [ "vsc"; "shot"; "final" ] |> ignore
        let core = CmdRun.coreFor run [] None false
        // The package restore needs the network; the code the lemming wrote then runs without it.
        match taskKind task with
        | RunsSuite ->
          let restore = CmdRun.sandboxExec core Sandbox.NetworkOpen [] 300 [ "dotnet"; "restore"; "DemoEnv.Tests" ]
          let ran = CmdRun.sandboxExec core Sandbox.NetworkIsolated [] 300 [ "dotnet"; "run"; "--no-restore"; "--project"; "DemoEnv.Tests" ]
          File.WriteAllText(Path.Combine(run.Out, "tests.out"), restore.Stdout + restore.Stderr + ran.Stdout + ran.Stderr)
          File.WriteAllText(Path.Combine(run.Out, "tests.exit"), string ran.ExitCode + "\n")
        | NoSessionExpected | Ordinary -> ()
        let underWork =
          match LemScore.SharedDaemon.listSessions shared.Port with
          | Ok all -> all |> List.filter (LemScore.SharedDaemon.belongsTo run.Work) |> List.map (fun s -> sprintf "%s %s" s.Id s.Status)
          | Error why -> [ sprintf "could not read the sessions list: %s" why ]
        File.WriteAllLines(Path.Combine(run.Out, "dashboard.sessions.txt"), underWork)
        driveToFile driveDll [] [ "host"; "sessions"; "--port"; string shared.Port; "--out"; Path.Combine(run.Out, "sessions.after.tsv") ] (Path.Combine(run.Out, "sessions.after.out")) |> ignore
        drive driveDll [] [ "host"; "foreign"; "--run-dir"; run.Dir; "--before"; Path.Combine(run.Out, "sessions.before.tsv"); "--after"; Path.Combine(run.Out, "sessions.after.tsv"); "--out"; Path.Combine(run.Out, "foreign.tsv") ] |> ignore
        let oracleExit = driveToFile driveDll [ "LEM_CDP_PORT", string cdp ] [ "oracle"; task; "--run-dir"; run.Dir; "--port"; string shared.Port ] (Path.Combine(run.Out, "oracle.out"))
        drive driveDll [] [ "fellover"; "--run-dir"; run.Dir; "--expect-session"; (match taskKind task with NoSessionExpected -> "false" | _ -> "true"); "--sessions"; string (underWork |> List.filter (fun l -> l <> "") |> List.length) ] |> ignore
        // Cleanup: the lemming's own sessions, by id, then the window.
        let cleanupResult = CmdRun.cleanupSessions run shared.Port own.Value
        cleaned.Value <- true
        teardownWindow ()
        let windowsAfter = desktopWindows ()
        File.WriteAllLines(Path.Combine(run.Out, "windows.after"), windowsAfter)
        let leak = newWindows windowsBefore windowsAfter
        if not leak.IsEmpty then say (sprintf "A WINDOW APPEARED ON THE REAL DESKTOP: %s" (String.Join("; ", leak)))
        // Nothing of this run may still be alive. A process is the run's by evidence (a pid this harness
        // started, the session it led, or a working directory inside the run: the workers of the run's
        // sessions), never by a command line that mentions the run path (see Leftovers.fs). The workers of
        // the sessions just stopped take a moment to exit, so the check waits up to ten seconds.
        let pids = String.Join(",", [ x.Process.Id; c.Process.Id; events.Process.Id ])
        let leftC = drive driveDll [] [ "host"; "leftovers"; "--run-dir"; run.Dir; "--pids"; pids; "--wait"; string leftoversWaitSeconds; "--out"; Path.Combine(run.Out, "leftovers.txt") ]
        let left = if leftC.ExitCode = 0 then leftC.Stdout.Trim() else "unknown"
        // The shared daemon is never stopped by this harness. Its pid is compared with the one before the run.
        let daemonPidEnd = CmdRun.daemonPid shared.Port
        let daemonSame = (daemonPidStart.IsSome && daemonPidEnd = daemonPidStart)
        File.WriteAllText(
          Path.Combine(run.Out, "teardown.txt"),
          sprintf "windowStop=%s desktopLeak=%s cleanup=%s processesLeft=%s daemonPid=%s->%s daemon=%s\n" codeStop.Value (if leak.IsEmpty then "no" else "yes") (Cleanup.toString cleanupResult) left (defaultArg daemonPidStart "none") (defaultArg daemonPidEnd "none") (if daemonSame then "same" else "CHANGED"))
        if left <> "0" then say (sprintf "%s process(es) of this run are still alive; see %s" left (Path.Combine(run.Out, "leftovers.txt")))
        if not daemonSame then say (sprintf "THE SHARED DAEMON'S PID CHANGED DURING THE RUN (%s -> %s); it was stopped or restarted by something in the run" (defaultArg daemonPidStart "none") (defaultArg daemonPidEnd "none"))
        let json = scoreWith (Some cmdc) (string oracleExit) cleanupResult
        printfn "%s" json
        say (sprintf "dashboard http://localhost:%d/dashboard  run %s" shared.DashPort run.Dir)
        CmdRun.finishRun run (CmdRun.retentionFromEnv ())
        match leak.IsEmpty, daemonSame with
        | false, _ -> exitCodeOf (DesktopLeak "a window appeared on the real desktop")
        | _, false -> exitCodeOf (DaemonRestarted "the daemon's pid changed")
        | true, true -> 0
      with HarnessDied why ->
        say (sprintf "HarnessError: %s" why)
        teardownWindow ()
        let cleanup = CmdRun.cleanupSessions run shared.Port own.Value
        cleaned.Value <- true
        (try scoreWith (Some { ExitCode = -1; Seconds = 0 }) "skip" cleanup |> ignore with _ -> ())
        4
    finally
      // Whatever way this ends, the window goes and the lemming's own sessions are stopped by id.
      teardownWindow ()
      if not cleaned.Value then CmdRun.cleanupSessions run shared.Port own.Value |> ignore
  | _ -> fail (MissingArgument "usage: run-vscode-lemming <task> <free-model> [max-turns]")

// ---- run-vscode-tour ------------------------------------------------------------------------------

/// The fixture a tour names in its own first comment lines ("# fixture: <name>"), demoenv otherwise.
let tourFixture (text: string) : string =
  text.Split('\n')
  |> Array.tryPick (fun l ->
    let m = Regex.Match(l, @"^# fixture: *(.*)$")
    match m.Success with
    | true -> Some (m.Groups[1].Value.Trim())
    | false -> None)
  |> Option.defaultValue "demoenv"

/// A tour with `other-session` runs a second session in <run>/other, outside the workspace, standing in
/// for another agent on the daemon.
let usesOtherSession (text: string) : bool =
  text.Split('\n') |> Array.exists (fun l -> l.StartsWith "other-session")

let private tourName (tour: string) = Path.GetFileNameWithoutExtension tour

/// One tour on a fresh VS Code with the SageFs extension, as a client of the shared daemon, for the
/// automated design review. No model is called. The run is /tmp/lem/tour-<tour-name>-<nn>, with the
/// numbered shots in out/shots and the driver's call log in out/screens. Returns the exit code.
let private runOneTour (shared: CmdRun.Shared) (driveDir: string) (current: (unit -> unit) ref) (tour: string) : int =
  let driveDll = Path.Combine(driveDir, "LemDrive.dll")
  let name = tourName tour
  let id = LemScore.Catalog.nextRunId Env.lemRoot "tour" name
  let run = CmdRun.runOf id
  for d in [ run.Work; Path.Combine(run.Out, "screens"); Path.Combine(run.Out, "shots") ] do Directory.CreateDirectory d |> ignore
  let text = File.ReadAllText tour
  let fixture = tourFixture text
  if not (Directory.Exists(Path.Combine(Env.lemDir, "fixtures", fixture))) then
    say (sprintf "no fixture '%s' under %s" fixture (Path.Combine(Env.lemDir, "fixtures")))
    2
  else
    Workspace.copyFixture (Workspace.Named fixture) run.Work
    let other = Path.Combine(run.Dir, "other")
    if usesOtherSession text then
      Directory.CreateDirectory other |> ignore
      Workspace.copyFixture (Workspace.Named "demoenv") other
    File.Copy(tour, Path.Combine(run.Out, "tour.txt"), true)
    say (sprintf "tour %s in %s  dashboard http://localhost:%d/dashboard" name run.Dir shared.DashPort)
    prepareExtension run.Dir |> ignore
    writeProfile run.Dir shared
    let windowsBefore = desktopWindows ()
    File.WriteAllLines(Path.Combine(run.Out, "windows.before"), windowsBefore)
    match startXvfb run.Out with
    | Error why ->
      say "no display"
      say why
      4
    | Ok (display, xvfb) ->
      let overlays =
        [ Path.Combine(run.Dir, "out"); Path.Combine(run.Dir, "bin")
          Path.Combine(run.Dir, ".lem", "vsc", "User", "settings.json"); Path.Combine(run.Dir, ".lem", "vsc", "User", "keybindings.json") ]
        |> List.filter (fun p -> File.Exists p || Directory.Exists p)
      let cdp = freeCdpPort ()
      let code =
        Proc.start
          (Proc.spec "setsid" ("bwrap" :: windowArgs Env.home run.Dir display [ driveDir ] overlays @ [ vscodeBinary () ] @ codeArgs run.Dir cdp (Path.Combine(run.Dir, ".lem", "ext", "sagefs"))))
          (Path.Combine(run.Out, "vscode.stdout")) (Path.Combine(run.Out, "vscode.stderr"))
      File.WriteAllText(Path.Combine(run.Out, "window.pids"), sprintf "%s %d %d %d\n" display xvfb.Process.Id code.Process.Id cdp)
      // If this run is interrupted, end only what it started: its own sessions by id, its own window and display by exact pid.
      current.Value <-
        fun () ->
          (if Directory.Exists other then CmdRun.cleanupSessions { run with Work = other } shared.Port [] |> ignore)
          CmdRun.cleanupSessions run shared.Port [] |> ignore
          stopProcess code (codeStopSeconds ()) |> ignore
          stopProcess xvfb 5 |> ignore
      let tourExit =
        match waitCdp cdp cdpAnswerSeconds && (driveToFile driveDll [] [ "host"; "ready"; "--cdp-port"; string cdp; "--wait"; string extensionReadySeconds ] (Path.Combine(run.Out, "ready.snapshot.txt")) = 0) with
        | true ->
          driveToFile driveDll
            [ "LEM_CDP_PORT", string cdp; "LEM_SCREENS_DIR", Path.Combine(run.Out, "screens"); "LEM_SHOTS_DIR", Path.Combine(run.Out, "shots"); "LEM_RUN_DIR", run.Dir; "NODE_NO_WARNINGS", "1" ]
            [ "vsc"; "tour"; tour ] (Path.Combine(run.Out, "tour.out"))
        | false ->
          say (sprintf "the SageFs extension never activated in %s" name)
          4
      // Whatever the tour made on the shared daemon is stopped by id, then checked gone. The second session
      // of an `other-session` tour is under <run>/other and goes the same way, first so that the run's own
      // residue file is the workspace's.
      let otherRun = { run with Work = other }
      let cleanupResult =
        match Directory.Exists other with
        | true ->
          CmdRun.cleanupSessions otherRun shared.Port [] |> ignore
          (try File.Move(Path.Combine(run.Out, "residue.json"), Path.Combine(run.Out, "residue.other.json"), true) with _ -> ())
          CmdRun.cleanupSessions run shared.Port []
        | false -> CmdRun.cleanupSessions run shared.Port []
      let countUnder (dir: string) =
        match LemScore.SharedDaemon.listSessions shared.Port with
        | Ok all -> all |> List.filter (LemScore.SharedDaemon.belongsTo dir) |> List.length
        | Error _ -> 0
      let left = countUnder run.Work + (if Directory.Exists other then countUnder other else 0)
      let stopCode = stopProcess code (codeStopSeconds ())
      let stopX = stopProcess xvfb 5
      current.Value <- ignore
      let windowsAfter = desktopWindows ()
      File.WriteAllLines(Path.Combine(run.Out, "windows.after"), windowsAfter)
      let leak = newWindows windowsBefore windowsAfter
      if not leak.IsEmpty then say (sprintf "A WINDOW APPEARED ON THE REAL DESKTOP: %s" (String.Join("; ", leak)))
      File.WriteAllText(
        Path.Combine(run.Out, "teardown.txt"),
        sprintf "tourExit=%d windowStop=%s displayStop=%s desktopLeak=%s cleanup=%s sessionsLeft=%d\n" tourExit (Proc.Ended.toString stopCode) (Proc.Ended.toString stopX) (if leak.IsEmpty then "no" else "yes") (Cleanup.toString cleanupResult) left)
      File.WriteAllText(Path.Combine(run.Out, "tour.json"), sprintf "{ \"tour\": \"%s\", \"exit\": %d, \"cleanup\": \"%s\" }\n" name tourExit (Cleanup.toString cleanupResult))
      (match File.Exists(Path.Combine(run.Out, "tour.out")) with
       | true -> File.ReadAllLines(Path.Combine(run.Out, "tour.out")) |> Array.rev |> Array.truncate tourTail |> Array.rev |> Array.iter (printfn "%s")
       | false -> ())
      let shots = Directory.GetFiles(Path.Combine(run.Out, "shots"), "*.png").Length
      say (sprintf "shots in %s (%d png)" (Path.Combine(run.Out, "shots")) shots)
      CmdRun.finishRun run (CmdRun.retentionFromEnv ())
      match leak.IsEmpty && left = 0 with
      | true -> tourExit
      | false -> exitCodeOf (DesktopLeak "a window leaked onto the real desktop or a session was left on the dashboard")

/// `run-vscode-tour <tour-file>...`: exit 0 every tour ran and nothing was left behind; 1 a tour step
/// failed; 5 a window leaked onto the real desktop or a session was left on the dashboard.
let runTours (argv: string list) : int =
  match argv with
  | [] -> fail (Refused "usage: run-vscode-tour <tour-file>...")
  | tours ->
    let shared = CmdRun.useSharedDaemon ()
    let driveDir = ensureDriver ()
    let current : (unit -> unit) ref = ref ignore
    // If this is interrupted or terminated mid-tour, end only what this run started. The happy path
    // does the same and clears these, so the handler then has nothing to do. (SIGKILL cannot be caught.)
    use _interrupt =
      Proc.onInterrupt (fun () ->
        say "interrupted: stopping this run's sessions and window"
        current.Value ())
    tours |> List.fold (fun status tour -> max status (runOneTour shared driveDir current tour)) 0
