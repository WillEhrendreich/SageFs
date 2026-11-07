/// The expression rows of a watch tab: what an expression is (one line the F# parser accepts as a single expression, so an illegal one
/// cannot be stored), what evaluating it needs (names and fields only, or code), what each walk mode does about that, how it is read
/// from the values the session already holds, and how a tab keeps its list as a setting.
module SageFs.Tests.WatchExpressionTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.LiveValueTree
open SageFs.Features.Tweak.WatchRows

let private parsed (text: string) : WatchExpression =
  match WatchExpression.parse text with
  | Ok e -> e
  | Error why -> failtestf "'%s' should be an expression: %s" text (ExpressionError.describe why)

let private node (label: string) (preview: string) (children: LiveValueNode list) : LiveValueNode =
  { Label = label; TypeName = "t"; Preview = preview; Kind = (match children with [] -> NodeKind.Leaf | _ -> NodeKind.Record)
    Children = children; BestEffort = false; Assign = Assignment.NotKnown; Depth = 0 }

let private snapshot (bindings: (string * LiveValueNode) list) : LiveValueSnapshot =
  { SessionId = "s"; Generation = 1L; Truncated = false; CapturedAt = DateTimeOffset.UnixEpoch
    Bindings = bindings |> List.map (fun (name, root) -> { Name = name; TypeSignature = "t"; Root = root }) }

/// Names, dotted names and a few operators, the shapes a watch row is likely to be.
let private identifier : Gen<string> =
  gen {
    let! head = Gen.elements [ 'a' .. 'z' ]
    let! tail = Gen.listOfLength 4 (Gen.elements ([ 'a' .. 'z' ] @ [ '0' .. '9' ]))
    return String(Array.ofList (head :: tail))
  }

