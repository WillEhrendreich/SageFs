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
      TweakVerb.ofToken "steps" "-3" |> Expect.equal "steps are a whole number the page counted" (Ok(TweakVerb.Steps -3))
      TweakVerb.ofToken "steps" "three" |> Expect.isError "a count that is not a number is refused"
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

    testTask "steps are counted by the page and turned into the literal here: three steps up from 9.8 is 10.1, written in the file's own style" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows (RowKey.top "gravity")
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place (RowKey.top "gravity") (TweakVerb.Steps 3))
        File.ReadAllText sb.Tuning |> Expect.equal "three tenths up" (tuningText.Replace("let gravity = 9.8", "let gravity = 10.1"))
        let! again = view sb [ "gravity", box 10.1; "maxHealth", box 100 ]
        let health = RowKey.top "maxHealth"
        let healthPlace = placeOf again health
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor healthPlace health (TweakVerb.Steps -7))
        File.ReadAllText sb.Tuning |> Expect.equal "an integer moves by whole units"
          (tuningText.Replace("let gravity = 9.8", "let gravity = 10.1").Replace("let maxHealth = 100", "let maxHealth = 93"))
      }) }

    testTask "steps on a row that is not a number refuse with a reason and write nothing" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "jump", box 19.6 ]
        let place = placeOf rows (RowKey.top "jump")
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place (RowKey.top "jump") (TweakVerb.Steps 2))
        File.ReadAllText sb.Tuning |> Expect.equal "untouched" tuningText
        let! after = view sb [ "jump", box 19.6 ]
        PersistenceState.token (stateOf after (RowKey.top "jump")) |> Expect.equal "the row says it was refused" "Refused"
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

/// Public on purpose: the live-values walk takes a non-public record for an opaque class.
type CallPlayer = { Health: int; Speed: float; Name: string }

let private callFiles =
  [ "State.fs",
    "module Game.Tuning\n\ntype CallPlayer = { Health: int; Speed: float; Name: string }\n\nlet initPlayer () = { Health = 100; Speed = 4.5; Name = \"Ada\" }\n\nlet player = initPlayer ()\nlet spare = initPlayer ()\n"
    "Other.fs", "module Game.Other\n\nlet again = Game.Tuning.initPlayer ()\n" ]

/// A real directory holding `callFiles`, both owned and watched, and a service over it.
let private withCallFiles (body: string -> Service -> OwnedFiles -> Task<unit>) : Task<unit> =
  task {
    let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-call-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    try
      let paths = callFiles |> List.map (fun (name, text) -> let path = Path.Combine(dir, name) in File.WriteAllText(path, text); path)
      let env : Env =
        { Ports = McpNudge.productionPorts (Path.Combine(dir, "tweaks-journal"))
          Locks = FileLocks()
          Stamp = Env.stampOf
          Now = fun () -> 0L }
      do! body dir (Service.create env) (Service.ownedOf "s" dir (paths |> List.map (fun path -> path, HotReloadWatch.Watched)))
    finally
      try Directory.Delete(dir, true) with _ -> ()
  }

[<Tests>]
let callTests =
  testList "the service follows a call into its initializer" [

    testTask "WHY — a part of a value built by a call is placed in the callee, the callers are counted in every owned file, and the write changes only that literal" {
      do! withCallFiles (fun dir service owned -> task {
        let player = box { Health = 100; Speed = 4.5; Name = "Ada" }
        let health = RowKey.field (RowKey.top "player") "Health"
        let! rows = Service.viewFor service "s" owned (walked [ "player", player ]) SessionReload.NoReloadYet
        let row = (TweakView.tryRow rows health).Value
        match row.Through with
        | ThroughCall.Through followed ->
          followed.Callee |> Expect.equal "the callee" "initPlayer"
          followed.Callers |> Expect.equal "two calls in State.fs and one qualified call in Other.fs, which declares no name the pane asked about" (CallerCount.Counted 3)
        | other -> failtestf "expected the call to be followed, got %A" other
        let place = placeOf rows health
        place.File |> Expect.equal "the callee's file" (Path.Combine(dir, "State.fs"))
        do! Service.act service "s" owned noReload ignore (requestFor place health (TweakVerb.SetLiteral "62"))
        let written = File.ReadAllText(Path.Combine(dir, "State.fs"))
        written |> Expect.equal "exactly one literal changed" ((snd callFiles.[0]).Replace("Health = 100", "Health = 62"))
        File.ReadAllText(Path.Combine(dir, "Other.fs")) |> Expect.equal "the other file is untouched" (snd callFiles.[1])
      }) }

    testTask "a binding built by a call of the framework is not read whole, so it stays out of the files, as before" {
      do! withCallFiles (fun _ service owned -> task {
        let! rows = Service.viewFor service "s" owned (walked [ "answer", box 42 ]) SessionReload.NoReloadYet
        stateOf rows (RowKey.top "answer") |> Expect.equal "not in a file" (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt)
      }) }
  ]

