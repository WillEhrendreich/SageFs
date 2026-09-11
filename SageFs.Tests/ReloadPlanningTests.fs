module SageFs.Tests.ReloadPlanningTests

open Expecto
open Expecto.Flip
open SageFs.Features.ReloadPlanning

let private baselineSource = """// header comment
module Demo.Web.Program

open System

type TodoItem = { Id: int; Text: string }

let mutable todos: TodoItem list = []

let render (items: TodoItem list) =
  sprintf "%d remaining" items.Length

let getHome : string = "home"

[<EntryPoint>]
let main args =
  0
"""

let private declsOf (source: string) =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

let private replace (oldText: string) (newText: string) (source: string) =
  match source.Contains oldText with
  | true -> source.Replace(oldText, newText)
  | false -> failtestf "fixture edit target not found: %s" oldText

let private plan (edited: string) =
  planReload (declsOf baselineSource) (declsOf edited)

let private patchedNames (p: ReloadPlan) =
  match p with
  | ReloadPlan.PatchFunctions fs -> fs |> List.map _.Name
  | ReloadPlan.RestartRequired (first, rest) -> failtestf "expected a patch, got restart %A" (first :: rest)

let private restartChanges (p: ReloadPlan) =
  match p with
  | ReloadPlan.RestartRequired (first, rest) -> first :: rest
  | ReloadPlan.PatchFunctions fs -> failtestf "expected a restart, got patch %A" (fs |> List.map _.Name)

[<Tests>]
let extractDeclsTests =
  testList "ReloadPlanning extractDecls" [
    testCase "WHY — ReloadPlanning.extractDecls — reads the module path and each declaration's kind because the reload decision is made per declaration" <| fun _ ->
      let decls = declsOf baselineSource
      decls.ModulePath |> Expect.equal "file-level module path" [ "Demo"; "Web"; "Program" ]
      decls.Opens |> Expect.equal "opens, for re-emitting" [ "System" ]
      decls.Decls
      |> List.map (fun d -> d.Name, d.Kind)
      |> Expect.equal "kinds in source order"
        [ "TodoItem", DeclKind.TypeDecl
          "todos", DeclKind.ValueDecl
          "render", DeclKind.FunctionDecl
          "getHome", DeclKind.ValueDecl
          "main", DeclKind.EntryPointDecl ]
  ]

