/// The nudge door, behavior first: what a caller asks, what the file and the
/// journal hold afterwards, and what is refused with which reason. The door runs
/// its real code against an in-memory disk, so each refusal and each crash is a
/// state a test sets up and a value a test reads.
module SageFs.Tests.NudgeTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open SageFs.Tests.SharedGenerators
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.TweakLog
open SageFs.Features.Tweak.NudgeAddress
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.Nudge
open SageFs.Simulation
open SageFs.Simulation.NudgeDisk
open SageFs.Simulation.NudgeWorld

// ── helpers ──

let ran (label: string) (result: Result<Ran, NudgeRefusal>) : Ran =
  match result with
  | Ok r -> r
  | Error refusal -> failtestf "%s: expected the call to run, got the refusal %s" label (NudgeRefusal.token refusal)

let refused (label: string) (result: Result<Ran, NudgeRefusal>) : NudgeRefusal =
  match result with
  | Error refusal -> refusal
  | Ok r -> failtestf "%s: expected a refusal, got %A" label r.Outcome

let written (label: string) (result: Result<Ran, NudgeRefusal>) : Receipt =
  match (ran label result).Outcome with
  | NudgeOutcome.Written receipt -> receipt
  | other -> failtestf "%s: expected Written, got %A" label other

let undone (label: string) (result: Result<Ran, NudgeRefusal>) : Receipt =
  match (ran label result).Outcome with
  | NudgeOutcome.Undone receipt -> receipt
  | other -> failtestf "%s: expected Undone, got %A" label other

let redone (label: string) (result: Result<Ran, NudgeRefusal>) : Receipt =
  match (ran label result).Outcome with
  | NudgeOutcome.Redone receipt -> receipt
  | other -> failtestf "%s: expected Redone, got %A" label other

let inspection (label: string) (result: Result<Ran, NudgeRefusal>) : Inspection =
  match (ran label result).Outcome with
  | NudgeOutcome.Inspected inspected -> inspected
  | other -> failtestf "%s: expected Inspected, got %A" label other

let journalOf (world: World) : EventLog =
  match Journal.load world.Disk.Steps sourcePath (journalPath world) with
  | Ok loaded -> loaded.Log
  | Error fault -> failtestf "the journal does not load: %A" fault

let tokensOf (refusal: NudgeRefusal) = NudgeRefusal.token refusal

/// The mutating steps a clean `Set` takes on a fresh world, so a test can aim a fault at "the file replace"
/// without hardcoding how many steps the journal takes before it.
let stepsOfACleanSet () : int =
  let world = create ()
  world.Disk.Arm []
  setLiteral world gravity "9.8" "12.5" |> written "clean set" |> ignore
  world.Disk.StepsTaken

let diesIn (work: unit -> unit) : bool =
  try
    work ()
    false
  with SimulatedCrash _ -> true

let raw (action: string) (file: string) (address: string) (seen: string) (literal: string) (expression: string) : RawNudge =
  { Action = action; File = file; Address = address; Seen = seen; Literal = literal; Expression = expression }

let kindOf (disk: Disk) = disk.Steps.KindOf

[<Tests>]
let ownershipTests =
  testList "Nudge ownership" [
    testCase "WHY - a watched file is owned, whether it is named by its full path or relative to the working directory" <| fun _ ->
      let world = create ()
      Ownership.tryOwn owned (kindOf world.Disk) sourcePath |> Expect.isOk "full path"
      Ownership.tryOwn owned (kindOf world.Disk) "src/Tuning.fs" |> Expect.isOk "relative to the session's working directory"
      match Ownership.tryOwn owned (kindOf world.Disk) "src/Tuning.fs" with
      | Ok file -> Ownership.pathOf file |> Expect.equal "the owned file carries the full path" sourcePath
      | Error why -> failtestf "%A" why

    testCase "WHY - a file the session does not watch is refused, even in the same directory" <| fun _ ->
      let world = create ()
      world.Disk.PutText(otherPath, "module Other\nlet y = 1\n")
      Ownership.tryOwn owned (kindOf world.Disk) otherPath |> Expect.equal "not watched" (Error NotOwnedWhy.NotAmongProjectFiles)

    testCase "WHY - a path that only starts like a watched file is not that file" <| fun _ ->
      let world = create ()
      world.Disk.PutText(sourcePath + ".bak", "module Tuning\n")
      Ownership.tryOwn owned (kindOf world.Disk) (sourcePath + ".bak") |> Expect.equal "a sibling by prefix" (Error NotOwnedWhy.NotAmongProjectFiles)

    testCase "WHY - climbing out with .. and back in cannot reach a file the session does not own" <| fun _ ->
      let world = create ()
      world.Disk.PutText("/etc/passwd.fs", "module X\n")
      Ownership.tryOwn owned (kindOf world.Disk) "../../etc/passwd.fs" |> Expect.equal "outside the watched set" (Error NotOwnedWhy.NotAmongProjectFiles)
      Ownership.tryOwn owned (kindOf world.Disk) "src/../src/Tuning.fs" |> Expect.isOk "a detour that lands on a watched file is that file"

    testCase "WHY - a watched path that is a symbolic link is refused, because a rename over it would replace the link" <| fun _ ->
      let world = create ()
      world.Disk.Link sourcePath
      Ownership.tryOwn owned (kindOf world.Disk) sourcePath |> Expect.equal "a link" (Error NotOwnedWhy.IsASymbolicLink)

    testCase "WHY - a watched path that is not there, or not a regular file, is refused as not a file" <| fun _ ->
      let disk = Disk()
      Ownership.tryOwn owned (kindOf disk) sourcePath |> Expect.equal "missing" (Error NotOwnedWhy.NotARegularFile)

    testCase "WHY - a project file hot reload is not watching is still owned, and the file says so" <| fun _ ->
      let world = create ()
      match Ownership.tryOwn { owned with Watched = Set.empty } (kindOf world.Disk) sourcePath with
      | Ok file -> Ownership.watchOf file |> Expect.equal "not watched" WatchStatus.NotWatched
      | Error why -> failtestf "%A" why
      match Ownership.tryOwn owned (kindOf world.Disk) sourcePath with
      | Ok file -> Ownership.watchOf file |> Expect.equal "watched" WatchStatus.Watched
      | Error why -> failtestf "%A" why

    testCase "WHY - a write to a file hot reload is not watching says the running app has not seen it" <| fun _ ->
      let world = create ()
      let unwatched =
        match Ownership.tryOwn { owned with Watched = Set.empty } (kindOf world.Disk) sourcePath with
        | Ok file -> file
        | Error why -> failtestf "%A" why
      let request = NudgeRequest.Set(unwatched, gravity, seenOf "9.8", NudgeValue.LiteralText "12.5")
      (ran "unwatched write" (runUnlocked world.Ports request)).Notes |> Expect.contains "the change is on disk only" RunNote.FileNotWatched
      (ran "watched write" (setLiteral world maxHealth "100" "150")).Notes |> Expect.isEmpty "a watched file carries no such note"
  ]

