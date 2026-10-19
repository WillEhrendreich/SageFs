/// The domain-modelling doctrine, counted. SageFs models "maybe" as a union case that says why and a flag as a union,
/// not as `option` and `bool`, and a `match` over a union the repo owns lists every case, so adding a case breaks the
/// build where it is not handled. Nothing counted any of that, so the numbers rose (R-DOMAIN-7). This pins four
/// numbers per shipped project and they only go DOWN:
///
///  - option fields: a record field, or a named union case field, whose type is `T option`;
///  - bool fields: the same, for `bool`;
///  - wildcards over an owned union: a `| _ ->` arm whose nearest sibling arm names a union case that THIS repo defines
///    in a `type` block (so it catches a new case silently, and never a `| _ ->` after `Some`/`Ok`);
///  - `with _ ->`: a one-line catch-all handler (`with _ ->` or `with | _ ->`).
///
/// What this scans, and nothing more: the four shipped projects' `.fs` files (not `bin`/`obj`, not tests, not
/// SageFs.Simulation). It reads lines, so a field written across several lines is counted at the line that carries the
/// `:` and the type, text after `//` is dropped, and a `(* *)` block comment is not understood. The shape tests below
/// say exactly what each count sees. A key that is not in the table has a budget of zero, a budget above the real count
/// is a failure, and `--ratchets --tighten` rewrites the numbers.
module SageFs.Tests.DomainBudgetsTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

/// The shipped projects. A closed set: `Project.all` is the table's rows.
[<RequireQualifiedAccess>]
type Project =
  | Core
  | Daemon
  | Host
  | FsiHost

module Project =
  let all = [ Project.Core; Project.Daemon; Project.Host; Project.FsiHost ]

  let directory (project: Project) : string =
    match project with
    | Project.Core -> "SageFs.Core"
    | Project.Daemon -> "SageFs"
    | Project.Host -> "SageFs.Host"
    | Project.FsiHost -> "SageFs.FsiHost"

/// What is counted. A closed set: `Metric.all` is the table's columns.
[<RequireQualifiedAccess>]
type Metric =
  | OptionFields
  | BoolFields
  | WildcardOverOwnedUnion
  | WithWildcard

module Metric =
  let all = [ Metric.OptionFields; Metric.BoolFields; Metric.WildcardOverOwnedUnion; Metric.WithWildcard ]

  let name (metric: Metric) : string =
    match metric with
    | Metric.OptionFields -> "option fields"
    | Metric.BoolFields -> "bool fields"
    | Metric.WildcardOverOwnedUnion -> "wildcard over owned union"
    | Metric.WithWildcard -> "with wildcard"

/// The budget key of one cell of the table: the text the literal in `budgets` carries.
let keyOf (project: Project) (metric: Metric) : string =
  sprintf "%s: %s" (Project.directory project) (Metric.name metric)

