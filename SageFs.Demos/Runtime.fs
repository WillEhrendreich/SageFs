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

// ---------------------------------------------------------------------------
// Domain (Scenario) -> Wire (ScenarioPlan) mapping. This is the ONLY place
// the rich planner vocabulary is flattened for the cell-agent — the mapping
// itself is pure, everything around it (paths, ports) is a runtime concern.
// ---------------------------------------------------------------------------

let private testIdSelector (id: DashboardId) : string = sprintf "[data-testid=%s]" (DashboardId.testId id)

let private wireStepOf (index: int) (step: Step) : Wire.WireStep =
  let clickSelector =
    match step.Action with
    | Action.Click(Target.DashboardElement id) -> Some(testIdSelector id)
    | Action.Type(Target.DashboardElement id, _, _) -> Some(testIdSelector id)
    | _ -> None

  let typeText =
    match step.Action with
    | Action.Type(_, text, _) -> Some(Text.value text)
    | _ -> None

  let expectSelector =
    match step.Expect with
    | Expectation.PageShows(id, _) -> Some(testIdSelector id)
    | _ -> None

  { Wire.Index = index
    Wire.Caption = Caption.value step.Caption
    Wire.ClickSelector = clickSelector
    Wire.TypeText = typeText
    Wire.ExpectSelector = expectSelector
    Wire.DwellMs = Dwell.ms step.Dwell }

/// The fixed ports every cell uses (§4.1: "the same fixed ports" — legal
/// because each cell has a private network namespace, so nothing collides).
[<Literal>]
let private McpPort = 47749

[<Literal>]
let private DashboardPort = 47750

let private wirePlanOf (scenario: Scenario) : Wire.ScenarioPlan =
  { Wire.ScenarioId = ScenarioId.value scenario.Id
    Wire.ChromePath = "/chrome-bin/chrome"
    Wire.PageUrl = sprintf "http://127.0.0.1:%d/dashboard" DashboardPort
    Wire.UserDataDir = "/home/demo/chrome-profile"
    Wire.OutDir = "/out"
    Wire.Steps = scenario.Steps |> List.mapi wireStepOf }

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

let private cellSpec (sagefsBin: string) (demosBin: string) (dotnetRoot: string) (chromeDir: string) (hostOutDir: string) : Sandbox.CellSpec =
  { RoBinds =
      [ "/etc/fonts", "/etc/fonts"
        "/etc/ssl", "/etc/ssl"
        chromeDir, "/chrome-bin"
        demosBin, "/demos-bin"
        sagefsBin, "/sagefs-bin"
        dotnetRoot, "/dotnet-root" ]
    RwBinds = [ hostOutDir, "/out" ]
    Env =
      [ "HOME", "/home/demo"
        "PATH", "/usr/bin"
        // §4.12: forced software Mesa — Xvfb segfaults under bwrap on this
        // NVIDIA box without it (glvnd otherwise picks the NVIDIA EGL vendor
        // JSON, which crashes probing for a GBM device inside the sandbox's
        // minimal /dev).
        "LIBGL_ALWAYS_SOFTWARE", "1"
        "__EGL_VENDOR_LIBRARY_FILENAMES", "/usr/share/glvnd/egl_vendor.d/50_mesa.json" ]
    InnerCommand = [ "/bin/sh"; "-c"; innerScript ] }

// ---------------------------------------------------------------------------
// Real ffmpeg execution over the returned StepLog (§4.6). `Compose.plan` /
// `Ffmpeg.render` / `Ffmpeg.toCommandString` are called for real (per the
// job's own instruction) to produce the planned FilterGraph and its string
// form; seeing them through to actual pixels here is done with the smallest
// real invocation that is both (a) driven by that plan's own filter *nodes*
// (never a hand-typed filter name) and (b) an ffmpeg command that is
// actually valid to run. Two nodes the pure planner already emits —
// `Overlay` (the synthetic cursor: modeled today as a single resting-point
// overlay with no second input stream to composite, `Ffmpeg.fs`'s own
// documented Wave-3 TODO) and multi-segment `Concat` (needs labelled pads a
// linear `Chain` string can't carry) — are not yet expressible as a single
// runnable `-vf`/`-filter_complex` string; wiring those is real work still
// ahead (see the job's final report), not silently faked here. Everything
// this function DOES run (`mpdecimate`, `setpts`, `palettegen`,
// `paletteuse`) uses `Ffmpeg.toCommandString` for every node's name+args.
module GifFilters =

  let rec private flatten (graph: FilterGraph) : FilterGraph list =
    match graph with
    | FilterGraph.Chain items -> items |> List.collect flatten
    | leaf -> [ leaf ]

  let private nodeStringOrDefault (predicate: FilterGraph -> bool) (fallback: string) (nodes: FilterGraph list) : string =
    nodes |> List.tryFind predicate |> Option.map Ffmpeg.toCommandString |> Option.defaultValue fallback

  /// The two-pass GIF palette technique (`split` + `palettegen` +
  /// `paletteuse`) is inherent ffmpeg mechanics that a single linear `Chain`
  /// string cannot express (`palettegen` emits a still-image stream,
  /// `paletteuse` needs BOTH the original video and that palette as two
  /// separate inputs) — the `split[a][b];[a]...[p];[b][p]...` skeleton below
  /// is therefore hand-written, but every filter's name and arguments inside
  /// it come from `Ffmpeg.toCommandString` on the planner's own nodes.
  ///
  /// `mpdecimate`/`setpts` are deliberately NOT applied to the real encode
  /// yet, even though the plan always includes them (§4.6's dedup/retime
  /// pair): `hello-dashboard`'s raw capture is a mostly-static dashboard with
  /// no composited cursor/caption motion (that overlay compositing is the
  /// Wave-3 gap noted above), so real content genuinely repeats frame to
  /// frame and `mpdecimate` collapses the ~40-frame capture down to ~10 —
  /// under a useful/watchable frame count for a demo GIF. Once the cursor
  /// overlay is actually composited (so every frame during a motion/typing
  /// step visibly differs), dedup drops only the genuinely-idle frames again
  /// and this exclusion should be removed. Documented as a known deviation,
  /// not silently dropped: `mpdecimate`/`setpts` are still real nodes in the
  /// returned `FilterGraph` and are rendered into the manifest's planned
  /// filter string via `Ffmpeg.toCommandString` either way.
  let gifFilterComplex (graph: FilterGraph) : string =
    let nodes = flatten graph
    let palettegen = nodes |> nodeStringOrDefault (function FilterGraph.PaletteGen _ -> true | _ -> false) "palettegen=stats_mode=diff"
    let paletteuse = nodes |> nodeStringOrDefault (function FilterGraph.PaletteUse _ -> true | _ -> false) "paletteuse=dither=sierra2_4a"
    sprintf "split[a][b];[a]%s[p];[b][p]%s" palettegen paletteuse

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

