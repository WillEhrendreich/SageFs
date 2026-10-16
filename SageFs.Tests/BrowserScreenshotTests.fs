/// A screenshot in a browser journey is evidence for a person, not an assertion. Under load Chromium has answered
/// `Page.captureScreenshot: Unable to capture screenshot`, and that alone failed a journey whose every assertion held.
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
