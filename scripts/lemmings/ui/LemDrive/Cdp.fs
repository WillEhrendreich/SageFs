/// Talks to the real VS Code over CDP with Playwright.NET and reads what the
/// window shows into `Snapshot.Facts`. The only script strings here are three
/// read-only readers (an attribute list, a bounding-box top, a pseudo-element's
/// content): they return data and decide nothing. Everything that picks, joins or
/// words is F#.
module LemDrive.Cdp

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Playwright
open LemDrive.Snapshot

/// The one page that is the workbench, and the Playwright that opened it.
type Connection =
  { Playwright: IPlaywright
    Page: IPage }

/// How long a Playwright action may wait for its target before it gives up.
[<Literal>]
let ActionTimeoutMs = 8000.0

/// How long connecting over CDP may take.
[<Literal>]
let ConnectTimeoutMs = 20000.0

// --- the three read-only readers -------------------------------------------------

[<Literal>]
let private ReadAttributes = "(els, names) => els.map(e => names.map(n => e.getAttribute(n)))"

[<Literal>]
let private ReadTops = "els => els.map(e => e.getBoundingClientRect().top)"

[<Literal>]
let private ReadPseudoContent =
  "els => els.map(e => [getComputedStyle(e, '::before').content, getComputedStyle(e, '::after').content])"

// --- selectors, named once -------------------------------------------------------

module Sel =
  [<Literal>]
  let Editor = ".part.editor .editor-group-container.active .monaco-editor"
  [<Literal>]
  let EditorTabs = ".part.editor .editor-group-container.active .tabs-container .tab"
  [<Literal>]
  let ActivityItems = ".part.activitybar li.action-item"
  [<Literal>]
  let SideBar = ".part.sidebar"
  [<Literal>]
  let Panel = ".part.panel"
  [<Literal>]
  let Quick = ".quick-input-widget"
  [<Literal>]
  let StatusItems = ".part.statusbar .statusbar-item"
  [<Literal>]
  let Notifications = ".notification-list-item"
  [<Literal>]
  let Dialog = ".monaco-dialog-box"
  [<Literal>]
  let Hover = ".monaco-hover:not(.hidden)"
  [<Literal>]
  let Suggest = ".suggest-widget.visible"
  [<Literal>]
  let ListRow = ".monaco-list-row"

let private notNull (s: string | null) : string = match s with | null -> "" | v -> v

let private cell (e: JsonElement) : string =
  match e.ValueKind with
  | JsonValueKind.String -> notNull (e.GetString())
  | _ -> ""

/// The named attributes of every element the locator matches, in DOM order. A
/// missing attribute reads as "".
let private attrs (loc: ILocator) (names: string list) : Task<string list list> =
  task {
    let! raw = loc.EvaluateAllAsync<JsonElement>(ReadAttributes, names |> Array.ofList)
    return [ for row in raw.EnumerateArray() -> [ for c in row.EnumerateArray() -> cell c ] ]
  }

let private tops (loc: ILocator) : Task<float list> =
  task {
    let! raw = loc.EvaluateAllAsync<JsonElement>(ReadTops)
    return [ for t in raw.EnumerateArray() -> t.GetDouble() ]
  }

let private texts (loc: ILocator) : Task<string list> =
  task {
    let! all = loc.AllInnerTextsAsync()
    return all |> Seq.map notNull |> List.ofSeq
  }

let private visible (loc: ILocator) : Task<bool> =
  task {
    let! n = loc.CountAsync()
    match n with
    | 0 -> return false
    | _ -> return! loc.First.IsVisibleAsync()
  }

let private nth (loc: ILocator) (i: int) : ILocator = loc.Nth i

let private hasClass (cls: string) (word: string) : bool =
  cls.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> Array.contains word

// --- connecting ------------------------------------------------------------------

/// Connects to VS Code's CDP endpoint and picks the workbench page.
let connect (port: int) : Task<Result<Connection, string>> =
  task {
    try
      // Playwright's bundled node driver warns about its own url.parse on every start,
      // which would sit in front of every transcript.
      Environment.SetEnvironmentVariable("NODE_NO_WARNINGS", "1")
      let! pw = Playwright.CreateAsync()
      let options = BrowserTypeConnectOverCDPOptions(Timeout = float32 ConnectTimeoutMs)
      let! browser = pw.Chromium.ConnectOverCDPAsync(sprintf "http://127.0.0.1:%d" port, options)
      let pages = browser.Contexts |> Seq.collect (fun c -> c.Pages) |> List.ofSeq
      match pages |> List.tryFind (fun p -> p.Url.Contains "workbench") |> Option.orElse (List.tryHead pages) with
      | None ->
        pw.Dispose()
        return Result.Error(sprintf "connected to CDP on %d but VS Code has no window open" port)
      | Some page ->
        page.SetDefaultTimeout(float32 ActionTimeoutMs)
        return Ok { Playwright = pw; Page = page }
    with ex -> return Result.Error(sprintf "cannot reach VS Code on CDP port %d: %s" port ex.Message)
  }