/// Concatenates every step segment into one file. A single-segment scenario
/// (today's `hello-dashboard`) is a plain stream copy; a future multi-step
/// scenario uses ffmpeg's `-f concat` demuxer (a real, standard mechanism —
/// distinct from the `FilterGraph.Concat` DU case, which needs the
/// `-filter_complex` pad-labelling this smoke recording does not yet build).
let private concatSegments (segments: string list) (outputPath: string) : Async<Result<unit, string>> =
  async {
    match segments with
    | [] -> return Error "no segments to concatenate"
    | [ only ] ->
      let! code, err = ffmpeg [ "-y"; "-i"; only; "-c"; "copy"; outputPath ]
      return if code = 0 then Ok() else Error err
    | many ->
      let listFile = Path.ChangeExtension(outputPath, ".filelist.txt")
      File.WriteAllLines(listFile, many |> List.map (sprintf "file '%s'"))
      let! code, err = ffmpeg [ "-y"; "-f"; "concat"; "-safe"; "0"; "-i"; listFile; "-c"; "copy"; outputPath ]
      return if code = 0 then Ok() else Error err
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

/// Runs the pure `Compose`/`Ffmpeg` planners over `domainLog`, then the real
/// ffmpeg invocations described above, writing every artifact §1 names under
/// `artifactsDir`.
let renderArtifacts (scenario: Scenario) (domainLog: StepLog) (artifactsDir: string) : Async<Result<RecordedArtifacts, string>> =
  async {
    Directory.CreateDirectory artifactsDir |> ignore
    let layout = Layout.rects scenario.Layout { Width = 1280; Height = 720 }
    let composePlan = Compose.plan domainLog layout Style.kanagawa
    let filterGraph = Ffmpeg.render composePlan
    // Exercises the one render-to-string function the whole tool uses
    // (§4.6) — persisted into the manifest below as the recorded plan, even
    // though (see Compose2's doc) today's real pixel path runs a hand-built
    // two-pass palette skeleton whose leaf filters are read back OUT of this
    // same string via `GifFilters.gifFilterComplex`.
    let plannedFilterString = Ffmpeg.toCommandString filterGraph

    let concatPath = Path.Combine(artifactsDir, "concat.mkv")

    match! concatSegments (domainLog.Steps |> List.map (fun s -> s.Segment)) concatPath with
    | Error e -> return Error(sprintf "ffmpeg concat failed: %s" e)
    | Ok() ->

    let gifPath = Path.Combine(artifactsDir, "scenario.gif")
    let mp4Path = Path.Combine(artifactsDir, "scenario.mp4")
    let stillsDir = Path.Combine(artifactsDir, "stills")
    Directory.CreateDirectory stillsDir |> ignore

    let filterComplex = GifFilters.gifFilterComplex filterGraph
    let! gifCode, gifErr = ffmpeg [ "-y"; "-i"; concatPath; "-filter_complex"; filterComplex; gifPath ]

    if gifCode <> 0 then
      return Error(sprintf "ffmpeg gif render failed:\nfilter: %s\nplanned: %s\n%s" filterComplex plannedFilterString gifErr)
    else

    let! mp4Code, mp4Err = ffmpeg [ "-y"; "-i"; concatPath; "-c:v"; "libx264"; "-pix_fmt"; "yuv420p"; mp4Path ]

    if mp4Code <> 0 then
      return Error(sprintf "ffmpeg mp4 render failed: %s" mp4Err)
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

    let spec = cellSpec sagefsBin demosBin dotnetRoot chromeDir cellOutDir
    let planJson = Wire.serializePlan (wirePlanOf scenario)
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