/// Pure counting over source text. Nothing here touches the disk.
module Scan =
  let indentOf (line: string) : int = line.Length - line.TrimStart().Length

  let isBlank (line: string) : bool = line.Trim().Length = 0

  let isComment (line: string) : bool = line.TrimStart().StartsWith "//"

  /// Code lines: carriage returns gone, whole-line comments blanked, and a trailing `//` comment cut off.
  let codeLines (text: string) : string array =
    text.Split '\n'
    |> Array.map (fun raw ->
      let line = raw.TrimEnd '\r'
      match isComment line with
      | true -> ""
      | false ->
        match line.IndexOf " //" with
        | -1 -> line
        | cut -> line.Substring(0, cut))

  /// A line that declares a field: a record field (`Name: T`, optionally behind `{` or `mutable`), or a union case with
  /// a payload (`| Case of ...`).
  let fieldLine = Regex(@"^\s*(?:\{\s*)?(?:mutable\s+)?[A-Z]\w*\s*:|^\s*\|\s*[A-Z]\w*\s+of\s", RegexOptions.Compiled)

  /// One named type on a field line: `name: <anything up to the next field separator> <word>`, where the word is not the
  /// element of a function, a tuple, a list or an option of an option.
  let typedAs (word: string) =
    Regex(
      @"\b[A-Za-z_]\w*\s*:\s*(?:(?!->)[^:;*|=])*?\b" + word + @"\b(?!\s*(?:->|\*(?!\s*[A-Za-z_]\w*\s*:)|list\b|array\b|seq\b|option\b))",
      RegexOptions.Compiled)

  let optionType = typedAs "option"
  let boolType = typedAs "bool"

  let countFields (typePattern: Regex) (text: string) : int =
    codeLines text
    |> Array.filter fieldLine.IsMatch
    |> Array.sumBy (fun line -> typePattern.Matches(line).Count)

  let optionFields (text: string) : int = countFields optionType text
  let boolFields (text: string) : int = countFields boolType text

  let withWildcard = Regex(@"\bwith\s*(?:\|\s*)?_\s*->", RegexOptions.Compiled)

  let withWildcards (text: string) : int =
    codeLines text |> Array.sumBy (fun line -> withWildcard.Matches(line).Count)

  let typeStart = Regex(@"^\s*(?:type|and)\s", RegexOptions.Compiled)
  let unionCaseLine = Regex(@"^\s*\|\s*([A-Z]\w*)\s*(?:of\b.*)?$", RegexOptions.Compiled)
  let memberStart = Regex(@"^\s*(?:member|static|override|interface|abstract|default|new|with)\b", RegexOptions.Compiled)

  /// The names F# itself owns, which a repo type of the same name would only shadow.
  let notOwned = set [ "Some"; "None"; "Ok"; "Error" ]

  /// The union cases defined in the `type` blocks of `text`. A block ends at the first line that is not indented under
  /// its `type`, and stops yielding cases at its first member.
  let ownedCases (text: string) : Set<string> =
    let lines = codeLines text
    let found = System.Collections.Generic.HashSet<string>()
    let mutable blockIndent = -1
    let mutable members = false
    for line in lines do
      match isBlank line with
      | true -> ()
      | false ->
        let indent = indentOf line
        match typeStart.IsMatch line with
        | true ->
          blockIndent <- indent
          members <- false
        | false ->
          match blockIndent >= 0 && indent <= blockIndent with
          | true -> blockIndent <- -1
          | false ->
            match blockIndent >= 0 && not members with
            | false -> ()
            | true ->
              match memberStart.IsMatch line with
              | true -> members <- true
              | false ->
                match unionCaseLine.Match line with
                | m when m.Success -> found.Add(m.Groups[1].Value) |> ignore
                | _ -> ()
    Set.ofSeq found |> Set.filter (notOwned.Contains >> not)

  let wildcardArm = Regex(@"^\s*\|\s*_\s*->", RegexOptions.Compiled)
  let armCase = Regex(@"^\s*\|\s*\(?\s*(?:[A-Za-z_]\w*\.)*([A-Z]\w*)", RegexOptions.Compiled)

  /// The `| _ ->` arms whose nearest sibling arm (the closest earlier `|` line at the same indent, in the same match)
  /// names one of `owned`.
  let wildcardsOver (owned: Set<string>) (text: string) : int =
    let lines = codeLines text
    let mutable count = 0
    for i in 0 .. lines.Length - 1 do
      match wildcardArm.IsMatch lines[i] with
      | false -> ()
      | true ->
        let indent = indentOf lines[i]
        let mutable j = i - 1
        let mutable sibling = -1
        while j >= 0 && sibling < 0 do
          let line = lines[j]
          match isBlank line with
          | true -> j <- j - 1
          | false ->
            match indentOf line with
            | n when n < indent -> j <- -1
            | n when n = indent && line.TrimStart().StartsWith "|" -> sibling <- j
            | _ -> j <- j - 1
        match sibling >= 0 with
        | false -> ()
        | true ->
          match armCase.Match lines[sibling] with
          | m when m.Success && owned.Contains(m.Groups[1].Value) -> count <- count + 1
          | _ -> ()
    count

