/// An app started with `run_app` keeps running when a save edits a body, and the save says how.
///
/// The app runs in the worker process, out of the reach of the reload agent in the FSI host, so today every
/// function edit to such an app ends the run and the daemon rebuilds and relaunches it (about six seconds,
/// a new process, every in-memory value gone). A metadata delta applied to the project's own compiled
/// assembly changes the method bodies of the process that is already running, for every instantiation of a
/// generic function, for the closure objects the app holds, and for the objects an instance member runs on.
///
/// One row per case, each a real app on a host of its own. A row that has to be patched asks four things
/// of the SAME process: it serves the new body, its process id is the one it had, the state a restart would
/// have thrown away is still there, and the save says it was patched by metadata delta, after the new
/// body ran. A row that cannot be patched asks for a restart that names what could not be.
///
/// These rows are the outcome gate for the metadata-delta path. They are written before the path exists,
/// and each one's message says what it does today.
module SageFs.Tests.RunAppDeltaTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.HotReloadStateHarness
open SageFs.Tests.RunAppDeltaHarness

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// What a row ends in.
[<RequireQualifiedAccess>]
type Ending =
  /// The same process serves the new code, and the save says it was patched by metadata delta.
  | PatchedByDelta
  /// The save restarts the app and names the declaration that could not be patched.
  | RestartsNaming of declaration: string

type Row = {
  /// The route the row reads: GET /<Name>.
  Name: string
  /// What the row drives, in one line, for the failure message.
  Why: string
  /// The edits one save makes to Handlers.fs, each anchor unique in the file.
  Edits: (string * string) list
  /// What the route serves before the save.
  Before: string
  /// What it serves once the save has landed.
  After: string
  /// Other routes, each read once after the save and the text it must serve.
  AlsoAfter: (string * string) list
  Ending: Ending
}

let rows : Row list = [
  // -- the three calls the path exists for ---------------------------------------------------------------------
  { Name = "generic"
    Why = "a generic function called with an int, a string and a record: a detour reaches the instantiations that ran, a delta reaches all of them"
    Edits = [ "\"generic:A:\"", "\"generic:B:\"" ]
    Before = "generic:A:7|generic:A:s|generic:A:(1,2)"
    After = "generic:B:7|generic:B:s|generic:B:(1,2)"
    AlsoAfter = [ "genericLate", "generic:B:1.5" ]
    Ending = Ending.PatchedByDelta }
  { Name = "closure"
    Why = "a closure the app built at startup and keeps: the object stays, and its body changes"
    Edits = [ "\"closure:A\"", "\"closure:B\"" ]
    Before = "closure:A!?"
    After = "closure:B!?"
    AlsoAfter = []
    Ending = Ending.PatchedByDelta }
  { Name = "instance"
    Why = "an instance member that keeps its count in a field: the object is the same one, so the count carries on"
    Edits = [ "\"instance:A#\"", "\"instance:B#\"" ]
    Before = "instance:A#1"
    After = "instance:B#2"
    AlsoAfter = []
    Ending = Ending.PatchedByDelta }
  // -- two more bodies a delta takes ----------------------------------------------------------------------------
  { Name = "taskBody"
    Why = "a task body, which a Debug build compiles to a closure class"
    Edits = [ "\"taskBody:A\"", "\"taskBody:B\"" ]
    Before = "taskBody:A"
    After = "taskBody:B"
    AlsoAfter = []
    Ending = Ending.PatchedByDelta }
  { Name = "addedMethod"
    Why = "a new function the saved code calls, added to a type the app already runs"
    Edits =
      [ "let addedCaller () : string = \"addedMethod:A\"",
        "let addedHelper () : string = \"addedMethod:B\"\n\nlet addedCaller () : string = addedHelper ()" ]
    Before = "addedMethod:A"
    After = "addedMethod:B"
    AlsoAfter = []
    Ending = Ending.PatchedByDelta }
  // -- the edits that restart, and say what -----------------------------------------------------------------------
  { Name = "rudeVirtual"
    Why = "a virtual member changes its signature, so every override and every caller has to change with it"
    Edits =
      [ "abstract Name: unit -> string", "abstract Name: int -> string"
        "override _.Name() : string = \"rudeVirtual:A\"", "override _.Name(n: int) : string = \"rudeVirtual:B\" + string n"
        "shape.Name()", "shape.Name(1)" ]
    Before = "rudeVirtual:A"
    After = ""
    AlsoAfter = []
    Ending = Ending.RestartsNaming "Shape" }
  { Name = "rudeStruct"
    Why = "a struct gains a field, so its size changes under every value of it already in memory"
    Edits =
      [ "type Dim = { W: int }", "type Dim = { W: int; H: int }"
        "let d = { W = 3 }", "let d = { W = 3; H = 4 }"
        "\"rudeStruct:A\" + string d.W", "\"rudeStruct:B\" + string (d.W + d.H)" ]
    Before = "rudeStruct:A3"
    After = ""
    AlsoAfter = []
    Ending = Ending.RestartsNaming "Dim" }
]