// --- reading the window ----------------------------------------------------------

let private readTabs (page: IPage) : Task<Tab list> =
  task {
    let loc = page.Locator Sel.EditorTabs
    let! rows = attrs loc [ "aria-label"; "class" ]
    return
      rows
      |> List.map (fun r ->
        { Label = r[0]
          Active = hasClass r[1] "active"
          Unsaved = hasClass r[1] "dirty" })
  }

let private readActivityBar (page: IPage) : Task<Tab list> =
  task {
    let items = page.Locator Sel.ActivityItems
    let! classes = attrs items [ "class" ]
    let! labels = attrs (page.Locator(Sel.ActivityItems + " .action-label")) [ "aria-label" ]
    return
      match List.length classes = List.length labels with
      | true ->
        List.zip classes labels
        |> List.map (fun (c, l) -> { Label = l[0]; Active = hasClass c[0] "checked"; Unsaved = false })
      | false -> labels |> List.map (fun l -> { Label = l[0]; Active = false; Unsaved = false })
  }

/// Strips the quotes CSS puts around a pseudo-element's content, and drops the
/// "nothing here" values.
let private pseudoText (raw: string) : string option =
  match raw.Trim() with
  | ""
  | "none"
  | "normal"
  | "\"\"" -> None
  | quoted when quoted.Length >= 2 && quoted[0] = '"' && quoted[quoted.Length - 1] = '"' ->
    Some(quoted.Substring(1, quoted.Length - 2).Replace("\\\"", "\"").Replace("\\a", " "))
  | other -> Some other

/// A line whose top is within this many pixels of another element's top is on that line.
[<Literal>]
let private SameLinePixels = 6.0

let private readEditor (page: IPage) : Task<EditorFacts option> =
  task {
    let root = page.Locator Sel.Editor
    let! present = visible root
    match present with
    | false -> return None
    | true ->
      let lineLoc = page.Locator(Sel.Editor + " .view-lines .view-line")
      let! lineTops = tops lineLoc
      let! lineTexts = texts lineLoc
      let marginLoc = page.Locator(Sel.Editor + " .margin-view-overlays > div")
      let! marginTops = tops marginLoc
      let! marginTexts = texts marginLoc
      let numberAtTop (top: float) =
        List.zip marginTops marginTexts
        |> List.tryFind (fun (t, _) -> abs (t - top) < SameLinePixels)
        |> Option.bind (fun (_, n) -> match Int32.TryParse(n.Trim()) with | true, v -> Some v | _ -> None)
      let lines =
        List.zip lineTops lineTexts
        |> List.choose (fun (top, text) -> numberAtTop top |> Option.map (fun n -> top, { Number = n; Text = text }))
        |> List.sortBy fst
      let lineAt (top: float) =
        lines |> List.tryFind (fun (t, _) -> abs (t - top) < SameLinePixels) |> Option.map (fun (_, l) -> l.Number)
      let lineBelow (top: float) =
        lines |> List.tryFind (fun (t, _) -> t >= top - SameLinePixels) |> Option.map (fun (_, l) -> l.Number)
      // Code lenses sit above the line they belong to.
      let lensLoc = page.Locator(".part.editor .editor-group-container.active .codelens-decoration")
      let! lensTops = tops lensLoc
      let! lensTexts = texts lensLoc
      let lenses =
        List.zip lensTops lensTexts
        |> List.choose (fun (top, text) ->
          lineBelow top |> Option.map (fun n -> { Line = n; Kind = CodeLens; Text = text }))
      // Text the extension draws after a line is a pseudo-element's content.
      let decoLoc = page.Locator(Sel.Editor + " .view-lines span[class*=\"ced-\"]")
      let! decoTops = tops decoLoc
      let! raw = decoLoc.EvaluateAllAsync<JsonElement>(ReadPseudoContent)
      let contents = [ for pair in raw.EnumerateArray() -> [ for c in pair.EnumerateArray() -> cell c ] ]
      let decos =
        List.zip decoTops contents
        |> List.collect (fun (top, pair) ->
          pair
          |> List.choose pseudoText
          |> List.choose (fun t -> lineAt top |> Option.map (fun n -> { Line = n; Kind = InlineText; Text = t })))
      // Marks in the gutter: the classes the extension's decoration types carry.
      let gutterLoc = page.Locator(Sel.Editor + " .glyph-margin-widgets > *, " + Sel.Editor + " .margin-view-overlays .cgmr")
      let! gutterTops = tops gutterLoc
      let! gutterAttrs = attrs gutterLoc [ "class"; "title"; "aria-label" ]
      let gutter =
        List.zip gutterTops gutterAttrs
        |> List.choose (fun (top, a) ->
          let described =
            match a[1], a[2] with
            | "", "" -> a[0]
            | "", label -> label
            | title, _ -> title
          lineAt top |> Option.map (fun n -> { Line = n; Kind = GutterMark; Text = described }))
      return Some { Lines = lines |> List.map snd; Notes = lenses @ decos @ gutter }
  }

