/// What the head of each dashboard page tells a browser before it lays anything out. A page with no viewport meta is
/// laid out on a phone as a 980 px desktop page and shrunk, so the width media queries in the stylesheet never match; a
/// page with no charset can be read in the wrong encoding; a page with no language cannot be read aloud correctly.
module SageFs.Tests.DashboardHeadTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server

/// The one viewport the dashboard asks for: the device's width, at its natural scale.
let private viewportContent = "width=device-width, initial-scale=1"

let private shell : string =
  Dashboard.renderShell "0.0.0" "client-1" "" "" (Elem.div [] []) |> renderNode

let private settings : string =
  SettingsPanel.renderPage [] |> renderNode

let private headOf (page: string) : string =
  let start = page.IndexOf "<head"
  let finish = page.IndexOf "</head>"
  page.Substring(start, finish - start)

let private expectHead (name: string) (page: string) =
  page |> Expect.stringContains (sprintf "%s declares its language on <html>" name) "<html lang=\"en\""
  let head = headOf page
  head |> Expect.stringContains (sprintf "%s declares utf-8 in its head" name) "charset=\"utf-8\""
  head |> Expect.stringContains (sprintf "%s names a viewport in its head" name) "name=\"viewport\""
  head |> Expect.stringContains (sprintf "%s asks for the device width at natural scale" name) (sprintf "content=\"%s\"" viewportContent)
  (head.IndexOf "charset=" < head.IndexOf "<title")
  |> Expect.isTrue (sprintf "%s declares its charset before the title, within the first bytes a browser sniffs" name)

[<Tests>]
let tests =
  testList "Dashboard page heads" [
    testCase "WHY — the dashboard shell declares charset, viewport and language, because without a viewport a phone lays it out at about 980 px and the 768 px and 480 px layouts never apply" <| fun _ ->
      expectHead "the shell" shell

    testCase "WHY — the settings page declares them too, because it is a page of its own that a phone opens from the gear" <| fun _ ->
      expectHead "the settings page" settings
  ]
