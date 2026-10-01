/// What the VS Code window shows, as plain data, and how it reads as text. The
/// driver fills `Facts` from the page; everything here is pure, so the rendering
/// (what a model that cannot see the screen actually reads) is proven in the REPL
/// with made-up facts before it ever touches a window.
module LemDrive.Snapshot

open System

/// The widest a line of editor text or a row label is shown, so one long line
/// cannot eat a small model's context.
[<Literal>]
let MaxTextWidth = 220

/// The most rows shown for one tree view, one quick-input list or one panel.
[<Literal>]
let MaxRows = 40

/// The most lines of panel output shown.
[<Literal>]
let MaxPanelLines = 30

type NoteKind =
  | CodeLens
  | InlineText
  | GutterMark

type Tab =
  { Label: string
    Active: bool
    Unsaved: bool }

type EditorLine = { Number: int; Text: string }

/// Something drawn on or beside a line: a code lens above it, text after it, a
/// mark in the gutter.
type Note =
  { Line: int
    Kind: NoteKind
    Text: string }

type EditorFacts =
  { Lines: EditorLine list
    Notes: Note list }

type QuickRow = { Label: string; Focused: bool }

type QuickFacts =
  { Input: string
    Rows: QuickRow list }

type Row =
  { Label: string
    Level: int
    Expanded: bool option
    Selected: bool }

type Pane =
  { Title: string
    Expanded: bool
    Body: string
    Rows: Row list }

type SideBarFacts =
  { Title: string
    Panes: Pane list }

type PanelFacts =
  { Tabs: Tab list
    Lines: string list }

type Toast =
  { Severity: string
    Message: string
    Buttons: string list }

type DialogFacts =
  { Message: string
    Buttons: string list }

type Facts =
  { WindowTitle: string
    Focus: string
    ActivityBar: Tab list
    Tabs: Tab list
    Editor: EditorFacts option
    Quick: QuickFacts option
    SideBar: SideBarFacts option
    Panel: PanelFacts option
    StatusBar: string list
    Toasts: Toast list
    Dialog: DialogFacts option
    Hover: string option
    Suggest: string list }

let private clip (text: string) : string =
  let flat = text.Replace(' ', ' ').Replace("\r", "").TrimEnd()
  match flat.Length > MaxTextWidth with
  | true -> flat.Substring(0, MaxTextWidth) + " ..."
  | false -> flat

