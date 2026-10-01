/// Hot reload parity: the edits .NET Hot Reload takes for C# that the shape
/// matrix never asked about.
///
/// One row per case, each a real app. The app is built the way SageFs builds a
/// session's project, started inside a real host on the runtime under test, and
/// its route table is captured once at startup, the way a Falco, Giraffe or
/// Saturn app does it. A row saves a real edit and then reads what the SAME
/// process serves, and it reads what the planner said about the save. Two kinds
/// of ending are allowed and nothing else:
///
///   * the process serves the new code and the save is `Patched` (reached only
///     after the new body has been seen running), or
///   * the process still serves the old code and the save names the reason it
///     could not be patched, one of the closed set of restart reasons.
///
/// A save that moves behaviour while saying it did nothing, or says `Patched`
/// while the process serves the old code, is a red row. So is a restart that
/// names the wrong cause.
///
/// The rows run on .NET 10 and .NET 11. A runtime that cannot run a row is a red
/// row, not a skipped one.
module SageFs.Tests.HotReloadParityTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.HotReloadStateHarness

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The fixture the rows run against: ParityFixture/Parity.fs and App.fs.
let parityFixture =
  { Folder = "HotReloadParityFixture"
    Sources = [ "Parity.fs"; "App.fs" ]
    Project = "ParityFixture"
    ReadyRoute = "ready" }

/// What a row ends in.
[<RequireQualifiedAccess>]
type Ending =
  /// The process serves the new code and the save is `Patched`.
  | Patches
  /// The process still serves the old code, and the save names this restart
  /// reason (the `case` on the wire).
  | Restarts of reason: string

type Row = {
  /// The route the row reads: GET /<Name>.
  Name: string
  /// What the row drives, in one line, for the failure message.
  Why: string
  /// The edits one save makes to Parity.fs, each anchor unique in the file.
  Edits: (string * string) list
  /// What the route serves before the save.
  Before: string
  /// What it serves once the save has landed. For a row that restarts this is
  /// what the process must STILL serve.
  After: string
  Ending: Ending
}