// ── a number's chosen range and step, and the limits of its own type ──

let private limitsFile (sb: Sandbox) : string = Service.limitsPathOf sb.Dir

let private gravityKey : RowKey = RowKey.top "gravity"

/// Choose a row's range and step the way the editor does, from the three texts. The answer is the note the editor shows.
let private choose (sb: Sandbox) (row: RowKey) (lowest: string) (highest: string) (step: string) : string =
  Service.setLimits sb.Service sb.Dir row { LowestText = lowest; HighestText = highest; StepText = step }

let private isRefused (state: PersistenceState) : bool =
  match state with
  | PersistenceState.Refused _ -> true
  | _ -> false

[<Tests>]
let limitsTests =
  testList "a number's range and step, on a real directory" [

    testTask "WHY — a step past the chosen range is refused, the file is byte-identical, and a step that lands inside it is written" {
      do! withSandbox (fun sb -> task {
        choose sb gravityKey "0" "10" "0.1" |> Expect.equal "kept" ""
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows gravityKey
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place gravityKey (TweakVerb.Steps 3))
        File.ReadAllText sb.Tuning |> Expect.equal "10.1 is outside 0 to 10, so nothing was written" tuningText
        let! after = view sb [ "gravity", box 9.8 ]
        stateOf after gravityKey |> isRefused |> Expect.isTrue "the row says it was refused"
        // A refusal carries the expression the row was showing, so the next click is addressed to it.
        let again =
          match stateOf after gravityKey with
          | PersistenceState.Refused(_, shown) -> shown
          | other -> failtestf "expected a refusal, got %A" other
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor again gravityKey (TweakVerb.Steps 2))
        File.ReadAllText sb.Tuning |> Expect.stringContains "10.0 is the highest, and it lands" "let gravity = 10.0"
      }) }

    testTask "WHY — a step the person chose replaces the step the spelling gives" {
      do! withSandbox (fun sb -> task {
        choose sb gravityKey "" "" "0.25" |> Expect.equal "kept" ""
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows gravityKey
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place gravityKey (TweakVerb.Steps 1))
        File.ReadAllText sb.Tuning |> Expect.stringContains "a quarter up from 9.8" "let gravity = 10.05"
      }) }

    testTask "WHY — a row's choices are read back into its view, so the slider's ends are the ones that were saved" {
      do! withSandbox (fun sb -> task {
        choose sb gravityKey "0" "30" "0.1" |> Expect.equal "kept" ""
        let! rows = view sb [ "gravity", box 9.8 ]
        (TweakView.tryRow rows gravityKey).Value.Limits
        |> Expect.equal "the row carries what was chosen" (NumberLimits.ofNumbers (Edge.At 0.0) (Edge.At 30.0) (StepChoice.Every 0.1) |> Result.defaultValue NumberLimits.unlimited)
      }) }

    testTask "WHY — choosing a second row keeps the first, and clearing a row takes only its entry out of the file" {
      do! withSandbox (fun sb -> task {
        let health = RowKey.top "maxHealth"
        choose sb gravityKey "0" "30" "0.1" |> Expect.equal "first" ""
        choose sb health "1" "500" "5" |> Expect.equal "second" ""
        let both = File.ReadAllText(limitsFile sb)
        both |> Expect.stringContains "the first row is still there" "gravity"
        both |> Expect.stringContains "and the second" "maxHealth"
        choose sb gravityKey "" "" "" |> Expect.equal "cleared" ""
        let left = File.ReadAllText(limitsFile sb)
        left.Contains "gravity" |> Expect.isFalse "gravity's entry is gone"
        left |> Expect.stringContains "maxHealth's stays" "maxHealth"
      }) }

    testTask "WHY — a choice no number can satisfy is refused with the reason, and the file is not touched" {
      do! withSandbox (fun sb -> task {
        choose sb gravityKey "30" "0" "" |> Expect.stringContains "says which is above which" "above the highest"
        File.Exists(limitsFile sb) |> Expect.isFalse "nothing was written"
        choose sb gravityKey "abc" "" "" |> Expect.stringContains "names the field" "lowest"
      }) }

    testTask "WHY — a limits file that cannot be read stops numbers from stepping and is never overwritten, because it holds the person's other choices" {
      do! withSandbox (fun sb -> task {
        Directory.CreateDirectory(Path.GetDirectoryName(limitsFile sb)) |> ignore
        File.WriteAllText(limitsFile sb, "{ not json")
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows gravityKey
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place gravityKey (TweakVerb.Steps 1))
        File.ReadAllText sb.Tuning |> Expect.equal "no write while the choices cannot be read" tuningText
        choose sb gravityKey "0" "30" "" |> Expect.stringContains "says why" "tweak-limits.json"
        File.ReadAllText(limitsFile sb) |> Expect.equal "the broken file is left as it was" "{ not json"
      }) }

    testTask "WHY — an int stepped past what an int holds is refused by the door, and the file is byte-identical" {
      do! withSandbox (fun sb -> task {
        let health = RowKey.top "maxHealth"
        let! rows = view sb [ "maxHealth", box 100 ]
        let place = placeOf rows health
        do! Service.act sb.Service "s" sb.Owned noReload ignore (requestFor place health (TweakVerb.Steps 2147483600))
        File.ReadAllText sb.Tuning |> Expect.equal "2147483700 does not fit an int, so nothing was written" tuningText
      }) }

    testTask "WHY — an agent's nudge_value is held to the chosen range too, as a literal or as an expression, and a number inside it lands" {
      do! withSandbox (fun sb -> task {
        choose sb gravityKey "0" "30" "" |> Expect.equal "kept" ""
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows gravityKey
        let nudge (literal: string) (expression: string) : Task<string> =
          McpNudge.nudgeWith
            sb.Owned
            (McpNudge.productionPorts (Path.Combine(sb.Dir, "tweaks-journal")))
            McpNudge.locks
            { Action = "set"; File = place.File; Address = SourceRef.addressText place; Seen = place.Hash; Literal = literal; Expression = expression }
        let! tooHigh = nudge "31.0" ""
        tooHigh |> Expect.stringContains "refused as out of range" "ValueOutOfRange"
        File.ReadAllText sb.Tuning |> Expect.equal "nothing was written" tuningText
        let! asExpression = nudge "" "31.0"
        asExpression |> Expect.stringContains "the expression way is no way around" "ValueOutOfRange"
        File.ReadAllText sb.Tuning |> Expect.equal "still nothing written" tuningText
        let! inside = nudge "29.5" ""
        inside |> Expect.stringContains "a number inside lands" "Written"
        File.ReadAllText sb.Tuning |> Expect.stringContains "in the file" "let gravity = 29.5"
      }) }
  ]

