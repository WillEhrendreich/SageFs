/// Following a call into its initializer (docs/plans/live-bindings-watch-view.md, phase 3, step 6). A live binding built by a call
/// (`let player = initPlayer ()`) has parts its own file does not spell, and the literal that gives each part its value sits in the
/// callee. The link is the NAME, matched in the session's own files, and it never guesses: a callee found once is followed, and a
/// callee declared twice, in a file nobody could read, or built from something other than a literal is refused with its own reason.
module SageFs.Tests.CallResultTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Features.Tweak.CallFollow
open SageFs.Tests.BindingTweakTests
open SageFs.Tests.BindingTweakRowsTests

/// A callee found, and three callers of it.
let private oneCallee =
  "module Game.Tuning\n\ntype Player = { Health: int; Speed: float; Name: string }\n\nlet initPlayer () = { Health = 100; Speed = 4.5; Name = \"Ada\" }\n\nlet player = initPlayer ()\nlet spare = initPlayer ()\nlet third = initPlayer ()\n"

/// A callee whose Health comes from its parameter, and whose Speed is a literal.
let private parameterised =
  "module Game.Tuning\n\ntype Player = { Health: int; Speed: float }\n\nlet make hp = { Health = hp; Speed = 4.5 }\n\nlet boss = make 50\n"

/// A second file that declares `initPlayer` in a module of the same name.
let private otherFile =
  "module Game.Tuning\n\nlet initPlayer () = { Health = 7; Speed = 1.5; Name = \"Bo\" }\n"

let private wholeOf (file: string) (text: string) (name: string) : SourceRef list =
  inspectedWith file text (InspectTarget.OneAddress(addressOf name []))

/// The session's index of one file: the door's listing, and the whole right-hand side of each name in `wholes`, with the calls
/// the file makes counted.
let private fileIndex (file: string) (text: string) (wholes: string list) : FileInspection * ApplicationCounts =
  let items = inspectedWith file text InspectTarget.WholeFile
  { fileOf file items with Wholes = wholes |> List.collect (wholeOf file text) }, CallFollow.applicationsIn text

let private indexOver (files: (FileInspection * ApplicationCounts) list) : SourceIndex =
  { Files = files |> List.map fst
    Unreadable = []
    Applied = CallFollow.combineCounts (files |> List.map (fun (file, counts) -> file.File, counts)) }

let private ownerOf (index: SourceIndex) (binding: string) : SourceRef =
  match SourceIndex.declarations index binding |> List.collect (fun d -> d.Owners) with
  | [ owner ] -> owner
  | other -> failtestf "expected one owner of %s, got %d" binding other.Length

let private health = [ PathStep.RecordField "Health" ]

