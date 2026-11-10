module SageFs.Tests.ProjectFileChangeTests

/// `ProjectFileChange` (`SageFs.Core/Features/ProjectFileChange.fs`): what a change to a project file does to the tests a landing
/// runs. A project file used to be matched against coverage, which only names source documents, so a landing that added a source
/// file ran the whole suite and one that bumped a package ran whatever else the diff happened to select. These cases pin the diff
/// into a closed set of changes, the rule for which of them force the whole suite, and the scan that says who can see a source file.
/// The simulation in `CompileListSimTests` proves the selection is safe over seeded edits.
open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ProjectFileChange

let project = "src/App/App.fsproj"

/// A project file's text: a framework, the compile list, and whatever else the case adds after it.
let projectText (compiles: string list) (extra: string) : string =
  let items = compiles |> List.map (fun file -> sprintf "    <Compile Include=\"%s\" />" file) |> String.concat "\n"
  sprintf "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n%s\n  </ItemGroup>\n%s</Project>" items extra

let diffOf (before: string) (after: string) : Change list = diff project (Some before) (Some after)

let baseline = projectText [ "A.fs"; "B.fs"; "C.fs" ] ""

let noText (_: string) : string option = None

let namedKinds (changes: Change list) : string list =
  changes
  |> List.map (fun change ->
    match change with
    | Change.CompileList _ -> "compile"
    | Change.ReferenceChanged _ -> "reference"
    | Change.PackageChanged _ -> "package"
    | Change.PropertyChanged _ -> "property"
    | Change.OtherItemChanged _ -> "item"
    | Change.Unreadable _ -> "unreadable"
    | Change.ProjectCreated _ -> "created"
    | Change.ProjectDeleted _ -> "deleted"
    | Change.OtherBuildInput _ -> "input")

