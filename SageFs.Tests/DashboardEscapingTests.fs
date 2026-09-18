module SageFs.Tests.DashboardEscapingTests

open System
open System.IO
open System.Net
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server.DashboardFragments

// Dashboard XSS sinks: session ids, working directories, project names,
// status text and error messages are all runtime strings (user- or agent-
// controlled). Falco.Markup's `Text.raw` and attribute values are emitted
// VERBATIM, so every runtime string must go through `textEnc` (text) or
// `attrEnc` (attribute values) before it reaches the DOM. These tests pin the
// rule both behaviorally (properties over hostile strings) and structurally (a
// source scan that fails when a new runtime-string `Text.raw` sink lands).

/// Payloads covering the three breakout classes: element injection, raw
/// markup, and double/single-quoted attribute breakout.
let private payloads = [|
  "\"><script>alert(1)</script>"
  "<img src=x onerror=alert(1)>"
  "x\" onmouseover=\"alert(1)"
  "' onfocus='alert(1)"
  "&amp;<b>bold</b>&"
|]

let private orEmpty (s: string) = match String.IsNullOrEmpty s with | true -> "" | false -> s

let private hostile (prefix: string) (pick: int) =
  let n = payloads.Length
  orEmpty prefix + payloads.[((pick % n) + n) % n]

/// No hostile payload may survive as live markup in the rendered fragment.
let private expectInert (what: string) (html: string) =
  html.Contains("<script", StringComparison.OrdinalIgnoreCase)
  |> Expect.isFalse (sprintf "%s must not contain a live <script> element" what)
  html.Contains("<img", StringComparison.OrdinalIgnoreCase)
  |> Expect.isFalse (sprintf "%s must not contain a live <img> element" what)
  html.Contains("<b>", StringComparison.Ordinal)
  |> Expect.isFalse (sprintf "%s must not contain injected <b> markup" what)
  html.Contains("\" onmouseover=\"", StringComparison.Ordinal)
  |> Expect.isFalse (sprintf "%s must not allow a double-quoted attribute breakout" what)
  html.Contains("' onfocus='", StringComparison.Ordinal)
  |> Expect.isFalse (sprintf "%s must not allow a single-quoted attribute breakout" what)

[<Tests>]
let dashboardEscapingEncoderTests =
  testList "Dashboard escaping.encoder" [

    testProperty "htmlEscape output never contains a raw < > \" or '" <| fun (s: string) ->
      let escaped = htmlEscape (orEmpty s)
      escaped.IndexOfAny([| '<'; '>'; '"'; '\'' |])
      |> Expect.equal "escaped text must contain no markup-significant character" -1

    testProperty "htmlEscape round-trips through an HTML decoder" <| fun (s: string) ->
      let s = orEmpty s
      htmlEscape s |> WebUtility.HtmlDecode
      |> Expect.equal "decoding the escaped text must give back the original" s

    testCase "htmlEscape leaves benign Unicode byte-identical" <| fun _ ->
      htmlEscape "🟢 Healthy · SageFs é ⏳ /home/ü"
      |> Expect.equal "emoji, middle dot and accents must pass through unchanged" "🟢 Healthy · SageFs é ⏳ /home/ü"
  ]

[<Tests>]
let dashboardEscapingRenderTests =
  testList "Dashboard escaping.render" [

    testProperty "renderSessionStatus never emits a hostile session id, dir, progress or workflow raw" <| fun (prefix: string) (pick: int) ->
      let h = hostile prefix pick
      renderSessionStatus "Ready" h h h h |> renderNode
      |> expectInert "renderSessionStatus (Ready)"
      renderSessionStatus "WarmingUp" h h h h |> renderNode
      |> expectInert "renderSessionStatus (WarmingUp)"

    testProperty "renderSessionStatus never emits a hostile session state raw" <| fun (prefix: string) (pick: int) ->
      let h = hostile prefix pick
      renderSessionStatus h "abcd1234" "/work" "" "REPL" |> renderNode
      |> expectInert "renderSessionStatus (hostile state)"

    testProperty "renderSessionStatus keeps the working dir, encoded once, in text and data attribute" <| fun (prefix: string) (pick: int) ->
      let h = hostile prefix pick
      let html = renderSessionStatus "Ready" "abcd1234" h "" "REPL" |> renderNode
      html
      |> Expect.stringContains "data-working-dir must carry the escaped dir"
           (sprintf "data-working-dir=\"%s\"" (htmlEscape h))
      html
      |> Expect.stringContains "the CWD text must carry the escaped dir"
           (sprintf "CWD: %s" (htmlEscape h))

    testProperty "renderStatuslineLeft (session switch patch) never emits a hostile state or dir raw" <| fun (prefix: string) (pick: int) ->
      let h = hostile prefix pick
      let html = renderStatuslineLeft h h |> renderNode
      html |> expectInert "renderStatuslineLeft"
      html
      |> Expect.stringContains "the statusline must show the working dir escaped exactly once"
           (sprintf "<div id=\"statusline-file\" class=\"statusline-file\">%s</div>" (htmlEscape h))

    testProperty "evalResultError never emits a hostile message raw" <| fun (prefix: string) (pick: int) ->
      let h = hostile prefix pick
      let html = evalResultError h |> renderNode
      html |> expectInert "evalResultError"
      html
      |> Expect.stringContains "evalResultError must show the message escaped exactly once"
           (htmlEscape h)
  ]

