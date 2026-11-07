/// The Watch tab of the live bindings pane: a list of expressions the person keeps, each answered from the values the session holds
/// (docs/plans/live-bindings-watch-view.md, step 8). The decisions are `WatchRows` (pure, in Core): a name or a field is read off the walk
/// and runs nothing; an expression that runs code is held until its Run button is clicked. This file draws that, remembers what a click
/// answered, and holds the signals and routes the tab posts to.
module SageFs.Server.LiveBindingsWatch

open System
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Features
open SageFs.Features.Tweak.WatchRows
open SageFs.Server.DashboardFragments

module WatchSignals =
  /// What the add box holds.
  let [<Literal>] Input = "watchInput"
  /// What the last add, remove or run said when it could not do what was asked.
  let [<Literal>] Note = "watchNote"
  let [<Literal>] AddEndpoint = "/dashboard/watch/add"
  let [<Literal>] RemoveEndpoint = "/dashboard/watch/remove"
  let [<Literal>] RunEndpoint = "/dashboard/watch/run"

  /// Declared once in the page shell with the dock's other signals.
  let initial : XmlAttribute list = [ Ds.signal (Input, ""); Ds.signal (Note, "") ]

/// How many answers to clicks are kept at once, across sessions. A click's answer is a convenience for the row it came from, so the
/// oldest goes first.
let [<Literal>] RunCapacity = 512

/// What each click answered, by session and expression. A row that runs code shows its answer until the next click replaces it.
let private runs = PageChoices<RunOutcome option>(RunCapacity)

let private runKey (sessionId: string) (expression: WatchExpression) : string = sprintf "%s\n%s" sessionId (WatchExpression.text expression)

let recordRun (sessionId: string) (expression: WatchExpression) (outcome: RunOutcome) : unit =
  runs.Set(runKey sessionId expression, Some outcome)

let lastRun (sessionId: string) (expression: WatchExpression) : RunOutcome option =
  runs.Find(runKey sessionId expression, None)

/// One expression and what it shows.
type WatchRowView = { Expression: WatchExpression; Answer: Answer }

/// The rows for a session's list: each expression answered from the walk in the mode the session is in, with what its last click said.
let rowsFor (sessionId: string) (mode: LiveValueTree.WalkMode) (walk: LiveValueTree.LiveValueSnapshot option) (expressions: WatchExpression list) : WatchRowView list =
  expressions
  |> List.map (fun expression -> { Expression = expression; Answer = answer mode walk (lastRun sessionId expression) expression })

/// The state of a row as a word, so a row never says it with colour alone.
let stateWord (answer: Answer) : string =
  match answer with
  | Answer.Value _ -> "value"
  | Answer.Held _ -> "held"
  | Answer.Ran _ -> "ran"
  | Answer.RunFailed _ -> "failed"
  | Answer.NotFound _ -> "not found"

let private postFor (endpoint: string) (expression: WatchExpression) : string =
  Ds.post (sprintf "%s?e=%s" endpoint (Uri.EscapeDataString(WatchExpression.text expression)))

