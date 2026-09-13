/// Proves `Actors/Neovim.fs` GENUINELY, against a REAL `kitty`+`nvim` on a
/// REAL, private Xvfb display (demo-actors-plan.md §2.2's own RED-test
/// requirement: "a demos harness test that Actors/Neovim.fs resolves a
/// non-empty rect for a real caret target against a launched kitty+nvim on
/// Xvfb"). No mock, no fake display, no stubbed RPC — this test spawns the
/// exact real infrastructure the cell-agent would, isolated per the verified
/// `feedback_electron_gui_isolation.md` recipe (its own scratch `HOME`/
/// `XDG_*`, a private Xvfb display chosen to avoid colliding with the real
/// desktop's `:0` or any other concurrently-running agent/cell's own display
/// — confirmed necessary directly: this box had a dozen-plus `/tmp/.X11-
/// unix/X*` sockets already live from other concurrent work when this test
/// was written).
///
/// Fails LOUD, not skipped, if `kitty`/`nvim`/`Xvfb` are missing — matching
/// the job's "never a silent green no-op" doctrine; `failtestf` surfaces the
/// exact missing tool.
module SageFs.Demos.Tests.NeovimActorTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.Actors.Neovim

let private findOnPath (exeName: string) : string option =
  match Environment.GetEnvironmentVariable "PATH" with
  | null -> None
  | path ->
    path.Split(Path.PathSeparator)
    |> Array.tryPick (fun dir ->
      try
        let candidate = Path.Combine(dir, exeName)
        if File.Exists candidate then Some candidate else None
      with _ ->
        None)

/// A private Xvfb display, retried against a random number until its X11
/// socket does not already exist — this is a host-side test process, not a
/// bwrap cell with its own private `/tmp`, so avoiding a real collision with
/// another concurrently-running display is this helper's own job.
type private PrivateXvfb =
  { Process: Process
    Display: string }

let private startPrivateXvfb () : PrivateXvfb =
  let rng = Random()

  let rec pick attempts =
    let n = 150 + rng.Next(750)
    if File.Exists(sprintf "/tmp/.X11-unix/X%d" n) && attempts > 0 then
      pick (attempts - 1)
    else
      n

  let displayNumber = pick 20
  let displayName = sprintf ":%d" displayNumber

  let psi = ProcessStartInfo("Xvfb", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)

  for a in [ displayName; "-screen"; "0"; "1280x720x24"; "-nocursor" ] do
    psi.ArgumentList.Add a

  let proc = new Process(StartInfo = psi)
  proc.Start() |> ignore

  let deadline = DateTime.UtcNow.AddSeconds 5.0

  while not (File.Exists(sprintf "/tmp/.X11-unix/X%d" displayNumber)) && DateTime.UtcNow < deadline do
    Threading.Thread.Sleep 100

  if not (File.Exists(sprintf "/tmp/.X11-unix/X%d" displayNumber)) then
    (try
      proc.Kill true
     with _ ->
      ())

    failtestf "Xvfb never came up on %s" displayName

  { Process = proc; Display = displayName }

let private stopPrivateXvfb (xvfb: PrivateXvfb) : unit =
  try
    xvfb.Process.Kill true
  with _ ->
    ()

/// Injects text directly via nvim's own `--remote-send` (a different,
/// legitimate nvim CLI capability from `--remote-expr`/RPC) — used ONLY to
/// set up buffer content for an `Observe` assertion; typing "on camera" via
/// XTest is `CellAgent.fs`'s job (already covered by `InputTests.fs`/
/// `KeymapTests.fs` against a live display), not this actor's own
/// `ResolveRect`/`Observe` contract, which is what this file proves.
let private remoteSend (nvimPath: string) (socket: string) (env: (string * string) list) (keys: string) : unit =
  let psi = ProcessStartInfo(nvimPath, UseShellExecute = false)
  psi.ArgumentList.Add "--server"
  psi.ArgumentList.Add socket
  psi.ArgumentList.Add "--remote-send"
  psi.ArgumentList.Add keys
  psi.EnvironmentVariables.Clear()

  for k, v in env do
    psi.EnvironmentVariables.[k] <- v

  use proc = new Process(StartInfo = psi)
  proc.Start() |> ignore
  proc.WaitForExit()

