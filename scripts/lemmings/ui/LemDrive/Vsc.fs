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

/// How many matches of a click target are tried before the click gives up.
[<Literal>]
let ClickCandidates = 6

/// How long one candidate gets to become clickable.
[<Literal>]
let ClickTryMs = 2000.0

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
      match Guard.checkChord chord, isAcceptKey chord with
      | Result.Error e, _ -> task { return Result.Error e }
      | Ok(), true -> guardAccept c
      | Ok(), false -> task { return Ok() }
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
        // Try the matches in order until one can actually be clicked: the first one can sit
        // behind the tab strip (the code lens above line 1 does), and Playwright then waits
        // out its whole timeout on an element something else covers.
        let mutable clicked : (string * int) option = None
        let mutable refusal : string option = None
        let mutable lastError = ""
        for i in 0 .. (min n ClickCandidates) - 1 do
          match clicked, refusal with
          | None, None ->
            let candidate = loc.Nth i
            let! label = candidate.GetAttributeAsync "aria-label"
            let! inner = candidate.InnerTextAsync()
            let described = match String.IsNullOrWhiteSpace(defaultArg (Option.ofObj label) "") with | false -> defaultArg (Option.ofObj label) "" | true -> inner
            match Guard.check described with
            | Result.Error e -> refusal <- Some e
            | Ok() ->
              try
                do! candidate.ClickAsync(LocatorClickOptions(Timeout = float32 ClickTryMs))
                clicked <- Some(described, i)
              with ex ->
                // Playwright's timeout is not a PlaywrightException; any failure to click moves on.
                lastError <- ex.Message.Split('\n') |> Array.tryHead |> Option.defaultValue ex.Message
          | _ -> ()
        // Still nothing: click where the first match is, even though something sits on top of it.
        // That is what a person's mouse does (a search box's placeholder text is covered by the
        // input itself, so Playwright refuses a click that works fine by hand).
        match clicked, refusal with
        | None, None ->
          try
            do! (loc.Nth 0).ClickAsync(LocatorClickOptions(Timeout = float32 ClickTryMs, Force = true))
            clicked <- Some(target, -1)
          with ex ->
            lastError <- ex.Message.Split('\n') |> Array.tryHead |> Option.defaultValue ex.Message
        | _ -> ()
        match clicked, refusal with
        | _, Some e -> return Refused e
        | Some(described, -1), None ->
          return! done' c (sprintf "clicked \"%s\" [%s] at its position (another element is drawn over it, as with a placeholder in a search box)" (described.Trim().Replace('\n', ' ')) how)
        | Some(described, i), None ->
          let which = match n, i with | 1, _ -> "" | k, 0 -> sprintf " (%d matched, clicked the first; use longer text to pick another)" k | k, j -> sprintf " (%d matched, match %d was the first one that could be clicked)" k (j + 1)
          return! done' c (sprintf "clicked \"%s\" [%s]%s" (described.Trim().Replace('\n', ' ')) how which)
        | None, None ->
          let! b = brief c
          return DriveFailed(sprintf "found %d match(es) for \"%s\" but none could be clicked (%s).\n%s" n target lastError b)
  }

// --- palette and quick open --------------------------------------------------

let private openPicker (c: Cdp.Connection) (chord: string) : Task<bool> =
  task {
    do! c.Page.Keyboard.PressAsync chord
    return! waitFor PickerOpenMs (pickerOpen c)
  }

