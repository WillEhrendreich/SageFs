module NvimTests.TourTests

/// WHY: a tour is how a design reviewer gets the same editor states every time, so a tour file
/// must parse the same way every time and a typo must stop it before an editor is opened.
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open LemDrive

/// The tour module is called NvimTour so it can sit beside the VS Code driver's Tour in one project.
module Tour = LemDrive.NvimTour

let private parseOk (text: string) : Tour.TourStep list =
  match Tour.parse text with
  | Result.Ok steps -> steps
  | Result.Error errs -> failwithf "expected the tour to parse: %A" errs

let private shotNames = Gen.elements [ "first-run"; "after_eval"; "a"; "Panel2"; "x-y-z" ]
let private safeText = Gen.elements [ "hello"; "let x = 1"; "a \"quoted\" word"; "back\\slash"; "café ✓"; "#hash"; "two  spaces" ]

let private step : Gen<Tour.TourStep> =
  Gen.oneof
    [ Gen.map (fun t -> Tour.StepKeys t) (Gen.elements [ ":SageFsStatus<CR>"; "<Esc>"; "38G<M-CR>"; "iabc<Esc>" ])
      Gen.map Tour.StepType safeText
      Gen.map Tour.StepWait (Gen.choose (0, 30))
      Gen.map Tour.StepShot shotNames
      Gen.map2 (fun t w -> Tour.StepExpect(t, w)) safeText (Gen.choose (1, 120))
      Gen.map2 (fun c r -> Tour.StepResize(c, r)) (Gen.choose (20, 300)) (Gen.choose (8, 100))
      Gen.map Tour.StepShell (Gen.elements [ "curl http://localhost:5000/hello"; "ls -la"; "cat Program.fs" ])
      Gen.map Tour.StepActivate (Gen.elements [ "DemoEnv.Tests"; "FalcoHello" ]) ]

[<Tests>]
let tours =
  testList "tour files" [
    testCase "a tour with every kind of step parses in order" <| fun _ ->
      parseOk """
# first run
resize 80x24
keys :SageFsStatus<CR>
type hello world
wait 3
expect "Connected" within 20
shot status
shell curl http://localhost:5000/hello
"""
      |> Expect.equal "the steps"
        [ Tour.StepResize(80, 24)
          Tour.StepKeys ":SageFsStatus<CR>"
          Tour.StepType "hello world"
          Tour.StepWait 3
          Tour.StepExpect("Connected", 20)
          Tour.StepShot "status"
          Tour.StepShell "curl http://localhost:5000/hello" ]

    testCase "expect without within waits the default ten seconds" <| fun _ ->
      parseOk "expect \"ready\"" |> Expect.equal "default" [ Tour.StepExpect("ready", 10) ]

    testCase "an escaped quote and backslash inside expect text" <| fun _ ->
      parseOk "expect \"say \\\"hi\\\" \\\\ bye\"" |> Expect.equal "unescaped" [ Tour.StepExpect("say \"hi\" \\ bye", 10) ]

    testCase "blank lines, comments and Windows line endings are fine" <| fun _ ->
      parseOk "# a\r\n\r\nwait 1\r\n" |> Expect.equal "one step" [ Tour.StepWait 1 ]

    testCase "every bad line is reported with its number" <| fun _ ->
      match Tour.parse "wait 1\nwait 99\nfly away\nshot ../x\nresize 5x5\nexpect nothing" with
      | Result.Ok _ -> failtest "expected errors"
      | Result.Error errs ->
        errs |> List.map (fun e -> e.Line) |> Expect.equal "the failing lines" [ 2; 3; 4; 5; 6 ]
        (errs |> List.find (fun e -> e.Line = 3)).Reason |> Expect.stringContains "names the steps" "keys, type, wait, shot, expect, resize, shell, activate"

    testCase "activate names part of a project path and nothing else" <| fun _ ->
      parseOk "activate DemoEnv.Tests" |> Expect.equal "one step" [ Tour.StepActivate "DemoEnv.Tests" ]
      Tour.parseLine "activate" |> Expect.isError "it needs the text"

    testCase "an unclosed quote is an error, not a silent empty text" <| fun _ ->
      Tour.parseLine "expect \"never closed" |> Expect.isError "unclosed"

    testCase "every step word is one the parser reads back" <| fun _ ->
      for s in [ Tour.StepKeys "x"; Tour.StepType "x"; Tour.StepWait 1; Tour.StepShot "x"; Tour.StepExpect("x", 1); Tour.StepResize(80, 24); Tour.StepShell "ls"; Tour.StepActivate "DemoEnv.Tests" ] do
        Tour.parseLine (Tour.formatStep s) |> Expect.equal (sprintf "%s round-trips" (Tour.stepWord s)) (Result.Ok(Some s))

    testProperty "any generated step survives being written and read again"
      (Prop.forAll (Arb.fromGen step) (fun s -> Tour.parseLine (Tour.formatStep s) = Result.Ok(Some s)))

    testProperty "a whole generated tour survives being written and read again"
      (Prop.forAll (Arb.fromGen (Gen.nonEmptyListOf step)) (fun steps ->
        let text = steps |> List.map Tour.formatStep |> String.concat "\n"
        Tour.parse text = Result.Ok steps))
  ]
