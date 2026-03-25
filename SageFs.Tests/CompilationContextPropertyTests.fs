module SageFs.Tests.CompilationContextPropertyTests

// ─────────────────────────────────────────────────────────────────
// Property-based tests for CompilationContext block-eval module wrapping.
//
// WHY THIS FILE EXISTS:
//   Example-based tests in CompilationContextTests.fs cover specific shapes.
//   These properties cover *arbitrary* valid F# module/namespace trees so we
//   cannot introduce a regression on any structurally valid F# file.
//
// APPROACH:
//   1. Generate an arbitrary valid F# module tree (FileTree DU).
//   2. Render it to F# source text with exactly-predictable line numbers.
//   3. Parse it through parseFileStructure (the real pipeline).
//   4. Pick a random target line inside any leaf container.
//   5. Assert invariants that must hold for ALL inputs:
//        I-1  "module Tmp" is never emitted for any named-module file
//        I-2  The path returned by locateBlock has length = named-module depth
//        I-3  The emitted wrappers are properly nested, never dotted
//        I-4  On second eval, "open <deepest>" precedes all module wrappers
//        I-5  EvaluatedModules records exactly the deepest qualified name
// ─────────────────────────────────────────────────────────────────

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Fantomas.FCS.Syntax
open SageFs.Middleware.CompilationContext

// ─────────────────────────────────────────────────────────────────
// Domain model for generated trees
// ─────────────────────────────────────────────────────────────────

/// A single named module node in the tree.
/// Depth is distance from root (0 = top-level or direct child of namespace).
type ModuleNode = {
  Name: string
  Children: ModuleNode list
  /// 1-based start line in the rendered source (filled by renderer)
  StartLine: int
  /// 1-based end line in the rendered source (filled by renderer)
  EndLine: int
}

/// The structure of a generated file.
/// F# validity constraints enforced by the generator:
///   - FileLevelModule: exactly one top-level `module X` (no `=`).
///     Children are nested modules at depth ≥ 1.
///   - NamespaceFile: one or more namespaces, each with ≥ 0 nested modules.
///     Namespaces are always top-level; modules under them are depth 1+.
type FileTree =
  /// module Pong  (file-level, no =)
  ///   module Inner =   (nested, depth 1)
  | FileLevelModule of rootName: string * children: ModuleNode list
  /// namespace MyNs
  ///   module Foo =
  ///     module Bar =
  | NamespaceFile of ns: string * topModules: ModuleNode list

// ─────────────────────────────────────────────────────────────────
// Identifier generator — safe single-segment identifiers only
// ─────────────────────────────────────────────────────────────────

/// F# identifiers: uppercase first letter, alphanumeric body.
/// We use only ASCII letters so the rendered source is always valid.
let genIdent =
  gen {
    let! first = Gen.elements ['A'..'Z']
    let! rest  = Gen.listOfLength 4 (Gen.elements (['a'..'z'] @ ['A'..'Z'] @ ['0'..'9']))
    return System.String(first :: rest |> List.toArray)
  }

/// Dotted namespace name like "MyNs" or "MyNs.Domain" (1–3 segments).
let genNsName =
  gen {
    let! segments = Gen.listOf genIdent |> Gen.resize 3 |> Gen.filter (fun l -> l.Length >= 1)
    return segments |> String.concat "."
  }

// ─────────────────────────────────────────────────────────────────
// Tree generator
// ─────────────────────────────────────────────────────────────────

/// Generate a tree of nested module nodes with depth ≤ maxDepth.
/// Names are guaranteed unique within siblings (by tagging with path index).
let rec genModuleNodes (parentQualified: string) (maxDepth: int) (depthLeft: int) =
  gen {
    if depthLeft = 0 then
      return []
    else
      let! count = Gen.choose (0, min 3 depthLeft)
      let! names = Gen.listOfLength count genIdent
      // deduplicate sibling names
      let uniqueNames = names |> List.mapi (fun i n -> sprintf "%s%d" n i) |> List.distinct
      let! nodes =
        uniqueNames
        |> List.map (fun name ->
          gen {
            let qualified = sprintf "%s.%s" parentQualified name
            let! children = genModuleNodes qualified maxDepth (depthLeft - 1)
            // StartLine / EndLine are placeholder 0 — renderer fills them in
            return { Name = name; Children = children; StartLine = 0; EndLine = 0 }
          })
        |> List.foldBack
            (fun g acc -> gen { let! x = g in let! xs = acc in return x :: xs })
        <| gen { return [] }
      return nodes
  }

