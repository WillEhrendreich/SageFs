/// Reading the extension's own `SageFs` Output channel from the window. The daemon's /events
/// stream carries eval output only for the session the daemon has active, so the channel (which
/// keeps every result the extension printed) is the second place the oracle looks. The harness
/// reads it itself, after the lemming is done: it opens the Output panel, picks the channel, and
/// reads the lines.
module LemDrive.OutputChannel

open System
open System.Threading.Tasks
open Microsoft.Playwright

/// The Output channel the SageFs extension writes to, as the channel picker names it.
[<Literal>]
let SageFsChannel = "SageFs"

/// How long the panel may take to switch channel and render.
[<Literal>]
let SettleMs = 1500

/// How long the harness waits for the panel's channel picker to appear after opening it.
[<Literal>]
let PickerAppearMs = 6000

/// How often it looks.
[<Literal>]
let PollMs = 250

/// The panel is an editor, and an editor draws only the lines in view (about a dozen here). The
/// channel also carries every session's hot-reload chatter on a busy daemon, so a result can be
/// many pages up. The harness reads from the end, a page at a time, until what it wants shows or
/// the top is reached, and gives up after this many pages.
[<Literal>]
let MostPages = 400

/// How long the editor takes to redraw after a page key.
[<Literal>]
let PageSettleMs = 120

/// The panel's channel picker, as VS Code draws it: a native select in the panel's title bar.
/// Named once, with the panel itself.
module Sel =
  [<Literal>]
  let Panel = ".part.panel"
  [<Literal>]
  let Picker = ".part.panel .monaco-action-bar select.monaco-select-box"
  [<Literal>]
  let Lines = ".part.panel .monaco-editor .view-line"
  [<Literal>]
  let Slider = ".part.panel .monaco-editor .scrollbar.vertical .slider"

let private firstLine (ex: exn) : string = ex.Message.Split('\n')[0]

let private visible (page: IPage) (selector: string) : Task<bool> =
  task {
    let loc = page.Locator selector
    let! n = loc.CountAsync()
    match n with
    | 0 -> return false
    | _ -> return! loc.First.IsVisibleAsync()
  }

/// Runs a palette command by typing its title and pressing Enter on the first row. The
/// harness uses the palette rather than the chord because a chord is eaten by whatever has the
/// focus (an editor, a picker that is still open), and the palette opens from anywhere.
let private runPaletteCommand (page: IPage) (title: string) : Task =
  task {
    // Anything left open (a picker, a suggestion list) is closed first.
    do! page.Keyboard.PressAsync "Escape"
    do! page.Keyboard.PressAsync "Control+Shift+P"
    do! Task.Delay 600
    do! page.Keyboard.TypeAsync title
    do! Task.Delay 800
    do! page.Keyboard.PressAsync "Enter"
  }

/// Waits for a selector to be visible, polling; true when it showed in time.
let private appears (page: IPage) (selector: string) (ms: int) : Task<bool> =
  task {
    let started = Diagnostics.Stopwatch.StartNew()
    let mutable shown = false
    while not shown && started.ElapsedMilliseconds < int64 ms do
      let! v = visible page selector
      match v with
      | true -> shown <- true
      | false -> do! Task.Delay PollMs
    return shown
  }

/// What the panel looks like, for the failure message: its title bar's text and whether it is
/// on screen. A failed read says what was there instead of only that it timed out.
let private describePanel (page: IPage) : Task<string> =
  task {
    let! showing = visible page Sel.Panel
    match showing with
    | false -> return "the panel is not on screen"
    | true ->
      let! tabs = page.Locator(".part.panel .composite-bar .action-item").AllInnerTextsAsync()
      let! selects = page.Locator(".part.panel select").CountAsync()
      return sprintf "the panel is on screen with tabs [%s] and %d select element(s)" (String.Join(", ", tabs |> Seq.map (fun t -> t.Trim()) |> Seq.filter (fun t -> t <> ""))) selects
  }

/// The editor draws a space as a no-break space; a pattern is written with plain ones.
let normalizeLine (line: string) : string = line.Replace(' ', ' ')

/// What the editor shows now: its lines, and where its vertical scrollbar's slider sits. Two
/// pages with the same text (a run of identical hot-reload lines) are told apart by the slider,
/// so "the page did not change" means "the top was reached", never "two pages look alike".
let private viewState (page: IPage) : Task<string list * string> =
  task {
    let! lines = page.Locator(Sel.Lines).AllInnerTextsAsync()
    let slider = page.Locator(Sel.Slider)
    let! n = slider.CountAsync()
    let! position =
      match n with
      | 0 -> task { return "" }
      | _ ->
        task {
          let! style = slider.First.GetAttributeAsync "style"
          return (match style with | null -> "" | s -> s)
        }
    return (lines |> Seq.map normalizeLine |> List.ofSeq), position
  }

/// The channel's text from its end upward, a page at a time, until `wanted` holds of what has
/// been read so far or the top of the channel is reached. Returns the lines read, in order.
let private readFromEnd (page: IPage) (wanted: string -> bool) : Task<string list> =
  task {
    // The editor must have focus for the page keys; a click into a read-only editor changes nothing.
    do! page.Locator(Sel.Lines).First.ClickAsync(LocatorClickOptions(Force = true, Timeout = 3000.0f))
    do! page.Keyboard.PressAsync "Control+End"
    do! Task.Delay PageSettleMs
    let mutable collected : string list = []
    let mutable previousState : (string list * string) option = None
    let mutable pages = 0
    let mutable finished = false
    while not finished && pages < MostPages do
      let! state = viewState page
      let current = fst state
      match previousState = Some state with
      | true -> finished <- true
      | false ->
        collected <- current @ collected
        pages <- pages + 1
        previousState <- Some state
        match wanted (String.Join("\n", collected)) with
        | true -> finished <- true
        | false ->
          do! page.Keyboard.PressAsync "PageUp"
          do! Task.Delay PageSettleMs
    return collected
  }

/// The lines of the SageFs Output channel, read from its end until `wanted` holds of the text
/// read so far (or all of it, when it never does). Opens the Output panel through the palette
/// (leaving it open: the harness reads after the lemming is done and tears the window down
/// next), waits for the channel picker, picks the channel, and reads. Each step that can fail
/// says which, and what the panel showed instead.
let read (page: IPage) (wanted: string -> bool) : Task<Result<string, string>> =
  task {
    try
      let! pickerThere = visible page Sel.Picker
      let! opened =
        task {
          match pickerThere with
          | true -> return true
          | false ->
            do! runPaletteCommand page "Output: Focus on Output View"
            return! appears page Sel.Picker PickerAppearMs
        }
      match opened with
      | false ->
        let! seen = describePanel page
        return Result.Error(sprintf "the Output panel's channel picker did not appear: %s" seen)
      | true ->
        let picker = page.Locator(Sel.Picker).First
        let! _ = picker.SelectOptionAsync(SageFsChannel, LocatorSelectOptionOptions(Timeout = 3000.0f))
        do! Task.Delay SettleMs
        let! lines = readFromEnd page wanted
        return Ok(String.Join("\n", lines))
    with ex ->
      let! seen = describePanel page
      return Result.Error(sprintf "could not read the SageFs Output channel: %s (%s)" (firstLine ex) seen)
  }