[<Tests>]
let followTests =
  testList "following a call into its initializer" [

    testCase "WHY — a part of a value built by a call is followed to the literal in the callee, and the callers of that callee are counted" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player"; "initPlayer" ] ]
      match CallFollow.follow index (ownerOf index "player") health with
      | Outcome.Followed followed ->
        followed.Callee |> Expect.equal "the callee's name" "initPlayer"
        followed.Place.Text |> Expect.equal "the literal in the callee" "100"
        followed.Place.Address.BindingName |> Expect.equal "in the callee's binding" "initPlayer"
        followed.Place.Address.Path |> Expect.equal "at the same part" health
        followed.Callers |> Expect.equal "three calls in the files, the row's own among them" (CallerCount.Counted 3)
      | other -> failtestf "expected the call to be followed, got %A" other

    testCase "WHY — the write through a followed call changes exactly one literal, in the callee, and every caller's text is untouched" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player"; "initPlayer" ] ]
      match CallFollow.follow index (ownerOf index "player") health with
      | Outcome.Followed followed ->
        let written = setLiteral oneCallee followed.Place.Address (LiteralValue.Integer 62L) |> Expect.wantOk "the literal is set"
        written |> Expect.equal "one literal differs" (oneCallee.Replace("Health = 100", "Health = 62"))
        (written.Split('\n') |> Array.filter (fun line -> line.Contains "= initPlayer ()") |> Array.length) |> Expect.equal "the three callers read as before" 3
      | other -> failtestf "expected the call to be followed, got %A" other

    testCase "WHY — a callee declared in two files is refused with both places named, and neither is picked" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player"; "initPlayer" ]; fileIndex "Other.fs" otherFile [ "initPlayer" ] ]
      match CallFollow.follow index (ownerOf { index with Files = [ index.Files.Head ] } "player") health with
      | Outcome.Refused(Refusal.CalleeDeclaredSeveralTimes(name, places)) ->
        name |> Expect.equal "the name" "initPlayer"
        places |> List.map (fun p -> p.File) |> List.sort |> Expect.equal "both files are named" [ "Other.fs"; "State.fs" ]
      | other -> failtestf "expected a refusal naming both declarations, got %A" other

    testCase "WHY — a callee in a file nobody could read is not 'no callee': the refusal says the files are unknown, and why" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player" ] ]
      let index = { index with Unreadable = [ "Other.fs", "the file does not parse" ] }
      let callsMissing = { ownerOf index "player" with Text = "missing ()" }
      match CallFollow.follow index callsMissing health with
      | Outcome.Refused(Refusal.CalleeFilesUnknown reason) -> reason |> Expect.stringContains "the reason is the file's own" "the file does not parse"
      | other -> failtestf "expected files unknown, got %A" other

    testCase "WHY — a callee no file declares is refused as absent, which is a different answer from unreadable" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player" ] ]
      let callsMissing = { ownerOf index "player" with Text = "missing ()" }
      match CallFollow.follow index callsMissing health with
      | Outcome.Refused(Refusal.NoCalleeInFiles name) -> name |> Expect.equal "the name that was looked for" "missing"
      | other -> failtestf "expected no callee in the files, got %A" other

    testCase "WHY — a callee a file declares but whose body was not read is refused as not read, not as absent" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player" ] ]
      match CallFollow.follow index (ownerOf index "player") health with
      | Outcome.Refused(Refusal.CalleeNotRead(name, file)) ->
        name |> Expect.equal "the callee" "initPlayer"
        file |> Expect.equal "the file that declares it" "State.fs"
      | other -> failtestf "expected not read, got %A" other

    testCase "WHY — a qualified call is followed only to a module the qualifier names, so Tuning.make does not reach a make in Other" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player"; "initPlayer" ] ]
      let qualified (text: string) = CallFollow.follow index { ownerOf index "player" with Text = text } health
      match qualified "Tuning.initPlayer ()" with
      | Outcome.Followed followed -> followed.Place.Text |> Expect.equal "the module's end names it" "100"
      | other -> failtestf "expected the qualified call to be followed, got %A" other
      match qualified "Elsewhere.initPlayer ()" with
      | Outcome.Refused(Refusal.NoCalleeInFiles _) -> ()
      | other -> failtestf "expected a module that is not there to find nothing, got %A" other

    testCase "WHY — a part the callee builds from its parameter is refused, while a literal beside it in the same callee is followed" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" parameterised [ "boss"; "make" ] ]
      match CallFollow.follow index (ownerOf index "boss") health with
      | Outcome.Refused(Refusal.ResultIsAnExpression expression) ->
        expression.Text |> Expect.equal "the refusal names the expression the callee spells" "hp"
        expression.Address.BindingName |> Expect.equal "in the callee" "make"
      | other -> failtestf "expected the parameter-built part to be refused, got %A" other
      match CallFollow.follow index (ownerOf index "boss") [ PathStep.RecordField "Speed" ] with
      | Outcome.Followed followed -> followed.Place.Text |> Expect.equal "the literal that does not depend on the parameter" "4.5"
      | other -> failtestf "expected the literal part to be followed, got %A" other

    testCase "WHY — a right-hand side that is not a call of a plain name is not followed, because there is no callee to name" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "initPlayer" ] ]
      let notACall = { ownerOf index "initPlayer" with Text = "{ Health = 1 }" }
      CallFollow.follow index notACall health |> Expect.equal "a record is not a call" (Outcome.Refused Refusal.NotAPlainCall)
      let infix = { notACall with Text = "x |> initPlayer" }
      CallFollow.follow index infix health |> Expect.equal "an operator is not a plain call" (Outcome.Refused Refusal.NotAPlainCall)

    testCase "WHY — calls that cannot be counted are said so, and never counted as none" <| fun _ ->
      let index = { indexOver [ fileIndex "State.fs" oneCallee [ "player"; "initPlayer" ] ] with Applied = ApplicationCounts.NotCounted "Other.fs does not parse" }
      match CallFollow.follow index (ownerOf index "player") health with
      | Outcome.Followed followed -> followed.Callers |> Expect.equal "the reason, not zero" (CallerCount.NotCounted "Other.fs does not parse")
      | other -> failtestf "expected the call to be followed, got %A" other

    testCase "WHY — the callers counted are calls: nested in a lambda, qualified by module, or curried count once each, and a name passed as a value does not" <| fun _ ->
      let text =
        "module Game.Tuning\n\nlet make a b = (a, b)\n\nlet one = make 1 2\nlet two = [ make 3 4; (fun () -> make 5 6) () ]\nlet three = Game.Tuning.make 7 8\nlet value = List.map make [ 1 ]\n"
      match CallFollow.applicationsIn text with
      | ApplicationCounts.Counted counts ->
        counts |> Map.tryFind "make" |> Expect.equal "plain calls, the lambda's included, once each" (Some 3)
        counts |> Map.tryFind "Game.Tuning.make" |> Expect.equal "the qualified call under its own spelling" (Some 1)
      | other -> failtestf "expected a count, got %A" other

    testCase "a file that does not parse is not counted, and says so" <| fun _ ->
      match CallFollow.applicationsIn "module M\nlet x = (((\n" with
      | ApplicationCounts.NotCounted reason -> reason |> Expect.isNotEmpty "a reason"
      | other -> failtestf "expected NotCounted, got %A" other
  ]

