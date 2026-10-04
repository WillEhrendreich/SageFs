/// Does nudging a value change what the RUNNING APP serves? Outcome tests only:
/// a real host runs a real app on each runtime the host ships for, the nudge door
/// writes the source file the way `nudge_value` does (the real disk, the real
/// ownership list from the worker), hot reload picks the save up, and the same
/// process is asked what it serves. Undo is checked the same way, and the bytes
/// of the file are compared before and after.
module SageFs.Tests.NudgeOutcomeTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.Nudge
open SageFs.Tests.HotReloadStateHarness

module Integration = SageFs.Tests.TestInfrastructure.Integration

let label : TweakAddress = { ModulePath = [ "StateFixture"; "State" ]; BindingName = "label"; Path = [] }

let session = "nudge-outcome"

/// The door as the daemon wires it: the real disk, a real clock, journals under the run's own directory, and
/// the files the worker itself reports as the session's.
type Door =
  { Ports: Ports
    Locks: FileLocks
    Owned: OwnedFiles }

let doorFor (app: RunningApp) : Task<Door> = task {
  let! listed = getWorker app "/hotreload"
  let owned =
    match McpNudge.ownedFilesFromJson session app.RunDir listed with
    | Ok owned -> owned
    | Error refusal -> failtestf "the worker's file list was not readable: %s" (NudgeRefusal.token refusal)
  let ports : Ports =
    { Files = NudgeFs.steps
      Now = fun () -> DateTime.UtcNow.Ticks
      JournalPathOf = fun file -> NudgeFs.journalPathFor (Path.Combine(app.RunDir, "tweaks")) session (Ownership.pathOf file) }
  return { Ports = ports; Locks = FileLocks(); Owned = owned }
}

let request (door: Door) (app: RunningApp) (raw: RawNudge) : NudgeRequest =
  match parse door.Owned NudgeFs.steps.KindOf raw with
  | Ok request -> request
  | Error refusal -> failtestf "the request was refused at the edge: %s" (NudgeRefusal.token refusal)

let run (door: Door) (req: NudgeRequest) : Task<Result<Ran, NudgeRefusal>> =
  execute door.Ports door.Locks Timeouts.nudgeFileLock req

let raw action address seen literal expression : RawNudge =
  { Action = action; File = ""; Address = address; Seen = seen; Literal = literal; Expression = expression }

/// A request for the app's own state file, filled in with the full path.
let forStateFile (app: RunningApp) (r: RawNudge) = { r with File = app.StateSource }

/// The hash an inspect reports for the label's expression: what a caller carries into its write.
let seenOfLabel (door: Door) (app: RunningApp) : Task<string> = task {
  let! ran = run door (request door app (forStateFile app (raw "inspect" (NudgeAddress.format label) "" "" "")))
  match ran with
  | Ok { Outcome = NudgeOutcome.Inspected inspected } ->
    return inspected.Items |> List.find (fun i -> i.Address = label) |> _.Hash
  | other -> return failtestf "inspect did not come back as an inspection: %A" other
}

let verdictAfter (app: RunningApp) (what: string) (door: Door) (req: NudgeRequest) : Task<string * Result<Ran, NudgeRefusal>> = task {
  let result = ref (Error(NudgeRefusal.NothingToUndo))
  let! verdict =
    awaitVerdictAfter TestTimeouts.saveVerdict app what (fun () -> task {
      let! ran = run door req
      result.Value <- ran })
  return verdict, result.Value
}

let json (payload: string) = JsonDocument.Parse(payload).RootElement

