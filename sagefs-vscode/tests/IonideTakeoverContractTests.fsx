// WHY — Alt+Enter support re-registered Ionide's fsi.SendSelection / SendLine / SendFile command
// ids on every activation. VS Code throws when an id is already taken, and the extension
// swallowed that at debug level, so whichever extension activated second lost its registration
// and nobody was told. There was also no setting to stop SageFs claiming the ids (roast-5
// finding 6).
//
// These pin the setting the takeover now hangs on (declared in package.json, on by default,
// window scope) and the one-line notice written when the registration is refused.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring CommandContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/IonideTakeoverPure.fs"

open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.Vscode.IonideTakeoverPure

let private pkg =
  JsonDocument.Parse(File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "package.json"))).RootElement

let private setting =
  pkg.GetProperty("contributes").GetProperty("configuration").GetProperty("properties")
    .GetProperty(sprintf "sagefs.%s" settingKey)

let tests =
  testList "VS Code Ionide takeover - a setting and a visible refusal" [

    testCase "WHY - the setting is declared in package.json, so it shows in the Settings UI" <| fun _ ->
      setting.GetProperty("type").GetString() |> Expect.equal "boolean" "boolean"

    testCase "WHY - the setting defaults to on, because Alt+Enter routing is what users have today" <| fun _ ->
      setting.GetProperty("default").GetBoolean() |> Expect.isTrue "default true"

    testCase "WHY - the setting is window scope, because the registration happens per window at activation" <| fun _ ->
      setting.GetProperty("scope").GetString() |> Expect.equal "window scope" "window"

    testCase "WHY - the plan follows the setting" <| fun _ ->
      plan true |> Expect.equal "on" Plan.TakeOver
      plan false |> Expect.equal "off" Plan.LeaveToIonide

    testCase "WHY - all three Ionide send commands are covered" <| fun _ ->
      commandIds |> Expect.equal "ids" [ "fsi.SendSelection"; "fsi.SendLine"; "fsi.SendFile" ]

    testCase "WHY - the refusal notice is one line, names the command, and names the setting" <| fun _ ->
      let n = refusedNotice "fsi.SendLine"
      Expect.isFalse "one line" (n.Contains "\n")
      n |> Expect.stringContains "command" "fsi.SendLine"
      n |> Expect.stringContains "setting" (sprintf "sagefs.%s" settingKey)
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