let private json (payload: string) = System.Text.Json.JsonDocument.Parse(payload).RootElement

let private prop (e: System.Text.Json.JsonElement) (name: string) =
  match e.TryGetProperty name with
  | true, v -> v.ToString()
  | false, _ -> ""

/// The restart reasons a verdict names, as `case: message`.
let private reasonsOf (verdict: string) : string list =
  match (json verdict).TryGetProperty "reasons" with
  | true, reasons -> [ for r in reasons.EnumerateArray() -> sprintf "%s: %s" (prop r "case") (prop r "message") ]
  | false, _ -> []

let private shorten (m: string) = if m.Length > 300 then m.Substring(0, 300) + "..." else m

/// What the worker said about a save, in one line.
let private said (verdict: string) : string =
  let v = json verdict
  let reasons = reasonsOf verdict
  match reasons with
  | [] -> sprintf "%s/%s %s" (prop v "type") (prop v "outcome") (shorten (prop v "message"))
  | _ -> sprintf "%s/%s %A" (prop v "type") (prop v "outcome") (reasons |> List.map shorten)

/// A read of a route that says what happened instead of throwing: a process a save ended does not answer.
let private tryGet (app: RunningApp) (route: string) : Task<string> = task {
  try
    return! get app route
  with ex -> return sprintf "<no answer: %s>" (ex.Message.Split('\n').[0])
}

type Observed = {
  Row: Row
  Said: string
  Served: string
  /// Why the row is red, empty when it held.
  Problem: string
}

