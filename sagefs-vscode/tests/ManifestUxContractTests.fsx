// WHY — three manifest defects that render wrong and raise no error, found by reading package.json
// (roast-5 findings 3, 4 and the settings note):
//   * every walkthrough step's `media.image` was a codicon token such as "$(terminal)". The field
//     is a file path relative to the extension root, so the first screen a new user sees showed a
//     broken image,
//   * `SageFs: Create Session` sat in the Sessions view's title bar with no `icon`, so VS Code
//     printed the whole title there while the other view/title commands showed an icon, and
//   * ten settings declared no `scope`, so the Settings UI could not say which are per machine
//     and which a workspace may override.
//
// These read package.json as data, the way CommandContractTests.fsx does. Runs under plain
// `dotnet fsi` (no Fable).
#r "nuget: Expecto, 11.0.0-alpha8"

open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip

let private root = Path.Combine(__SOURCE_DIRECTORY__, "..")
let private pkg = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json"))).RootElement
let private contributes = pkg.GetProperty "contributes"

let private tryProp (name: string) (el: JsonElement) =
  match el.TryGetProperty name with
  | true, v -> Some v
  | _ -> None

let private walkthroughSteps =
  contributes.GetProperty("walkthroughs").EnumerateArray()
  |> Seq.collect (fun w -> w.GetProperty("steps").EnumerateArray())
  |> Seq.toList

let private validScopes =
  set [ "application"; "machine"; "window"; "resource"; "language-overridable"; "machine-overridable" ]

let tests =
  testList "VS Code manifest - walkthrough media, view/title icons, setting scopes" [

    testCase "WHY - no walkthrough step uses a codicon token as its media path" <| fun _ ->
      for step in walkthroughSteps do
        let id = step.GetProperty("id").GetString()
        match tryProp "media" step with
        | None -> ()
        | Some media ->
          for field in [ "image"; "markdown"; "svg" ] do
            match tryProp field media with
            | Some v -> Expect.isFalse (sprintf "%s media.%s is a path, not a $(codicon)" id field) (v.GetString().StartsWith "$(")
            | None -> ()

    testCase "WHY - every walkthrough media path exists on disk and ships in the vsix" <| fun _ ->
      let ignoreText = File.ReadAllText(Path.Combine(root, ".vscodeignore"))
      for step in walkthroughSteps do
        match tryProp "media" step with
        | None -> ()
        | Some media ->
          for field in [ "image"; "markdown"; "svg" ] do
            match tryProp field media with
            | Some v ->
              let rel = v.GetString()
              Expect.isTrue (sprintf "%s exists" rel) (File.Exists(Path.Combine(root, rel)))
              let topDir = rel.Split('/').[0]
              Expect.isFalse (sprintf "%s is not excluded from the package" topDir) (ignoreText.Contains(topDir + "/**"))
            | None -> ()

    testCase "WHY - the walkthrough has a Create Session step, so first-run does not stop at an empty daemon" <| fun _ ->
      walkthroughSteps
      |> List.exists (fun s -> s.GetProperty("description").GetString().Contains "command:sagefs.createSession")
      |> Expect.isTrue "a step links Create Session"

    testCase "WHY - every command in a view/title menu has an icon, or the title text fills the bar" <| fun _ ->
      let iconOf =
        contributes.GetProperty("commands").EnumerateArray()
        |> Seq.map (fun c -> c.GetProperty("command").GetString(), (tryProp "icon" c).IsSome)
        |> Map.ofSeq
      for item in contributes.GetProperty("menus").GetProperty("view/title").EnumerateArray() do
        let cmd = item.GetProperty("command").GetString()
        Expect.isTrue (sprintf "%s has an icon" cmd) (Map.tryFind cmd iconOf = Some true)

    testCase "WHY - every sagefs setting declares a valid scope" <| fun _ ->
      for p in contributes.GetProperty("configuration").GetProperty("properties").EnumerateObject() do
        match tryProp "scope" p.Value with
        | None -> failtestf "%s declares no scope" p.Name
        | Some s -> Expect.isTrue (sprintf "%s scope %s is valid" p.Name (s.GetString())) (validScopes.Contains(s.GetString()))

    testCase "WHY - ports are machine scope, because they belong to the machine's daemon and no workspace should override them" <| fun _ ->
      let props = contributes.GetProperty("configuration").GetProperty("properties")
      for key in [ "sagefs.mcpPort"; "sagefs.dashboardPort" ] do
        props.GetProperty(key).GetProperty("scope").GetString() |> Expect.equal key "machine"

    testCase "WHY - the project path is resource scope, because it names a file inside one workspace" <| fun _ ->
      contributes.GetProperty("configuration").GetProperty("properties").GetProperty("sagefs.projectPath").GetProperty("scope").GetString()
      |> Expect.equal "projectPath" "resource"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
