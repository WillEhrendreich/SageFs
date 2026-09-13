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
///
/// The whole bwrap lifecycle runs on ONE DEDICATED, long-lived `Thread` —
/// not `async`/`Task` continuations, which the .NET ThreadPool is free to
/// resume on a DIFFERENT worker thread each time, and are themselves free to
/// exit and be recycled once idle. `--die-with-parent` is implemented via
/// Linux's `PR_SET_PDEATHSIG`, whose "parent" is the SPECIFIC THREAD that
/// forked the child, not the parent process as a whole (a well-documented
/// Linux `prctl` gotcha) — so if the thread-pool thread that happened to
/// call `proc.Start()` gets recycled while bwrap is still running (routine
/// under heavy ThreadPool churn, and this tool's own daemon-build step
/// upstream already forces at least one prior thread hop via `Async.AwaitTask`),
/// bwrap receives an unearned SIGKILL mid-recording — with no coredump (it's
/// a real SIGKILL, not a crash) and no OOM log anywhere (confirmed absent
/// from `coredumpctl`/`journalctl` while diagnosing this exact failure: a
/// long recording — waiting for real session warmup, not just a quick UI
/// click — died this way on every single attempt on a busy box, while an
/// otherwise-identical SHORT recording never did). Blocking THIS thread on
/// `proc.WaitForExit()` for bwrap's entire lifetime is what keeps the
/// PDEATHSIG relationship intact regardless of ThreadPool pressure or how
/// long the recording runs.
let run (spec: CellSpec) (stdinPayload: string) : Async<int * string * string> =
  async {
    let tcs = System.Threading.Tasks.TaskCompletionSource<int * string * string>()

    let work () =
      try
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
        // Drain stdout/stderr on their own tasks so neither pipe's buffer
        // can fill and deadlock the child while this thread blocks below.
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        proc.StandardInput.WriteLine(stdinPayload: string)
        proc.StandardInput.Close()
        proc.WaitForExit() // blocks THIS thread — see module doc above
        let stdout = stdoutTask.GetAwaiter().GetResult()
        let stderr = stderrTask.GetAwaiter().GetResult()
        tcs.SetResult(proc.ExitCode, stdout, stderr)
      with ex ->
        tcs.SetException ex

    let thread = System.Threading.Thread(System.Threading.ThreadStart(work), IsBackground = true, Name = "sagefs-demos-bwrap")
    thread.Start()
    return! tcs.Task |> Async.AwaitTask
  }