/// Runs a palette command. In exact mode the row must be titled exactly `text`; the focused row
/// is moved to it with the arrow keys, and if there is no such row nothing is run.
let private palette (exact: bool) (c: Cdp.Connection) (text: string) : Task<Outcome> =
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
          let focusedIndex = rows |> List.tryFindIndex (fun r -> r.Focused) |> Option.defaultValue 0
          let wanted =
            match exact with
            | false -> Ok focusedIndex
            | true ->
              match exactRowIndex (rows |> List.map (fun r -> r.Label)) text with
              | Some i -> Ok i
              | None -> Result.Error(sprintf "the palette does not offer a command titled \"%s\" right now (a when-clause may hide it). It offers: %s" text (String.Join("; ", rows |> List.truncate 5 |> List.map (fun r -> r.Label))))
          match wanted with
          | Result.Error e ->
            do! c.Page.Keyboard.PressAsync(toPlaywright escapeChord)
            return DriveFailed e
          | Ok index ->
          let label = rows[index].Label
          match Guard.check label with
          | Result.Error e ->
            do! c.Page.Keyboard.PressAsync(toPlaywright escapeChord)
            return Refused e
          | Ok() ->
            // Move the focus to the wanted row (a no-op unless it is not the focused one).
            for _ in 1 .. abs (index - focusedIndex) do
              do! c.Page.Keyboard.PressAsync(match index > focusedIndex with | true -> "ArrowDown" | false -> "ArrowUp")
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

/// How often an expect-text looks at the window again.
[<Literal>]
let ExpectPollMs = 500

/// How long the daemon may take to accept a workflow change, which restarts the session's worker.
[<Literal>]
let WorkflowRequestSeconds = 120

/// The folder under the run directory where `other-session` runs its second session: next to the
/// workspace (`w`), not inside it, so no folder of this window's workspace contains it.
[<Literal>]
let OtherFolder = "other"

/// How long the second session may take to be Ready.
[<Literal>]
let OtherSessionReadySeconds = 240

/// The run directory the harness named, when it did. A shot's activation line and a tour's file
/// changes need it; a plain call without it still works.
let private runDirOf () : string option =
  match DriverEnv.read RunDir with
  | Ok d -> Some d
  | Result.Error _ -> None

/// `shot <name> [--region part]...`: the numbered PNG, its crops and the text sidecar.
let private shot (c: Cdp.Connection) (screen: Screen) (name: string) (regions: Region list) : Task<Outcome> =
  task {
    let screensDir = defaultArg (DriverEnv.read ScreensDir |> Result.toOption) (IO.Path.GetDirectoryName screen.TextPath)
    match Shot.Reservation.reserve (Shot.shotsDir screensDir) name with
    | Result.Error e -> return DriveFailed e
    | Ok r ->
      let! written = Shot.capture c (defaultArg (runDirOf ()) "") r regions
      let! b = brief c
      return Output(sprintf "shot saved:\n%s\n%s" written b)
  }

/// Sets the window's size and says what it is afterwards, so a failed resize is not silent.
let private resize (c: Cdp.Connection) (width: int) (height: int) : Task<Outcome> =
  task {
    do! c.Page.SetViewportSizeAsync(width, height)
    do! sleep ActionSettleMs
    let! actual = c.Page.EvaluateAsync<string>(Shot.ReadInnerSize)
    let wanted = sprintf "%dx%d" width height
    match actual = wanted with
    | true -> return Output(sprintf "window is now %s" actual)
    | false -> return DriveFailed(sprintf "asked for %s but the window reports %s" wanted actual)
  }

let private waitSeconds (c: Cdp.Connection) (seconds: int) : Task<Outcome> =
  task {
    do! sleep (seconds * 1000)
    let! b = brief c
    return Output(sprintf "waited %d s\n%s" seconds b)
  }

