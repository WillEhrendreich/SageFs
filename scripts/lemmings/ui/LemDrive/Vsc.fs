/// Runs one `VscCommand` against the real VS Code. Every command answers with
/// what it did and then a brief of the window (focus, an open picker, a dialog,
/// notifications, the status bar), so a model that cannot see the screen still
/// sees what its action caused. A command that runs something from the SageFs
/// extension that controls the shared daemon is refused here, before it runs.
module LemDrive.Vsc

open System
open System.Threading.Tasks
open Microsoft.Playwright
open LemDrive.Calls
open LemDrive.Chord
open LemDrive.VscCommand
open LemDrive.Snapshot

/// How long to let the window settle after an action before reading it back. The
/// extension answers over HTTP and SSE, so a longer wait is the model's call
/// ("wait"), not the driver's.
[<Literal>]
let ActionSettleMs = 600

/// The command palette's fuzzy matcher has no state to poll between typing and
/// Enter (any page evaluation there steals focus from the picker), so this is the
/// one fixed wait. Same value the Electron journeys settled on.
[<Literal>]
let MatcherSettleMs = 800

/// How long a picker may take to appear.
[<Literal>]
let PickerOpenMs = 5000

/// How often a condition is checked while waiting for it.
[<Literal>]
let PollMs = 100

let private sleep (ms: int) : Task = Task.Delay ms

let private brief (c: Cdp.Connection) : Task<string> =
  task {
    let! f = Cdp.facts c
    return renderBrief f
  }

let private done' (c: Cdp.Connection) (did: string) : Task<Outcome> =
  task {
    do! sleep ActionSettleMs
    let! b = brief c
    return Output(sprintf "%s\n%s" did b)
  }

/// Waits for a condition on the page, polling; true when it held in time.
let private waitFor (timeoutMs: int) (probe: unit -> Task<bool>) : Task<bool> =
  task {
    let started = Diagnostics.Stopwatch.StartNew()
    let mutable met = false
    while not met && started.ElapsedMilliseconds < int64 timeoutMs do
      let! ok = probe ()
      match ok with
      | true -> met <- true
      | false -> do! sleep PollMs
    return met
  }

let private pickerOpen (c: Cdp.Connection) () : Task<bool> =
  c.Page.Locator(Cdp.Sel.Quick).First.IsVisibleAsync()

/// The label of the row the picker would accept on Enter, if a picker is open.
let private focusedRow (c: Cdp.Connection) : Task<string option> =
  task {
    let! f = Cdp.facts c
    return
      f.Quick
      |> Option.bind (fun q -> q.Rows |> List.tryFind (fun r -> r.Focused))
      |> Option.map (fun r -> r.Label)
  }

/// Refuses Enter when it would run a guarded command from an open picker.
let private guardAccept (c: Cdp.Connection) : Task<Result<unit, string>> =
  task {
    let! open' = pickerOpen c ()
    match open' with
    | false -> return Ok()
    | true ->
      let! row = focusedRow c
      let! f = Cdp.facts c
      let typed = f.Quick |> Option.map (fun q -> q.Input) |> Option.defaultValue ""
      return
        match Guard.check (defaultArg row "") with
        | Result.Error e -> Result.Error e
        | Ok() -> Guard.check typed
  }

let private pressChord (c: Cdp.Connection) (chord: Chord) : Task<Result<unit, string>> =
  task {
    let! allowed =
      match isAcceptKey chord with
      | true -> guardAccept c
      | false -> task { return Ok() }
    match allowed with
    | Result.Error e -> return Result.Error e
    | Ok() ->
      do! c.Page.Keyboard.PressAsync(toPlaywright chord)
      return Ok()
  }

let private pressAll (c: Cdp.Connection) (chords: Chord list) : Task<Result<unit, string>> =
  task {
    let mutable outcome = Ok()
    for chord in chords do
      match outcome with
      | Result.Error _ -> ()
      | Ok() ->
        let! r = pressChord c chord
        outcome <- r
        do! sleep PollMs
    return outcome
  }

let private escapeChord : Chord = { Modifiers = []; Key = Named Escape }
let private enterChord : Chord = { Modifiers = []; Key = Named Enter }

let private suggestVisible (c: Cdp.Connection) : Task<bool> =
  c.Page.Locator(Cdp.Sel.Suggest).First.IsVisibleAsync()

