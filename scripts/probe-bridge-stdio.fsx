/// Drive the REAL `sagefs mcp` stdio bridge the way an agent does.
///
/// WHY THIS SCRIPT IS SHAPED THIS WAY — two traps, each of which cost real time:
///
///  * stderr MUST be drained on its own thread. The bridge writes diagnostics there, and an
///    unread pipe fills and BLOCKS the bridge. That presents as "my tool call never answered",
///    i.e. as a broken product rather than as a full pipe.
///
///  * Responses are framed (SSE), so reading a FIXED number of lines deadlocks. Read until
///    the response id you asked for appears, against a time budget.
///
/// Run it:
///   dotnet fsi scripts/probe-bridge-stdio.fsx
///
/// It calls `get_daemon_status` through the bridge. To test whether a tool CAN be
/// reached from another repository, change `toolName`/`toolArgs` — `join_cohort` takes an
/// explicit `working_directory`, which is the whole answer to "cohorts are per-repository,
/// so can I work in another repo from this connection?".

open System
open System.Diagnostics
open System.IO
open System.Text

let toolName = "join_cohort"
let toolArgs =
  """{"agentName":"scope-probe","role":"Implementer","working_directory":"/home/will/Work/nehemiah"}"""
let wantedId = "\"id\":2"

let psi = new ProcessStartInfo("sagefs")
psi.ArgumentList.Add("mcp") |> ignore
psi.RedirectStandardInput <- true
psi.RedirectStandardOutput <- true
psi.RedirectStandardError <- true
psi.UseShellExecute <- false

let p = new Process(StartInfo = psi)
p.Start() |> ignore

// stderr on its own thread — an unread pipe blocks the bridge.
let errBag = ResizeArray<string>()
let errThread = Threading.Thread((fun () -> errBag.Add(p.StandardError.ReadToEnd())))
errThread.IsBackground <- true
errThread.Start()

let send (s: string) =
  p.StandardInput.WriteLine(s)
  p.StandardInput.Flush()

/// Read stdout until `marker` appears or the budget runs out. Never a fixed line count.
let readUntil (marker: string) (budgetMs: int) : string list =
  let sw = Stopwatch.StartNew()
  let acc = ResizeArray<string>()
  let reader = Threading.Thread((fun () ->
    try
      while sw.ElapsedMilliseconds < budgetMs do
        let l = p.StandardOutput.ReadLine()
        if isNull l then Threading.Thread.Sleep 5 else acc.Add(l)
    with _ -> ()))
  reader.IsBackground <- true
  reader.Start()
  while sw.ElapsedMilliseconds < budgetMs && not (acc |> Seq.exists (fun l -> l.Contains marker)) do
    Threading.Thread.Sleep 25
  acc |> Seq.toList

let initializeRequest =
  """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"dogfood","version":"1"}}}"""

send initializeRequest
let init = readUntil "\"id\":1" 15000
printfn "initialize answered: %b" (init |> List.exists (fun l -> l.Contains "\"id\":1"))

send """{"jsonrpc":"2.0","method":"notifications/initialized"}"""

let call =
  sprintf """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"%s","arguments":%s}}""" toolName toolArgs

send call
let answer = readUntil wantedId 45000

printfn "%s answered: %b" toolName (answer |> List.exists (fun l -> l.Contains wantedId))

// Print EVERYTHING the bridge said, not just `data:` lines — a refusal arrives as its own
// frame, and "it did not answer" without the reason is the least useful thing to report.
printfn "--- every line the bridge sent (%d) ---" answer.Length
for l in answer do printfn "  |%s" l

for l in answer do
  if l.StartsWith "data:" && l.Length > 5 then
    let t = l.Substring 5
    let shown = if t.Length > 900 then t.Substring(0, 900) else t
    printfn "  %s" shown

try p.Kill(true) with _ -> ()
if errBag.Count > 0 && not (String.IsNullOrWhiteSpace errBag.[0]) then
  printfn "stderr: %s" errBag.[0]