[<Tests>]
let parseTests =
  let world = create ()
  let parse' r = parse owned (kindOf world.Disk) r
  let seen = contentHash "9.8"
  testList "Nudge parse" [
    testCase "WHY - a well-formed set parses to a typed request that carries the owned file and the address" <| fun _ ->
      match parse' (raw "set" sourcePath "Game.Tuning.gravity" seen "12.5" "") with
      | Ok(NudgeRequest.Set(file, address, hash, NudgeValue.LiteralText text)) ->
        Ownership.pathOf file |> Expect.equal "file" sourcePath
        address |> Expect.equal "address" gravity
        SeenHash.value hash |> Expect.equal "hash" seen
        text |> Expect.equal "literal" "12.5"
      | other -> failtestf "%A" other

    testCase "WHY - an expression parses to an expression request" <| fun _ ->
      match parse' (raw "set" sourcePath "Game.Tuning.gravity" seen "" "9.8 * 2.0") with
      | Ok(NudgeRequest.Set(_, _, _, NudgeValue.ExpressionText text)) -> text |> Expect.equal "expression" "9.8 * 2.0"
      | other -> failtestf "%A" other

    testCase "WHY - every action parses by its token, and a token nobody defined is refused and named" <| fun _ ->
      for action in NudgeAction.all do
        let token = NudgeAction.toToken action
        (NudgeAction.all |> List.filter (fun a -> NudgeAction.toToken a = token) |> List.length) |> Expect.equal "tokens are distinct" 1
      match parse' (raw "wiggle" sourcePath "" "" "" "") with
      | Error(NudgeRefusal.UnknownAction given) -> given |> Expect.equal "the bad action is carried" "wiggle"
      | other -> failtestf "%A" other

    testCase "WHY - the action is read without regard to case or padding" <| fun _ ->
      parse' (raw "  Inspect " sourcePath "" "" "" "") |> Expect.isOk "inspect"
      parse' (raw "UNDO" sourcePath "" "" "" "") |> Expect.isOk "undo"
      parse' (raw "Redo" sourcePath "" "" "" "") |> Expect.isOk "redo"

    testCase "WHY - a set with a field missing names the field, in the order a caller fixes them" <| fun _ ->
      parse' (raw "set" "" "Game.Tuning.gravity" seen "1" "") |> Expect.equal "file first" (Error(NudgeRefusal.MissingField NudgeField.File))
      parse' (raw "set" sourcePath "" seen "1" "") |> Expect.equal "then address" (Error(NudgeRefusal.MissingField NudgeField.Address))
      parse' (raw "set" sourcePath "Game.Tuning.gravity" "" "1" "") |> Expect.equal "then the seen hash" (Error(NudgeRefusal.MissingField NudgeField.Seen))
      parse' (raw "set" sourcePath "Game.Tuning.gravity" seen "" "") |> Expect.equal "then a value" (Error(NudgeRefusal.MissingField NudgeField.Value))

    testCase "WHY - a literal and an expression together are refused, not one picked over the other" <| fun _ ->
      parse' (raw "set" sourcePath "Game.Tuning.gravity" seen "1" "2") |> Expect.equal "ambiguous" (Error NudgeRefusal.ValueGivenTwice)

    testCase "WHY - a file the session does not own is refused at the edge, with the reason, before anything is read" <| fun _ ->
      parse' (raw "inspect" otherPath "" "" "" "") |> Expect.equal "not owned" (Error(NudgeRefusal.NotOwned(otherPath, NotOwnedWhy.NotAmongProjectFiles)))

    testCase "WHY - an address that is not an address is refused with the text parser's own reason" <| fun _ ->
      match parse' (raw "set" sourcePath "Game.Tuning.gravity/Sideways" seen "1" "") with
      | Error(NudgeRefusal.AddressTextInvalid(AddressTextRefusal.UnknownStep step)) -> step |> Expect.equal "the step" "Sideways"
      | other -> failtestf "%A" other

    testCase "WHY - a seen hash that is not a sha256 is refused, so a typo cannot read as a stale address" <| fun _ ->
      parse' (raw "set" sourcePath "Game.Tuning.gravity" "not-a-hash" "1" "") |> Expect.equal "bad hash" (Error(NudgeRefusal.SeenHashInvalid "not-a-hash"))
      SeenHash.tryParse (seen.ToUpperInvariant()) |> Expect.isOk "uppercase hex is the same hash"
      SeenHash.tryParse (seen + "0") |> Expect.isError "too long"
      SeenHash.tryParse (seen.Substring 1) |> Expect.isError "too short"

    testCase "WHY - an inspect with no address lists the file, and one with an address looks at that one" <| fun _ ->
      match parse' (raw "inspect" sourcePath "" "" "" "") with
      | Ok(NudgeRequest.Inspect(_, InspectTarget.WholeFile)) -> ()
      | other -> failtestf "%A" other
      match parse' (raw "inspect" sourcePath "Game.Tuning.gravity" "" "" "") with
      | Ok(NudgeRequest.Inspect(_, InspectTarget.OneAddress address)) -> address |> Expect.equal "address" gravity
      | other -> failtestf "%A" other
  ]

[<Tests>]
let literalKindTests =
  testList "Nudge literal kinds" [
    testCase "WHY - literal text is read as the kind the existing literal is" <| fun _ ->
      parseLiteralAs (LiteralValue.Real 9.8) "12.5" |> Expect.equal "real" (Ok(LiteralValue.Real 12.5))
      parseLiteralAs (LiteralValue.Integer 100L) "150" |> Expect.equal "integer" (Ok(LiteralValue.Integer 150L))
      parseLiteralAs (LiteralValue.Integer 100L) "-3" |> Expect.equal "negative integer" (Ok(LiteralValue.Integer -3L))
      parseLiteralAs (LiteralValue.Bool true) "false" |> Expect.equal "bool" (Ok(LiteralValue.Bool false))
      parseLiteralAs (LiteralValue.Char 'a') "z" |> Expect.equal "char" (Ok(LiteralValue.Char 'z'))
      parseLiteralAs (LiteralValue.Text "A") "B C" |> Expect.equal "text is taken as given" (Ok(LiteralValue.Text "B C"))
      parseLiteralAs (LiteralValue.Case "Easy") "Hard" |> Expect.equal "union case" (Ok(LiteralValue.Case "Hard"))

    testCase "WHY - text that is not a value of the literal's kind is refused with the kind named, never coerced" <| fun _ ->
      parseLiteralAs (LiteralValue.Real 9.8) "abc" |> Expect.equal "real" (Error(NudgeRefusal.LiteralNotReadable(LiteralKindName.Real, "abc")))
      parseLiteralAs (LiteralValue.Integer 100L) "1.5" |> Expect.equal "integer" (Error(NudgeRefusal.LiteralNotReadable(LiteralKindName.Integer, "1.5")))
      parseLiteralAs (LiteralValue.Bool true) "yes" |> Expect.equal "bool" (Error(NudgeRefusal.LiteralNotReadable(LiteralKindName.Boolean, "yes")))
      parseLiteralAs (LiteralValue.Char 'a') "ab" |> Expect.equal "char" (Error(NudgeRefusal.LiteralNotReadable(LiteralKindName.Character, "ab")))
      parseLiteralAs (LiteralValue.Case "Easy") "lower" |> Expect.equal "a case starts uppercase" (Error(NudgeRefusal.LiteralNotReadable(LiteralKindName.UnionCase, "lower")))

    testCase "WHY - a float that source cannot spell as a number (not-a-number, infinity) is refused" <| fun _ ->
      for text in [ "NaN"; "Infinity"; "-Infinity" ] do
        match parseLiteralAs (LiteralValue.Real 1.0) text with
        | Error(NudgeRefusal.LiteralNotReadable(LiteralKindName.Real, _)) -> ()
        | other -> failtestf "%s: expected a refusal, got %A" text other

    testPropertyWithConfig propConfig "PROPERTY, any finite float reads back as the same float" <| fun () ->
      Prop.forAll (Arb.fromGen (Gen.choose (-100000, 100000) |> Gen.map (fun n -> float n / 64.0))) (fun v ->
        parseLiteralAs (LiteralValue.Real 0.0) (v.ToString("R", Globalization.CultureInfo.InvariantCulture)) = Ok(LiteralValue.Real v))
  ]