/// One row against an app that is already running: read it, save the row's edit, read it again, and say
/// what the worker claimed and what the process did.
let exerciseRow (app: RunningApp) (row: Row) : Task<Observed> = task {
  let! pidBefore = get app "pid"
  let! before = get app row.Name
  let! verdict = saveEdits app app.StateSource row.Edits
  let observed served problem = { Row = row; Said = said verdict; Served = served; Problem = problem }
  match before = row.Before with
  | false -> return observed before (sprintf "before the save the route served %A, not %A" before row.Before)
  | true ->
  match row.Ending with
  | Ending.PatchedByDelta ->
    // One read of the route: a stateful row counts its calls, so it cannot be read twice.
    let! served = tryGet app row.Name
    let! pidAfter = tryGet app "pid"
    let outcome = prop (json verdict) "outcome"
    match outcome, served = row.After, pidAfter = pidBefore with
    | "Restarted", _, _ ->
      return observed served (sprintf "the save ended the run (%s) where a metadata delta changes the bodies of the process that is running" outcome)
    | _, false, _ -> return observed served (sprintf "the process serves %A, not %A" served row.After)
    | _, true, false -> return observed served (sprintf "the app was restarted: its process id went from %s to %s" pidBefore pidAfter)
    | _, true, true ->
      let mutable stray = ""
      for route, want in row.AlsoAfter do
        let! got = tryGet app route
        match got = want with
        | true -> ()
        | false -> stray <- sprintf "%s serves %A, not %A" route got want
      match stray with
      | "" ->
        // The reads above ran the patched bodies, so the worker can now say it saw them running.
        let! confirmedVerdict = confirmed app
        let final = json confirmedVerdict
        match prop final "outcome", (prop final "message").Contains("metadata delta", StringComparison.OrdinalIgnoreCase) with
        | "Patched", true -> return observed served ""
        | "Patched", false ->
          return observed served (sprintf "the save ended Patched and does not say it was a metadata delta: %s" (shorten (prop final "message")))
        | other, _ -> return observed served (sprintf "the new body ran and the save ended %s, not Patched: %s" other (shorten confirmedVerdict))
      | problem -> return observed served problem
  | Ending.RestartsNaming declaration ->
    let reasons = reasonsOf verdict
    match prop (json verdict) "outcome", reasons with
    | "Restarted", (_ :: _) when reasons |> List.exists (fun r -> r.Contains(declaration, StringComparison.Ordinal)) ->
      return observed "" ""
    | "Restarted", [] -> return observed "" "the app restarted and the verdict names no cause"
    | "Restarted", _ ->
      return observed "" (sprintf "the restart should name %s, and says %A" declaration reasons)
    | other, _ -> return observed "" (sprintf "this edit cannot be patched, and the save ended %s, not Restarted" other)
}

/// One row on a host of its own, start to finish. A row that cannot be patched ends the run, and every
/// later save in that host would carry its edit too.
let private runRow (runtime: HostRuntime) (row: Row) : Task<Observed> = task {
  let! app = startRunApp runtime
  try
    return! exerciseRow app row
  finally
    stop app
}

/// How many hosts one test runs at once. Each is a real process with its own build.
let private concurrentHosts = 3

let private runRows (runtime: HostRuntime) (selected: Row list) : Task<unit> = task {
  use gate = new System.Threading.SemaphoreSlim(concurrentHosts)
  let one (row: Row) : Task<Observed> = task {
    do! gate.WaitAsync()
    try
      try
        return! runRow runtime row
      with ex ->
        return { Row = row; Said = "no verdict"; Served = ""; Problem = ex.Message.Split('\n').[0] }
    finally
      gate.Release() |> ignore
  }
  let! observed = selected |> List.map one |> Task.WhenAll
  let table =
    observed
    |> Array.map (fun o ->
      match o.Problem with
      | "" -> sprintf "  held    %-12s %s, serves %A" o.Row.Name o.Said o.Served
      | problem -> sprintf "  RED     %-12s %s, serves %A. %s (%s)" o.Row.Name o.Said o.Served problem o.Row.Why)
    |> String.concat "\n"
  observed
  |> Array.filter (fun o -> o.Problem <> "")
  |> Array.length
  |> Expect.equal (sprintf "every run_app delta row holds on %s:\n%s" (HostRuntime.moniker runtime) table) 0
}

let private patching = rows |> List.filter (fun r -> r.Ending = Ending.PatchedByDelta)

let private restarting = rows |> List.filter (fun r -> r.Ending <> Ending.PatchedByDelta)

[<Tests>]
let runAppDeltaTests =
  Integration.hostList "run_app metadata delta" [
    for runtime in HostRuntime.all do
      testTask (sprintf "[%s] an edit to the body of a running run_app app is patched into the same process, by metadata delta" (HostRuntime.moniker runtime)) {
        do! runRows runtime patching
      }
      testTask (sprintf "[%s] an edit a metadata delta cannot take restarts the app and names the declaration" (HostRuntime.moniker runtime)) {
        do! runRows runtime restarting
      }
  ]
