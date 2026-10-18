/// A tab picks up a new daemon build by itself, in a real browser and with no daemon. The page is the real shell with the
/// real stylesheet and Datastar bundle, served from memory; the stream the daemon would push is answered with the one
/// event this case is about, a heartbeat that names the daemon's build. Nothing else moves, so the only thing a case can
/// observe is whether the page asked the server for a new shell.
///
/// An open tab used to keep the shell it was served after every install: new morphs from the new daemon arrived into an
/// old shell with an old signal set.
module SageFs.Tests.DashboardStreamResilienceBrowserTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open Microsoft.Playwright
open SageFs.Server
open SageFs.Server.DashboardConnection
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private origin = "http://sagefs.test"

/// The build the in-memory shell says it was served by.
let private shellBuild = "1.0.0"

/// Another build, as a daemon that was reinstalled would name itself.
let private otherBuild = "2.0.0"

let private shellHtml : string =
  Dashboard.renderShell shellBuild "client-1" "" "/work" (Elem.div [] []) |> renderNode

/// The event the daemon's heartbeat sends to say which build it is.
let private heartbeatNaming (build: string) : string =
  sprintf "event: datastar-patch-signals\ndata: signals {\"%s\":\"%s\"}\n\n" DaemonVersionSignal build

/// How many times the page asked for the shell, and how many times for the stream.
type private Requests = { Shells: int[]; Streams: int[] }

let private newRequests () : Requests = { Shells = [| 0 |]; Streams = [| 0 |] }

let private seen (counter: int[]) : int = Volatile.Read(&counter.[0])

/// Serve the dashboard from memory. Every stream request is answered with a heartbeat naming `build`.
let private serve (page: IPage) (build: string) (requests: Requests) = task {
  do! page.RouteAsync("**/*", Func<IRoute, Task>(fun route ->
    task {
      let fulfil (contentType: string) (body: string) =
        route.FulfillAsync(RouteFulfillOptions(Status = 200, ContentType = contentType, Body = body))
      match Uri(route.Request.Url).AbsolutePath with
      | "/dashboard" ->
        Interlocked.Increment(&requests.Shells.[0]) |> ignore
        do! fulfil "text/html; charset=utf-8" shellHtml
      | "/dashboard/dashboard.css" -> do! fulfil "text/css; charset=utf-8" Dashboard.dashboardCss
      | "/dashboard/datastar.js" -> do! fulfil "application/javascript; charset=utf-8" Dashboard.datastarBundle
      | path when path.StartsWith "/dashboard/stream/" ->
        Interlocked.Increment(&requests.Streams.[0]) |> ignore
        do! fulfil "text/event-stream" (heartbeatNaming build)
      | _ -> do! route.FulfillAsync(RouteFulfillOptions(Status = 404, Body = ""))
    } :> Task))
}

/// Wait, within a budget, until `counter` has reached `count`. Returns what it reached.
let private waitFor (counter: int[]) (count: int) (budgetMs: int) : Task<int> = task {
  let sw = Diagnostics.Stopwatch.StartNew()
  while seen counter < count && sw.ElapsedMilliseconds < int64 budgetMs do
    do! Task.Delay TestTimeouts.pollPage
  return seen counter
}

/// A page on the in-memory dashboard whose stream names `build`, run through `body`.
let private openDashboard (build: string) (body: IPage -> Requests -> Task<unit>) = task {
  let! page = PlaywrightFixture.newPage ()
  let requests = newRequests ()
  // Closed by awaiting, never by blocking a thread on it; the failure is carried out of the block and rethrown after.
  let! failure =
    task {
      try
        do! serve page build requests
        let! _ = page.GotoAsync(sprintf "%s/dashboard" origin)
        do! body page requests
        return None
      with ex -> return Some ex
    }
  do! PlaywrightFixture.closePage page
  match failure with
  | Some ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
  | None -> ()
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard stream resilience browser tests" [

    testTask "[Integration] Stream resilience: a heartbeat that names another build makes the page ask for a new shell, once" {
      do! openDashboard otherBuild (fun _ requests -> task {
        let! shells = waitFor requests.Shells 2 BrowserWaits.pageRenders
        Expect.equal "the page asked for the shell a second time after the heartbeat named another build" 2 shells
        // The reloaded shell hears the same heartbeat again. It must not reload again: a shell and a daemon that never
        // agree would otherwise reload for ever. Wait for the stream to have answered the reloaded page, then look.
        let! streams = waitFor requests.Streams 2 BrowserWaits.pageRenders
        Expect.isTrue "the reloaded page opened its own stream and heard the heartbeat again" (streams >= 2)
        do! Task.Delay TestTimeouts.warmupSettle
        Expect.equal "and the page did not reload a second time" 2 (seen requests.Shells)
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")

    testTask "[Integration] Stream resilience: a heartbeat that names the build the page was served by leaves the page alone" {
      do! openDashboard shellBuild (fun _ requests -> task {
        let! streams = waitFor requests.Streams 1 BrowserWaits.pageRenders
        Expect.isTrue "the stream answered the page, so the heartbeat has been sent" (streams >= 1)
        // Long enough that a reload, if one were going to happen, has happened (the case above sees one within it).
        do! Task.Delay TestTimeouts.warmupSettle
        Expect.equal "and the page asked for the shell only the once" 1 (seen requests.Shells)
      })
    }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
