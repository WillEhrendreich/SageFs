/// An app started with `run_app` keeps running when a save edits a body, and the save says how.
///
/// The app runs in the worker process, out of the reach of the reload agent in the FSI host, so with the route
/// off every function edit to such an app ends the run and the daemon rebuilds and relaunches it (about six
/// seconds, a new process, every in-memory value gone). A metadata delta applied to the project's own compiled
/// assembly changes the method bodies of the process that is already running, for every instantiation of a
/// generic function, for the closure objects the app holds, and for the objects an instance member runs on.
///
/// One row per case, each a real app on a host of its own. A row that has to be patched asks four things
/// of the SAME process: it serves the new body, its process id is the one it had, the state a restart would
/// have thrown away is still there, and the save says it was patched by metadata delta, after the new
/// body ran. A row that cannot be patched asks for a restart that names what could not be.
///
/// These rows are the outcome gate for the metadata-delta path. They were written before the path existed,
/// and each one's message says what it does when it does not hold.
module SageFs.Tests.RunAppDeltaTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.HotReloadStateHarness
open SageFs.Tests.RunAppDeltaHarness

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Who refused the edit. The source planner sees a type's shape change before anything is built; the emitter sees what
/// the compiler made of an edit the planner took for a body.
[<RequireQualifiedAccess>]
type Cause =
  /// The source planner restarts it before a build.
  | Planner
  /// The emitter refuses it with this `RudeCause` case, after the build.
  | Emitter of case: string