/// Types text the way a keyboard does. A newline is Enter, and a suggestion list that
/// is open is dismissed first so Enter starts a new line instead of accepting a
/// suggestion the model never asked for.
let private typeText (c: Cdp.Connection) (text: string) : Task<Result<unit, string>> =
  task {
    match Guard.check text with
    | Result.Error e -> return Result.Error e
    | Ok() ->
      let parts = text.Replace("\r\n", "\n").Split('\n')
      let mutable outcome = Ok()
      for i in 0 .. parts.Length - 1 do
        match outcome with
        | Result.Error _ -> ()
        | Ok() ->
          match parts[i] with
          | "" -> ()
          | part -> do! c.Page.Keyboard.TypeAsync part
          match i < parts.Length - 1 with
          | false -> ()
          | true ->
            let! suggesting = suggestVisible c
            match suggesting with
            | true -> do! c.Page.Keyboard.PressAsync(toPlaywright escapeChord)
            | false -> ()
            let! r = pressChord c enterChord
            outcome <- r
      return outcome
  }

// --- click -------------------------------------------------------------------

/// The ways a target is looked for, most exact first. Each is a locator over the
/// whole window; only visible matches count.
let private strategies (page: IPage) (target: string) : (string * ILocator) list =
  [ "label equals", page.GetByLabel(target, PageGetByLabelOptions(Exact = true))
    "text equals", page.GetByText(target, PageGetByTextOptions(Exact = true))
    "label contains", page.GetByLabel(target)
    "text contains", page.GetByText(target)
    "title contains", page.GetByTitle(target) ]

let private click (c: Cdp.Connection) (target: string) : Task<Outcome> =
  task {
    match Guard.check target with
    | Result.Error e -> return Refused e
    | Ok() ->
      let mutable found : (string * ILocator * int) option = None
      for (how, loc) in strategies c.Page target do
        match found with
        | Some _ -> ()
        | None ->
          let visibleOnly = loc.Filter(LocatorFilterOptions(Visible = true))
          let! n = visibleOnly.CountAsync()
          match n with
          | 0 -> ()
          | _ -> found <- Some(how, visibleOnly, n)
      match found with
      | None ->
        let! b = brief c
        return DriveFailed(sprintf "nothing visible matches \"%s\" (by label, text or title). Try \"snapshot\" to see the names on screen.\n%s" target b)
      | Some(how, loc, n) ->
        let first = loc.First
        let! label = first.GetAttributeAsync "aria-label"
        let! inner = first.InnerTextAsync()
        let described = match String.IsNullOrWhiteSpace(defaultArg (Option.ofObj label) "") with | false -> defaultArg (Option.ofObj label) "" | true -> inner
        match Guard.check described with
        | Result.Error e -> return Refused e
        | Ok() ->
          do! first.ClickAsync()
          let many = match n with | 1 -> "" | k -> sprintf " (%d matched, clicked the first; use longer text to pick another)" k
          return! done' c (sprintf "clicked \"%s\" [%s]%s" (described.Trim().Replace('\n', ' ')) how many)
  }

// --- palette and quick open --------------------------------------------------

let private openPicker (c: Cdp.Connection) (chord: string) : Task<bool> =
  task {
    do! c.Page.Keyboard.PressAsync chord
    return! waitFor PickerOpenMs (pickerOpen c)
  }

let private palette (c: Cdp.Connection) (text: string) : Task<Outcome> =
  task {
    match Guard.check text with
    | Result.Error e -> return Refused e
    | Ok() ->
      let! opened = openPicker c "Control+Shift+P"
      match opened with
      | false -> return DriveFailed "the command palette did not open (ctrl+shift+p). Press escape and try again."
      | true ->
        do! c.Page.Keyboard.TypeAsync text
        do! sleep MatcherSettleMs
        let! f = Cdp.facts c
        match f.Quick |> Option.map (fun q -> q.Rows) |> Option.defaultValue [] with
        | [] ->
          do! c.Page.Keyboard.PressAsync(toPlaywright escapeChord)
          return DriveFailed(sprintf "no command matches \"%s\". Nothing was run." text)
        | rows ->
          let chosen = rows |> List.tryFind (fun r -> r.Focused) |> Option.orElse (List.tryHead rows)
          let label = chosen |> Option.map (fun r -> r.Label) |> Option.defaultValue ""
          match Guard.check label with
          | Result.Error e ->
            do! c.Page.Keyboard.PressAsync(toPlaywright escapeChord)
            return Refused e
          | Ok() ->
            do! c.Page.Keyboard.PressAsync(toPlaywright enterChord)
            let others = rows |> List.filter (fun r -> Some r.Label <> Some label) |> List.truncate 4 |> List.map (fun r -> r.Label)
            let also = match others with | [] -> "" | o -> sprintf " (other matches: %s)" (String.Join("; ", o))
            return! done' c (sprintf "ran \"%s\"%s" label also)
  }

