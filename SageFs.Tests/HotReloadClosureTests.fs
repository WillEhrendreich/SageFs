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
    testCase "WHY - lambdaDiff - an edit inside one lambda is reported with that lambda's lines counted from the declaration, because the host finds its closure by them" <| fun _ ->
      let was = declsOf baseline
      let now = declsOf (routes "\"A\"" "\"B2\"" "a")
      match lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now) with
      | LambdaDiff.LambdasOnly(edit, []) ->
        edit |> Expect.equal "the second lambda, lines 2 to 4 below the `let`" { Was = { FirstLine = 2; LastLine = 4 }; Now = { FirstLine = 2; LastLine = 4 } }
      | other -> failtestf "expected one lambda edit, got %A" other

    testCase "WHY - lambdaDiff - a lambda that moved down a line is the same lambda, because lines are counted inside the declaration" <| fun _ ->
      let was = declsOf baseline
      let moved = baseline.Replace("(fun () -> \"A\")", "(fun () ->\n                \"A2\")")
      let now = declsOf moved
      match lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now) with
      | LambdaDiff.LambdasOnly(edit, rest) ->
        rest |> Expect.isEmpty "only the first lambda changed"
        edit.Was |> Expect.equal "it was one line" { FirstLine = 1; LastLine = 1 }
        edit.Now |> Expect.equal "it is two now" { FirstLine = 1; LastLine = 2 }
      | other -> failtestf "expected one lambda edit, got %A" other

    testCase "WHY - lambdaDiff - a change outside every lambda is not a lambda edit, because the closures are not where the change is" <| fun _ ->
      let was = declsOf baseline
      let now = declsOf (routes "\"A\"" "\"B\"" "renamed")
      lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now)
      |> Expect.equal "the route key is data the list holds" LambdaDiff.NotLambdaOnly

    testCase "WHY - lambdaDiff - a lambda and something outside it changing together is not a lambda edit, because only the closures would be updated" <| fun _ ->
      let was = declsOf baseline
      let now = declsOf (routes "\"A2\"" "\"B\"" "renamed")
      lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now)
      |> Expect.equal "both moved" LambdaDiff.NotLambdaOnly

    testCase "WHY - lambdaDiff - gaining a lambda is not a lambda edit, because there is no closure of that shape in the running app" <| fun _ ->
      let was = declsOf baseline
      let now = declsOf (baseline.Replace("(fun () -> \"A\")", "(fun () -> \"A\")\n    \"c\", (fun () -> \"C\")"))
      lambdaDiff was now (declNamed "routes" was) (declNamed "routes" now)
      |> Expect.equal "one more lambda" LambdaDiff.NotLambdaOnly

    testCase "WHY - lambdaDiff - two lambdas on one line cannot be told apart by line, so it is not read as a lambda edit" <| fun _ ->
      let source (a: string) = sprintf "module Demo.Pair\n\nlet pair : (unit -> string) * (unit -> string) =\n  (fun () -> \"%s\"), (fun () -> \"y\")\n" a
      let was = declsOf (source "x")
      let now = declsOf (source "z")
      lambdaDiff was now (declNamed "pair" was) (declNamed "pair" now)
      |> Expect.equal "closure names carry a line, and these share it" LambdaDiff.NotLambdaOnly

    testCase "WHY - lambdaDiff - a value bound directly to a lambda has no closure, because F# compiles it to a method" <| fun _ ->
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
    testCase "WHY - planReload - a value edited only inside its lambdas is patched as a function, not redefined, because redefining it is a restart whenever the app kept the list" <| fun _ ->
      match planOf (routes "\"A\"" "\"B2\"" "a") with
      | ReloadPlan.PatchFunctions [ patch ] ->
        patch.Kind |> Expect.equal "emitted as a function so FSI compiles the lambdas and runs nothing" DeclKind.ValueClosures
        patch.Name |> Expect.equal "still the value's name, because the closures are named after it" "routes"
        patch.Text
        |> Expect.stringContains "the value became a function of unit, annotation kept" "let routes () : (string * (unit -> string)) list ="
      | other -> failtestf "expected a patch of the value's closures, got %A" other

    testCase "WHY - planReload - a value edited outside its lambdas is still a redefinition, because only the app knows whether it kept a copy" <| fun _ ->
      match planOf (routes "\"A\"" "\"B\"" "renamed") with
      | ReloadPlan.PatchKeepingState([], LiveState.Redefined d, []) -> d.Name |> Expect.equal "routes waits on the app's evidence" "routes"
      | other -> failtestf "expected a redefinition, got %A" other

    testCase "WHY - planReload - the lambda patch of a private value is still a patch, because it re-points closures and never touches a getter" <| fun _ ->
      let privateRoutes = (routes "\"A\"" "\"B\"" "a").Replace("let routes :", "let private routes :")
      let edited = (routes "\"A\"" "\"B2\"" "a").Replace("let routes :", "let private routes :")
      match planReload (declsOf privateRoutes) (declsOf edited) with
      | ReloadPlan.PatchFunctions [ patch ] -> patch.Kind |> Expect.equal "closures" DeclKind.ValueClosures
      | other -> failtestf "expected a patch of the value's closures, got %A" other

    testCase "WHY - lambdaEditsOf - an edited function's lambda is found too, because a closure it handed out at startup is still running the old body" <| fun _ ->
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
  testList "HotReloadCore layoutFit" [
    testCase "WHY - layoutFit - a type with the same fields can run code compiled for another, because that code reads fields by offset" <| fun _ ->
      layoutFit typeof<LayoutOld> typeof<LayoutSame> |> Expect.equal "same field, same type" LayoutFit.SameLayout

    testCase "WHY - layoutFit - a field the new code added is named, because the old object has no room for it" <| fun _ ->
      layoutFit typeof<LayoutOld> typeof<LayoutMore>
      |> Expect.equal "the new field" (LayoutFit.Differs "it now holds tag: String")

    testCase "WHY - layoutFit - a field the new code dropped is named" <| fun _ ->
      layoutFit typeof<LayoutMore> typeof<LayoutOld>
      |> Expect.equal "the lost field" (LayoutFit.Differs "it no longer holds tag: String")

    testCase "WHY - layoutFit - a field of another type is a different layout even under the same name" <| fun _ ->
      layoutFit typeof<LayoutOld> typeof<LayoutRetyped> |> Expect.notEqual "string became int" LayoutFit.SameLayout

    testCase "WHY - layoutFit - a different base type is a different layout" <| fun _ ->
      layoutFit typeof<LayoutOld> typeof<LayoutOther> |> Expect.notEqual "Object versus List" LayoutFit.SameLayout
  ]