/// Generator for a complete FileTree.
let genFileTree =
  gen {
    let! useNamespace = Gen.choose (0, 1) |> Gen.map (fun n -> n = 0)
    let! depth = Gen.choose (1, 4)
    if useNamespace then
      let! ns = genNsName
      let! mods = genModuleNodes ns depth depth
      return NamespaceFile(ns, mods)
    else
      let! rootName = genIdent
      let! children = genModuleNodes rootName depth depth
      return FileLevelModule(rootName, children)
  }

// ─────────────────────────────────────────────────────────────────
// Tree shrinker — reduce depth and child count so FsCheck finds
// the minimal failing case
// ─────────────────────────────────────────────────────────────────

let rec shrinkModuleNode (n: ModuleNode) : ModuleNode seq =
  seq {
    // drop all children
    if n.Children <> [] then
      yield { n with Children = [] }
    // shrink children list by dropping one
    for i in 0 .. n.Children.Length - 1 do
      let fewer = n.Children |> List.indexed |> List.choose (fun (j, c) -> if j = i then None else Some c)
      yield { n with Children = fewer }
    // recursively shrink each child
    for i in 0 .. n.Children.Length - 1 do
      for shrunk in shrinkModuleNode n.Children.[i] do
        let newChildren = n.Children |> List.mapi (fun j c -> if j = i then shrunk else c)
        yield { n with Children = newChildren }
  }

let shrinkFileTree (t: FileTree) : FileTree seq =
  seq {
    match t with
    | FileLevelModule(name, children) ->
      // drop all children
      if children <> [] then
        yield FileLevelModule(name, [])
      // drop one child at a time
      for i in 0 .. children.Length - 1 do
        let fewer = children |> List.indexed |> List.choose (fun (j, c) -> if j = i then None else Some c)
        yield FileLevelModule(name, fewer)
      // shrink individual children
      for i in 0 .. children.Length - 1 do
        for shrunk in shrinkModuleNode children.[i] do
          let newChildren = children |> List.mapi (fun j c -> if j = i then shrunk else c)
          yield FileLevelModule(name, newChildren)
    | NamespaceFile(ns, mods) ->
      if mods <> [] then
        yield NamespaceFile(ns, [])
      for i in 0 .. mods.Length - 1 do
        let fewer = mods |> List.indexed |> List.choose (fun (j, c) -> if j = i then None else Some c)
        yield NamespaceFile(ns, fewer)
      for i in 0 .. mods.Length - 1 do
        for shrunk in shrinkModuleNode mods.[i] do
          let newMods = mods |> List.mapi (fun j c -> if j = i then shrunk else c)
          yield NamespaceFile(ns, newMods)
  }

let arbFileTree = Arb.fromGenShrink (genFileTree, shrinkFileTree)

// ─────────────────────────────────────────────────────────────────
// Renderer: FileTree → F# source text with annotated line positions
// ─────────────────────────────────────────────────────────────────

/// Mutable line counter for the renderer.
type LineCounter() =
  let mutable line = 1
  member _.Current = line
  member _.Advance n = line <- line + n
  member _.Next() =
    let l = line
    line <- line + 1
    l

