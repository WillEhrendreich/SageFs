/// Step 3 of docs/plans/markers-on-expecto-cases.md: where each reflected test leaf
/// places its mark in the parsed source. The example pins the plan's two-case list,
/// the properties pin placement over generated files (literal names, one computed
/// name, names containing a slash), and the mutants ask whether a wrong fallback or
/// a wrong name would be noticed at all.
module SageFs.Tests.ExpectoCasesTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open MutationTestingFramework
open SageFs.Features.LiveTesting
open SageFs.Tests.SharedGenerators

/// One input to `place`, so a mutant can transform the function itself.
type PlaceInput = SourceCase array * (string * string list) list

let lit (name: string) : SourceName = SourceName.Literal name

let mkBinding (file: string) (line: int) (endLine: int) : SourceCase =
  { Kind = SourceKind.Binding
    FilePath = file
    Path = []
    Line = line
    Column = 1
    EndLine = endLine
    Body = "" }

let mkList (file: string) (path: SourceName list) (line: int) (endLine: int) : SourceCase =
  { Kind = SourceKind.List
    FilePath = file
    Path = path
    Line = line
    Column = 1
    EndLine = endLine
    Body = "" }

let mkCase (file: string) (path: SourceName list) (line: int) : SourceCase =
  { Kind = SourceKind.Case
    FilePath = file
    Path = path
    Line = line
    Column = 2
    EndLine = line
    Body = "()" }

// --- The plan's example (test 1) ------------------------------------------------------

let twoCaseSource (file: string) : SourceCase array =
  [| mkBinding file 1 10
     mkList file [ lit "a" ] 3 9
     mkCase file [ lit "a"; lit "x" ] 4
     mkCase file [ lit "a"; lit "y" ] 7 |]

let twoCaseReflected : (string * string list) list =
  [ "M.S.suite/a/x", ["a"; "x"]
    "M.S.suite/a/y", ["a"; "y"] ]

// --- A generated file (tests 4 and 5) -------------------------------------------------

/// The shape of a generated file before names, lines and kinds are assigned:
/// a case, or a list of children. Names are handed out later from one counter,
/// so two paths in one generated file can never collide.
type NodeShape =
  | ShapeCase
  | ShapeList of NodeShape list

let rec genShape (depth: int) : Gen<NodeShape> =
  gen {
    match depth with
    | 0 -> return ShapeCase
    | _ ->
      let! isCase = Gen.choose (0, 2)
      if isCase = 0 then
        return ShapeCase
      else
        let! count = Gen.choose (1, 3)
        let! children = Gen.listOfLength count (genShape (depth - 1))
        return ShapeList children
  }

/// One generated file: one root shape per binding, one to three bindings.
let genBindings : Gen<NodeShape list> =
  gen {
    let! count = Gen.choose (1, 3)
    let! bindings = Gen.listOfLength count (genShape 3)
    return bindings
  }

type BuiltCase =
  { FullName: string
    Binding: int
    Path: int list
    Segments: string list
    Line: int }

type BuiltNode =
  { Binding: int
    Path: int list
    Line: int
    Name: string }

type BuildState =
  { NextName: int
    NextLine: int
    Source: SourceCase list
    Reflected: (string * string list) list
    Cases: BuiltCase list
    Nodes: BuiltNode list
    Bindings: (int * int * int) list }

type BuiltFile =
  { File: string
    Source: SourceCase list
    Reflected: (string * string list) list
    Cases: BuiltCase list
    Nodes: BuiltNode list
    Bindings: (int * int * int) list }

let rec shapeSize (shape: NodeShape) : int =
  match shape with
  | ShapeCase -> 1
  | ShapeList children -> 1 + (children |> List.map shapeSize |> List.sum)

let plainName (n: int) = sprintf "n%d" n

let slashName (n: int) = sprintf "n%d/slash" n

