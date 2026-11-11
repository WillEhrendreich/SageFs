// WHY — measured on a fresh install: the first Alt+Enter with the daemon up and no session
// bound ended in an inline red "No active session selected" and the Output panel opening.
// Nothing on screen said what to do next, and the one "Create Session" button lived in a
// sidebar view a new user has not opened (roast-5 finding 1).
//
// These pin the decision that replaces the dead end: given the daemon's state and what the
// window knows about sessions, the eval command either goes ahead or names the next step,
// and the notice for that step carries a button that runs an existing command.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring StatusBarContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/EvalPreflightPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.EvalPreflightPure

let tests =
  testList "VS Code eval preflight - next step instead of a dead end" [

    testCase "WHY - a bound session lets the eval go ahead" <| fun _ ->
      nextStep DaemonState.Up SessionState.Bound
      |> Expect.equal "proceed" EvalStep.Proceed

    testCase "WHY - a down daemon is the start flow's job, whatever the session state" <| fun _ ->
      [ SessionState.Bound; SessionState.NoneBound 0; SessionState.NoneBound 3 ]
      |> List.iter (fun s ->
        nextStep DaemonState.Down s
        |> Expect.equal "start daemon" EvalStep.StartDaemon)

    testCase "WHY - an empty daemon offers Create Session, which is the fresh-install case" <| fun _ ->
      nextStep DaemonState.Up (SessionState.NoneBound 0)
      |> Expect.equal "create" EvalStep.OfferCreateSession

    testCase "WHY - sessions that belong to other workspaces still leave Create Session on offer" <| fun _ ->
      nextStep DaemonState.Up (SessionState.NoneBound 2)
      |> Expect.equal "create or switch" EvalStep.OfferCreateOrSwitchSession

    testCase "WHY - every step that stops the eval has a notice whose button runs a registered command" <| fun _ ->
      [ EvalStep.OfferCreateSession; EvalStep.OfferCreateOrSwitchSession ]
      |> List.iter (fun step ->
        match notice step with
        | None -> failwithf "%A has no notice" step
        | Some n ->
          Expect.isNonEmpty "text says what is missing" n.Text
          Expect.isNonEmpty "has actions" n.Actions
          n.Actions
          |> List.iter (fun a ->
            Expect.isTrue (sprintf "%s names a sagefs command" a.Label) (a.Command.StartsWith "sagefs.")))

    testCase "WHY - the first action of every notice is Create Session, the path a new user needs" <| fun _ ->
      [ EvalStep.OfferCreateSession; EvalStep.OfferCreateOrSwitchSession ]
      |> List.iter (fun step ->
        (notice step).Value.Actions.Head
        |> Expect.equal "create first" { Label = "Create Session"; Command = "sagefs.createSession" })

    testCase "WHY - Proceed and StartDaemon carry no notice, because other code already speaks for them" <| fun _ ->
      notice EvalStep.Proceed |> Expect.isNone "proceed"
      notice EvalStep.StartDaemon |> Expect.isNone "start"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