let private openFile (c: Cdp.Connection) (path: string) : Task<Outcome> =
  task {
    let! opened = openPicker c "Control+P"
    match opened with
    | false -> return DriveFailed "quick open did not appear (ctrl+p). Press escape and try again."
    | true ->
      do! c.Page.Keyboard.TypeAsync path
      do! sleep MatcherSettleMs
      let! f = Cdp.facts c
      match f.Quick |> Option.map (fun q -> q.Rows) |> Option.defaultValue [] with
      | [] ->
        do! c.Page.Keyboard.PressAsync(toPlaywright escapeChord)
        return DriveFailed(sprintf "no file matches \"%s\". Nothing was opened." path)
      | rows ->
        let chosen = rows |> List.tryFind (fun r -> r.Focused) |> Option.orElse (List.tryHead rows) |> Option.map (fun r -> r.Label) |> Option.defaultValue ""
        do! c.Page.Keyboard.PressAsync(toPlaywright enterChord)
        return! done' c (sprintf "opened \"%s\"" chosen)
  }

// --- snapshot, screenshot, wait -----------------------------------------------

let private snapshot (c: Cdp.Connection) : Task<Outcome> =
  task {
    let! f = Cdp.facts c
    return Output(render f)
  }

let private shot (c: Cdp.Connection) (screen: Screen) : Task<Outcome> =
  task {
    let! _ = c.Page.ScreenshotAsync(PageScreenshotOptions(Path = screen.ImagePath))
    let! b = brief c
    return Output(sprintf "screenshot saved: %s\n%s" screen.ImagePath b)
  }

let private waitSeconds (c: Cdp.Connection) (seconds: int) : Task<Outcome> =
  task {
    do! sleep (seconds * 1000)
    let! b = brief c
    return Output(sprintf "waited %d s\n%s" seconds b)
  }

/// Runs one command. Any Playwright failure becomes a `DriveFailed` that says what
/// was being done, never a stack trace.
let private run (c: Cdp.Connection) (screen: Screen) (cmd: VscCommand) : Task<Outcome> =
  task {
    try
      match cmd with
      | Snapshot -> return! snapshot c
      | Shot -> return! shot c screen
      | Wait s -> return! waitSeconds c s
      | Click target -> return! click c target
      | Palette text -> return! palette c text
      | Open path -> return! openFile c path
      | Type text ->
        let! r = typeText c text
        match r with
        | Result.Error e -> return Refused e
        | Ok() -> return! done' c (sprintf "typed %d characters" text.Length)
      | Key chords ->
        let! r = pressAll c chords
        match r with
        | Result.Error e -> return Refused e
        | Ok() -> return! done' c (sprintf "pressed %s" (String.Join(" ", chords |> List.map describe)))
    with ex -> return DriveFailed(sprintf "%s failed: %s" (verb cmd) ex.Message)
  }

/// The `vsc` command line: parse, connect, run, and leave a numbered transcript.
/// Every call is logged, including the ones that fail to parse.
let cli (args: string list) : Task<Outcome> =
  task {
    match DriverEnv.read ScreensDir, DriverEnv.read CdpPort with
    | Result.Error e, _
    | _, Result.Error e -> return BadUsage(sprintf "the driver is not set up for this window: %s" e)
    | Ok screensDir, Ok portText ->
      match Screens.reserve screensDir with
      | Result.Error e -> return DriveFailed e
      | Ok screen ->
        let! outcome =
          task {
            match parse args, Int32.TryParse portText with
            | Result.Error e, _ -> return BadUsage e
            | _, (false, _) -> return BadUsage(sprintf "LEM_CDP_PORT is not a port number: %s" portText)
            | Ok cmd, (true, port) ->
              let! conn = Cdp.connect port
              match conn with
              | Result.Error e -> return DriveFailed e
              | Ok c ->
                try return! run c screen cmd
                finally c.Playwright.Dispose()
          }
        Screens.write screen ("vsc" :: args) outcome
        return outcome
  }
