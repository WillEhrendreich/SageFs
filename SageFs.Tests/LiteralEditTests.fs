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

let private refusedAsOutOfRange (source: string) (value: LiteralValue) : bool =
  match setLiteral source (addr "x") value with
  | Error(SetLiteralError.OutOfRange _) -> true
  | Ok _
  | Error _ -> false

let private accepted (source: string) (value: LiteralValue) : bool =
  match setLiteral source (addr "x") value with
  | Ok _ -> true
  | Error _ -> false

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
      let source = "module M\nlet x = 1_234\n"
      roundTrip source "x" (LiteralValue.Integer 2345678L)
      |> Expect.equal "grouped every 3 digits from the right" "module M\nlet x = 2_345_678\n"

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
      let source = "module M\nlet x = 1_234\n"
      roundTrip source "x" (LiteralValue.Integer 1234L) |> Expect.equal "byte for byte" source

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

    // ── a literal never leaves the numbers its own type holds ──
    // The door writes text. A number the type cannot hold is text that does not compile, so it is refused here, where every
    // path that writes a number (the dashboard's slider, the MCP tool) passes through.

    testCase "an int (no suffix) holds exactly the int32 numbers" <| fun _ ->
      let source = "module M\nlet x = 100\n"
      accepted source (LiteralValue.Integer 2147483647L) |> Expect.isTrue "the largest int32"
      accepted source (LiteralValue.Integer -2147483648L) |> Expect.isTrue "the smallest int32"
      refusedAsOutOfRange source (LiteralValue.Integer 2147483648L) |> Expect.isTrue "one past the largest"
      refusedAsOutOfRange source (LiteralValue.Integer -2147483649L) |> Expect.isTrue "one before the smallest"

    testCase "a byte (uy) holds 0 to 255" <| fun _ ->
      let source = "module M\nlet x = 2uy\n"
      accepted source (LiteralValue.Integer 255L) |> Expect.isTrue "255"
      accepted source (LiteralValue.Integer 0L) |> Expect.isTrue "0"
      refusedAsOutOfRange source (LiteralValue.Integer 256L) |> Expect.isTrue "256"
      refusedAsOutOfRange source (LiteralValue.Integer -1L) |> Expect.isTrue "-1"

    testCase "sbyte, int16, uint16 and uint32 keep their own edges" <| fun _ ->
      refusedAsOutOfRange "module M\nlet x = 1y\n" (LiteralValue.Integer 128L) |> Expect.isTrue "sbyte 128"
      accepted "module M\nlet x = 1y\n" (LiteralValue.Integer -128L) |> Expect.isTrue "sbyte -128"
      refusedAsOutOfRange "module M\nlet x = 1s\n" (LiteralValue.Integer 32768L) |> Expect.isTrue "int16 32768"
      accepted "module M\nlet x = 1s\n" (LiteralValue.Integer 32767L) |> Expect.isTrue "int16 32767"
      refusedAsOutOfRange "module M\nlet x = 1us\n" (LiteralValue.Integer 65536L) |> Expect.isTrue "uint16 65536"
      refusedAsOutOfRange "module M\nlet x = 1us\n" (LiteralValue.Integer -1L) |> Expect.isTrue "uint16 -1"
      accepted "module M\nlet x = 1u\n" (LiteralValue.Integer 4294967295L) |> Expect.isTrue "uint32 max"
      refusedAsOutOfRange "module M\nlet x = 1u\n" (LiteralValue.Integer 4294967296L) |> Expect.isTrue "uint32 past max"

    testCase "an int64 (L) holds every int64, and an unsigned 64 (UL) holds no negative" <| fun _ ->
      accepted "module M\nlet x = 1L\n" (LiteralValue.Integer System.Int64.MaxValue) |> Expect.isTrue "int64 max"
      accepted "module M\nlet x = 1L\n" (LiteralValue.Integer System.Int64.MinValue) |> Expect.isTrue "int64 min"
      refusedAsOutOfRange "module M\nlet x = 1UL\n" (LiteralValue.Integer -1L) |> Expect.isTrue "uint64 -1"

    testCase "hex, octal and binary have no negative spelling, so a negative number is refused there" <| fun _ ->
      refusedAsOutOfRange "module M\nlet x = 0x1F\n" (LiteralValue.Integer -5L) |> Expect.isTrue "hex"
      refusedAsOutOfRange "module M\nlet x = 0o17\n" (LiteralValue.Integer -5L) |> Expect.isTrue "octal"
      refusedAsOutOfRange "module M\nlet x = 0b101\n" (LiteralValue.Integer -5L) |> Expect.isTrue "binary"

    testCase "a float is always a finite number, and NaN and infinity are not spellable" <| fun _ ->
      let source = "module M\nlet x = 1.0\n"
      refusedAsOutOfRange source (LiteralValue.Real nan) |> Expect.isTrue "NaN"
      refusedAsOutOfRange source (LiteralValue.Real infinity) |> Expect.isTrue "infinity"
      refusedAsOutOfRange source (LiteralValue.Real -infinity) |> Expect.isTrue "minus infinity"
      accepted source (LiteralValue.Real System.Double.MaxValue) |> Expect.isTrue "the largest double"

    testCase "a float32 (f) and a decimal (m) keep their own range" <| fun _ ->
      refusedAsOutOfRange "module M\nlet x = 1.0f\n" (LiteralValue.Real 1e39) |> Expect.isTrue "float32 past 3.4e38"
      accepted "module M\nlet x = 1.0f\n" (LiteralValue.Real 3.0e38) |> Expect.isTrue "float32 inside"
      refusedAsOutOfRange "module M\nlet x = 1.0m\n" (LiteralValue.Real 1e29) |> Expect.isTrue "decimal past 7.9e28"
      accepted "module M\nlet x = 1.0m\n" (LiteralValue.Real 1e28) |> Expect.isTrue "decimal inside"

    testProperty "PROPERTY, a write to a plain int either lands inside the int32 range or is refused, and never writes text outside it" <|
      fun (m: int64) ->
        let source = "module M\nlet x = 100\n"
        match setLiteral source (addr "x") (LiteralValue.Integer m) with
        | Ok newSource ->
          match readLiteral newSource (addr "x") with
          | Ok lit ->
            match lit.Value with
            | LiteralValue.Integer v -> v = m && m >= -2147483648L && m <= 2147483647L
            | _ -> false
          | Error _ -> false
        | Error(SetLiteralError.OutOfRange _) -> m < -2147483648L || m > 2147483647L
        | Error _ -> false
  ]