/// Runs one command. Any Playwright failure becomes a `DriveFailed` that says what
/// was being done, never a stack trace.
let rec private run (c: Cdp.Connection) (screen: Screen) (cmd: VscCommand) : Task<Outcome> =
  task {
    try
      match cmd with
      | Snapshot -> return! snapshot c
      | Shot(name, regions) -> return! shot c screen name regions
      | Resize(w, h) -> return! resize c w h
      | Tour file -> return! runTour c screen file
      | Wait s -> return! waitSeconds c s
      | Click target -> return! click c target
      | Palette text -> return! palette false c text
      | PaletteExact text -> return! palette true c text
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

// --- tours ---------------------------------------------------------------------

/// Waits until the window's text contains `text` (any case), for up to `seconds`.
and private expectText (c: Cdp.Connection) (text: string) (seconds: int) : Task<Result<unit, string>> =
  task {
    let started = Diagnostics.Stopwatch.StartNew()
    let mutable found = false
    let mutable last = ""
    while not found && started.Elapsed.TotalSeconds < float seconds do
      let! f = Cdp.facts c
      last <- render f
      match last.Contains(text, StringComparison.OrdinalIgnoreCase) with
      | true -> found <- true
      | false -> do! sleep ExpectPollMs
    return
      match found with
      | true -> Ok()
      | false -> Result.Error(sprintf "the window never showed \"%s\" within %d s" text seconds)
  }

/// Asks the daemon to put THIS run's session (and only it) in a workflow. The request carries a
/// Content-Length: the extension's own Switch Workflow sends a chunked body, which the daemon's
/// workflow route reads as no workflow at all, so that command cannot be used in a tour.
and private setWorkflow (workflow: string) : Task<Result<string, string>> =
  task {
    match runDirOf () with
    | None -> return Result.Error "set-workflow needs LEM_RUN_DIR (the harness sets it)"
    | Some runDir ->
      match Daemon.sessions Daemon.DefaultMcpPort with
      | Result.Error e -> return Result.Error e
      | Ok all ->
        match all |> List.filter (Daemon.belongsTo (IO.Path.Combine(runDir, "w"))) with
        | [] -> return Result.Error "set-workflow: the daemon has no session under the run directory"
        | s :: _ ->
          try
            use client = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(float WorkflowRequestSeconds))
            use body = new Net.Http.StringContent(sprintf "{\"workflow\":\"%s\"}" workflow, Text.Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(sprintf "http://localhost:%d/api/sessions/%s/workflow" Daemon.DefaultMcpPort s.Id, body)
            let! text = resp.Content.ReadAsStringAsync()
            match resp.IsSuccessStatusCode with
            | true -> return Ok(sprintf "session %s asked to switch to %s: %s" s.Id workflow text)
            | false -> return Result.Error(sprintf "the daemon refused the workflow change (HTTP %d): %s" (int resp.StatusCode) text)
          with ex -> return Result.Error(sprintf "set-workflow failed: %s" ex.Message)
  }

/// Fills {session} in a step with this run's session id, read from the daemon.
and private resolveSession (step: Tour.Step) : Task<Result<Tour.Step, string>> =
  task {
    match Tour.usesSession step, runDirOf () with
    | false, _ -> return Ok step
    | true, None -> return Result.Error "a step uses {session} but LEM_RUN_DIR is not set"
    | true, Some runDir ->
      match Daemon.sessions Daemon.DefaultMcpPort with
      | Result.Error e -> return Result.Error(sprintf "cannot read the daemon's sessions for {session}: %s" e)
      | Ok all ->
        match all |> List.filter (Daemon.belongsTo (IO.Path.Combine(runDir, "w"))) with
        | s :: _ -> return Ok(Tour.withSession s.Id step)
        | [] -> return Result.Error "{session}: the daemon has no session under the run directory yet"
  }

/// Waits until this run's own session (under <run>/w) on the shared daemon is Ready. Read only.
and private expectSessionReady (seconds: int) : Task<Result<string, string>> =
  task {
    match runDirOf () with
    | None -> return Result.Error "expect-session needs LEM_RUN_DIR (the harness sets it)"
    | Some runDir ->
      let workspace = IO.Path.Combine(runDir, "w")
      let started = Diagnostics.Stopwatch.StartNew()
      let mutable outcome : Result<string, string> option = None
      let mutable last = "the daemon has no session under the run directory yet"
      while outcome.IsNone && started.Elapsed.TotalSeconds < float seconds do
        match Daemon.sessions Daemon.DefaultMcpPort with
        | Result.Error e -> last <- e
        | Ok all ->
          match all |> List.filter (Daemon.belongsTo workspace) with
          | [] -> last <- "the daemon has no session under the run directory yet"
          | mine ->
            match mine |> List.tryFind (fun s -> s.Status = "Ready") with
            | Some s -> outcome <- Some(Ok(sprintf "session %s is Ready" s.Id))
            | None -> last <- sprintf "session state: %s" (String.Join(", ", mine |> List.map (fun s -> sprintf "%s %s" s.Id s.Status)))
        match outcome with
        | Some _ -> ()
        | None -> do! sleep ExpectPollMs
      return (match outcome with | Some o -> o | None -> Result.Error(sprintf "no Ready session within %d s (%s)" seconds last))
  }

/// Changes a file on disk under `<run>/<folder>` (the workspace `w`, or the other session's folder), once,
/// and only if the text to find occurs exactly once.
and private replaceInFolder (folder: string) (path: string) (find: string) (replacement: string) : Result<string, string> =
  match runDirOf () with
  | None -> Result.Error "replace needs LEM_RUN_DIR (the harness sets it)"
  | Some runDir ->
    let full = IO.Path.Combine(runDir, folder, path)
    match IO.File.Exists full with
    | false -> Result.Error(sprintf "%s does not exist in %s" path folder)
    | true ->
      let text = IO.File.ReadAllText full
      let count = (text.Length - text.Replace(find, "").Length) / find.Length
      match count with
      | 1 ->
        IO.File.WriteAllText(full, text.Replace(find, replacement))
        Ok(sprintf "changed %s" path)
      | 0 -> Result.Error(sprintf "no match for \"%s\" in %s" find path)
      | n -> Result.Error(sprintf "%d matches for \"%s\" in %s; refusing to guess which" n find path)

/// Starts a second session in `<run>/other`, which is outside the workspace, and waits until it is Ready.
/// It stands in for another agent on the shared daemon. The run script copies the DemoEnv fixture there
/// when a tour has this step, and stops the session by id afterwards, like this run's own.
and private startOtherSession () : Task<Result<string, string>> =
  task {
    match runDirOf () with
    | None -> return Result.Error "other-session needs LEM_RUN_DIR (the harness sets it)"
    | Some runDir ->
      let folder = IO.Path.Combine(runDir, OtherFolder)
      let project = IO.Path.Combine(folder, "DemoEnv.Tests", "DemoEnv.Tests.fsproj")
      match IO.File.Exists project with
      | false ->
        return Result.Error(sprintf "%s does not exist: the run script copies the DemoEnv fixture there when a tour has other-session" project)
      | true ->
        try
          use client = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(float WorkflowRequestSeconds))
          let payload = Text.Json.JsonSerializer.Serialize({| projects = [| project |]; workingDirectory = folder |})
          use body = new Net.Http.StringContent(payload, Text.Encoding.UTF8, "application/json")
          let! resp = client.PostAsync(sprintf "http://localhost:%d/api/sessions/create" Daemon.DefaultMcpPort, body)
          let! text = resp.Content.ReadAsStringAsync()
          match resp.IsSuccessStatusCode with
          | false -> return Result.Error(sprintf "the daemon refused the second session (HTTP %d): %s" (int resp.StatusCode) text)
          | true ->
            let started = Diagnostics.Stopwatch.StartNew()
            let mutable ready : string option = None
            while ready.IsNone && started.Elapsed.TotalSeconds < float OtherSessionReadySeconds do
              match Daemon.sessions Daemon.DefaultMcpPort with
              | Ok all ->
                match all |> List.filter (Daemon.belongsTo folder) |> List.tryFind (fun s -> s.Status = "Ready") with
                | Some s -> ready <- Some s.Id
                | None -> ()
              | Result.Error _ -> ()
              match ready with
              | Some _ -> ()
              | None -> do! sleep ExpectPollMs
            return
              match ready with
              | Some id -> Ok(sprintf "second session %s is Ready in %s" id folder)
              | None -> Result.Error(sprintf "the second session was not Ready within %d s" OtherSessionReadySeconds)
        with ex -> return Result.Error(sprintf "other-session failed: %s" ex.Message)
  }