[<Tests>]
let diffTests =
  testList "ProjectFileChange.diff" [

    testCase "WHY — a file added at the end is one compile-list change carrying both lists, repository-relative" <| fun _ ->
      diffOf baseline (projectText [ "A.fs"; "B.fs"; "C.fs"; "D.fs" ] "")
      |> Expect.equal "the lists" [ Change.CompileList (project, [ "src/App/A.fs"; "src/App/B.fs"; "src/App/C.fs" ], [ "src/App/A.fs"; "src/App/B.fs"; "src/App/C.fs"; "src/App/D.fs" ]) ]

    testCase "WHY — backslash separators and a leading dot-slash in an include are read as the same path" <| fun _ ->
      let windows = baseline.Replace("\"B.fs\"", "\".\\B.fs\"")
      diffOf baseline windows |> Expect.isEmpty "the same list"

    testCase "WHY — indentation, blank lines and comments are no change at all" <| fun _ ->
      let reformatted = "<!-- a note -->\n" + baseline.Replace("  ", "      ").Replace("</Project>", "\n\n<!-- another -->\n</Project>")
      diffOf baseline reformatted |> Expect.isEmpty "nothing changed"

    testCase "a package added, bumped or removed is a package change that names the package" <| fun _ ->
      let withPackage version = projectText [ "A.fs"; "B.fs"; "C.fs" ] (sprintf "  <ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"%s\" /></ItemGroup>\n" version)
      diffOf baseline (withPackage "13.0.1") |> Expect.equal "added" [ Change.PackageChanged (project, [ "Newtonsoft.Json" ]) ]
      diffOf (withPackage "13.0.1") (withPackage "13.0.3") |> Expect.equal "bumped" [ Change.PackageChanged (project, [ "Newtonsoft.Json" ]) ]
      diffOf (withPackage "13.0.1") baseline |> Expect.equal "removed" [ Change.PackageChanged (project, [ "Newtonsoft.Json" ]) ]

    testCase "a project reference added is a reference change" <| fun _ ->
      diffOf baseline (projectText [ "A.fs"; "B.fs"; "C.fs" ] "  <ItemGroup><ProjectReference Include=\"..\\Core\\Core.fsproj\" /></ItemGroup>\n")
      |> Expect.equal "named by its path" [ Change.ReferenceChanged (project, [ "..\\Core\\Core.fsproj" ]) ]

    testCase "a property changed is a property change that names the property" <| fun _ ->
      diffOf baseline (baseline.Replace("net11.0", "net10.0")) |> Expect.equal "TargetFramework" [ Change.PropertyChanged (project, [ "TargetFramework" ]) ]

    testCase "a new property is a property change, and so is a changed Sdk" <| fun _ ->
      diffOf baseline (baseline.Replace("</PropertyGroup>", "  <LangVersion>preview</LangVersion>\n  </PropertyGroup>"))
      |> Expect.equal "LangVersion" [ Change.PropertyChanged (project, [ "LangVersion" ]) ]
      diffOf baseline (baseline.Replace("Microsoft.NET.Sdk", "Microsoft.NET.Sdk.Web"))
      |> Expect.equal "the Sdk" [ Change.ReferenceChanged (project, [ "Project/Sdk" ]) ]

    testCase "an embedded resource or a target is an item change" <| fun _ ->
      diffOf baseline (projectText [ "A.fs"; "B.fs"; "C.fs" ] "  <ItemGroup><EmbeddedResource Include=\"x.json\" /></ItemGroup>\n")
      |> Expect.equal "item" [ Change.OtherItemChanged project ]
      diffOf baseline (projectText [ "A.fs"; "B.fs"; "C.fs" ] "  <Target Name=\"Hello\" />\n")
      |> Expect.equal "target" [ Change.OtherItemChanged project ]

    testCase "WHY — a compile entry under a condition, with metadata or by glob is not on the compile list, so a change to it is an item change" <| fun _ ->
      let conditional = projectText [ "A.fs"; "B.fs"; "C.fs" ] "  <ItemGroup Condition=\"'$(OS)' == 'Windows_NT'\"><Compile Include=\"Win.fs\" /></ItemGroup>\n"
      diffOf baseline conditional |> namedKinds |> Expect.equal "an item, not the compile list" [ "item" ]
      let linked = projectText [ "A.fs"; "B.fs"; "C.fs" ] "  <ItemGroup><Compile Include=\"L.fs\"><Link>x/L.fs</Link></Compile></ItemGroup>\n"
      diffOf baseline linked |> namedKinds |> Expect.equal "an item" [ "item" ]
      let glob = projectText [ "A.fs"; "B.fs"; "C.fs" ] "  <ItemGroup><Compile Include=\"gen/*.fs\" /></ItemGroup>\n"
      diffOf baseline glob |> namedKinds |> Expect.equal "an item" [ "item" ]

    testCase "one edit that changes the compile list and a package is both changes" <| fun _ ->
      diffOf baseline (projectText [ "A.fs"; "B.fs"; "C.fs"; "D.fs" ] "  <ItemGroup><PackageReference Include=\"X\" Version=\"1\" /></ItemGroup>\n")
      |> namedKinds |> Expect.equal "both" [ "compile"; "package" ]

    testCase "text that is not XML, an empty file and a project that appears or vanishes" <| fun _ ->
      diffOf baseline "this is not a project" |> Expect.equal "unreadable" [ Change.Unreadable project ]
      diffOf "" baseline |> Expect.equal "an empty earlier text is unreadable" [ Change.Unreadable project ]
      diff project None (Some baseline) |> Expect.equal "created" [ Change.ProjectCreated project ]
      diff project (Some baseline) None |> Expect.equal "deleted" [ Change.ProjectDeleted project ]
      diff project None None |> Expect.isEmpty "neither side"
  ]