/// One line per node in source order, every node named from one counter, and
/// what each leaf needs back: its runtime path, its line, its binding. When
/// `computedAt` names a node, that node's SOURCE name becomes Computed while
/// the runtime path every consumer sees keeps the original literal.
let buildFile (nameOf: int -> string) (file: string) (computedAt: (int * int list) option) (bindings: NodeShape list) : BuiltFile =
  let rec walkShape (bIdx: int) (indexPath: int list) (sourcePath: SourceName list) (runtimePath: string list) (shape: NodeShape) (state: BuildState) : BuildState =
    let nameId = state.NextName
    let name = nameOf nameId
    let isComputed = computedAt = Some (bIdx, indexPath)
    let sourceName =
      if isComputed then SourceName.Computed else SourceName.Literal name
    let nodePath = sourcePath @ [sourceName]
    let line = state.NextLine
    match shape with
    | ShapeCase ->
      let entry: SourceCase =
        { Kind = SourceKind.Case
          FilePath = file
          Path = nodePath
          Line = line
          Column = 2
          EndLine = line
          Body = "()" }
      let segments = runtimePath @ [name]
      let fullName = sprintf "M.B%d/%s" bIdx (String.concat "/" segments)
      { state with
          NextName = nameId + 1
          NextLine = line + 1
          Source = entry :: state.Source
          Reflected = (fullName, segments) :: state.Reflected
          Cases = { FullName = fullName; Binding = bIdx; Path = indexPath; Segments = segments; Line = line } :: state.Cases
          Nodes = { Binding = bIdx; Path = indexPath; Line = line; Name = name } :: state.Nodes }
    | ShapeList children ->
      let size = shapeSize shape
      let entry: SourceCase =
        { Kind = SourceKind.List
          FilePath = file
          Path = nodePath
          Line = line
          Column = 1
          EndLine = line + size - 1
          Body = "" }
      let stateAfterSelf =
        { state with
            NextName = nameId + 1
            NextLine = line + 1
            Source = entry :: state.Source
            Nodes = { Binding = bIdx; Path = indexPath; Line = line; Name = name } :: state.Nodes }
      children
      |> List.mapi (fun i child -> i, child)
      |> List.fold (fun st (i, child) ->
        walkShape bIdx (indexPath @ [i]) nodePath (runtimePath @ [name]) child st) stateAfterSelf
  let initial: BuildState =
    { NextName = 0
      NextLine = 1
      Source = []
      Reflected = []
      Cases = []
      Nodes = []
      Bindings = [] }
  let final =
    bindings
    |> List.mapi (fun i root -> i, root)
    |> List.fold (fun state (bIdx, root) ->
      let bindingLine = state.NextLine
      let afterLine = { state with NextLine = bindingLine + 1 }
      let afterTree = walkShape bIdx [ 0 ] [] [] root afterLine
      let bindingEnd = afterTree.NextLine - 1
      let bindingEntry: SourceCase =
        { Kind = SourceKind.Binding
          FilePath = file
          Path = []
          Line = bindingLine
          Column = 1
          EndLine = bindingEnd
          Body = "" }
      { afterTree with
          Source = bindingEntry :: afterTree.Source
          Bindings = (bIdx, bindingLine, bindingEnd) :: afterTree.Bindings }) initial
  { File = file
    Source = List.rev final.Source
    Reflected = List.rev final.Reflected
    Cases = List.rev final.Cases
    Nodes = List.rev final.Nodes
    Bindings = List.rev final.Bindings }

let caseByFullName (built: BuiltFile) (fullName: string) : BuiltCase =
  built.Cases |> List.find (fun c -> c.FullName = fullName)

let bindingSpan (built: BuiltFile) (bIdx: int) : int * int =
  built.Bindings
  |> List.find (fun (i, _, _) -> i = bIdx)
  |> fun (_, startLine, endLine) -> startLine, endLine

let lineOf (placement: CasePlacement) : int option =
  match placement with
  | CasePlacement.Exact (_, line) -> Some line
  | CasePlacement.NearestAncestor (_, line, _) -> Some line
  | CasePlacement.NotInSource -> None

let placedInOwnBinding (built: BuiltFile) (placed: (string * CasePlacement) list) : bool =
  placed
  |> List.forall (fun (fullName, placement) ->
    match lineOf placement with
    | None -> false
    | Some line ->
      let startLine, endLine = bindingSpan built (caseByFullName built fullName).Binding
      startLine <= line && line <= endLine)

let expectedExact (built: BuiltFile) : (string * CasePlacement) list =
  built.Reflected
  |> List.map (fun (fullName, _) ->
    fullName, CasePlacement.Exact (built.File, (caseByFullName built fullName).Line))

// --- Mutant fixtures (plan test 7, place half; the categoryOf half joins step 6) ------

let mutantFile = "Fixture.fs"

let mutantSource : SourceCase array =
  [| mkBinding mutantFile 1 8
     mkList mutantFile [ lit "a" ] 3 7
     mkCase mutantFile [ lit "a"; lit "x" ] 4
     mkCase mutantFile [ lit "a"; SourceName.Computed ] 6 |]

let mutantReflected : (string * string list) list =
  [ "M.S.suite/a/x", ["a"; "x"]
    "M.S.suite/a/y", ["a"; "y"] ]

let placeBoth (input: PlaceInput) : (string * CasePlacement) list =
  let source, reflected = input
  ExpectoCases.place source reflected

let hasNearestAncestor (placed: (string * CasePlacement) list) : bool =
  placed
  |> List.exists (fun (_, placement) ->
    match placement with
    | CasePlacement.NearestAncestor _ -> true
    | _ -> false)