[<Tests>]
let setTests =
  testList "Nudge set" [
    testCase "WHY - a literal nudge rewrites only that literal, and every other byte of the file is as it was" <| fun _ ->
      let world = create ()
      let receipt = setLiteral world gravity "9.8" "12.5" |> written "set gravity"
      source world |> Expect.equal "the file differs from the original in exactly that range" (tuningSource.Replace("let gravity = 9.8", "let gravity = 12.5"))
      receipt.Before |> Expect.equal "the text before" "9.8"
      receipt.After |> Expect.equal "the text after" "12.5"
      receipt.HashAfter |> Expect.equal "the hash after is the content hash of the text after" (contentHash "12.5")

    testCase "WHY - the literal inside a formula can be nudged without touching the formula around it" <| fun _ ->
      let world = create ()
      setLiteral world jumpVelocityRight "2.0" "3.5" |> written "set the right operand" |> ignore
      source world |> Expect.equal "gravity * 3.5" (tuningSource.Replace("gravity * 2.0", "gravity * 3.5"))

    testCase "WHY - an integer, a union case and a string are nudged as their own kinds" <| fun _ ->
      let world = create ()
      setLiteral world maxHealth "100" "150" |> written "integer" |> ignore
      setLiteral world difficulty "Easy" "Hard" |> written "case" |> ignore
      setLiteral world label "\"A\"" "B" |> written "string" |> ignore
      source world
      |> Expect.equal "three ranges changed, nothing else"
        (tuningSource.Replace("MaxHealth = 100", "MaxHealth = 150").Replace("Difficulty = Easy", "Difficulty = Hard").Replace("Label = \"A\"", "Label = \"B\""))

    testCase "WHY - the author's number style survives: 1.0 stays 1.0 and a unit of measure keeps its unit" <| fun _ ->
      let world = createWith "module M\nlet a = 1.0\nlet b = 12.5<m/s>\n"
      let a: TweakAddress = { ModulePath = [ "M" ]; BindingName = "a"; Path = [] }
      let b: TweakAddress = { ModulePath = [ "M" ]; BindingName = "b"; Path = [] }
      setLiteral world a "1.0" "3" |> written "a" |> ignore
      setLiteral world b "12.5<m/s>" "13.2" |> written "b" |> ignore
      source world |> Expect.equal "3.0 and 13.2<m/s>" "module M\nlet a = 3.0\nlet b = 13.2<m/s>\n"

    testCase "WHY - an expression replaces the whole value, and is written as the formatter would write it" <| fun _ ->
      let world = create ()
      setExpression world jumpVelocity "gravity * 2.0" "gravity*2.5+1.0" |> written "set formula" |> ignore
      source world |> Expect.equal "only the field's expression changed" (tuningSource.Replace("gravity * 2.0", "gravity * 2.5 + 1.0"))

    testCase "WHY - an expression write says it was parsed and not type-checked, a literal write does not" <| fun _ ->
      let world = create ()
      (ran "expression" (setExpression world jumpVelocity "gravity * 2.0" "gravity * 3.0")).Notes
      |> Expect.contains "nothing the door does is silent" RunNote.ExpressionNotTypeChecked
      (ran "literal" (setLiteral world gravity "9.8" "10.5")).Notes |> Expect.isEmpty "a literal keeps its kind, so there is nothing to caveat"

    testCase "WHY - line endings are not normalized: a CRLF file stays CRLF outside the range" <| fun _ ->
      let crlf = tuningSource.Replace("\n", "\r\n")
      let world = createWith crlf
      setLiteral world gravity "9.8" "12.5" |> written "set" |> ignore
      source world |> Expect.equal "every CRLF is still there" (crlf.Replace("9.8", "12.5"))

    testCase "WHY - a byte-order mark and a non-ASCII character elsewhere in the file survive the write" <| fun _ ->
      let world = createWith "module M\n// café 日本\nlet x = 1\n"
      let withBom = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (world.Disk.BytesOf sourcePath)
      world.Disk.Put(sourcePath, withBom)
      let x: TweakAddress = { ModulePath = [ "M" ]; BindingName = "x"; Path = [] }
      setLiteral world x "1" "2" |> written "set" |> ignore
      world.Disk.BytesOf sourcePath |> Expect.equal "the mark and the text are intact"
        (Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (System.Text.UTF8Encoding(false).GetBytes "module M\n// café 日本\nlet x = 2\n"))

    testCase "WHY - setting a value to what it already is writes nothing and journals nothing" <| fun _ ->
      let world = create ()
      let before = world.Disk.BytesOf sourcePath
      match (ran "same value" (setLiteral world gravity "9.8" "9.8")).Outcome with
      | NudgeOutcome.Unchanged(address, text) ->
        address |> Expect.equal "the address" gravity
        text |> Expect.equal "the text" "9.8"
      | other -> failtestf "%A" other
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical" before
      world.Disk.Exists(journalPath world) |> Expect.isFalse "no journal was created for a no-op"

    testPropertyWithConfig propConfig "PROPERTY, a literal nudge changes nothing outside the literal's own range, for any float" <| fun () ->
      Prop.forAll (Arb.fromGen (Gen.choose (-50000, 50000) |> Gen.map (fun n -> float n / 16.0))) (fun v ->
        let world = create ()
        let text = v.ToString("R", Globalization.CultureInfo.InvariantCulture)
        match setLiteral world gravity "9.8" text with
        | Ok _ ->
          let now = source world
          let prefix = "module Game.Tuning\n\nlet gravity = "
          now.StartsWith prefix && now.EndsWith(tuningSource.Substring(prefix.Length + 3))
        | Error _ -> false)
  ]