[<Tests>]
let ruleTests =
  testList "ProjectFileChange.select and forcesWholeSuite" [

    testCase "WHY — every kind of change but the compile list forces the whole suite" <| fun _ ->
      [ Change.ReferenceChanged (project, [ "x" ]); Change.PackageChanged (project, [ "x" ]); Change.PropertyChanged (project, [ "x" ])
        Change.OtherItemChanged project; Change.Unreadable project; Change.ProjectCreated project; Change.ProjectDeleted project
        Change.OtherBuildInput "Directory.Packages.props" ]
      |> List.iter (fun change -> forcesWholeSuite change |> Expect.isTrue (sprintf "%A forces it" change))
      forcesWholeSuite (Change.CompileList (project, [], [ "a.fs" ])) |> Expect.isFalse "the compile list does not"

    testCase "a landing that only adds a file selects that file and nothing else, when no file can see it" <| fun _ ->
      let changes = diffOf baseline (projectText [ "A.fs"; "B.fs"; "C.fs"; "D.fs" ] "")
      match select noText changes with
      | Selection.NarrowToFiles files -> files |> Expect.equal "only the appended file" [ "src/App/D.fs" ]
      | other -> failtestf "expected a narrow, got %A" other

    testCase "WHY — with no text to read, an insertion in the middle selects the files after it, the most the order allows" <| fun _ ->
      match select noText (diffOf baseline (projectText [ "A.fs"; "New.fs"; "B.fs"; "C.fs" ] "")) with
      | Selection.NarrowToFiles files -> files |> Expect.equal "the new file and the two after it" [ "src/App/New.fs"; "src/App/B.fs"; "src/App/C.fs" ]
      | other -> failtestf "expected a narrow, got %A" other

    testCase "WHY — with the texts, an inserted module nobody names selects only itself, wherever it sits" <| fun _ ->
      let texts =
        Map.ofList
          [ "src/App/A.fs", "module App.A\nlet a = 1\n"
            "src/App/New.fs", "module App.New\nlet n = 2\n"
            "src/App/B.fs", "module App.B\nlet b = A.a\n"
            "src/App/C.fs", "module App.C\nlet c = B.b\n" ]
      match select (fun file -> Map.tryFind file texts) (diffOf baseline (projectText [ "A.fs"; "New.fs"; "B.fs"; "C.fs" ] "")) with
      | Selection.NarrowToFiles files -> files |> Expect.equal "only the new file" [ "src/App/New.fs" ]
      | other -> failtestf "expected a narrow, got %A" other

    testCase "WHY — an inserted module that a later file names selects that file, and the file that names it in turn" <| fun _ ->
      let texts =
        Map.ofList
          [ "src/App/A.fs", "module App.A\nlet a = 1\n"
            "src/App/New.fs", "module App.New\nlet n = 2\n"
            "src/App/B.fs", "module App.B\nlet b = New.n\n"
            "src/App/C.fs", "module App.C\nlet c = B.b\n" ]
      match select (fun file -> Map.tryFind file texts) (diffOf baseline (projectText [ "A.fs"; "New.fs"; "B.fs"; "C.fs" ] "")) with
      | Selection.NarrowToFiles files -> files |> Expect.equal "the new file, what names it, and what names that" [ "src/App/New.fs"; "src/App/B.fs"; "src/App/C.fs" ]
      | other -> failtestf "expected a narrow, got %A" other

    testCase "WHY — any change besides the compile list sends the whole suite, and the selection names why" <| fun _ ->
      let changes = diffOf baseline (projectText [ "A.fs"; "B.fs"; "C.fs"; "D.fs" ] "  <ItemGroup><PackageReference Include=\"X\" Version=\"1\" /></ItemGroup>\n")
      match select noText changes with
      | Selection.WholeSuite because -> because |> namedKinds |> Expect.equal "the package, not the compile list" [ "package" ]
      | other -> failtestf "expected the whole suite, got %A" other

    testCase "a build input that is not a project file sends the whole suite" <| fun _ ->
      match select noText [ Change.OtherBuildInput "Directory.Packages.props" ] with
      | Selection.WholeSuite [ Change.OtherBuildInput "Directory.Packages.props" ] -> ()
      | other -> failtestf "expected the whole suite for a props file, got %A" other

    testCase "a project file that only changed its formatting selects no file, and the caller's floor decides" <| fun _ ->
      select noText [] |> Expect.equal "nothing to narrow to" (Selection.NarrowToFiles [])

    testCase "the selection says in words what it is" <| fun _ ->
      describeSelection (Selection.WholeSuite [ Change.PackageChanged (project, [ "X" ]) ]) |> Expect.stringContains "names the package" "a package of src/App/App.fsproj (X)"
      describeSelection (Selection.NarrowToFiles [ "a.fs"; "b.fs" ]) |> Expect.stringContains "names the files" "a.fs, b.fs"
  ]