let literalNudgeChangesWhatTheAppServes (runtime: HostRuntime) =
  testTask (sprintf "[%s] nudging a literal changes what the running app serves, rewrites only that range, and undo puts every byte back" (HostRuntime.moniker runtime)) {
    let! app = start runtime
    try
      let! door = doorFor app
      let original = File.ReadAllBytes app.StateSource
      let! served = get app "label"
      served |> Expect.equal "the app starts serving the original" "A"

      let! seen = seenOfLabel door app
      let write = request door app (forStateFile app (raw "set" (NudgeAddress.format label) seen "B" ""))
      let! verdict, outcome = verdictAfter app "the nudge of the label" door write
      match outcome with
      | Ok { Outcome = NudgeOutcome.Written receipt; Notes = notes } ->
        receipt.Before |> Expect.equal "the literal it replaced" "\"A\""
        receipt.After |> Expect.equal "and what it wrote" "\"B\""
        notes |> Expect.isEmpty "a watched file, a literal: nothing to caveat"
      | other -> failtestf "the nudge did not land: %A\nHost log:\n%s" other (RunningApp.log app)
      verdict |> Expect.stringContains (sprintf "hot reload applied the patch.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "\"type\":\"pending\""

      let! nudged = settle app "label" "B"
      nudged |> Expect.equal (sprintf "the app now serves the nudged value.\nVerdict: %s\nHost log:\n%s" verdict (RunningApp.log app)) "B"
      File.ReadAllText app.StateSource
      |> Expect.equal "the source holds the new literal and nothing else changed" (Text.Encoding.UTF8.GetString(original).Replace("let label () : string = \"A\"", "let label () : string = \"B\""))

      let! undoVerdict, undone = verdictAfter app "the undo of the nudge" door (request door app (forStateFile app (raw "undo" "" "" "" "")))
      match undone with
      | Ok { Outcome = NudgeOutcome.Undone _ } -> ()
      | other -> failtestf "the undo did not land: %A" other
      let! restored = settle app "label" "A"
      restored |> Expect.equal (sprintf "the app serves the original again.\nVerdict: %s\nHost log:\n%s" undoVerdict (RunningApp.log app)) "A"
      File.ReadAllBytes app.StateSource |> Expect.equal "the file is byte-identical to before the nudge" original
    finally
      stop app
  }

let expressionNudgeAndItsRefusals (runtime: HostRuntime) =
  testTask (sprintf "[%s] an expression nudge lands, a stale one is refused, and an expression that does not compile is reported while the app keeps its last good value" (HostRuntime.moniker runtime)) {
    let! app = start runtime
    try
      let! door = doorFor app
      let original = File.ReadAllBytes app.StateSource
      let! seen = seenOfLabel door app

      // A formula, not a literal.
      let formula = request door app (forStateFile app (raw "set" (NudgeAddress.format label) seen "" "\"A\" + \"!\""))
      let! _, landed = verdictAfter app "the expression nudge" door formula
      match landed with
      | Ok { Outcome = NudgeOutcome.Written _; Notes = notes } ->
        notes |> Expect.contains "an expression is parsed, not type-checked, and the reply says so" RunNote.ExpressionNotTypeChecked
      | other -> failtestf "the expression nudge did not land: %A" other
      let! formulaServed = settle app "label" "A!"
      formulaServed |> Expect.equal (sprintf "the app serves the formula's value.\nHost log:\n%s" (RunningApp.log app)) "A!"

      // The caller's hash is from before that write: the door refuses, and the file does not move.
      let afterFormula = File.ReadAllBytes app.StateSource
      let stale = request door app (forStateFile app (raw "set" (NudgeAddress.format label) seen "C" ""))
      let! staleResult = run door stale
      match staleResult with
      | Error(NudgeRefusal.SourceMoved _) -> ()
      | other -> failtestf "a stale hash must be refused as SourceMoved, got %A" other
      File.ReadAllBytes app.StateSource |> Expect.equal "the refusal left the file byte-identical" afterFormula

      // An expression of the wrong type is written (the door only parses), hot reload says it failed,
      // the app keeps serving what it had, and undo puts the file back.
      let! freshSeen = seenOfLabel door app
      let illTyped = request door app (forStateFile app (raw "set" (NudgeAddress.format label) freshSeen "" "42"))
      let! failedVerdict, _ = verdictAfter app "the ill-typed expression nudge" door illTyped
      failedVerdict |> Expect.stringContains (sprintf "the reload verdict names the failure.\nVerdict: %s" failedVerdict) "\"type\":\"failed\""
      let! stillServed = get app "label"
      stillServed |> Expect.equal "the app keeps serving its last good value" "A!"

      let! _, undoIll = verdictAfter app "undoing the ill-typed nudge" door (request door app (forStateFile app (raw "undo" "" "" "" "")))
      match undoIll with
      | Ok { Outcome = NudgeOutcome.Undone _ } -> ()
      | other -> failtestf "the undo of the ill-typed nudge did not land: %A" other
      File.ReadAllBytes app.StateSource |> Expect.equal "undo put the formula's file back" afterFormula
      let! _, undoFormula = verdictAfter app "undoing the formula" door (request door app (forStateFile app (raw "undo" "" "" "" "")))
      match undoFormula with
      | Ok { Outcome = NudgeOutcome.Undone _ } -> ()
      | other -> failtestf "the undo of the formula did not land: %A" other
      File.ReadAllBytes app.StateSource |> Expect.equal "and then the original, byte for byte" original
      let! restored = settle app "label" "A"
      restored |> Expect.equal (sprintf "the app serves the original again.\nHost log:\n%s" (RunningApp.log app)) "A"
    finally
      stop app
  }

[<Tests>]
let nudgeOutcomeTests =
  Integration.hostList "nudge_value changes what a running app serves" [
    for runtime in HostRuntime.all do
      literalNudgeChangesWhatTheAppServes runtime
      expressionNudgeAndItsRefusals runtime
  ]
