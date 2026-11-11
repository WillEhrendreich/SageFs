/// Hostile text through the dashboard's attribute and Datastar-expression sinks.
///
/// The dashboard origin may POST /dashboard/eval, so script that gets into the page runs F# as the user. Text sinks were
/// already escaped; attribute values and Datastar expressions were not: Falco.Markup writes `Attr.create` values verbatim,
/// and Falco.Datastar writes `Ds.signal (name, text)` as `'text'` and an `Ds.onEvent` expression as it is. These tests feed
/// hostile strings (an agent name, a repo's settings value, a file path, a theme name, a completion, a search) through the
/// real renderers and check two things against a rendering of plain text in the same place:
///   - the markup has the same elements and the same attribute names (nothing the text made), and
///   - every Datastar expression has the same code once its string literals are blanked (the text stayed inside a literal).
module SageFs.Tests.DashboardSinkTests

open System
open System.Net
open System.Text
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Server
open SageFs.Server.DashboardFragments
open SageFs.Server.DashboardSinks
open SageFs.Tests.DashboardPanelVisibilityTests

/// Strings that break out of an HTML attribute, a JS string literal, a script element or a template literal, or that a
/// JS string literal cannot hold raw.
let hostileStrings : string list =
  [ "\"><img src=x onerror=alert(1)>"
    "\" data-on:load=\"alert(1)"
    "' data-on:load='alert(1)"
    "';alert(1);//"
    "\\';alert(1);//"
    "</script><script>alert(1)</script>"
    "\\"
    "`${alert(1)}`"
    "a\nb\r\nc"
    "line\u2028sep\u2029para"
    "javascript:alert(1)"
    "&quot;&#39;&amp;<b>" ]

/// What a renderer gets in the hostile string's place for the baseline rendering.
let plainText = "plainvalue"

// ── a minimal HTML tokenizer: elements, attribute names, decoded attribute values ──

type MarkupToken =
  | OpenTag of tag: string * attributes: (string * string) list
  | CloseTag of tag: string

/// The tokens of `html`, read as an HTML parser reads tags: a `<` followed by a letter opens a tag, attribute values are
/// double-quoted, single-quoted or bare, and the text of a `script` or `style` element runs to its end tag. Values are
/// entity-decoded, as the browser hands them to Datastar.
let markupTokens (html: string) : MarkupToken list =
  let tokens = ResizeArray<MarkupToken>()
  let n = html.Length
  let isSpace (c: char) = c = ' ' || c = '\n' || c = '\t' || c = '\r' || c = '\f'
  let mutable i = 0
  while i < n do
    match html.[i] with
    | '<' when i + 1 < n && html.[i + 1] = '/' ->
      let close = html.IndexOf('>', i)
      let stop = match close with | -1 -> n | c -> c
      tokens.Add(CloseTag(html.Substring(i + 2, stop - i - 2).Trim().ToLowerInvariant()))
      i <- stop + 1
    | '<' when i + 1 < n && Char.IsLetter html.[i + 1] ->
      let mutable j = i + 1
      while j < n && not (isSpace html.[j]) && html.[j] <> '>' && html.[j] <> '/' do j <- j + 1
      let tag = html.Substring(i + 1, j - i - 1).ToLowerInvariant()
      let attributes = ResizeArray<string * string>()
      let mutable inTag = true
      while inTag && j < n do
        while j < n && (isSpace html.[j] || html.[j] = '/') do j <- j + 1
        match j < n && html.[j] = '>' with
        | true ->
          inTag <- false
          j <- j + 1
        | false when j >= n -> inTag <- false
        | false ->
          let start = j
          while j < n && not (isSpace html.[j]) && html.[j] <> '=' && html.[j] <> '>' do j <- j + 1
          let name = html.Substring(start, j - start).ToLowerInvariant()
          match j < n && html.[j] = '=' with
          | true ->
            j <- j + 1
            match j < n && (html.[j] = '"' || html.[j] = '\'') with
            | true ->
              let quote = html.[j]
              let close = html.IndexOf(quote, j + 1)
              let stop = match close with | -1 -> n | c -> c
              attributes.Add((name, WebUtility.HtmlDecode(html.Substring(j + 1, stop - j - 1))))
              j <- stop + 1
            | false ->
              let vstart = j
              while j < n && not (isSpace html.[j]) && html.[j] <> '>' do j <- j + 1
              attributes.Add((name, WebUtility.HtmlDecode(html.Substring(vstart, j - vstart))))
          | false -> attributes.Add((name, ""))
      tokens.Add(OpenTag(tag, List.ofSeq attributes))
      match tag with
      | "script" | "style" ->
        let close = html.IndexOf("</" + tag, j, StringComparison.OrdinalIgnoreCase)
        i <- (match close with | -1 -> n | c -> c)
      | _ -> i <- j
    | _ -> i <- i + 1
  List.ofSeq tokens

