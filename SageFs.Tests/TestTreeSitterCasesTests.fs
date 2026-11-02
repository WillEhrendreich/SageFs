/// Step 1 of docs/plans/markers-on-expecto-cases.md: the parse shape of the six Expecto builder
/// forms, pinned to a golden file. The pure matcher in the plan reads node types and positions out
/// of exactly these shapes, so a grammar change that reshapes them fails here first, before any
/// mark is ever placed from a tree nobody looked at.
module SageFs.Tests.TestTreeSitterCasesTests

open System
open System.IO
open Expecto
open Expecto.Flip
open TreeSitter
open SageFs.Features.LiveTesting

let fixtureSource =
  String.concat "\n" [
    "[<Tests>]"
    "let ``six builders`` = testList \"outer\" ["
    "  testCase \"paren\" (fun () -> ())"
    "  testCase \"pipe\" <| fun _ -> ()"
    "  testTask \"task\" { do () }"
    "  testAsync \"async\" { do () }"
    "  testProperty \"prop\" (fun (x: int) -> x = x)"
    "  test \"builder\" { do () }"
    "]"
  ]

let repoRoot = RepoPaths.repoPathFull [||]

let goldenDir =
  Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "TreeSitter")

let goldenFile = Path.Combine(goldenDir, "expecto-builder-shapes.txt")

[<Literal>]
let UpdateVariable = "SAGEFS_UPDATE_TREE_SITTER_GOLDEN"

/// One line per node: `type [row:col]`, and the source text for a leaf, so both the shape and the
/// positions the matcher will read are pinned.
let renderShapeLines (library: string) (code: string) : string list =
  let language = new Language(library, "tree_sitter_fsharp")
  use parser = new Parser(language)
  use tree = parser.Parse(code)
  let rec findList (n: Node) : Node option =
    if n.Type = "list_expression" then Some n
    else
      n.Children
      |> Seq.fold (fun acc c -> match acc with Some _ -> acc | None -> findList c) None
  let rec render (n: Node) (depth: int) (into: ResizeArray<string>) =
    let indent = String(' ', depth * 2)
    let pos = sprintf "[%d:%d]" (int n.StartPosition.Row) (int n.StartPosition.Column)
    if n.IsNamed && n.Children.Count > 0 then
      into.Add(sprintf "%s%s %s" indent n.Type pos)
      for c in n.Children do render c (depth + 1) into
    else
      into.Add(sprintf "%s%s %s '%s'" indent n.Type pos (n.Text.Replace("\n", "\\n")))
  match findList tree.RootNode with
  | None -> [ "NO-LIST-FOUND" ]
  | Some listNode ->
      let into = ResizeArray<string>()
      listNode.NamedChildren
      |> Seq.iteri (fun i element ->
        into.Add(sprintf "--- shape-%d ---" i)
        render element 1 into)
      List.ofSeq into

