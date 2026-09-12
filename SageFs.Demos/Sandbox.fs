/// The bwrap sandbox edge (demo-gif-plan.md §4.1, §4.12). Building the
/// argument list is pure data — the exact recipe the Phase-0 spike proved
/// necessary on this machine (§4.12: "codify these exact flags, they are not
/// optional extras") — so it is unit-testable without ever spawning bwrap;
/// only `run` below actually launches a cell.
module SageFs.Demos.Sandbox

open System.Diagnostics

/// Everything one cell needs bound into it (§4.1's fixed filesystem layout).
/// `RoBinds`/`RwBinds` are (hostPath, cellPath) pairs; exactly one `RwBinds`
/// entry is expected in practice — the `/out` mount that is the ONLY way the
/// bulky recorded segments get out of a sealed cell (§4.1's data plane).
type CellSpec =
  { RoBinds: (string * string) list
    RwBinds: (string * string) list
    Env: (string * string) list
    InnerCommand: string list }

/// The exact bwrap flags the Phase-0 spike proved (§4.12): private
/// user/net/pid/ipc namespaces (so every cell can reuse the same fixed ports
/// and the same `:99` display without colliding, §4.1); `--uid 0 --gid 0`
/// (Xvfb's `/tmp/.X11-unix` ownership check fails without it — a genuine
/// crash the spike root-caused, not a style preference); tmpfs `/tmp` and
/// `/home/demo` (teardown is process exit, zero garbage); the `usr`-only bind
/// with `/bin,/sbin,/lib,/lib64` as symlinks into it (the spike's minimal
/// read-only toolchain shape); and `--die-with-parent` so a killed runner can
/// never leave a cell behind.
let args (spec: CellSpec) : string list =
  [ "--unshare-user"
    "--unshare-net"
    "--unshare-pid"
    "--unshare-ipc"
    "--uid"
    "0"
    "--gid"
    "0"
    "--tmpfs"
    "/tmp"
    "--tmpfs"
    "/home/demo"
    "--ro-bind"
    "/usr"
    "/usr"
    "--symlink"
    "usr/bin"
    "/bin"
    "--symlink"
    "usr/bin"
    "/sbin"
    "--symlink"
    "usr/lib"
    "/lib"
    "--symlink"
    "usr/lib"
    "/lib64" ]
  @ (spec.RoBinds |> List.collect (fun (host, cell) -> [ "--ro-bind"; host; cell ]))
  @ (spec.RwBinds |> List.collect (fun (host, cell) -> [ "--bind"; host; cell ]))
  @ [ "--proc"; "/proc"; "--dev"; "/dev"; "--die-with-parent"; "--clearenv" ]
  @ (spec.Env |> List.collect (fun (k, v) -> [ "--setenv"; k; v ]))
  @ [ "--" ]
  @ spec.InnerCommand

/// Spawns `bwrap` for `spec`, writing `stdinPayload` to its stdin (§4.1's one
/// control-plane pipe — the ONLY thing that reaches inside a sealed cell) and
/// returning its exit code plus everything it wrote to stdout/stderr. This
/// function itself never signals the process: the cell's own inner script
/// owns graceful `SIGTERM`→wait→`SIGKILL` teardown of everything it spawned
/// (§4.11), and `--die-with-parent` plus the private pid namespace guarantee
/// that if the .NET `Process` object here is ever killed from outside, the
/// whole cell's process tree is reaped with it.
let run (spec: CellSpec) (stdinPayload: string) : Async<int * string * string> =
  async {
    let psi =
      ProcessStartInfo(
        "bwrap",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      )

    for a in args spec do
      psi.ArgumentList.Add a

    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let stdoutTask = proc.StandardOutput.ReadToEndAsync()
    let stderrTask = proc.StandardError.ReadToEndAsync()
    do! proc.StandardInput.WriteLineAsync(stdinPayload: string) |> Async.AwaitTask
    proc.StandardInput.Close()
    do! proc.WaitForExitAsync() |> Async.AwaitTask
    let! stdout = stdoutTask |> Async.AwaitTask
    let! stderr = stderrTask |> Async.AwaitTask
    return proc.ExitCode, stdout, stderr
  }
