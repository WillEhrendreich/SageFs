module LemDrive.Tests.HarnessTests

open Expecto
open Expecto.Flip
open LemDrive

[<Tests>]
let tests =
  testList "the harness's own helpers" [
    testCase "the sagefs shim runs read-only verbs and refuses the rest" <| fun _ ->
      for args in [ [ "--version" ]; [ "status" ]; [ "check" ]; [ "--help" ] ] do
        Shim.allowed (Shim.verbOf args) |> Expect.isOk (sprintf "%A runs" args)
      for args in [ [ "stop" ]; [ "sweep"; "--kill" ]; [ "mcp" ]; [ "--prune" ]; []; [ "--mcp-port"; "5000" ] ] do
        Shim.allowed (Shim.verbOf args) |> Expect.isError (sprintf "%A refused" args)

    testCase "oracle: a find-help answer must name real commands" <| fun _ ->
      let package = """{"contributes":{"commands":[{"title":"SageFs: Create Session"},{"title":"SageFs: Enable Live Testing"},{"title":"Switch To"}]}}"""
      let titles = Oracle.commandTitles package
      titles |> Expect.equal "only SageFs titles" [ "SageFs: Create Session"; "SageFs: Enable Live Testing" ]
      Oracle.titlesNamed titles "I found create session and also Enable Live Testing."
      |> List.length |> Expect.equal "both named" 2
      Oracle.titlesNamed titles "It can do magic." |> Expect.isEmpty "none named"

    testCase "oracle: every task has at least one check" <| fun _ ->
      for t in Oracle.LemTask.all do
        Oracle.checksFor t |> Expect.isNonEmpty (sprintf "%s has checks" (Oracle.LemTask.name t))

    testCase "oracle: task names round-trip" <| fun _ ->
      for t in Oracle.LemTask.all do
        Oracle.LemTask.tryParse (Oracle.LemTask.name t) |> Expect.equal "round trip" (Ok t)
      Oracle.LemTask.tryParse "ui-nothing" |> Expect.isError "unknown"

    testCase "fellover: a failed call, a refused call and a silent run" <| fun _ ->
      let call n cmd r : FellOver.Call = { Number = n; Command = cmd; Result = r; Body = "x" }
      FellOver.entries [] true 0
      |> List.map (fun e -> e.Stage)
      |> Expect.equal "never drove the editor, and no session" [ "Adoption"; "SessionCreate" ]
      FellOver.entries [ call "001" "vsc click Nope" FellOver.CallFailed; call "002" "vsc palette SageFs: Stop Daemon" FellOver.CallRefused; call "003" "vsc snapshot" FellOver.CallOk ] false 0
      |> List.map (fun e -> e.Stage)
      |> Expect.equal "failed is Editor, refused is ToolSurface" [ "Editor"; "ToolSurface" ]

    testCase "fellover: a transcript is read back" <| fun _ ->
      let text = "# vsc click Nope\n# 2026-10-01T00:00:00.0000000Z  failed\nFAILED: nothing visible matches\n"
      match FellOver.parseTranscript "004" text with
      | Some c ->
        c.Result |> Expect.equal "failed" FellOver.CallFailed
        c.Command |> Expect.equal "command" "vsc click Nope"
      | None -> failtest "should parse"
  ]
