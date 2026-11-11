// WHY — roast-5, VS Code section, tree views. A TreeItem's label and description are joined for
// a screen reader with no separator ("Foo.fs● watching", "System.IO7 namespaces | 3 failed"), the
// Hot Reload, Session Context and API Browser rows set no accessible name at all (only the Sessions
// rows did), and the API Browser's failure leaves ("Not connected", "Error parsing response",
// "Could not list members") were dead ends with no click action. These pin the pure naming and the
// leaf actions, and that each provider that sets a description also sets an accessible name.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring StatusBarContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/TreeRowsPure.fs"

open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.Vscode.TreeRowsPure

let private src name = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "src", name))

let tests =
  testList "VS Code tree rows - accessible names and leaf actions" [

    testCase "WHY - label and description are separated, because they used to read as 'Foo.fs watching' run together" <| fun _ ->
      accessibleName "Foo.fs" "watching" |> Expect.equal "joined" "Foo.fs — watching"

    testCase "WHY - an empty description leaves the label alone" <| fun _ ->
      accessibleName "System.IO" "" |> Expect.equal "label only" "System.IO"

    testCase "WHY - a row whose description was never set still gets a name, because the property reads as null" <| fun _ ->
      accessibleName "Loading..." null |> Expect.equal "label only" "Loading..."

    testCase "WHY - pipes read as the word 'pipe', so a pipe-separated description is spoken as a list" <| fun _ ->
      accessibleName "Session Context" "4 assemblies | 12 namespaces | 380ms | 1 failed"
      |> Expect.equal "commas" "Session Context — 4 assemblies, 12 namespaces, 380ms, 1 failed"

    testCase "WHY - the watch state glyphs are not read aloud, because a screen reader says 'black circle'" <| fun _ ->
      accessibleName "Foo.fs" "● watching" |> Expect.equal "watching" "Foo.fs — watching"
      accessibleName "Foo.fs" "○ not watching" |> Expect.equal "not watching" "Foo.fs — not watching"

    testCase "WHY - every dead-end leaf of the API Browser has a way out, a tooltip and no codicon token" <| fun _ ->
      for leaf in apiBrowserLeafSamples do
        let view = leafView leaf
        Expect.isNotEmpty (sprintf "label of %A" leaf) view.Label
        Expect.isNotEmpty (sprintf "tooltip of %A" leaf) view.Tooltip
        Expect.isFalse (sprintf "no codicon token in %s" view.Tooltip) (view.Tooltip.Contains "$(")

    testCase "WHY - the failure leaves carry a command, and only the honest 'no members' leaf has none" <| fun _ ->
      (leafView ApiBrowserLeaf.NotConnected).Command |> Expect.equal "start" (Some "sagefs.start")
      (leafView ApiBrowserLeaf.ParseFailed).Command |> Expect.equal "output" (Some "sagefs.showOutput")
      (leafView (ApiBrowserLeaf.CouldNotList "session not active")).Command |> Expect.equal "output" (Some "sagefs.showOutput")
      (leafView ApiBrowserLeaf.NoMembers).Command |> Expect.equal "nothing to do" None

    testCase "WHY - the reason the daemon gave is the leaf's description, so the cause is on the row" <| fun _ ->
      (leafView (ApiBrowserLeaf.CouldNotList "session not active")).Description |> Expect.equal "reason" "session not active"

    testCase "WHY - every command a leaf names is contributed by package.json" <| fun _ ->
      let contributed =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "package.json")))
          .RootElement.GetProperty("contributes").GetProperty("commands").EnumerateArray()
        |> Seq.map (fun c -> c.GetProperty("command").GetString())
        |> Set.ofSeq
      for leaf in apiBrowserLeafSamples do
        (leafView leaf).Command |> Option.iter (fun c -> contributed |> Expect.contains (sprintf "%s is contributed" c) c)

    testCase "WHY - each provider that sets a description on a row also sets an accessible name" <| fun _ ->
      for file in [ "HotReloadTreeProvider.fs"; "SessionContextTreeProvider.fs"; "TypeExplorerProvider.fs"; "SessionsTreeProvider.fs" ] do
        let text = src file
        Expect.isTrue (sprintf "%s sets a description" file) (text.Contains ".description <-" || file = "SessionsTreeProvider.fs")
        Expect.isTrue (sprintf "%s sets an accessible name" file) (text.Contains "setAccessibleName" || text.Contains "accessibilityInformation")
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