[<Tests>]
let staleTests =
  testList "Nudge stale addresses" [
    testCase "WHY - an expression that changed since it was inspected is refused with both texts, and the file is untouched" <| fun _ ->
      let world = create ()
      world.Disk.PutText(sourcePath, tuningSource.Replace("9.8", "9.81"))
      let before = world.Disk.BytesOf sourcePath
      match refused "stale" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.SourceMoved(seen, actual, currentText) ->
        seen |> Expect.equal "what the caller saw" (contentHash "9.8")
        actual |> Expect.equal "what is there" (contentHash "9.81")
        currentText |> Expect.equal "the text that is there, so the caller can decide" "9.81"
      | other -> failtestf "%A" other
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical" before
      world.Disk.Exists(journalPath world) |> Expect.isFalse "nothing was journaled"

    testCase "WHY - the same staleness check guards an expression write" <| fun _ ->
      let world = create ()
      world.Disk.PutText(sourcePath, tuningSource.Replace("gravity * 2.0", "gravity * 2.1"))
      match refused "stale expression" (setExpression world jumpVelocity "gravity * 2.0" "gravity * 3.0") with
      | NudgeRefusal.SourceMoved(_, _, currentText) -> currentText |> Expect.equal "the current formula" "gravity * 2.1"
      | other -> failtestf "%A" other

    testCase "WHY - a reformat or a comment above the binding does not make an address stale" <| fun _ ->
      let world = create ()
      world.Disk.PutText(sourcePath, tuningSource.Replace("let gravity", "// the pull of the world\n\n\nlet gravity"))
      setLiteral world gravity "9.8" "12.5" |> written "set after a reformat" |> ignore
      source world |> Expect.stringContains "the new value is in" "let gravity = 12.5"
      source world |> Expect.stringContains "the comment is still there" "// the pull of the world"

    testCase "WHY - a renamed binding is not guessed at: the refusal offers the new address, and taking the offer works" <| fun _ ->
      let world = create ()
      let renamed = tuningSource.Replace("let gravity = 9.8", "let g = 9.8").Replace("gravity * 2.0", "g * 2.0")
      world.Disk.PutText(sourcePath, renamed)
      let g: TweakAddress = { gravity with BindingName = "g" }
      match refused "renamed" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.AddressMoved(address, candidate) ->
        address |> Expect.equal "the address that stopped resolving" gravity
        candidate |> Expect.equal "where the same expression is now" g
      | other -> failtestf "%A" other
      world.Disk.TextOf sourcePath |> Expect.equal "nothing was written by the refusal" renamed
      setLiteral world g "9.8" "12.5" |> written "taking the offer" |> ignore
      source world |> Expect.equal "the renamed binding now holds the new value" (renamed.Replace("let g = 9.8", "let g = 12.5"))

    testCase "WHY - a binding that is gone with no copy of its expression anywhere is refused as gone" <| fun _ ->
      let world = create ()
      world.Disk.PutText(sourcePath, "module Game.Tuning\n\nlet other = 1\n")
      match refused "gone" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.AddressGone(ResolveError.BindingRemoved address) -> address |> Expect.equal "named" gravity
      | other -> failtestf "%A" other

    testCase "WHY - a path inside the expression that no longer leads anywhere is refused as gone" <| fun _ ->
      let world = create ()
      world.Disk.PutText(sourcePath, tuningSource.Replace("gravity * 2.0", "42"))
      match refused "shape changed" (setLiteral world jumpVelocityRight "2.0" "3.5") with
      | NudgeRefusal.AddressGone(ResolveError.PathGone address) -> address |> Expect.equal "named" jumpVelocityRight
      | other -> failtestf "%A" other

    testCase "WHY - a file that no longer parses is refused as such, and untouched" <| fun _ ->
      let world = create ()
      world.Disk.PutText(sourcePath, "let = = =\n")
      match refused "unparseable" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.AddressGone(ResolveError.ParseFailed _) -> ()
      | other -> failtestf "%A" other
      world.Disk.TextOf sourcePath |> Expect.equal "untouched" "let = = =\n"
  ]

[<Tests>]
let valueRefusalTests =
  testList "Nudge value refusals" [
    testCase "WHY - a literal nudge on something that is not a literal is refused, naming what is there" <| fun _ ->
      let world = create ()
      match refused "formula" (setLiteral world jumpVelocity "gravity * 2.0" "3.5") with
      | NudgeRefusal.NotALiteral text -> text |> Expect.equal "the formula" "gravity * 2.0"
      | other -> failtestf "%A" other

    testCase "WHY - text that is not the literal's kind is refused before the file is touched" <| fun _ ->
      let world = create ()
      let before = world.Disk.BytesOf sourcePath
      match refused "bad kind" (setLiteral world gravity "9.8" "fast") with
      | NudgeRefusal.LiteralNotReadable(kind, given) ->
        kind |> Expect.equal "real" LiteralKindName.Real
        given |> Expect.equal "what was given" "fast"
      | other -> failtestf "%A" other
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical" before

    testCase "WHY - an expression that does not parse is refused, and the file is untouched" <| fun _ ->
      let world = create ()
      let before = world.Disk.BytesOf sourcePath
      match refused "bad expression" (setExpression world jumpVelocity "gravity * 2.0" "gravity *") with
      | NudgeRefusal.ExpressionDoesNotParse _ -> ()
      | other -> failtestf "%A" other
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical" before
      world.Disk.Exists(journalPath world) |> Expect.isFalse "nothing was journaled"
  ]

[<Tests>]
let inspectTests =
  testList "Nudge inspect" [
    testCase "WHY - inspecting a file lists every address the engine can nudge, each with the hash a write will ask for" <| fun _ ->
      let world = create ()
      let listed = inspectAll world |> inspection "inspect"
      let expected = addressesOf tuningSource |> Expect.wantOk "parses"
      listed.Items |> List.map _.Address |> Expect.equal "the engine's own addresses, in its order" expected
      for item in listed.Items do
        item.Hash |> Expect.equal (sprintf "%s: the hash is the content hash of the text" (format item.Address)) (contentHash item.Text)
      listed.FileHash |> Expect.equal "and the whole file's hash" (contentHash tuningSource)
      listed.Listing |> Expect.equal "everything is shown" Listing.Complete

    testCase "WHY - a literal is reported as a knob with its value, and a formula as a formula" <| fun _ ->
      let world = create ()
      let listed = inspectAll world |> inspection "inspect"
      let kindOfAddress address = listed.Items |> List.find (fun i -> i.Address = address) |> _.Kind
      kindOfAddress gravity |> Expect.equal "a float knob" (ItemKind.Knob(LiteralValue.Real 9.8))
      kindOfAddress maxHealth |> Expect.equal "an integer knob" (ItemKind.Knob(LiteralValue.Integer 100L))
      kindOfAddress difficulty |> Expect.equal "a union case knob" (ItemKind.Knob(LiteralValue.Case "Easy"))
      kindOfAddress jumpVelocity |> Expect.equal "a formula" ItemKind.Formula

    testCase "WHY - one address inspects as just that address, and an address that is not there is refused" <| fun _ ->
      let world = create ()
      let one =
        runUnlocked world.Ports (NudgeRequest.Inspect(file world, InspectTarget.OneAddress maxHealth)) |> inspection "one"
      one.Items |> List.map _.Address |> Expect.equal "only it" [ maxHealth ]
      match refused "missing" (runUnlocked world.Ports (NudgeRequest.Inspect(file world, InspectTarget.OneAddress { maxHealth with BindingName = "nope" }))) with
      | NudgeRefusal.AddressGone _ -> ()
      | other -> failtestf "%A" other

    testCase "WHY - a long file is listed up to a stated limit and says how many there are, never silently cut" <| fun _ ->
      let total = NudgeLimits.maxInspectedItems + 50
      let big = "module Big\n" + String.concat "" [ for i in 0 .. total - 1 -> sprintf "let v%d = %d\n" i i ]
      let world = createWith big
      let listed = inspectAll world |> inspection "inspect a big file"
      listed.Items.Length |> Expect.equal "the limit" NudgeLimits.maxInspectedItems
      listed.Listing |> Expect.equal "and the total is said" (Listing.Truncated(NudgeLimits.maxInspectedItems, total))

    testCase "WHY - inspecting changes nothing on disk: no step that writes is taken" <| fun _ ->
      let world = create ()
      world.Disk.Arm []
      inspectAll world |> ran "inspect" |> ignore
      world.Disk.StepsTaken |> Expect.equal "no write, no append, no rename" 0

    testCase "WHY - inspect says how much history there is and where undo stands" <| fun _ ->
      let world = create ()
      (inspectAll world |> inspection "fresh").Journaled |> Expect.equal "nothing yet" 0
      setLiteral world gravity "9.8" "12.5" |> written "set" |> ignore
      let afterSet = inspectAll world |> inspection "after a set"
      afterSet.Journaled |> Expect.equal "one record" 1
      afterSet.Cursor |> Expect.equal "at the head" UndoCursor.AtHead
      undo world |> undone "undo" |> ignore
      (inspectAll world |> inspection "after an undo").Cursor |> Expect.notEqual "stepped back" UndoCursor.AtHead
  ]