[<Tests>]
let tests =
  testList "the expression rows of a watch tab" [

    testCase "an expression that does not parse cannot be constructed, and the refusal says why" <| fun _ ->
      for text in [ "player.("; "f (1 +"; "[1; 2"; "if x then"; "fun x ->"; "let x = 1 in" ] do
        match WatchExpression.parse text with
        | Error (ExpressionError.DoesNotParse reason) -> reason |> Expect.isNotEmpty (sprintf "'%s' says why" text)
        | other -> failtestf "'%s' should not parse, got %A" text other

    testCase "an empty text and a text of several lines are refused as such" <| fun _ ->
      WatchExpression.parse "" |> Expect.equal "empty" (Error ExpressionError.Empty)
      WatchExpression.parse "   \t " |> Expect.equal "blank" (Error ExpressionError.Empty)
      WatchExpression.parse "a +\n b" |> Expect.equal "two lines" (Error ExpressionError.SeveralLines)

    testProperty "WHY — a dotted name built from identifiers is an expression that keeps its text, and only names are needed to read it" <| fun () ->
      Prop.forAll (Arb.fromGen (Gen.listOfLength 3 identifier)) (fun names ->
        let text = String.Join(".", names)
        // a keyword is not a name: the parser refuses it, and then there is nothing to read
        match WatchExpression.parse text with
        | Error _ -> true
        | Ok e -> WatchExpression.text e = text && (match WatchExpression.reach e with ExpressionReach.NamesOnly path -> path = names | ExpressionReach.RunsCode _ -> false))

    testCase "names, fields and parentheses are read without running code; a call, an operator and a literal run it, and say which" <| fun _ ->
      for text, path in [ "gravity", [ "gravity" ]; "player.Lives", [ "player"; "Lives" ]; "(player.Lives)", [ "player"; "Lives" ]; "Tuning.gravity", [ "Tuning"; "gravity" ]; "(player).Name", [ "player"; "Name" ] ] do
        WatchExpression.reach (parsed text) |> Expect.equal (sprintf "'%s' is names only" text) (ExpressionReach.NamesOnly path)
      for text in [ "normalize \"  Foo \""; "gravity * 2.0"; "List.length items"; "fun x -> x"; "42"; "player.Name.ToUpper()" ] do
        match WatchExpression.reach (parsed text) with
        | ExpressionReach.RunsCode why -> why |> Expect.isNotEmpty (sprintf "'%s' says why it runs code" text)
        | ExpressionReach.NamesOnly p -> failtestf "'%s' read as names only %A" text p

    testProperty "WHY — a stored list is one expression to a line and reads back whole, in order" <| fun (seed: int) ->
      let pool = [ "gravity"; "player.Lives"; "normalize \"  Foo \""; "a + b"; "(x)"; "items |> List.length" ]
      let chosen = pool |> List.indexed |> List.filter (fun (i, _) -> (seed >>> i) % 2 = 0) |> List.map snd
      let expressions = chosen |> List.map parsed
      let back, bad = WatchExpression.parseLines (WatchExpression.render expressions)
      (back |> List.map WatchExpression.text) = chosen && List.isEmpty bad

    testCase "a stored list edited by hand keeps the lines that are expressions and names the ones that are not" <| fun _ ->
      let back, bad = WatchExpression.parseLines "gravity\n\nplayer.(\n  player.Lives  \n"
      back |> List.map WatchExpression.text |> Expect.equal "the legal lines, trimmed" [ "gravity"; "player.Lives" ]
      bad |> List.map fst |> Expect.equal "the illegal one" [ "player.(" ]

    testCase "WHY — in Safe mode an expression that runs code is held and never runs by itself; only names and fields are read" <| fun _ ->
      for mode in [ WalkMode.Safe; WalkMode.Off ] do
        match decide mode (ExpressionReach.RunsCode "it calls a function") with
        | Reading.HeldUntilClicked why -> why |> Expect.stringContains "says why" "calls a function"
        | other -> failtestf "%A runs code by itself: %A" mode other
      for mode in [ WalkMode.Safe; WalkMode.Off; WalkMode.Everything ] do
        decide mode (ExpressionReach.NamesOnly [ "a"; "b" ]) |> Expect.equal (sprintf "%A reads names" mode) (Reading.ReadFromWalk [ "a"; "b" ])
      match decide WalkMode.Everything (ExpressionReach.RunsCode "it calls a function") with
      | Reading.RunsOnItsOwn _ -> ()
      | other -> failtestf "Everything holds a call: %A" other

    testCase "a path is read from the walk: a binding, then a field of it; and what is missing is named" <| fun _ ->
      let walk = snapshot [ "player", node "player" "{ Lives = 3 }" [ node "Lives" "3" []; node "Name" "\"Ann\"" [] ]; "gravity", node "gravity" "9.8" [] ]
      match readPath walk [ "player"; "Lives" ] with
      | WalkRead.Found n -> n.Preview |> Expect.equal "the field's value" "3"
      | other -> failtestf "expected the field: %A" other
      match readPath walk [ "gravity" ] with
      | WalkRead.Found n -> n.Preview |> Expect.equal "the binding's value" "9.8"
      | other -> failtestf "expected the binding: %A" other
      readPath walk [ "speed" ] |> Expect.equal "no such binding" (WalkRead.NoBinding "speed")
      readPath walk [ "player"; "Score" ] |> Expect.equal "no such field" (WalkRead.NoMember("Score", "player"))
      readPath walk [] |> Expect.equal "an empty path names nothing" (WalkRead.NoBinding "")

    testCase "a held getter in the path is read as the held row it is, not as a value" <| fun _ ->
      let held = { node "Area" "…" [] with Kind = NodeKind.NotEvaluated NotEvaluatedReason.GetterRunsCode }
      let walk = snapshot [ "shape", node "shape" "Circle" [ held ] ]
      match readPath walk [ "shape"; "Area" ] with
      | WalkRead.Found n -> (match n.Kind with NodeKind.NotEvaluated _ -> () | k -> failtestf "kind %A" k)
      | other -> failtestf "expected the held row: %A" other

    testCase "WHY — what a row says: a name or field reads from the walk, code waits for a click, and a click's answer or failure is shown as it came" <| fun _ ->
      let walk = snapshot [ "player", node "player" "{ Lives = 3 }" [ node "Lives" "3" [] ]; "gravity", node "gravity" "9.8" [] ]
      let say mode lastRun text = answer mode (Some walk) lastRun (parsed text)
      say WalkMode.Safe None "player.Lives" |> Expect.equal "a field" (Answer.Value("3", "t"))
      say WalkMode.Safe None "gravity" |> Expect.equal "a binding" (Answer.Value("9.8", "t"))
      say WalkMode.Safe None "speed" |> Expect.equal "no such binding" (Answer.NotFound "no value called speed is bound in this session")
      say WalkMode.Safe None "player.Score" |> Expect.equal "no such field" (Answer.NotFound "player has no member Score")
      // code is held in Safe mode, and a name's read never needs a click
      say WalkMode.Safe None "gravity * 2.0" |> Expect.equal "held" (Answer.Held "it uses an operator")
      say WalkMode.Safe (Some (RunOutcome.RanOk "val it: float = 19.6")) "gravity * 2.0" |> Expect.equal "the click's answer" (Answer.Ran "val it: float = 19.6")
      say WalkMode.Safe (Some (RunOutcome.RanFailed "boom")) "gravity * 2.0" |> Expect.equal "the click's failure" (Answer.RunFailed "boom")
      // a name is read from the walk even when an old run is remembered for the same text
      say WalkMode.Safe (Some (RunOutcome.RanOk "stale")) "gravity" |> Expect.equal "the walk wins" (Answer.Value("9.8", "t"))
      answer WalkMode.Safe None None (parsed "gravity") |> Expect.equal "no values yet" (Answer.NotFound "this session has no values yet")

    testCase "a held getter reached by name is held, not shown as a value" <| fun _ ->
      let held = { node "Area" "not read: it runs code" [] with Kind = NodeKind.NotEvaluated NotEvaluatedReason.GetterRunsCode }
      let walk = snapshot [ "shape", node "shape" "Circle" [ held ] ]
      match answer WalkMode.Safe (Some walk) None (parsed "shape.Area") with
      | Answer.Held _ -> ()
      | other -> failtestf "expected held: %A" other

    testCase "the setting for a tab's expressions stores them, parses what it stored, and refuses a list with an illegal line" <| fun _ ->
      let descriptor = WatchSettings.descriptor
      let expressions = [ parsed "gravity"; parsed "normalize \"  Foo \"" ]
      let stored = descriptor.Render (WatchSettings.valueOf expressions)
      descriptor.Parse stored |> Result.map WatchSettings.expressionsOf |> Expect.equal "round trip" (Ok expressions)
      descriptor.Parse "gravity\nplayer.(" |> Expect.isError "an illegal line cannot be stored"
      descriptor.Parse "" |> Result.map WatchSettings.expressionsOf |> Expect.equal "an empty list is a list" (Ok [])
      WatchSettings.expressionsOf descriptor.Default |> Expect.equal "nothing is watched until you add something" []

    testCase "WHY — the route stores an add only when the text is an expression, refuses a remove of what is not listed, and says why in words" <| fun _ ->
      let current = [ parsed "gravity"; parsed "player.Lives" ]
      let verbs = SageFs.Server.LiveBindingsWatchRoute.WatchVerb.Add, SageFs.Server.LiveBindingsWatchRoute.WatchVerb.Remove
      let add, remove = verbs
      match SageFs.Server.LiveBindingsWatchRoute.changeList add "speed * 2" current with
      | SageFs.Server.LiveBindingsWatchRoute.ListChange.Store next -> next |> List.map WatchExpression.text |> Expect.equal "added at the end" [ "gravity"; "player.Lives"; "speed * 2" ]
      | other -> failtestf "expected a store: %A" other
      match SageFs.Server.LiveBindingsWatchRoute.changeList add "player.(" current with
      | SageFs.Server.LiveBindingsWatchRoute.ListChange.Refuse words -> words |> Expect.stringContains "says it does not parse" "does not parse"
      | other -> failtestf "expected a refusal: %A" other
      match SageFs.Server.LiveBindingsWatchRoute.changeList remove "speed" current with
      | SageFs.Server.LiveBindingsWatchRoute.ListChange.Refuse words -> words |> Expect.stringContains "names it" "speed"
      | other -> failtestf "expected a refusal: %A" other
      match SageFs.Server.LiveBindingsWatchRoute.changeList remove "gravity" current with
      | SageFs.Server.LiveBindingsWatchRoute.ListChange.Store next -> next |> List.map WatchExpression.text |> Expect.equal "removed" [ "player.Lives" ]
      | other -> failtestf "expected a store: %A" other

    testCase "adding an expression that is already listed does not list it twice, and removing one leaves the others in order" <| fun _ ->
      let a, b, c = parsed "a", parsed "b", parsed "c"
      WatchSettings.add a [ a; b ] |> Expect.equal "no duplicate" [ a; b ]
      WatchSettings.add c [ a; b ] |> Expect.equal "added at the end" [ a; b; c ]
      WatchSettings.remove b [ a; b; c ] |> Expect.equal "removed" [ a; c ]
      WatchSettings.remove c [ a ] |> Expect.equal "absent is a no-op" [ a ]
  ]
