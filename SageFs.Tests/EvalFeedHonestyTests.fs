module SageFs.Tests.EvalFeedHonestyTests

// WHY — an agent that evaluates through MCP reads its own session back through
// `get_cell_dependencies`, `plan_ripple`, `preview_what_if`, `suggest_next_cell` and the
// filmstrip. Every one of them folds the eval history, and the MCP text path stores each
// result as `Result: val x: int = 1`. The two binding readers only looked at lines that
// START with `val `, so the first `val` line of every statement was invisible: the cell
// graph had nodes and no producers, so "0 downstream", "0 steps" and "No bindings in
// scope" came out of a session that plainly had bindings.
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features

/// Verbatim what the daemon recorded for `let a6x = 1;;` then `let a6y = a6x + 1;;`, sent as one
/// send_fsharp_code call (two statements, so two prefixed blocks).
let private mcpTwoStatements = "Result: val a6x: int = 1\n\n\n\nResult: val a6y: int = 2\n\n"

/// The wire text `formatWorkerEvalResult` (Mcp.fs) puts in front of an eval's output.
let private mcpResultPrefix = "Result: "

let private recordMcpEvals () =
  FeatureHooks.FeaturePushState.empty
  |> FeatureHooks.recordEval "let a6x = 1" "Result: val a6x: int = 1" 4L
  |> FeatureHooks.recordEval "let a6y = a6x + 1" "Result: val a6y: int = 2" 4L

let private genName = Gen.elements [ "a"; "b"; "count"; "x'"; "total2" ]

let private genValLine =
  Gen.map2 (fun name n -> sprintf "val %s: int = %d" name n) genName (Gen.choose (0, 999))

[<Tests>]
let mcpResultPrefixTests =
  testList "MCP eval output reaches the analysis feeds" [

    testCase "producedNames reads the val behind the MCP result prefix" <| fun _ ->
      CellDependencyGraph.producedNames mcpTwoStatements
      |> Expect.equal "both statements' bindings are produced names" [ "a6x"; "a6y" ]

    testCase "parseBindings reads the val behind the MCP result prefix" <| fun _ ->
      BindingExplorer.parseBindings mcpTwoStatements
      |> List.map (fun (name, typeSig, _) -> name, typeSig)
      |> Expect.equal "both statements' bindings are parsed" [ "a6x", "int"; "a6y", "int" ]

    testCase "output that carries no prefix (the /exec path) still reads the same" <| fun _ ->
      CellDependencyGraph.producedNames "val a6x: int = 1\nval a6y: int = 2"
      |> Expect.equal "the plain path is unchanged" [ "a6x"; "a6y" ]

    testCase "a recorded MCP eval puts its binding in the store's known bindings" <| fun _ ->
      let state = recordMcpEvals ()
      state.History.KnownBindings
      |> Map.toList
      |> Expect.equal "each binding is known, by the cell that made it" [ ("a6x", 0); ("a6y", 1) ]

    testCase "the cell graph links an MCP cell to the MCP cell that uses its binding" <| fun _ ->
      let graph = FeatureHooks.cellGraph (recordMcpEvals ())
      graph.Edges |> Expect.equal "cell 0 feeds cell 1" [ (0, 1) ]

    testCase "the scope holds every binding an MCP eval made" <| fun _ ->
      let scope = FeatureHooks.scope (recordMcpEvals ())
      scope.ActiveBindings
      |> Map.keys
      |> Seq.toList
      |> Expect.equal "both bindings are in scope" [ "a6x"; "a6y" ]

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 200 }
      "putting the MCP result prefix in front of any val line changes nothing a reader sees" <|
      Prop.forAll (Arb.fromGen (Gen.nonEmptyListOf genValLine)) (fun lines ->
        let plain = String.concat "\n" lines
        let prefixed = lines |> List.map (fun l -> mcpResultPrefix + l) |> String.concat "\n\n"
        CellDependencyGraph.producedNames prefixed = CellDependencyGraph.producedNames plain
        && BindingExplorer.parseBindings prefixed = BindingExplorer.parseBindings plain)
  ]