[<Tests>]
let undoTests =
  testList "Nudge undo and redo" [
    testCase "WHY - undo puts the file back to the exact bytes it had, and redo puts the nudge back" <| fun _ ->
      let world = create ()
      let original = world.Disk.BytesOf sourcePath
      setLiteral world gravity "9.8" "12.5" |> written "set" |> ignore
      let nudged = world.Disk.BytesOf sourcePath
      let receipt = undo world |> undone "undo"
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical to before the nudge" original
      receipt.Before |> Expect.equal "the undo moved it from the nudged text" "12.5"
      receipt.After |> Expect.equal "back to the original text" "9.8"
      redo world |> redone "redo" |> ignore
      world.Disk.BytesOf sourcePath |> Expect.equal "the nudge is back, exactly" nudged

    testCase "WHY - undo steps back one write at a time, newest first, and only that write's range changes" <| fun _ ->
      let world = create ()
      setLiteral world gravity "9.8" "12.5" |> written "first" |> ignore
      let afterFirst = world.Disk.BytesOf sourcePath
      setLiteral world maxHealth "100" "150" |> written "second" |> ignore
      undo world |> undone "undo second" |> ignore
      world.Disk.BytesOf sourcePath |> Expect.equal "the first nudge is still in, the second is out" afterFirst
      undo world |> undone "undo first" |> ignore
      source world |> Expect.equal "both gone" tuningSource

    testCase "WHY - nothing to undo and nothing to redo are said, not faked, and change nothing" <| fun _ ->
      let world = create ()
      refused "fresh undo" (undo world) |> Expect.equal "no history" NudgeRefusal.NothingToUndo
      refused "fresh redo" (redo world) |> Expect.equal "no history" NudgeRefusal.NothingToRedo
      setLiteral world gravity "9.8" "12.5" |> written "set" |> ignore
      refused "redo at the head" (redo world) |> Expect.equal "already at the newest" NudgeRefusal.NothingToRedo
      undo world |> undone "undo" |> ignore
      refused "undo past the start" (undo world) |> Expect.equal "at the oldest write" NudgeRefusal.NothingToUndo
      source world |> Expect.equal "the file is the original" tuningSource

    testCase "WHY - a write after an undo starts a new line of history, and the next undo goes back to before it" <| fun _ ->
      let world = create ()
      setLiteral world gravity "9.8" "12.5" |> written "first" |> ignore
      undo world |> undone "undo" |> ignore
      setLiteral world gravity "9.8" "7.5" |> written "second, from the original" |> ignore
      undo world |> undone "undo the second" |> ignore
      source world |> Expect.equal "back to the original, not to the first" tuningSource

    testCase "WHY - undo refuses when the expression was edited since, with all three texts, and leaves the later edit alone" <| fun _ ->
      let world = create ()
      setLiteral world gravity "9.8" "12.5" |> written "set" |> ignore
      world.Disk.PutText(sourcePath, (source world).Replace("12.5", "13.75"))
      let before = world.Disk.BytesOf sourcePath
      match refused "diverged" (undo world) with
      | NudgeRefusal.UndoDiverged(wrote, now, original) ->
        wrote |> Expect.equal "what the nudge wrote" "12.5"
        now |> Expect.equal "what is there now" "13.75"
        original |> Expect.equal "what it was" "9.8"
      | other -> failtestf "%A" other
      world.Disk.BytesOf sourcePath |> Expect.equal "the later edit is untouched" before

    testCase "WHY - undo leaves a later edit to an unrelated binding in place" <| fun _ ->
      let world = create ()
      setLiteral world gravity "9.8" "12.5" |> written "set" |> ignore
      world.Disk.PutText(sourcePath, (source world).Replace("MaxHealth = 100", "MaxHealth = 999"))
      undo world |> undone "undo" |> ignore
      source world |> Expect.equal "gravity is back, the unrelated edit stays" (tuningSource.Replace("MaxHealth = 100", "MaxHealth = 999"))

    testCase "WHY - undo when the binding has gone refuses by name rather than guessing a new home" <| fun _ ->
      let world = create ()
      setLiteral world gravity "9.8" "12.5" |> written "set" |> ignore
      world.Disk.PutText(sourcePath, "module Game.Tuning\n\nlet other = 1\n")
      match refused "gone" (undo world) with
      | NudgeRefusal.HistoryRefused(RollbackError.AddressGone _) -> ()
      | other -> failtestf "%A" other
      source world |> Expect.equal "untouched" "module Game.Tuning\n\nlet other = 1\n"
  ]

[<Tests>]
let journalGuardTests =
  testList "Nudge journal guards" [
    testCase "WHY - a journal at its event budget refuses a new write, naming the count and the limit, and still allows undo" <| fun _ ->
      let world = create ()
      let full =
        [ for i in 1 .. TweakLogLimits.maxEventsPerSession ->
            { Id = i; At = int64 i; Event = TweakLogEvent.TweakSaved(maxHealth, "100", "100", contentHash "100", "h") } ]
      world.Disk.Put(journalPath world, TweakLogFormat.encodeSegment (Journal.fingerprintFor sourcePath) full)
      match refused "at budget" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.JournalAtBudget(events, limit) ->
        events |> Expect.equal "how many it holds" TweakLogLimits.maxEventsPerSession
        limit |> Expect.equal "the limit" TweakLogLimits.maxEventsPerSession
      | other -> failtestf "%A" other
      source world |> Expect.equal "untouched" tuningSource
      match undo world with
      | Error NudgeRefusal.NothingToUndo
      | Error(NudgeRefusal.UndoDiverged _)
      | Ok _ -> ()
      | Error(NudgeRefusal.JournalAtBudget _) -> failtest "undo must never be refused for budget"
      | Error other -> failtestf "unexpected %s" (tokensOf other)

    testCase "WHY - one event short of the budget still takes a write" <| fun _ ->
      let world = create ()
      let nearlyFull =
        [ for i in 1 .. TweakLogLimits.maxEventsPerSession - 1 ->
            { Id = i; At = int64 i; Event = TweakLogEvent.TweakSaved(maxHealth, "100", "100", contentHash "100", "h") } ]
      world.Disk.Put(journalPath world, TweakLogFormat.encodeSegment (Journal.fingerprintFor sourcePath) nearlyFull)
      setLiteral world gravity "9.8" "12.5" |> written "the last one that fits" |> ignore

    testCase "WHY - an address with an open conflict is locked: no write there, but other addresses are free" <| fun _ ->
      let world = create ()
      let conflict = { Id = 1; At = 1L; Event = TweakLogEvent.ConflictRaised(gravity, "12.5", "13", "9.8") }
      world.Disk.Put(journalPath world, TweakLogFormat.encodeSegment (Journal.fingerprintFor sourcePath) [ conflict ])
      match refused "locked" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.BlockedByOpenConflict address -> address |> Expect.equal "the locked address" gravity
      | other -> failtestf "%A" other
      setLiteral world maxHealth "100" "150" |> written "another address" |> ignore

    testCase "WHY - a journal this build cannot trust is refused by name before anything is written" <| fun _ ->
      let world = create ()
      let foreign = { Journal.fingerprintFor sourcePath with SchemaVersion = Fingerprint.schemaVersion + 1 }
      world.Disk.Put(journalPath world, TweakLogFormat.encodeSegment foreign [])
      match refused "untrusted" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.JournalFailed(JournalFault.Untrusted _) -> ()
      | other -> failtestf "%A" other
      source world |> Expect.equal "untouched" tuningSource
  ]