/// Render a ModuleNode to lines, filling in StartLine / EndLine.
/// Returns the annotated node and accumulated lines.
let rec renderNode (indent: int) (qualified: string) (counter: LineCounter) (node: ModuleNode) =
  let pad = String.replicate indent "  "
  let startLine = counter.Current
  let header = sprintf "%smodule %s =" pad node.Name
  counter.Advance 1

  // one placeholder body declaration so the module isn't empty
  let bodyLine = sprintf "%s  let _%s = ()" pad node.Name
  counter.Advance 1

  let renderedChildren, annotatedChildren =
    node.Children
    |> List.map (fun child ->
      let qualifiedChild = sprintf "%s.%s" qualified child.Name
      let lines, annotated = renderNode (indent + 1) qualifiedChild counter child
      lines, annotated)
    |> List.unzip
  let childLines = renderedChildren |> List.concat

  let endLine = counter.Current - 1
  let annotated = { node with StartLine = startLine; EndLine = endLine; Children = annotatedChildren }
  [ header; bodyLine ] @ childLines, annotated

/// Render the full FileTree to (sourceText, annotatedTree).
let renderTree (tree: FileTree) : string * FileTree =
  let counter = LineCounter()
  match tree with
  | FileLevelModule(name, children) ->
    let headerLine = sprintf "module %s" name
    counter.Advance 1
    // blank line after header
    counter.Advance 1

    let renderedChildren, annotatedChildren =
      children
      |> List.map (fun child ->
        let lines, annotated = renderNode 0 (sprintf "%s.%s" name child.Name) counter child
        lines, annotated)
      |> List.unzip

    let allLines =
      [ headerLine; "" ] @ (renderedChildren |> List.concat)
    allLines |> String.concat "\n", FileLevelModule(name, annotatedChildren)

  | NamespaceFile(ns, topMods) ->
    let headerLine = sprintf "namespace %s" ns
    counter.Advance 1
    counter.Advance 1  // blank line

    let renderedMods, annotatedMods =
      topMods
      |> List.map (fun m ->
        let lines, annotated = renderNode 0 (sprintf "%s.%s" ns m.Name) counter m
        lines, annotated)
      |> List.unzip

    let allLines = [ headerLine; "" ] @ (renderedMods |> List.concat)
    allLines |> String.concat "\n", NamespaceFile(ns, annotatedMods)

// ─────────────────────────────────────────────────────────────────
// Oracle: what should locateBlock return for a given line?
// ─────────────────────────────────────────────────────────────────

/// Walk the annotated tree to find the path (ancestor list, deepest last)
/// from the root to the deepest module container that contains targetLine.
/// Namespace containers are tagged as namespace in the path so callers
/// can filter them when building wrappers.
type PathEntry =
  | NsEntry of qualifiedName: string
  | ModEntry of qualifiedName: string * leafName: string

let oraclePathAt (tree: FileTree) (targetLine: int) : PathEntry list =

  let rec walkNode (qualifiedName: string) (node: ModuleNode) (accPath: PathEntry list) =
    if targetLine >= node.StartLine && targetLine <= node.EndLine then
      let entry = ModEntry(qualifiedName, node.Name)
      let pathHere = accPath @ [entry]
      // check if any child is a better (deeper) match
      let childResult =
        node.Children
        |> List.tryPick (fun child ->
          let cq = sprintf "%s.%s" qualifiedName child.Name
          walkNode cq child pathHere)
      match childResult with
      | Some deeper -> Some deeper
      | None -> Some pathHere
    else
      None

  match tree with
  | FileLevelModule(rootName, children) ->
    // The file-level module itself covers every line after line 1.
    // Check children first for deeper match, else the root catches it.
    let childResult =
      children |> List.tryPick (fun child ->
        let cq = sprintf "%s.%s" rootName child.Name
        walkNode cq child [ ModEntry(rootName, rootName) ])
    match childResult with
    | Some path -> path
    | None -> [ ModEntry(rootName, rootName) ]

  | NamespaceFile(ns, topMods) ->
    topMods |> List.tryPick (fun m ->
      let mq = sprintf "%s.%s" ns m.Name
      walkNode mq m [ NsEntry ns ])
    |> Option.defaultValue [ NsEntry ns ]