/// The elements and attribute names, in order: what text cannot change without having made markup.
let markupShape (html: string) : string list =
  markupTokens html
  |> List.map (fun token ->
    match token with
    | OpenTag(tag, attributes) -> sprintf "<%s %s>" tag (attributes |> List.map fst |> String.concat " ")
    | CloseTag tag -> sprintf "</%s>" tag)

// ── Datastar expressions with their string literals blanked ──

/// `expr` with the inside of every string literal removed, so two expressions that differ only in the text their literals
/// hold compare equal. A literal a raw line end breaks, or one that never closes, leaves a marker that compares unequal.
let codeSkeleton (expr: string) : string =
  let sb = StringBuilder()
  let n = expr.Length
  let mutable i = 0
  while i < n do
    match expr.[i] with
    | '\'' | '"' | '`' as quote ->
      sb.Append(quote).Append(quote) |> ignore
      let mutable j = i + 1
      let mutable closed = false
      while not closed && j < n do
        match expr.[j] with
        | '\\' -> j <- j + 2
        | c when c = quote ->
          closed <- true
          j <- j + 1
        | '\n' | '\r' when quote <> '`' ->
          sb.Append("<LINE-END-IN-LITERAL>") |> ignore
          j <- j + 1
        | '$' when quote = '`' && j + 1 < n && expr.[j + 1] = '{' ->
          sb.Append("<TEMPLATE-SUBSTITUTION>") |> ignore
          j <- j + 1
        | _ -> j <- j + 1
      match closed with
      | true -> ()
      | false -> sb.Append("<UNCLOSED-LITERAL>") |> ignore
      i <- j
    | c ->
      sb.Append(c) |> ignore
      i <- i + 1
  sb.ToString()

/// The attributes Datastar evaluates as script. A signal's value is a JS expression too.
let isDatastarExpression (name: string) : bool =
  name.StartsWith("data-on", StringComparison.Ordinal)
  || name.StartsWith("data-signals", StringComparison.Ordinal)
  || name.StartsWith("data-show", StringComparison.Ordinal)
  || name.StartsWith("data-class", StringComparison.Ordinal)
  || name.StartsWith("data-attr", StringComparison.Ordinal)
  || name.StartsWith("data-effect", StringComparison.Ordinal)
  || name.StartsWith("data-text", StringComparison.Ordinal)
  || name.StartsWith("data-computed", StringComparison.Ordinal)

/// Each Datastar expression in the markup with its literals blanked, as `name=skeleton`. Numbers are blanked too: the shell
/// seeds its heartbeat signal from the clock, so two renderings differ there.
let expressionSkeletons (html: string) : string list =
  markupTokens html
  |> List.collect (fun token ->
    match token with
    | OpenTag(_, attributes) ->
      attributes
      |> List.filter (fst >> isDatastarExpression)
      |> List.map (fun (name, value) ->
        sprintf "%s=%s" name (Text.RegularExpressions.Regex.Replace(codeSkeleton value, @"\d+", "0")))
    | CloseTag _ -> [])

/// The markup `render hostile` makes has the shape and the expression code of `render plainText`, for every hostile string.
let expectInertFor (what: string) (render: string -> string) =
  let baseline = render plainText
  for hostile in hostileStrings do
    let html = render hostile
    markupShape html
    |> Expect.equal (sprintf "%s: the text %A made or removed markup" what hostile) (markupShape baseline)
    expressionSkeletons html
    |> Expect.equal (sprintf "%s: the text %A changed a Datastar expression's code" what hostile) (expressionSkeletons baseline)

// ── the real renderers, each with a hostile string where outside text arrives ──

let private machine = CohortScope.Machine
let private join (who: MemberId) = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)
let private epoch = DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)

/// The cohort panel on a vacant seat, with a present member whose agent name is `name` (any MCP caller picks it).
let cohortVacantSeat (name: string) : string =
  let alice = MemberId.Minted "alice"
  ledgerAfter [ join alice; join (MemberId.Minted name); CohortCommand.Depart(alice, machine) ]
  |> frameOf
  |> renderCohortPanel
  |> renderNode

