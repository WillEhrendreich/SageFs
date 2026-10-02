module LemDrive.Tests.TourTests

open System
open System.IO
open Expecto
open Expecto.Flip
open LemDrive
open LemDrive.VscCommand
open LemDrive.Tour

let private steps (text: string) : Step list =
  match Tour.parse text with
  | Ok t -> t.Steps |> List.map (fun p -> p.Step)
  | Result.Error errs -> failtestf "should parse, but: %s" (String.Join("; ", errs))

let private errors (text: string) : string list =
  match Tour.parse text with
  | Ok _ -> failtest "should not parse"
  | Result.Error errs -> errs

/// The tours directory next to the runner, found from the test assembly's own location.
let private toursDir () : string =
  let rec up (dir: DirectoryInfo) =
    match dir with
    | null -> failtest "could not find scripts/lemmings/ui/vscode/tours above the test assembly"
    | d ->
      let candidate = Path.Combine(d.FullName, "vscode", "tours")
      match Directory.Exists candidate with
      | true -> candidate
      | false -> up d.Parent
  up (DirectoryInfo AppContext.BaseDirectory)

[<Tests>]
let tests =
  testList "the tour file format" [
    testCase "every verb parses into the step it names" <| fun _ ->
      let text =
        """
        # a comment
        command SageFs: Create Session
        key alt+enter
        click Sessions
        type let x = 1
        open DemoEnv/DemoEnv.fs
        wait 5
        shot first-run
        shot editor-only --region editor --region statusbar
        resize 1024 700
        expect-text SageFs: ready
        expect-text --within 60 11/11 passed
        expect-session --within 240
        expect-session
        replace DemoEnv/DemoEnv.fs "Some value" "Some (value + 1)"
        """
      steps text
      |> Expect.equal
        "the steps, in order"
        [ Run(PaletteExact "SageFs: Create Session")
          Run(Key [ { Modifiers = [ Chord.Alt ]; Key = Chord.Named Chord.Enter } ])
          Run(Click "Sessions")
          Run(Type "let x = 1")
          Run(Open "DemoEnv/DemoEnv.fs")
          Run(Wait 5)
          Run(Shot("first-run", []))
          Run(Shot("editor-only", [ Editor; StatusBar ]))
          Run(Resize(1024, 700))
          ExpectText("SageFs: ready", DefaultExpectSeconds)
          ExpectText("11/11 passed", 60)
          ExpectSession 240
          ExpectSession DefaultExpectSeconds
          Replace("DemoEnv/DemoEnv.fs", "Some value", "Some (value + 1)") ]

    testCase "comments and blank lines are ignored, and line numbers count them" <| fun _ ->
      match Tour.parse "# one\n\nwait 1\n# two\nwait 2" with
      | Ok t -> t.Steps |> List.map (fun p -> p.Line) |> Expect.equal "the lines of the two steps" [ 3; 5 ]
      | Result.Error e -> failtestf "%A" e

    testCase "an empty tour is refused" <| fun _ ->
      errors "# nothing here\n\n" |> Expect.equal "says so" [ "the tour has no steps" ]

    testCase "every bad line is reported with its number" <| fun _ ->
      let errs = errors "wait 1\nfly away\nwait 99\nresize 10 10\nshot ok"
      errs |> List.length |> Expect.equal "three bad lines" 3
      errs[0] |> Expect.stringStarts "first bad line is line 2" "line 2:"
      errs[1] |> Expect.stringStarts "then line 3" "line 3:"
      errs[2] |> Expect.stringStarts "then line 4" "line 4:"

    testCase "an unknown step names the known ones" <| fun _ ->
      (errors "juggle 3")[0] |> Expect.stringContains "lists the verbs" "expect-text"

    testCase "replace needs both texts in quotes" <| fun _ ->
      errors "replace a.fs find replace" |> List.length |> Expect.equal "unquoted refused" 1
      errors "replace a.fs \"only one\"" |> List.length |> Expect.equal "one text refused" 1
      errors "replace a.fs \"\" \"x\"" |> List.length |> Expect.equal "empty find refused" 1

    testCase "set-workflow takes only the three workflows the daemon knows" <| fun _ ->
      steps "set-workflow HotReload" |> Expect.equal "hot reload" [ SetWorkflow "HotReload" ]
      errors "set-workflow Turbo" |> List.length |> Expect.equal "unknown refused" 1
      errors "set-workflow" |> List.length |> Expect.equal "none refused" 1

    testCase "{session} is filled in where a step's text carries it" <| fun _ ->
      let step = Run(Type "{session}")
      usesSession step |> Expect.isTrue "it uses the session"
      withSession "b380bce0" step |> Expect.equal "filled" (Run(Type "b380bce0"))
      usesSession (Run(Wait 1)) |> Expect.isFalse "a wait does not"
      withSession "x" (ExpectText("row {session}", 5)) |> Expect.equal "expect-text too" (ExpectText("row x", 5))

    testCase "replace understands escapes in its quoted texts" <| fun _ ->
      steps "replace a.fs \"say \\\"hi\\\"\" \"line1\\nline2\""
      |> Expect.equal "unescaped" [ Replace("a.fs", "say \"hi\"", "line1\nline2") ]

    testCase "replace stays inside the workspace" <| fun _ ->
      errors "replace ../x.fs \"a\" \"b\"" |> List.length |> Expect.equal "parent refused" 1
      errors "replace /etc/x \"a\" \"b\"" |> List.length |> Expect.equal "absolute refused" 1

    testCase "expect-text bounds its wait" <| fun _ ->
      errors "expect-text --within 301 x" |> List.length |> Expect.equal "too long" 1
      errors "expect-text" |> List.length |> Expect.equal "no text" 1
      errors "expect-session --within 0" |> List.length |> Expect.equal "session wait bounded too" 1
      errors "expect-session soon" |> List.length |> Expect.equal "session wait takes only --within" 1

    testCase "a step that is the command line's own refusal is a line error" <| fun _ ->
      errors "open ../secret" |> List.length |> Expect.equal "open refuses .." 1
      errors "resize 1280" |> List.length |> Expect.equal "resize needs two" 1

    testCase "every step describes itself" <| fun _ ->
      for s in steps "command X\nkey escape\nwait 1\nshot a --region panel\nresize 800 600\nexpect-text y\nreplace f \"a\" \"b\"" do
        Tour.describe s |> Expect.isNotEmpty "a description"

    testCase "every example tour that ships parses, and every shot name in it is unique" <| fun _ ->
      let files = Directory.GetFiles(toursDir (), "*.tour")
      Expect.isGreaterThan "there are example tours" (Array.length files, 6)
      for file in files do
        match Tour.parse (File.ReadAllText file) with
        | Result.Error errs -> failtestf "%s does not parse: %s" (Path.GetFileName file) (String.Join("; ", errs))
        | Ok t ->
          let names = t.Steps |> List.choose (fun p -> match p.Step with | Run(Shot(n, _)) -> Some n | _ -> None)
          Expect.isGreaterThan (sprintf "%s takes shots" (Path.GetFileName file)) (List.length names, 1)
          names |> List.distinct |> List.length |> Expect.equal (sprintf "%s: shot names are unique" (Path.GetFileName file)) (List.length names)
  ]
