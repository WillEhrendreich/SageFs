/// Coverage for `LiteralEdit`: setting a literal keeps the author's style,
/// round-trips floats exactly, and reproduces the original source byte for
/// byte when the "new" value is the one that was already there.
module SageFs.Tests.LiteralEditTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit

let private addr name : TweakAddress = { ModulePath = [ "M" ]; BindingName = name; Path = [] }

let private roundTrip (source: string) (name: string) (newValue: LiteralValue) : string =
  setLiteral source (addr name) newValue |> Expect.wantOk "setLiteral should succeed"

[<Tests>]
let literalEditTests =
  testList "LiteralEdit" [

    testCase "1.0 stays 1.0, never becomes 1." <| fun _ ->
      let source = "module M\nlet x = 1.0\n"
      roundTrip source "x" (LiteralValue.Real 2.0) |> Expect.equal "keeps the trailing .0" "module M\nlet x = 2.0\n"

    testCase "5. (trailing dot, no digits) stays that way for a new integral value" <| fun _ ->
      let source = "module M\nlet x = 5.\n"
      roundTrip source "x" (LiteralValue.Real 7.0) |> Expect.equal "still a bare trailing dot" "module M\nlet x = 7.\n"

    testCase "hex stays hex" <| fun _ ->
      let source = "module M\nlet x = 0x1F\n"
      roundTrip source "x" (LiteralValue.Integer 255L) |> Expect.equal "still hex, uppercase digits" "module M\nlet x = 0xFF\n"

    testCase "underscore grouping is kept for a freshly grouped value" <| fun _ ->
      let source = "module M\nlet x = 1_000\n"
      roundTrip source "x" (LiteralValue.Integer 2000000L)
      |> Expect.equal "grouped every 3 digits from the right" "module M\nlet x = 2_000_000\n"

    testCase "suffixes survive: float32" <| fun _ ->
      let source = "module M\nlet x = 1.0f\n"
      roundTrip source "x" (LiteralValue.Real 3.5) |> Expect.equal "keeps the f suffix" "module M\nlet x = 3.5f\n"

    testCase "suffixes survive: int64 L" <| fun _ ->
      let source = "module M\nlet x = 10L\n"
      roundTrip source "x" (LiteralValue.Integer 99L) |> Expect.equal "keeps the L suffix" "module M\nlet x = 99L\n"

    testCase "suffixes survive: byte uy" <| fun _ ->
      let source = "module M\nlet x = 2uy\n"
      roundTrip source "x" (LiteralValue.Integer 9L) |> Expect.equal "keeps the uy suffix" "module M\nlet x = 9uy\n"

    testCase "units of measure survive a scrub" <| fun _ ->
      let source = "module M\nlet x = 12.5<m/s>\n"
      roundTrip source "x" (LiteralValue.Real 13.2) |> Expect.equal "the unit annotation is untouched" "module M\nlet x = 13.2<m/s>\n"

    testCase "negative decimal literals keep their sign spelling" <| fun _ ->
      let source = "module M\nlet x = -1.5\n"
      roundTrip source "x" (LiteralValue.Real -3.25) |> Expect.equal "still a negative float" "module M\nlet x = -3.25\n"

    testCase "floats round-trip exactly: 0.12 never becomes 0.11999999" <| fun _ ->
      let source = "module M\nlet x = 0.1\n"
      let result = roundTrip source "x" (LiteralValue.Real 0.12)
      result |> Expect.equal "exact round trip" "module M\nlet x = 0.12\n"

    testCase "setting the exact same value back reproduces the original source byte for byte" <| fun _ ->
      let source = "module M\nlet x = 1_000\n"
      roundTrip source "x" (LiteralValue.Integer 1000L) |> Expect.equal "byte for byte" source

    testCase "readLiteral on a bool" <| fun _ ->
      let source = "module M\nlet x = true\n"
      let lit = readLiteral source (addr "x") |> Expect.wantOk "reads"
      lit.Value |> Expect.equal "bool value" (LiteralValue.Bool true)
      setLiteral source (addr "x") (LiteralValue.Bool false) |> Expect.equal "flips" (Ok "module M\nlet x = false\n")

    testCase "readLiteral on a DU-case-like identifier" <| fun _ ->
      let source = "module M\nlet x = Hard\n"
      let lit = readLiteral source (addr "x") |> Expect.wantOk "reads"
      lit.Value |> Expect.equal "case value" (LiteralValue.Case "Hard")
      setLiteral source (addr "x") (LiteralValue.Case "Easy")
      |> Expect.equal "swaps the case" (Ok "module M\nlet x = Easy\n")

    testCase "setLiteral on a non-literal address fails with NotALiteral" <| fun _ ->
      let source = "module M\nlet x = someFunction 1 2\n"
      match readLiteral source (addr "x") with
      | Error(LiteralError.NotALiteral _) -> ()
      | other -> failtestf "expected NotALiteral, got %A" other

    testCase "setLiteral fails cleanly with a kind mismatch rather than silently changing the expression's type" <| fun _ ->
      let source = "module M\nlet x = 1.0\n"
      match setLiteral source (addr "x") (LiteralValue.Text "oops") with
      | Error(SetLiteralError.KindMismatch _) -> ()
      | other -> failtestf "expected KindMismatch, got %A" other

    testCase "bytes outside the range never change" <| fun _ ->
      let source = "module M\nlet a = 1\nlet x = 1.0\nlet b = 2\n"
      let result = setLiteral source (addr "x") (LiteralValue.Real 99.0) |> Expect.wantOk "sets"
      result |> Expect.equal "everything except the literal itself is untouched" "module M\nlet a = 1\nlet x = 99.0\nlet b = 2\n"

    testProperty "PROPERTY, setting an integer literal back to its own value reproduces the source byte for byte" <|
      fun (n: int) ->
        let source = sprintf "module M\nlet x = %d\n" n
        setLiteral source (addr "x") (LiteralValue.Integer(int64 n)) = Ok source

    testProperty "PROPERTY, setting a new integer value always parses back to exactly that value" <|
      fun (n: int) (m: int) ->
        let source = sprintf "module M\nlet x = %d\n" n
        match setLiteral source (addr "x") (LiteralValue.Integer(int64 m)) with
        | Ok newSource ->
          match readLiteral newSource (addr "x") with
          | Ok lit -> lit.Value = LiteralValue.Integer(int64 m)
          | Error _ -> false
        | Error _ -> false

    testProperty "PROPERTY, a float value always parses back to exactly the value that was set" <|
      fun (NormalFloat f) ->
        let source = "module M\nlet x = 0.0\n"
        match setLiteral source (addr "x") (LiteralValue.Real f) with
        | Ok newSource ->
          match readLiteral newSource (addr "x") with
          | Ok lit ->
            match lit.Value with
            | LiteralValue.Real v -> v = f
            | _ -> false
          | Error _ -> false
        | Error _ -> false

    testProperty "PROPERTY, bytes before and after the literal's own range never change" <|
      fun (n: int) (m: int) ->
        let source = sprintf "module M\nlet before = 111\nlet x = %d\nlet after = 222\n" n
        match setLiteral source (addr "x") (LiteralValue.Integer(int64 m)) with
        | Ok newSource ->
          newSource.StartsWith "module M\nlet before = 111\nlet x = "
          && newSource.EndsWith "\nlet after = 222\n"
        | Error _ -> false
  ]