[<Tests>]
let atomicityTests =
  testList "Nudge atomicity and crashes" [
    testCase "WHY - a refused journal append writes nothing and leaves the file byte-identical" <| fun _ ->
      let world = create ()
      let before = world.Disk.BytesOf sourcePath
      let clean = stepsOfACleanSet ()
      // The record's own append is the third mutating step (header write, header rename, append).
      world.Disk.Arm [ 3, Fault.FailCleanly ]
      match refused "append fails" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.JournalFailed(JournalFault.Io fault) -> fault.Operation |> Expect.equal "the append" FileOperation.Appending
      | other -> failtestf "%A" other
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical" before
      (clean > 3) |> Expect.isTrue "the file replace comes after the append"

    testCase "WHY - a failed file replace is WriteFailed, the file is byte-identical, and the record is marked undone so history stays true" <| fun _ ->
      let world = create ()
      let before = world.Disk.BytesOf sourcePath
      let clean = stepsOfACleanSet ()
      world.Disk.Arm [ clean - 1, Fault.FailCleanly ]
      match refused "replace fails" (setLiteral world gravity "9.8" "12.5") with
      | NudgeRefusal.WriteFailed fault -> fault.Operation |> Expect.equal "the temp write" FileOperation.WritingTemp
      | other -> failtestf "%A" other
      world.Disk.Disarm()
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical" before
      world.Disk.Paths |> Expect.equal "no temp file is left" [ journalPath world; sourcePath ]
      refused "nothing to undo" (undo world) |> Expect.equal "the failed write is not in history as a live write" NudgeRefusal.NothingToUndo
      redo world |> redone "the failed write can be retried as a redo" |> ignore
      source world |> Expect.stringContains "and it lands" "let gravity = 12.5"

    testCase "WHY - a crash between the journal record and the rename leaves the file alone, and the next call settles the record" <| fun _ ->
      let world = create ()
      let before = world.Disk.BytesOf sourcePath
      let clean = stepsOfACleanSet ()
      world.Disk.Arm [ clean, Fault.CrashBefore ]
      diesIn (fun () -> setLiteral world gravity "9.8" "12.5" |> ignore) |> Expect.isTrue "the process died at the rename"
      world.Disk.Disarm()
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical" before
      world.Disk.Exists(AtomicWrite.tempPathFor sourcePath) |> Expect.isTrue "the crash left its temp file behind"
      let next = ran "after the crash" (inspectAll world)
      world.Disk.Exists(AtomicWrite.tempPathFor sourcePath) |> Expect.isFalse "the next call sweeps the stale temp file"
      match next.Notes |> List.tryPick (function RunNote.UnlandedWriteMarkedUndone id -> Some id | _ -> None) with
      | Some _ -> ()
      | None -> failtestf "the unfinished write was not reported: %A" next.Notes
      refused "nothing live to undo" (undo world) |> Expect.equal "it never landed" NudgeRefusal.NothingToUndo
      (ran "the second look" (inspectAll world)).Notes |> Expect.isEmpty "settled once, not every time"

    testCase "WHY - a crash after the rename, before the answer, leaves a record that undoes it exactly" <| fun _ ->
      let world = create ()
      let before = world.Disk.BytesOf sourcePath
      let clean = stepsOfACleanSet ()
      world.Disk.Arm [ clean, Fault.CrashAfter ]
      diesIn (fun () -> setLiteral world gravity "9.8" "12.5" |> ignore) |> Expect.isTrue "died after the rename"
      world.Disk.Disarm()
      source world |> Expect.stringContains "the write landed" "let gravity = 12.5"
      (ran "after" (inspectAll world)).Notes |> Expect.isEmpty "nothing to settle: the record and the file agree"
      undo world |> undone "undo" |> ignore
      world.Disk.BytesOf sourcePath |> Expect.equal "byte-identical to the original" before

    testCase "WHY - a crash that tears the journal record is healed on the next call and the file is untouched" <| fun _ ->
      let world = create ()
      let before = world.Disk.BytesOf sourcePath
      world.Disk.Arm [ 3, Fault.CrashTorn 50 ]
      diesIn (fun () -> setLiteral world gravity "9.8" "12.5" |> ignore) |> Expect.isTrue "died mid-append"
      world.Disk.Disarm()
      world.Disk.BytesOf sourcePath |> Expect.equal "the file was never touched" before
      (ran "after" (inspectAll world)).Notes |> Expect.contains "the tear is reported" RunNote.TornJournalTailRemoved
      setLiteral world gravity "9.8" "12.5" |> written "and a retry works" |> ignore

    testCase "WHY - every step of a nudge, crashed or failed in every way, leaves the file old or new, and the history can still restore the original" <| fun _ ->
      let steps = stepsOfACleanSet ()
      let original = (create ()).Disk.BytesOf sourcePath
      let faults = [ Fault.CrashBefore; Fault.CrashTorn 50; Fault.CrashTorn 1; Fault.CrashAfter; Fault.FailCleanly ]
      for step in 1 .. steps do
        for fault in faults do
          let world = create ()
          world.Disk.Arm [ step, fault ]
          let outcome = try Some(setLiteral world gravity "9.8" "12.5") with SimulatedCrash _ -> None
          world.Disk.Disarm()
          let now = world.Disk.BytesOf sourcePath
          let nudged = System.Text.UTF8Encoding(false).GetBytes(tuningSource.Replace("9.8", "12.5"))
          let label = sprintf "step %d of %d, %A" step steps fault
          (now = original || now = nudged) |> Expect.isTrue (sprintf "%s: the file is wholly old or wholly new" label)
          match outcome with
          | Some(Error _) -> (now = original) |> Expect.isTrue (sprintf "%s: a refusal leaves the file byte-identical" label)
          | _ -> ()
          // Recovery: look, then undo everything there is to undo. The original comes back exactly.
          inspectAll world |> ran (sprintf "%s: recover" label) |> ignore
          let mutable more = true
          while more do
            match undo world with
            | Ok _ -> ()
            | Error NudgeRefusal.NothingToUndo -> more <- false
            | Error refusal -> failtestf "%s: undo refused with %s" label (tokensOf refusal)
          world.Disk.BytesOf sourcePath |> Expect.equal (sprintf "%s: undo restores the original bytes" label) original
  ]