[<Tests>]
let tests =
  testList "TestTreeSitter cases" [
    test "the six builder shapes parse to the pinned golden" {
      match TestTreeSitter.availability () with
      | TestTreeSitter.NativeAvailability.Degraded _ ->
        failtest (TestTreeSitter.describeAvailability ())
      | TestTreeSitter.NativeAvailability.Available (_, library) ->
        let actual = renderShapeLines library fixtureSource
        match Environment.GetEnvironmentVariable UpdateVariable = "1" with
        | true ->
          Directory.CreateDirectory goldenDir |> ignore
          File.WriteAllLines(goldenFile, actual)
          printfn "%s=1: wrote %d lines to %s" UpdateVariable actual.Length goldenFile
        | false ->
          File.Exists goldenFile
          |> Expect.isTrue
            (sprintf "golden file '%s' should exist; run with %s=1 to create it" goldenFile UpdateVariable)
          let pinned = File.ReadAllLines goldenFile |> List.ofArray
          pinned
          |> Expect.equal "the six builder shapes parse exactly as they did when pinned" actual
    }

    test "every builder in the matrix produces a SourceCase on its own line" {
      match TestTreeSitter.availability () with
      | TestTreeSitter.NativeAvailability.Degraded _ ->
        failtest (TestTreeSitter.describeAvailability ())
      | TestTreeSitter.NativeAvailability.Available _ ->
        let entries = ExpectoCases.discover "Fixture.fs" fixtureSource
        match entries |> Array.tryFind (fun e -> e.Kind = SourceKind.Binding) with
        | None -> failtest "the [<Tests>] binding was not discovered"
        | Some binding ->
          binding.Line |> Expect.equal "the binding starts on its first line" 1
          binding.Path |> Expect.equal "the binding carries no name path" []
        match entries |> Array.tryFind (fun e -> e.Kind = SourceKind.List) with
        | None -> failtest "the testList was not discovered"
        | Some list ->
          list.Path |> Expect.equal "the list carries its own name" [ SourceName.Literal "outer" ]
        let cases = entries |> Array.filter (fun e -> e.Kind = SourceKind.Case)
        cases.Length |> Expect.equal "one SourceCase per builder in the matrix" 6
        let asSegments (c: SourceCase) =
          c.Line, (c.Path |> List.map (fun s -> match s with SourceName.Literal text -> text | SourceName.Computed -> "<computed>"))
        cases
        |> Array.map asSegments
        |> Array.sortBy fst
        |> List.ofArray
        |> Expect.equal "each builder sits on the line it was written on, under the list chain above it"
          [ 3, [ "outer"; "paren" ]
            4, [ "outer"; "pipe" ]
            5, [ "outer"; "task" ]
            6, [ "outer"; "async" ]
            7, [ "outer"; "prop" ]
            8, [ "outer"; "builder" ] ]
    }

    test "a name the text computes is discovered as Computed, never guessed" {
      match TestTreeSitter.availability () with
      | TestTreeSitter.NativeAvailability.Degraded _ ->
        failtest (TestTreeSitter.describeAvailability ())
      | TestTreeSitter.NativeAvailability.Available _ ->
        let source =
          String.concat "\n" [
            "[<Tests>]"
            "let ``computed name`` = testList \"suite\" ["
            "  testCase (sprintf \"case %d\" 1) (fun () -> ())"
            "]"
          ]
        let cases =
          ExpectoCases.discover "Fixture.fs" source
          |> Array.filter (fun e -> e.Kind = SourceKind.Case)
        match cases with
        | [| case |] ->
          case.Path
          |> Expect.equal "the computed name stays Computed all the way down"
            [ SourceName.Literal "suite"; SourceName.Computed ]
          case.Line |> Expect.equal "the case still lands on its own line" 3
        | other -> failtestf "expected exactly one discovered case, found %d" other.Length
    }

    test "a list nested in lists names each ancestor once, and so does the case inside it" {
      match TestTreeSitter.availability () with
      | TestTreeSitter.NativeAvailability.Degraded _ ->
        failtest (TestTreeSitter.describeAvailability ())
      | TestTreeSitter.NativeAvailability.Available _ ->
        let source =
          String.concat "\n" [
            "[<Tests>]"
            "let ``nested`` = testList \"outer\" ["
            "  testList \"alpha\" ["
            "    testList \"gamma\" ["
            "      testCase \"g1\" (fun () -> ())"
            "    ]"
            "  ]"
            "]"
          ]
        let asNames (path: SourceName list) =
          path |> List.map (fun s -> match s with SourceName.Literal text -> text | SourceName.Computed -> "<computed>")
        let entries = ExpectoCases.discover "Fixture.fs" source
        let lists = entries |> Array.filter (fun e -> e.Kind = SourceKind.List)
        lists
        |> Array.map (fun e -> e.Line, asNames e.Path)
        |> List.ofArray
        |> Expect.equal "each list carries the lists above it once, then its own name"
          [ 2, [ "outer" ]
            3, [ "outer"; "alpha" ]
            4, [ "outer"; "alpha"; "gamma" ] ]
        entries
        |> Array.filter (fun e -> e.Kind = SourceKind.Case)
        |> Array.map (fun e -> e.Line, asNames e.Path)
        |> List.ofArray
        |> Expect.equal "the case sits under the full chain of lists around it"
          [ 5, [ "outer"; "alpha"; "gamma"; "g1" ] ]
    }
  ]
