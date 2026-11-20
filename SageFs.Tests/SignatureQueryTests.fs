/// TDD for the pure signature-at-caret decision. No compiler, no file read, no session: the FCS
/// answer is injected, so every boundary of the caret mapping and the refusal of an empty tooltip is
/// replayable. The end-of-line caret (column = length + 1) is a real editor position and is pinned by
/// an example and by a mutation that moves the boundary one to the left.
module SageFs.Tests.SignatureQueryTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features
open MutationTestingFramework

let private lines = [| "let answer = 42"; "let double x = x * 2"; "" |]

let private req (file: string) (line: int) (column: int) : SignatureQuery.Request =
  { File = file; Line = line; Column = column }

let private answer (tooltip: string) : SignatureQuery.FcsAnswer =
  { Name = "double"
    Tooltip = tooltip
    Kind = "Method" }

let private signatureOf (r: Result<SignatureQuery.View, SignatureQuery.Refusal>) =
  match r with
  | Ok v -> v.Signature
  | Error _ -> "<refused>"

/// A caret address that is valid by construction: a real line and a column from 1 to length + 1.
let private genValidCaret =
  gen {
    let! line = Gen.choose (1, lines.Length)
    let! column = Gen.choose (1, lines.[line - 1].Length + 1)
    return line, column
  }