[<Tests>]
let concurrencyTests =
  /// Steps that make the first two readers of the source file meet before either goes on: the first waits for the
  /// second, up to the rendezvous. With the file lock the second can never arrive, so the first times out and goes.
  let meetingSteps (disk: Disk) : FileSteps =
    let arrivals = ref 0
    let met = new ManualResetEventSlim(false)
    let inner = disk.Steps
    { inner with
        ReadBytes =
          fun path ->
            match path = sourcePath with
            | false -> inner.ReadBytes path
            | true ->
              let bytes = inner.ReadBytes path
              match Interlocked.Increment(&arrivals.contents) with
              | 1 -> met.Wait NudgeTimeouts.nudgeRendezvous |> ignore
              | 2 -> met.Set()
              | _ -> ()
              bytes }

  let twoNudges (run: Ports -> NudgeRequest -> Task<Result<Ran, NudgeRefusal>>) =
    let world = create ()
    let ports = { world.Ports with Files = meetingSteps world.Disk }
    let request value = NudgeRequest.Set(file world, gravity, seenOf "9.8", NudgeValue.LiteralText value)
    let first = run ports (request "12.5")
    let second = run ports (request "13.5")
    Task.WhenAll([| first :> Task; second :> Task |]).Wait(TestTimeouts.patience) |> Expect.isTrue "both nudges finish"
    world, [ first.Result; second.Result ]

  let writtenCount results = results |> List.filter (function Ok { Outcome = NudgeOutcome.Written _ } -> true | _ -> false) |> List.length

  testList "Nudge concurrency" [
    testCase "WHY - two nudges from the same seen hash cannot both land: one wins, the other is told the expression moved" <| fun _ ->
      let locks = FileLocks()
      let world, results = twoNudges (fun ports request -> execute ports locks TestTimeouts.patience request)
      writtenCount results |> Expect.equal "exactly one write" 1
      results
      |> List.exists (function Error(NudgeRefusal.SourceMoved _) -> true | _ -> false)
      |> Expect.isTrue "the loser is refused as stale, not silently overwritten"
      (journalOf world).Events |> List.length |> Expect.equal "one journal record, for the one write" 1
      let now = source world
      (now.Contains "12.5" <> now.Contains "13.5") |> Expect.isTrue "the file holds exactly one of the two values"

    testCase "WHY - the twin that skips the lock lets both land, so the test above has teeth" <| fun _ ->
      let _, results = twoNudges (fun ports request -> executeWithoutLockTwin ports request)
      writtenCount results |> Expect.equal "without the lock both read the same hash and both write: a lost update" 2

    testCase "WHY - a wait for a busy file is refused as FileBusy after the bound, and names how long it waited" <| fun _ ->
      let locks = FileLocks()
      use release = new ManualResetEventSlim(false)
      let ok = Ok { Outcome = NudgeOutcome.Unchanged(gravity, "9.8"); Notes = [] }
      let holder = locks.WithLock(sourcePath, TestTimeouts.patience, fun () -> release.Wait(TestTimeouts.patience) |> ignore; ok)
      let waiter = locks.WithLock(sourcePath, NudgeTimeouts.nudgeShortLockWait, fun () -> ok)
      match waiter.Result with
      | Error(NudgeRefusal.FileBusy waited) -> waited |> Expect.equal "the bound it was given" NudgeTimeouts.nudgeShortLockWait
      | other -> failtestf "%A" other
      release.Set()
      holder.Wait(TestTimeouts.patience) |> ignore

    testCase "WHY - a busy file does not hold up a different file" <| fun _ ->
      let locks = FileLocks()
      use release = new ManualResetEventSlim(false)
      let ok = Ok { Outcome = NudgeOutcome.Unchanged(gravity, "9.8"); Notes = [] }
      let holder = locks.WithLock(sourcePath, TestTimeouts.patience, fun () -> release.Wait(TestTimeouts.patience) |> ignore; ok)
      let other = locks.WithLock(otherPath, NudgeTimeouts.nudgeShortLockWait, fun () -> ok)
      other.Wait(TestTimeouts.patience) |> ignore
      other.Result |> Expect.isOk "a different path has its own lock"
      release.Set()
      holder.Wait(TestTimeouts.patience) |> ignore
  ]

[<Tests>]
let engineAdditionTests =
  let saved id after = { Id = id; At = int64 id; Event = TweakLogEvent.TweakSaved(gravity, "9.8", after, contentHash after, "h") }
  let log events = { Events = events; NextId = (events |> List.map _.Id |> List.fold max 0) + 1 }
  testList "Nudge engine additions" [
    testCase "WHY - resolveAll reports every address addressesOf does, each resolved exactly as resolve would, from one parse" <| fun _ ->
      let all = resolveAll tuningSource |> Expect.wantOk "parses"
      all |> List.map (fun r -> r.Address) |> Expect.equal "the same addresses in the same order" (addressesOf tuningSource |> Expect.wantOk "parses")
      for r in all do
        resolve tuningSource r.Address |> Expect.wantOk "resolves" |> Expect.equal (sprintf "%s resolves the same way both ways" (format r.Address)) r

    testCase "WHY - resolveAll on a file that does not parse says so" <| fun _ ->
      match resolveAll "let = = =\n" with
      | Error(ResolveError.ParseFailed _) -> ()
      | other -> failtestf "%A" other

    testCase "WHY - a literal's value and style are read from its text alone, the same as from the file" <| fun _ ->
      readLiteralText "9.8" |> Expect.equal "a float" (Ok(LiteralValue.Real 9.8, LiteralStyle.FloatStyle("", false)))
      readLiteralText "12.5<m/s>" |> Expect.equal "with a unit" (Ok(LiteralValue.Real 12.5, LiteralStyle.MeasureStyle("m/s", LiteralStyle.FloatStyle("", false))))
      readLiteralText "gravity * 2.0" |> Expect.equal "a formula is not a literal" (Error(LiteralError.NotALiteral "gravity * 2.0"))
      for r in resolveAll tuningSource |> Expect.wantOk "parses" do
        let fromFile = readLiteral tuningSource r.Address |> Result.map (fun l -> l.Value, l.Style)
        readLiteralText r.Text |> Expect.equal (sprintf "%s reads the same from its text" (format r.Address)) fromFile

    testCase "WHY - every literal value maps to its own kind" <| fun _ ->
      LiteralKindName.ofValue (LiteralValue.Bool true) |> Expect.equal "bool" LiteralKindName.Boolean
      LiteralKindName.ofValue (LiteralValue.Integer 1L) |> Expect.equal "integer" LiteralKindName.Integer
      LiteralKindName.ofValue (LiteralValue.Real 1.0) |> Expect.equal "real" LiteralKindName.Real
      LiteralKindName.ofValue (LiteralValue.Char 'a') |> Expect.equal "char" LiteralKindName.Character
      LiteralKindName.ofValue (LiteralValue.Text "a") |> Expect.equal "text" LiteralKindName.Text
      LiteralKindName.ofValue (LiteralValue.Case "A") |> Expect.equal "case" LiteralKindName.UnionCase

    testCase "WHY - the last effect of an empty journal is none" <| fun _ ->
      lastEffectOf EventLog.empty |> Expect.equal "none" LastEffect.NoEffects

    testCase "WHY - the last effect is the newest event that moved text, and a rollback reports its target flipped" <| fun _ ->
      let l = log [ saved 1 "12.5" ]
      lastEffectOf l |> Expect.equal "a save" (LastEffect.Effect(1, gravity, "9.8", "12.5"))
      let rolled = log [ saved 1 "12.5"; { Id = 2; At = 2L; Event = TweakLogEvent.RolledBack 1 } ]
      lastEffectOf rolled |> Expect.equal "an undo moved it back" (LastEffect.Effect(2, gravity, "12.5", "9.8"))
      let redone = log [ saved 1 "12.5"; { Id = 2; At = 2L; Event = TweakLogEvent.RolledBack 1 }; { Id = 3; At = 3L; Event = TweakLogEvent.RolledBack 2 } ]
      lastEffectOf redone |> Expect.equal "a redo is the undo's own flip" (LastEffect.Effect(3, gravity, "9.8", "12.5"))

    testCase "WHY - an event that moves no text does not hide the last effect" <| fun _ ->
      let l = log [ saved 1 "12.5"; { Id = 2; At = 2L; Event = TweakLogEvent.ConflictResolved gravity } ]
      lastEffectOf l |> Expect.equal "still the save" (LastEffect.Effect(1, gravity, "9.8", "12.5"))

    testCase "WHY - the undo cursor read from the journal is where undo and redo stand" <| fun _ ->
      cursorOf EventLog.empty |> Expect.equal "fresh" UndoCursor.AtHead
      cursorOf (log [ saved 1 "12.5" ]) |> Expect.equal "after a write" UndoCursor.AtHead
      cursorOf (log [ saved 1 "12.5"; { Id = 2; At = 2L; Event = TweakLogEvent.RolledBack 1 } ]) |> Expect.equal "after an undo" (UndoCursor.At 1)
      cursorOf (log [ saved 1 "12.5"; { Id = 2; At = 2L; Event = TweakLogEvent.RolledBack 1 }; { Id = 3; At = 3L; Event = TweakLogEvent.RolledBack 2 } ])
      |> Expect.equal "after a redo" UndoCursor.AtHead

    testCase "WHY - reconcile finds nothing to settle when the file shows what the last record says" <| fun _ ->
      let afterSave = tuningSource.Replace("9.8", "12.5")
      reconcile afterSave (log [ saved 1 "12.5" ]) |> Expect.equal "agrees" Reconciliation.Consistent
      reconcile tuningSource EventLog.empty |> Expect.equal "no history" Reconciliation.Consistent

    testCase "WHY - reconcile marks a save undone when the file still holds the text from before it" <| fun _ ->
      match reconcile tuningSource (log [ saved 1 "12.5" ]) with
      | Reconciliation.WriteNeverLanded(id, TweakLogEvent.RolledBack target) ->
        id |> Expect.equal "the unlanded record" 1
        target |> Expect.equal "the marker undoes that record" 1
      | other -> failtestf "%A" other

    testCase "WHY - reconcile marks an undo undone when the file still holds the text the undo was to remove" <| fun _ ->
      let l = log [ saved 1 "12.5"; { Id = 2; At = 2L; Event = TweakLogEvent.RolledBack 1 } ]
      match reconcile (tuningSource.Replace("9.8", "12.5")) l with
      | Reconciliation.WriteNeverLanded(id, TweakLogEvent.RolledBack target) ->
        id |> Expect.equal "the unlanded undo" 2
        target |> Expect.equal "the marker undoes the undo" 2
      | other -> failtestf "%A" other

    testCase "WHY - reconcile does not touch a file someone else has edited, because it cannot tell whose write that is" <| fun _ ->
      reconcile (tuningSource.Replace("9.8", "13.0")) (log [ saved 1 "12.5" ]) |> Expect.equal "neither before nor after" Reconciliation.Consistent
      reconcile "module Other\nlet y = 1\n" (log [ saved 1 "12.5" ]) |> Expect.equal "address gone: cannot tell" Reconciliation.Consistent
  ]

