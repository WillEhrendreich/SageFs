module SageFs.Server.ClientMode

open System
open SageFs.Server
open SageFs

/// Display daemon connection info.
let private showConnectionBanner (info: DaemonInfo) =
  let elapsed = DateTime.UtcNow - info.StartedAt
  let agoText =
    if elapsed.TotalMinutes < 1.0 then "just now"
    elif elapsed.TotalHours < 1.0 then sprintf "%dm ago" (int elapsed.TotalMinutes)
    elif elapsed.TotalDays < 1.0 then sprintf "%dh ago" (int elapsed.TotalHours)
    else sprintf "%dd ago" (int elapsed.TotalDays)
  printfn "Connected to SageFs daemon (PID %d, port %d)" info.Pid info.Port
  printfn "  %s  •  Started %s  •  v%s" info.WorkingDirectory agoText info.Version
  printfn ""

/// Start daemon in background, wait for it to be ready.
let startDaemonInBackground (daemonArgs: string) =
  let psi = System.Diagnostics.ProcessStartInfo()
  psi.FileName <- "SageFs"
  psi.Arguments <- sprintf "-d %s" daemonArgs
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true

  let proc = System.Diagnostics.Process.Start(psi)
  if isNull proc then
    Error (SageFsError.DaemonStartFailed "Failed to start daemon")
  else
    let mutable attempts = 0
    let mutable found = false
    while attempts < 30 && not found do
      System.Threading.Thread.Sleep(500)
      match DaemonState.read () with
      | Some _ -> found <- true
      | None -> attempts <- attempts + 1
    if found then Ok ()
    else Error (SageFsError.DaemonStartFailed "Daemon started but did not become ready in 15s")

/// Check if a daemon is running and show its info.
let tryConnect () =
  match DaemonState.read () with
  | Some info ->
    showConnectionBanner info
    Some info
  | None -> None
