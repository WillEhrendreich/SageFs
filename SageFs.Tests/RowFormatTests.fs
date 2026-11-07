/// How a row shows its value: as it is, in hex, in binary, as a colour, or on a range. The set is closed, one function formats, and the
/// function that reads a number back is its inverse, so a number shown in hex or binary reads back as the number it was.
module SageFs.Tests.RowFormatTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.Tweak.RowFormatting

/// Each whole-number type with its smallest and largest value, as text.
let private integerTypes : (string * string * string) list =
  [ "Byte", "0", "255"
    "SByte", "-128", "127"
    "Int16", "-32768", "32767"
    "UInt16", "0", "65535"
    "Int32", "-2147483648", "2147483647"
    "UInt32", "0", "4294967295"
    "Int64", "-9223372036854775808", "9223372036854775807"
    "UInt64", "0", "18446744073709551615" ]

let private refusalOf (formatted: Formatted) : FormatRefusal =
  match formatted with
  | Formatted.CannotShow refusal -> refusal
  | Formatted.Text _
  | Formatted.Swatch _
  | Formatted.Meter _ -> failtestf "expected a refusal, got %A" formatted

[<Tests>]
let tests =
  testList "how a row shows its value" [

    testCase "the choices are closed: each has its own token and label, the token reads back, and anything else is refused with the set" <| fun _ ->
      FormatChoice.all.Length |> Expect.equal "five ways to show a value" 5
      (FormatChoice.all |> List.map FormatChoice.token |> List.distinct).Length |> Expect.equal "distinct tokens" FormatChoice.all.Length
      (FormatChoice.all |> List.map FormatChoice.label |> List.distinct).Length |> Expect.equal "distinct labels" FormatChoice.all.Length
      for choice in FormatChoice.all do
        FormatChoice.ofToken (FormatChoice.token choice) |> Expect.equal "round trip" (Ok choice)
      match FormatChoice.ofToken "octal" with
      | Error why -> ConfigError.describe why |> Expect.stringContains "names the word" "octal"
      | Ok choice -> failtestf "'octal' read as %A" choice

    testCase "the digits of the values people write: 255 is 0xFF, 5 is 0b101, a negative number is its two's complement in the type's width" <| fun _ ->
      format RowFormat.Hex "Int32" "255" |> Expect.equal "hex" (Formatted.Text "0xFF")
      format RowFormat.Hex "Int32" "0" |> Expect.equal "zero" (Formatted.Text "0x0")
      format RowFormat.Hex "Int32" "-1" |> Expect.equal "-1 as Int32" (Formatted.Text "0xFFFFFFFF")
      format RowFormat.Hex "SByte" "-1" |> Expect.equal "-1 as SByte" (Formatted.Text "0xFF")
      format RowFormat.Binary "Byte" "5" |> Expect.equal "binary" (Formatted.Text "0b101")
      format RowFormat.Binary "Int32" "0" |> Expect.equal "zero" (Formatted.Text "0b0")
      format RowFormat.Binary "SByte" "-2" |> Expect.equal "-2 as SByte" (Formatted.Text "0b11111110")
      format RowFormat.AsIs "Double" "9.8" |> Expect.equal "as is" (Formatted.Text "9.8")

    testCase "a colour is a whole number up to 0xFFFFFF, drawn as its #rrggbb with a swatch; beyond that it is refused with the range" <| fun _ ->
      format RowFormat.Colour "Int32" "16711680" |> Expect.equal "red" (Formatted.Swatch("#FF0000", "#ff0000"))
      format RowFormat.Colour "Int32" "255" |> Expect.equal "blue" (Formatted.Swatch("#0000FF", "#0000ff"))
      format RowFormat.Colour "Int32" "16777216" |> refusalOf |> Expect.equal "past white" (FormatRefusal.ColourOutOfRange "16777216")
      format RowFormat.Colour "Int32" "-1" |> refusalOf |> Expect.equal "below black" (FormatRefusal.ColourOutOfRange "-1")
      FormatRefusal.describe (FormatRefusal.ColourOutOfRange "16777216") |> Expect.stringContains "says the range" "16777215"

    testCase "a range draws how far along it a value is, and refuses a value outside it or a text that is not a number" <| fun _ ->
      format (RowFormat.Slider(0.0, 100.0)) "Double" "25" |> Expect.equal "a quarter" (Formatted.Meter("25", 0.25))
      format (RowFormat.Slider(-1.0, 1.0)) "Double" "0" |> Expect.equal "the middle" (Formatted.Meter("0", 0.5))
      format (RowFormat.Slider(0.0, 1.0)) "Double" "2" |> refusalOf |> Expect.equal "outside" (FormatRefusal.OutsideRange("2", 0.0, 1.0))
      format (RowFormat.Slider(0.0, 1.0)) "String" "abc" |> refusalOf |> Expect.equal "not a number" (FormatRefusal.NotANumber "abc")
      FormatRefusal.describe (FormatRefusal.OutsideRange("2", 0.0, 1.0)) |> Expect.stringContains "names the range" "0 to 1"

    testCase "WHY — a format that does not apply to the value says so in words instead of drawing nonsense" <| fun _ ->
      for rowFormat, choice in [ RowFormat.Hex, FormatChoice.Hex; RowFormat.Binary, FormatChoice.Binary; RowFormat.Colour, FormatChoice.Colour ] do
        format rowFormat "Double" "9.8" |> refusalOf |> Expect.equal "a Double has no digits" (FormatRefusal.NeedsWholeNumber(choice, "Double"))
        format rowFormat "Int32" "ten" |> refusalOf |> Expect.equal "text is not a number" (FormatRefusal.NotAWholeNumber(choice, "ten"))
      FormatRefusal.describe (FormatRefusal.NeedsWholeNumber(FormatChoice.Hex, "Double")) |> Expect.stringContains "names the type" "Double"
      FormatRefusal.describe (FormatRefusal.NotAWholeNumber(FormatChoice.Hex, "ten")) |> Expect.stringContains "names the value" "ten"

    testCase "at the ends of every whole-number type, hex and binary read back as the number they were" <| fun _ ->
      for typeName, smallest, largest in integerTypes do
        for value in [ smallest; "0"; "1"; largest ] do
          for rowFormat in [ RowFormat.Hex; RowFormat.Binary ] do
            match format rowFormat typeName value with
            | Formatted.Text spelled -> unformat rowFormat typeName spelled |> Expect.equal (sprintf "%s %A %s" typeName rowFormat value) (Ok value)
            | other -> failtestf "%s %s should be drawn in %A: %A" typeName value rowFormat other

    testProperty "WHY — over any Int64, hex and binary read back as that number" <| fun (n: int64) ->
      [ RowFormat.Hex; RowFormat.Binary ]
      |> List.forall (fun rowFormat ->
        match format rowFormat "Int64" (string n) with
        | Formatted.Text spelled -> unformat rowFormat "Int64" spelled = Ok (string n)
        | Formatted.Swatch _
        | Formatted.Meter _
        | Formatted.CannotShow _ -> false)

    testProperty "WHY — over any Int32, hex and binary read back as that number" <| fun (n: int) ->
      [ RowFormat.Hex; RowFormat.Binary ]
      |> List.forall (fun rowFormat ->
        match format rowFormat "Int32" (string n) with
        | Formatted.Text spelled -> unformat rowFormat "Int32" spelled = Ok (string n)
        | Formatted.Swatch _
        | Formatted.Meter _
        | Formatted.CannotShow _ -> false)

    testProperty "WHY — over any 24-bit number, a colour reads back as that number" <| fun (n: uint16) (m: byte) ->
      let value = int n * 256 + int m
      match format RowFormat.Colour "Int32" (string value) with
      | Formatted.Swatch(text, _) -> unformat RowFormat.Colour "Int32" text = Ok (string value)
      | Formatted.Text _
      | Formatted.Meter _
      | Formatted.CannotShow _ -> false

    testCase "text that is not in the format, or too wide for the type, is refused with the reason" <| fun _ ->
      unformat RowFormat.Hex "Byte" "0x1FF" |> Expect.equal "too wide for a Byte" (Error(FormatRefusal.DoesNotFit("0x1FF", 8)))
      unformat RowFormat.Hex "Int32" "FF" |> Expect.equal "no prefix" (Error(FormatRefusal.MissingPrefix("FF", "0x")))
      unformat RowFormat.Hex "Int32" "0xZZ" |> Expect.equal "not hex digits" (Error(FormatRefusal.NotDigits("0xZZ", "0x")))
      unformat RowFormat.Hex "Int32" "0x" |> Expect.equal "no digits" (Error(FormatRefusal.NotDigits("0x", "0x")))
      unformat RowFormat.Binary "Int32" "0b102" |> Expect.equal "a 2 is not a bit" (Error(FormatRefusal.NotDigits("0b102", "0b")))
      unformat RowFormat.Hex "Double" "0x10" |> Expect.equal "a Double has no hex" (Error(FormatRefusal.NeedsWholeNumber(FormatChoice.Hex, "Double")))
      unformat RowFormat.Colour "Int32" "#FF0000" |> Expect.equal "a colour reads back" (Ok "16711680")
      unformat RowFormat.AsIs "Double" "9.8" |> Expect.equal "as is reads as it is" (Ok "9.8")

    testCase "what a type can be shown as: whole numbers get the four spellings, a range only when the row has one, and anything else is as it is" <| fun _ ->
      for typeName, _, _ in integerTypes do
        offered typeName None |> Expect.equal (typeName + " without a range") [ FormatChoice.AsIs; FormatChoice.Hex; FormatChoice.Binary; FormatChoice.Colour ]
        offered typeName (Some(0.0, 1.0)) |> List.contains FormatChoice.Slider |> Expect.isTrue (typeName + " with a range")
      offered "Double" None |> Expect.equal "a Double has only itself without a range" [ FormatChoice.AsIs ]
      offered "Double" (Some(0.0, 10.0)) |> Expect.equal "and a range with one" [ FormatChoice.AsIs; FormatChoice.Slider ]
      for typeName in [ "String"; "Boolean"; "Char"; "BigInteger" ] do
        offered typeName (Some(0.0, 1.0)) |> Expect.equal (typeName + " is as it is") [ FormatChoice.AsIs ]

    testCase "WHY — a pick that no longer applies is the value as it is: the row never fails to draw because of an earlier choice" <| fun _ ->
      for choice in FormatChoice.all do
        for typeName in [ "Int32"; "Double"; "String" ] do
          for range in [ None; Some(0.0, 10.0) ] do
            match List.contains choice (offered typeName range), resolve choice typeName range with
            | false, RowFormat.AsIs -> ()
            | false, other -> failtestf "%A on %s (range %A) is not offered but resolved to %A" choice typeName range other
            | true, _ -> ()
      resolve FormatChoice.Slider "Double" (Some(1.0, 5.0)) |> Expect.equal "the range is the row's" (RowFormat.Slider(1.0, 5.0))
      resolve FormatChoice.Hex "Double" None |> Expect.equal "hex on a Double is as it is" RowFormat.AsIs
      resolve FormatChoice.Hex "Int32" None |> Expect.equal "hex on an Int32 is hex" RowFormat.Hex
      FormatChoice.ofFormat (RowFormat.Slider(1.0, 5.0)) |> Expect.equal "a slider is the slider choice" FormatChoice.Slider
  ]
