namespace SageFs.Features

open System
open System.Xml.Linq

/// What a change to a project file does to the tests a landing has to run.
///
/// A landing that touches a project file used to be verified against whatever the coverage said about the OTHER files in the
/// diff, and when the project file stood alone the narrow selected nothing, so the floor ran the whole discovered suite. Coverage
/// names source documents, never a project file, so the project file said nothing either way. A landing that only added a source
/// file to a project ran 17000 tests, and one that bumped a package ran only the tests that covered some other file in the diff,
/// which cannot be right either.
///
/// The decision is made from what the project file diff says. Each project file's two texts become a closed set of changes, and
/// the rule for which of them force the whole suite is `forcesWholeSuite`:
///
///  - the compile list (`<Compile Include>` entries, in order) is the only thing F# source order is. A file's meaning depends on
///    the project's references, its properties, and the files compiled BEFORE it. So when only the compile list changed, the files
///    at and after the first difference are the ones whose meaning can have changed, and the files before it cannot have changed.
///    The tests that cover those files are the tests the edit can affect (`reshapedFiles`);
///  - a reference, a package, a property, any other item, an unreadable file, a project created or deleted, or any other build
///    input (props, targets, a lock file, the SDK pin) can change what every test runs against, so the whole suite runs.
///
/// What this does not see: a project that references the one that changed. Its code is affected only through what the changed
/// project now exposes, and an unmodified file there cannot name a file that did not exist. The build compiles it, and the release
/// gate runs the whole suite. A new type that shadows one the downstream code already opens is the case this leaves to those.
///
/// Pure: the caller reads the two texts of each project file and says what the other build inputs are.
module ProjectFileChange =

  /// One thing a landing changed in the build inputs. A project file's diff is a list of these.
  [<RequireQualifiedAccess>]
  type Change =
    /// The compile list differs: files added, removed or moved. The lists are repository-relative, in compile order.
    | CompileList of project: string * before: string list * after: string list
    /// A project, framework or other reference differs. `what` names each.
    | ReferenceChanged of project: string * what: string list
    /// A package reference or version differs. `what` names each package.
    | PackageChanged of project: string * what: string list
    /// A property differs (TargetFramework, DefineConstants, LangVersion, ...). `names` names each.
    | PropertyChanged of project: string * names: string list
    /// Any other item or element differs (a resource, a content file, a target).
    | OtherItemChanged of project: string
    /// One of the two texts is not XML, so nothing can be said about what changed.
    | Unreadable of project: string
    /// The landing adds a project file.
    | ProjectCreated of project: string
    /// The landing deletes a project file.
    | ProjectDeleted of project: string
    /// A build input that is not a project file (props, targets, a lock file, the SDK pin, a feed).
    | OtherBuildInput of path: string

  /// Which changes force the whole suite. Exhaustive on purpose: a new kind of change must say which side it is on.
  let forcesWholeSuite (change: Change) : bool =
    match change with
    | Change.CompileList _ -> false
    | Change.ReferenceChanged _
    | Change.PackageChanged _
    | Change.PropertyChanged _
    | Change.OtherItemChanged _
    | Change.Unreadable _
    | Change.ProjectCreated _
    | Change.ProjectDeleted _
    | Change.OtherBuildInput _ -> true

  /// The change in words, for the landing's report.
  let describe (change: Change) : string =
    let named (what: string list) = String.Join(", ", what)
    match change with
    | Change.CompileList (project, _, _) -> sprintf "the compile list of %s" project
    | Change.ReferenceChanged (project, what) -> sprintf "a reference of %s (%s)" project (named what)
    | Change.PackageChanged (project, what) -> sprintf "a package of %s (%s)" project (named what)
    | Change.PropertyChanged (project, names) -> sprintf "a property of %s (%s)" project (named names)
    | Change.OtherItemChanged project -> sprintf "an item of %s" project
    | Change.Unreadable project -> sprintf "%s, which could not be read as a project file" project
    | Change.ProjectCreated project -> sprintf "%s, which the landing adds" project
    | Change.ProjectDeleted project -> sprintf "%s, which the landing deletes" project
    | Change.OtherBuildInput path -> sprintf "the build input %s" path

  // ── Reading a project file ──

  [<RequireQualifiedAccess>]
  type Category =
    | Package
    | Reference
    | Property
    | Other

  /// One fact a project file states outside its compile list. Two project files state the same things when their sorted facts are
  /// equal; the facts only in one of them say what changed.
  type Fact = { Category: Category; Key: string; Detail: string }

  type Parsed = { Compiles: string list; Facts: Fact list }

  /// A project file as a commit holds it: its repository-relative path and its text.
  type ProjectFileText = { ProjectPath: string; Xml: string }

  /// A directory named from the repository root, `""` for the root itself.
  type RepoDirectory = RepoDirectory of string

  let directoryOf(path: string) : RepoDirectory =
    match path.LastIndexOf '/' with
    | -1 -> RepoDirectory ""
    | i -> RepoDirectory(path.Substring(0, i))

  let resolveFrom(RepoDirectory dir) (relative: string) : string =
    match dir with
    | "" -> CohortLandingPlan.normalize relative
    | _ -> CohortLandingPlan.normalize (dir + "/" + relative)

  let attribute(name: string) (element: XElement) : string option =
    match element.Attribute(XName.Get name) with
    | null -> None
    | attr -> Some attr.Value

  /// The conditions on the element's ancestors, outermost first: the same item under another condition is another fact.
  let conditionChain(element: XElement) : string =
    element.Ancestors()
    |> Seq.choose (attribute "Condition")
    |> Seq.rev
    |> String.concat " && "

  /// A `Compile Include` the compile list can speak for: one literal path, no other attribute, no content, no condition above it.
  let isPlainCompile(element: XElement) : bool =
    let attributes = element.Attributes() |> Seq.toList
    element.Name.LocalName = "Compile"
    && not element.HasElements
    && attributes.Length = 1
    && attributes.Head.Name.LocalName = "Include"
    && not (attributes.Head.Value.Contains '*')
    && String.IsNullOrWhiteSpace(conditionChain element)

  let categoryOfItem(name: string) : Category =
    match name with
    | "PackageReference"
    | "PackageVersion"
    | "GlobalPackageReference"
    | "PackageDownload" -> Category.Package
    | "ProjectReference"
    | "Reference"
    | "FrameworkReference"
    | "COMReference"
    | "Analyzer" -> Category.Reference
    | _ -> Category.Other

  let keyOfItem(element: XElement) : string =
    [ "Include"; "Update"; "Remove" ]
    |> List.tryPick (fun name -> attribute name element)
    |> Option.defaultValue element.Name.LocalName

  let outerXml(element: XElement) : string =
    element.ToString(SaveOptions.DisableFormatting)

  let factsOf(root: XElement) : Fact list =
    let rootFacts =
      root.Attributes()
      |> Seq.map (fun attr -> { Category = Category.Reference; Key = sprintf "Project/%s" attr.Name.LocalName; Detail = attr.Value })
      |> Seq.toList
    let childFacts (group: XElement) : Fact list =
      let chain = conditionChain group
      let groupCondition = attribute "Condition" group |> Option.defaultValue ""
      let under = String.concat " && " (List.filter (String.IsNullOrEmpty >> not) [ chain; groupCondition ])
      match group.Name.LocalName with
      | "PropertyGroup" ->
        group.Elements()
        |> Seq.map (fun property ->
          { Category = Category.Property
            Key = property.Name.LocalName
            Detail = sprintf "%s | %s | %s" under (attribute "Condition" property |> Option.defaultValue "") (property.Value.Trim()) })
        |> Seq.toList
      | "ItemGroup" ->
        group.Elements()
        |> Seq.filter (isPlainCompile >> not)
        |> Seq.map (fun item ->
          { Category = categoryOfItem item.Name.LocalName
            Key = keyOfItem item
            Detail = sprintf "%s | %s" under (outerXml item) })
        |> Seq.toList
      | _ -> [ { Category = Category.Other; Key = group.Name.LocalName; Detail = outerXml group } ]
    let topFacts =
      root.Elements()
      |> Seq.collect (fun child ->
        match child.Name.LocalName with
        | "Import" -> [ { Category = Category.Reference; Key = keyOfItem child; Detail = outerXml child } ]
        | _ -> childFacts child)
      |> Seq.toList
    rootFacts @ topFacts

  /// Read a project file's text: its compile list (repository-relative, in order) and everything else it states. `None` for text
  /// that is not XML or has no root.
  let parse({ ProjectPath = projectPath; Xml = xml }: ProjectFileText) : Parsed option =
    try
      let doc = XDocument.Parse xml
      match doc.Root with
      | null -> None
      | root ->
        doc.DescendantNodes() |> Seq.toList |> List.iter (function :? XComment as comment -> comment.Remove() | _ -> ())
        let dir = directoryOf (CohortLandingPlan.normalize projectPath)
        let compiles =
          root.Descendants()
          |> Seq.filter isPlainCompile
          |> Seq.map (fun element -> resolveFrom dir (element.Attribute(XName.Get "Include").Value))
          |> Seq.toList
        Some { Compiles = compiles; Facts = factsOf root }
    with :? System.Xml.XmlException -> None

  // ── The diff ──

  let onlyIn(these: Fact list) (others: Fact list) : Fact list =
    // A multiset difference: a fact present twice on one side and once on the other is one fact that changed.
    let remaining = Collections.Generic.List<Fact>(others)
    these
    |> List.filter (fun fact ->
      match remaining.IndexOf fact with
      | -1 -> true
      | i ->
        remaining.RemoveAt i
        false)

  let keysOf(category: Category) (facts: Fact list) : string list =
    facts |> List.filter (fun fact -> fact.Category = category) |> List.map (fun fact -> fact.Key) |> List.distinct |> List.sort

  /// What changed between the two texts of the project file at `project` (repository-relative). `None` for a side the landing does
  /// not have: the file is created or deleted. Two texts that state the same things (whitespace, comments, indentation) are no change.
  let diff (project: string) (before: string option) (after: string option) : Change list =
    match before, after with
    | None, None -> []
    | None, Some _ -> [ Change.ProjectCreated project ]
    | Some _, None -> [ Change.ProjectDeleted project ]
    | Some earlier, Some later ->
      match parse { ProjectPath = project; Xml = earlier }, parse { ProjectPath = project; Xml = later } with
      | Some was, Some now ->
        let removed = onlyIn was.Facts now.Facts
        let added = onlyIn now.Facts was.Facts
        let differing = removed @ added
        [ match was.Compiles = now.Compiles with
          | true -> ()
          | false -> yield Change.CompileList (project, was.Compiles, now.Compiles)
          match keysOf Category.Reference differing with
          | [] -> ()
          | what -> yield Change.ReferenceChanged (project, what)
          match keysOf Category.Package differing with
          | [] -> ()
          | what -> yield Change.PackageChanged (project, what)
          match keysOf Category.Property differing with
          | [] -> ()
          | names -> yield Change.PropertyChanged (project, names)
          match differing |> List.exists (fun fact -> fact.Category = Category.Other) with
          | false -> ()
          | true -> yield Change.OtherItemChanged project ]
      | _ -> [ Change.Unreadable project ]

  // ── What a source file can do to the files after it ──

  /// Who can be affected by a source file.
  [<RequireQualifiedAccess>]
  type Reach =
    /// It can change what a later file means without that file naming it (an AutoOpen module, an extension member, a namespace
    /// that declares types, whose union cases and record labels are visible to whoever opens the namespace), so every later
    /// file counts as affected.
    | Anything
    /// Only a later file that names one of these: a module is reached through its own name, whatever it holds.
    | ByName of names: Set<string>

  /// What a source file's text names.
  [<RequireQualifiedAccess>]
  type Mentioned =
    /// The text could not be read, so the file counts as naming everything.
    | Everything
    /// The identifiers the file's text contains.
    | Identifiers of Set<string>

  type FileFacts = { Reach: Reach; Mentions: Mentioned }

  let opaqueFacts : FileFacts = { Reach = Reach.Anything; Mentions = Mentioned.Everything }

  let identifier = Text.RegularExpressions.Regex(@"[A-Za-z_][A-Za-z0-9_']*", Text.RegularExpressions.RegexOptions.Compiled)
  let moduleHeader = Text.RegularExpressions.Regex(@"^module\s+(?:rec\s+|inline\s+|public\s+|internal\s+|private\s+|\[<[^>]*>\]\s*)*([A-Za-z_][\w.']*)\s*$", Text.RegularExpressions.RegexOptions.Multiline)
  let namespaceHeader = Text.RegularExpressions.Regex(@"^namespace\s+(?:rec\s+)?(?:global|[A-Za-z_][\w.']*)\s*$", Text.RegularExpressions.RegexOptions.Multiline)
  let nestedModule = Text.RegularExpressions.Regex(@"^(?:\[<[^>]*>\]\s*)*module\s+(?:rec\s+|inline\s+|public\s+|internal\s+|private\s+)*([A-Za-z_][\w']*)\s*=", Text.RegularExpressions.RegexOptions.Compiled)
  let intrinsicExtension = Text.RegularExpressions.Regex(@"\btype\s+[^\n=]*\bwith\b", Text.RegularExpressions.RegexOptions.Compiled)
  let opaqueMarkers = [ "AutoOpen"; "Extension"; "assembly:"; "EntryPoint" ]

  /// What a file's text says about who can see it. Conservative: anything the scan does not recognise is Opaque.
  let factsOfText (text: string option) : FileFacts =
    match text with
    | None -> opaqueFacts
    | Some source ->
      let mentioned = identifier.Matches source |> Seq.map (fun m -> m.Value) |> Set.ofSeq
      let lines = source.Replace("\r\n", "\n").Split('\n')
      let isOpaqueByMarker =
        opaqueMarkers |> List.exists (fun marker -> source.Contains marker) || intrinsicExtension.IsMatch source
      let atColumnZero = lines |> Array.filter (fun line -> line.Length > 0 && not (Char.IsWhiteSpace line.[0]))
      let moduleName =
        match atColumnZero |> Array.tryFind (fun line -> line.StartsWith "module ") with
        | Some line when moduleHeader.IsMatch line -> Some (moduleHeader.Match(line).Groups.[1].Value)
        | _ -> None
      let opaque = { opaqueFacts with Mentions = Mentioned.Identifiers mentioned }
      match isOpaqueByMarker, moduleName with
      | true, _ -> opaque
      | false, Some name ->
        // `module A.B.C` at column 0 is the whole file: it is reached by naming C.
        let last = name.Substring(name.LastIndexOf '.' + 1)
        { Reach = Reach.ByName (Set.singleton last); Mentions = Mentioned.Identifiers mentioned }
      | false, None ->
        match atColumnZero |> Array.exists (fun line -> namespaceHeader.IsMatch line) with
        | false -> opaque
        | true ->
          // A namespace whose column-zero declarations are all modules (and opens, attributes and comments): each module is
          // reached by its own name. A type, an exception or anything else at column zero leaks its contents to whoever
          // opens the namespace.
          let declarations =
            atColumnZero
            |> Array.filter (fun line ->
              not (line.StartsWith "namespace " || line.StartsWith "open " || line.StartsWith "//" || line.StartsWith "///"
                   || line.StartsWith "(*" || line.StartsWith "#" || line.StartsWith "[<" && not (line.Contains "module ")))
          let names = declarations |> Array.choose (fun line -> match nestedModule.Match line with m when m.Success -> Some m.Groups.[1].Value | _ -> None)
          match names.Length = declarations.Length with
          | true -> { Reach = Reach.ByName (Set.ofArray names); Mentions = Mentioned.Identifiers mentioned }
          | false -> opaque

  /// Can `file` be affected by a change to `other`, given that `other` came before it? Opaque files can reach anything after
  /// them; a plain module reaches only the files that name it. A file whose text could not be read names everything.
  let canSee (file: FileFacts) (other: FileFacts) : bool =
    match other.Reach with
    | Reach.Anything -> true
    | Reach.ByName names ->
      match file.Mentions with
      | Mentioned.Everything -> true
      | Mentioned.Identifiers mentioned -> names |> Set.exists (fun name -> Set.contains name mentioned)

  /// The files of the new compile list whose meaning a compile-list edit can have changed. A file's meaning depends on the
  /// project's references and properties (unchanged here), its own text (a changed file is in the landing's diff already), and
  /// the files compiled before it that it can see (`canSee`), in order. So a file is affected when the sequence of visible files
  /// before it differs between the two lists (it was added, or something it can see was added, removed or moved), or when it
  /// can see a file that is itself affected, since that file's signature may have changed. A file that cannot see anything that
  /// changed means what it meant, wherever it sits in the list.
  let affectedFiles (factsOf: string -> FileFacts) (before: string list) (after: string list) : string list =
    let beforeSet = Set.ofList before
    let visibleBefore (list: string list) (file: string) : string list =
      let facts = factsOf file
      list |> List.takeWhile (fun other -> other <> file) |> List.filter (fun other -> canSee facts (factsOf other))
    let directly =
      after
      |> List.filter (fun file -> not (Set.contains file beforeSet) || visibleBefore before file <> visibleBefore after file)
    let position = after |> List.mapi (fun i file -> file, i) |> Map.ofList
    let rec grow (affected: Set<string>) : Set<string> =
      let more =
        after
        |> List.filter (fun file ->
          not (Set.contains file affected)
          && affected |> Seq.exists (fun other -> position[other] < position[file] && canSee (factsOf file) (factsOf other)))
      match more with
      | [] -> affected
      | _ -> grow (Set.union affected (Set.ofList more))
    let all = grow (Set.ofList directly)
    after |> List.filter (fun file -> Set.contains file all)

  /// How many files `before` and `after` start with in common.
  let commonPrefixLength (before: string list) (after: string list) : int =
    let shared = min before.Length after.Length
    List.zip (List.truncate shared before) (List.truncate shared after)
    |> List.takeWhile (fun (a, b) -> a = b)
    |> List.length

  /// The most a compile-list edit can reach, from the order alone: the files of the new list from the first difference on. A
  /// file before the first difference has the same files compiled before it as it had. `affectedFiles` narrows this by what each
  /// file can see, and never selects a file outside it.
  let reshapedFiles (before: string list) (after: string list) : string list =
    after |> List.skip (commonPrefixLength before after)

  // ── The rule ──

  [<RequireQualifiedAccess>]
  type Selection =
    /// Something other than the compile list changed: every test can be affected.
    | WholeSuite of because: Change list
    /// Only compile lists changed. The tests that cover these files (repository-relative) are the ones the edit can affect. The
    /// caller adds the tests the landing introduced, and keeps its floor: a narrow that selects nothing runs the whole suite.
    | NarrowToFiles of files: string list

  /// The selection for everything a landing changed in the build inputs. `readText` reads a source file of the new tree by
  /// its repository-relative path; a file it cannot read counts as one that can reach everything after it.
  let select (readText: string -> string option) (changes: Change list) : Selection =
    match changes |> List.filter forcesWholeSuite with
    | [] ->
      let facts = Collections.Generic.Dictionary<string, FileFacts>()
      let factsOf (file: string) : FileFacts =
        match facts.TryGetValue file with
        | true, known -> known
        | false, _ ->
          let computed = factsOfText (readText file)
          facts[file] <- computed
          computed
      Selection.NarrowToFiles (
        changes
        |> List.collect (fun change ->
          match change with
          | Change.CompileList (_, before, after) -> affectedFiles factsOf before after
          | Change.ReferenceChanged _
          | Change.PackageChanged _
          | Change.PropertyChanged _
          | Change.OtherItemChanged _
          | Change.Unreadable _
          | Change.ProjectCreated _
          | Change.ProjectDeleted _
          | Change.OtherBuildInput _ -> [])
        |> List.distinct)
    | forcing -> Selection.WholeSuite forcing

  /// The selection in words, for the log line and the landing's report.
  let describeSelection (selection: Selection) : string =
    match selection with
    | Selection.WholeSuite because ->
      sprintf "the whole suite, because of %s" (because |> List.map describe |> String.concat "; ")
    | Selection.NarrowToFiles [] -> "the tests the other changes affect, and the tests the landing adds: no existing file can see what the compile list changed"
    | Selection.NarrowToFiles files -> sprintf "the tests that cover %s" (String.concat ", " files)