// ─── Hot-reload controls: Datastar wiring, not raw onclick/fetch ─────
// roast-6 #9: Watch All / Unwatch All / per-directory / per-file toggles
// used to be `Attr.create "onclick" (...fetch...)` — no Ds.indicator
// feedback, response dropped, bypassing Datastar entirely. These pin the
// Ds.onClick/Ds.indicator wiring and, since the toggle rows carry runtime
// file paths and directories, that the same hostile-payload strings stay
// inert in the new `data-on:click` attribute.

[<Tests>]
let dashboardEscapingHotReloadTests =
  testList "Dashboard escaping.hotreload" [

    testCase "renderHotReloadPanel wires every control through Datastar, never raw onclick" <| fun _ ->
      let html =
        renderHotReloadPanel "abcd1234"
          [ {| path = "src/A.fs"; watched = true |}; {| path = "src/Sub/B.fs"; watched = false |} ] 1
        |> renderNode
      html.Contains(" onclick=", StringComparison.Ordinal)
      |> Expect.isFalse "hot-reload controls must not use raw onclick (use Ds.onClick)"
      html.Contains("data-on:click=", StringComparison.Ordinal)
      |> Expect.isTrue "hot-reload controls must carry Datastar data-on:click"
      html.Contains("data-indicator:", StringComparison.Ordinal)
      |> Expect.isTrue "hot-reload controls must carry a Ds.indicator loading signal"

    testCase "renderHotReloadPanel posts to the unchanged hotreload endpoints and bodies" <| fun _ ->
      let html =
        renderHotReloadPanel "abcd1234"
          [ {| path = "src/A.fs"; watched = true |}; {| path = "src/Sub/B.fs"; watched = false |} ] 1
        |> renderNode
      html
      |> Expect.stringContains "watch-all endpoint unchanged" (htmlEscape "@post('/api/sessions/abcd1234/hotreload/watch-all')")
      html
      |> Expect.stringContains "unwatch-all endpoint unchanged" (htmlEscape "@post('/api/sessions/abcd1234/hotreload/unwatch-all')")
      html
      |> Expect.stringContains "toggle endpoint unchanged" (htmlEscape "@post('/api/sessions/abcd1234/hotreload/toggle')")
      html
      |> Expect.stringContains "watch-directory endpoint unchanged" (htmlEscape "@post('/api/sessions/abcd1234/hotreload/watch-directory')")
      html
      |> Expect.stringContains "per-file toggle still stages the path field the worker parses"
           (htmlEscape "$path = 'src/A.fs';")
      html
      |> Expect.stringContains "per-directory toggle still stages the directory field the worker parses"
           (htmlEscape "$directory = 'src/Sub/';")

    testProperty "renderHotReloadPanel never emits a hostile file path raw" <| fun (prefix: string) (pick: int) ->
      let h = hostile prefix pick
      renderHotReloadPanel "abcd1234" [ {| path = h; watched = false |} ] 0 |> renderNode
      |> expectInert "renderHotReloadPanel (file path)"

    testProperty "renderHotReloadPanel never emits a hostile directory raw" <| fun (prefix: string) (pick: int) ->
      let h = hostile prefix pick
      renderHotReloadPanel "abcd1234" [ {| path = h + "/File.fs"; watched = false |} ] 0 |> renderNode
      |> expectInert "renderHotReloadPanel (directory)"
  ]