type LayoutRecord = { Value: int }

type LayoutProperty() =
  member _.Name = "x"

[<Tests>]
let instanceRegistryTests =
  let registered =
    lazy (getAllMethods (System.Reflection.Assembly.GetExecutingAssembly()) |> List.filter (fun m -> m.FullName.Contains "HotReloadClosureTests.Layout"))
  let named (suffix: string) = registered.Value |> List.filter (fun m -> m.FullName.EndsWith(suffix, System.StringComparison.Ordinal))
  testList "HotReloadCore instance members" [
    testCase "WHY - getAllMethods - a class's own instance member is registered, because an edit to its body is a patch like a function's" <| fun _ ->
      match named "LayoutOld.Say" with
      | [ m ] -> m.MethodInfo.IsStatic |> Expect.isFalse "an instance member"
      | other -> failtestf "expected one LayoutOld.Say, got %d" other.Length

    testCase "WHY - getAllMethods - a member a class only inherits is not registered, because every type has a ToString and none of them is the user's code" <| fun _ ->
      named "LayoutOld.ToString" |> Expect.isEmpty "not declared by LayoutOld"
      named "LayoutOld.GetHashCode" |> Expect.isEmpty "not declared by LayoutOld"

    testCase "WHY - getAllMethods - the members the compiler writes for a record are not registered, because nobody edits them" <| fun _ ->
      named "LayoutRecord.Equals" |> Expect.isEmpty "generated"
      named "LayoutRecord.GetHashCode" |> Expect.isEmpty "generated"

    testCase "WHY - accessorRole - an instance property's getter is a plain member, because only a module's accessors are one mutable binding's pair" <| fun _ ->
      match named "LayoutProperty.get_Name" with
      | [ m ] -> accessorRole m |> Expect.equal "not a binding's getter" AccessorRole.Plain
      | other -> failtestf "expected one LayoutProperty.get_Name, got %d" other.Length

    testCase "WHY - holdKey - a module function is filed under its name and an instance member under its type and name, so two classes' same-named members are two entries" <| fun _ ->
      let say = named "LayoutOld.Say" |> List.head
      let sameSay = named "LayoutSame.Say" |> List.head
      holdKey say.MethodInfo |> Expect.equal "type and member" "LayoutOld.Say"
      holdKey sameSay.MethodInfo |> Expect.notEqual "another class's Say is another entry" (holdKey say.MethodInfo)
      holdKey (typeof<System.String>.GetMethod("Concat", [| typeof<string>; typeof<string> |])) |> Expect.equal "a static method keeps its bare name" "Concat"
  ]

