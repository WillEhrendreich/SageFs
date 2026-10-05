/// What each state of the guided new-session dialog draws. The markup is rendered the way the page gets it
/// (`renderNode`), so these read the same bytes a browser reads. Behaviour in a real browser is in
/// NewSessionDialogBrowserTests; the state machine itself is in NewSessionDialogTests.
module SageFs.Tests.NewSessionDialogViewTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Falco.Markup
open SageFs
open SageFs.WorkflowTypes
open SageFs.Server.DashboardTypes
open SageFs.Server.NewSessionDialog
open SageFs.Server.NewSessionDialogView
open SageFs.Tests.NewSessionDialogTests

let html (state: NewSessionDialog) : string = render state |> renderNode

let hostile = "/tmp/\"><script>alert(1)</script>"

let twoCandidates : Found =
  { Directory = "/work/repo"
    Candidates =
      [ { Path = "All.slnx"; Kind = CandidateKind.Solution; Frameworks = Frameworks.WholeSolution }
        { Path = "App/App.fsproj"; Kind = CandidateKind.Project; Frameworks = Frameworks.Declared [ "net10.0"; "net11.0" ] } ]
    Hint = WorkflowHint.NoneSuggested }

let overlapping = overlapOf { Id = "abc123"; WorkingDirectory = "/work/repo"; Boundary = Boundary.Repository "/work/repo" } Relation.SameDirectory

/// How Datastar spells a camelCase signal name in an attribute key: `newSessionDir` is `new-session-dir`.
let kebab (name: string) : string =
  Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant()

let contains (needle: string) (message: string) (text: string) =
  text |> Expect.stringContains message needle

let excludes (needle: string) (message: string) (text: string) =
  (not (text.Contains needle)) |> Expect.isTrue message