/// Fails if the window shows `text` at any moment during `seconds`. Polls like expect-text does.
and private expectAbsent (c: Cdp.Connection) (text: string) (seconds: int) : Task<Result<unit, string>> =
  task {
    let started = Diagnostics.Stopwatch.StartNew()
    let mutable shown = false
    while not shown && started.Elapsed.TotalSeconds < float seconds do
      let! f = Cdp.facts c
      match (render f).Contains(text, StringComparison.OrdinalIgnoreCase) with
      | true -> shown <- true
      | false -> do! sleep ExpectPollMs
    return
      match shown with
      | true -> Result.Error(sprintf "the window showed \"%s\" within %d s, and it must not" text seconds)
      | false -> Ok()
  }

/// Runs a tour file in order. A step that fails does not stop the tour (the later shots still
/// matter to a reviewer), but the shots after it say so, and the whole call fails at the end.
and private runTour (c: Cdp.Connection) (screen: Screen) (file: string) : Task<Outcome> =
  task {
    match IO.File.Exists file with
    | false -> return BadUsage(sprintf "no tour file at %s" file)
    | true ->
      match Tour.parse (IO.File.ReadAllText file) with
      | Result.Error errors -> return BadUsage(sprintf "%s does not parse:\n%s" file (String.Join("\n", errors)))
      | Ok tour ->
        let log = ResizeArray<string>()
        let mutable failures = 0
        let total = List.length tour.Steps
        for (i, placed) in tour.Steps |> List.indexed do
          let label = sprintf "[%d/%d] line %d  %s" (i + 1) total placed.Line (Tour.describe placed.Step)
          let! resolved = resolveSession placed.Step
          let! result =
            task {
              match resolved with
              | Result.Error e -> return DriveFailed e
              | Ok step ->
              match step with
              | Tour.Run cmd -> return! run c screen cmd
              | Tour.SetWorkflow workflow ->
                let! r = setWorkflow workflow
                return (match r with | Ok m -> Output m | Result.Error e -> DriveFailed e)
              | Tour.ExpectSession seconds ->
                let! r = expectSessionReady seconds
                return (match r with | Ok m -> Output m | Result.Error e -> DriveFailed e)
              | Tour.ExpectText(text, seconds) ->
                let! r = expectText c text seconds
                return (match r with | Ok() -> Output "found" | Result.Error e -> DriveFailed e)
              | Tour.Replace(path, find, replacement) ->
                return (match replaceInFolder "w" path find replacement with | Ok m -> Output m | Result.Error e -> DriveFailed e)
              | Tour.ReplaceOther(path, find, replacement) ->
                return (match replaceInFolder OtherFolder path find replacement with | Ok m -> Output m | Result.Error e -> DriveFailed e)
              | Tour.OtherSession ->
                let! r = startOtherSession ()
                return (match r with | Ok m -> Output m | Result.Error e -> DriveFailed e)
              | Tour.ExpectAbsent(text, seconds) ->
                let! r = expectAbsent c text seconds
                return (match r with | Ok() -> Output "never showed" | Result.Error e -> DriveFailed e)
            }
          match result with
          | Output text ->
            // Only the first line of a step's answer: the shots carry the detail.
            log.Add(sprintf "%s\n    ok: %s" label (text.Split('\n')[0]))
          | other ->
            failures <- failures + 1
            log.Add(sprintf "%s\n    %s" label (Outcome.text other))
        let summary = sprintf "tour %s: %d step(s), %d failed" (IO.Path.GetFileName file) total failures
        let body = String.Join("\n", log) + "\n" + summary
        return (match failures with | 0 -> Output body | _ -> DriveFailed body)
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
        let startedMs = Timeline.nowMs ()
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
        Timeline.append
          { StartMs = startedMs
            EndMs = Timeline.nowMs ()
            Editor = "vsc"
            Command = (match args with | v :: _ -> v | [] -> "")
            Args = (match args with | _ :: rest -> rest | [] -> [])
            Outcome = Outcome.label outcome }
        return outcome
  }