let private rows : Row list = [
  // ── closures ──────────────────────────────────────────────────────────────
  { Name = "inlineLambda"
    Why = "a lambda written inline in the route list, so there is no named method to re-point"
    Edits = [ "\"inlineLambda:A\"", "\"inlineLambda:B\"" ]
    Before = "inlineLambda:A"
    After = "inlineLambda:B"
    Ending = Ending.Patches }
  { Name = "inlineCapture"
    Why = "an inline lambda that captures a startup value; the body changes and the captured set does not"
    Edits = [ "\"inlineCapture:A\"", "\"inlineCapture:B\"" ]
    Before = "inlineCapture:Ax"
    After = "inlineCapture:Bx"
    Ending = Ending.Patches }
  { Name = "taskLambda"
    Why = "an inline lambda whose body is a task that awaits"
    Edits = [ "\"taskLambda:A\"", "\"taskLambda:B\"" ]
    Before = "taskLambda:A"
    After = "taskLambda:B"
    Ending = Ending.Patches }
  { Name = "asyncLambda"
    Why = "an inline lambda whose body is an async that awaits"
    Edits = [ "\"asyncLambda:A\"", "\"asyncLambda:B\"" ]
    Before = "asyncLambda:A"
    After = "asyncLambda:B"
    Ending = Ending.Patches }
  { Name = "heldClosure"
    Why = "a function hands back a closure at startup and the route table keeps it; the function's lambda is what changes"
    Edits = [ "\"heldClosure:A\"", "\"heldClosure:B\"" ]
    Before = "heldClosure:A!?"
    After = "heldClosure:B!?"
    Ending = Ending.Patches }
  // ── async and task, named ─────────────────────────────────────────────────
  { Name = "taskNamed"
    Why = "a named function whose body is a task that awaits"
    Edits = [ "\"taskNamed:A\"", "\"taskNamed:B\"" ]
    Before = "taskNamed:A"
    After = "taskNamed:B"
    Ending = Ending.Patches }
  { Name = "asyncNamed"
    Why = "a named function whose body is an async that awaits"
    Edits = [ "\"asyncNamed:A\"", "\"asyncNamed:B\"" ]
    Before = "asyncNamed:A"
    After = "asyncNamed:B"
    Ending = Ending.Patches }
  // ── instance members ──────────────────────────────────────────────────────
  { Name = "instance"
    Why = "an instance member of an object built once at startup"
    Edits = [ "\"instance:A\"", "\"instance:B\"" ]
    Before = "instance:A"
    After = "instance:B"
    Ending = Ending.Patches }
  { Name = "instanceState"
    Why = "an instance member that keeps its state in a field: the object is the same one, so the count has to carry on"
    Edits = [ "\"instanceState:A#\"", "\"instanceState:B#\"" ]
    Before = "instanceState:A#1"
    After = "instanceState:B#2"
    Ending = Ending.Patches }
  // ── added, removed and re-signed ──────────────────────────────────────────
  { Name = "addedFunction"
    Why = "a new function the saved code calls, with the call added in the same save"
    Edits =
      [ "let addedFunctionCaller () : string = \"addedFunction:A\"",
        "let addedFunctionHelper () : string = \"addedFunction:B\"\n\nlet addedFunctionCaller () : string = addedFunctionHelper ()" ]
    Before = "addedFunction:A"
    After = "addedFunction:B"
    Ending = Ending.Patches }
  { Name = "addedType"
    Why = "a new record type the saved code uses"
    Edits =
      [ "let addedTypeCaller () : string = \"addedType:A\"",
        "type AddedBox = { Text: string }\n\nlet addedTypeCaller () : string = ({ Text = \"addedType:B\" }: AddedBox).Text" ]
    Before = "addedType:A"
    After = "addedType:B"
    Ending = Ending.Patches }
  { Name = "addedValue"
    Why = "a new value the saved code reads"
    Edits =
      [ "let addedValueCaller () : string = \"addedValue:A\"",
        "let addedLabel = \"addedValue:\" + \"B\"\n\nlet addedValueCaller () : string = addedLabel" ]
    Before = "addedValue:A"
    After = "addedValue:B"
    Ending = Ending.Patches }
  { Name = "removed"
    Why = "a function removed together with its only caller's use of it"
    Edits =
      [ "let removedHelper () : string = \"removed:A\"\n\n", ""
        "let removedCaller () : string = removedHelper ()", "let removedCaller () : string = \"removed:B\"" ]
    Before = "removed:A"
    After = "removed:B"
    Ending = Ending.Patches }
  { Name = "signature"
    Why = "a function gains a parameter, and its caller in the same save passes it"
    Edits =
      [ "let sigHandler (who: string) : string = \"signature:A\" + who",
        "let sigHandler (who: string) (times: int) : string = \"signature:B\" + who + string times"
        "sigHandler \"!\"", "sigHandler \"!\" 2" ]
    Before = "signature:A!"
    After = "signature:B!2"
    Ending = Ending.Patches }
  // ── the cases that have to restart, and say why ───────────────────────────
  { Name = "inlineNewCapture"
    Why = "an inline lambda starts capturing a value it did not capture, so closures already built have no field for it"
    Edits = [ "fun () -> Task.FromResult \"inlineNewCapture:A\")", "fun () -> Task.FromResult (\"inlineNewCapture:B\" + extra))" ]
    Before = "inlineNewCapture:A"
    After = "inlineNewCapture:A"
    Ending = Ending.Restarts "ClosureShapeChanged" }
  { Name = "instanceNewField"
    Why = "an instance member starts reading a constructor argument, so the compiler adds a field the live object does not have"
    Edits = [ "member _.Tag() : string = \"instanceNewField:A\"", "member _.Tag() : string = \"instanceNewField:B\" + tag" ]
    Before = "instanceNewField:A"
    After = "instanceNewField:A"
    Ending = Ending.Restarts "InstanceLayoutChanged" }
  { Name = "generic"
    Why = "a generic function: a patch reaches the instantiations that have run, and one that runs later would still get the old body"
    Edits = [ "let genericTag<'T> (x: 'T) : string = \"generic:A\" + string x", "let genericTag<'T> (x: 'T) : string = \"generic:B\" + string x" ]
    Before = "generic:As|generic:A7"
    After = "generic:As|generic:A7"
    Ending = Ending.Restarts "GenericFunction" }
]

let private json (payload: string) = System.Text.Json.JsonDocument.Parse(payload).RootElement

let private str (e: System.Text.Json.JsonElement) (name: string) =
  match e.TryGetProperty name with
  | true, v -> v.ToString()
  | false, _ -> failtestf "no '%s' in %s" name (e.GetRawText())

/// The restart reasons a verdict names, by `case`.
let private reasonCases (verdict: string) : string list =
  match (json verdict).TryGetProperty "reasons" with
  | true, reasons -> [ for r in reasons.EnumerateArray() -> str r "case" ]
  | false, _ -> []

/// What a row saw, in the words of the table the run ends with.
type private Observed = {
  Row: Row
  /// What the worker said about the save: `type/outcome` and the restart reasons.
  Said: string
  /// What the route served after the save.
  Served: string
  /// Why the row is red, empty when it held.
  Problem: string
}