/// What the session was asked to evaluate, and what it said: the stand-in for the worker the push route sends statements to.
let private sessionThat (answer: Result<string, string>) : ResizeArray<string> * (string -> Task<Result<string, string>>) =
  let asked = ResizeArray<string>()
  asked,
  fun code ->
    task {
      asked.Add code
      return answer
    }

[<Tests>]
let pushServiceTests =
  let gravityKey = RowKey.top "gravity"
  testList "trying a value in the session: the daemon's half" [

    testTask "a tried value is evaluated in the session, the file is untouched, and the row then reads tweaked with what was tried" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows gravityKey
        let asked, session = sessionThat (Ok "val gravity: float = 9.5")
        let changes = ref 0
        do! Service.push sb.Service "s" session (fun () -> changes.Value <- changes.Value + 1) gravityKey place PushVia.RebindBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Push "9.5"
        asked |> List.ofSeq |> Expect.equal "the statement, ready to evaluate" [ "let gravity = 9.5;;" ]
        File.ReadAllText sb.Tuning |> Expect.equal "the file is exactly as it was" tuningText
        changes.Value |> Expect.equal "the page was told" 1
        let! after = view sb [ "gravity", box 9.5 ]
        match stateOf after gravityKey with
        | PersistenceState.Tweaked(live, filePlace) ->
          live |> Expect.equal "what the session holds" "9.5"
          filePlace.Text |> Expect.equal "what the file still says" "9.8"
        | other -> failtestf "expected Tweaked, got %A" other
      }) }

    testTask "WHY — a session that refuses the statement leaves the row as it was: nothing is remembered as tried" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows gravityKey
        let asked, session = sessionThat (Error "error FS0001: this value is not mutable")
        do! Service.push sb.Service "s" session ignore gravityKey place PushVia.AssignBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Push "9.5"
        asked.Count |> Expect.equal "it was asked once" 1
        let! after = view sb [ "gravity", box 9.8 ]
        PersistenceState.token (stateOf after gravityKey) |> Expect.equal "still in step with the file" "InSource"
      }) }

    testTask "a typed value the literal cannot take is refused on the row with the door's rule, and the session is never asked" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows gravityKey
        let asked, session = sessionThat (Ok "")
        do! Service.push sb.Service "s" session ignore gravityKey place PushVia.RebindBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Push "fast"
        asked.Count |> Expect.equal "the session was not asked" 0
        let! after = view sb [ "gravity", box 9.8 ]
        match stateOf after gravityKey with
        | PersistenceState.Refused(NudgeRefusal.LiteralNotReadable(kind, given), _) ->
          kind |> Expect.equal "a real was wanted" LiteralKindName.Real
          given |> Expect.equal "what was typed" "fast"
        | other -> failtestf "expected the door's refusal, got %A" other
      }) }

    testTask "a revert puts the file's literal back and ends the tweak; a push that returns to the file's own value ends it too" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8 ]
        let place = placeOf rows gravityKey
        let _, session = sessionThat (Ok "ok")
        do! Service.push sb.Service "s" session ignore gravityKey place PushVia.RebindBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Push "9.5"
        Memory.pushOf (Service.memoryOf sb.Service "s") gravityKey |> Expect.equal "tried" (PushedValue.Pushed "9.5")
        let asked, reverting = sessionThat (Ok "ok")
        do! Service.push sb.Service "s" reverting ignore gravityKey place PushVia.RebindBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Revert ""
        asked |> List.ofSeq |> Expect.equal "the file's own text" [ "let gravity = 9.8;;" ]
        Memory.pushOf (Service.memoryOf sb.Service "s") gravityKey |> Expect.equal "nothing tried any more" PushedValue.NoPush
        do! Service.push sb.Service "s" session ignore gravityKey place PushVia.RebindBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Push "9.5"
        do! Service.push sb.Service "s" session ignore gravityKey place PushVia.RebindBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Push "9.8"
        Memory.pushOf (Service.memoryOf sb.Service "s") gravityKey |> Expect.equal "typing the file's value again is not a tweak" PushedValue.NoPush
      }) }

    testTask "WHY — several reverts are ONE evaluation: the dashboard reads the values after each eval without waiting, and two reads in a row can finish out of order" {
      do! withSandbox (fun sb -> task {
        let! rows = view sb [ "gravity", box 9.8; "maxHealth", box 100 ]
        let gravity = placeOf rows gravityKey
        let health = placeOf rows (RowKey.top "maxHealth")
        let _, session = sessionThat (Ok "ok")
        do! Service.push sb.Service "s" session ignore gravityKey gravity PushVia.RebindBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Push "9.5"
        do! Service.push sb.Service "s" session ignore (RowKey.top "maxHealth") health PushVia.RebindBinding SageFs.Server.LiveBindingsTweakView.PushVerb.Push "7"
        let asked, reverting = sessionThat (Ok "ok")
        do! Service.revertMany sb.Service "s" reverting ignore [ gravityKey, gravity, PushVia.RebindBinding; RowKey.top "maxHealth", health, PushVia.RebindBinding ]
        asked.Count |> Expect.equal "one evaluation" 1
        asked.[0] |> Expect.equal "both statements, in order, as one interaction" "let gravity = 9.8\nlet maxHealth = 100;;"
        let memory = Service.memoryOf sb.Service "s"
        Memory.pushOf memory gravityKey |> Expect.equal "gravity is no longer tried" PushedValue.NoPush
        Memory.pushOf memory (RowKey.top "maxHealth") |> Expect.equal "nor is maxHealth" PushedValue.NoPush
      }) }

    testTask "WHY — a file written while it is being read is read again next time, never served as current under its newer stamp: the row would say the file holds a value it no longer does" {
      let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-tweak-%s" (Guid.NewGuid().ToString("N")))
      Directory.CreateDirectory dir |> ignore
      try
        let tuning = Path.Combine(dir, "Tuning.fs")
        File.WriteAllText(tuning, tuningText)
        let writes = ref 0
        let armed = ref false
        // A write that lands right after each read of the file, the way the knob's own write lands while the page is still reading the
        // row it just started. Every text is as long as the one before, so only the stamp can tell them apart.
        let rewrite () =
          writes.Value <- writes.Value + 1
          File.WriteAllText(tuning, tuningText.Replace("let gravity = 9.8", sprintf "let gravity = 9.%d" (writes.Value % 10)))
        let ports = McpNudge.productionPorts (Path.Combine(dir, "tweaks-journal"))
        let readThenWrite (path: string) =
          let answer = ports.Files.ReadBytes path
          match armed.Value && path = tuning with
          | true -> rewrite ()
          | false -> ()
          answer
        let env : Env =
          { Ports = { ports with Files = { ports.Files with ReadBytes = readThenWrite } }
            Locks = FileLocks()
            Stamp =
              fun path ->
                match path = tuning with
                | true -> FileStamp.Stamped(int64 (File.ReadAllText path).Length, int64 writes.Value)
                | false -> FileStamp.Unstamped
            Now = fun () -> 0L }
        let service = Service.create env
        let owned = Service.ownedOf "s" dir [ tuning, HotReloadWatch.Watched ]
        let look () = Service.viewFor service "s" owned (walked [ "gravity", box 9.8 ]) SessionReload.NoReloadYet
        let! _ = look ()
        armed.Value <- true
        rewrite ()
        let! _ = look ()
        armed.Value <- false
        let! settled = look ()
        let onDisk = Text.RegularExpressions.Regex.Match(File.ReadAllText tuning, "let gravity = ([0-9.]+)").Groups.[1].Value
        match stateOf settled (RowKey.top "gravity") with
        | PersistenceState.DiffersFromFile(_, place)
        | PersistenceState.InSource place -> place.Text |> Expect.equal "the row says what the file holds now" onDisk
        | other -> failtestf "gravity: %A" other
      finally
        try Directory.Delete(dir, true) with _ -> ()
    }

    testTask "a binding the file declares `let mutable`, changed by the program, reads evolved from the real file; the same change to a plain `let` does not" {
      do! withSandbox (fun sb -> task {
        File.WriteAllText(sb.Tuning, tuningText + "\nlet mutable counter = 5\n")
        let! inStep = view sb [ "gravity", box 9.8; "counter", box 5 ]
        PersistenceState.token (stateOf inStep (RowKey.top "counter")) |> Expect.equal "in step first" "InSource"
        let! moved = view sb [ "gravity", box 9.8; "counter", box 7 ]
        match stateOf moved (RowKey.top "counter") with
        | PersistenceState.Evolved(live, EvolvedBy.MovedWhileRunning, _) -> live |> Expect.equal "what the program holds" "7"
        | other -> failtestf "expected Evolved, got %A" other
        (TweakView.tryRow moved (RowKey.top "counter")).Value.Via |> Expect.equal "assigned, because the file says mutable" PushVia.AssignBinding
        let! plain = view sb [ "gravity", box 12.0 ]
        PersistenceState.token (stateOf plain gravityKey) |> Expect.equal "a plain let is only a difference" "DiffersFromFile"
      }) }
  ]