let dropLeafInstead : Mutant<PlaceInput -> (string * CasePlacement) list> =
  { Name = "the nearest-ancestor fallback drops the leaf"
    Description = "a leaf whose path holds a computed name disappears instead of landing on its ancestor"
    Apply = fun f (source, reflected) ->
      f (source, reflected)
      |> List.filter (fun (_, placement) ->
        match placement with
        | CasePlacement.NearestAncestor _ -> false
        | _ -> true) }

let computedAsEmptyLiteral : Mutant<PlaceInput -> (string * CasePlacement) list> =
  { Name = "a computed name is read as an empty literal"
    Description = "a computed segment only matches the empty string, so its leaves fall out of the source"
    Apply = fun f (source, reflected) ->
      let sourceWithEmpty =
        source
        |> Array.map (fun entry ->
          { entry with
              Path =
                entry.Path
                |> List.map (fun name ->
                  match name with
                  | SourceName.Computed -> SourceName.Literal ""
                  | SourceName.Literal _ as keep -> keep) })
      f (sourceWithEmpty, reflected) }

// --- Tests ---------------------------------------------------------------------------

[<Tests>]
let tests =
  testList "ExpectoCases" [
    testCase "two cases under one list place Exact on their own, different lines" <| fun () ->
      let file = "Fixture.fs"
      ExpectoCases.place (twoCaseSource file) twoCaseReflected
      |> Expect.equal "each leaf is Exact on its own line"
        [ "M.S.suite/a/x", CasePlacement.Exact (file, 4)
          "M.S.suite/a/y", CasePlacement.Exact (file, 7) ]

    testCase "a leaf no source knows is NotInSource, not a guess" <| fun () ->
      let file = "Fixture.fs"
      ExpectoCases.place (twoCaseSource file) [ "M.S.suite/a/ghost", ["a"; "ghost"] ]
      |> Expect.equal "an unknown leaf stays NotInSource"
        [ "M.S.suite/a/ghost", CasePlacement.NotInSource ]

    testPropertyWithConfig propConfig "every leaf of a literal file lands Exact on its own line, in source order" <| fun () ->
      Prop.forAll (Arb.fromGen genBindings) (fun bindings ->
        let built = buildFile plainName "Fixture.fs" None bindings
        let placed = ExpectoCases.place (List.toArray built.Source) built.Reflected
        let placedLines = placed |> List.choose (fun (_, p) -> lineOf p)
        let keptEveryLeaf = placedLines.Length = placed.Length
        let inSourceOrder =
          placedLines
          |> List.pairwise
          |> List.forall (fun (a, b) -> a < b)
        placed = expectedExact built
        && keptEveryLeaf
        && inSourceOrder
        && placedInOwnBinding built placed)

    testPropertyWithConfig propConfig "one computed name turns only its subtree into NearestAncestor, on the nearest literal ancestor" <| fun () ->
      Prop.forAll (Arb.fromGen (Gen.zip genBindings (Gen.choose (0, 1000003)))) (fun (bindings, pick) ->
        let plain = buildFile plainName "Fixture.fs" None bindings
        let chosen = plain.Nodes |> List.item (pick % plain.Nodes.Length)
        let built = buildFile plainName "Fixture.fs" (Some (chosen.Binding, chosen.Path)) bindings
        let expected =
          built.Cases
          |> List.map (fun c ->
            let inChosenSubtree =
              c.Binding = chosen.Binding
              && c.Path.Length >= chosen.Path.Length
              && List.take chosen.Path.Length c.Path = chosen.Path
            if not inChosenSubtree then
              c.FullName, CasePlacement.Exact (built.File, c.Line)
            else
              let ancestorLine =
                if chosen.Path.Length = 1 then
                  bindingSpan built chosen.Binding |> fun (startLine, _) -> startLine
                else
                  let parentPath = chosen.Path |> List.truncate (chosen.Path.Length - 1)
                  (plain.Nodes
                   |> List.find (fun n -> n.Binding = chosen.Binding && n.Path = parentPath)).Line
              c.FullName, CasePlacement.NearestAncestor (built.File, ancestorLine, [ chosen.Name ]))
        let placed = ExpectoCases.place (List.toArray built.Source) built.Reflected
        placed = expected && placedInOwnBinding built placed)

    testPropertyWithConfig propConfig "a name containing a slash still lands Exact, because the structured path is never split" <| fun () ->
      Prop.forAll (Arb.fromGen genBindings) (fun bindings ->
        let built = buildFile slashName "Fixture.fs" None bindings
        let placed = ExpectoCases.place (List.toArray built.Source) built.Reflected
        placed = expectedExact built && placedInOwnBinding built placed)

    detectsOutputMutant
      dropLeafInstead
      (mutantSource, mutantReflected)
      placeBoth
      hasNearestAncestor

    detectsOutputMutant
      computedAsEmptyLiteral
      (mutantSource, mutantReflected)
      placeBoth
      hasNearestAncestor
  ]