let private button (word: string) (label: string) (id: string) (click: string) : XmlNode =
  Elem.button
    [ Attr.class' "session-btn session-btn-labeled"
      Attr.type' "button"
      testid id
      Attr.create "aria-label" (attrEnc label)
      Ds.onClick click ]
    [ Elem.span [ Attr.class' "session-btn-word" ] [ textEnc word ] ]

let private answerNodes (answer: Answer) : XmlNode list =
  match answer with
  | Answer.Value(preview, typeName) ->
    [ Elem.code [ Attr.class' "live-watch-value live-value"; testid "live-watch-value" ] [ textEnc preview ]
      Elem.span [ Attr.class' "live-watch-type" ] [ textEnc typeName ] ]
  | Answer.Held why ->
    [ Elem.span [ Attr.class' "live-watch-why"; testid "live-watch-why" ] [ textEnc (sprintf "not run: %s" why) ] ]
  | Answer.Ran text ->
    [ Elem.code [ Attr.class' "live-watch-value live-value"; testid "live-watch-value" ] [ textEnc text ] ]
  | Answer.RunFailed reason ->
    [ Elem.span [ Attr.class' "live-watch-why live-watch-failed"; testid "live-watch-why" ] [ textEnc reason ] ]
  | Answer.NotFound words ->
    [ Elem.span [ Attr.class' "live-watch-why"; testid "live-watch-why" ] [ textEnc words ] ]

let private rowNode (row: WatchRowView) : XmlNode =
  let runWord =
    match row.Answer with
    | Answer.Held _ -> Some "Run"
    | Answer.Ran _
    | Answer.RunFailed _ ->
      match WatchExpression.reach row.Expression with
      | ExpressionReach.RunsCode _ -> Some "Run again"
      | ExpressionReach.NamesOnly _ -> None
    | Answer.Value _
    | Answer.NotFound _ -> None
  Elem.div [ Attr.class' "live-watch-row"; testid "live-watch-row" ] [
    Elem.code [ Attr.class' "live-watch-expression"; testid "live-watch-expression" ] [ textEnc (WatchExpression.text row.Expression) ]
    Elem.span [ Attr.class' "live-watch-state"; testid "live-watch-state" ] [ textEnc (stateWord row.Answer) ]
    Elem.span [ Attr.class' "live-watch-answer" ] (answerNodes row.Answer)
    Elem.span [ Attr.class' "live-watch-actions" ] [
      match runWord with
      | Some word -> button word (sprintf "%s %s" word (WatchExpression.text row.Expression)) "live-watch-run" (postFor WatchSignals.RunEndpoint row.Expression)
      | None -> ()
      button "Remove" (sprintf "Stop watching %s" (WatchExpression.text row.Expression)) "live-watch-remove" (postFor WatchSignals.RemoveEndpoint row.Expression) ] ]

let [<Literal>] RunsCodeWords = "A name or a field is read from the session's values. Anything else runs your code, so it waits for Run."

/// The tab's body: the box to add an expression, what the last attempt said, and the rows.
let render (rows: WatchRowView list) : XmlNode =
  let add = Ds.post WatchSignals.AddEndpoint
  Elem.div [ Attr.id "live-watch-body"; Attr.class' "live-watch"; testid "live-watch" ] [
    Elem.div [ Attr.class' "live-watch-add" ] [
      Elem.input
        [ Attr.id "live-watch-input"
          Attr.class' "live-watch-input"
          Attr.type' "text"
          Attr.create "placeholder" "an expression, such as player.Lives"
          Attr.create "aria-label" "An expression to watch. Press Enter to add it."
          Attr.create "autocomplete" "off"
          Attr.create "spellcheck" "false"
          testid "live-watch-input"
          Ds.bind WatchSignals.Input
          Ds.onEvent ("keydown", attrEnc (sprintf "if (event.key === 'Enter') { event.preventDefault(); %s }" add)) ]
      button "Add" "Add the expression to the Watch tab" "live-watch-add" add ]
    Elem.div [ Attr.class' "live-watch-note"; testid "live-watch-note"; Attr.create "role" "status"; Ds.text (sprintf "$%s" WatchSignals.Note) ] []
    Elem.div [ Attr.class' "live-watch-help" ] [ textEnc RunsCodeWords ]
    match rows with
    | [] -> Elem.div [ Attr.class' "live-empty"; testid "live-watch-empty" ] [ textEnc (WatchTab.emptyWords WatchTab.Watch) ]
    | rows -> Elem.div [ Attr.class' "live-watch-rows"; testid "live-watch-rows" ] (rows |> List.map rowNode) ]

/// Where the layers a session's settings live are: the global file, and the app's own `.SageFs/settings.json` (the session's working
/// directory is "this app"). A session with no working directory has only the global layer.
let pathsFor (workingDir: string) : ConfigPaths =
  let repo =
    match String.IsNullOrWhiteSpace workingDir with
    | true -> NoRepoCheckout
    | false -> RepoRootAt workingDir
  { GlobalDir = DaemonState.SageFsDir; Repo = repo }

/// The list a session's app keeps, or why the saved list could not be read.
let expressionsAt (paths: ConfigPaths) : Result<WatchExpression list, string> =
  match SettingsCatalog.resolve paths WatchSettings.descriptor with
  | Ok provenance -> Ok (WatchSettings.expressionsOf provenance.Effective)
  | Error why -> Error (ConfigError.describe why)

/// The Watch tab for a session: its list, answered from the walk in the session's mode. A list that cannot be read is a note on the
/// tab, with no rows, so the tab never looks empty over a list that is still on disk.
let partsFor (sessionId: string) (paths: ConfigPaths) (mode: LiveValueTree.WalkMode) (walk: LiveValueTree.LiveValueSnapshot option) : LiveBindingsViewPanel.WatchTabParts =
  match expressionsAt paths with
  | Ok expressions ->
    let rows = rowsFor sessionId mode walk expressions
    { Body = render rows; Count = rows.Length }
  | Error why ->
    { Body =
        Elem.div [ Attr.class' "live-watch"; testid "live-watch" ] [
          Elem.div [ Attr.class' "live-watch-note"; testid "live-watch-problem" ] [ textEnc (sprintf "The saved list could not be read: %s" why) ] ]
      Count = 0 }
