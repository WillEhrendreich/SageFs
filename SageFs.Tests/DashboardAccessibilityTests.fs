/// The accessibility basics of the dashboard shell: enough contrast for the dim text the page uses for every secondary
/// line, a way for a person who asks their system for less motion to get it, and banners a screen reader is told about.
module SageFs.Tests.DashboardAccessibilityTests

open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server

/// WCAG 2.x AA for normal-size text. The page sets its secondary lines at 0.65 to 0.75 rem, so the large-text allowance
/// (3:1) does not apply.
let private minimumContrast = 4.5

/// The `--name: #rrggbb` the stylesheet declares in its first (default theme) block.
let private declaredColour (name: string) : string =
  let m = Regex.Match(Dashboard.dashboardCss, sprintf @"%s:\s*#([0-9a-fA-F]{6})" (Regex.Escape name))
  match m.Success with
  | true -> m.Groups.[1].Value
  | false -> failwithf "the stylesheet declares no %s" name

/// Relative luminance of an sRGB colour, as WCAG defines it.
let private luminance (hex: string) : float =
  let channel (offset: int) =
    let c = float (Convert.ToInt32(hex.Substring(offset, 2), 16)) / 255.0
    match c <= 0.03928 with
    | true -> c / 12.92
    | false -> Math.Pow((c + 0.055) / 1.055, 2.4)
  0.2126 * channel 0 + 0.7152 * channel 2 + 0.0722 * channel 4

let private contrast (foreground: string) (background: string) : float =
  let a, b = luminance foreground, luminance background
  (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05)

/// The body of the first `@media (prefers-reduced-motion: reduce) { ... }` rule, braces matched.
let private reducedMotionBlock () : string option =
  let css = Dashboard.dashboardCss
  let start = css.IndexOf "@media (prefers-reduced-motion: reduce)"
  match start with
  | -1 -> None
  | _ ->
    let open' = css.IndexOf('{', start)
    let mutable depth = 0
    let mutable finish = open'
    let mutable fin = false
    while not fin && finish < css.Length do
      match css.[finish] with
      | '{' -> depth <- depth + 1
      | '}' ->
        depth <- depth - 1
        fin <- depth = 0
      | _ -> ()
      finish <- finish + 1
    Some (css.Substring(open', finish - open'))

[<Tests>]
let tests =
  testList "Dashboard accessibility basics" [

    testCase "the contrast helper has teeth: the old dim colour fails on the panel and white-on-black passes" <| fun _ ->
      (contrast "727169" "2a2a37" < minimumContrast) |> Expect.isTrue "#727169 on #2a2a37 is 2.88:1 and must be flagged"
      (contrast "ffffff" "000000" > 20.0) |> Expect.isTrue "the maximum contrast is 21:1"

    testCase "WHY — --fg-dim reaches 4.5:1 on the page, the panel and the selection, because every secondary line is set in it at 0.65 to 0.75 rem" <| fun _ ->
      let dim = declaredColour "--fg-dim"
      for background in [ "--bg-default"; "--bg-panel"; "--bg-selection" ] do
        let ratio = contrast dim (declaredColour background)
        (ratio >= minimumContrast)
        |> Expect.isTrue (sprintf "#%s on %s is %.2f:1, below %.1f:1" dim background ratio minimumContrast)

    testCase "WHY — --fg-dim stays dimmer than the body text, because lifting it to default would flatten the hierarchy the dim colour exists for" <| fun _ ->
      let dim, body = luminance (declaredColour "--fg-dim"), luminance (declaredColour "--fg-default")
      (dim < body) |> Expect.isTrue "dim text is still darker than default text"

    testCase "WHY — a reduced-motion preference stops every animation, because the connection pulse repeats for as long as the daemon is away" <| fun _ ->
      match reducedMotionBlock () with
      | None -> failtest "the stylesheet has no prefers-reduced-motion rule"
      | Some block ->
        block |> Expect.stringContains "it applies to every element" "*"
        // Not `animation: none`: the tooltip fade-in ends on `forwards` at opacity 1, so removing it would leave every
        // tooltip invisible. A one-tick, one-iteration animation still lands on its end state.
        block |> Expect.stringContains "an animation that repeats runs once" "animation-iteration-count: 1 !important"
        block |> Expect.stringContains "and runs for one tick, still arriving at its end state" "animation-duration: 0.01ms !important"
        block |> Expect.stringContains "without waiting to start" "animation-delay: 0s !important"
        block |> Expect.stringContains "transitions are cut the same way" "transition-duration: 0.01ms !important"
        block |> Expect.stringContains "and smooth scrolling is off" "scroll-behavior: auto !important"

    testCase "WHY — the hard banner is an alert, because a screen reader is otherwise never told the daemon went away" <| fun _ ->
      DashboardConnection.hardBanner ()
      |> renderNode
      |> Expect.stringContains "the daemon-not-running banner announces itself assertively" "role=\"alert\""

    testCase "WHY — the soft banner is a status, because paused live updates are worth saying but not worth interrupting for" <| fun _ ->
      DashboardConnection.softBanner ()
      |> renderNode
      |> Expect.stringContains "the paused-updates banner announces itself politely" "role=\"status\""
  ]
