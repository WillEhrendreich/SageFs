module NvimTests.DriverTests

/// WHY: the lemming's only reach is this driver, so its closed command set, its key notation and
/// its shell allow-list are what keep "only keys" true. Each refusal is an example.
open Expecto
open Expecto.Flip
open LemDrive

let private keys (input: string) : Nvim.KeyToken list =
  match Nvim.parseKeys input with
  | Result.Ok tokens -> tokens
  | Result.Error e -> failwithf "expected %s to parse: %s" input e

let private workspace = "/tmp/lem/space-bunny-ui-eval-01/w"

[<Tests>]
let keyNotation =
  testList "key notation" [
    testCase "text and named keys are split in order" <| fun _ ->
      keys "ihello<Esc>:w<CR>"
      |> List.map (function
        | Nvim.Text t -> "text " + t
        | Nvim.Key c -> "key " + Nvim.tmuxChord c)
      |> Expect.equal "the tokens" [ "text ihello"; "key Escape"; "text :w"; "key Enter" ]

    testCase "modifiers map to tmux names" <| fun _ ->
      keys "<C-w><M-CR><S-Left><C-M-x>"
      |> List.map (function Nvim.Key c -> Nvim.tmuxChord c | Nvim.Text t -> t)
      |> Expect.equal "tmux key names" [ "C-w"; "M-Enter"; "S-Left"; "C-M-x" ]

    testCase "<lt> and <Bar> are literal characters" <| fun _ ->
      keys "a <lt> b <Bar>" |> Expect.equal "one text token" [ Nvim.Text "a < b |" ]

    testCase "a < that does not open a key name is typed as it is" <| fun _ ->
      keys "if a < b then" |> Expect.equal "one text token" [ Nvim.Text "if a < b then" ]

    testCase "an unknown key name is refused with a way out, never typed silently" <| fun _ ->
      match Nvim.parseKeys "x<int>y" with
      | Result.Error e -> e |> Expect.stringContains "it names the fix" "<lt>"
      | Result.Ok _ -> failtest "expected a refusal"

    testCase "an unknown modifier is refused" <| fun _ ->
      Nvim.parseKeys "<X-a>" |> Expect.isError "unknown modifier"

    testCase "function keys are F1 to F12 only" <| fun _ ->
      Nvim.parseKeys "<F12>" |> Expect.isOk "F12 is fine"
      Nvim.parseKeys "<F13>" |> Expect.isError "F13 is not a key"
  ]

[<Tests>]
let shellAllowList =
  let refused (line: string) =
    match Nvim.parseShellCommand workspace line with
    | Result.Error e -> e
    | Result.Ok c -> failwithf "expected '%s' to be refused but got %A" line c
  testList "the shell window" [
    testCase "a plain localhost curl is allowed and gets a time limit" <| fun _ ->
      match Nvim.parseShellCommand workspace "curl -s http://localhost:5199/hello" with
      | Result.Ok c -> Nvim.shellCommandLine c |> Expect.equal "the line it runs" "curl -s --max-time 15 http://localhost:5199/hello"
      | Result.Error e -> failtest e

    testCase "curl may not leave localhost" <| fun _ ->
      refused "curl http://example.com/" |> Expect.stringContains "says why" "localhost"

    testCase "curl may not write files or read config" <| fun _ ->
      refused "curl -o out.txt http://localhost:1/" |> Expect.stringContains "says which flag" "-o"
      refused "curl -K cfg http://localhost:1/" |> Expect.stringContains "says which flag" "-K"

    testCase "chaining, redirection and substitution are refused" <| fun _ ->
      for line in [ "ls; rm -rf x"; "ls && id"; "cat a | sh"; "ls > f"; "cat $(id)"; "ls `id`"; "curl 'http://localhost:1/'" ] do
        refused line |> Expect.stringContains (sprintf "'%s' names the character problem" line) "not allowed"

    testCase "only curl, cat and ls exist" <| fun _ ->
      refused "rm -rf /" |> Expect.stringContains "lists what is allowed" "curl, cat, ls"
      refused "bash" |> Expect.stringContains "lists what is allowed" "curl, cat, ls"

    testCase "cat and ls stay inside the project directory" <| fun _ ->
      Nvim.parseShellCommand workspace "cat DemoEnv/DemoEnv.fs" |> Expect.isOk "a relative path is fine"
      Nvim.parseShellCommand workspace ("cat " + workspace + "/Program.fs") |> Expect.isOk "an absolute path inside is fine"
      refused "cat /etc/passwd" |> Expect.stringContains "outside" "outside"
      refused "cat ../secret" |> Expect.stringContains "dotdot" ".."
      refused "cat -n file" |> Expect.stringContains "no flags" "no flags"

    testCase "ls takes the usual letters and nothing else" <| fun _ ->
      Nvim.parseShellCommand workspace "ls -la" |> Expect.isOk "-la"
      refused "ls -Z" |> Expect.stringContains "unknown letter" "-Z"
  ]