/// The cohort panel on a seat whose holder went silent: the holder is named `name`, and a present member can replace it.
let cohortSilentHolder (name: string) : string =
  let frame = ledgerAfter [ join (MemberId.Minted name); join (MemberId.Minted "bob") ] |> frameOf
  let silence = Features.CohortStatusText.HolderSilence.SilentSince(epoch, epoch + Timeouts.cohortLeaseWindow)
  renderCohortPanelCore (fun subject -> "ticket-" + string subject.Length) silence (epoch + Timeouts.cohortLeaseWindow) frame
  |> renderNode

/// A setting row for the first editable session option, whose saved repo-layer value renders as `saved`, in a panel opened
/// for the repo at `repoDir`. `<repo>/.SageFs/settings.json` comes with a cloned repo.
let settingsPanel (saved: string) (repoDir: string) : string =
  let d =
    SessionOptionSettings.all
    |> List.find (fun d ->
      match d.Applicability with
      | ReadOnly _ -> false
      | Live | RestartRequired | NextSession -> true)
  let d = { d with Render = fun _ -> saved }
  let provenance : Provenance = { Effective = d.Default; Source = LRepo; PerLayer = [ LDefault, d.Default; LRepo, d.Default ] }
  let row : SettingsPanel.SettingRow =
    { Descriptor = d
      Resolved = Ok provenance
      InUse = InUseSameAsSaved
      Override = SettingsCatalog.NotOverridden
      Notes = [] }
  SettingsPanel.renderPanel SettingsPanel.Quiet (RepoRootAt repoDir) [ row ] |> renderNode

/// The coverage treemap with a file whose path is `path` (a file in a cloned repo can be named anything).
let treemap (path: string) : string =
  let leaf : Features.Treemap.CoverageTreemapNode =
    { Id = path; Name = path; Kind = Features.Treemap.CoverageNodeKind.File; Weight = 3.0; ProbeCount = 3; CoveredCount = 2
      Status = Features.Treemap.CoverageStatus.Covered; Children = [] }
  let root : Features.Treemap.CoverageTreemapNode =
    { leaf with Id = "root"; Name = "root"; Kind = Features.Treemap.CoverageNodeKind.Project; Children = [ leaf ] }
  renderCoverageTreemap "abcd1234" (Some root) |> renderNode

/// The completion dropdown offering `text` (FCS offers any identifier, including double-backtick names from a dependency).
let completions (text: string) : string =
  let item : Features.AutoCompletion.CompletionItem =
    { DisplayText = text; ReplacementText = text; Kind = Features.AutoCompletion.CompletionKind.Method; GetDescription = None }
  renderCompletionDropdown [ item ] 3 |> renderNode

/// The app output pane holding the search `search`.
let appOutputSearch (search: string) : string =
  AppOutputView.renderBody (AppOutputPane.AppOutputPane.create |> AppOutputPane.AppOutputPane.setSearch search) DockPanes.PinnedByUser
  |> renderNode

/// The page shell, with `text` as the default working directory, the viewing session and the client id.
let shell (text: string) : string =
  Dashboard.renderShell "0.0.0" text text text (Elem.div [] []) |> renderNode

[<Tests>]
let rendererTests =
  testList "Dashboard sinks.renderers" [
    testCase "WHY - an agent name on a vacant seat's Seat button stays text, in the attribute and in the click's script" <| fun _ ->
      expectInertFor "cohort Seat button" cohortVacantSeat

    testCase "WHY - a silent holder's name and the member's on a Replace button stay text" <| fun _ ->
      expectInertFor "cohort Replace button" cohortSilentHolder

    testCase "WHY - a setting value saved in a cloned repo's settings file stays inside its signal" <| fun _ ->
      expectInertFor "settings value signal" (fun text -> settingsPanel text "/work/repo")

    testCase "WHY - the repo directory the settings panel names stays inside its signal" <| fun _ ->
      expectInertFor "settings repo dir signal" (settingsPanel "true")

    testCase "WHY - a file path in the coverage treemap stays inside the drill expressions" <| fun _ ->
      expectInertFor "coverage treemap" treemap

    testCase "WHY - a completion's text stays inside the click that inserts it" <| fun _ ->
      expectInertFor "completion dropdown" completions

    testCase "WHY - the app output search stays inside the input's value" <| fun _ ->
      expectInertFor "app output search" appOutputSearch

    testCase "WHY - the default directory, viewing session and client id stay inside the page's signals" <| fun _ ->
      expectInertFor "page shell" shell

    testCase "WHY - a theme name the picker is handed stays inside its signal and its option value" <| fun _ ->
      expectInertFor "theme picker" (renderThemePicker >> renderNode)
  ]

