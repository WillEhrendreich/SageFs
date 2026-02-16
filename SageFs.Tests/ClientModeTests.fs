module SageFs.Tests.ClientModeTests

open Expecto
open Expecto.Flip
open SageFs.Server.ClientMode

let semicolons = System.String(';', 2)

let parseTests = testList "ReplCommand.parse" [
  testCase "parses #quit" <| fun () ->
    ReplCommand.parse "#quit"
    |> Expect.equal "" Quit

  testCase "parses #exit" <| fun () ->
    ReplCommand.parse "#exit"
    |> Expect.equal "" Quit

  testCase "parses #q" <| fun () ->
    ReplCommand.parse "#q"
    |> Expect.equal "" Quit

  testCase "parses #help" <| fun () ->
    ReplCommand.parse "#help"
    |> Expect.equal "" Help

  testCase "parses #clear" <| fun () ->
    ReplCommand.parse "#clear"
    |> Expect.equal "" Clear

  testCase "parses #sessions" <| fun () ->
    ReplCommand.parse "#sessions"
    |> Expect.equal "" ListSessions

  testCase "parses #switch with id" <| fun () ->
    ReplCommand.parse "#switch abc123"
    |> Expect.equal "" (SwitchSession "abc123")

  testCase "parses #switch with empty id as Help" <| fun () ->
    ReplCommand.parse "#switch "
    |> Expect.equal "" Help

  testCase "parses #stop with id" <| fun () ->
    ReplCommand.parse "#stop session-42"
    |> Expect.equal "" (StopSession "session-42")

  testCase "parses #stop with empty id as Help" <| fun () ->
    ReplCommand.parse "#stop "
    |> Expect.equal "" Help

  testCase "parses #create with no dir" <| fun () ->
    ReplCommand.parse "#create"
    |> Expect.equal "" (CreateSession None)

  testCase "parses #create with dir" <| fun () ->
    ReplCommand.parse "#create C:/Code/MyProject"
    |> Expect.equal "" (CreateSession (Some "C:/Code/MyProject"))

  testCase "parses #diag" <| fun () ->
    ReplCommand.parse "#diag"
    |> Expect.equal "" ShowDiagnostics

  testCase "parses #diagnostics" <| fun () ->
    ReplCommand.parse "#diagnostics"
    |> Expect.equal "" ShowDiagnostics

  testCase "parses #reset" <| fun () ->
    ReplCommand.parse "#reset"
    |> Expect.equal "" Reset

  testCase "parses #hard-reset" <| fun () ->
    ReplCommand.parse "#hard-reset"
    |> Expect.equal "" HardReset

  testCase "parses #status" <| fun () ->
    ReplCommand.parse "#status"
    |> Expect.equal "" ShowStatus

  testCase "parses F# code as EvalCode" <| fun () ->
    let code = sprintf "let x = 42%s" semicolons
    ReplCommand.parse code
    |> Expect.equal "" (EvalCode code)

  testCase "parses plain text as EvalCode" <| fun () ->
    ReplCommand.parse "printfn hello"
    |> Expect.equal "" (EvalCode "printfn hello")
]

[<Tests>]
let tests = testList "ClientMode" [
  parseTests
]
