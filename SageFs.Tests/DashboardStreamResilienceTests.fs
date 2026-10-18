/// What keeps an open dashboard tab alive across a daemon that is away for a while, and what makes it pick up a new
/// build. The page's Datastar stream stopped trying after ten attempts (about 181 s at the 30 s ceiling), and the
/// soft banner went on saying the page was reconnecting; and a daemon that was reinstalled left every open tab running
/// the old shell against the new daemon's morphs. See `DashboardConnection.fs`.
module SageFs.Tests.DashboardStreamResilienceTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server
open SageFs.Server.DashboardConnection

/// The shell as the browser receives it, with HTML attribute encoding undone so a JSON option can be read as written.
let private shellText (version: string) : string =
  Dashboard.renderShell version "client-1" "" "" (Elem.div [] [])
  |> renderNode
  |> System.Net.WebUtility.HtmlDecode

[<Tests>]
let tests =
  testList "Dashboard stream resilience" [

    testCase "WHY — the stream asks Datastar to retry without a ceiling, because ten tries (about 181 s) left a tab frozen under a banner that said it was reconnecting" <| fun _ ->
      shellText "1.2.3"
      |> Expect.stringContains
           "the stream's @get carries the retry ceiling as a typed option"
           (sprintf "\"retryMaxCount\":%d" streamRetryMaxCount)

    testCase "the retry ceiling is far past what ten tries cost, so no laptop sleep or daemon upgrade can reach it" <| fun _ ->
      let tenTriesAtTheThirtySecondCap = 10
      (streamRetryMaxCount > tenTriesAtTheThirtySecondCap * 1000)
      |> Expect.isTrue "the ceiling is effectively unbounded"

    testCase "the stream route is still the one the server serves for this client" <| fun _ ->
      shellText "1.2.3"
      |> Expect.stringContains "the @get targets this page's stream" "@get('/dashboard/stream/client-1'"

    testCase "WHY — the shell seeds the build it was served by and nothing patches that signal, because the heartbeat's build is compared against it" <| fun _ ->
      let text = shellText "1.2.3"
      text |> Expect.stringContains "the shell's build is a signal" "ds-shell-version"
      text |> Expect.stringContains "the daemon's build has a signal of its own" "ds-daemon-version"
      text |> Expect.stringContains "both start at the build the shell was rendered for" "'1.2.3'"

    testCase "WHY — a page reloads once when the daemon's build is not the shell's, because an open tab otherwise keeps an old shell after every install" <| fun _ ->
      let expr = versionChangeEffectExpr ()
      expr |> Expect.stringContains "it compares the heartbeat's build with the shell's" (sprintf "$%s !== $%s" DaemonVersionSignal ShellVersionSignal)
      expr |> Expect.stringContains "and reloads" "location.reload()"

    testCase "WHY — the reload is guarded by the build it already reloaded for, because a shell and a daemon that never agree must not reload forever" <| fun _ ->
      let expr = versionChangeEffectExpr ()
      expr |> Expect.stringContains "the guard reads what the tab already reloaded for" "sessionStorage"
      expr |> Expect.stringContains "and the key is the one named value" ReloadedForKey

    testCase "the effect rides the monitor element with the other connection signals" <| fun _ ->
      let rendered = Elem.div (monitorAttributes ()) [] |> renderNode |> System.Net.WebUtility.HtmlDecode
      rendered |> Expect.stringContains "the effect is installed" "location.reload()"
  ]