[<Tests>]
let signatureQueryTests =
  testList "SignatureQuery — signature at a caret" [

    testList "plan — request gates" [
      testCase "WHY — an empty file path is refused before any caret math" <| fun _ ->
        SignatureQuery.plan (req "" 1 1)
        |> Expect.equal "refuses the missing path" (Error SignatureQuery.Refusal.NoFilePath)

      testCase "WHY — line is 1-based, so 0 is refused" <| fun _ ->
        SignatureQuery.plan (req "a.fs" 0 1)
        |> Expect.equal "refuses line 0" (Error(SignatureQuery.Refusal.LineBeforeOne 0))

      testCase "WHY — column is 1-based, so 0 is refused" <| fun _ ->
        SignatureQuery.plan (req "a.fs" 1 0)
        |> Expect.equal "refuses column 0" (Error(SignatureQuery.Refusal.ColumnBeforeOne 0))

      testCase "WHY — a valid address is accepted unchanged" <| fun _ ->
        SignatureQuery.plan (req "a.fs" 2 5)
        |> Expect.equal "keeps the request" (Ok(req "a.fs" 2 5))
    ]

    testList "caretOf — maps (line, column) to a character offset" [
      testCase "WHY — the top-left caret is offset 0" <| fun _ ->
        SignatureQuery.caretOf lines (req "a.fs" 1 1) |> Expect.equal "offset 0" (Ok 0)

      testCase "WHY — a later line adds every earlier line plus its newline" <| fun _ ->
        SignatureQuery.caretOf lines (req "a.fs" 2 1) |> Expect.equal "line 2 start" (Ok 16)

      testCase "WHY — a caret one past the last character is legal (end of line)" <| fun _ ->
        SignatureQuery.caretOf lines (req "a.fs" 1 16) |> Expect.equal "end of line 1" (Ok 15)

      testCase "WHY — a caret two past the last character is refused" <| fun _ ->
        SignatureQuery.caretOf lines (req "a.fs" 1 17)
        |> Expect.equal "refuses past end of line" (Error(SignatureQuery.Refusal.ColumnPastEnd(17, 15)))

      testCase "WHY — the first column of an empty last line is the file end" <| fun _ ->
        SignatureQuery.caretOf lines (req "a.fs" 3 1) |> Expect.equal "file end" (Ok 37)

      testCase "WHY — a line past the file is refused with the line count" <| fun _ ->
        SignatureQuery.caretOf lines (req "a.fs" 9 1)
        |> Expect.equal "refuses line 9" (Error(SignatureQuery.Refusal.LinePastEnd(9, 3)))
    ]

    testList "wordAt / pickIndex — resolve the identifier under the caret" [
      testCase "WHY — a caret on a letter includes that letter in the word" <| fun _ ->
        SignatureQuery.wordAt "let double x = x * 2" 10 |> Expect.equal "the whole name" "double"

      testCase "WHY — a caret mid-word returns the word up to the caret, not past it" <| fun _ ->
        SignatureQuery.wordAt "let double x = x * 2" 9 |> Expect.equal "up to the caret" "doubl"

      testCase "WHY — a caret on whitespace has no word" <| fun _ ->
        SignatureQuery.wordAt "let double x = x * 2" 11 |> Expect.equal "no word" ""

      testCase "WHY — only an exact-name candidate is picked" <| fun _ ->
        SignatureQuery.pickIndex "double" [| "double"; "downcast" |]
        |> Expect.equal "exact match" (Some 0)

      testCase "WHY — a prefix does not silently resolve to a longer name" <| fun _ ->
        SignatureQuery.pickIndex "doub" [| "double"; "downcast" |]
        |> Expect.equal "no match" None

      testCase "WHY — an empty word never picks a candidate" <| fun _ ->
        SignatureQuery.pickIndex "" [| "double" |] |> Expect.equal "no match" None
    ]

    testList "render — the compiler's answer becomes a signature" [
      testCase "WHY — only the first tooltip line is the signature, trimmed" <| fun _ ->
        SignatureQuery.render (answer "  val double: x:int -> int  \nthe long docs")
        |> signatureOf
        |> Expect.equal "first line only" "val double: x:int -> int"

      testCase "WHY — an empty tooltip is refused, not shown as a blank signature" <| fun _ ->
        SignatureQuery.render (answer "   ")
        |> Expect.equal "refuses empty" (Error SignatureQuery.Refusal.EmptySignature)

      testCase "WHY — a tooltip whose first line is empty is also refused" <| fun _ ->
        SignatureQuery.render (answer "\n  val double: x:int -> int")
        |> Expect.equal "refuses leading blank" (Error SignatureQuery.Refusal.EmptySignature)

      testCase "WHY — the name and kind travel with the signature" <| fun _ ->
        match SignatureQuery.render (answer "val double: x:int -> int") with
        | Ok v ->
          v.Name |> Expect.equal "name" "double"
          v.Kind |> Expect.equal "kind" "Method"
        | Error e -> failtestf "expected a signature, got %A" e
    ]

    testList "properties — the caret offset is monotone and total over valid addresses" [
      testProperty "PROPERTY — a valid address never overflows and grows with the column" <|
        Prop.forAll (Arb.fromGen genValidCaret) (fun (line, column) ->
          match SignatureQuery.caretOf lines (req "a.fs" line column) with
          | Error _ -> false
          | Ok offset ->
            offset >= 0
            && (if column < lines.[line - 1].Length + 1
                then SignatureQuery.caretOf lines (req "a.fs" line (column + 1)) = Ok(offset + 1)
                else true))
    ]

    testList "mutations — each boundary decision is caught by an assertion" [
      // caretOf is curried over the file's lines; bind them so the framework sees a one-arg function.
      let caret = SignatureQuery.caretOf lines

      let acceptsPastEnd : Mutant<SignatureQuery.Request -> Result<int, SignatureQuery.Refusal>> =
        { Name = "end_of_line_boundary_moves_right"
          Description = "accepting column = length + 2 would let a caret past the line through"
          Apply = fun _ _ -> Ok 0 }

      let blankInsteadOfRefusal : Mutant<SignatureQuery.FcsAnswer -> Result<SignatureQuery.View, SignatureQuery.Refusal>> =
        { Name = "empty_tooltip_becomes_a_blank_signature"
          Description = "showing a blank signature instead of refusing hides 'no symbol here'"
          Apply = fun _ a ->
            Ok { Name = a.Name; Signature = ""; Kind = a.Kind } }

      let lineZeroAccepted : Mutant<SignatureQuery.Request -> Result<SignatureQuery.Request, SignatureQuery.Refusal>> =
        { Name = "line_zero_accepted"
          Description = "a 0-based line would let an invalid caret address through"
          Apply = fun _ r -> Ok r }

      detectsOutputMutant acceptsPastEnd (req "a.fs" 1 17) caret
        (fun r -> r = Error(SignatureQuery.Refusal.ColumnPastEnd(17, 15)))

      detectsOutputMutant blankInsteadOfRefusal (answer "   ") SignatureQuery.render
        (fun r -> r = Error SignatureQuery.Refusal.EmptySignature)

      detectsOutputMutant lineZeroAccepted (req "a.fs" 0 1) SignatureQuery.plan
        (fun r -> r = Error(SignatureQuery.Refusal.LineBeforeOne 0))
    ]
  ]
