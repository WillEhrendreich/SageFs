/// The `DemoRuntime` edge (demo-gif-plan.md §5, §4.1, §4.12): the runner
/// side of one recording. It never reaches into a sealed cell directly — it
/// builds the SageFs daemon FROM THIS WORKTREE'S SOURCE (§4.12: never the
/// globally-installed tool, which can silently lag the working tree), builds
/// the exact bwrap cell the Phase-0 spike proved, pipes one
/// `Wire.ScenarioPlan` line into the cell-agent's stdin, reads one
/// `Wire.StepLog` line back off its stdout, and then runs the pure
/// `Compose`/`Ffmpeg` planners over the segments the cell wrote to the shared
/// `/out` bind mount (§4.1's data plane) to actually produce the artifacts.
///
/// Renamed from the plain `SageFs.Demos.Runtime` to `...Runtime.Core` (Island
/// F, demo-actors-plan.md §1.2): F# refuses to compile a real module named
/// `SageFs.Demos.Runtime` alongside sibling per-actor extension modules
/// nested under that same path (`SageFs.Demos.Runtime.VsCode`, `...Neovim`,
/// etc. — FS0247, "used as both a namespace and a module"). A module-
/// declaration rename only — every function body below is unchanged.
module SageFs.Demos.Runtime.Core

open System
open System.Diagnostics
open System.IO
open SageFs.Demos.Domain
// Renaming this file's own module one level deeper (`...Runtime.Core`, see
// above) moved it out of the `SageFs.Demos` namespace its sibling modules
// (`Wire`, `Sandbox`, `Compose`, `Ffmpeg`, `Layout`, ...) live in, so their
// short names (`Wire.ScenarioPlan`, `Sandbox.CellSpec`, ...) need this
// explicit open where they used to resolve implicitly same-namespace.
open SageFs.Demos

// ---------------------------------------------------------------------------
// Small process-spawning helpers. Every one of these is the thin, injected,
// impure edge the pure planners never see (§3).
// ---------------------------------------------------------------------------