[<Tests>]
let orderTests =
  testList "ProjectFileChange order bound" [
    testCase "commonPrefixLength and reshapedFiles at the boundaries" <| fun _ ->
      commonPrefixLength [] [] |> Expect.equal "empty" 0
      commonPrefixLength [ "a" ] [ "a"; "b" ] |> Expect.equal "an append" 1
      reshapedFiles [ "a"; "b" ] [ "a"; "b"; "c" ] |> Expect.equal "only the appended file" [ "c" ]
      reshapedFiles [ "a"; "b"; "c" ] [ "a"; "c" ] |> Expect.equal "the removal reshapes what is after it" [ "c" ]
      reshapedFiles [ "a"; "b"; "c" ] [ "a"; "c"; "b" ] |> Expect.equal "a move reshapes from the first difference" [ "c"; "b" ]
      reshapedFiles [ "a"; "b" ] [ "a"; "b" ] |> Expect.isEmpty "no change, nothing reshaped"
      reshapedFiles [ "a"; "b" ] [ "b" ] |> Expect.equal "the first file removed reshapes the rest" [ "b" ]
  ]

[<Tests>]
let propertyTests =
  let config = { FsCheckConfig.defaultConfig with maxTest = 300 }
  testList "ProjectFileChange properties" [

    testPropertyWithConfig config
      "any edit that changes something besides the compile list forces the whole suite, whatever it does to the compile list"
      <| fun (seed: int) ->
        let rnd = Random(seed)
        let files = [ for i in 0 .. rnd.Next(1, 8) -> sprintf "F%d.fs" i ]
        let edited = files |> List.filter (fun _ -> rnd.Next 3 > 0) |> List.rev
        let extra =
          match rnd.Next 4 with
          | 0 -> "  <ItemGroup><PackageReference Include=\"P\" Version=\"1\" /></ItemGroup>\n"
          | 1 -> "  <ItemGroup><ProjectReference Include=\"../X/X.fsproj\" /></ItemGroup>\n"
          | 2 -> "  <PropertyGroup><LangVersion>preview</LangVersion></PropertyGroup>\n"
          | _ -> "  <ItemGroup><EmbeddedResource Include=\"r.txt\" /></ItemGroup>\n"
        match select noText (diffOf (projectText files "") (projectText edited extra)) with
        | Selection.WholeSuite _ -> ()
        | other -> failtestf "seed %d: %A did not force the whole suite" seed other

    testPropertyWithConfig config
      "a compile-list-only edit never forces the whole suite, and never selects a file outside the new list"
      <| fun (seed: int) ->
        let rnd = Random(seed)
        let files = [ for i in 0 .. rnd.Next(1, 12) -> sprintf "F%d.fs" i ]
        let edited = files |> List.filter (fun _ -> rnd.Next 4 > 0) |> fun kept -> kept @ [ for k in 0 .. rnd.Next 3 -> sprintf "New%d.fs" k ]
        match select noText (diffOf (projectText files "") (projectText edited "")) with
        | Selection.WholeSuite because -> failtestf "seed %d: a compile-list-only edit forced the whole suite: %A" seed because
        | Selection.NarrowToFiles selected ->
          let inNew = edited |> List.map (fun file -> "src/App/" + file) |> Set.ofList
          selected |> List.forall inNew.Contains |> Expect.isTrue (sprintf "seed %d: selected %A outside the new list" seed selected)

    testPropertyWithConfig config
      "reformatting a project file (indentation, blank lines, comments) is never a change"
      <| fun (seed: int) ->
        let rnd = Random(seed)
        let files = [ for i in 0 .. rnd.Next(1, 10) -> sprintf "F%d.fs" i ]
        let text = projectText files "  <ItemGroup><PackageReference Include=\"P\" Version=\"1\" /></ItemGroup>\n"
        let indent = String(' ', rnd.Next(0, 9))
        let noisy =
          text.Replace("\n", sprintf "\n%s%s" (String('\n', rnd.Next(0, 3))) indent)
          |> fun t -> sprintf "<!-- %d -->\n%s" seed t
        diffOf text noisy |> Expect.isEmpty "no change"
  ]