let private readQuick (page: IPage) : Task<QuickFacts option> =
  task {
    let! open' = visible (page.Locator Sel.Quick)
    match open' with
    | false -> return None
    | true ->
      let! input = page.Locator(Sel.Quick + " .quick-input-box input").First.InputValueAsync()
      let! rows = attrs (page.Locator(Sel.Quick + " .quick-input-list " + Sel.ListRow)) [ "aria-label"; "class" ]
      return
        Some
          { Input = input
            Rows = rows |> List.map (fun r -> { Label = r[0]; Focused = hasClass r[1] "focused" }) }
  }

let private readRows (scope: ILocator) : Task<Row list> =
  task {
    let! rows = attrs (scope.Locator Sel.ListRow) [ "aria-label"; "aria-level"; "aria-expanded"; "aria-selected" ]
    return
      rows
      |> List.map (fun r ->
        { Label = r[0]
          Level = (match Int32.TryParse r[1] with | true, v -> v | _ -> 1)
          Expanded = (match r[2] with | "true" -> Some true | "false" -> Some false | _ -> None)
          Selected = (r[3] = "true") })
  }

let private readSideBar (page: IPage) : Task<SideBarFacts option> =
  task {
    let bar = page.Locator Sel.SideBar
    let! present = visible bar
    match present with
    | false -> return None
    | true ->
      let! titles = texts (bar.Locator ".composite.title")
      let panes = bar.Locator ".pane"
      let! n = panes.CountAsync()
      let! read =
        [ for i in 0 .. n - 1 -> i ]
        |> List.map (fun i ->
          task {
            let pane = nth panes i
            let! title = texts (pane.Locator ".pane-header .title")
            let! header = attrs (pane.Locator ".pane-header") [ "aria-expanded" ]
            let! rows = readRows (pane.Locator ".pane-body")
            let! welcome = texts (pane.Locator ".pane-body .welcome-view")
            return
              { Title = title |> List.tryHead |> Option.defaultValue ""
                Expanded = (header |> List.tryHead |> Option.map (fun h -> h[0] <> "false") |> Option.defaultValue true)
                Body = String.Join(" ", welcome)
                Rows = rows }
          })
        |> Task.WhenAll
      return Some { Title = titles |> List.tryHead |> Option.defaultValue ""; Panes = List.ofArray read }
  }

let private readPanel (page: IPage) : Task<PanelFacts option> =
  task {
    let panel = page.Locator Sel.Panel
    let! present = visible panel
    match present with
    | false -> return None
    | true ->
      let tabLoc = panel.Locator ".composite-bar .action-item"
      let! tabAttrs = attrs tabLoc [ "aria-label"; "class" ]
      let tabs =
        tabAttrs
        |> List.filter (fun t -> t[0] <> "")
        |> List.map (fun t -> { Label = t[0]; Active = hasClass t[1] "checked"; Unsaved = false })
      let! viewLines = texts (panel.Locator ".monaco-editor .view-lines .view-line")
      let! listRows = attrs (panel.Locator Sel.ListRow) [ "aria-label" ]
      let! terminal = texts (panel.Locator ".xterm-rows")
      let lines =
        viewLines @ (listRows |> List.map (fun r -> r[0])) @ (terminal |> List.collect (fun t -> t.Split('\n') |> List.ofArray))
        |> List.map (fun l -> l.Replace(' ', ' ').TrimEnd())
        |> List.filter (fun l -> l <> "")
      return Some { Tabs = tabs; Lines = lines }
  }