/// What a row ends in.
[<RequireQualifiedAccess>]
type Ending =
  /// The same process serves the new code, and the save says it was patched by metadata delta.
  | PatchedByDelta
  /// The save restarts the app and names the declaration that could not be patched.
  | RestartsNaming of declaration: string * cause: Cause

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
    Ending = Ending.RestartsNaming ("Shape", Cause.Planner) }
  { Name = "rudeStruct"
    Why = "a struct gains a field, so its size changes under every value of it already in memory"
    Edits =
      [ "type Dim = { W: int }", "type Dim = { W: int; H: int }"
        "let d = { W = 3 }", "let d = { W = 3; H = 4 }"
        "\"rudeStruct:A\" + string d.W", "\"rudeStruct:B\" + string (d.W + d.H)" ]
    Before = "rudeStruct:A3"
    After = ""
    AlsoAfter = []
    Ending = Ending.RestartsNaming ("Dim", Cause.Planner) }
  { Name = "closure"
    Why = "a lambda starts capturing one more value: the closure object the app holds has no field for it, which only the compiled shapes show"
    Edits = [ "fun () -> \"closure:A\" + tag", "fun () -> \"closure:A\" + tag + suffix" ]
    Before = "closure:A!?"
    After = ""
    AlsoAfter = []
    Ending = Ending.RestartsNaming ("makeHeld", Cause.Emitter "FieldsChanged") }
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
        // The first verdict said Pending, by the same mechanism: a client reads `mechanism`, never the words.
        match prop final "outcome", prop final "mechanism", prop (json verdict) "outcome", prop (json verdict) "mechanism" with
        | "Patched", "metadata-delta", "PatchPending", "metadata-delta" -> return observed served ""
        | "Patched", mechanism, _, _ when mechanism <> "metadata-delta" ->
          return observed served (sprintf "the save ended Patched by %A, not by metadata delta: %s" mechanism (shorten (prop final "message")))
        | "Patched", _, first, firstMechanism ->
          return observed served (sprintf "the first verdict was %s by %A, and should have been PatchPending by metadata-delta" first firstMechanism)
        | other, _, _, _ -> return observed served (sprintf "the new body ran and the save ended %s, not Patched: %s" other (shorten confirmedVerdict))
      | problem -> return observed served problem
  | Ending.RestartsNaming (declaration, cause) ->
    let reasons = reasonsOf verdict
    let fromWho (r: string) =
      match cause with
      | Cause.Planner -> true
      | Cause.Emitter case -> r.StartsWith(case + ":", StringComparison.Ordinal)
    match prop (json verdict) "outcome", reasons with
    | "Restarted", (_ :: _) when reasons |> List.exists (fun r -> fromWho r && r.Contains(declaration, StringComparison.Ordinal)) ->
      return observed "" ""
    | "Restarted", [] -> return observed "" "the app restarted and the verdict names no cause"
    | "Restarted", _ ->
      return observed "" (sprintf "the restart should name %s (%A), and says %A" declaration cause reasons)
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
      testTask (sprintf "[%s] saves after saves each land on the one before: the chain carries from the first delta to the third, in the same process" (HostRuntime.moniker runtime)) {
        let! app = startRunApp runtime
        try
          let! pid = get app "pid"
          let mutable problems = []
          for was, now in [ "A", "B"; "B", "C"; "C", "D" ] do
            let! verdict = saveEdits app app.StateSource [ sprintf "\"closure:%s\"" was, sprintf "\"closure:%s\"" now ]
            let! served = settle app "closure" (sprintf "closure:%s!?" now)
            let! pidAfter = tryGet app "pid"
            match prop (json verdict) "type", prop (json verdict) "mechanism", served = sprintf "closure:%s!?" now, pidAfter = pid with
            | "pending", "metadata-delta", true, true -> ()
            | _ -> problems <- sprintf "%s to %s: said %s, serves %A, pid %s (was %s)" was now (said verdict) served pidAfter pid :: problems
          problems |> Expect.isEmpty "every save in the chain is a delta on the same process"
        finally
          stop app
      }
      testTask (sprintf "[%s] a patch and then an edit a delta cannot take: the first is patched, the second restarts and names what could not be" (HostRuntime.moniker runtime)) {
        let! app = startRunApp runtime
        try
          let! first = saveEdits app app.StateSource [ "\"taskBody:A\"", "\"taskBody:B\"" ]
          prop (json first) "type" |> Expect.equal (sprintf "the first save is patched: %s" (said first)) "pending"
          let! second = saveEdits app app.StateSource [ "fun () -> \"closure:A\" + tag", "fun () -> \"closure:A\" + tag + suffix" ]
          reasonsOf second
          |> List.exists (fun r -> r.StartsWith("FieldsChanged:", StringComparison.Ordinal) && r.Contains "makeHeld")
          |> Expect.isTrue (sprintf "the second save restarts and names the closure of makeHeld: %s" (said second))
        finally
          stop app
      }
      testTask (sprintf "[%s] a save that does not build is reported as it is and leaves the process alone, and the fix is patched on the chain that was there" (HostRuntime.moniker runtime)) {
        let! app = startRunApp runtime
        try
          let! pid = get app "pid"
          let! broken = saveEdits app app.StateSource [ "\"closure:A\"", "undefinedNameZ" ]
          prop (json broken) "type" |> Expect.equal (sprintf "a build that fails is reported as failed: %s" (said broken)) "failed"
          let! stillServing = get app "closure"
          stillServing |> Expect.equal "the process keeps serving what it had" "closure:A!?"
          let! fixedSave = saveEdits app app.StateSource [ "undefinedNameZ", "\"closure:B\"" ]
          prop (json fixedSave) "mechanism" |> Expect.equal (sprintf "the fix is patched by delta: %s" (said fixedSave)) "metadata-delta"
          let! served = settle app "closure" "closure:B!?"
          served |> Expect.equal "and the process serves it" "closure:B!?"
          let! pidAfter = get app "pid"
          pidAfter |> Expect.equal "in the same process" pid
        finally
          stop app
      }
      testTask (sprintf "[%s] with the route off, a body edit to a run_app app restarts the app as it always did" (HostRuntime.moniker runtime)) {
        let! app = startRunAppWith SageFs.Features.MetadataDelta.MetadataDeltaMode.Off runtime
        try
          let! verdict = saveEdits app app.StateSource [ "\"closure:A\"", "\"closure:B\"" ]
          prop (json verdict) "outcome" |> Expect.equal (sprintf "the edit restarts: %s" (said verdict)) "Restarted"
          prop (json verdict) "mechanism" |> Expect.equal "and it is not a patch, so it names no mechanism" ""
        finally
          stop app
      }
  ]