// -- What a source file says about who can see it --

[<Tests>]
let scanTests =
  let facts (text: string) = factsOfText (Some text)
  let isOpaque (f: FileFacts) = f.Reach = Reach.Anything
  testList "ProjectFileChange.factsOfText" [

    testCase "WHY — a file that is one module is reached by that module's last name" <| fun _ ->
      let f = facts "module SageFs.Tests.RunStallTests\n\nopen System\nlet x = 1\n"
      f.Reach |> Expect.equal "named by its last segment" (Reach.ByName (Set.singleton "RunStallTests"))

    testCase "WHY — a namespace whose column-zero declarations are all modules is reached by their names" <| fun _ ->
      let f = facts "namespace SageFs.Features.LiveTesting\n\nopen System\n\n/// doc\nmodule RunStall =\n  let a = 1\n\nmodule Other =\n  let b = 2\n"
      f.Reach |> Expect.equal "both modules" (Reach.ByName (Set.ofList [ "RunStall"; "Other" ]))

    testCase "WHY — a namespace that declares a type is opaque: its union cases and record labels reach whoever opens it" <| fun _ ->
      isOpaque (facts "namespace A\n\ntype Color = Red | Blue\n") |> Expect.isTrue "a type at column zero"
      isOpaque (facts "namespace A\n\nexception Boom\n") |> Expect.isTrue "an exception"

    testCase "WHY — an AutoOpen module, an extension member, an intrinsic extension and an assembly attribute are opaque" <| fun _ ->
      isOpaque (facts "[<AutoOpen>]\nmodule A.Prelude\nlet x = 1\n") |> Expect.isTrue "AutoOpen"
      isOpaque (facts "module A.Ext\nopen System.Runtime.CompilerServices\n[<Extension>]\ntype E = class end\n") |> Expect.isTrue "Extension"
      isOpaque (facts "module A.Ext\ntype System.String with\n  member s.Twice() = s + s\n") |> Expect.isTrue "an intrinsic extension"
      isOpaque (facts "module A.Info\n[<assembly: System.Reflection.AssemblyTitle(\"x\")>]\ndo ()\n") |> Expect.isTrue "an assembly attribute"

    testCase "a file with no module or namespace header, and a file that could not be read, are opaque" <| fun _ ->
      isOpaque (facts "printfn \"hello\"\n") |> Expect.isTrue "a script-shaped file"
      isOpaque (factsOfText None) |> Expect.isTrue "unreadable"
      (factsOfText None).Mentions |> Expect.equal "and it names everything" Mentioned.Everything

    testCase "the identifiers a file mentions are read from its text" <| fun _ ->
      (facts "module A.B\nlet x = Foo.bar 1\n").Mentions
      |> Expect.equal "the identifiers" (Mentioned.Identifiers (Set.ofList [ "module"; "A"; "B"; "let"; "x"; "Foo"; "bar" ]))

    testCase "canSee: a file sees a module it names, and does not see one it does not, and sees everything opaque" <| fun _ ->
      let plain = { Reach = Reach.ByName (Set.singleton "Util"); Mentions = Mentioned.Identifiers Set.empty }
      let namesUtil = { Reach = Reach.ByName Set.empty; Mentions = Mentioned.Identifiers (Set.singleton "Util") }
      let namesNothing = { Reach = Reach.ByName Set.empty; Mentions = Mentioned.Identifiers Set.empty }
      let unreadable = { Reach = Reach.Anything; Mentions = Mentioned.Everything }
      canSee namesUtil plain |> Expect.isTrue "names it"
      canSee namesNothing plain |> Expect.isFalse "does not name it"
      canSee namesNothing { plain with Reach = Reach.Anything } |> Expect.isTrue "opaque is seen by everything"
      canSee unreadable plain |> Expect.isTrue "an unreadable file names everything"
  ]