[<Tests>]
let commandSet =
  testList "the closed command set" [
    testCase "every command round-trips through its wire name" <| fun _ ->
      for cmd in [ Nvim.Keys ":w<CR>"; Nvim.NvimType "x"; Nvim.NvimScreen; Nvim.NvimWait 3; Nvim.NvimMessages; Nvim.NvimShell "ls"; Nvim.NvimShot "after-eval" ] do
        Nvim.parseCommand (Nvim.commandName cmd) [ Nvim.commandArgument cmd ]
        |> Expect.equal (sprintf "%s survives the wire" (Nvim.commandName cmd)) (Result.Ok cmd)

    testCase "arguments of keys are joined with nothing, words of type with a space" <| fun _ ->
      Nvim.parseCommand "keys" [ ":w"; "<CR>" ] |> Expect.equal "keys" (Result.Ok(Nvim.Keys ":w<CR>"))
      Nvim.parseCommand "nvim-type" [ "let"; "x" ] |> Expect.equal "type" (Result.Ok(Nvim.NvimType "let x"))

    testCase "nvim-wait is bounded to 30 seconds" <| fun _ ->
      Nvim.parseCommand "nvim-wait" [ "99" ] |> Expect.equal "clamped" (Result.Ok(Nvim.NvimWait 30))
      Nvim.parseCommand "nvim-wait" [ "x" ] |> Expect.isError "not a number"

    testCase "there is no command that runs Lua or reaches the plugin" <| fun _ ->
      for name in [ "lua"; "eval"; "exec"; "call"; "mcp"; "server"; "rpc" ] do
        Nvim.parseCommand name [ "anything" ] |> Expect.isError (sprintf "'%s' is not a command" name)

    testCase "a shot name is plain" <| fun _ ->
      Nvim.parseShotName "after-eval_2" |> Expect.isOk "dashes and underscores"
      Nvim.parseShotName "../x" |> Expect.isError "no path"
      Nvim.parseShotName "" |> Expect.isError "not empty"
      Nvim.parseShotName (System.String('a', 41)) |> Expect.isError "at most 40"
  ]

[<Tests>]
let timeline =
  testList "the timeline line" [
    testCase "one JSON line with epoch milliseconds, the editor, the command, an args array and an outcome" <| fun _ ->
      let entry = Nvim.timelineEntry "lemming" "keys" ":w<CR>" true 1790894121442L 1790894121873L
      use doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize entry)
      let r = doc.RootElement
      r.GetProperty("startMs").GetInt64() |> Expect.equal "start" 1790894121442L
      r.GetProperty("endMs").GetInt64() |> Expect.equal "end" 1790894121873L
      r.GetProperty("editor").GetString() |> Expect.equal "editor" "nvim"
      r.GetProperty("command").GetString() |> Expect.equal "command" "keys"
      r.GetProperty("args").EnumerateArray() |> Seq.map (fun a -> a.GetString()) |> List.ofSeq |> Expect.equal "args" [ ":w<CR>" ]
      r.GetProperty("outcome").GetString() |> Expect.equal "outcome" "ok"

    testCase "a command with no argument has an empty args array, and a failure says so" <| fun _ ->
      let entry = Nvim.timelineEntry "tour" "shot" "" false 1L 2L
      entry.args |> Expect.equal "no args" []
      entry.outcome |> Expect.equal "failed" "failed"
  ]

[<Tests>]
let screens =
  testList "what the screen header says" [
    testCase "modes are read off the bottom line and the prompts anywhere" <| fun _ ->
      Nvim.detectMode [ "x"; "status"; "-- INSERT --" ] 1 |> Expect.equal "insert" Nvim.InsertMode
      Nvim.detectMode [ "x"; "status"; ":SageFs" ] 3 |> Expect.equal "command line: the cursor is on the last row" Nvim.CommandLine
      Nvim.detectMode [ "x"; "status"; ":edit a.fs" ] 1 |> Expect.equal "a command that already ran is still printed, the cursor is elsewhere" Nvim.Normal
      Nvim.detectMode [ "Press ENTER or type command to continue" ] 1 |> Expect.equal "hit enter" Nvim.HitEnter
      Nvim.detectMode [ "1: A"; "2: B"; "Type number and <Enter> (q or empty cancels):" ] 3 |> Expect.equal "picker" Nvim.InputPrompt
      Nvim.detectMode [ "x"; "status"; "" ] 1 |> Expect.equal "normal" Nvim.Normal

    testCase "the status line is the second row from the bottom" <| fun _ ->
      Nvim.statusLine [ "text"; "  DemoEnv.fs  1:1 SageFs  "; "" ] |> Expect.equal "trimmed" "DemoEnv.fs  1:1 SageFs"

    testCase "the header carries mode, cursor and status" <| fun _ ->
      let snap : Nvim.ScreenSnapshot = { Rows = [ "a"; "bar"; "" ]; CursorRow = 3; CursorCol = 9; Mode = Nvim.Normal }
      Nvim.formatScreen snap
      |> Expect.stringStarts "first line" "[nvim] mode=NORMAL | cursor on screen row 3, column 9 | status line: bar"
  ]
