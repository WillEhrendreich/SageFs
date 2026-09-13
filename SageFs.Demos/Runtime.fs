/// The `DemoRuntime` edge (demo-gif-plan.md §5, §4.1, §4.12): the runner
/// side of one recording. It never reaches into a sealed cell directly — it
/// builds the SageFs daemon FROM THIS WORKTREE'S SOURCE (§4.12: never the
/// globally-installed tool, which can silently lag the working tree), builds
/// the exact bwrap cell the Phase-0 spike proved, pipes one
/// `Wire.ScenarioPlan` line into the cell-agent's stdin, reads one
/// `Wire.StepLog` line back off its stdout, and then runs the pure
/// `Compose`/`Ffmpeg` planners over the segments the cell wrote to the shared
/// `/out` bind mount (§4.1's data plane) to actually produce the artifacts.
module SageFs.Demos.Runtime

open System
open System.Diagnostics
open System.IO
open SageFs.Demos.Domain

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

/// Substitutes `Text.RepoRootToken` for the real, absolute repo root — the
/// one runtime fact a pure `Scenario` value can never carry itself (§10).
/// Idempotent no-op on any text that doesn't contain the token.
let private resolveRepoRootToken (repoRoot: string) (text: string) : string =
  text.Replace(Text.RepoRootToken, repoRoot)

let private wireStepOf (repoRoot: string) (index: int) (step: Step) : Wire.WireStep =
  let preClickSelector =
    match step.Action with
    | Action.ClickThenTypeThenClick(preClickTarget, _, _, _, _) -> dashboardSelector preClickTarget
    | _ -> None

  let clickSelector =
    match step.Action with
    | Action.Click target -> dashboardSelector target
    | Action.Type(target, _, _) -> dashboardSelector target
    | Action.TypeThenClick(typeTarget, _, _, _) -> dashboardSelector typeTarget
    | Action.ClickThenTypeThenClick(_, typeTarget, _, _, _) -> dashboardSelector typeTarget
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
    | Action.TypeThenClick(_, _, _, submitTarget) -> dashboardSelector submitTarget
    | Action.ClickThenTypeThenClick(_, _, _, _, submitTarget) -> dashboardSelector submitTarget
    | _ -> None

  let expectSelector =
    match step.Expect with
    | Expectation.PageShows(id, _) -> Some(testIdSelector id)
    // Playwright's own CSS extension: `:has-text("...")` is a substring,
    // whitespace-normalized text match layered onto a plain CSS selector —
    // exactly what "wait until this element's text contains X" needs,
    // without inventing a second selector mini-language of our own.
    | Expectation.PageTextContains(selector, text) -> Some(sprintf "%s:has-text(\"%s\")" selector text)
    | _ -> None

  { Wire.Index = index
    Wire.Caption = Caption.value step.Caption
    Wire.PreClickSelector = preClickSelector
    Wire.ClickSelector = clickSelector
    Wire.TypeText = typeText
    Wire.SubmitSelector = submitSelector
    Wire.ExpectSelector = expectSelector
    Wire.DwellMs = Dwell.ms step.Dwell }

/// The fixed ports every cell uses (§4.1: "the same fixed ports" — legal
/// because each cell has a private network namespace, so nothing collides).
[<Literal>]
let private McpPort = 47749

[<Literal>]
let private DashboardPort = 47750

let private wirePlanOf (repoRoot: string) (scenario: Scenario) : Wire.ScenarioPlan =
  { Wire.ScenarioId = ScenarioId.value scenario.Id
    Wire.ChromePath = "/chrome-bin/chrome"
    Wire.PageUrl = sprintf "http://127.0.0.1:%d/dashboard" DashboardPort
    Wire.UserDataDir = "/home/demo/chrome-profile"
    Wire.OutDir = "/out"
    Wire.Steps = scenario.Steps |> List.mapi (wireStepOf repoRoot) }

// ---------------------------------------------------------------------------
// The cell: exact bwrap shape + inner script (§4.12's proven recipe).
// ---------------------------------------------------------------------------

/// Mirrors the Phase-0 spike's Stage-4 inner script exactly (§4.12): a
/// pre-created `/tmp/.X11-unix` before Xvfb starts (Xvfb refuses to `mkdir`
/// it itself unless euid==0 — satisfied by `--uid 0 --gid 0`, but the
/// directory still has to exist), the daemon on isolated ports/data dir with
/// a health poll before anything is recorded, and the cell-agent as the
/// LAST, foreground command so it inherits the piped `ScenarioPlan` on
/// stdin — the exact control-plane mechanism §4.1 describes. Triple-quoted
/// so every `$`/`\` below is literal bash, not an F# escape.
let private innerScript: string =
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
    RwBinds = [ hostOutDir, "/out" ]
    Env =
      [ "HOME", "/home/demo"
        "PATH", "/usr/bin:/dotnet-root"
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
    InnerCommand = [ "/bin/sh"; "-c"; innerScript ] }

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

    let spec = cellSpec sagefsBin demosBin dotnetRoot chromeDir cellOutDir repoRoot (nugetPackagesDir ())
    let planJson = Wire.serializePlan (wirePlanOf repoRoot scenario)
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