/// Read a single-quoted JS string literal the way JS does, for the escapes `Encode.jsInner` writes. None when it is not one
/// literal from end to end.
let readJsLiteral (literal: string) : string option =
  match literal.Length >= 2 && literal.[0] = '\'' && literal.[literal.Length - 1] = '\'' with
  | false -> None
  | true ->
    let body = literal.Substring(1, literal.Length - 2)
    let sb = StringBuilder()
    let mutable i = 0
    let mutable ok = true
    while ok && i < body.Length do
      match body.[i] with
      | '\\' when i + 1 < body.Length ->
        match body.[i + 1] with
        | 'n' -> sb.Append('\n') |> ignore; i <- i + 2
        | 'r' -> sb.Append('\r') |> ignore; i <- i + 2
        | 't' -> sb.Append('\t') |> ignore; i <- i + 2
        | 'x' when i + 3 < body.Length ->
          sb.Append(char (Convert.ToInt32(body.Substring(i + 2, 2), 16))) |> ignore
          i <- i + 4
        | 'u' when i + 5 < body.Length ->
          sb.Append(char (Convert.ToInt32(body.Substring(i + 2, 4), 16))) |> ignore
          i <- i + 6
        | c ->
          sb.Append(c) |> ignore
          i <- i + 2
      | '\'' | '\n' | '\r' | ' ' | ' ' -> ok <- false
      | c ->
        sb.Append(c) |> ignore
        i <- i + 1
    match ok with
    | true -> Some(sb.ToString())
    | false -> None

/// Hostile text, and anything FsCheck makes.
let private textCases (generated: string) : string list =
  (match isNull generated with | true -> "" | false -> generated) :: hostileStrings

/// The value of the one attribute on the one element `attr` renders onto, as the browser hands it on.
let private renderedValue (attr: XmlAttribute) : string =
  match markupTokens (renderNode (Elem.div [ attr ] [])) with
  | [ OpenTag("div", [ _, value ]); CloseTag "div" ] -> value
  | other -> failwithf "expected one div with one attribute, got %A" other

[<Tests>]
let sinkTests =
  testList "Dashboard sinks.helpers" [
    testProperty "WHY - Sink.attr gives the browser back exactly the text, on one attribute" <| fun (generated: string) ->
      for text in textCases generated do
        renderedValue (Sink.attr "title" (UserText.ofString text))
        |> Expect.equal (sprintf "the attribute holds %A" text) text

    testProperty "WHY - Sink.signal's value is one JS literal holding exactly the text" <| fun (generated: string) ->
      for text in textCases generated do
        renderedValue (Sink.signal "s" (UserText.ofString text))
        |> readJsLiteral
        |> Expect.equal (sprintf "the signal holds %A" text) (Some text)

    testProperty "WHY - Sink.jsArg is one JS literal holding exactly the text" <| fun (generated: string) ->
      for text in textCases generated do
        Sink.jsArg (UserText.ofString text) |> readJsLiteral |> Expect.equal (sprintf "the literal holds %A" text) (Some text)

    testCase "WHY - an attribute expression keeps && and quotes readable, and only a reference-shaped & is encoded" <| fun _ ->
      Encode.attrValue "a && b == 'c' \"d\" &amp;" |> Expect.equal "only the quote and the &a are encoded" "a && b == 'c' &quot;d&quot; &amp;amp;"

    testProperty "WHY - an expression through Sink.onEvent reaches Datastar unchanged" <| fun (generated: string) ->
      for text in textCases generated do
        let expr = sprintf "if(confirm(%s)){%s}" (Sink.jsArg (UserText.ofString text)) (Sink.post "/dashboard/x?y=1")
        renderedValue (Sink.onEvent (DomEvent.Click, expr)) |> Expect.equal "the handler is the expression" expr
        renderedValue (Sink.onClick expr) |> Expect.equal "the click is the expression" expr
        renderedValue (Sink.show expr) |> Expect.equal "the show is the expression" expr
        renderedValue (Sink.classWhen (CssClass "on", expr)) |> Expect.equal "the class condition is the expression" expr
        renderedValue (Sink.attrExpr (AttrName "title", expr)) |> Expect.equal "the attribute expression is the expression" expr

    testCase "WHY - each DomEvent names the browser event Datastar listens for, and a debounce carries its window" <| fun _ ->
      [ DomEvent.Click; DomEvent.DoubleClick; DomEvent.Change; DomEvent.Input; DomEvent.KeyDown; DomEvent.Scroll
        DomEvent.Close; DomEvent.PointerMove; DomEvent.PointerUp; DomEvent.InputDebounced "300ms" ]
      |> List.map DomEvent.name
      |> Expect.equal "the event names"
           [ "click"; "dblclick"; "change"; "input"; "keydown"; "scroll"; "close"; "pointermove"; "pointerup"
             "input.debounce_300ms" ]

    testCase "WHY - Sink.onEvent writes the same attribute Datastar writes for the event's name" <| fun _ ->
      let tokens attr = markupTokens (renderNode (Elem.div [ attr ] []))
      tokens (Sink.onEvent (DomEvent.InputDebounced "250ms", "f()"))
      |> Expect.equal "the debounced input attribute" (tokens (Falco.Datastar.Ds.onEvent ("input.debounce_250ms", "f()")))

    testCase "WHY - Sink.post puts the URL in a literal, so a quote in it cannot end the call" <| fun _ ->
      Sink.post "/a/b'c" |> Expect.equal "the quote is escaped" "@post('/a/b\\'c')"

    testCase "WHY - a null reads as empty text" <| fun _ ->
      UserText.ofString null |> UserText.value |> Expect.equal "null is empty" ""
  ]

