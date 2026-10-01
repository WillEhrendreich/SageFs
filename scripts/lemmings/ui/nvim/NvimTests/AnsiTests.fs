module NvimTests.AnsiTests

/// WHY: the design review reads a PNG drawn from `tmux capture-pane -e`, so the ANSI to HTML
/// step is the one place a screenshot can silently lie. It has two contracts: it is total (any
/// string in, a grid out) and it never changes the visible text. Both are pinned as properties
/// over generated terminal output, and the SGR cases it does understand are pinned as examples.
open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open LemDrive

let private esc = "\u001b"

type private Piece =
  | Plain of string
  | Sgr of int list
  | Newline
  | Noise of string

let private textPool =
  [ 'a'; 'Z'; '0'; ' '; '<'; '>'; '&'; '"'; '\''; ';'; 'l'; 't'; 'm'; '['; ']'; '\\'; '✓'; '✗'; '╭'; '│'; '▐'; '⚡' ]

let private plainText : Gen<string> =
  gen {
    let! chars = Gen.listOf (Gen.elements textPool)
    let! emoji = Gen.elements [ ""; "💤"; "👍" ]
    return String(Array.ofList chars) + emoji
  }

let private sgrParams : Gen<int list> =
  Gen.oneof
    [ Gen.listOf (Gen.choose (0, 110))
      Gen.map2 (fun n tail -> 38 :: 5 :: n :: tail) (Gen.choose (-5, 300)) (Gen.listOf (Gen.choose (0, 50)))
      Gen.map3 (fun r g b -> [ 48; 2; r; g; b ]) (Gen.choose (-20, 400)) (Gen.choose (0, 255)) (Gen.choose (0, 255)) ]

/// Escape sequences that are not colours. None of them may print anything.
let private noiseSequences =
  [ esc + "[2J"; esc + "[10;5H"; esc + "[?25l"; esc + "]0;a title\u0007"; esc + "]8;;http://x" + esc + "\\"; esc + "(B"; esc + "c" ]

let private piece : Gen<Piece> =
  Gen.frequency
    [ 6, Gen.map Plain plainText
      4, Gen.map Sgr sgrParams
      2, Gen.constant Newline
      1, Gen.map Noise (Gen.elements noiseSequences) ]

let private pieces : Gen<Piece list> = Gen.listOf piece

let private render (ps: Piece list) : string =
  ps
  |> List.map (function
    | Plain t -> t
    | Sgr codes -> esc + "[" + (codes |> List.map string |> String.concat ";") + "m"
    | Newline -> "\n"
    | Noise n -> n)
  |> String.concat ""

let private visibleText (ps: Piece list) : string =
  ps
  |> List.map (function
    | Plain t -> t
    | Newline -> "\n"
    | Sgr _
    | Noise _ -> "")
  |> String.concat ""

/// Any characters at all, with the ones that start escape sequences over-represented.
let private hostile : Gen<string> =
  Gen.elements [ '\u001b'; '['; ']'; 'm'; ';'; '0'; '1'; '3'; '8'; '5'; '2'; '\u0007'; '\\'; '\n'; '\t'; '\r'; 'a'; '<'; '&'; '\u0000'; '\u001f' ]
  |> Gen.listOf
  |> Gen.map (fun cs -> String(Array.ofList cs))

let private tag = Regex("<[^>]*>")

let private styleOfFirst (input: string) : Ansi.Style =
  (Ansi.parse input |> List.head |> List.head).Style