let private runCaptured (fileName: string) (args: string list) (workDir: string option) : Async<int * string * string> =
  async {
    let psi = ProcessStartInfo(fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    workDir |> Option.iter (fun d -> psi.WorkingDirectory <- d)
    for a in args do
      psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let stdoutTask = proc.StandardOutput.ReadToEndAsync()
    let stderrTask = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync() |> Async.AwaitTask
    let! stdout = stdoutTask |> Async.AwaitTask
    let! stderr = stderrTask |> Async.AwaitTask
    return proc.ExitCode, stdout, stderr
  }

/// Finds the repo root by walking up from `startDir` looking for
/// `SageFs.slnx` — works whether this tool is run via `dotnet run` (Debug
/// bin dir) or a Release build, from anywhere inside the checkout.
let rec findRepoRoot (startDir: string) : string option =
  if File.Exists(Path.Combine(startDir, "SageFs.slnx")) then
    Some startDir
  else
    match Directory.GetParent(startDir) with
    | null -> None
    | parent -> findRepoRoot parent.FullName

/// §4.12: "Always run the daemon FROM SOURCE in a cell, never the
/// globally-installed `sagefs`" — the installed tool can lag the working
/// tree, producing a baffling "element visible but the Locator never finds
/// it" symptom (the spike's own Stage-4 red herring) rather than a clean
/// failure. This is a `dotnet build`, not `dotnet run`/`publish` — the daemon
/// is then launched inside the cell via `dotnet <dll>` against a RO-bound
/// `DOTNET_ROOT`, exactly like the spike's Stage 4.
let buildDaemonFromSource (repoRoot: string) : Async<Result<string, string>> =
  async {
    let fsproj = Path.Combine(repoRoot, "SageFs", "SageFs.fsproj")
    let! code, stdout, stderr = runCaptured "dotnet" [ "build"; fsproj; "-c"; "Release" ] (Some repoRoot)

    if code = 0 then
      return Ok(Path.Combine(repoRoot, "SageFs", "bin", "Release", "net10.0"))
    else
      return Error(sprintf "dotnet build SageFs.fsproj failed (exit %d):\n%s\n%s" code stdout stderr)
  }

/// Builds `sample`'s real project on the HOST, exactly like
/// `buildDaemonFromSource` builds the daemon — a scenario that opens a REAL
/// project inside the cell (§10, `Scenarios.fs`'s `Text.RepoRootToken` path)
/// needs that project's `obj`/`bin` ALREADY populated before the cell ever
/// runs, because the cell has no network (`Sandbox.fs`'s `--unshare-net`):
/// SageFs's own project loader refuses to build/restore anything itself —
/// it reads already-built outputs and reports "Build the project (dotnet
/// build) before starting a session" otherwise
/// (`SageFs.Core/ProjectLoading.fs`) — so failing to pre-build here would
/// surface as a faulted session inside the recording, not a clean error
/// here. Empirically confirmed offline-safe on this machine: with every
/// package this project needs already resolved into the shared NuGet
/// global-packages cache (`resolveNugetPackagesDir`, RO-bound into the cell
/// alongside the repo at its OWN absolute path so the resulting
/// `obj/project.assets.json`'s baked-in absolute paths keep resolving once
/// the cell starts), `dotnet build` needs no network round-trip at all.
let buildSampleFromSource (repoRoot: string) (sampleRelativeDir: string) : Async<Result<unit, string>> =
  async {
    let projectDir = Path.Combine(repoRoot, sampleRelativeDir)
    let! code, stdout, stderr = runCaptured "dotnet" [ "build"; projectDir; "-c"; "Release" ] (Some projectDir)

    if code = 0 then
      return Ok()
    else
      return Error(sprintf "dotnet build %s failed (exit %d):\n%s\n%s" sampleRelativeDir code stdout stderr)
  }

/// The shared NuGet global-packages folder — `NUGET_PACKAGES` if the host
/// has it set, otherwise NuGet's own documented default
/// (`~/.nuget/packages`). A pre-built sample project's `obj/project.assets.json`
/// bakes in ABSOLUTE paths into this folder for every restored package; the
/// cell needs it RO-bound at this SAME absolute path (never remapped) for
/// those paths to keep resolving once the sandbox's own `HOME` is
/// overridden to a private, empty `/home/demo` (§10).
let private nugetPackagesDir () : string =
  match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
  | null
  | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
  | dir -> dir

/// The `dotnet` muxer's own directory (`DOTNET_ROOT`), resolved the same way
/// the spike's stage4 script did (`dirname "$(readlink -f "$(command -v
/// dotnet)")"`) so the cell can RO-bind exactly the runtime already installed
/// on this machine, with no assumption about where it lives.
let resolveDotnetRoot () : Async<Result<string, string>> =
  async {
    let! code, stdout, stderr = runCaptured "sh" [ "-c"; "dirname \"$(readlink -f \"$(command -v dotnet)\")\"" ] None

    if code <> 0 || String.IsNullOrWhiteSpace stdout then
      return Error(sprintf "could not resolve dotnet root: %s" stderr)
    else
      return Ok(stdout.Trim())
  }

/// The bundled Playwright Chromium the Phase-0 spike proved against (§2's
/// correction: chromium-1208, the revision `Microsoft.Playwright` 1.58.0
/// expects). Not resolved dynamically inside the cell — the cell only ever
/// sees this one RO-bound directory, and the actor is handed its executable
/// path directly, exactly like the spike.
let private chromiumDir () : string =
  Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cache", "ms-playwright", "chromium-1208", "chrome-linux64")

/// The bundled cursor/ripple PNG assets (§8, §9), copied next to this
/// assembly's DLL by the fsproj's `CopyToOutputDirectory` items — resolved
/// the same way `chromiumDir` resolves its own bundled dependency: one fixed
/// path under this tool's own directory, never a runtime download.
let private assetPath (fileName: string) : string =
  Path.Combine(AppContext.BaseDirectory, "assets", fileName)

// ---------------------------------------------------------------------------
// Domain (Scenario) -> Wire (ScenarioPlan) mapping. This is the ONLY place
// the rich planner vocabulary is flattened for the cell-agent — the mapping
// itself is pure, everything around it (paths, ports) is a runtime concern.
// ---------------------------------------------------------------------------

let private testIdSelector (id: DashboardId) : string = sprintf "[data-testid=%s]" (DashboardId.testId id)

/// Resolves any dashboard-reachable `Target` to a CSS/Playwright selector —
/// `DashboardElement` uses the typed `data-testid` vocabulary; `DashboardCssSelector`
/// is the documented raw-selector escape hatch (`Domain.fs`'s own doc: used
/// only when a real element has no `data-testid`, e.g. the eval textarea).
let private dashboardSelector (target: Target) : string option =
  match target with
  | Target.DashboardElement id -> Some(testIdSelector id)
  | Target.DashboardCssSelector selector -> Some selector
  | _ -> None

/// The wire token for a `Domain.ActorId` — the same closed vocabulary
/// `clientToken`/`Wire.ScenarioPlan.Client` already use, extended to every
/// actor (including the App co-actor and `WindowCenter`'s own carried id).
let private actorIdToken (actorId: ActorId) : string =
  match actorId with
  | ActorId.Dashboard -> "dashboard"
  | ActorId.VsCode -> "vscode"
  | ActorId.Neovim -> "neovim"
  | ActorId.App -> "app"
  | ActorId.Agent -> "agent"

/// A VS Code target resolved through the extension host's own
/// `sagefs.debug.rectFor` vocabulary (`DebugRects.RectTarget.parse` in
/// `sagefs-vscode/src/DebugRects.fs`: `"caret"|"editor"|"statusBar"|
/// "view:<id>"`, roast H5 — never CDP/DOM). `EditorPosition`'s own
/// line/column cannot be reached exactly through this coarse, ext-host-only
/// API (`Actors/VsCode.fs`'s own documented compromise), so it resolves to
/// the editor's whole content rect rather than a fabricated per-character
/// pixel — a real click still lands inside the real, live-measured editor
/// pane, never a guess at a target this actor cannot honor.
/// `Target.PaletteItem` has no wire mapping yet: opening the command palette
/// and running a NAMED command needs a real `executeCommand` channel
/// `Actors/VsCode.fs`'s `Command` does not implement today (it is a
/// documented no-op, mirroring Dashboard's own) — honest `None`, never a
/// guessed click target, exactly `dashboardSelector`'s own `_ -> None`
/// doctrine.
let private vsCodeTargetSelector (target: Target) : string option =
  match target with
  | Target.EditorPosition _ -> Some "editor"
  | Target.WindowCenter ActorId.VsCode -> Some "editor"
  | _ -> None

/// A Neovim target resolved through `Actors/Neovim.fs`'s own selector
/// vocabulary (`resolveRect`'s `"window-center"|"commandline"|"statusline"|
/// "caret"|"position:<line>:<col>"`) — `EditorPosition`'s line/column DOES
/// reach exactly here (unlike VS Code above), because Neovim's own
/// `screenpos()` genuinely resolves a live pixel for a real buffer
/// position, not a coarse window-manager-level guess.
let private neovimTargetSelector (target: Target) : string option =
  match target with
  | Target.NvimCommandLine -> Some "commandline"
  | Target.WindowCenter ActorId.Neovim -> Some "window-center"
  | Target.EditorPosition(_, line, column) -> Some(sprintf "position:%d:%d" line column)
  | _ -> None

/// Resolves `target` to a wire selector string through whichever live
/// actor `client` is filmed through — the "single extra call site"
/// `Scenarios.VsCode.fs`'s own doc names as the remaining integration gap.
/// Dashboard-reachable targets (`DashboardElement`/`DashboardCssSelector`)
/// resolve identically regardless of `client`, since an editor scenario's
/// narrator pane is a real, live Dashboard actor too (see `expectationWire`
/// below) — only the CLICK side is actually client-specific.
let private targetSelector (client: Client) (target: Target) : string option =
  match dashboardSelector target with
  | Some selector -> Some selector
  | None ->
    match client with
    | Client.VsCode -> vsCodeTargetSelector target
    | Client.Neovim -> neovimTargetSelector target
    | Client.Dashboard
    | Client.Agent -> None

/// Substitutes `Text.RepoRootToken` for the real, absolute repo root — the
/// one runtime fact a pure `Scenario` value can never carry itself (§10).
/// Idempotent no-op on any text that doesn't contain the token.
let private resolveRepoRootToken (repoRoot: string) (text: string) : string =
  text.Replace(Text.RepoRootToken, repoRoot)

/// Which live actor OBSERVES a step's `Expectation`, and the wire token that
/// actor's own `Observe` understands — distinct from the CLICK-side actor
/// because a step's input and its proof-of-effect can come from two
/// different live actors in the same cell (module doc on
/// `Wire.WireStep.ObserveActor`). `Client.Dashboard`/`Client.Agent` keep the
/// exact pre-seam behavior (`None` ⇒ default to `TargetActor`) — this
/// function only ever routes VS Code/Neovim-client expectations elsewhere,
/// never touching the two clients that already worked.
let private expectationWire (client: Client) (expect: Expectation) : string option * string option =
  let dashboardText (selector: string) (text: string) =
    // Playwright's own CSS extension: `:has-text("...")` is a substring,
    // whitespace-normalized text match layered onto a plain CSS selector —
    // exactly what "wait until this element's text contains X" needs,
    // without inventing a second selector mini-language of our own.
    sprintf "%s:has-text(\"%s\")" selector text

  match expect with
  | Expectation.PageShows(id, _) ->
    let selector = Some(testIdSelector id)

    match client with
    | Client.Dashboard
    | Client.Agent -> selector, None
    | Client.VsCode
    | Client.Neovim -> selector, Some "dashboard"
  | Expectation.PageTextContains(selector, text) ->
    let wire = Some(dashboardText selector text)

    match client with
    | Client.Dashboard
    | Client.Agent -> wire, None
    // The daemon's session/eval/live-testing state is one shared source of
    // truth regardless of which client drove the input — an editor-driven
    // step's dashboard-shaped expectation is genuinely, honestly provable
    // through the SAME shared Dashboard narrator pane `EditorFull`/
    // `EditorLeft` always places alongside the editor (never fabricated:
    // the pane is a real, live Chromium window this file also now launches
    // for these clients — see `assembleActors`' co-launch below).
    | Client.VsCode
    | Client.Neovim -> wire, Some "dashboard"
  | Expectation.NvimBufferContains text -> Some(sprintf "buffer-contains:%s" (Text.value text)), Some "neovim"
  | Expectation.AppOutputChanged _ -> Some "app-output-changed", Some "app"
  // "is the app's window/URL discoverable yet" — a real, live presence
  // check through the SAME App co-actor that later observes
  // `AppOutputChanged` (`Actors/App.fs`'s own `resolveRect`), never a guess
  // that the daemon's run-app call "must have worked."
  | Expectation.AppState AppRunStateCase.Running -> Some "app-running", Some "app"
  | Expectation.AppState _ -> None, None
  | Expectation.EditorSaved _ ->
    match client with
    // Neovim's own `&modified` flips to 0 the instant a real `:w` lands —
    // genuine proof of a save, through the SAME actor that typed it.
    | Client.Neovim -> Some "saved", Some "neovim"
    // VS Code has no wired save-observation channel yet (no ext-host
    // status this actor's `Observe` reads today) — honest gap, not faked.
    | Client.VsCode
    | Client.Dashboard
    | Client.Agent -> None, None
  | Expectation.TestOutcome _ -> None, None

let private wireStepOf (repoRoot: string) (client: Client) (index: int) (step: Step) : Wire.WireStep =
  let selectorFor = targetSelector client

  let preClickSelector =
    match step.Action with
    | Action.ClickThenTypeThenClick(preClickTarget, _, _, _, _) -> selectorFor preClickTarget
    | _ -> None

  let clickSelector =
    match step.Action with
    | Action.Click target -> selectorFor target
    | Action.Type(target, _, _) -> selectorFor target
    | Action.TypeThenClick(typeTarget, _, _, _) -> selectorFor typeTarget
    | Action.ClickThenTypeThenClick(_, typeTarget, _, _, _) -> selectorFor typeTarget
    | _ -> None

  let typeText =
    match step.Action with
    | Action.Type(_, text, _) -> Some(Text.value text)
    | Action.TypeThenClick(_, text, _, _) -> Some(Text.value text)
    | Action.ClickThenTypeThenClick(_, _, text, _, _) -> Some(Text.value text)
    | _ -> None
    |> Option.map (resolveRepoRootToken repoRoot)

  let submitSelector =
    match step.Action with
    | Action.TypeThenClick(_, _, _, submitTarget) -> selectorFor submitTarget
    | Action.ClickThenTypeThenClick(_, _, _, _, submitTarget) -> selectorFor submitTarget
    | _ -> None

  let chordKeys =
    match step.Action with
    | Action.Chord keys -> Some(keys |> List.map Key.toToken)
    | _ -> None

  // `Action.Setup`'s wire image: the opaque token `Actors.<X>.command`
  // already accepts. `OpenFile`'s path is resolved to a real, absolute,
  // `{{REPO_ROOT}}`-substituted path HERE (the one place with a `repoRoot`)
  // — never a literal the cell has to interpret further.
  let setupCommand =
    match step.Action with
    | Action.Setup(ClientCommand.OpenFile file) ->
      Some(sprintf "open-file:%s" (IO.Path.Combine(repoRoot, Sample.relativePath file.Sample, file.RelativePath)))
    | Action.Setup ClientCommand.RunApp -> Some "run-app"
    | Action.Setup ClientCommand.StopApp -> Some "stop-app"
    | Action.Setup ClientCommand.SaveAll -> Some "save-all"
    // `ClientCommand.CreateSession`'s wire image (see its own doc on
    // `Domain.fs`): Neovim resolves to its pinned plugin's own
    // non-interactive command, given the RELATIVE `.fsproj` name (nvim's
    // cwd is already `plan.WorkspaceDir` — the sample's own project
    // directory, `CellAgent.fs`'s `Neovim.launch` call); every other client
    // has no such non-interactive editor command, so it resolves to a
    // direct daemon `/api/sessions/create` call instead, given the real,
    // absolute project directory (`Actors/VsCode.fs`'s `command`).
    | Action.Setup(ClientCommand.CreateSession sample) ->
      match client with
      | Client.Neovim -> Some(sprintf "create-session:%s" (Sample.projectFileName sample))
      | Client.VsCode
      | Client.Dashboard
      | Client.Agent -> Some(sprintf "create-session-api:%s" (IO.Path.Combine(repoRoot, Sample.relativePath sample)))
    | _ -> None

  let expectSelector, observeActor = expectationWire client step.Expect

  { Wire.Index = index
    Wire.Caption = Caption.value step.Caption
    Wire.PreClickSelector = preClickSelector
    Wire.ClickSelector = clickSelector
    Wire.TypeText = typeText
    Wire.SubmitSelector = submitSelector
    Wire.ExpectSelector = expectSelector
    Wire.DwellMs = Dwell.ms step.Dwell
    // A joint (multi-actor) scenario's own per-step actor override lands
    // here once one is genuinely needed; every step built by this function
    // today drives the SAME actor its own `Scenario.Client` names, so `None`
    // (⇒ default to the plan's own `Client`) is still correct.
    Wire.TargetActor = None
    Wire.ChordKeys = chordKeys
    Wire.SetupCommand = setupCommand
    Wire.ObserveActor = observeActor }

/// The wire token for a `Domain.Client` (Island F, demo-actors-plan.md
/// §1.2) — the one place the rich `Client` DU is flattened to the primitive
/// string `Wire.ScenarioPlan.Client` carries across the sandbox wall.
let private clientToken (client: Client) : string =
  match client with
  | Client.Dashboard -> "dashboard"
  | Client.VsCode -> "vscode"
  | Client.Neovim -> "neovim"
  // Agent island (demo-actors-plan.md §2.4): the ONLY change this island
  // makes to this function — one new match arm, exactly the shape every
  // other actor's own arm already takes. The Agent actor needs no
  // per-client PageUrl/actorBinds from this file (it opens a `file://` page
  // it writes into the cell itself and calls the daemon's already-bound MCP
  // port directly — see `Actors/Agent.fs`), so nothing else here changes.
  | Client.Agent -> "agent"

/// The fixed ports every cell uses (§4.1: "the same fixed ports" — legal
/// because each cell has a private network namespace, so nothing collides).
[<Literal>]
let private McpPort = 47749

[<Literal>]
let private DashboardPort = 47750

/// Every actor's placed rect for `scenario.Layout`, flattened to the wire's
/// primitive `WireRect` — the seam integration's own missing piece
/// (`assembleActors`/`CellAgent.fs` previously hardcoded a single
/// full-screen Dashboard rect because nothing on the wire ever said
/// otherwise).
let private wireRectsOf (scenario: Scenario) : Wire.WireRect list =
  Layout.rects scenario.Layout { Width = 1280; Height = 720 }
  |> Map.toList
  |> List.map (fun (actorId, rect) ->
    { Wire.ActorToken = actorIdToken actorId
      Wire.X = rect.X
      Wire.Y = rect.Y
      Wire.W = rect.W
      Wire.H = rect.H })

/// The `Target` a step's own `Action` moves toward, if any (mirrors
/// `Storyboard.fs`'s own identically-named private helper — kept separate
/// rather than shared, since that module is a never-touch seam core for a
/// different island and this one has its own reason to exist: resolving
/// which real file Neovim should open, not sketching a cursor path).
let private actionTargetOf (action: Action) : Target option =
  match action with
  | Action.Click target -> Some target
  | Action.Type(target, _, _) -> Some target
  | Action.Typo(target, _, _) -> Some target
  | Action.TypeThenClick(typeTarget, _, _, _) -> Some typeTarget
  | Action.ClickThenTypeThenClick(_, typeTarget, _, _, _) -> Some typeTarget
  | Action.Chord _
  | Action.Setup _
  | Action.Await _ -> None

/// The real, absolute file Neovim should open on launch (`Wire.ScenarioPlan.
/// NvimOpenFilePath`'s own doc: a bare directory argument opens `netrw`, not
/// an editable buffer — confirmed directly against a real recording). Prefers
/// the scenario's own first `Target.EditorPosition`-named file (exact, and
/// genuinely what the scenario's later steps expect to be open); falls back
/// to a real, existing `Program.fs` in the workspace (every runnable/
/// live-testing sample this tool drives has one) when the scenario names no
/// file at all (e.g. `replNeovim`, which types straight into "whatever
/// buffer is open"); `None` only if neither exists.
let private nvimOpenFileOf (repoRoot: string) (scenario: Scenario) : string option =
  let namedFile =
    scenario.Steps
    |> List.tryPick (fun step ->
      actionTargetOf step.Action
      |> Option.bind (function
        | Target.EditorPosition(file, _, _) -> Some(IO.Path.Combine(repoRoot, Sample.relativePath file.Sample, file.RelativePath))
        | _ -> None))

  match namedFile with
  | Some _ -> namedFile
  | None ->
    let defaultProgram = IO.Path.Combine(repoRoot, Sample.relativePath scenario.Sample, "Program.fs")
    if IO.File.Exists defaultProgram then Some defaultProgram else None

let private wirePlanOf
  (repoRoot: string)
  (vsCodeConfig: Wire.VsCodeConfig option)
  (nvimConfig: Wire.NvimConfig option)
  (appConfig: Wire.AppConfig option)
  (scenario: Scenario)
  : Wire.ScenarioPlan =
  { Wire.ScenarioId = ScenarioId.value scenario.Id
    Wire.ChromePath = "/chrome-bin/chrome"
    Wire.PageUrl = sprintf "http://127.0.0.1:%d/dashboard" DashboardPort
    Wire.UserDataDir = "/home/demo/chrome-profile"
    Wire.OutDir = "/out"
    Wire.Steps = scenario.Steps |> List.mapi (wireStepOf repoRoot scenario.Client)
    Wire.Client = clientToken scenario.Client
    Wire.VsCode = vsCodeConfig
    Wire.Nvim = nvimConfig
    Wire.App = appConfig
    Wire.ActorRects = wireRectsOf scenario
    // Only the editor clients open a real project this way (Dashboard drives
    // its own "Open Directory" picker; Agent finds the repo itself inside
    // the cell, `Actors/Agent.fs`'s `RepoRoot.find`).
    Wire.WorkspaceDir =
      match scenario.Client with
      | Client.VsCode
      | Client.Neovim -> Some(IO.Path.Combine(repoRoot, Sample.relativePath scenario.Sample))
      | Client.Dashboard
      | Client.Agent -> None
    Wire.NvimOpenFilePath =
      match scenario.Client with
      | Client.Neovim -> nvimOpenFileOf repoRoot scenario
      | Client.VsCode
      | Client.Dashboard
      | Client.Agent -> None }

// ---------------------------------------------------------------------------
// The cell: exact bwrap shape + inner script (§4.12's proven recipe).
// ---------------------------------------------------------------------------

/// Mirrors the Phase-0 spike's Stage-4 inner script exactly (§4.12): a
/// pre-created `/tmp/.X11-unix` before Xvfb starts (Xvfb refuses to `mkdir`
/// it itself unless euid==0 — satisfied by `--uid 0 --gid 0`, but the
/// directory still has to exist), the daemon on isolated ports/data dir with
/// a health poll before anything is recorded, and the cell-agent as the
/// LAST, foreground command so it inherits the piped `ScenarioPlan` on
/// stdin — the exact control-plane mechanism §4.1 describes. `actorPrologue`
/// is Island F's extension point (demo-actors-plan.md §1.2): each actor
/// island splices ITS OWN launch fragment (start VS Code / kitty+nvim / a
/// second window) here, right after the daemon is confirmed healthy and
/// before the cell-agent is exec'd — never by editing this function again.
/// Island F contributes no fragment of its own, so `actorPrologue = []`
/// reproduces the exact pre-seam script byte-for-line. Triple-quoted so
/// every `$`/`\` below is literal bash, not an F# escape.
let private innerScript (actorPrologue: string list) : string =
  sprintf
    """
set -euo pipefail
export HOME=/home/demo
mkdir -p /home/demo /home/demo/chrome-profile /home/demo/.sagefs
mkdir -m 1777 -p /tmp/.X11-unix

Xvfb :99 -screen 0 1280x720x24 -nocursor &
XVFB_PID=$!
for i in $(seq 1 50); do [ -e /tmp/.X11-unix/X99 ] && break; sleep 0.1; done
[ -e /tmp/.X11-unix/X99 ] || { echo "CELL: Xvfb never came up" >&2; exit 1; }
export DISPLAY=:99

export SAGEFS_DATA_DIR=/home/demo/.sagefs
export SAGEFS_BIND_HOST=127.0.0.1
export DOTNET_ROOT=/dotnet-root
/dotnet-root/dotnet /sagefs-bin/SageFs.dll --mcp-port 47749 --no-watch --no-resume \
  --owner-pid $$ --ttl 5m \
  </dev/null >/out/daemon.log 2>&1 &
DAEMON_PID=$!

DAEMON_UP=0
for i in $(seq 1 80); do
  if curl -s -o /dev/null -m 1 http://127.0.0.1:47749/health; then DAEMON_UP=1; break; fi
  sleep 0.5
done
if [ "$DAEMON_UP" != "1" ]; then
  echo "CELL: daemon never became healthy" >&2
  cat /out/daemon.log >&2
  kill -TERM "$XVFB_PID" 2>/dev/null || true
  exit 1
fi

%s

set +e
/dotnet-root/dotnet /demos-bin/SageFs.Demos.dll cell-agent
CELLAGENT_EXIT=$?
set -e

kill -TERM "$DAEMON_PID" 2>/dev/null || true
for i in $(seq 1 20); do kill -0 "$DAEMON_PID" 2>/dev/null || break; sleep 0.2; done
kill -KILL "$DAEMON_PID" 2>/dev/null || true
kill -TERM "$XVFB_PID" 2>/dev/null || true
for i in $(seq 1 20); do kill -0 "$XVFB_PID" 2>/dev/null || break; sleep 0.1; done
kill -KILL "$XVFB_PID" 2>/dev/null || true
exit "$CELLAGENT_EXIT"
"""
    (actorPrologue |> String.concat "\n")

/// §10: a scenario that opens a REAL project (`Text.RepoRootToken`) needs
/// the repo — and the shared NuGet package cache its pre-built `obj/` refers
/// to by absolute path — actually reachable inside the cell, at the SAME
/// absolute paths they have on the host (a bind mount does not rewrite the
/// bytes of any file it exposes: `obj/project.assets.json`'s own baked-in
/// absolute paths would point at nothing if the repo were remapped to a
/// different mount point). `/dotnet-root` is also added to `PATH` (was
/// `/usr/bin` only) so SageFs's own project-cracking (`Ionide.ProjInfo`,
/// which DOES shell out to `dotnet msbuild` for a design-time build) can
/// find a `dotnet` to run, the same one this cell already RO-binds to
/// launch the daemon itself.
let private cellSpec
  (sagefsBin: string)
  (demosBin: string)
  (dotnetRoot: string)
  (chromeDir: string)
  (hostOutDir: string)
  (repoRoot: string)
  (nugetPackagesDir: string)
  // The scenario's sample project directory (under `repoRoot`). It is RW-bound
  // ON TOP of the read-only `repoRoot` bind so SageFs's project loader
  // (Ionide.ProjInfo) can run its offline design-time MSBuild build, which
  // WRITES intermediates into the project's `obj/` (e.g.
  // `obj/<Config>/<TFM>/*.CoreCompileInputs.cache`). With `repoRoot` read-only
  // that write fails (`MSB3491: Read-only file system`), Ionide silently returns
  // zero projects, and the session warms up with "0 assemblies" — so live
  // testing discovers no tests and every `lt`/`hr`/editor scenario's test-run
  // step times out. Only the sample being recorded is writable; the rest of the
  // repo stays read-only.
  (sampleDir: string)
  // Island F's extension points (demo-actors-plan.md §1.2): each actor
  // island appends ONLY its own RO binds and its own innerScript prologue
  // line(s) here, never editing this function's core again. Both are `[]`
  // until an actor island fills them, reproducing the exact pre-seam cell.
  (actorBinds: (string * string) list)
  (actorPrologue: string list)
  : Sandbox.CellSpec =
  { RoBinds =
      [ "/etc/fonts", "/etc/fonts"
        "/etc/ssl", "/etc/ssl"
        chromeDir, "/chrome-bin"
        demosBin, "/demos-bin"
        sagefsBin, "/sagefs-bin"
        dotnetRoot, "/dotnet-root"
        repoRoot, repoRoot
        nugetPackagesDir, nugetPackagesDir ]
      @ actorBinds
    RwBinds = [ hostOutDir, "/out"; sampleDir, sampleDir ]
    Env =
      [ "HOME", "/home/demo"
        // `/xdotool-bin` is harmless in `PATH` even for a scenario that
        // never binds anything there (a missing directory is simply skipped
        // during lookup) — only the VS Code actor's extension host actually
        // shells out to `xdotool` (`sagefs-vscode/src/Extension.fs`'s
        // `resolveOwnWindowRect`, roast H5's window-manager-level geometry
        // query), and VS Code inherits this SAME cell `PATH`
        // (`Actors/VsCode.fs`'s `launch`).
        //
        // `/dotnet-root` MUST come before `/usr/bin`: the host's `/usr/bin/dotnet`
        // is a symlink to the SYSTEM dotnet (`/usr/share/dotnet`), which is NOT
        // bound into the cell — so if `/usr/bin` wins, `dotnet` resolves to a
        // muxer whose SDK directory does not exist in the sandbox and every
        // MSBuild/Ionide design-time build fails with "No .NET SDKs were found".
        // The mounted SDK lives under `/dotnet-root` (host `DOTNET_ROOT`), so it
        // must be found first. (The daemon itself is launched via an absolute
        // `/dotnet-root/dotnet`, but Ionide.ProjInfo resolves `dotnet` through
        // PATH when it shells out for the design-time build.)
        "PATH", "/dotnet-root:/usr/bin:/xdotool-bin"
        // Explicit, not derived from $HOME (which is the private, empty
        // /home/demo above) — any restore/design-time-build path that
        // recomputes the global-packages location fresh, instead of only
        // trusting a pre-built project's cached `obj/project.assets.json`,
        // must still land on the SAME populated folder this cell RO-binds
        // (§10).
        "NUGET_PACKAGES", nugetPackagesDir
        // §4.12: forced software Mesa — Xvfb segfaults under bwrap on this
        // NVIDIA box without it (glvnd otherwise picks the NVIDIA EGL vendor
        // JSON, which crashes probing for a GBM device inside the sandbox's
        // minimal /dev).
        "LIBGL_ALWAYS_SOFTWARE", "1"
        "__EGL_VENDOR_LIBRARY_FILENAMES", "/usr/share/glvnd/egl_vendor.d/50_mesa.json" ]
    InnerCommand = [ "/bin/sh"; "-c"; innerScript actorPrologue ] }

// ---------------------------------------------------------------------------
// Per-actor cell binds/prologue/wire-config resolution (seam integration):
// the ONE call site that actually invokes each `Runtime.<X>.fs` extension
// module's own resolvers and turns their results into the `cellSpec`
// extension-point arguments plus the `Wire.ScenarioPlan` config fields —
// exactly the wiring `Runtime.VsCode.fs`/`Runtime.Neovim.fs`'s own doc
// comments flag as "not this island's own file to edit."
// ---------------------------------------------------------------------------

let private findOnPath (exeName: string) : string option =
  match Environment.GetEnvironmentVariable "PATH" with
  | null -> None
  | path ->
    path.Split(Path.PathSeparator)
    |> Array.tryPick (fun dir ->
      try
        if File.Exists(Path.Combine(dir, exeName)) then Some dir else None
      with _ ->
        None)

/// Resolves the directory `xdotool` (+ its `libxdo.so.4`, if not system-
/// installed) lives in — the VS Code actor's window-geometry dependency
/// (`Actors/VsCode.fs`'s own module doc: "the demo cell must include
/// xdotool"). `SAGEFS_XDOTOOL_DIR` is the escape hatch for a self-built,
/// non-system binary (this box had no `xdotool` package and no passwordless
/// sudo to install one — a self-contained build with `-Wl,-rpath=$ORIGIN`
/// resolves its own `libxdo.so.4` from the same directory, so pointing this
/// var at that directory is enough); otherwise a real `xdotool` already on
/// `PATH` is used directly. Fails loud — never a silently rect-less VS Code
/// scenario.
let private resolveXdotoolDir () : Result<string, string> =
  match Environment.GetEnvironmentVariable "SAGEFS_XDOTOOL_DIR" with
  | dir when not (String.IsNullOrWhiteSpace dir) && File.Exists(Path.Combine(dir, "xdotool")) -> Ok dir
  | _ ->
    match findOnPath "xdotool" with
    | Some dir -> Ok dir
    | None ->
      Error(
        "xdotool not found — install it (pacman -S xdotool / apt install xdotool), or build it and set "
        + "SAGEFS_XDOTOOL_DIR to a directory containing an `xdotool` binary (plus libxdo.so.4 alongside it "
        + "if not system-installed) — the VS Code actor's sagefs.debug.rectFor window-geometry query needs it "
        + "(roast H5: real OS window geometry, never CDP)."
      )

/// Resolves every cell bind/prologue/`Wire` config `scenario.Client` (and
/// `scenario.App`, for a joint hot-reload scenario) needs, beyond the fixed
/// set `cellSpec` always includes. Fails loud with an actionable message the
/// moment a genuinely-needed dependency is missing — never a silently
/// incomplete cell that would leave an actor's window absent from the
/// recording (§2's "never a silent green no-op" doctrine, applied at the
/// integration seam itself, not just inside each actor).
let private resolveActorExtras
  (repoRoot: string)
  (scenario: Scenario)
  : Async<Result<(string * string) list * string list * Wire.VsCodeConfig option * Wire.NvimConfig option * Wire.AppConfig option, string>> =
  async {
    let appConfig =
      match scenario.App with
      | AppKind.NoApp -> None
      | kind -> Some(Runtime.App.appConfigOf kind)

    let appBinds = Runtime.App.actorBinds scenario.App
    let appPrologue = Runtime.App.actorPrologue scenario.App

    match scenario.Client with
    | Client.Dashboard
    | Client.Agent -> return Ok(appBinds, appPrologue, None, None, appConfig)
    | Client.VsCode ->
      match Runtime.VsCode.resolveCodeBin repoRoot with
      | Error e -> return Error e
      | Ok codeBin ->

      let extDevPath = Runtime.VsCode.extensionDevPath repoRoot

      if not (File.Exists(Path.Combine(extDevPath, "dist", "Extension.js"))) then
        return
          Error(
            sprintf
              "sagefs-vscode extension is not built: no %s — run `npm install && npm run compile` under sagefs-vscode/ first (never a silent skip of the VS Code scenarios)."
              (Path.Combine(extDevPath, "dist", "Extension.js"))
          )
      else

      match resolveXdotoolDir () with
      | Error e -> return Error e
      | Ok xdotoolDir ->

      // `cellBinds` wants the whole VS Code build DIRECTORY (Electron needs
      // its bundled resources next to the binary, not just the executable
      // itself) — `resolveCodeBin` resolves the "code" binary's own full
      // path, so this is its containing directory, never the binary path
      // itself (confirmed directly: passing the binary path here produced
      // a cell-visible `/vscode-bin/code` that did not exist, because the
      // bind mounted the executable's PARENT one level too shallow).
      let codeBinDir = Path.GetDirectoryName codeBin
      let vsCodeBinds = Runtime.VsCode.cellBinds codeBinDir extDevPath @ [ xdotoolDir, "/xdotool-bin" ]
      let vsCodeConfig = Runtime.VsCode.config "/vscode-ext"
      // No bash-level prologue: `Actors/VsCode.fs`'s `launch` owns spawning
      // VS Code itself, exactly like the Dashboard actor owns spawning
      // Chromium (that module's own doc) — nothing to splice into
      // `innerScript` ahead of the cell-agent.
      return Ok(appBinds @ vsCodeBinds, appPrologue, Some vsCodeConfig, None, appConfig)
    | Client.Neovim ->
      match Runtime.Neovim.resolveKitty (), Runtime.Neovim.resolveNvim () with
      | Error e, _
      | _, Error e -> return Error e
      | Ok _, Ok _ ->

      let pluginRepoDir = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, "Work", "sagefs.nvim")

      if not (Directory.Exists(Path.Combine(pluginRepoDir, ".git"))) then
        return
          Error(
            sprintf
              "no sagefs.nvim checkout at %s — clone WillEhrendreich/sagefs.nvim there to record a Neovim scenario."
              pluginRepoDir
          )
      else

      let scratchDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-demos-nvim-plugin-%s" (Guid.NewGuid().ToString "N"))

      // Pinned to the sagefs.nvim commit that adds a non-interactive
      // `:SageFsCreateSession <project>` (skips the `vim.ui.select` project
      // picker synthetic keystrokes cannot answer) — never the floating
      // `"master"` ref (roast I12: a resolved commit, not a moving branch).
      match! Runtime.Neovim.resolvePinnedPlugin pluginRepoDir scratchDir "90bc3f41" with
      | Error e -> return Error e
      | Ok(sha, pluginDir) ->

      let nvimBinds = Runtime.Neovim.actorBinds pluginDir
      let nvimConfig = Runtime.Neovim.nvimConfig sha pluginDir
      return Ok(appBinds @ nvimBinds, appPrologue @ Runtime.Neovim.actorPrologue, None, Some nvimConfig, appConfig)
  }