[<Tests>]
let themeTests =
  testList "Dashboard sinks.theme names" [
    testCase "WHY - a preset's name is known in any case and comes back in the preset's spelling" <| fun _ ->
      ThemePresets.known "kanagawa" |> Expect.equal "case does not matter" (Ok "Kanagawa")

    testCase "WHY - a name that is no preset is refused, so it is never saved or drawn" <| fun _ ->
      for hostile in hostileStrings do
        match ThemePresets.known hostile with
        | Ok name -> failwithf "%A was taken as the theme %s" hostile name
        | Error(ThemePresets.UnknownTheme name) -> name |> Expect.equal "the refusal names what was asked for" hostile

    testCase "WHY - a themes file written before names were checked loses its unknown entries on load" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-theme-%s" (Guid.NewGuid().ToString "N"))
      IO.Directory.CreateDirectory dir |> ignore
      try
        IO.File.WriteAllText(IO.Path.Combine(dir, "themes.json"), """{"/w/a":"Nordic","/w/b":"';alert(1);//"}""")
        let loaded = DashboardTypes.loadThemes dir
        loaded.Keys |> List.ofSeq |> Expect.equal "only the preset is kept" [ "/w/a" ]
      finally
        IO.Directory.Delete(dir, true)
  ]

[<Tests>]
let checkerTests =
  testList "Dashboard sinks.checker" [
    testCase "WHY - the shape check sees an attribute a quote made" <| fun _ ->
      let render (text: string) = sprintf "<div title=\"%s\"></div>" text
      markupShape (render "\" onclick=\"x") |> Expect.notEqual "a raw quote adds an attribute" (markupShape (render plainText))

    testCase "WHY - the skeleton check sees code a quote let out of a literal" <| fun _ ->
      codeSkeleton "f('';alert(1);//')" |> Expect.notEqual "a raw quote ends the literal" (codeSkeleton "f('plain')")

    testCase "WHY - the skeleton check sees a raw line end inside a literal" <| fun _ ->
      codeSkeleton "f('a\nb')" |> Expect.notEqual "a line end breaks a single-quoted literal" (codeSkeleton "f('ab')")
  ]

// ─── Structural guard ───────────────────────────────────────────────
// A renderer reaches an attribute or a Datastar expression only through `DashboardSinks.Sink`. The raw Falco calls that
// write a value as it is given (`Attr.create`, `Ds.signal`, `Ds.onEvent`, `Ds.onClick`, `Ds.show`, `Ds.post`) may appear
// outside DashboardSinks.fs only with literal arguments. The budget is zero. A use that must stay carries the comment
// `// sink-literal: <why>` on its line, which a reviewer reads; nothing is allowed by count.

/// A plain F# string literal (not triple-quoted).
let private literal = "\"(?:[^\"\\\\]|\\\\.)*\"(?!\")"

