/// Per-step ffmpeg x11grab capture (demo-gif-plan.md §4.5, §4.11): recording
/// runs only while a step's input plan executes or its `Expectation` is being
/// awaited — `start`/`stop` bracket exactly one step, never a cell's whole
/// lifetime, per §4.5's "no idle frames ever captured". Teardown is always
/// graceful (`SIGTERM`, a short grace period, `SIGKILL` only as a last
/// resort, §4.11) — a cleanly signalled ffmpeg finalizes its container and
/// exits 0, which is exactly why neither signal ever produces a coredump.
module SageFs.Demos.Recorder

open System
open System.Diagnostics
open System.Runtime.InteropServices

module private Native =
  [<DllImport("libc", SetLastError = true)>]
  extern int kill(int pid, int sig_)

[<Literal>]
let private SIGTERM = 15

[<Literal>]
let private SIGKILL = 9

[<Literal>]
let private GracePeriodMs = 3000

type Handle = { Process: Process; OutputPath: string }

/// Starts one `ffmpeg -f x11grab` capture of `display` to `outputPath` (§4.5:
/// 15fps, 1280x720 — the output resolution, no downscale pass —
/// `libx264 -preset ultrafast` for a cheap-to-produce per-step segment).
let start (display: string) (outputPath: string) : Handle =
  let psi = ProcessStartInfo("ffmpeg", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)

  for a in
    [ "-y"
      "-f"
      "x11grab"
      "-framerate"
      "15"
      "-video_size"
      "1280x720"
      "-i"
      display
      "-c:v"
      "libx264"
      "-preset"
      "ultrafast"
      "-qp"
      "0"
      outputPath ] do
    psi.ArgumentList.Add a

  let proc = Process.Start psi
  { Process = proc; OutputPath = outputPath }

/// Stops one step's segment: `SIGTERM` first (ffmpeg's clean-shutdown signal
/// — it finalizes the container and exits 0), a bounded poll for exit, then
/// `SIGKILL` only if it is still alive after the grace period (§4.11).
let stop (handle: Handle) : Async<unit> =
  async {
    if not handle.Process.HasExited then
      Native.kill (handle.Process.Id, SIGTERM) |> ignore
      let mutable waitedMs = 0

      while not handle.Process.HasExited && waitedMs < GracePeriodMs do
        do! Async.Sleep 100
        waitedMs <- waitedMs + 100

      if not handle.Process.HasExited then
        Native.kill (handle.Process.Id, SIGKILL) |> ignore
        let mutable killedWaitMs = 0

        while not handle.Process.HasExited && killedWaitMs < 1000 do
          do! Async.Sleep 50
          killedWaitMs <- killedWaitMs + 50
  }