let private said (verdict: string) : string =
  let v = json verdict
  let reasons = reasonCases verdict
  let firstMessage =
    match v.TryGetProperty "reasons" with
    | true, listed ->
      [ for r in listed.EnumerateArray() -> str r "message" ]
      |> List.tryHead
      |> Option.map (fun m -> sprintf " (%s)" (if m.Length > 200 then m.Substring(0, 200) + "..." else m))
      |> Option.defaultValue ""
    | false, _ -> ""
  let shorten (m: string) = if m.Length > 300 then m.Substring(0, 300) + "..." else m
  match reasons with
  | [] ->
    match str v "outcome" with
    // A save that did not compile says what the compiler said, which is the whole story.
    | "CompileFailed" ->
      let diagnostics =
        match v.TryGetProperty "diagnostics" with
        | true, listed -> [ for d in listed.EnumerateArray() -> str d "message" ] |> String.concat " | "
        | false, _ -> ""
      sprintf "%s/%s (%s) %s" (str v "type") (str v "outcome") (shorten (str v "error")) (shorten diagnostics)
    | _ -> sprintf "%s/%s" (str v "type") (str v "outcome")
  | _ -> sprintf "%s/%s %A%s" (str v "type") (str v "outcome") reasons firstMessage

/// One row on a host of its own, start to finish. A row gets its own host because a save the
/// product cannot patch leaves its edit on disk and the baseline behind, so every later save in
/// that host would carry it too: one red row would make every row after it red for the wrong reason.
let private runRow (runtime: HostRuntime) (row: Row) : Task<Observed> = task {
  let! app = startFixture parityFixture runtime ignore
  try
    let! before = get app row.Name
    let! verdict = saveEdits app app.StateSource row.Edits
    // One read of the route: a stateful row counts its calls, so it cannot be read twice.
    let! served = get app row.Name
    let observed problem = { Row = row; Said = said verdict; Served = served; Problem = problem }
    match before = row.Before with
    | false -> return observed (sprintf "before the save the route served %A, not %A" before row.Before)
    | true ->
    match row.Ending with
    | Ending.Patches ->
      let outcome = str (json verdict) "type"
      match outcome = "pending", served = row.After with
      | false, _ -> return observed (sprintf "the save should be applied in place, and the worker said %s" (said verdict))
      | true, false -> return observed (sprintf "the save said it was applied, and the process serves %A, not %A" served row.After)
      | true, true ->
        // The read above ran the patched body, so the worker can now say it was seen running.
        let! confirmedVerdict = confirmed app
        match str (json confirmedVerdict) "outcome" with
        | "Patched" -> return observed ""
        | other -> return observed (sprintf "the new body ran and the save ended %s, not Patched: %s" other confirmedVerdict)
    | Ending.Restarts reason ->
      let claims =
        match str (json verdict) "outcome" = "Patched", str (json verdict) "type" = "pending" with
        | true, _ -> Some "the worker claims a patch for a save that cannot land"
        | _, true -> Some "the page was told to refresh into the same bytes"
        | false, false -> None
      match claims, served = row.After, reasonCases verdict = [ reason ] with
      | Some problem, _, _ -> return observed problem
      | None, false, _ -> return observed (sprintf "the process was changed (serves %A) by a save that says it needs a restart" served)
      | None, true, false -> return observed (sprintf "the restart should name %s, and the worker said %s" reason (said verdict))
      | None, true, true -> return observed ""
  finally
    stop app
}

/// How many hosts one test runs at once. Each is a real process with its own build, so this is
/// bounded by what a machine can run, not by what is quick.
let private concurrentHosts = 3

/// Every row of one kind on one runtime, each on its own host, and ONE verdict at the end: a table of
/// the rows that held and the ones that did not, so a red run reads as a matrix and not as the first
/// failure.
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
      | "" -> sprintf "  held    %-18s %s, serves %A" o.Row.Name o.Said o.Served
      | problem -> sprintf "  RED     %-18s %s, serves %A. %s (%s)" o.Row.Name o.Said o.Served problem o.Row.Why)
    |> String.concat "\n"
  observed
  |> Array.filter (fun o -> o.Problem <> "")
  |> Array.length
  |> Expect.equal (sprintf "every parity row holds on %s:\n%s" (HostRuntime.moniker runtime) table) 0
}

let private patching = rows |> List.filter (fun r -> r.Ending = Ending.Patches)

let private restarting = rows |> List.filter (fun r -> r.Ending <> Ending.Patches)

[<Tests>]
let hotReloadParityTests =
  Integration.hostList "hot reload parity with .NET Hot Reload" [
    for runtime in HostRuntime.all do
      testTask (sprintf "[%s] every edit that has to land in the running app, lands, and is Patched only after its new body ran" (HostRuntime.moniker runtime)) {
        do! runRows runtime patching
      }
      testTask (sprintf "[%s] every edit that has to restart says why, and leaves the running app alone" (HostRuntime.moniker runtime)) {
        do! runRows runtime restarting
      }
  ]