[<Tests>]
let planReloadTests =
  testList "ReloadPlanning planReload" [
    testCase "WHY — ReloadPlanning.planReload — an unchanged file patches nothing because a save with no code change must not disturb the app" <| fun _ ->
      plan baselineSource |> patchedNames |> Expect.isEmpty "nothing to patch"

    testCase "WHY — ReloadPlanning.planReload — a comment-only edit patches nothing because comments do not change behaviour" <| fun _ ->
      plan (replace "// header comment" "// a different comment" baselineSource)
      |> patchedNames |> Expect.isEmpty "nothing to patch"

    testCase "WHY — ReloadPlanning.planReload — a function body edit patches just that function because it can be swapped in place" <| fun _ ->
      plan (replace "\"%d remaining\"" "\"%d left to do\"" baselineSource)
      |> patchedNames |> Expect.equal "only render" [ "render" ]

    testCase "WHY — ReloadPlanning.planReload — a type edit requires a restart because the running app's values have the old shape" <| fun _ ->
      plan (replace "Text: string }" "Text: string; Done: bool }" baselineSource)
      |> restartChanges |> Expect.equal "TodoItem changed" [ ReloadChange.TypeChanged "TodoItem" ]

    testCase "WHY — ReloadPlanning.planReload — a module-level value edit requires a restart because the app captured the old value at startup" <| fun _ ->
      plan (replace "\"home\"" "\"welcome\"" baselineSource)
      |> restartChanges |> Expect.equal "getHome changed" [ ReloadChange.ValueChanged "getHome" ]

    testCase "WHY — ReloadPlanning.planReload — a function signature edit requires a restart because callers were compiled against the old one" <| fun _ ->
      plan (replace "let render (items: TodoItem list) =" "let render (title: string) (items: TodoItem list) =" baselineSource)
      |> restartChanges |> Expect.equal "render signature changed" [ ReloadChange.SignatureChanged "render" ]

    testCase "WHY — ReloadPlanning.planReload — an entry point edit requires a restart because it only runs at startup" <| fun _ ->
      plan (replace "  0\n" "  1\n" baselineSource)
      |> restartChanges |> Expect.equal "main changed" [ ReloadChange.EntryPointChanged ]

    testCase "WHY — ReloadPlanning.planReload — removing a function requires a restart because running code may still call it" <| fun _ ->
      plan (replace "let render (items: TodoItem list) =\n  sprintf \"%d remaining\" items.Length\n" "" baselineSource)
      |> restartChanges |> Expect.equal "render removed" [ ReloadChange.DeclarationRemoved "render" ]

    testCase "WHY — ReloadPlanning.planReload — a new function is patched in because nothing compiled references it yet" <| fun _ ->
      plan (baselineSource + "\nlet helper (x: int) = x + 1\n")
      |> patchedNames |> Expect.equal "helper added" [ "helper" ]

    testCase "WHY — ReloadPlanning.planReload — every restart reason is reported because the card must say what changed" <| fun _ ->
      baselineSource
      |> replace "Text: string }" "Text: string; Done: bool }"
      |> replace "\"home\"" "\"welcome\""
      |> plan
      |> restartChanges
      |> Expect.equal "both reasons, source order" [ ReloadChange.TypeChanged "TodoItem"; ReloadChange.ValueChanged "getHome" ]
  ]

[<Tests>]
let realSourceTests =
  testList "ReloadPlanning real sources" [
    testCase "WHY — ReloadPlanning — every Core source file with one top-level module plans against itself as a no-op because an unchanged save must never restart a running app" <| fun _ ->
      let coreDir =
        System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Core"))
      let results =
        System.IO.Directory.GetFiles(coreDir, "*.fs", System.IO.SearchOption.AllDirectories)
        |> Array.filter (fun f -> not (f.Contains "/obj/" || f.Contains "\\obj\\"))
        |> Array.map (fun f -> f, extractDecls (System.IO.File.ReadAllText f))
      let extracted =
        results |> Array.choose (fun (f, r) -> match r with Ok d -> Some (f, d) | Error _ -> None)
      extracted.Length > 50 |> Expect.isTrue (sprintf "most Core files have one top-level module (%d did)" extracted.Length)
      extracted
      |> Array.choose (fun (f, d) ->
        let file = System.IO.Path.GetFileName f
        match planReload d d with
        | ReloadPlan.PatchFunctions [] -> None
        | ReloadPlan.PatchFunctions patched -> Some (sprintf "%s patches %A" file (patched |> List.map _.Name))
        | ReloadPlan.RestartRequired (first, rest) -> Some (sprintf "%s restarts %A" file (first :: rest)))
      |> Array.toList
      |> Expect.equal "files that do not plan against themselves as a no-op" []
  ]

[<Tests>]
let companionModuleTests =
  let source = "module Demo.State\n\ntype Phase = Idle | Busy\n\nmodule Phase =\n  let label (p: Phase) = string p\n"
  testList "ReloadPlanning companion modules" [
    testCase "WHY — ReloadPlanning.planReload — a type and its same-named companion module plan against themselves as a no-op because sharing a name is not a change" <| fun _ ->
      planReload (declsOf source) (declsOf source) |> patchedNames |> Expect.isEmpty "nothing to patch"

    testCase "WHY — ReloadPlanning.planReload — editing only the companion module reports the module, not the type, because the card must name what really changed" <| fun _ ->
      let edited = replace "string p" "sprintf \"%A\" p" source
      planReload (declsOf source) (declsOf edited)
      |> restartChanges |> Expect.equal "only the module" [ ReloadChange.ModuleChanged "Phase" ]
  ]