// ─── Structural guard ───────────────────────────────────────────────
// Every `Text.raw` in the dashboard renderers must take a plain string
// literal. Anything computed at runtime goes through `textEnc`. The only
// exceptions are trusted sinks pinned by exact count — adding another one
// fails this test and forces a conscious decision.

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

/// A plain (non-triple-quoted) F# string literal immediately after Text.raw.
let private literalArg = Regex("""^\s+"(?:[^"\\]|\\.)*"(?!")""", RegexOptions.Compiled)

type private TrustedSink = {
  File: string
  /// Exact text that follows `Text.raw` on the line.
  Prefix: string
  Count: int
  Why: string
}

let private trustedSinks = [
  { File = "Dashboard.fs"; Prefix = " (sprintf \"\"\""; Count = 4
    Why = "inline <script> blocks; interpolate DomIds constants only (the old connectionMonitorScript fetch-monkeypatch block was removed for the SSE-heartbeat disconnect indicator)" }
  { File = "Dashboard.fs"; Prefix = " fontFaceCss"; Count = 1
    Why = "@font-face CSS built from constant weights and the embedded-font URLs only" }
  { File = "DashboardFragments.fs"; Prefix = " (sprintf \":root { %s }\" (Theme.toCssVariables config))"; Count = 1
    Why = "theme CSS variables built from ThemePresets" }
  { File = "DashboardFragments.fs"; Prefix = " (htmlEscape s)"; Count = 1
    Why = "textEnc — the single escaped-text constructor" }
  { File = "DashboardFragments.fs"; Prefix = " svg"; Count = 1
    Why = "territory-map SVG (CohortTerritory.toSvg): every runtime string it interpolates \
           (a claim's Label) is passed through its own private escapeXml before reaching the \
           template; every other interpolated field is a program-controlled float or a fixed \
           palette/stroke constant. Pinned here, not inlined as textEnc, because the value is \
           live <svg> markup that must render as an element, not escaped text — see \
           CohortTerritoryTests.fs's hostile-payload property for the behavioral proof." }
  { File = "DashboardFragments.fs"; Prefix = " lanesSvg"; Count = 1
    Why = "lane-view SVG (CohortLanes.toSvg, §6.5's span flames): every runtime string it \
           interpolates (a lane's member label, a span's Label) is passed through its own \
           private escapeXml before reaching the template, the same discipline as the \
           territory map's toSvg — see CohortLanesTests.fs's XSS-shaped-label tests for the \
           behavioral proof." }
]

let private rawSinks (file: string) =
  File.ReadAllLines(Path.Combine(repoRoot, "SageFs", file))
  |> Array.mapi (fun i line -> i + 1, line)
  // Comment lines (doc or line comments) describe sinks; they are not sinks.
  |> Array.filter (fun (_, line) -> not (line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
  |> Array.collect (fun (lineNo, line) ->
    Regex.Matches(line, @"Text\.raw\b")
    |> Seq.map (fun m -> lineNo, line.Substring(m.Index + m.Length))
    |> Seq.toArray)

[<Tests>]
let dashboardEscapingSourceTests =
  testList "Dashboard escaping.source" [
    for file in [ "Dashboard.fs"; "DashboardFragments.fs" ] do
      testCase (sprintf "%s: every Text.raw takes a string literal or is a pinned trusted sink" file) <| fun _ ->
        let sinks = rawSinks file
        let trustedHere = trustedSinks |> List.filter (fun t -> t.File = file)
        let isTrusted (rest: string) =
          trustedHere |> List.exists (fun t -> rest.StartsWith(t.Prefix, StringComparison.Ordinal))
        let offenders =
          sinks
          |> Array.filter (fun (_, rest) -> not (literalArg.IsMatch rest) && not (isTrusted rest))
          |> Array.map (fun (lineNo, rest) -> sprintf "%s:%d Text.raw%s" file lineNo rest)
        offenders
        |> Expect.isEmpty (
             sprintf "runtime strings must use textEnc, never Text.raw. Offenders:\n%s"
               (String.concat "\n" offenders))
        for t in trustedHere do
          sinks
          |> Array.filter (fun (_, rest) -> rest.StartsWith(t.Prefix, StringComparison.Ordinal))
          |> Array.length
          |> Expect.equal (sprintf "trusted sink count changed (%s) — review the new sink" t.Why) t.Count
  ]