let repoRoot = RepoPaths.repoPathFull [||]

/// A project's source files: `.fs` under its directory, never build output.
let sourceFilesOf (project: Project) : string array =
  let dir = Path.Combine(repoRoot, Project.directory project)
  match Directory.Exists dir with
  | false -> [||]
  | true ->
    Directory.GetFiles(dir, "*.fs", SearchOption.AllDirectories)
    |> Array.filter (fun path ->
      let normal = path.Replace('\\', '/')
      not (normal.Contains "/obj/") && not (normal.Contains "/bin/"))

/// Every cell of the table, counted once: the union cases are defined across a whole project, so they are read first.
let counted : Lazy<Map<string, int>> =
  lazy
    (Project.all
     |> List.collect (fun project ->
       let texts = sourceFilesOf project |> Array.map File.ReadAllText
       let owned = texts |> Array.map Scan.ownedCases |> Set.unionMany
       let sum (count: string -> int) = texts |> Array.sumBy count
       [ keyOf project Metric.OptionFields, sum Scan.optionFields
         keyOf project Metric.BoolFields, sum Scan.boolFields
         keyOf project Metric.WildcardOverOwnedUnion, sum (Scan.wildcardsOver owned)
         keyOf project Metric.WithWildcard, sum Scan.withWildcards ])
     |> Map.ofList)

/// What each project may still carry. Ratchet down, never up. A key that is not here has a budget of zero.
let budgets : (string * int) list =
  [ "SageFs.Core: option fields", 158
    "SageFs.Core: bool fields", 55
    "SageFs.Core: wildcard over owned union", 254
    "SageFs.Core: with wildcard", 201
    "SageFs: option fields", 83
    "SageFs: bool fields", 22
    "SageFs: wildcard over owned union", 68
    "SageFs: with wildcard", 77
    "SageFs.Host: option fields", 0
    "SageFs.Host: bool fields", 0
    "SageFs.Host: wildcard over owned union", 2
    "SageFs.Host: with wildcard", 7
    "SageFs.FsiHost: option fields", 0
    "SageFs.FsiHost: bool fields", 1
    "SageFs.FsiHost: wildcard over owned union", 0
    "SageFs.FsiHost: with wildcard", 11 ]

/// The budget table as data, so `--ratchets --tighten` can lower it.
let budgetTable =
  TestInfrastructure.Ratchet.table
    { Name = "domain budgets"
      SourceFile = "SageFs.Tests/DomainBudgetsTests.fs"
      Budgets = budgets
      Actual = fun () -> counted.Value |> Map.toList }

let scanShapeTests =
  testList "domain budgets scan shapes" [

    testCase "a record field and a named union case field typed T option are counted, a parameter, a function result and a tuple element are not" <| fun _ ->
      let text =
        String.concat "\n"
          [ "type R ="
            "  { Name: string option"
            "    Count: int"
            "    Next: string -> int option"
            "    Pair: string * int option"
            "    Many: int option list }"
            "type U ="
            "  | A of reason: string option * n: int"
            "  | B of int option"
            "let f (x: int option) = x" ]
      Scan.optionFields text |> Expect.equal "Name and reason, and nothing else" 2

    testCase "a bool field is counted the same way, and text after // is not" <| fun _ ->
      let text =
        String.concat "\n"
          [ "type R ="
            "  { Enabled: bool"
            "    Flip: bool -> int"
            "    // Hidden: bool"
            "    Note: string // Trailing: bool"
            "    Both: int; Other: bool }"
            "type U ="
            "  | Case of failed: bool" ]
      Scan.boolFields text |> Expect.equal "Enabled, Other and failed" 3

    testCase "with _ -> counts the one-line catch-all in both spellings and not a handler that names its exception" <| fun _ ->
      let text =
        String.concat "\n"
          [ "try f () with _ -> 0"
            "try f () with | _ -> 0"
            "try f () with ex -> 1"
            "// try f () with _ -> 2" ]
      Scan.withWildcards text |> Expect.equal "two catch-alls" 2

    testCase "owned cases are the cases of a type block, and Some, None, Ok, Error and match arms in a member are not" <| fun _ ->
      let text =
        String.concat "\n"
          [ "type Shape ="
            "  | Circle of r: float"
            "  | Square"
            "  member s.Area ="
            "    match s with"
            "    | Hidden"
            "    | Other -> 1.0"
            "type Reply ="
            "  | Ok"
            "  | Pending"
            "let g x ="
            "  match x with"
            "  | Late"
            "  | Later -> 0" ]
      Scan.ownedCases text |> Expect.equal "Circle, Square and Pending" (set [ "Circle"; "Square"; "Pending" ])

    testCase "a wildcard arm is counted when its sibling arm names an owned case, and not after Some or Ok or another type's case" <| fun _ ->
      let text =
        String.concat "\n"
          [ "let a x ="
            "  match x with"
            "  | Circle r -> r"
            "  | _ -> 0.0"
            "let b x ="
            "  match x with"
            "  | Some y -> y"
            "  | _ -> 0"
            "let c x ="
            "  match x with"
            "  | Ok y -> y"
            "  | _ -> 0"
            "let d x ="
            "  match x with"
            "  | Shapes.Square ->"
            "    match y with"
            "    | Foreign -> 1"
            "    | _ -> 2"
            "  | _ -> 3" ]
      Scan.wildcardsOver (set [ "Circle"; "Square" ]) text
      |> Expect.equal "the outer arm of a and the arm of d that follows Square, not the one after Foreign" 2
  ]