let private ffmpeg (args: string list) : Async<int * string> =
  async {
    let psi = ProcessStartInfo("ffmpeg", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    for a in args do
      psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let errTask = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync() |> Async.AwaitTask
    let! err = errTask |> Async.AwaitTask
    return proc.ExitCode, err
  }


type RecordedArtifacts =
  { Gif: string
    Mp4: string
    StillsDir: string
    StepsMd: string
    Manifest: string }

let private writeStepsMarkdown (path: string) (scenarioId: string) (log: StepLog) : unit =
  let lines =
    [ sprintf "# %s" scenarioId ]
    @ (log.Steps
       |> List.map (fun s ->
         let outcome = match s.Outcome with Outcome.Passed -> "passed" | Outcome.Failed -> "failed" | Outcome.Skipped -> "skipped"
         sprintf "%d. %s ⟶ stills/step-%02d.png (%s)" (s.Index + 1) (Caption.value s.Caption) s.Index outcome))

  File.WriteAllLines(path, lines)

let private writeManifest (path: string) (scenarioId: string) (log: StepLog) : unit =
  let steps =
    log.Steps
    |> List.map (fun s ->
      {| index = s.Index
         caption = Caption.value s.Caption
         outcome = (match s.Outcome with Outcome.Passed -> "Passed" | Outcome.Failed -> "Failed" | Outcome.Skipped -> "Skipped")
         startedMs = s.StartedMs
         endedMs = s.EndedMs |})

  let manifest =
    {| scenarioId = scenarioId
       recordedAt = DateTimeOffset.UtcNow.ToString("o")
       steps = steps |}

  let json = System.Text.Json.JsonSerializer.Serialize(manifest, System.Text.Json.JsonSerializerOptions(WriteIndented = true))
  File.WriteAllText(path, json)

/// Runs the pure `Compose`/`Ffmpeg` planners over `domainLog`, then executes
/// the resulting `FilterGraph` as ONE real, multi-input ffmpeg invocation
/// (§4.6, per the job's own instruction: `Compose.plan → Ffmpeg.render →
/// toCommandString` must be the actual source of the executed graph, never a
/// parallel hand-built one) — one `-i` per step segment (matching the
/// `Pad.Input i` indices `Ffmpeg.render` wired), the whole planned graph as
/// `-filter_complex`, and two `-map`s pulling the GIF-ready `[outv]` pad and
/// the `[vmp4]` tap (`Ffmpeg.render`'s own 3-way post-decimate split — a
/// filtergraph pad has exactly one consumer, so the mp4 output needs its own
/// tap rather than reusing a palette-branch pad) out of that SAME graph for
/// the two output files — so the `.gif` and the constant-frame-rate `.mp4`
/// are both real products of the one planned filtergraph, not two
/// separately-encoded passes. Writes every artifact §1 names under
/// `artifactsDir`.
let renderArtifacts (scenario: Scenario) (domainLog: StepLog) (artifactsDir: string) : Async<Result<RecordedArtifacts, string>> =
  async {
    Directory.CreateDirectory artifactsDir |> ignore
    let layout = Layout.rects scenario.Layout { Width = 1280; Height = 720 }
    let composePlan = Compose.plan domainLog layout Style.kanagawa
    let filterGraph = Ffmpeg.render composePlan
    let filterComplex = Ffmpeg.toCommandString filterGraph

    let gifPath = Path.Combine(artifactsDir, "scenario.gif")
    let mp4Path = Path.Combine(artifactsDir, "scenario.mp4")
    let stillsDir = Path.Combine(artifactsDir, "stills")
    Directory.CreateDirectory stillsDir |> ignore

    let inputArgs =
      domainLog.Steps
      |> List.sortBy (fun s -> s.Index)
      |> List.collect (fun s -> [ "-i"; s.Segment ])

    // The cursor/ripple asset inputs `Ffmpeg.render` wired at pad indices
    // `total` and `total + 1` (right after every step's own segment input,
    // in exactly this order) — `-loop 1` makes each static PNG repeat for
    // the whole encode instead of ending after its one frame.
    let assetInputArgs =
      [ "-loop"; "1"; "-i"; assetPath "cursor.png"
        "-loop"; "1"; "-i"; assetPath "ripple.png" ]

    let! encodeCode, encodeErr =
      ffmpeg (
        [ "-y" ]
        @ inputArgs
        @ assetInputArgs
        @ [ "-filter_complex"; filterComplex
            "-map"; "[vmp4]"; "-c:v"; "libx264"; "-pix_fmt"; "yuv420p"; mp4Path
            "-map"; "[outv]"; "-loop"; "0"; gifPath ]
      )

    if encodeCode <> 0 then
      return Error(sprintf "ffmpeg render failed:\nfilter_complex: %s\n%s" filterComplex encodeErr)
    else

    for step in domainLog.Steps do
      let stillPath = Path.Combine(stillsDir, sprintf "step-%02d.png" step.Index)
      let! _code, _err = ffmpeg [ "-y"; "-sseof"; "-1"; "-i"; step.Segment; "-frames:v"; "1"; stillPath ]
      ()

    let scenarioIdText = ScenarioId.value scenario.Id
    let stepsMdPath = Path.Combine(artifactsDir, "steps.md")
    let manifestPath = Path.Combine(artifactsDir, "manifest.json")
    writeStepsMarkdown stepsMdPath scenarioIdText domainLog
    writeManifest manifestPath scenarioIdText domainLog

    return
      Ok
        { Gif = gifPath
          Mp4 = mp4Path
          StillsDir = stillsDir
          StepsMd = stepsMdPath
          Manifest = manifestPath }
  }

// ---------------------------------------------------------------------------
// The whole record flow: build -> spawn cell -> map back -> render.
// ---------------------------------------------------------------------------

/// Rewrites the cell-visible `/out/...` path the cell-agent reported into
/// the host path the runner can actually read, via the same RW bind mount
/// that made the file visible on both sides in the first place (§4.1: "a
/// bind mount is just a window onto a host directory").
let private hostPathOf (outDir: string) (cellPath: string) : string =
  if cellPath.StartsWith("/out/") then
    Path.Combine(outDir, cellPath.Substring("/out/".Length))
  else
    cellPath

let private toDomainStepLog (scenario: Scenario) (outDir: string) (wire: Wire.StepLog) : StepLog =
  { ScenarioId = scenario.Id
    Steps =
      wire.Steps
      |> List.map (fun s ->
        { Index = s.Index
          Caption = Caption.mk s.Caption
          Segment = hostPathOf outDir s.Segment
          StartedMs = int s.StartedMs
          EndedMs = int s.EndedMs
          PointerPath = s.PointerPath |> List.map (fun p -> { X = p.[0]; Y = p.[1] })
          ObservedAtMs = int s.ObservedAtMs
          Outcome = (if s.Outcome = "Passed" then Outcome.Passed else Outcome.Failed) }) }

/// Records `scenario` end to end: build the daemon from `repoRoot`'s source,
/// build and run the bwrap cell, and turn the returned `StepLog` into real
/// artifacts under `artifacts/demos/<scenario-id>/`.
let record (repoRoot: string) (scenario: Scenario) : Async<Result<Wire.StepLog * RecordedArtifacts, string>> =
  async {
    match! buildDaemonFromSource repoRoot with
    | Error e -> return Error e
    | Ok sagefsBin ->

    // §10: a scenario referencing `Sample.relativePath` (a real project,
    // never a bare Quick Start temp session) needs that project already
    // built on the HOST before the cell — which has no network — ever
    // starts, or SageFs's own project loader refuses it outright.
    match! buildSampleFromSource repoRoot (Sample.relativePath scenario.Sample) with
    | Error e -> return Error e
    | Ok() ->

    match! resolveDotnetRoot () with
    | Error e -> return Error e
    | Ok dotnetRoot ->

    let demosBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)
    let chromeDir = chromiumDir ()

    if not (File.Exists(Path.Combine(chromeDir, "chrome"))) then
      return Error(sprintf "bundled Playwright chromium not found at %s — install it (playwright install chromium) or update the pinned revision" chromeDir)
    else

    let scenarioIdText = ScenarioId.value scenario.Id
    let artifactsDir = Path.Combine(repoRoot, "artifacts", "demos", scenarioIdText)
    let cellOutDir = Path.Combine(artifactsDir, "_cell", "out")

    if Directory.Exists(Path.Combine(artifactsDir, "_cell")) then
      Directory.Delete(Path.Combine(artifactsDir, "_cell"), true)

    Directory.CreateDirectory cellOutDir |> ignore

    // Seam integration: resolve this scenario's OWN actor binds/prologue/
    // wire configs (dashboard/agent need none — `[] [] None None appConfig`
    // reproduces the exact pre-seam cell for them; VS Code/Neovim genuinely
    // need real, resolved dependencies, and fail loud here rather than
    // producing a silently incomplete cell).
    match! resolveActorExtras repoRoot scenario with
    | Error e -> return Error e
    | Ok(actorBinds, actorPrologue, vsCodeConfig, nvimConfig, appConfig) ->

    let sampleDir = Path.Combine(repoRoot, Sample.relativePath scenario.Sample)
    let spec = cellSpec sagefsBin demosBin dotnetRoot chromeDir cellOutDir repoRoot (nugetPackagesDir ()) sampleDir actorBinds actorPrologue
    let planJson = Wire.serializePlan (wirePlanOf repoRoot vsCodeConfig nvimConfig appConfig scenario)
    let! exitCode, stdout, stderr = Sandbox.run spec planJson

    let stepLogLine =
      stdout.Split('\n')
      |> Array.map (fun l -> l.Trim())
      |> Array.filter (fun l -> l.StartsWith("{"))
      |> Array.tryLast

    match stepLogLine with
    | None ->
      return Error(sprintf "cell produced no StepLog (exit %d)\n--- stdout ---\n%s\n--- stderr ---\n%s" exitCode stdout stderr)
    | Some line ->

    let wireLog = Wire.deserializeStepLog line
    let domainLog = toDomainStepLog scenario cellOutDir wireLog

    match! renderArtifacts scenario domainLog artifactsDir with
    | Error e -> return Error(sprintf "%s\n(cell exit %d, StepLog: %s)" e exitCode line)
    | Ok artifacts -> return Ok(wireLog, artifacts)
  }
