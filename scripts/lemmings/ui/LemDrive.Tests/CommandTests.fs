module LemDrive.Tests.CommandTests

open Expecto
open Expecto.Flip
open LemDrive
open LemDrive.VscCommand

let private parse (words: string list) = VscCommand.parse words

[<Tests>]
let tests =
  testList "the vsc command set" [
    testCase "snapshot takes nothing" <| fun _ ->
      parse [ "snapshot" ] |> Expect.equal "snapshot" (Ok Snapshot)

    testCase "click joins its words" <| fun _ ->
      parse [ "click"; "Run"; "All" ] |> Expect.equal "joined" (Ok(Click "Run All"))

    testCase "click with nothing is refused" <| fun _ ->
      parse [ "click" ] |> Expect.isError "needs text"

    testCase "type keeps its spacing between words" <| fun _ ->
      parse [ "type"; "let"; "x"; "="; "1" ] |> Expect.equal "typed" (Ok(Type "let x = 1"))

    testCase "palette takes the rest as the command text" <| fun _ ->
      parse [ "palette"; "SageFs:"; "Create"; "Session" ] |> Expect.equal "palette" (Ok(Palette "SageFs: Create Session"))

    testCase "open refuses an absolute path" <| fun _ ->
      parse [ "open"; "/etc/passwd" ] |> Expect.isError "absolute"

    testCase "open refuses a parent segment" <| fun _ ->
      parse [ "open"; "../x" ] |> Expect.isError "parent"

    testCase "open takes a relative path" <| fun _ ->
      parse [ "open"; "DemoEnv/DemoEnv.fs" ] |> Expect.equal "relative" (Ok(Open "DemoEnv/DemoEnv.fs"))

    testCase "wait is bounded at 30 seconds" <| fun _ ->
      parse [ "wait"; "31" ] |> Expect.isError "too long"
      parse [ "wait"; "30" ] |> Expect.equal "the limit" (Ok(Wait 30))
      parse [ "wait"; "0" ] |> Expect.isError "too short"

    testCase "shot with no name is called shot" <| fun _ ->
      parse [ "shot" ] |> Expect.equal "default name" (Ok(Shot(DefaultShotName, [])))

    testCase "shot takes a name and repeated regions" <| fun _ ->
      parse [ "shot"; "ready"; "--region"; "sidebar"; "--region"; "editor" ]
      |> Expect.equal "name and two regions" (Ok(Shot("ready", [ SideBar; Editor ])))

    testCase "shot refuses an unknown region by name" <| fun _ ->
      match parse [ "shot"; "x"; "--region"; "toolbar" ] with
      | Result.Error e -> e |> Expect.stringContains "names the region" "toolbar"
      | Ok _ -> failtest "should not parse"

    testCase "resize takes two sizes in range" <| fun _ ->
      parse [ "resize"; "1280"; "800" ] |> Expect.equal "size" (Ok(Resize(1280, 800)))
      parse [ "resize"; "100"; "800" ] |> Expect.isError "too small"
      parse [ "resize"; "1280" ] |> Expect.isError "needs both"
      parse [ "resize"; "wide"; "800" ] |> Expect.isError "not a number"

    testCase "tour takes a file" <| fun _ ->
      parse [ "tour"; "tours/first-run.tour" ] |> Expect.equal "file" (Ok(Tour "tours/first-run.tour"))

    testCase "there is no verb that runs script" <| fun _ ->
      for v in [ "eval"; "js"; "exec"; "evaluate"; "run" ] do
        parse [ v; "1+1" ] |> Expect.isError (sprintf "'%s' is not a command" v)

    testCase "every verb has a usage line and parses its own name" <| fun _ ->
      for v in verbs do
        usageLine v |> Expect.notEqual (sprintf "%s has help" v) v

    testCase "the guard refuses the daemon's stop, restart, start and switch-project" <| fun _ ->
      for c in Guard.all do
        Guard.check (Guard.title c) |> Expect.isError (sprintf "%s refused by title" (Guard.title c))
        Guard.check (Guard.commandId c) |> Expect.isError (sprintf "%s refused by id" (Guard.commandId c))

    testCase "the guard leaves a session's own stop alone" <| fun _ ->
      Guard.check "SageFs: Stop Session" |> Expect.isOk "stopping a session is fine"
      Guard.check "sagefs.stopSession" |> Expect.isOk "the id too"
      Guard.check "  SageFs:   Restart   Daemon  recently used" |> Expect.isError "spacing does not hide it"
  ]