let private readStatus (page: IPage) : Task<string list> =
  task {
    let! rows = attrs (page.Locator Sel.StatusItems) [ "aria-label" ]
    let! plain = texts (page.Locator Sel.StatusItems)
    return
      List.zip rows plain
      |> List.map (fun (a, t) -> match a[0] with | "" -> t.Trim() | label -> label)
      |> List.filter (fun s -> s <> "")
  }

let private severityOf (cls: string) : string =
  match cls with
  | c when c.Contains "codicon-error" -> "error"
  | c when c.Contains "codicon-warning" -> "warning"
  | _ -> "info"

let private readToasts (page: IPage) : Task<Toast list> =
  task {
    let items = page.Locator Sel.Notifications
    let! n = items.CountAsync()
    let! read =
      [ for i in 0 .. n - 1 -> i ]
      |> List.map (fun i ->
        task {
          let item = nth items i
          let! message = texts (item.Locator ".notification-list-item-message")
          let! icon = attrs (item.Locator ".notification-list-item-icon") [ "class" ]
          let! buttons = texts (item.Locator ".notification-list-item-buttons-container .monaco-button")
          return
            { Severity = icon |> List.tryHead |> Option.map (fun c -> severityOf c[0]) |> Option.defaultValue "info"
              Message = String.Join(" ", message)
              Buttons = buttons |> List.map (fun b -> b.Trim()) |> List.filter (fun b -> b <> "") }
        })
      |> Task.WhenAll
    return read |> List.ofArray |> List.filter (fun t -> t.Message <> "")
  }

let private readDialog (page: IPage) : Task<DialogFacts option> =
  task {
    let dialog = page.Locator Sel.Dialog
    let! present = visible dialog
    match present with
    | false -> return None
    | true ->
      let! message = texts (dialog.Locator ".dialog-message-text, .dialog-detail")
      let! buttons = texts (dialog.Locator ".dialog-buttons .monaco-button")
      return Some { Message = String.Join(" ", message); Buttons = buttons |> List.map (fun b -> b.Trim()) }
  }

let private readHover (page: IPage) : Task<string option> =
  task {
    let hover = page.Locator Sel.Hover
    let! present = visible hover
    match present with
    | false -> return None
    | true ->
      let! t = texts hover
      return t |> List.tryHead
  }

let private readSuggest (page: IPage) : Task<string list> =
  task {
    let widget = page.Locator Sel.Suggest
    let! present = visible widget
    match present with
    | false -> return []
    | true ->
      let! rows = attrs (widget.Locator Sel.ListRow) [ "aria-label" ]
      return rows |> List.map (fun r -> r[0])
  }

/// Which part of the window has keyboard focus, in words.
let private readFocus (page: IPage) (quickOpen: bool) : Task<string> =
  task {
    match quickOpen with
    | true -> return "quick input (the command palette or a picker)"
    | false ->
      let! editor = page.Locator(Sel.Editor + ".focused").CountAsync()
      match editor with
      | n when n > 0 -> return "editor"
      | _ ->
        let regions =
          [ ".part.sidebar", "side bar"
            Sel.Panel, "panel"
            ".part.activitybar", "activity bar"
            ".part.statusbar", "status bar"
            ".part.titlebar", "title bar"
            ".part.auxiliarybar", "secondary side bar" ]
        let! hits =
          regions
          |> List.map (fun (sel, name) ->
            task {
              let! c = page.Locator(sel + " :focus").CountAsync()
              return name, c
            })
          |> Task.WhenAll
        return
          hits
          |> Array.tryFind (fun (_, c) -> c > 0)
          |> Option.map fst
          |> Option.defaultValue "nothing in particular"
  }

/// Everything the window shows right now.
let facts (c: Connection) : Task<Facts> =
  task {
    let page = c.Page
    let! title = page.TitleAsync()
    let! tabs = readTabs page
    let! activity = readActivityBar page
    let! editor = readEditor page
    let! quick = readQuick page
    let! side = readSideBar page
    let! panel = readPanel page
    let! status = readStatus page
    let! toasts = readToasts page
    let! dialog = readDialog page
    let! hover = readHover page
    let! suggest = readSuggest page
    let! focus = readFocus page quick.IsSome
    return
      { WindowTitle = title
        Focus = focus
        ActivityBar = activity
        Tabs = tabs
        Editor = editor
        Quick = quick
        SideBar = side
        Panel = panel
        StatusBar = status
        Toasts = toasts
        Dialog = dialog
        Hover = hover
        Suggest = suggest }
  }