/// What may follow each raw call when every argument is a literal.
let private literalOnly : (string * Text.RegularExpressions.Regex) list =
  let rx (pattern: string) = Text.RegularExpressions.Regex(pattern, Text.RegularExpressions.RegexOptions.Compiled)
  let post = sprintf @"\(\s*Ds\.post\s+%s\s*\)" literal
  // The name's literal is matched without the triple-quote guard: `Attr.create "aria-label""Theme"` is two literals.
  [ "Attr.create", rx (sprintf @"^\s*""(?:[^""\\]|\\.)*""\s*%s" literal)
    "Ds.signal", rx (sprintf @"^\s*\(\s*[\w.]+\s*,\s*(?:true|false|-?\d+L?|%s|Array\.empty<string>)\s*\)" literal)
    "Ds.onEvent", rx (sprintf @"^\s*\(\s*%s\s*,\s*(?:%s|%s)\s*\)" literal literal post)
    "Ds.onClick", rx (sprintf @"^\s*(?:%s|%s)" literal post)
    "Ds.show", rx (sprintf @"^\s*%s" literal)
    "Ds.post", rx (sprintf @"^\s*%s" literal) ]

/// The dashboard's renderer files: every file in SageFs/ that opens Falco.Markup or Falco.Datastar, except the sinks.
let rendererFiles () : string list =
  let dir = RepoPaths.repoPathFull [| "SageFs" |]
  IO.Directory.GetFiles(dir, "*.fs")
  |> Array.filter (fun path -> IO.Path.GetFileName path <> "DashboardSinks.fs")
  |> Array.filter (fun path ->
    let text = IO.File.ReadAllText path
    text.Contains "open Falco.Markup" || text.Contains "open Falco.Datastar")
  |> Array.sort
  |> List.ofArray

/// Each raw call in `lines` whose arguments are not all literals, as `file:line call`.
let rawSinkUses (file: string) (lines: string array) : string list =
  [ for i in 0 .. lines.Length - 1 do
      let line = lines.[i]
      let trimmed = line.TrimStart()
      match trimmed.StartsWith("//", StringComparison.Ordinal) || line.Contains "// sink-literal:" with
      | true -> ()
      | false ->
        for call, allowed in literalOnly do
          for m in Text.RegularExpressions.Regex.Matches(line, Text.RegularExpressions.Regex.Escape call + @"\b(?!')") do
            let rest = line.Substring(m.Index + m.Length)
            match allowed.IsMatch rest with
            | true -> ()
            | false -> yield sprintf "%s:%d %s%s" file (i + 1) call rest ]

[<Tests>]
let sinkSourceTests =
  testList "Dashboard sinks.source" [
    testCase "WHY - no renderer writes a computed value through a raw Falco attribute or Datastar call" <| fun _ ->
      let files = rendererFiles ()
      files |> Expect.isNonEmpty "the scan must find the renderer files"
      let offenders =
        files |> List.collect (fun path -> rawSinkUses (IO.Path.GetFileName path) (IO.File.ReadAllLines path))
      offenders
      |> Expect.isEmpty (
           sprintf
             "a computed value goes through DashboardSinks.Sink (attr, signal, jsArg, onEvent, onClick, show, post). Offenders:\n%s"
             (String.concat "\n" offenders))

    testCase "WHY - the scan sees a computed value in each raw call" <| fun _ ->
      let lines =
        [| "  Attr.create \"title\" name"
           "  Ds.signal (Signals.Theme, selectedTheme)"
           "  Ds.onEvent (\"click\", sprintf \"f('%s')\" name)"
           "  Ds.onEvent"
           "  Ds.onClick (sprintf \"g(%s)\" x)"
           "  Ds.show showExpr"
           "  Ds.post (sprintf \"/x/%s\" sid)" |]
      rawSinkUses "Sample.fs" lines |> List.length |> Expect.equal "every line is caught" lines.Length

    testCase "WHY - the scan lets literals, comments and marked lines through" <| fun _ ->
      let lines =
        [| "  Attr.create \"role\" \"status\""
           "  Attr.create \"aria-label\"\"Theme\""
           "  Ds.signal (Signals.Open, false)"
           "  Ds.signal (Signals.Code, \"\")"
           "  Ds.onEvent (\"click\", \"$open = !$open\")"
           "  Ds.onClick (Ds.post \"/dashboard/clear-output\")"
           "  /// Ds.onEvent writes its expression as it is"
           "  Ds.show x // sink-literal: x is the constant above" |]
      rawSinkUses "Sample.fs" lines |> Expect.isEmpty "nothing here is computed"
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant sinkSourceTests |> ignore
