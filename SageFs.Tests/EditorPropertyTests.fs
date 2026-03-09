module SageFs.Tests.EditorPropertyTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.SharedGenerators

let private pick gen = (Gen.sample 1 gen).[0]

// ── Generators ──

let private genBufferChar =
  Gen.elements (['a'..'z'] @ ['A'..'Z'] @ ['0'..'9'] @ [' '])

let private genValidatedBuffer =
  gen {
    let! lineCount = Gen.choose (1, 5)
    let! lineLens = Gen.listOfLength lineCount (Gen.choose (0, 20))
    let! lines =
      gen {
        let mutable result = []
        for len in List.rev lineLens do
          let! chars = Gen.listOfLength len genBufferChar
          result <- System.String(chars |> List.toArray) :: result
        return result
      }
    let! line = Gen.choose (0, lineCount - 1)
    let! col = Gen.choose (0, lines.[line].Length)
    return { Lines = lines; Cursor = { Line = line; Column = col } }
  }

let private genDirection =
  Gen.elements [ Direction.Up; Direction.Down; Direction.Left; Direction.Right ]

let private genDirectionSequence =
  Gen.choose (1, 20)
  |> Gen.bind (fun n -> Gen.listOfLength n genDirection)

let private genCursorPosition =
  gen {
    let! line = Gen.choose (-5, 10)
    let! col = Gen.choose (-5, 30)
    return { Line = line; Column = col }
  }

// ── Helpers ──

let private isValidCursor (buf: ValidatedBuffer) =
  let c = ValidatedBuffer.cursor buf
  let ls = ValidatedBuffer.lines buf
  c.Line >= 0
  && c.Line < ls.Length
  && c.Column >= 0
  && c.Column <= ls.[c.Line].Length

// ── ValidatedBuffer.create properties ──

let createTests =
  testList "ValidatedBuffer.create properties" [

    testPropertyWithConfig propConfig "rejects empty lines list" <|
      fun () ->
        let pos = pick genCursorPosition
        match ValidatedBuffer.create [] pos with
        | Error BufferError.EmptyLines -> true
        | _ -> false

    testPropertyWithConfig propConfig "rejects out-of-bounds cursor" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let lines = ValidatedBuffer.lines buf
        // cursor well beyond valid range
        let badCursor = { Line = lines.Length + 5; Column = 100 }
        match ValidatedBuffer.create lines badCursor with
        | Error (BufferError.CursorOutOfBounds _) -> true
        | _ -> false

    testPropertyWithConfig propConfig "accepts valid buffer round-trip" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let result =
          ValidatedBuffer.create
            (ValidatedBuffer.lines buf)
            (ValidatedBuffer.cursor buf)
        match result with
        | Ok rebuilt ->
          ValidatedBuffer.lines rebuilt = ValidatedBuffer.lines buf
          && ValidatedBuffer.cursor rebuilt = ValidatedBuffer.cursor buf
        | Error _ -> false
  ]

// ── insertChar properties ──

let insertCharTests =
  testList "insertChar properties" [

    testPropertyWithConfig propConfig "increases text length by 1" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let c = pick genBufferChar
        let before = (ValidatedBuffer.text buf).Length
        let after = (buf |> ValidatedBuffer.insertChar c |> ValidatedBuffer.text).Length
        after = before + 1

    testPropertyWithConfig propConfig "insertChar then deleteBackward is identity" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let c = pick genBufferChar
        let roundTripped =
          buf
          |> ValidatedBuffer.insertChar c
          |> ValidatedBuffer.deleteBackward
        ValidatedBuffer.text roundTripped = ValidatedBuffer.text buf

    testPropertyWithConfig propConfig "cursor stays valid after insertChar" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let c = pick genBufferChar
        buf |> ValidatedBuffer.insertChar c |> isValidCursor
  ]

// ── newLine properties ──

let newLineTests =
  testList "newLine properties" [

    testPropertyWithConfig propConfig "increases line count by 1" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let before = (ValidatedBuffer.lines buf).Length
        let after = (buf |> ValidatedBuffer.newLine |> ValidatedBuffer.lines).Length
        after = before + 1

    testPropertyWithConfig propConfig "preserves total text content" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let charsBefore =
          ValidatedBuffer.text buf
          |> Seq.filter (fun c -> c <> '\n')
          |> Seq.toList
        let charsAfter =
          buf
          |> ValidatedBuffer.newLine
          |> ValidatedBuffer.text
          |> Seq.filter (fun c -> c <> '\n')
          |> Seq.toList
        charsAfter = charsBefore

    testPropertyWithConfig propConfig "cursor stays valid after newLine" <|
      fun () ->
        let buf = pick genValidatedBuffer
        buf |> ValidatedBuffer.newLine |> isValidCursor
  ]

// ── moveCursor properties ──

let moveCursorTests =
  testList "moveCursor properties" [

    testPropertyWithConfig propConfig "never goes out of bounds for any direction" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let dir = pick genDirection
        buf |> ValidatedBuffer.moveCursor dir |> isValidCursor

    testPropertyWithConfig propConfig "stays valid after sequence of moves" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let dirs = pick genDirectionSequence
        let result =
          dirs |> List.fold (fun b d -> ValidatedBuffer.moveCursor d b) buf
        isValidCursor result

    testPropertyWithConfig propConfig "does not change text content" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let dir = pick genDirection
        let moved = buf |> ValidatedBuffer.moveCursor dir
        ValidatedBuffer.text moved = ValidatedBuffer.text buf
  ]

// ── setCursor properties ──

let setCursorTests =
  testList "setCursor properties" [

    testPropertyWithConfig propConfig "clamps to valid range for any position" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let pos = pick genCursorPosition
        buf |> ValidatedBuffer.setCursor pos |> isValidCursor

    testPropertyWithConfig propConfig "does not change text content" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let pos = pick genCursorPosition
        let result = buf |> ValidatedBuffer.setCursor pos
        ValidatedBuffer.text result = ValidatedBuffer.text buf
  ]

// ── text roundtrip ──

let textRoundtripTests =
  testList "text roundtrip properties" [

    testPropertyWithConfig propConfig "text split on newline equals lines" <|
      fun () ->
        let buf = pick genValidatedBuffer
        let fromText = (ValidatedBuffer.text buf).Split('\n') |> Array.toList
        fromText = ValidatedBuffer.lines buf
  ]

// ── Top-level ──

[<Tests>]
let allEditorPropertyTests =
  testList "Editor property tests" [
    createTests
    insertCharTests
    newLineTests
    moveCursorTests
    setCursorTests
    textRoundtripTests
  ]