let private oneLine (text: string) : string =
  String.Join(' ', (clip text).Split([| '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.Trim()))

let private takeRows (items: 'a list) : 'a list * int =
  items |> List.truncate MaxRows, max 0 (List.length items - MaxRows)

let private moreLine (hidden: int) : string list =
  match hidden with
  | 0 -> []
  | n -> [ sprintf "  ... %d more not shown" n ]

let private marker (t: Tab) : string =
  sprintf "%s%s" (match t.Active with | true -> "[*] " | false -> "[ ] ") (match t.Unsaved with | true -> t.Label + " (unsaved)" | false -> t.Label)

let private renderEditor (tabs: Tab list) (e: EditorFacts) : string list =
  let active = tabs |> List.tryFind (fun t -> t.Active) |> Option.map (fun t -> t.Label) |> Option.defaultValue "(no tab)"
  let first = e.Lines |> List.tryHead |> Option.map (fun l -> l.Number)
  let last = e.Lines |> List.tryLast |> Option.map (fun l -> l.Number)
  let range =
    match first, last with
    | Some a, Some b -> sprintf "lines %d-%d visible" a b
    | _ -> "no lines visible"
  let width = match last with | Some n -> (string n).Length | None -> 1
  let notesFor kind n = e.Notes |> List.filter (fun x -> x.Kind = kind && x.Line = n)
  let body =
    e.Lines
    |> List.collect (fun l ->
      let lenses = notesFor CodeLens l.Number |> List.map (fun x -> sprintf "%s     [lens] %s" (String(' ', width)) (oneLine x.Text))
      let inlineText = notesFor InlineText l.Number |> List.map (fun x -> sprintf "  [deco] %s" (oneLine x.Text)) |> String.concat ""
      let gutter = notesFor GutterMark l.Number |> List.map (fun x -> sprintf "  [gutter] %s" (oneLine x.Text)) |> String.concat ""
      lenses @ [ sprintf "%s | %s%s%s" ((string l.Number).PadLeft width) (clip l.Text) inlineText gutter ])
  sprintf "--- editor: %s (%s) ---" active range :: body

let private renderQuick (q: QuickFacts) : string list =
  let rows, hidden = takeRows q.Rows
  sprintf "--- quick input is OPEN, text: \"%s\" ---" q.Input
  :: (rows |> List.map (fun r -> sprintf "  %s %s" (match r.Focused with | true -> ">" | false -> " ") (oneLine r.Label)))
  @ moreLine hidden

let private renderRow (r: Row) : string =
  let indent = String(' ', 2 * max 0 (r.Level - 1))
  let twisty = match r.Expanded with | Some true -> "[-] " | Some false -> "[+] " | None -> ""
  sprintf "  %s%s%s%s" indent twisty (oneLine r.Label) (match r.Selected with | true -> "  (selected)" | false -> "")

let private renderPane (p: Pane) : string list =
  let rows, hidden = takeRows p.Rows
  let head = sprintf "[%s] %s" p.Title (match p.Expanded with | true -> "expanded" | false -> "collapsed")
  let body = match String.IsNullOrWhiteSpace p.Body with | true -> [] | false -> [ sprintf "  text: %s" (oneLine p.Body) ]
  head :: body @ (rows |> List.map renderRow) @ moreLine hidden

let private renderPanel (p: PanelFacts) : string list =
  let tabs = p.Tabs |> List.map (fun t -> match t.Active with | true -> sprintf "[%s]" t.Label | false -> t.Label) |> String.concat " | "
  let lines = p.Lines |> List.truncate MaxPanelLines |> List.map (fun l -> "  " + clip l)
  let hidden = max 0 (List.length p.Lines - MaxPanelLines)
  sprintf "--- panel: %s ---" tabs :: lines @ moreLine hidden

let private renderToast (t: Toast) : string =
  let buttons = match t.Buttons with | [] -> "" | bs -> sprintf "  buttons: %s" (String.Join(" | ", bs |> List.map (fun b -> sprintf "[%s]" b)))
  sprintf "  [%s] %s%s" t.Severity (oneLine t.Message) buttons

let private header (f: Facts) : string list =
  [ sprintf "window: %s" f.WindowTitle; sprintf "focus: %s" f.Focus ]

/// The lines after the window and focus: whatever is open on top, then notifications and the status bar.
let private overlays (f: Facts) : string list =
  let quick = f.Quick |> Option.map renderQuick |> Option.defaultValue []
  let dialog =
    f.Dialog
    |> Option.map (fun d ->
      [ sprintf "--- DIALOG: %s ---" (oneLine d.Message)
        sprintf "  buttons: %s" (String.Join(" | ", d.Buttons |> List.map (fun b -> sprintf "[%s]" b))) ])
    |> Option.defaultValue []
  let toasts = match f.Toasts with | [] -> [] | ts -> "--- notifications ---" :: (ts |> List.map renderToast)
  let status = "--- status bar ---" :: [ "  " + String.Join("  |  ", f.StatusBar |> List.map oneLine) ]
  let hover = f.Hover |> Option.map (fun h -> [ "--- hover ---"; "  " + oneLine h ]) |> Option.defaultValue []
  let suggest = match f.Suggest with | [] -> [] | s -> "--- suggestions ---" :: (s |> List.truncate MaxRows |> List.map (fun x -> "  " + oneLine x))
  dialog @ quick @ suggest @ hover @ toasts @ status

/// The part of the window that matters right after an action: focus, an open
/// quick input, a dialog, notifications and the status bar. Returned by every
/// command so a model sees what its action did without asking for a snapshot.
let renderBrief (f: Facts) : string = String.Join("\n", header f @ overlays f)

/// Everything the window shows, top to bottom.
let render (f: Facts) : string =
  let activity =
    sprintf "activity bar: %s" (String.Join(" | ", f.ActivityBar |> List.map (fun t -> match t.Active with | true -> sprintf "[%s]" t.Label | false -> t.Label)))
  let tabs = match f.Tabs with | [] -> [ "--- tabs: none open ---" ] | ts -> "--- tabs ---" :: (ts |> List.map marker)
  let editor = f.Editor |> Option.map (renderEditor f.Tabs) |> Option.defaultValue []
  let side =
    f.SideBar
    |> Option.map (fun s -> sprintf "--- side bar: %s ---" s.Title :: (s.Panes |> List.collect renderPane))
    |> Option.defaultValue [ "--- side bar: closed ---" ]
  let panel = f.Panel |> Option.map renderPanel |> Option.defaultValue []
  String.Join("\n", header f @ [ activity ] @ tabs @ editor @ side @ panel @ overlays f)

// --- the sidecar a design review reads beside a screenshot ------------------------

/// What a shot's text file says about the moment it was taken.
type Sidecar =
  { Name: string
    Regions: string list
    Width: int
    Height: int
    TakenAt: string
    Activation: string
    Facts: Facts }

/// The rows of every SageFs view in the side bar, or why there are none to show.
let private viewLines (f: Facts) : string list =
  match f.SideBar with
  | None -> [ "  (the side bar is closed in this shot, so no view is visible)" ]
  | Some s ->
    match s.Panes with
    | [] -> [ sprintf "  (the \"%s\" side bar has no views)" s.Title ]
    | panes ->
      panes
      |> List.collect (fun p ->
        let rows, hidden = takeRows p.Rows
        let head = sprintf "  [%s] %s, %d row(s)" p.Title (match p.Expanded with | true -> "expanded" | false -> "collapsed") (List.length p.Rows)
        let body = match String.IsNullOrWhiteSpace p.Body with | true -> [] | false -> [ sprintf "    text: %s" (oneLine p.Body) ]
        head :: body @ (rows |> List.map (fun r -> "  " + renderRow r)) @ moreLine hidden)

/// The text file beside NNN-name.png: the state a reviewer needs to cross-check the image,
/// then the same text snapshot a model would have read at that moment.
let renderSidecar (s: Sidecar) : string =
  let f = s.Facts
  let regions = match s.Regions with | [] -> "full window" | rs -> "full window, plus " + String.Join(", ", rs)
  let status = f.StatusBar |> List.map (fun i -> sprintf "  - %s" (oneLine i))
  let toasts =
    match f.Toasts with
    | [] -> [ "  (none showing)" ]
    | ts -> ts |> List.map renderToast
  String.Join(
    "\n",
    [ sprintf "# shot %s" s.Name
      sprintf "taken: %s" s.TakenAt
      sprintf "window: %dx%d (%s)" s.Width s.Height regions
      ""
      "## extension state"
      sprintf "activation: %s" s.Activation
      "status bar items:" ]
    @ status
    @ [ "SageFs views:" ]
    @ viewLines f
    @ [ "notifications:" ]
    @ toasts
    @ [ ""; "## text snapshot (the accessibility view of the same moment)"; render f; "" ])