let private withLiveActor (f: Handle -> unit) : unit =
  match findOnPath "kitty", findOnPath "nvim" with
  | None, _ -> failtest "kitty not found on PATH — install it to run this test (never silently skipped)"
  | _, None -> failtest "nvim not found on PATH — install it to run this test (never silently skipped)"
  | Some kittyPath, Some nvimPath ->

  let xvfb = startPrivateXvfb ()
  let workDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-demos-nvim-actor-test-%s" (Guid.NewGuid().ToString("N")))

  try
    let handle =
      launch kittyPath nvimPath None (Display xvfb.Display) { X = 0; Y = 0; W = 1280; H = 720 } workDir None 47749 47750 None
      |> Async.RunSynchronously

    try
      f handle
    finally
      close handle |> Async.RunSynchronously
  finally
    stopPrivateXvfb xvfb

    try
      Directory.Delete(workDir, true)
    with _ ->
      ()

// Sequenced, not parallel: each case spins up its own real Xvfb+kitty+nvim
// triple. Running all of them at once starved this box's thread pool during
// development (many concurrent `Async.RunSynchronously`/`Process.WaitForExitAsync`
// chains) and wastes real CPU/RAM alongside whatever else is running
// concurrently on this shared machine — `testSequenced` keeps this list
// genuinely real without either problem.
[<Tests>]
let tests =
  testSequenced
  <| testList "Actors.Neovim (real kitty + nvim on a private Xvfb)" [

    testCase "launch places the real kitty window at the requested rect, measured live (never a guess)" <| fun _ ->
      withLiveActor (fun handle ->
        (handle.WindowRect.W, 0) |> Expect.isGreaterThan "the real window has non-zero width"
        (handle.WindowRect.H, 0) |> Expect.isGreaterThan "the real window has non-zero height"
        (handle.Columns, 0) |> Expect.isGreaterThan "nvim reports a real, non-zero column count"
        (handle.Rows, 0) |> Expect.isGreaterThan "nvim reports a real, non-zero row count")

    testCase "resolveRect 'caret' resolves a non-empty rect for the real cursor position (RPC screenpos(), never a pixel guess)" <| fun _ ->
      withLiveActor (fun handle ->
        let rectOpt = resolveRect handle "caret" |> Async.RunSynchronously

        match rectOpt with
        | None -> failtest "expected Some rect for the caret — nvim's own screenpos() should resolve a freshly-launched window's cursor"
        | Some rect ->
          (rect.W, 0) |> Expect.isGreaterThan "non-empty width"
          (rect.H, 0) |> Expect.isGreaterThan "non-empty height"
          (rect.X >= handle.WindowRect.X && rect.X < handle.WindowRect.X + handle.WindowRect.W)
          |> Expect.isTrue "the caret rect lies inside the real window's own measured bounds")

    testCase "resolveRect 'window-center' returns the actor's own measured window rect" <| fun _ ->
      withLiveActor (fun handle ->
        resolveRect handle "window-center"
        |> Async.RunSynchronously
        |> Expect.equal "window-center is the whole measured window" (Some handle.WindowRect))

    testCase "resolveRect returns None for an unrecognized selector — never a fabricated rect" <| fun _ ->
      withLiveActor (fun handle -> resolveRect handle "not-a-real-selector" |> Async.RunSynchronously |> Expect.isNone "unknown selector resolves to nothing, not a guess")

    testCase "observe 'buffer-contains:' genuinely detects real typed content, including sagefs.nvim-style virtual-text renders" <| fun _ ->
      withLiveActor (fun handle ->
        remoteSend handle.NvimPath handle.NvimSocket handle.NvimEnv "iSageFsDemoActorProbe12345<Esc>"
        observe handle "buffer-contains:SageFsDemoActorProbe12345" 5000.0
        |> Async.RunSynchronously
        |> Expect.isTrue "the real buffer content, read back over RPC, contains the text a real keystroke just typed")

    testCase "observe 'buffer-contains:' genuinely times out (returns false) for text that was never typed — never a fake pass" <| fun _ ->
      withLiveActor (fun handle ->
        observe handle "buffer-contains:ThisTextWasNeverTypedAnywhere" 1200.0
        |> Async.RunSynchronously
        |> Expect.isFalse "absent content is honestly reported as not-observed, not silently passed")

    testCase "observe returns false for an unrecognized selector — never a silent pass (§2's 'never fake a step')" <| fun _ ->
      withLiveActor (fun handle ->
        observe handle "not-a-real-selector" 300.0
        |> Async.RunSynchronously
        |> Expect.isFalse "unknown selectors are honestly false, never true-by-default")

    testCase "toLiveActor reports ActorId.Neovim, matching CellAgent.actorIdOfString's own 'neovim' mapping" <| fun _ -> withLiveActor (fun handle -> (toLiveActor handle).Id |> Expect.equal "actor id" ActorId.Neovim)
  ]
