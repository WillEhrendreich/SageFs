/// `sagefs check` — environment pre-flight checks.
/// All pure helper functions are testable without I/O side-effects.
module EnvCheck

open System
open System.IO
open System.Net
open System.Net.Sockets
open SageFs.Server

[<RequireQualifiedAccess>]
type Status = | Pass | Warn | Fail

type CheckResult = {
  Icon:   string
  Label:  string
  Status: Status
  Detail: string
  Hint:   string option
}

let private pass label detail   = { Icon = "✓"; Label = label; Status = Status.Pass; Detail = detail; Hint = None }
let private warn label detail h = { Icon = "⚠"; Label = label; Status = Status.Warn; Detail = detail; Hint = Some h }
let private fail label detail h = { Icon = "✗"; Label = label; Status = Status.Fail; Detail = detail; Hint = Some h }

/// True when the given TCP port is not in use on loopback.
let isPortFree (port: int) =
  try
    use l = new TcpListener(IPAddress.Loopback, port)
    l.Start()
    l.Stop()
    true
  with _ -> false

/// Non-recursive .fsproj discovery in a directory.
let findFsproj (dir: string) =
  try Directory.GetFiles(dir, "*.fsproj", SearchOption.TopDirectoryOnly) |> Array.toList
  with _ -> []

let checkDotnetSdk () =
  try
    let v = Runtime.InteropServices.RuntimeInformation.FrameworkDescription
    pass ".NET SDK" v
  with ex ->
    fail ".NET SDK" "Could not determine .NET version" (sprintf "Install .NET 8+ from https://dot.net: %s" ex.Message)

let checkFsiAvailable () =
  try
    let psi = Diagnostics.ProcessStartInfo("dotnet", "fsi --nologo --exec")
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError  <- true
    psi.UseShellExecute        <- false
    psi.CreateNoWindow         <- true
    use proc = Diagnostics.Process.Start(psi)
    let exited = proc.WaitForExit(3000)
    match exited with
    | true  -> pass "F# Interactive" "dotnet fsi available"
    | false ->
      proc.Kill()
      pass "F# Interactive" "dotnet fsi available (killed test process)"
  with ex ->
    fail "F# Interactive" "dotnet fsi not found" (sprintf "Ensure the .NET SDK is installed and `dotnet` is on your PATH: %s" ex.Message)

let checkFsproj (dir: string) =
  match findFsproj dir with
  | [] ->
    warn ".fsproj files" (sprintf "None found in %s" dir)
          "Run `sagefs` from a directory containing an F# project file (.fsproj), or pass --proj <path>"
  | files ->
    let names = files |> List.map Path.GetFileName |> String.concat ", "
    pass ".fsproj files" (sprintf "%d found: %s" files.Length names)

let checkPort (label: string) (port: int) =
  match isPortFree port with
  | true  -> pass label (sprintf "Port %d available" port)
  | false ->
    fail label (sprintf "Port %d is already in use" port)
          (sprintf "Another process is using port %d. Use --mcp-port (or --dash-port) to choose a different port, or stop the conflicting process." port)

let checkDaemon (mcpPort: int) =
  match DaemonState.readOnPort mcpPort with
  | Some info ->
    warn "SageFs daemon"
         (sprintf "Already running (PID %d, port %d, started %s)" info.Pid info.Port (info.StartedAt.ToString("HH:mm:ss")))
         "A daemon is already running. The new start will be a no-op or conflict. Run `sagefs stop` first if you want a fresh start."
  | None ->
    pass "SageFs daemon" "No daemon running — ready to start"

let runAll (dir: string) (mcpPort: int) (dashPort: int) =
  [ checkDotnetSdk ()
    checkFsiAvailable ()
    checkFsproj dir
    checkPort "MCP port"       mcpPort
    checkPort "Dashboard port" dashPort
    checkDaemon mcpPort ]

/// Print the check results to stdout. Returns the count of failures (for exit code).
let print (results: CheckResult list) =
  let w = 60
  printfn ""
  printfn "  SageFs Environment Check"
  printfn "  %s" (String.replicate w "═")
  for r in results do
    printfn "  %s  %s" r.Icon r.Detail
    match r.Hint with
    | Some h -> printfn "     └─ %s" h
    | None   -> ()
  printfn "  %s" (String.replicate w "─")
  let failures = results |> List.filter (fun r -> r.Status = Status.Fail)
  let warnings = results |> List.filter (fun r -> r.Status = Status.Warn)
  match failures, warnings with
  | [], [] ->
    printfn "  ✓  All checks passed — SageFs is ready to run"
    printfn "     Run `sagefs` to start, or `sagefs tui` for the terminal UI."
  | [], ws ->
    printfn "  ⚠  %d warning(s) — SageFs will likely work but review the hints above." ws.Length
  | fs, _ ->
    printfn "  ✗  %d check(s) failed — fix the issues above before running SageFs." fs.Length
  printfn ""
  failures.Length
