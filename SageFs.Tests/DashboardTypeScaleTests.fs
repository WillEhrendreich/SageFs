/// Text on the dashboard is sized in units that follow the reader: `rem` and `em` follow the browser's own font size, so a person who
/// raises it, or who swaps the font, gets a page that grows with it. A size written in pixels does not follow, and the small helper
/// text was the first thing reviewers called too small to read. The stylesheet and the inline styles in the F# that renders the page
/// say every font size in `rem`, `em` or a percentage, and these cases count the ones that still say `px`.
module SageFs.Tests.DashboardTypeScaleTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs.Server

/// A `font-size` (or the `--font-size` token) whose value contains a pixel length.
let private pixelFontSize = Regex(@"font-size\s*:[^;{}""]*\d\s*px", RegexOptions.Compiled)

let private sourceDirectory : string = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs"))

/// The F# files that draw the dashboard, which carry inline `style` strings.
let private renderingSources : string list =
  [ "Dashboard.fs"; "DashboardFragments.fs"; "LiveBindingsDock.fs"; "LiveBindingsTweakView.fs"; "LiveBindingsViewPanel.fs"; "WarmupBanner.fs" ]

let private pixelSizesIn (text: string) : string list =
  pixelFontSize.Matches text |> Seq.map (fun m -> m.Value) |> Seq.toList

[<Tests>]
let tests =
  testList "dashboard text sizes follow the reader's font size" [
    testCase "WHY — the stylesheet says no font size in pixels, so a larger browser font or a zoom reaches all of its text" <| fun () ->
      pixelSizesIn Dashboard.dashboardCss
      |> Expect.isEmpty "every font-size in dashboard.css is rem, em or a percentage"

    testCase "WHY — the inline styles in the rendering code say no font size in pixels either" <| fun () ->
      renderingSources
      |> List.collect (fun name -> pixelSizesIn (File.ReadAllText(Path.Combine(sourceDirectory, name))) |> List.map (fun found -> name + ": " + found))
      |> Expect.isEmpty "no inline font-size in the dashboard's F# is in pixels"

    testCase "WHY — the layout breakpoints are in em and rem, so a larger font changes layout where its text stops fitting, not at a pixel count" <| fun () ->
      Dashboard.dashboardCss.Split('\n')
      |> Array.filter (fun line -> (line.TrimStart().StartsWith "@media" || line.TrimStart().StartsWith "@container") && line.Contains "px")
      |> Array.toList
      |> Expect.isEmpty "every @media and @container condition is a relative length"

    testCase "WHY — the default root size makes the smallest text 13px, because reviewers kept calling 12px helper text too small" <| fun () ->
      let root = Regex(@"--font-size:\s*([\d.]+)%").Match Dashboard.dashboardCss
      root.Success |> Expect.isTrue "the stylesheet sets the --font-size token"
      (float root.Groups.[1].Value / 100.0 * 16.0 * 0.857 > 12.8) |> Expect.isTrue "0.857rem is at least 12.8px at the default root"

    testCase "WHY — no text size is under the 0.857rem floor, so the root token alone decides how small the smallest text is" <| fun () ->
      // 0.857rem is the floor. A size under it is a size nobody asked for.
      let small = Regex(@"font-size\s*:\s*0?\.(\d+)rem")
      let tooSmall (text: string) =
        small.Matches text
        |> Seq.map (fun m -> m.Value, float ("0." + m.Groups.[1].Value))
        |> Seq.filter (fun (_, rem) -> rem < 0.856)
        |> Seq.map fst
        |> Seq.toList
      tooSmall Dashboard.dashboardCss |> Expect.isEmpty "dashboard.css has no font-size under the floor"
      renderingSources
      |> List.collect (fun name -> tooSmall (File.ReadAllText(Path.Combine(sourceDirectory, name))) |> List.map (fun found -> name + ": " + found))
      |> Expect.isEmpty "the inline styles have none either"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
