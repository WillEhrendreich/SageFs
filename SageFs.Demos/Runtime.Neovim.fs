/// The Neovim actor's runtime extension (demo-actors-plan.md §1.2/§2.2):
/// resolves the real `kitty`/`nvim` binaries and a CLEAN, pinned sagefs.nvim
/// checkout, and shapes the cell binds/`Wire.NvimConfig` `Actors/Neovim.fs`
/// needs — through `Runtime.fs`'s own `actorBinds`/`actorPrologue`/
/// `Wire.NvimConfig` extension points, never by editing `Runtime.fs`'s core
/// (never-touch).
///
/// SEAM GAP (reported, not fixed here — see this island's final report):
/// `Runtime.fs`'s `record` function calls `cellSpec ... [] []` and
/// `wirePlanOf` hard-codes `Wire.Nvim = None` — there is today no call site
/// that actually threads THIS module's `actorBinds`/`nvimConfig` into a real
/// `record` run for a Neovim-client scenario. That wiring is a small,
/// additive change to `Runtime.fs` itself (on this island's never-touch
/// list, and identical in shape for every other actor island), so it is
/// flagged for main-thread sign-off rather than made here. Every function
/// below is genuine and independently exercised by this island's own tests;
/// none of it is a no-op stub.
module SageFs.Demos.Runtime.Neovim

open System
open System.Diagnostics
open System.IO
// This module's own name (`SageFs.Demos.Runtime.Neovim`) sits one level
// deeper than `SageFs.Demos` — exactly the `Runtime.Core.fs` situation its
// own doc comment explains (FS0247: `SageFs.Demos.Runtime` can't be both a
// namespace and a module) — so `Wire.NvimConfig` below needs this explicit
// open to resolve by its short name.
open SageFs.Demos

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

/// Fail-loud resolution (§2's "never a silent green no-op" doctrine): a
/// missing `kitty`/`nvim` is an actionable `Error`, never a quietly-skipped
/// scenario.
let resolveKitty () : Result<string, string> =
  match findOnPath "kitty" with
  | Some path -> Ok path
  | None -> Error "kitty not found on PATH — install it (e.g. `pacman -S kitty` / `apt install kitty`) to record a Neovim scenario"

let resolveNvim () : Result<string, string> =
  match findOnPath "nvim" with
  | Some path -> Ok path
  | None -> Error "nvim not found on PATH — install it (e.g. `pacman -S neovim` / `apt install neovim`) to record a Neovim scenario"

/// Resolves a CLEAN, pinned sagefs.nvim commit into a scratch directory via
/// `git archive | tar -x` — NEVER `git checkout`/`git switch` against the
/// live repo, which currently has the unpushed `feat/coverage-review-view`
/// branch checked out (demo-actors-plan.md §2.2/§7.5's caution). `git
/// archive <ref>` reads the ref's tree straight out of the object database
/// and writes a tar stream; it touches neither the working tree nor the
/// index, so this is safe to run while that branch stays checked out, and
/// pins to `gitRef` (default `"master"`, verified identical to
/// `origin/master` when this was written — commit `7fc30c2`) rather than
/// whatever happens to be on disk. Piped process-to-process (no shell, no
/// quoting concerns) — `System.Diagnostics.Process.StandardOutput.BaseStream
/// .CopyToAsync(tarProc.StandardInput.BaseStream)` is the real pipe.
let resolvePinnedPlugin (pluginRepoDir: string) (scratchDir: string) (gitRef: string) : Async<Result<string * string, string>> =
  async {
    let! shaCode, shaOut, shaErr = runCaptured "git" [ "-C"; pluginRepoDir; "rev-parse"; gitRef ] None

    if shaCode <> 0 then
      return Error(sprintf "could not resolve sagefs.nvim ref '%s' in %s: %s" gitRef pluginRepoDir (shaErr.Trim()))
    else

    let sha = shaOut.Trim()
    Directory.CreateDirectory scratchDir |> ignore

    let archivePsi = ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)

    for a in [ "-C"; pluginRepoDir; "archive"; sha ] do
      archivePsi.ArgumentList.Add a

    use archiveProc = new Process(StartInfo = archivePsi)

    let tarPsi = ProcessStartInfo("tar", RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false)

    for a in [ "-x"; "-C"; scratchDir ] do
      tarPsi.ArgumentList.Add a

    use tarProc = new Process(StartInfo = tarPsi)

    archiveProc.Start() |> ignore
    tarProc.Start() |> ignore

    let copyTask = archiveProc.StandardOutput.BaseStream.CopyToAsync(tarProc.StandardInput.BaseStream)
    let archiveErrTask = archiveProc.StandardError.ReadToEndAsync()
    do! copyTask |> Async.AwaitTask
    tarProc.StandardInput.Close()
    let! tarErr = tarProc.StandardError.ReadToEndAsync() |> Async.AwaitTask
    do! archiveProc.WaitForExitAsync() |> Async.AwaitTask
    do! tarProc.WaitForExitAsync() |> Async.AwaitTask
    let! archiveErr = archiveErrTask |> Async.AwaitTask

    if archiveProc.ExitCode <> 0 then
      return Error(sprintf "git archive %s failed: %s" sha (archiveErr.Trim()))
    elif tarProc.ExitCode <> 0 then
      return Error(sprintf "tar extract into %s failed: %s" scratchDir (tarErr.Trim()))
    else
      return Ok(sha, scratchDir)
  }

/// The extra cell binds this actor needs (Island F's `actorBinds` extension
/// point, `Runtime.fs`'s `cellSpec`). `kitty`/`nvim` need NO extra bind: both
/// live under `/usr` on this machine, and `Sandbox.args` already RO-binds
/// `/usr` into every cell unconditionally (`SandboxTests.fs`'s own "the fixed
/// /usr bind" case) — verified directly (`which kitty nvim` both resolve
/// under `/usr/bin`). The ONLY thing genuinely missing from a bare cell is
/// the pinned plugin checkout, which lives under a scratch dir outside
/// `/usr`; bound at the SAME absolute path on both sides (the pattern
/// `Runtime.fs`'s own fixed binds already use for `repoRoot`/
/// `nugetPackagesDir`) so nothing needs path-rewriting.
let actorBinds (pluginScratchDir: string) : (string * string) list = [ pluginScratchDir, pluginScratchDir ]

/// This island's `actorPrologue` contribution is deliberately empty: exactly
/// like the Dashboard actor (Island F's own reference — `Actors/Dashboard.fs`
/// launches Chromium from INSIDE the cell-agent process, not from a shell
/// line spliced before it), `Actors.Neovim.launch` spawns kitty+nvim itself
/// once the cell-agent's actor-assembly step dispatches to it — there is
/// nothing this actor needs prepared at the shell level before the daemon
/// health-check/cell-agent handoff `Runtime.fs`'s `innerScript` already does.
let actorPrologue: string list = []

/// Fills `Wire.NvimConfig` (Island F's placeholder record) with the
/// resolved, pinned commit SHA (`resolvePinnedPlugin`'s first result) —
/// roast I12: a resolved commit, never a floating ref.
let nvimConfig (pluginCommitSha: string) : Wire.NvimConfig = { PluginCommit = Some pluginCommitSha }