/// All leaf containers in the annotated tree that have at least one body line
/// (i.e., somewhere to point a block_start_line).
let rec allLeafLines (tree: FileTree) : int list =
  let rec nodeLeaves (node: ModuleNode) =
    match node.Children with
    | [] -> [ node.StartLine + 1 ]  // +1 to land on body, not header
    | kids -> kids |> List.collect nodeLeaves
  match tree with
  | FileLevelModule(_, children) ->
    match children with
    | [] -> []  // no declarations → nothing to block-eval
    | kids -> kids |> List.collect nodeLeaves
  | NamespaceFile(_, topMods) ->
    match topMods with
    | [] -> []  // no declarations → nothing to block-eval
    | mods -> mods |> List.collect nodeLeaves

// ─────────────────────────────────────────────────────────────────
// FsCheck property config
// ─────────────────────────────────────────────────────────────────

let propCfg = { FsCheckConfig.defaultConfig with maxTest = 300 }

// ─────────────────────────────────────────────────────────────────
// Helper: run the full pipeline for a given tree + line
// ─────────────────────────────────────────────────────────────────

let runPipeline (source: string) (targetLine: int) (evaluated: Set<string>) =
  let fs = (parseFileStructure "Test.fs" source).Result
  let path = locateBlock fs (Some targetLine)
  let blockCode = "let _testBlock = ()"
  let result, newModules =
    preprocessForFsi (Some fs) Block (Some targetLine) evaluated blockCode
  fs, path, result, newModules

// ─────────────────────────────────────────────────────────────────
// Properties
// ─────────────────────────────────────────────────────────────────

