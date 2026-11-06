/// The dashboard draws its icons as monochrome text glyphs. A colour emoji keeps its own colours and size whatever the theme or the
/// font does, and the reviewers named the mix of emoji and glyphs as the most repeated polish fault. These cases hold the set closed:
/// the `Glyph` type is the one place an icon comes from, none of its spellings is an emoji, and no source that draws the page
/// writes one itself.
module SageFs.Tests.DashboardGlyphTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server

/// Code points that draw as a colour emoji by default, or that ask for one (the emoji variation selector).
let private isEmoji (codePoint: int) : bool =
  let inRange lo hi = codePoint >= lo && codePoint <= hi
  inRange 0x1F000 0x1FAFF
  || codePoint = 0xFE0F
  || inRange 0x231A 0x231B || inRange 0x23E9 0x23F3 || inRange 0x23F8 0x23FA
  || inRange 0x25FD 0x25FE || inRange 0x2614 0x2615 || inRange 0x2648 0x2653
  || codePoint = 0x267F || codePoint = 0x2693 || codePoint = 0x26A0 || codePoint = 0x26A1
  || inRange 0x26AA 0x26AB || inRange 0x26BD 0x26BE || inRange 0x26C4 0x26C5 || codePoint = 0x26CE || codePoint = 0x26D4
  || codePoint = 0x26EA || inRange 0x26F2 0x26F3 || codePoint = 0x26F5 || codePoint = 0x26FA || codePoint = 0x26FD
  || codePoint = 0x2705 || inRange 0x270A 0x270B || codePoint = 0x2728 || codePoint = 0x274C || codePoint = 0x274E
  || inRange 0x2753 0x2755 || codePoint = 0x2757 || inRange 0x2795 0x2797 || codePoint = 0x27B0 || codePoint = 0x27BF
  || inRange 0x2B1B 0x2B1C || codePoint = 0x2B50 || codePoint = 0x2B55
  // Text by default in the standard, and drawn as a colour emoji by the font fallback on a machine with no text face for them (the
  // gear and the heavy cross did, in the browser the dashboard was reviewed in).
  || codePoint = 0x2699 || codePoint = 0x2692 || codePoint = 0x2716 || codePoint = 0x2714 || codePoint = 0x2764 || codePoint = 0x270D || codePoint = 0x2712

let private emojiIn (text: string) : string list =
  let found = ResizeArray<string>()
  let mutable i = 0
  while i < text.Length do
    let codePoint = Char.ConvertToUtf32(text, i)
    match isEmoji codePoint with
    | true -> found.Add(sprintf "U+%04X" codePoint)
    | false -> ()
    i <- i + (match Char.IsHighSurrogate text.[i] with | true -> 2 | false -> 1)
  found |> Seq.toList

let private sourceDirectory : string = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs"))

let private coreDirectory : string = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Core"))

/// The files that draw the dashboard, as full paths: its F# (every file that renders a part of the page), its stylesheet, and the
/// sources of the lines the Output pane prints, which are the first thing on the page.
let private drawingSources : string list =
  let inSageFs (pattern: string) = Directory.GetFiles(sourceDirectory, pattern) |> Array.toList
  [ yield! inSageFs "Dashboard*.fs"
    yield! inSageFs "LiveBindings*.fs"
    yield! inSageFs "NewSession*.fs"
    yield! [ "DockPanes.fs"; "EvaluatePopover.fs"; "SettingsPanel.fs"; "WarmupBanner.fs"; "SageFsApp.fs"; "dashboard.css" ] |> List.map (fun name -> Path.Combine(sourceDirectory, name))
    yield Path.Combine(coreDirectory, "WarmupContext.fs") ]

[<Tests>]
let tests =
  testList "dashboard icons are monochrome glyphs" [
    testCase "WHY — no icon in the set is a colour emoji, so each takes the colour of the text beside it" <| fun () ->
      Glyphs.all
      |> List.collect (fun glyph -> emojiIn (Glyphs.text glyph) |> List.map (fun found -> sprintf "%A is %s" glyph found))
      |> Expect.isEmpty "every glyph is a character with no emoji presentation"

    testCase "WHY — two meanings never share a glyph, so an icon learned once reads the same everywhere" <| fun () ->
      let spellings = Glyphs.all |> List.map Glyphs.text
      spellings |> List.distinct |> List.length |> Expect.equal "every glyph has its own spelling" spellings.Length

    testCase "WHY — the set lists every case, so a glyph added to the type cannot be left out of the walks" <| fun () ->
      let cases = FSharp.Reflection.FSharpType.GetUnionCases typeof<Glyph>
      Glyphs.all |> List.length |> Expect.equal "all names every union case" cases.Length

    testCase "WHY — the sources that draw the page write no emoji of their own, they ask Glyph" <| fun () ->
      drawingSources
      |> List.collect (fun path ->
        File.ReadAllText path
        |> emojiIn
        |> List.distinct
        |> List.map (fun found -> sprintf "%s holds %s" (Path.GetFileName path) found))
      |> Expect.isEmpty "no colour emoji in a file that draws the dashboard"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