/// Public on purpose: the live-values walk reads a record through reflection and takes a non-public one for an opaque class.
type PlayerRecord = { Health: int; Speed: float; Name: string }

[<Tests>]
let readingTests =
  let declared (text: string) =
    match TweakAddress.declaredBindings text with
    | Ok found -> found
    | Error why -> failtestf "declaredBindings failed: %A" why
  testList "reading what a call needs from the files" [

    testCase "WHY — every let in a file is listed with where it is and how its right-hand side is written, whether or not the door lists a point in it" <| fun _ ->
      let found = declared oneCallee |> List.map (fun d -> d.Name, d.Rhs)
      found |> Expect.contains "a call-built binding, which the door lists no point of" ("player", "initPlayer ()")
      found |> Expect.contains "the callee, whose right-hand side is a record" ("initPlayer", "{ Health = 100; Speed = 4.5; Name = \"Ada\" }")

    testCase "WHY — only a binding asked about, built by a call of a name some owned file declares, has its whole right-hand side read" <| fun _ ->
      let wanted (names: string list) (known: string list) =
        CallFollow.callBuiltToRead (Set.ofList names) (Set.ofList known) (declared oneCallee) |> List.map (fun a -> a.BindingName)
      wanted [ "player"; "spare" ] [ "initPlayer" ] |> Expect.equal "the two call-built bindings" [ "player"; "spare" ]
      wanted [ "player" ] [ "initPlayer" ] |> Expect.equal "only the one asked about" [ "player" ]
      wanted [ "player" ] [] |> Expect.isEmpty "a callee no owned file declares is not read for"
      wanted [ "initPlayer" ] [ "initPlayer" ] |> Expect.isEmpty "a record is not a call"

    testCase "WHY — the callees to look for are the names the call-built wholes apply, without their module" <| fun _ ->
      let wholes = [ { sourceAt "a.fs" "x" "Game.Tuning.make 1 2" ItemKind.Formula with Address = addressOf "x" [] }; sourceAt "a.fs" "y" "{ A = 1 }" ItemKind.Formula ]
      CallFollow.calleeNamesOf wholes |> Expect.equal "make, and nothing for a record" (Set.ofList [ "make" ])

    testCase "WHY — counts over several files are added by name, and one file that could not be counted makes the total not counted and names the file" <| fun _ ->
      let counted (pairs: (string * int) list) = ApplicationCounts.Counted(Map.ofList pairs)
      CallFollow.combineCounts [ "a.fs", counted [ "make", 2 ]; "b.fs", counted [ "make", 1; "other", 4 ] ]
      |> Expect.equal "summed per name" (counted [ "make", 3; "other", 4 ])
      match CallFollow.combineCounts [ "a.fs", counted [ "make", 2 ]; "b.fs", ApplicationCounts.NotCounted "the file does not parse" ] with
      | ApplicationCounts.NotCounted reason -> reason |> Expect.stringContains "the file is named" "b.fs"
      | other -> failtestf "a total over the readable files is not the number of callers, got %A" other
      CallFollow.combineCounts [] |> Expect.equal "no files, nothing called" (counted [])
  ]

