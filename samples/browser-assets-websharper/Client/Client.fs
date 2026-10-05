module BrowserAssets.Client

open WebSharper
open WebSharper.JavaScript

[<JavaScript; SPAEntryPoint>]
let main () =
  JS.Document.GetElementById("message").TextContent <- "WebSharper client: version one"