[<Tests>]
let properties =
  testList "Ansi to HTML (properties)" [
    testProperty "the visible text is exactly the input without its escape sequences"
      (Prop.forAll (Arb.fromGen pieces) (fun ps ->
        Ansi.plainText (Ansi.parse (render ps)) = visibleText ps))

    testProperty "the text of the HTML is the text of the grid (nothing is lost or invented by escaping)"
      (Prop.forAll (Arb.fromGen pieces) (fun ps ->
        let input = render ps
        Ansi.textOfHtml (Ansi.render input) = Ansi.plainText (Ansi.parse input)))

    testProperty "it is total: any string at all gives a grid, never an exception"
      (Prop.forAll (Arb.fromGen hostile) (fun s ->
        let grid = Ansi.parse s
        not grid.IsEmpty))

    testProperty "an input with N newlines is N + 1 rows"
      (Prop.forAll (Arb.fromGen pieces) (fun ps ->
        let input = render ps
        let newlines = input |> Seq.filter (fun c -> c = '\n') |> Seq.length
        (Ansi.parse input).Length = newlines + 1))

    testProperty "no angle bracket from the screen text survives unescaped into the HTML"
      (Prop.forAll (Arb.fromGen pieces) (fun ps ->
        let html = Ansi.render (render ps)
        let withoutTags = tag.Replace(html, "")
        not (withoutTags.Contains "<") && not (withoutTags.Contains ">")))

    testProperty "every colour it can write is a plain #rrggbb"
      (Prop.forAll (Arb.fromGen pieces) (fun ps ->
        let html = Ansi.render (render ps)
        let colours = Regex.Matches(html, "(?:color|background):([^;\"]+)")
        colours |> Seq.forall (fun m -> Regex.IsMatch(m.Groups.[1].Value, "^#[0-9a-f]{6}$"))))
  ]

[<Tests>]
let examples =
  testList "Ansi to HTML (examples)" [
    testCase "bold red text, then a reset" <| fun _ ->
      let grid = Ansi.parse (esc + "[1;31mX" + esc + "[0mY")
      let cells = List.head grid
      cells.[0].Style
      |> Expect.equal "the first cell is bold and palette red" { Ansi.plain with Fg = Ansi.Palette 1; Attrs = Set.ofList [ Ansi.Bold ] }
      cells.[1].Style |> Expect.equal "the reset clears everything" Ansi.plain

    testCase "256-colour and true-colour foregrounds and backgrounds" <| fun _ ->
      (styleOfFirst (esc + "[38;5;208mX")).Fg |> Expect.equal "palette 208" (Ansi.Palette 208)
      (styleOfFirst (esc + "[48;2;10;20;30mX")).Bg |> Expect.equal "true colour background" (Ansi.Rgb(10, 20, 30))
      (styleOfFirst (esc + "[38;2;1;2;999mX")).Fg |> Expect.equal "a channel above 255 is clamped" (Ansi.Rgb(1, 2, 255))

    testCase "bright colours map to palette 8 to 15" <| fun _ ->
      (styleOfFirst (esc + "[91mX")).Fg |> Expect.equal "91 is bright red" (Ansi.Palette 9)
      (styleOfFirst (esc + "[107mX")).Bg |> Expect.equal "107 is bright white background" (Ansi.Palette 15)

    testCase "an SGR code it does not know changes nothing and prints nothing" <| fun _ ->
      let grid = Ansi.parse (esc + "[999mX")
      Ansi.plainText grid |> Expect.equal "the text is still X" "X"
      (styleOfFirst (esc + "[999mX")) |> Expect.equal "the style is untouched" Ansi.plain

    testCase "an unterminated escape swallows the rest instead of printing it" <| fun _ ->
      Ansi.plainText (Ansi.parse ("ab" + esc + "[31")) |> Expect.equal "only ab is shown" "ab"

    testCase "reverse video swaps the colours in the HTML" <| fun _ ->
      let html = Ansi.render (esc + "[31;7mX")
      html |> Expect.stringContains "the background is the old foreground" "background:#cd3131"

    testCase "markup in the screen text is escaped" <| fun _ ->
      (Ansi.render "<script>alert('x')</script> & \"q\"").Contains "<script"
      |> Expect.isFalse "no script tag reaches the page"
      Ansi.render "a<b" |> Expect.stringContains "the < is an entity" "a&lt;b"

    testCase "an East Asian wide character and an emoji are drawn in a two-column box" <| fun _ ->
      Ansi.isWide "漢" |> Expect.isTrue "CJK is wide"
      Ansi.isWide "💤" |> Expect.isTrue "an emoji is wide"
      Ansi.isWide "a" |> Expect.isFalse "a letter is not"
      Ansi.isWide "✓" |> Expect.isFalse "a check mark is one column"

    testCase "a tab becomes one space and other control characters are dropped" <| fun _ ->
      Ansi.plainText (Ansi.parse ("a\tb\u0001c\r")) |> Expect.equal "tab is a space, controls are gone" "a bc"
  ]
