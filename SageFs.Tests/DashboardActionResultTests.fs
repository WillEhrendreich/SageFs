/// Session-card actions report into a region the viewer can see.
///
/// They used to write to `#eval-result`, which sits inside the Evaluate popover, closed until
/// the viewer presses λ or `e`. A Run App that failed to build, a Purge that errored, a project
/// switch that could not load: nothing on screen changed and nothing was said.
module SageFs.Tests.DashboardActionResultTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server.Dashboard

let private sid (n: int) : WorkerProtocol.SessionId =
  WorkerProtocol.SessionId.validate (sprintf "%08x" (0x0b000000 + n))
  |> Result.defaultValue (WorkerProtocol.SessionId.newId ())

let private raw = WorkerProtocol.SessionId.value

[<Tests>]
let tests =
  testList "action result region" [
    testCase "WHY — a failure is announced as an alert, so a screen reader and a sighted viewer both get it" <| fun _ ->
      let html = ActionResult.render (ActionOutcome.Failure "Run App: build failed") |> renderNode
      html |> Expect.stringContains "the text is there" "Run App: build failed"
      html |> Expect.stringContains "announced as an alert" "role=\"alert\""

    testCase "WHY — a success is a polite status, not an alert" <| fun _ ->
      let html = ActionResult.render (ActionOutcome.Success "Stopped 2 other sessions") |> renderNode
      html |> Expect.stringContains "announced politely" "role=\"status\""
      Expect.isFalse "a success never shouts" (html.Contains "role=\"alert\"")

    testCase "WHY — text from a project or a worker is escaped once, never markup" <| fun _ ->
      let html = ActionResult.render (ActionOutcome.Failure "<script>alert(1)</script>") |> renderNode
      Expect.isFalse "no live script tag" (html.Contains "<script>")
      html |> Expect.stringContains "the text survives escaped" "&lt;script&gt;"

    testCase "WHY — the region sits outside every <details>, so a closed popover cannot hide it" <| fun _ ->
      let html = ActionResult.render (ActionOutcome.Info "Loading X") |> renderNode
      Expect.isFalse "no <details> around it" (html.Contains "<details")
      ActionResult.Selector |> Expect.equal "it is appended to the body, which no #main morph rewrites" "body"
  ]

[<Tests>]
let stopOthersTests =
  testList "stop-others report" [
    testCase "WHY — every stop worked, so the count is the number that stopped" <| fun _ ->
      let results = [ sid 1, Ok "stopped"; sid 2, Ok "stopped" ]
      match StopOthersReport.summarize results with
      | ActionOutcome.Success text -> text |> Expect.stringContains "says how many" "2"
      | other -> failtestf "expected Success, got %A" other

    testCase "WHY — a stop that failed is named with its reason and is not counted as stopped" <| fun _ ->
      let results = [ sid 1, Ok "stopped"; sid 2, Error "worker would not exit"; sid 3, Ok "stopped" ]
      match StopOthersReport.summarize results with
      | ActionOutcome.Failure text ->
        text |> Expect.stringContains "2 of 3 stopped" "2 of 3"
        text |> Expect.stringContains "the failed session is named" (raw (sid 2))
        text |> Expect.stringContains "the reason is given" "worker would not exit"
        Expect.isFalse "a session that stopped is not listed as failed" (text.Contains (raw (sid 1)))
      | other -> failtestf "expected Failure, got %A" other

    testCase "WHY — nothing to stop is said plainly rather than as 'Stopped 0'" <| fun _ ->
      match StopOthersReport.summarize [] with
      | ActionOutcome.Info text -> text |> Expect.stringContains "says there were none" "No other session"
      | other -> failtestf "expected Info, got %A" other

    testCase "WHY — when every stop failed none is reported as stopped" <| fun _ ->
      match StopOthersReport.summarize [ sid 1, Error "a"; sid 2, Error "b" ] with
      | ActionOutcome.Failure text -> text |> Expect.stringContains "0 of 2" "0 of 2"
      | other -> failtestf "expected Failure, got %A" other
  ]
