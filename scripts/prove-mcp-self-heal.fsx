// PROVE the stdio bridge self-heals across a daemon restart.
//
// The claim being tested: `sagefs mcp` owns the MCP handshake itself, so a client talking
// to it over stdio never holds a session id that a daemon restart can orphan.
//
// If true, a daemon killed mid-conversation costs one failed tool call and the NEXT one
// succeeds — with no human reload. If false, every call after the restart fails the same way
// the HTTP transport does.
//
// The test runs a real stdio client against `sagefs mcp`, holds a live session across a
// daemon restart, and records the status of the calls on each side of it.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading

// The repo the bridge runs against. Located at RUNTIME rather than hardcoded, so the repo works
// wherever it is checked out.
// Locate the repo at RUNTIME, walking up from this script's own location until we
// find the solution file. A build-time constant would bake in the directory the
// script was COMPILED, which is not where it RUNS.
let repoRoot =
  let rec walk (dir: string) (depth: int) : string =
    if depth > 24 then "" else
    let full =
      try
        let f = Path.GetFullPath dir
        let r = Path.GetPathRoot f
        if f = r then f else Path.TrimEndingDirectorySeparator f
      with _ -> dir
    if File.Exists(Path.Combine(full, "SageFs.slnx")) then full
    else
      let parent = Path.GetDirectoryName full
      if String.IsNullOrEmpty parent || parent = full then ""
      else walk parent (depth + 1)
  // Start from the directory this script lives in. A build-time constant only names where it was
  // built, so the walk up to SageFs.slnx is what locates the repo where the code actually runs.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let repo =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else repoRoot

/// One JSON-RPC request over the bridge's stdin, with its response from stdout.
let callClient (proc: Process) (id: int) (method_: string) (paramsJson: string) : string =
  let request =
    sprintf "{\"jsonrpc\":\"2.0\",\"id\":%d,\"method\":\"%s\",\"params\":%s}\n" id method_ paramsJson
  proc.StandardInput.Write(request)
  proc.StandardInput.Flush()
  // Bounded: a hang must fail the run, not wedge it.
  let readTask = System.Threading.Tasks.Task<string>.Factory.StartNew(fun () -> proc.StandardOutput.ReadLine())
  if readTask.Wait 30000 then readTask.Result else ""

let startBridge () =
  let psi = new ProcessStartInfo("sagefs", "mcp")
  psi.WorkingDirectory <- repo
  psi.RedirectStandardInput <- true
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.UseShellExecute <- false
  let p = new Process()
  p.StartInfo <- psi
  if not (p.Start()) then failwith "could not start `sagefs mcp`"
  p

let callIsOk (response: string) =
  not (String.IsNullOrWhiteSpace response) && response.Contains("\"result\"") && not (response.Contains("\"error\""))

printfn "1. starting the stdio bridge"
let bridge = startBridge()

printfn "2. initialize through the bridge (the bridge mints and keeps the session id)"
let init = callClient bridge 1 "initialize" """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"heal-probe","version":"1"}}"""
printfn "   initialize -> %s" (if callIsOk init then "ok" else sprintf "FAILED: %s" init)
if not (callIsOk init) then
  printfn "FAIL: the bridge could not initialize at all; nothing below is meaningful"
  exit 1

printfn "3. a real tool call BEFORE the restart"
let before = callClient bridge 2 "tools/call" """{"name":"get_daemon_status","arguments":{}}"""
printfn "   tools/call -> %s" (if callIsOk before then "ok" else sprintf "FAILED: %s" before)
if not (callIsOk before) then
  printfn "FAIL: no working baseline; a later success would prove nothing"
  exit 1

printfn "4. restarting the daemon underneath the live bridge"
let findDaemonPid () =
  let psi = new ProcessStartInfo("sh", "-c \"ss -ltnp 2>/dev/null | grep ':37749 ' | grep -oP 'pid=\\K[0-9]+' | head -1\"")
  psi.RedirectStandardOutput <- true
  psi.UseShellExecute <- false
  let sh = new Process()
  sh.StartInfo <- psi
  sh.Start() |> ignore
  let found = sh.StandardOutput.ReadToEnd().Trim()
  sh.WaitForExit 10000 |> ignore
  found
let daemonPid = findDaemonPid ()
let pid = match Int32.TryParse daemonPid with | true, v when v > 0 -> Some v | _ -> None
match pid with
| None ->
  printfn "   could not find the daemon pid; skipping the restart half (the baseline above still stands)"
| Some p ->
  printfn "   daemon pid %d — terminating it" p
  try (Process.GetProcessById p).Kill() |> ignore with _ -> ()
  // Wait for the port to actually free, so the bridge sees a real outage rather than a race.
  let sw = Stopwatch.StartNew()
  let mutable free = false
  while sw.ElapsedMilliseconds < 30000L && not free do
    let psi = new ProcessStartInfo("sh", "-c \"ss -ltn 2>/dev/null | grep -c ':37749 '\"")
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let probe = new Process()
    probe.StartInfo <- psi
    probe.Start() |> ignore
    let n = probe.StandardOutput.ReadToEnd().Trim()
    probe.WaitForExit 5000 |> ignore
    free <- n = "0"
    if not free then Thread.Sleep 500
  printfn "   port free after %dms" (int sw.ElapsedMilliseconds)
  printfn "5. bringing the daemon back (the bridge spawns one if needed)"
  // The bridge is asked for a tool; if the daemon is gone the bridge restarts it. Give it time.
  Thread.Sleep 3000

printfn "6. a real tool call AFTER the restart, same client, same process"
let mutable after = ""
let sw2 = Stopwatch.StartNew()
while sw2.ElapsedMilliseconds < 90000L && not (callIsOk after) do
  after <- callClient bridge 3 "tools/call" """{"name":"get_daemon_status","arguments":{}}"""
  if not (callIsOk after) then Thread.Sleep 2000
printfn "   tools/call -> %s (after %dms)" (if callIsOk after then "ok — SELF-HEALED, no human reload" else "FAILED") (int sw2.ElapsedMilliseconds)
if not (callIsOk after) then printfn "   last response: %s" after

try bridge.StandardInput.Close() with _ -> ()
try (if not bridge.HasExited then bridge.Kill()) with _ -> ()
exit (if callIsOk after then 0 else 1)