let layoutGenericFunction<'T> (x: 'T) : 'T = x

type LayoutGeneric() =
  member _.Echo<'T>(x: 'T) : 'T = x

[<Tests>]
let genericRegistryTests =
  let registered =
    lazy (getAllMethods (System.Reflection.Assembly.GetExecutingAssembly()) |> List.filter (fun m -> m.FullName.Contains "HotReloadClosureTests."))
  testList "HotReloadCore generic functions" [
    testCase "WHY - getAllMethods - a generic function is registered, because a save that edits one has to be able to name it, and it cannot be named if it was never seen" <| fun _ ->
      registered.Value
      |> List.filter (fun m -> m.FullName.EndsWith("layoutGenericFunction", System.StringComparison.Ordinal))
      |> List.map (fun m -> m.MethodInfo.IsGenericMethod)
      |> Expect.equal "one, and it is generic" [ true ]

    testCase "WHY - getAllMethods - a generic instance member is registered for the same reason" <| fun _ ->
      registered.Value
      |> List.filter (fun m -> m.FullName.EndsWith("LayoutGeneric.Echo", System.StringComparison.Ordinal))
      |> List.map (fun m -> m.MethodInfo.IsGenericMethod)
      |> Expect.equal "one, and it is generic" [ true ]
  ]

[<Tests>]
let typeTextTests =
  let typesIn (source: string) = (declsOf source).Decls |> List.filter (fun d -> d.Kind = DeclKind.TypeDecl)
  testList "ReloadPlanning type declarations are read from their keyword" [
    testCase "WHY - extractDecls - a type with no doc comment keeps its `type` keyword, because the compiler's range starts at the name and a declaration emitted without the keyword does not compile" <| fun _ ->
      match typesIn "module M\n\ntype AddedBox = { Text: string }\n" with
      | [ box ] ->
        box.Text |> Expect.equal "the type as written" "type AddedBox = { Text: string }"
        box.StartLine |> Expect.equal "on the line of the keyword" 3
      | other -> failtestf "expected one type, got %d" other.Length

    testCase "WHY - extractDecls - a documented type starts at its doc comment, and the keyword is inside it" <| fun _ ->
      match typesIn "module M\n\n/// A box.\ntype Box = { Text: string }\n" with
      | [ box ] ->
        box.Text |> Expect.stringContains "the keyword" "type Box = { Text: string }"
        box.Text |> Expect.stringContains "the doc comment" "/// A box."
      | other -> failtestf "expected one type, got %d" other.Length

    testCase "WHY - extractDecls - a type that is the second of an `and` group is a declaration of its own, so it starts with `type`" <| fun _ ->
      match typesIn "module M\n\ntype P = { X: int }\nand Q = { Y: int }\n" |> List.map (fun d -> d.Name, d.Text) with
      | [ "P", p; "Q", q ] ->
        p |> Expect.equal "the first as written" "type P = { X: int }"
        q |> Expect.equal "the second without `and`" "type Q = { Y: int }"
      | other -> failtestf "expected P and Q, got %A" other

    testCase "WHY - recordShapeOf - a type that starts at its keyword is still read, because the field names are how a migration recognises a carried field" <| fun _ ->
      match typesIn "module M\n\ntype Box = { Text: string; Count: int }\n" with
      | [ box ] -> (recordShapeOf box).Fields |> List.map _.Name |> Expect.equal "both fields" [ "Text"; "Count" ]
      | other -> failtestf "expected one type, got %d" other.Length

    testCase "WHY - declaredRecordFields - a documented record is read too, which it was not while the keyword was prefixed twice" <| fun _ ->
      match typesIn "module M\n\n/// A box.\ntype Box = { Text: string }\n" with
      | [ box ] -> declaredRecordFields box |> Expect.equal "its field" (Some [ "Text" ])
      | other -> failtestf "expected one type, got %d" other.Length
  ]
