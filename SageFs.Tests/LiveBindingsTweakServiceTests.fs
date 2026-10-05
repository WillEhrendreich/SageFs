/// The daemon's half of the live-bindings knob, on a real temp directory: reading what the files say about a row, and writing a value
/// back through the nudge door. The bytes of the file are the proof: a write changes exactly the range of the expression it named,
/// an undo puts the exact bytes back, and a stale click changes nothing and says why.
module SageFs.Tests.LiveBindingsTweakServiceTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.Tweak
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Server.LiveBindingsTweakService

let tuningText =
  "module Game.Tuning\n\nlet gravity = 9.8\nlet maxHealth = 100\nlet jump = gravity * 2.0\n\nlet tuning =\n  { JumpVelocity = 13.2\n    MaxHealth = 100 }\n"

let unrelatedText = "module Game.Other\n\nlet helper x = x + 1\n"

type Sandbox =
  { Dir: string
    Tuning: string
    Unrelated: string
    Service: Service
    Owned: OwnedFiles
    Clock: int64 ref }

let withSandbox (body: Sandbox -> Task<unit>) : Task<unit> =
  task {
    let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-tweak-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try
      let tuning = Path.Combine(dir, "Tuning.fs")
      let unrelated = Path.Combine(dir, "Other.fs")
      File.WriteAllText(tuning, tuningText)
      File.WriteAllText(unrelated, unrelatedText)
      let tweaksDir = Path.Combine(dir, "tweaks-journal")
      let clock = ref 0L
      let env : Env =
        { Ports = McpNudge.productionPorts tweaksDir
          Locks = FileLocks()
          Stamp = Env.stampOf
          Now = fun () -> clock.Value }
      let files = [ tuning, HotReloadWatch.Watched; unrelated, HotReloadWatch.NotWatched ]
      do!
        body
          { Dir = dir
            Tuning = tuning
            Unrelated = unrelated
            Service = Service.create env
            Owned = Service.ownedOf "s" dir files
            Clock = clock }
    finally
      try Directory.Delete(dir, true) with _ -> ()
  }

let walked (values: (string * obj) list) : LiveValueTree.LiveValueSnapshot =
  LiveValueTree.buildSnapshot "s" 1L (values |> List.map (fun (name, value) -> name, "t", value))

type Tuning = { JumpVelocity: float; MaxHealth: int }

let stateOf (view: TweakView) (key: RowKey) : PersistenceState =
  match TweakView.tryRow view key with
  | Some row -> row.State
  | None -> failtestf "no row for %A in %A" key (view.Rows |> Map.toList |> List.map fst)

let view (sb: Sandbox) (values: (string * obj) list) : Task<TweakView> =
  Service.viewFor sb.Service "s" sb.Owned (walked values) SessionReload.NoReloadYet

let placeOf (view: TweakView) (key: RowKey) : SourceRef =
  match stateOf view key with
  | PersistenceState.InSource place
  | PersistenceState.DiffersFromFile(_, place)
  | PersistenceState.Derived(place, _) -> place
  | other -> failtestf "no place for %A" other

let requestFor (place: SourceRef) (row: RowKey) (verb: TweakVerb) : TweakRequest =
  { Row = row; File = place.File; Address = SourceRef.addressText place; Seen = place.Hash; SeenText = place.Text; Verb = verb }

let noReload () = SessionReload.NoReloadYet

[<Tests>]
let requestTests =
  testList "a row's request, as the page stages it" [

    testCase "the declared names of a file: let, let mutable, let rec, let inline, and, with access words; not a nested or indented use of the word" <| fun _ ->
      Service.declaredNames "let a = 1\nlet mutable b = 2\nlet rec c x = c x\nlet inline d = 3\nlet private e = 4\nand f = 5\n  let inner = 6\n// let commented = 7\n"
      |> Set.toList
      |> Expect.equal "the names at the start of a line" [ "a"; "b"; "c"; "d"; "e"; "f"; "inner" ]

    testCase "a row is its binding then its record fields, joined by slashes, and a request needs a row, a file and a known verb" <| fun _ ->
      TweakRequest.rowOfText "tuning/JumpVelocity" |> Expect.equal "a field" (Ok { Binding = "tuning"; Labels = [ "JumpVelocity" ] })
      TweakRequest.parse "" "a.fs" "M.x" "h" "1" "set" "2" |> Expect.equal "no row" (Error RequestFault.NoRow)
      TweakRequest.parse "x" " " "M.x" "h" "1" "set" "2" |> Expect.equal "no file" (Error RequestFault.NoFile)
      match TweakRequest.parse "x" "a.fs" "M.x" "h" "1" "dance" "2" with
      | Error(RequestFault.UnknownVerb why) -> why |> Expect.stringContains "names the verb" "dance"
      | other -> failtestf "expected an unknown verb, got %A" other

    testCase "each verb is the door's own request: a literal, an expression, an undo, a redo, with the hash the row showed" <| fun _ ->
      let request verb : TweakRequest = { Row = RowKey.top "x"; File = "a.fs"; Address = "M.x"; Seen = "h"; SeenText = "1"; Verb = verb }
      (TweakRequest.rawOf (request (TweakVerb.SetLiteral "2"))).Literal |> Expect.equal "literal" "2"
      (TweakRequest.rawOf (request (TweakVerb.SetExpression "y * 2"))).Expression |> Expect.equal "expression" "y * 2"
      (TweakRequest.rawOf (request (TweakVerb.SetLiteral "2"))).Seen |> Expect.equal "the hash the row showed" "h"
      (TweakRequest.rawOf (request TweakVerb.Undo)).Action |> Expect.equal "undo" "undo"
      (TweakRequest.rawOf (request TweakVerb.Redo)).Action |> Expect.equal "redo" "redo"
  ]