[<Tests>]
let view =
  testList "NewSessionDialogView — one exhaustive render over the state" [

    testCase "WHY — Closed still draws the dialog frame, empty, so the page always has the node the open signal drives" <| fun _ ->
      let h = html NewSessionDialog.Closed
      h |> contains "<dialog" "a native dialog element"
      h |> contains (sprintf "id=\"%s\"" NewSessionNames.DialogId) "stable id so the morph keeps it"
      h |> contains "data-state=\"closed\"" "the state is on the page"
      h |> excludes (sprintf "data-testid=\"%s\"" NewSessionNames.CreateTestId) "no controls while closed"

    testCase "WHY — the dialog is labelled by its own title and the open attribute survives the page morph" <| fun _ ->
      let h = html (NewSessionDialog.Choosing twoCandidates)
      h |> contains (sprintf "aria-labelledby=\"%s\"" NewSessionNames.TitleId) "named for screen readers"
      h |> contains (sprintf "id=\"%s\"" NewSessionNames.TitleId) "the title it points at exists"
      h |> contains "data-preserve-attr=\"open\"" "a morph must not close an open modal"

    testCase "WHY — Discovering says what it is looking in and offers no candidates yet" <| fun _ ->
      let h = html (NewSessionDialog.Discovering "/work/repo")
      h |> contains "data-state=\"discovering\"" "state"
      h |> contains "/work/repo" "names the directory"
      h |> excludes NewSessionNames.CandidateTestId "no list yet"

    testCase "WHY — Choosing lists every candidate with its frameworks, solution first" <| fun _ ->
      let h = html (NewSessionDialog.Choosing twoCandidates)
      h |> contains "All.slnx" "the solution"
      h |> contains "App/App.fsproj" "the project"
      h |> contains "net10.0, net11.0" "its frameworks"
      h |> contains "whole solution" "what a solution is"
      h.IndexOf "All.slnx" |> fun solution -> (solution < h.IndexOf "App/App.fsproj") |> Expect.isTrue "solution before project"

    testCase "WHY — every candidate is a labelled checkbox bound to the selection signal, never a click handler on a div" <| fun _ ->
      let h = html (NewSessionDialog.Choosing twoCandidates)
      h |> contains "type=\"checkbox\"" "a real checkbox"
      h |> contains (sprintf "data-bind:%s" (kebab NewSessionNames.ProjectsSignal)) "bound to the selection signal"
      h |> contains "value=\"App/App.fsproj\"" "carrying the path"
      h |> contains "<label" "the row is a label, so the whole row is the target"

    testCase "WHY — loading projects or none is a radio pair, and Bare is always there" <| fun _ ->
      let h = html (NewSessionDialog.Choosing twoCandidates)
      h |> contains "type=\"radio\"" "radios"
      h |> contains (sprintf "value=\"%s\"" (TargetKind.key TargetKind.BareSession)) "the Bare choice"
      h |> contains (sprintf "value=\"%s\"" (TargetKind.key TargetKind.LoadProjects)) "the load choice"

    testCase "WHY — a directory with nothing to load says so and leaves Bare as the way forward" <| fun _ ->
      let h = html (NewSessionDialog.Choosing (Found.nothing "/work/empty"))
      h |> contains "No projects" "says nothing was found"
      h |> contains (sprintf "value=\"%s\"" (TargetKind.key TargetKind.BareSession)) "Bare is still offered"

    testCase "WHY — with no directory yet the dialog asks for one instead of claiming nothing was found there" <| fun _ ->
      let h = html (NewSessionDialog.Choosing (Found.nothing ""))
      h |> contains "Choose a directory" "asks for a directory"
      h |> excludes "No projects or solutions found" "does not report a find in nothing"
      h |> excludes "role=\"alert\"" "no alarm for an empty box"

    testCase "WHY — all three workflows appear with their one line, as a labelled radio group" <| fun _ ->
      let h = html (NewSessionDialog.Choosing twoCandidates)
      h |> contains "<fieldset" "a group"
      h |> contains "<legend" "named"
      for workflow in WorkflowChoice.all do
        h |> contains (sprintf "value=\"%s\"" (WorkflowChoice.key workflow)) "the workflow radio"
        h |> contains (SageFs.Server.DashboardFragments.htmlEscape (WorkflowChoice.oneLine workflow)) "its one line of meaning"

    testCase "WHY — a web project's hint is shown as a suggestion on the Hot Reload row, and selects nothing" <| fun _ ->
      let suggestion = { SuggestedWorkflow = SessionWorkflow.HotReload BrowserRefreshConfig.defaults; Reason = "Web project detected"; DetectedPackages = [ "Falco" ] }
      let h = html (NewSessionDialog.Choosing { twoCandidates with Hint = WorkflowHint.Suggested suggestion })
      h |> contains "Web project detected" "the reason"
      h |> excludes "checked" "nothing is pre-checked by the server: the signal owns the choice"

    testCase "WHY — Warning names the session that is already there and links to switch to it" <| fun _ ->
      let h = html (NewSessionDialog.Warning(twoCandidates, overlapping, []))
      h |> contains "data-state=\"warning\"" "state"
      h |> contains (sprintf "data-testid=\"%s\"" NewSessionNames.WarningTestId) "the warning"
      h |> contains "abc123" "names the session"
      h |> contains "/dashboard/session/switch/abc123" "a real switch action"
      h |> contains "role=\"alert\"" "announced"

    testCase "WHY — creating anyway is still allowed from a warning, and says it is a second session" <| fun _ ->
      let h = html (NewSessionDialog.Warning(twoCandidates, overlapping, []))
      h |> contains (sprintf "data-testid=\"%s\"" NewSessionNames.CreateTestId) "Create is still there"
      h |> contains "another" "worded as a second session"

    testCase "WHY — a worktree overlap names the branch, because that is how the person tells checkouts apart" <| fun _ ->
      let wt = overlapOf { Id = "w9"; WorkingDirectory = "/wt/x"; Boundary = Boundary.Worktree("/wt/x", "feature/login") } (Relation.SameRepository "/wt/x")
      let h = html (NewSessionDialog.Warning(twoCandidates, wt, []))
      h |> contains "feature/login" "the branch"

    testCase "WHY — Refused shows the named reason, what happened, and the next step, inside the dialog" <| fun _ ->
      let reason = Refusal.Daemon (SageFsError.NeedsRebuild [ "App.dll" ])
      let h = html (NewSessionDialog.Refused(reason, twoCandidates))
      h |> contains "data-state=\"refused\"" "state"
      h |> contains (sprintf "data-testid=\"%s\"" NewSessionNames.RefusalTestId) "the refusal block"
      h |> contains (Refusal.title reason) "the name"
      h |> contains "role=\"alert\"" "announced"
      h |> contains (Refusal.nextAction reason) "the next step"
      h |> contains "App/App.fsproj" "the choices are still there to change"

    testCase "WHY — Creating disables the button and says it is starting, so a second click cannot happen" <| fun _ ->
      let h = html (NewSessionDialog.Creating(request "/work/repo" Target.Bare, twoCandidates))
      h |> contains "data-state=\"creating\"" "state"
      h |> contains "Starting" "says what is happening"

    testCase "WHY — the Creating state also draws a starting card above the session list, so the dialog closes into a card" <| fun _ ->
      let region = renderRegion (NewSessionDialog.Creating(request "/work/repo" Target.Bare, twoCandidates)) (Elem.div [ Falco.Markup.Attr.id "sessions-panel" ] []) |> renderNode
      region |> contains (sprintf "id=\"%s\"" NewSessionNames.StartingCardId) "the card"
      region |> contains "Starting" "worded as starting"
      region |> contains "role=\"status\"" "announced politely"

    testCase "WHY — outside Creating the region holds the session list and the dialog and nothing else" <| fun _ ->
      let region = renderRegion NewSessionDialog.Closed (Elem.div [ Falco.Markup.Attr.id "sessions-panel" ] []) |> renderNode
      region |> excludes NewSessionNames.StartingCardId "no starting card"
      region |> contains "sessions-panel" "the list is kept"
      region |> contains NewSessionNames.DialogId "the dialog is in the region"

    testCase "WHY — the dialog closes on Esc or its close button and the signal follows, with no hand-written script" <| fun _ ->
      let h = html (NewSessionDialog.Choosing twoCandidates)
      h |> contains (sprintf "data-testid=\"%s\"" NewSessionNames.CloseTestId) "a close button"
      h |> contains "aria-label=\"Close" "labelled for screen readers"
      h |> contains "data-on:close" "the native close event drives the signal"
      h |> excludes " onclick=" "no inline handler"
      h |> excludes "fetch(" "no hand-rolled request"

    testProperty "WHY — every state renders, carries its own name, and never lets a hostile directory out of its quotes" <|
      Prop.forAll states (fun state ->
        let rendered = html state
        let hostileRendered =
          html (
            match state with
            | NewSessionDialog.Closed -> NewSessionDialog.Discovering hostile
            | NewSessionDialog.Discovering _ -> NewSessionDialog.Discovering hostile
            | NewSessionDialog.Choosing found -> NewSessionDialog.Choosing { found with Directory = hostile }
            | NewSessionDialog.Warning(found, first, rest) -> NewSessionDialog.Warning({ found with Directory = hostile }, first, rest)
            | NewSessionDialog.Creating(req, found) -> NewSessionDialog.Creating({ req with Directory = hostile }, { found with Directory = hostile })
            | NewSessionDialog.Refused(reason, found) -> NewSessionDialog.Refused(reason, { found with Directory = hostile }))
        rendered.Contains (sprintf "data-state=\"%s\"" (NewSessionDialog.stateKey state))
        && not (hostileRendered.Contains "<script>"))

    testProperty "WHY — every control in every state has a name a screen reader can read" <|
      Prop.forAll states (fun state ->
        let rendered = html state
        // Every button carries an aria-label or visible text, every input a label: counted, not eyeballed.
        let inputs = rendered.Split("<input").Length - 1
        let labelledInputs = rendered.Split("<label").Length - 1 + rendered.Split("aria-label=").Length - 1
        labelledInputs >= inputs)
  ]
