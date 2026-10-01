// HTML to PNG for the terminal screenshots, with Playwright.NET (the package SageFs.Tests
// already uses). One fixed monospace font stack, one fixed cell size and a dark background, so
// the same terminal state is always the same picture and a design review can compare them.
//
// The browser is headless and runs in the harness process, outside every sandbox. It is given
// an environment with no DISPLAY, WAYLAND_DISPLAY or XDG_RUNTIME_DIR, so it cannot reach the
// desktop session even by accident.
module LemDrive.Shot

open System
open System.Collections.Generic
open System.IO
open Microsoft.Playwright

/// What every screenshot is drawn with. Recorded beside each PNG.
module Look =
  let FontFamily = "'JetBrainsMono Nerd Font Mono', 'FiraCode Nerd Font Mono', 'Liberation Mono', monospace"
  let FontPx = 14
  let LineHeightPx = 18
  let PaddingPx = 8
  let ProbeCharacters = 10

type Rendered =
  { Png: string
    Columns: int
    Rows: int
    /// Measured width of one character cell, in pixels.
    CellWidthPx: float
    CellHeightPx: int
    ImageWidth: int
    ImageHeight: int
    FontFamily: string
    FontPx: int }

let private page (rowsHtml: string) (columns: int) : string =
  sprintf
    """<!doctype html><html><head><meta charset="utf-8"><style>
html,body{margin:0;background:%s}
#term{display:inline-block;box-sizing:content-box;padding:%dpx;background:%s;color:%s;font-family:%s;font-size:%dpx;line-height:%dpx;white-space:normal;font-kerning:none;font-variant-ligatures:none}
.row{height:%dpx;min-height:%dpx;width:%dch;white-space:pre;overflow:hidden}
.w{display:inline-block;width:2ch;font-style:normal;text-align:center}
#probe{position:absolute;visibility:hidden;font-family:%s;font-size:%dpx;white-space:pre}
</style></head><body><span id="probe">%s</span><div id="term">%s</div></body></html>"""
    Ansi.Theme.DefaultBg Look.PaddingPx Ansi.Theme.DefaultBg Ansi.Theme.DefaultFg Look.FontFamily Look.FontPx Look.LineHeightPx
    Look.LineHeightPx Look.LineHeightPx columns
    Look.FontFamily Look.FontPx (String('0', Look.ProbeCharacters)) rowsHtml

let private desktopVariables = set [ "DISPLAY"; "WAYLAND_DISPLAY"; "XDG_RUNTIME_DIR"; "DBUS_SESSION_BUS_ADDRESS" ]

let private browserEnvironment () : IDictionary<string, string> =
  let env = Dictionary<string, string>()
  for entry in Environment.GetEnvironmentVariables() |> Seq.cast<System.Collections.DictionaryEntry> do
    match entry.Key, entry.Value with
    | (:? string as k), (:? string as v) when not (desktopVariables.Contains k) -> env.[k] <- v
    | _ -> ()
  env :> IDictionary<string, string>

let private browser : Lazy<Result<IPlaywright * IBrowser, string>> =
  lazy
    (try
      let pw = Playwright.CreateAsync().GetAwaiter().GetResult()
      let launch = BrowserTypeLaunchOptions(Headless = true, Env = browserEnvironment ())
      let b = pw.Chromium.LaunchAsync(launch).GetAwaiter().GetResult()
      Result.Ok(pw, b)
     with ex ->
       Result.Error(sprintf "could not start headless Chromium through Playwright: %s (the browsers live in ~/.cache/ms-playwright)" ex.Message))

let private widthOf (box: LocatorBoundingBoxResult | null) : float =
  match box with
  | null -> 0.0
  | b -> float b.Width

let private heightOf (box: LocatorBoundingBoxResult | null) : float =
  match box with
  | null -> 0.0
  | b -> float b.Height

/// Draws the grid to `pngPath`. `columns` x `rows` is the terminal size, so a row that is
/// shorter than the terminal still gets its full width.
let renderPng (rowsHtml: string) (columns: int) (rows: int) (pngPath: string) : Result<Rendered, string> =
  match browser.Value with
  | Result.Error e -> Result.Error e
  | Result.Ok(_, b) ->
    let context = b.NewContextAsync(BrowserNewContextOptions(DeviceScaleFactor = 1.0f)).GetAwaiter().GetResult()
    try
      try
        let p = context.NewPageAsync().GetAwaiter().GetResult()
        p.SetContentAsync(page rowsHtml columns).GetAwaiter().GetResult()
        let probe = p.Locator("#probe").BoundingBoxAsync().GetAwaiter().GetResult()
        let term = p.Locator("#term")
        match Path.GetDirectoryName pngPath with
        | null -> ()
        | dir -> Directory.CreateDirectory dir |> ignore
        term.ScreenshotAsync(LocatorScreenshotOptions(Path = pngPath)).GetAwaiter().GetResult() |> ignore
        let box = term.BoundingBoxAsync().GetAwaiter().GetResult()
        Result.Ok
          { Png = pngPath
            Columns = columns
            Rows = rows
            CellWidthPx = widthOf probe / float Look.ProbeCharacters
            CellHeightPx = Look.LineHeightPx
            ImageWidth = int (widthOf box)
            ImageHeight = int (heightOf box)
            FontFamily = Look.FontFamily
            FontPx = Look.FontPx }
      with ex ->
        Result.Error(sprintf "could not render %s: %s" pngPath ex.Message)
    finally
      (try context.CloseAsync().GetAwaiter().GetResult() with _ -> ())

/// Closes the shared browser. Safe to call when it never started.
let shutdown () : unit =
  if browser.IsValueCreated then
    match browser.Value with
    | Result.Ok(pw, b) ->
      (try b.CloseAsync().GetAwaiter().GetResult() with _ -> ())
      (try pw.Dispose() with _ -> ())
    | Result.Error _ -> ()