let scanReachTests =
  testList "domain budgets scan reach" [

    testCase "every shipped project has source files, so a moved directory cannot turn its budgets into zeros" <| fun _ ->
      let empty = Project.all |> List.filter (fun project -> (sourceFilesOf project).Length = 0) |> List.map Project.directory
      empty |> Expect.isEmpty "every project directory is scanned"

    testCase "the table has exactly one row for every project and metric, and nothing else" <| fun _ ->
      let expected = [ for project in Project.all do for metric in Metric.all -> keyOf project metric ] |> List.sort
      budgets |> List.map fst |> List.sort |> Expect.equal "one budget per project and metric" expected

    testCase "the real tree has option and bool fields to count, so a broken pattern cannot read as a clean tree" <| fun _ ->
      let core metric = counted.Value[keyOf Project.Core metric]
      (core Metric.OptionFields, 0) |> Expect.isGreaterThan "SageFs.Core has option fields"
      (core Metric.BoolFields, 0) |> Expect.isGreaterThan "SageFs.Core has bool fields"
      (core Metric.WildcardOverOwnedUnion, 0) |> Expect.isGreaterThan "SageFs.Core has wildcard arms over its own unions"
      (core Metric.WithWildcard, 0) |> Expect.isGreaterThan "SageFs.Core has catch-all handlers"
  ]

[<Tests>]
let tests =
  testList "Domain budgets" [

    testCase "WHY: no project carries more option fields, bool fields, wildcards over its own unions or catch-all handlers than its budget, so the doctrine's debt only shrinks" <| fun _ ->
      let allowed = Map.ofList budgets
      let over =
        counted.Value
        |> Map.toList
        |> List.choose (fun (key, count) ->
          let budget = Map.tryFind key allowed |> Option.defaultValue 0
          match count > budget with
          | true -> Some (sprintf "%s has %d, budget %d" key count budget)
          | false -> None)
      over |> Expect.isEmpty "every count is within its budget (model the case as a union that says why, never raise a number)"

    testCase "WHY: a budget above the real count is stale, so room that was already won back cannot be spent again" <| fun _ ->
      let stale =
        budgets
        |> List.choose (fun (key, budget) ->
          let count = Map.tryFind key counted.Value |> Option.defaultValue 0
          match count < budget with
          | true -> Some (sprintf "%s has %d but its budget is %d: lower it with --ratchets --tighten" key count budget)
          | false -> None)
      stale |> Expect.isEmpty "every budget equals the current count"

    scanShapeTests
    scanReachTests
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.SizeProxy
