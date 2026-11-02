/// Step 8 of docs/plans/markers-on-expecto-cases.md: the deterministic
/// simulation for the marks a file shows while its text changes under it.
///
/// One scenario is one generated file: a `[<Tests>]` binding, nested lists,
/// one case per line, and a seeded schedule of the plan's five edits
/// (insert a line above, rename a case, delete a case, reorder the two
/// lists, give a case a computed name). Each run folds the REAL
/// `ExpectoCases.discover`, the REAL `ExpectoCases.place` and the REAL
/// `FileAnnotations.project` over the edited text. The twin computes its
/// placements from the initial text and never reads the text again, so its
/// marks drift as soon as a line is inserted above them.
///
/// `violations` states the plan's three invariants: an Exact mark's line
/// holds that case's literal name in the final text and the projection
/// shows an annotation there; a line shared by several marks carries only
/// NearestAncestor marks of one parent; a mark never lands in another
/// file than the leaf's own.
namespace SageFs.Simulation

open SageFs.Features.LiveTesting

module ExpectoCasesSim =

  /// One edit in the seeded schedule. An edit whose target is no longer in
  /// the text (a case an earlier edit renamed or deleted) is a no-op, the
  /// way a stale editor command simply finds nothing.
  type Edit =
    | InsertLineAbove of caseName: string
    | RenameCase of oldName: string * newName: string
    | DeleteCase of caseName: string
    | ReorderLists
    | AddComputedName of caseName: string

  /// A reflected leaf: what the running code knew before any edit. The
  /// chain is the structured path the plan keeps (list names then the
  /// case's own name), never a split of the joined full name.
  type Leaf =
    { FullName: string
      Segments: string list }

  type Scenario =
    { Seed: int
      FilePath: string
      Edits: Edit list
      Leaves: Leaf list
      InitialText: string }

  /// One mark: where a leaf was placed, in which file, and, for the
  /// sharing invariant, which list it fell back to.
  type Placement =
    { Leaf: string
      CaseName: string
      OwnFile: string
      MarkedFile: string
      Line: int
      IsExact: bool
      Parent: string list }

  type Trace =
    { Scenario: Scenario
      FinalText: string
      Placements: Placement list
      ProjectedLines: int list }

  type Violation =
    | NameNotOnLine of leaf: string * line: int
    | MarkNotProjected of leaf: string * line: int
    | SharedLine of line: int * leaves: string list
    | MarkInOtherFile of leaf: string * markedFile: string * ownFile: string

  /// The case names in a generated source, in document order: every line
  /// that opens a case with a string literal.
  let caseNamesIn (text: string) : string list =
    text.Split [| '\n' |]
    |> Array.toList
    |> List.choose (fun line ->
      let marker = line.IndexOf "testCase \""
      if marker < 0 then None
      else
        // `testCase "` is ten characters, so the opening quote sits at marker + 9.
        let openingQuote = marker + 9
        let closingQuote = line.IndexOf ('"', openingQuote + 1)
        if closingQuote < 0 then None
        else Some (line.Substring (openingQuote + 1, closingQuote - openingQuote - 1)))

  /// The initial text of one scenario: one binding, an outer list, the two
  /// sibling lists the reorder edit swaps, and an optional nested list so
  /// some chains are three deep. One case per line, because the plan's
  /// sharing invariant presumes a line is a case's own.
  let skeleton (alpha: int) (beta: int) (gamma: int) : string =
    let casesOf prefix count =
      [ for i in 1 .. count -> sprintf "    testCase \"%s%d\" (fun () -> ())" prefix i ]
    let gammaLines =
      match gamma with
      | 0 -> []
      | n -> [ "    testList \"gamma\" [" ] @ casesOf "g" n @ [ "    ]" ]
    [ "[<Tests>]"
      "let ``seeded suite`` = testList \"outer\" ["
      "  testList \"alpha\" [" ]
    @ casesOf "a" alpha
      @ gammaLines
      @ [ "  ]"
          "  testList \"beta\" [ " ]
      @ casesOf "b" beta
      @ [ "  ]"
          "]" ]
    |> String.concat "\n"

  /// The schedule for one seed: one to five edits. A destructive edit is
  /// only drawn while more than one case is still untouched, so every seed
  /// keeps at least one literal case and therefore at least one Exact mark.
  let generateEdits (rng: System.Random) (names: string list) : Edit list =
    let count = rng.Next(1, 6)
    let all = Array.ofList names
    let untouched = System.Collections.Generic.HashSet<string>(names)
    let stillUntouched () = untouched.Count > 1
    let pickExisting () = all.[rng.Next all.Length]
    let pickUntouched () =
      let live = untouched |> Seq.toArray
      live.[rng.Next live.Length]
    [ for _ in 1 .. count ->
        match rng.Next 100 with
        | n when n < 40 -> InsertLineAbove (pickExisting ())
        | n when n < 60 && stillUntouched () ->
            let target = pickUntouched ()
            untouched.Remove target |> ignore
            RenameCase (target, sprintf "renamed%d" (rng.Next(100, 10000)))
        | n when n < 75 && stillUntouched () ->
            let target = pickUntouched ()
            untouched.Remove target |> ignore
            DeleteCase target
        | n when n < 85 -> ReorderLists
        | _ when stillUntouched () ->
            let target = pickUntouched ()
            untouched.Remove target |> ignore
            AddComputedName target
        | _ -> InsertLineAbove (pickExisting ()) ]

  /// The line index of the case opened with this literal name, if the text
  /// still contains one.
  let indexOfCase (lines: string list) (caseName: string) : int option =
    lines
    |> List.tryFindIndex (fun line -> line.Contains(sprintf "testCase \"%s\"" caseName))

  /// The closing line of the list block that starts at `start`: the first
  /// later line whose bracket sits at the same indentation.
  let blockEnd (lines: string list) (start: int) : int =
    let indentOf (line: string) = line.Length - line.TrimStart().Length
    let indent = indentOf lines.[start]
    let rec loop i =
      if i >= lines.Length then lines.Length - 1
      elif lines.[i].Trim() = "]" && indentOf lines.[i] = indent && i > start then i
      else loop (i + 1)
    loop (start + 1)

  /// Apply one edit to the text. Every destructive edit looks its target up
  /// in the CURRENT text, so an edit drawn against a name a previous edit
  /// already renamed or deleted finds nothing and leaves the text alone.
  let applyEdit (text: string) (edit: Edit) : string =
    let lines = text.Split [| '\n' |] |> Array.toList
    let rebuild (xs: string list) = String.concat "\n" xs
    let replaceLine i updated = List.mapi (fun idx l -> if idx = i then updated else l) lines
    match edit with
    | InsertLineAbove caseName ->
      match indexOfCase lines caseName with
      | None -> text
      | Some i ->
        let indent = System.String(' ', lines.[i].Length - lines.[i].TrimStart().Length)
        rebuild (List.truncate i lines @ (indent + "// seeded edit" :: List.skip i lines))
    | RenameCase (oldName, newName) ->
      match indexOfCase lines oldName with
      | None -> text
      | Some i ->
        let line = lines.[i]
        rebuild (replaceLine i (line.Replace(sprintf "\"%s\"" oldName, sprintf "\"%s\"" newName)))
    | DeleteCase caseName ->
      match indexOfCase lines caseName with
      | None -> text
      | Some i -> rebuild (lines |> List.indexed |> List.choose (fun (idx, l) -> if idx = i then None else Some l))
    | AddComputedName caseName ->
      match indexOfCase lines caseName with
      | None -> text
      | Some i ->
        let line = lines.[i]
        rebuild (replaceLine i (line.Replace(sprintf "testCase \"%s\"" caseName, "testCase (sprintf \"computed-%d\" 1)")))
    | ReorderLists ->
      let startA = lines |> List.tryFindIndex (fun l -> l.Contains "testList \"alpha\"")
      let startB = lines |> List.tryFindIndex (fun l -> l.Contains "testList \"beta\"")
      match startA, startB with
      | Some a, Some b ->
        let (loStart, loEnd), (hiStart, hiEnd) =
          if a <= b then (a, blockEnd lines a), (b, blockEnd lines b)
          else (b, blockEnd lines b), (a, blockEnd lines a)
        let between =
          if loEnd + 1 >= hiStart then [] else lines.[(loEnd + 1) .. (hiStart - 1)]
        let tail =
          if hiEnd + 1 > lines.Length - 1 then [] else lines.[(hiEnd + 1) .. (lines.Length - 1)]
        rebuild (lines.[0 .. loStart - 1] @ lines.[hiStart .. hiEnd] @ between @ lines.[loStart .. loEnd] @ tail)
      | _ -> text

  /// The reflected leaves of the generated structure: every case's chain,
  /// in document order.
  let leavesOf (alpha: int) (beta: int) (gamma: int) : Leaf list =
    let leaf chain name =
      { FullName = "Simulated." + String.concat "." (chain @ [ name ])
        Segments = chain @ [ name ] }
    let alphaLeaves = [ for i in 1 .. alpha -> leaf [ "outer"; "alpha" ] (sprintf "a%d" i) ]
    let gammaLeaves =
      match gamma with
      | 0 -> []
      | n -> [ for i in 1 .. n -> leaf [ "outer"; "alpha"; "gamma" ] (sprintf "g%d" i) ]
    let betaLeaves = [ for i in 1 .. beta -> leaf [ "outer"; "beta" ] (sprintf "b%d" i) ]
    alphaLeaves @ gammaLeaves @ betaLeaves

  /// The scenario for one seed.
  let fromSeed (seed: int) : Scenario =
    let rng = System.Random(seed)
    let alpha = rng.Next(1, 4)
    let beta = rng.Next(1, 4)
    let gamma = if rng.Next 2 = 0 then 0 else rng.Next(1, 3)
    let text = skeleton alpha beta gamma
    let names = caseNamesIn text
    { Seed = seed
      FilePath = "SeedSuite.fs"
      Edits = generateEdits rng names
      Leaves = leavesOf alpha beta gamma
      InitialText = text }

  /// The fixed scenario the twin test pins: one line is inserted above a1.
  let insertLineAboveACase : Scenario =
    { Seed = 0
      FilePath = "FixedSuite.fs"
      Edits = [ InsertLineAbove "a1" ]
      Leaves =
        [ { FullName = "Fixed.alpha.a1"; Segments = [ "outer"; "alpha"; "a1" ] }
          { FullName = "Fixed.beta.b1"; Segments = [ "outer"; "beta"; "b1" ] } ]
      InitialText = skeleton 1 1 0 }

  let traceOf (scenario: Scenario) (finalText: string) (cases: SourceCase array) (placed: (string * CasePlacement) list) : Trace =
    let mapped =
      placed
      |> List.map (fun (fullName, placement) ->
        let segments =
          scenario.Leaves
          |> List.tryPick (fun l -> if l.FullName = fullName then Some l.Segments else None)
          |> Option.defaultValue [ fullName ]
        (fullName, segments, placement))
    let tests: TestCase array =
      mapped
      |> List.choose (fun (fullName, segments, placement) ->
        match placement with
        | CasePlacement.NotInSource -> None
        | _ ->
          let origin =
            match placement with
            | CasePlacement.Exact (file, line) -> TestOrigin.SourceMapped (file, line)
            | CasePlacement.NearestAncestor (file, line, _) -> TestOrigin.SourceMapped (file, line)
            | CasePlacement.NotInSource -> TestOrigin.ReflectionOnly
          Some
            { Id = TestId.create fullName TestFramework.Expecto
              FullName = fullName
              DisplayName = (match List.tryLast segments with Some n -> n | None -> fullName)
              NamePath = segments
              Origin = origin
              Labels = []
              Framework = TestFramework.Expecto
              Category = TestCategory.Unit })
      |> Array.ofList
    let entries: TestStatusEntry array =
      tests
      |> Array.map (fun test ->
        { TestId = test.Id
          DisplayName = test.DisplayName
          FullName = test.FullName
          Origin = test.Origin
          Framework = test.Framework
          Category = test.Category
          CurrentPolicy = RunPolicy.OnEveryChange
          Status = TestRunStatus.Detected
          PreviousStatus = TestRunStatus.Detected
          Provenance = ResultProvenance.Compiled })
    let state =
      LiveTestState.empty
      |> fun s -> { s with DiscoveredTests = tests }
      |> LiveTestState.withStatusEntries entries
    let annotations = FileAnnotations.project scenario.FilePath None state
    let placements =
      mapped
      |> List.choose (fun (fullName, segments, placement) ->
        let caseName = match List.tryLast segments with Some n -> n | None -> fullName
        match placement with
        | CasePlacement.NotInSource -> None
        | CasePlacement.Exact (file, line) ->
          Some
            { Leaf = fullName
              CaseName = caseName
              OwnFile = scenario.FilePath
              MarkedFile = file
              Line = line
              IsExact = true
              Parent = [] }
        | CasePlacement.NearestAncestor (file, line, _) ->
          let parent =
            cases
            |> Array.tryFind (fun c -> c.Line = line && (c.Kind = SourceKind.List || c.Kind = SourceKind.Binding))
            |> Option.map (fun c ->
              c.Path
              |> List.map (fun name ->
                match name with
                | SourceName.Literal s -> s
                | SourceName.Computed -> "?computed"))
            |> Option.defaultValue []
          Some
            { Leaf = fullName
              CaseName = caseName
              OwnFile = scenario.FilePath
              MarkedFile = file
              Line = line
              IsExact = false
              Parent = parent })
    let projected =
      annotations.TestAnnotations
      |> Array.map (fun a -> a.Line)
      |> Array.toList
    { Scenario = scenario
      FinalText = finalText
      Placements = placements
      ProjectedLines = projected }

  /// The real path: edit the text, re-read it, place, project.
  let run (scenario: Scenario) : Trace =
    let finalText = scenario.Edits |> List.fold applyEdit scenario.InitialText
    let cases = ExpectoCases.discover scenario.FilePath finalText
    let reflected = scenario.Leaves |> List.map (fun l -> l.FullName, l.Segments)
    let placed = ExpectoCases.place cases reflected
    traceOf scenario finalText cases placed

  /// The twin: place from the INITIAL text and never read the text again.
  let runTwin (scenario: Scenario) : Trace =
    let finalText = scenario.Edits |> List.fold applyEdit scenario.InitialText
    let casesAtStart = ExpectoCases.discover scenario.FilePath scenario.InitialText
    let reflected = scenario.Leaves |> List.map (fun l -> l.FullName, l.Segments)
    let placed = ExpectoCases.place casesAtStart reflected
    traceOf scenario finalText casesAtStart placed

  let violations (trace: Trace) : Violation list =
    let text = trace.FinalText.Split [| '\n' |]
    let lineOf n = if n >= 1 && n <= text.Length then text.[n - 1] else ""
    let namesAndProjection =
      trace.Placements
      |> List.choose (fun p ->
        if not p.IsExact then None
        elif not ((lineOf p.Line).Contains(p.CaseName)) then Some (NameNotOnLine (p.Leaf, p.Line))
        elif not (List.contains p.Line trace.ProjectedLines) then Some (MarkNotProjected (p.Leaf, p.Line))
        else None)
    let shared =
      trace.Placements
      |> List.groupBy (fun p -> p.MarkedFile, p.Line)
      |> List.choose (fun ((_, line), group) ->
        if List.length group <= 1 then None
        else
          let allNearest = group |> List.forall (fun p -> not p.IsExact)
          let oneParent = group |> List.map (fun p -> p.Parent) |> List.distinct |> List.length = 1
          if allNearest && oneParent then None
          else Some (SharedLine (line, group |> List.map (fun p -> p.Leaf))))
    let wrongFiles =
      trace.Placements
      |> List.choose (fun p ->
        if p.MarkedFile = p.OwnFile then None
        else Some (MarkInOtherFile (p.Leaf, p.MarkedFile, p.OwnFile)))
    namesAndProjection @ shared @ wrongFiles

  /// The twin produced a mark on a line that no longer holds the name: the
  /// invariant the offset table is guaranteed to break after an insert above.
  let twinHasNameViolation (scenario: Scenario) : bool =
    violations (runTwin scenario)
    |> List.exists (fun v -> match v with NameNotOnLine _ -> true | _ -> false)