let private rowFor (index: SourceIndex) (binding: string) (value: obj) (field: string) : RowView =
  let view = buildRows index (walked [ binding, value ]) Memory.empty
  let key = RowKey.field (RowKey.top binding) field
  match TweakView.tryRow view key with
  | Some row -> row
  | None -> failtestf "no row for %s.%s among %A" binding field (view.Rows |> Map.toList |> List.map fst)

[<Tests>]
let rowTests =
  let player (health: int) : obj = box { Health = health; Speed = 4.5; Name = "Ada" }
  testList "the pane's rows follow a call into its initializer" [

    testCase "WHY — a field of a value built by a call has the callee's literal as its source, so the knob writes where the value comes from" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player"; "initPlayer" ] ]
      let row = rowFor index "player" (player 100) "Health"
      match row.Place with
      | PlaceOf.Placed place ->
        place.Address.BindingName |> Expect.equal "the callee's binding is the target" "initPlayer"
        place.Text |> Expect.equal "the literal" "100"
      | PlaceOf.NotPlaced -> failtest "expected the row to be placed in the callee"
      match row.Through with
      | ThroughCall.Through followed -> followed.Callers |> Expect.equal "the three calls" (CallerCount.Counted 3)
      | other -> failtestf "expected Through, got %A" other
      match row.State with
      | PersistenceState.InSource _ -> ()
      | other -> failtestf "live equals the literal, so the row is in source, got %A" other

    testCase "WHY — a live value that is not the callee's literal says it differs, and never that the program changed it, because nothing says which came last" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player"; "initPlayer" ] ]
      match (rowFor index "player" (player 62) "Health").State with
      | PersistenceState.DiffersFromFile(live, place) ->
        live |> Expect.equal "the running value" "62"
        place.Text |> Expect.equal "the callee's literal" "100"
      | other -> failtestf "expected differs from the file, got %A" other

    testCase "WHY — a part the callee spells as an expression keeps the refusal and the expression's text, so the row can say what it is" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" parameterised [ "boss"; "make" ] ]
      let row = rowFor index "boss" (box { Health = 50; Speed = 4.5; Name = "" }) "Health"
      match row.Through with
      | ThroughCall.CouldNotFollow(Refusal.ResultIsAnExpression expression) -> expression.Text |> Expect.equal "the expression" "hp"
      | other -> failtestf "expected the refusal, got %A" other
      match row.State with
      | PersistenceState.NotInAFile(NotInFileWhy.NotSpelledInTheFile _) -> ()
      | other -> failtestf "no literal to write, got %A" other
      row.Control |> Expect.equal "so no knob" Control.NoControl

    testCase "WHY — a write through a followed call is remembered against the row that was clicked, so the reload verdict shows on that row and not on the callee's address" <| fun _ ->
      let index = indexOver [ fileIndex "State.fs" oneCallee [ "player"; "initPlayer" ] ]
      let key = RowKey.field (RowKey.top "player") "Health"
      let place =
        match (rowFor index "player" (player 100) "Health").Place with
        | PlaceOf.Placed place -> place
        | PlaceOf.NotPlaced -> failtest "expected the row to be placed in the callee"
      let ran = ranOf (NudgeOutcome.Written(receiptFor place.Address))
      let memory = Memory.finished key place ran SessionReload.NoReloadYet 5L Memory.empty
      memory.Writes |> Map.containsKey key |> Expect.isTrue "the clicked row holds the write"
      memory.Writes |> Map.containsKey (RowKey.top "initPlayer") |> Expect.isFalse "the callee's name is not a row of the pane"

    testCase "a record the file spells itself is not a call, and its parts say nothing about one" <| fun _ ->
      let text = "module Game.Tuning\n\nlet gravity = 9.8\n"
      let index = indexOver [ fileIndex "T.fs" text [ "gravity" ] ]
      let view = buildRows index (walked [ "gravity", box 9.8 ]) Memory.empty
      match TweakView.tryRow view (RowKey.top "gravity") with
      | Some row -> row.Through |> Expect.equal "no call" ThroughCall.NoCall
      | None -> failtest "no row for gravity"
  ]