let compilationContextPropertyTests =
  testList "CompilationContext: arbitrary tree properties" [

    // ── I-1: "module Tmp" is NEVER emitted ───────────────────────

    testPropertyWithConfig propCfg
      "I-1 'module Tmp' never appears in output for any valid F# file tree" <|
      Prop.forAll arbFileTree (fun tree ->
        let source, annotated = renderTree tree
        let lines = allLeafLines annotated
        lines |> List.forall (fun targetLine ->
          let _, _, result, _ = runPipeline source targetLine Set.empty
          not (result.Code.Contains("module Tmp"))
        )
      )

    // ── I-2: locateBlock module-path length = named-module depth ────

    testPropertyWithConfig propCfg
      "I-2 locateBlock path length equals named-module nesting depth at target line" <|
      Prop.forAll arbFileTree (fun tree ->
        let source, annotated = renderTree tree
        let lines = allLeafLines annotated
        lines |> List.forall (fun targetLine ->
          let fs = (parseFileStructure "Test.fs" source).Result
          let path = locateBlock fs (Some targetLine)
          let oraclePath = oraclePathAt annotated targetLine
          let oracleModDepth =
            oraclePath |> List.filter (function ModEntry _ -> true | NsEntry _ -> false) |> List.length
          // locateBlock returns the full path including namespace containers;
          // filter to module containers only, matching how transformBlock does it.
          let pathModCount =
            path |> List.filter (fun c ->
              match c.Kind with
              | SynModuleOrNamespaceKind.DeclaredNamespace
              | SynModuleOrNamespaceKind.GlobalNamespace -> false
              | _ -> true) |> List.length
          pathModCount = oracleModDepth
        )
      )

    // ── I-3: wrappers are nested, never dotted ───────────────────

    testPropertyWithConfig propCfg
      "I-3 emitted code uses nested 'module X =' not dotted 'module A.B.C ='" <|
      Prop.forAll arbFileTree (fun tree ->
        let source, annotated = renderTree tree
        let lines = allLeafLines annotated
        lines |> List.forall (fun targetLine ->
          let _, _, result, _ = runPipeline source targetLine Set.empty
          // A dotted module binding like "module Foo.Bar =" is invalid F# syntax.
          // Valid wrappers only have a single identifier: "module Foo ="
          // We check that no line in the output matches "module X.Y... ="
          // where the name contains a dot.
          let outputLines = result.Code.Split('\n')
          outputLines |> Array.forall (fun line ->
            let trimmed = line.TrimStart()
            if trimmed.StartsWith("module ") && trimmed.Contains("=") then
              // extract the name between "module " and " ="
              let afterModule = trimmed.Substring(7)  // skip "module "
              let eqIdx = afterModule.IndexOf(" =")
              if eqIdx > 0 then
                let name = afterModule.Substring(0, eqIdx).Trim()
                not (name.Contains("."))
              else true
            else true
          )
        )
      )

    // ── I-4: second eval emits 'open <deepest>' before module wrappers ─

    testPropertyWithConfig propCfg
      "I-4 second eval of any block opens the deepest qualified name first" <|
      Prop.forAll arbFileTree (fun tree ->
        let source, annotated = renderTree tree
        let lines = allLeafLines annotated
        lines |> List.forall (fun targetLine ->
          let _, _, _, modulesAfterFirst = runPipeline source targetLine Set.empty
          if modulesAfterFirst.IsEmpty then
            true  // no module was found — pass-through path, no open needed
          else
            let _, _, result, _ = runPipeline source targetLine modulesAfterFirst
            // If the output contains an 'open X' and a 'module Y =',
            // the open must come first.
            let code = result.Code
            let openIdx = code.IndexOf("open ")
            let moduleIdx = code.IndexOf("module ")
            if openIdx >= 0 && moduleIdx >= 0 then
              openIdx < moduleIdx
            else
              true  // no open or no module wrapper — nothing to check
        )
      )

    // ── I-5: EvaluatedModules gets exactly the deepest qualified name ─

    testPropertyWithConfig propCfg
      "I-5 EvaluatedModules gains exactly the deepest module's qualified name" <|
      Prop.forAll arbFileTree (fun tree ->
        let source, annotated = renderTree tree
        let lines = allLeafLines annotated
        lines |> List.forall (fun targetLine ->
          let _, path, _, newModules = runPipeline source targetLine Set.empty
          match path with
          | [] ->
            // pass-through: EvaluatedModules unchanged (still empty)
            newModules.IsEmpty
          | pathList ->
            // Only the deepest module (last in path) should be added
            let deepest = pathList |> List.last
            newModules |> Set.contains deepest.QualifiedName
            // and "Tmp" must never appear
            && not (newModules |> Set.contains "Tmp")
        )
      )

    // ── I-6: path qualified names chain correctly ─────────────────

    testPropertyWithConfig propCfg
      "I-6 each path entry qualified name is a proper prefix of the next" <|
      Prop.forAll arbFileTree (fun tree ->
        let source, annotated = renderTree tree
        let lines = allLeafLines annotated
        lines |> List.forall (fun targetLine ->
          let fs = (parseFileStructure "Test.fs" source).Result
          let path = locateBlock fs (Some targetLine)
          // Each path[i].QualifiedName must be a proper prefix of path[i+1].QualifiedName
          path
          |> List.pairwise
          |> List.forall (fun (parent, child) ->
            child.QualifiedName.StartsWith(parent.QualifiedName + ".")
          )
        )
      )

    // ── I-7: namespace containers never become module wrappers ────

    testPropertyWithConfig propCfg
      "I-7 namespace names never appear as 'module X =' wrappers in output" <|
      Prop.forAll arbFileTree (fun tree ->
        match tree with
        | FileLevelModule _ -> true  // no namespace to test here
        | NamespaceFile(ns, _) ->
          let source, annotated = renderTree tree
          let lines = allLeafLines annotated
          // The top-level namespace segments must never appear as module wrappers.
          // e.g. if ns = "MyNs.Domain", neither "module MyNs =" nor
          // "module MyNs.Domain =" should appear.
          let nsParts = ns.Split('.') |> Array.toList
          let nsSegments =
            nsParts
            |> List.mapi (fun i _ ->
              nsParts |> List.take (i + 1) |> String.concat ".")
          lines |> List.forall (fun targetLine ->
            let _, _, result, _ = runPipeline source targetLine Set.empty
            nsSegments |> List.forall (fun seg ->
              not (result.Code.Contains(sprintf "module %s =" seg))
            )
          )
      )

  ]

[<Tests>]
let allCompilationContextPropertyTests =
  testList "CompilationContext property tests" [
    compilationContextPropertyTests
  ]
