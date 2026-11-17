/// Unit tests for the browser journey infrastructure: a screenshot is evidence, not an assertion, and a step that
/// spans a Datastar morph on a loaded machine is retried as a whole. Both are about load, not about the product the
/// journeys exercise — see the two test lists below.
module SageFs.Tests.BrowserScreenshotTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.DashboardBrowserTests

[<Tests>]
let tests =
  testList "browser screenshots are evidence, not assertions" [
    testTask "a browser that cannot take the picture does not fail the journey" {
      let capture (_: string) : Task<unit> = task { return raise (InvalidOperationException "Unable to capture screenshot") }
      do! PlaywrightFixture.saveScreenshot capture "never-written.png"
    }

    testTask "a browser that can is asked for exactly the path it was given" {
      let asked = ref ""
      let capture (path: string) : Task<unit> = task { asked.Value <- path }
      do! PlaywrightFixture.saveScreenshot capture "the-picture.png"
      asked.Value |> Expect.equal "the capture wrote where the journey said" "the-picture.png"
    }
  ]

[<Tests>]
let panelResetTests =
  testList "a panel-reopen failure is retried with the step it guards" [
    testTask "a click that a morph briefly intercepts is retried" {
      // The failing click lives in `reopen` (openEvalArea), not in `body`. The helper's whole purpose is to
      // survive a morph on a loaded machine, so a throw from reopen must be retried just like a throw from body.
      let reopens = ref 0
      let bodies = ref 0
      let reopen () : Task<unit> = task {
        reopens.Value <- reopens.Value + 1
        if reopens.Value = 1 then failwith "a dock element intercepted the click"
      }
      let body () : Task<unit> = task { bodies.Value <- bodies.Value + 1 }
      do! DashboardDom.throughPanelReset reopen 3 body
      reopens.Value |> Expect.equal "the intercepted reopen was tried again" 2
      bodies.Value |> Expect.equal "the step ran once the panel was open" 1
    }

    testTask "a failure that never heals still surfaces after the attempts are used" {
      // Retrying must not swallow a real product failure: after the attempts run out the exception escapes.
      let reopens = ref 0
      let reopen () : Task<unit> = task {
        reopens.Value <- reopens.Value + 1
        failwith "the control is genuinely covered"
      }
      let body () : Task<unit> = task { () }
      let mutable surfaced = false
      try
        do! DashboardDom.throughPanelReset reopen 2 body
      with _ ->
        surfaced <- true
      surfaced |> Expect.isTrue "a persistent failure is not swallowed"
      reopens.Value |> Expect.equal "one reopen per attempt, plus the first" 3
    }
  ]