[<Tests>]
let serviceTests =
  testList "the live-bindings knob on a real directory" [

    testTask "a binding a file declares is read through the door; a file that declares none of the names is never parsed" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8; "speed", box 3 ]
        match stateOf rows (RowKey.top "gravity") with
        | PersistenceState.InSource place ->
          place.Text |> Expect.equal "the literal" "9.8"
          place.File |> Expect.equal "in the file" sb.Tuning
        | other -> failtestf "gravity: %A" other
        stateOf rows (RowKey.top "speed") |> Expect.equal "no file holds it" (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt)
        sb.Service.Inspected.ContainsKey sb.Unrelated |> Expect.isFalse "the file that declares none of them was not parsed"
      }) }

    testTask "an unchanged file is not parsed again, and a changed one is" {
      do! withSandbox (fun sb -> task {
        let! _ = view sb [ "gravity", box 9.8 ]
        let first = sb.Service.Inspected.[sb.Tuning]
        let! _ = view sb [ "gravity", box 9.8 ]
        obj.ReferenceEquals(first, sb.Service.Inspected.[sb.Tuning]) |> Expect.isTrue "the cache served the second read"
        File.WriteAllText(sb.Tuning, tuningText.Replace("9.8", "9.9"))
        let! rows = view sb [ "gravity", box 9.8 ]
        obj.ReferenceEquals(first, sb.Service.Inspected.[sb.Tuning]) |> Expect.isFalse "a changed file is read again"
        match stateOf rows (RowKey.top "gravity") with
        | PersistenceState.DiffersFromFile(live, place) ->
          live |> Expect.equal "the REPL still holds" "9.8"
          place.Text |> Expect.equal "the file says" "9.9"
        | other -> failtestf "gravity: %A" other
      }) }

    testTask "a write changes exactly the range of the expression, the row says Writing then settles, and an undo puts the exact bytes back" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows (RowKey.top "gravity")
        let pushes = ref 0
        do! Service.act sb.Service "s" sb.Owned noReload (fun () -> pushes.Value <- pushes.Value + 1) (requestFor place (RowKey.top "gravity") (TweakVerb.SetLiteral "10.4"))
        pushes.Value |> Expect.equal "the page was told twice: the write started, the write finished" 2
        File.ReadAllText sb.Tuning |> Expect.equal "only the literal changed" (tuningText.Replace("let gravity = 9.8", "let gravity = 10.4"))
        let! after = view sb [ "gravity", box 10.4 ]
        PersistenceState.token (stateOf after (RowKey.top "gravity")) |> Expect.equal "the REPL and the file agree" "InSource"
        (TweakView.tryRow after (RowKey.top "gravity")).Value.Undo |> Expect.equal "this row's write is on top" HistoryStep.StepAvailable
        let! undone = view sb [ "gravity", box 10.4 ]
        let placeNow = placeOf undone (RowKey.top "gravity")
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor placeNow (RowKey.top "gravity") TweakVerb.Undo)
        File.ReadAllText sb.Tuning |> Expect.equal "the exact bytes are back" tuningText
        let! redoable = view sb [ "gravity", box 9.8 ]
        (TweakView.tryRow redoable (RowKey.top "gravity")).Value.Redo |> Expect.equal "and the redo is offered" HistoryStep.StepAvailable
      }) }

    testTask "a stale click changes nothing and shows both versions; acting again from what is true then lands" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows (RowKey.top "gravity")
        // Someone edits the file under the row.
        let edited = tuningText.Replace("9.8", "9.9")
        File.WriteAllText(sb.Tuning, edited)
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place (RowKey.top "gravity") (TweakVerb.SetLiteral "12.0"))
        File.ReadAllText sb.Tuning |> Expect.equal "the file is exactly what the person left" edited
        let! stale = view sb [ "gravity", box 9.8 ]
        match stateOf stale (RowKey.top "gravity") with
        | PersistenceState.StaleAddress s ->
          s.Seen.Text |> Expect.equal "what the row showed" "9.8"
          match s.Now with
          | StaleNow.Edited(text, _) -> text |> Expect.equal "what the file holds now" "9.9"
          | other -> failtestf "expected the edited text, got %A" other
        | other -> failtestf "expected StaleAddress, got %A" other
        let current = (TweakView.tryRow stale (RowKey.top "gravity")).Value.Control
        current |> Expect.equal "the control is for what the file holds now" (Control.RealStepper(9.9, Step.Fraction 1))
        let! fresh = Service.indexFor sb.Service sb.Owned (Set.ofList [ "gravity" ])
        let latest = SourceIndex.factsFor fresh "gravity" []
        match latest with
        | SourceFacts.OneSource latestPlace ->
          do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor latestPlace (RowKey.top "gravity") (TweakVerb.SetLiteral "12.0"))
        | other -> failtestf "gravity: %A" other
        File.ReadAllText sb.Tuning |> Expect.equal "from the true hash it lands" (edited.Replace("let gravity = 9.9", "let gravity = 12.0"))
      }) }

    testTask "a formula is edited as an expression, and a record field as a literal, each only in its own range" {
      do! withSandbox (fun sb -> task {
        let record = { JumpVelocity = 13.2; MaxHealth = 100 }
        let! rows = view sb [ "jump", box 19.6; "tuning", box record ]
        let jump = placeOf rows (RowKey.top "jump")
        jump.Text |> Expect.equal "the whole formula" "gravity * 2.0"
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor jump (RowKey.top "jump") (TweakVerb.SetExpression "gravity * 3.0"))
        File.ReadAllText sb.Tuning |> Expect.equal "just the formula" (tuningText.Replace("gravity * 2.0", "gravity * 3.0"))
        let field = RowKey.field (RowKey.top "tuning") "JumpVelocity"
        let! again = view sb [ "jump", box 19.6; "tuning", box record ]
        let fieldPlace = placeOf again field
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor fieldPlace field (TweakVerb.SetLiteral "14.5"))
        File.ReadAllText sb.Tuning |> Expect.equal "and just the field"
          (tuningText.Replace("gravity * 2.0", "gravity * 3.0").Replace("JumpVelocity = 13.2", "JumpVelocity = 14.5"))
      }) }

    testTask "a file the session does not own is refused by the door's own rule, with nothing written" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows (RowKey.top "gravity")
        let outside = Path.Combine(sb.Dir, "Outside.fs")
        File.WriteAllText(outside, tuningText)
        let request = { requestFor place (RowKey.top "gravity") (TweakVerb.SetLiteral "1.0") with File = outside }
        do! Service.act sb.Service "s" sb.Owned noReload ignore request
        File.ReadAllText outside |> Expect.equal "untouched" tuningText
        let! after = view sb [ "gravity", box 9.8 ]
        match stateOf after (RowKey.top "gravity") with
        | PersistenceState.Refused(refusal, _) -> NudgeRefusal.token refusal |> Expect.equal "the door's refusal" "NotOwned"
        | other -> failtestf "expected Refused, got %A" other
      }) }

    testTask "the reload the write caused is watched against the verdict that stood when it was written" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows (RowKey.top "gravity")
        sb.Clock.Value <- 100L
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place (RowKey.top "gravity") (TweakVerb.SetLiteral "10.4"))
        let patched : ReloadFacts =
          { Case = ReloadCase.Patched
            Patched = 1
            Considered = 1
            Message = "patched"
            SuggestedAction = ""
            Mechanism = ReloadOutcome.PatchMechanism.NoPatch
            Declarations = [ "Game.Tuning.gravity" ]
            Callers = CallerState.CallersState.CallersNotReported }
        let! shown = Service.viewFor sb.Service "s" sb.Owned (walked [ "gravity", box 10.4 ]) (SessionReload.Finished patched)
        match (TweakView.tryRow shown (RowKey.top "gravity")).Value.Reload with
        | RowReload.Watching(file, ReloadWatch.Reported facts) ->
          file |> Expect.equal "the file" sb.Tuning
          facts.Declarations |> Expect.equal "the declaration named" [ "Game.Tuning.gravity" ]
        | other -> failtestf "expected the verdict, got %A" other
      }) }

    testTask "a session that is gone takes its memory with it" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows (RowKey.top "gravity")
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place (RowKey.top "gravity") (TweakVerb.SetLiteral "10.4"))
        Service.forget sb.Service "s"
        (Service.memoryOf sb.Service "s") |> Expect.equal "nothing remembered" Memory.empty
      }) }
  ]
