module SageFs.Tests.SessionStatusClassTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server.DashboardFragments

// The session status line in the header was drawn from the state's spelling ("Ready", "WarmingUp") and anything else fell
// into the failure colour, so a session that was evaluating, which is a loaded and working session, showed red. The class now
// comes from the display status every other surface uses.

let every : SessionState list =
  [ SessionState.Uninitialized; SessionState.WarmingUp; SessionState.Ready; SessionState.Evaluating; SessionState.Faulted ]

let expectedClass (state: SessionState) : string =
  match state with
  | SessionState.Ready
  | SessionState.Evaluating -> "status-ready"
  | SessionState.WarmingUp -> "status-warming"
  | SessionState.Uninitialized
  | SessionState.Faulted -> "status-faulted"

let html (state: SessionState) : string =
  renderSessionStatus state "abcd1234" "/work" "" "REPL" |> renderNode

[<Tests>]
let sessionStatusClassTests =
  testList "session status line" [
    testCase "WHY — an evaluating session is drawn as a working one, not as a failure" <| fun _ ->
      html SessionState.Evaluating |> Expect.stringContains "green, like the sidebar" "status status-ready"

    testCase "every state is drawn with its own label and the class its display status carries" <| fun _ ->
      for state in every do
        let rendered = html state
        rendered |> Expect.stringContains (sprintf "%A names itself" state) (SessionState.label state)
        rendered |> Expect.stringContains (sprintf "%A carries its class" state) (sprintf "status %s" (expectedClass state))
  ]
