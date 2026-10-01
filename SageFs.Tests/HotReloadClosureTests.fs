/// The planner's reading of an edit as "only lambda bodies changed", and the layout rule that
/// decides whether the closures the app already built can run the new code.
///
/// The real-app rows (HotReloadParityTests) are the outcome proof. These pin the pure decisions
/// underneath them, so a regression names the rule that broke and not only a served string.
module SageFs.Tests.HotReloadClosureTests

open Expecto
open Expecto.Flip
open SageFs.Features.ReloadPlanning
open SageFs.Middleware.HotReloadCore

let private declsOf (source: string) : FileDecls =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

let private declNamed (name: string) (file: FileDecls) : SourceDecl =
  match file.Decls |> List.tryFind (fun d -> d.Name = name) with
  | Some d -> d
  | None -> failtestf "no declaration named %s in %A" name (file.Decls |> List.map _.Name)

/// A module value holding two lambdas on separate lines, one of them several lines long.
let private routes (first: string) (second: string) (key: string) =
  String.concat "\n" [
    "module Demo.Routes"
    ""
    "let routes : (string * (unit -> string)) list ="
    sprintf "  [ \"%s\", (fun () -> %s)" key first
    "    \"b\", (fun () ->"
    "            let xs = [ 1; 2 ]"
    sprintf "            %s) ]" second
    "" ]

let private baseline = routes "\"A\"" "\"B\"" "a"

let private planOf (edited: string) = planReload (declsOf baseline) (declsOf edited)

[<Tests>]
let lambdaDiffTests =
  testList "ReloadPlanning lambdas" [
    testCase "WHY — lambdaDiff — an edit inside one lambda is reported with that lambda's lines counted from the declaration, because the host finds its closure by them" <| fun _ ->
      let was = declsOf baseline
      let now = declsOf (routes "\"A\"" "\"B2\"" "a")
      match lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now) with
      | LambdaDiff.LambdasOnly(edit, []) ->
        edit |> Expect.equal "the second lambda, lines 2 to 4 below the `let`" { Was = { FirstLine = 2; LastLine = 4 }; Now = { FirstLine = 2; LastLine = 4 } }
      | other -> failtestf "expected one lambda edit, got %A" other

    testCase "WHY — lambdaDiff — a lambda that moved down a line is the same lambda, because lines are counted inside the declaration" <| fun _ ->
      let was = declsOf baseline
      let moved = baseline.Replace("(fun () -> \"A\")", "(fun () ->\n                \"A2\")")
      let now = declsOf moved
      match lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now) with
      | LambdaDiff.LambdasOnly(edit, rest) ->
        rest |> Expect.isEmpty "only the first lambda changed"
        edit.Was |> Expect.equal "it was one line" { FirstLine = 1; LastLine = 1 }
        edit.Now |> Expect.equal "it is two now" { FirstLine = 1; LastLine = 2 }
      | other -> failtestf "expected one lambda edit, got %A" other

    testCase "WHY — lambdaDiff — a change outside every lambda is not a lambda edit, because the closures are not where the change is" <| fun _ ->
      let was = declsOf baseline
      let now = declsOf (routes "\"A\"" "\"B\"" "renamed")
      lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now)
      |> Expect.equal "the route key is data the list holds" LambdaDiff.NotLambdaOnly

    testCase "WHY — lambdaDiff — a lambda and something outside it changing together is not a lambda edit, because only the closures would be updated" <| fun _ ->
      let was = declsOf baseline
      let now = declsOf (routes "\"A2\"" "\"B\"" "renamed")
      lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now)
      |> Expect.equal "both moved" LambdaDiff.NotLambdaOnly

    testCase "WHY — lambdaDiff — gaining a lambda is not a lambda edit, because there is no closure of that shape in the running app" <| fun _ ->
      let was = declsOf baseline
      let now = declsOf (baseline.Replace("(fun () -> \"A\")", "(fun () -> \"A\")\n    \"c\", (fun () -> \"C\")"))
      lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now)
      |> Expect.equal "one more lambda" LambdaDiff.NotLambdaOnly

    testCase "WHY — lambdaDiff — two lambdas on one line cannot be told apart by line, so it is not read as a lambda edit" <| fun _ ->
      let source (a: string) = sprintf "module Demo.Pair\n\nlet pair : (unit -> string) * (unit -> string) =\n  (fun () -> \"%s\"), (fun () -> \"y\")\n" a
      let was = declsOf (source "x")
      let now = declsOf (source "z")
      lambdaDiff was now (declNamed "pair" was) (declNamed "pair" now)
      |> Expect.equal "closure names carry a line, and these share it" LambdaDiff.NotLambdaOnly

    testCase "WHY — lambdaDiff — a value bound directly to a lambda has no closure, because F# compiles it to a method" <| fun _ ->
      let source (a: string) = sprintf "module Demo.Direct\n\nlet direct : string -> string = fun who -> \"%s\" + who\n" a
      let was = declsOf (source "A")
      let now = declsOf (source "B")
      declNamed "direct" was |> fun d -> d.Kind |> Expect.equal "it is a function to the planner" DeclKind.FunctionDecl
      lambdaDiff was now (declNamed "direct" was) (declNamed "direct" now)
      |> Expect.equal "so there is nothing to find" LambdaDiff.NotLambdaOnly
  ]