let refusalSamples : NudgeRefusal list =
  [ NudgeRefusal.NoSessionToAct "no session"
    NudgeRefusal.ProjectFilesUnknown "worker down"
    NudgeRefusal.UnknownAction "wiggle"
    NudgeRefusal.MissingField NudgeField.Seen
    NudgeRefusal.ValueGivenTwice
    NudgeRefusal.NotOwned("/x.fs", NotOwnedWhy.NotAmongProjectFiles)
    NudgeRefusal.AddressTextInvalid AddressTextRefusal.Empty
    NudgeRefusal.SeenHashInvalid "zz"
    NudgeRefusal.AddressGone(ResolveError.BindingRemoved gravity)
    NudgeRefusal.AddressMoved(gravity, { gravity with BindingName = "g" })
    NudgeRefusal.SourceMoved("seen-hash", "actual-hash", "9.81")
    NudgeRefusal.NotALiteral "gravity * 2.0"
    NudgeRefusal.LiteralNotReadable(LiteralKindName.Real, "abc")
    NudgeRefusal.ValueKindMismatch "kind"
    NudgeRefusal.ExpressionDoesNotParse "bad"
    NudgeRefusal.BlockedByOpenConflict gravity
    NudgeRefusal.NothingToUndo
    NudgeRefusal.NothingToRedo
    NudgeRefusal.UndoDiverged("12.5", "13.75", "9.8")
    NudgeRefusal.HistoryRefused(RollbackError.NoSuchOperation 7)
    NudgeRefusal.FileUnreadable { Operation = FileOperation.Reading; Path = "/x.fs"; Reason = "denied" }
    NudgeRefusal.JournalFailed(JournalFault.Unreadable "garbage")
    NudgeRefusal.JournalAtBudget(5000, 5000)
    NudgeRefusal.WriteFailed { Operation = FileOperation.Renaming; Path = "/x.fs"; Reason = "denied" }
    NudgeRefusal.FileBusy(TimeSpan.FromSeconds 1.0) ]

[<Tests>]
let refusalTests =
  testList "Nudge refusals" [
    testCase "WHY - the samples cover every refusal case, so a new case without a rule and a next action cannot slip in" <| fun _ ->
      let cases = FSharpType.GetUnionCases typeof<NudgeRefusal>
      (refusalSamples |> List.map (fun r -> fst (FSharpValue.GetUnionFields(r, typeof<NudgeRefusal>)) |> fun c -> c.Tag) |> List.distinct |> List.length)
      |> Expect.equal "one sample per case" cases.Length

    testCase "WHY - every refusal has its own token, a rule, and a next action" <| fun _ ->
      (refusalSamples |> List.map NudgeRefusal.token |> List.distinct |> List.length) |> Expect.equal "tokens are distinct" refusalSamples.Length
      for refusal in refusalSamples do
        NudgeRefusal.token refusal |> String.IsNullOrWhiteSpace |> Expect.isFalse (sprintf "%s has a token" (NudgeRefusal.token refusal))
        NudgeRefusal.rule refusal |> String.IsNullOrWhiteSpace |> Expect.isFalse (sprintf "%s names the rule" (NudgeRefusal.token refusal))
        NudgeRefusal.nextAction refusal |> String.IsNullOrWhiteSpace |> Expect.isFalse (sprintf "%s says what to do next" (NudgeRefusal.token refusal))

    testCase "WHY - a stale refusal carries the facts a caller needs, so it can act without asking again" <| fun _ ->
      let moved = NudgeRefusal.SourceMoved("seen-hash", "actual-hash", "9.81")
      NudgeRefusal.rule moved |> Expect.stringContains "the text that is there now" "9.81"
      let offer = NudgeRefusal.AddressMoved(gravity, { gravity with BindingName = "g" })
      NudgeRefusal.nextAction offer |> Expect.stringContains "the address to take instead" (format { gravity with BindingName = "g" })
      let diverged = NudgeRefusal.UndoDiverged("12.5", "13.75", "9.8")
      for fact in [ "12.5"; "13.75"; "9.8" ] do
        NudgeRefusal.rule diverged |> Expect.stringContains "all three texts" fact

    testCase "WHY - the vocabularies a client branches on have distinct tokens" <| fun _ ->
      let distinct xs = xs |> List.distinct |> List.length
      (NudgeAction.all |> List.map NudgeAction.toToken |> distinct) |> Expect.equal "actions" NudgeAction.all.Length
      let fields = [ NudgeField.File; NudgeField.Address; NudgeField.Seen; NudgeField.Value ]
      (fields |> List.map NudgeField.toToken |> distinct) |> Expect.equal "fields" fields.Length
      let kinds = [ LiteralKindName.Boolean; LiteralKindName.Integer; LiteralKindName.Real; LiteralKindName.Character; LiteralKindName.Text; LiteralKindName.UnionCase ]
      (kinds |> List.map LiteralKindName.toToken |> distinct) |> Expect.equal "literal kinds" kinds.Length
  ]