[<Tests>]
let lambdaPlanTests =
  testList "ReloadPlanning plans a lambda edit" [
    testCase "WHY — planReload — a value edited only inside its lambdas is patched as a function, not redefined, because redefining it is a restart whenever the app kept the list" <| fun _ ->
      match planOf (routes "\"A\"" "\"B2\"" "a") with
      | ReloadPlan.PatchFunctions [ patch ] ->
        patch.Kind |> Expect.equal "emitted as a function so FSI compiles the lambdas and runs nothing" DeclKind.ValueClosures
        patch.Name |> Expect.equal "still the value's name, because the closures are named after it" "routes"
        patch.Text
        |> Expect.stringContains "the value became a function of unit, annotation kept" "let routes () : (string * (unit -> string)) list ="
      | other -> failtestf "expected a patch of the value's closures, got %A" other

    testCase "WHY — planReload — a value edited outside its lambdas is still a redefinition, because only the app knows whether it kept a copy" <| fun _ ->
      match planOf (routes "\"A\"" "\"B\"" "renamed") with
      | ReloadPlan.PatchKeepingState([], LiveState.Redefined d, []) -> d.Name |> Expect.equal "routes waits on the app's evidence" "routes"
      | other -> failtestf "expected a redefinition, got %A" other

    testCase "WHY — planReload — the lambda patch of a private value is still a patch, because it re-points closures and never touches a getter" <| fun _ ->
      let privateRoutes = (routes "\"A\"" "\"B\"" "a").Replace("let routes :", "let private routes :")
      let edited = (routes "\"A\"" "\"B2\"" "a").Replace("let routes :", "let private routes :")
      match planReload (declsOf privateRoutes) (declsOf edited) with
      | ReloadPlan.PatchFunctions [ patch ] -> patch.Kind |> Expect.equal "closures" DeclKind.ValueClosures
      | other -> failtestf "expected a patch of the value's closures, got %A" other

    testCase "WHY — lambdaEditsOf — an edited function's lambda is found too, because a closure it handed out at startup is still running the old body" <| fun _ ->
      let source (body: string) =
        String.concat "\n" [
          "module Demo.Held"
          ""
          "let makeHeld (suffix: string) : unit -> string ="
          "  let tag = suffix + \"?\""
          sprintf "  fun () -> \"%s\" + tag" body
          "" ]
      let was = declsOf (source "A")
      let now = declsOf (source "B")
      match planReload was now with
      | ReloadPlan.PatchFunctions [ patch ] ->
        match lambdaEditsOf was now patch with
        | Some found ->
          found.Was.Name |> Expect.equal "the baseline's own declaration travels with the edit" "makeHeld"
          found.First |> Expect.equal "the lambda on the last line" { Was = { FirstLine = 2; LastLine = 2 }; Now = { FirstLine = 2; LastLine = 2 } }
        | None -> failtest "expected the closure's lambda edit"
      | other -> failtestf "expected a function patch, got %A" other
  ]

type LayoutOld(prefix: string) =
  member _.Say(word: string) : string = prefix + word

type LayoutSame(prefix: string) =
  member _.Say(word: string) : string = prefix + word + "!"

type LayoutMore(prefix: string, tag: string) =
  member _.Say(word: string) : string = prefix + word + tag

type LayoutRetyped(prefix: int) =
  member _.Say(word: string) : string = string prefix + word

type LayoutOther() =
  inherit System.Collections.Generic.List<string>()

[<Tests>]
let layoutTests =
  testList "HotReloadCore layoutDifference" [
    testCase "WHY — layoutDifference — a type with the same fields can run code compiled for another, because that code reads fields by offset" <| fun _ ->
      layoutDifference typeof<LayoutOld> typeof<LayoutSame> |> Expect.isNone "same field, same type"

    testCase "WHY — layoutDifference — a field the new code added is named, because the old object has no room for it" <| fun _ ->
      layoutDifference typeof<LayoutOld> typeof<LayoutMore>
      |> Expect.equal "the new field" (Some "it now holds tag: String")

    testCase "WHY — layoutDifference — a field the new code dropped is named" <| fun _ ->
      layoutDifference typeof<LayoutMore> typeof<LayoutOld>
      |> Expect.equal "the lost field" (Some "it no longer holds tag: String")

    testCase "WHY — layoutDifference — a field of another type is a different layout even under the same name" <| fun _ ->
      layoutDifference typeof<LayoutOld> typeof<LayoutRetyped> |> Expect.isSome "string became int"

    testCase "WHY — layoutDifference — a different base type is a different layout" <| fun _ ->
      layoutDifference typeof<LayoutOld> typeof<LayoutOther> |> Expect.isSome "Object versus List"
  ]
