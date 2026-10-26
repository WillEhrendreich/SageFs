/// The domain-modelling doctrine, counted. SageFs models "maybe" as a union case that says why and a flag as a union,
/// not as `option` and `bool`, and a `match` over a union the repo owns lists every case, so adding a case breaks the
/// build where it is not handled. Nothing counted any of that, so the numbers rose (R-DOMAIN-7). This pins five
/// numbers per shipped project and they only go DOWN:
///
///  - option fields: a record field, or a named union case field, whose type is `T option`;
///  - bool fields: the same, for `bool`;
///  - wildcards over an owned union: a `| _ ->` arm whose nearest sibling arm names a union case that THIS repo defines
///    in a `type` block (so it catches a new case silently, and never a `| _ ->` after `Some`/`Ok`);
///  - `with _ ->`: a one-line catch-all handler (`with _ ->` or `with | _ ->`);
///  - named catch-all: the same handler with the exception bound to a name. `with ex ->`, `with | e ->`, and the last arm
///    of several typed arms (`| :? IOException -> ... | ex -> ...`) catch everything exactly as `with _ ->` does, and
///    the `with _ ->` count could not see them. A guarded arm (`ex when ... ->`) and a typed arm (`:? A as ex ->`) are
///    not catch-alls and are not counted. A `with _ ->` is never counted twice: it belongs to the metric above.
///
/// What this scans, and nothing more: the four shipped projects' `.fs` files (not `bin`/`obj`, not tests, not
/// SageFs.Simulation). It reads lines, so a field written across several lines is counted at the line that carries the
/// `:` and the type, text after `//` is dropped, and the first four counts do not understand a `(* *)` block comment or a
/// string that spans lines (the named catch-all count does, see below). The shape tests below
/// say exactly what each count sees. A key that is not in the table has a budget of zero, a budget above the real count
/// is a failure, and `--ratchets --tighten` rewrites the numbers.
///
/// The named catch-all count reads the text through `Scan.codeOnly` first (comments gone, every string emptied, a string
/// that spans lines collapsed onto its first line). Telling `try ... with ex ->` from `match x with n ->` from line text
/// alone is a heuristic: the innermost `try` or `match` opened on the same line, and not closed by a bracket, owns the
/// `with`; a `with` that starts its line is owned by the nearest earlier line that is not indented deeper. It is
/// imperfect in two directions that the shape tests pin: a `with` that is neither first on its line nor preceded on that
/// line by its `try` or `match` is not seen, and a `try` whose `with` is misaligned with it is not seen. A multi-line
/// handler whose last arm is `| _ ->` is seen by neither wildcard metric.
module SageFs.Tests.DomainBudgetsTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp

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
  | NamedCatchAll

module Metric =
  let all =
    [ Metric.OptionFields
      Metric.BoolFields
      Metric.WildcardOverOwnedUnion
      Metric.WithWildcard
      Metric.NamedCatchAll ]

  let name (metric: Metric) : string =
    match metric with
    | Metric.OptionFields -> "option fields"
    | Metric.BoolFields -> "bool fields"
    | Metric.WildcardOverOwnedUnion -> "wildcard over owned union"
    | Metric.WithWildcard -> "with wildcard"
    | Metric.NamedCatchAll -> "named catch-all"

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

  /// What a `with` closes.
  [<RequireQualifiedAccess>]
  type WithOwner =
    | Try
    | Match
    | Neither

  /// What is still open while the tokens of one line are read left to right.
  [<RequireQualifiedAccess>]
  type Opener =
    | Try
    | Match
    | Bracket

  /// Where `codeOnly` stood when the text ended. Anything but `Code` means a quote never closed, so the counts that
  /// follow it cannot be trusted.
  [<RequireQualifiedAccess>]
  type Lexing =
    | Code
    | Regular
    | Verbatim
    | Triple

  /// The text with comments gone and every string literal emptied to `""`, split into lines. A string or block comment
  /// that spans lines collapses onto the line it began on, so a SQL block at column 0 cannot pose as code. Handles `//`,
  /// `(* *)` (not nested), `"..."`, `@"..."`, `"""..."""` and the two char literals that hold a quote.
  let codeOnly (text: string) : string array * Lexing =
    let out = System.Text.StringBuilder(text.Length)
    let n = text.Length
    let at (k: int) : char = match k < n with | true -> text[k] | false -> '\000'
    let mutable state = Lexing.Code
    let mutable i = 0
    while i < n do
      let c = text[i]
      match state with
      | Lexing.Code ->
        match c with
        | '/' when at (i + 1) = '/' ->
          while i < n && text[i] <> '\n' do
            i <- i + 1
        | '(' when at (i + 1) = '*' && at (i + 2) <> ')' ->
          i <- i + 2
          while i < n && not (text[i] = '*' && at (i + 1) = ')') do
            i <- i + 1
          i <- min n (i + 2)
        | '\'' when at (i + 1) = '"' && at (i + 2) = '\'' ->
          out.Append "' '" |> ignore
          i <- i + 3
        | '\'' when at (i + 1) = '\\' && at (i + 2) = '"' && at (i + 3) = '\'' ->
          out.Append "' '" |> ignore
          i <- i + 4
        | '"' when at (i + 1) = '"' && at (i + 2) = '"' ->
          out.Append '"' |> ignore
          state <- Lexing.Triple
          i <- i + 3
        | '@' when at (i + 1) = '"' ->
          out.Append '"' |> ignore
          state <- Lexing.Verbatim
          i <- i + 2
        | '"' ->
          out.Append '"' |> ignore
          state <- Lexing.Regular
          i <- i + 1
        | _ ->
          out.Append c |> ignore
          i <- i + 1
      | Lexing.Regular ->
        match c with
        | '\\' -> i <- i + 2
        | '"' ->
          out.Append '"' |> ignore
          state <- Lexing.Code
          i <- i + 1
        | _ -> i <- i + 1
      | Lexing.Verbatim ->
        match c with
        | '"' when at (i + 1) = '"' -> i <- i + 2
        | '"' ->
          out.Append '"' |> ignore
          state <- Lexing.Code
          i <- i + 1
        | _ -> i <- i + 1
      | Lexing.Triple ->
        match c with
        | '"' when at (i + 1) = '"' && at (i + 2) = '"' ->
          out.Append '"' |> ignore
          state <- Lexing.Code
          i <- i + 3
        | _ -> i <- i + 1
    out.ToString().Split '\n' |> Array.map (fun line -> line.TrimEnd '\r'), state

  /// The words that open or close a `try` or `match`, and the brackets that fence them. A name such as `with'` is one
  /// identifier, not the keyword.
  let keywordPattern = Regex(@"(?<![\w'])(?:try|match|with)(?![\w'])|[(\[{]|[)\]}]", RegexOptions.Compiled)

  let rec closeBracket (opened: Opener list) : Opener list =
    match opened with
    | [] -> []
    | Opener.Bracket :: rest -> rest
    | Opener.Try :: rest -> closeBracket rest
    | Opener.Match :: rest -> closeBracket rest

  let ownerOf (opened: Opener list) : WithOwner =
    match opened with
    | Opener.Try :: _ -> WithOwner.Try
    | Opener.Match :: _ -> WithOwner.Match
    | Opener.Bracket :: _ -> WithOwner.Neither
    | [] -> WithOwner.Neither

  /// Every `with` on a line, left to right, as (start index, owner), and what is still open at the end of the line. The
  /// owner is the innermost `try` or `match` opened on this line and not yet closed by a `with` or a bracket.
  let withsOf (line: string) : (int * WithOwner) list * Opener list =
    let mutable stack : Opener list = []
    let found = System.Collections.Generic.List<int * WithOwner>()
    for m in keywordPattern.Matches line do
      match m.Value with
      | "try" -> stack <- Opener.Try :: stack
      | "match" -> stack <- Opener.Match :: stack
      | "with" ->
        found.Add((m.Index, ownerOf stack))
        match stack with
        | Opener.Try :: rest -> stack <- rest
        | Opener.Match :: rest -> stack <- rest
        | Opener.Bracket :: _ -> ()
        | [] -> ()
      | "(" | "[" | "{" -> stack <- Opener.Bracket :: stack
      | _ -> stack <- closeBracket stack
    List.ofSeq found, stack

  /// The owner of a `with` that is the first thing on its line: the nearest earlier line that is not indented deeper
  /// decides, by what it left open.
  let ownerAbove (lines: string array) (index: int) : WithOwner =
    let indent = indentOf lines[index]
    let mutable j = index - 1
    let mutable found = -1
    while j >= 0 && found < 0 do
      match isBlank lines[j] || indentOf lines[j] > indent with
      | true -> j <- j - 1
      | false -> found <- j
    match found with
    | -1 -> WithOwner.Neither
    | at -> ownerOf (snd (withsOf lines[at]))

  /// A name a handler binds the exception to: lowercase, or `_` followed by more. A bare `_` is the other metric's.
  let binding = @"(?:_[\w']+|[a-z][\w']*)"
  let firstArm = Regex(@"^\s*(?:\|\s*)?(" + binding + @")\s*->", RegexOptions.Compiled)
  let laterArm = Regex(@"(?<!\|)\|(?![|>])\s*(" + binding + @")\s*->", RegexOptions.Compiled)
  let armLine = Regex(@"^\s*\|(?![|>])", RegexOptions.Compiled)
  let nestedConstruct = Regex(@"(?<![\w'])(?:match|function|fun|try)(?![\w'])", RegexOptions.Compiled)

  /// The names that look like a binding and are not one.
  let literalNames = set [ "true"; "false"; "null" ]

  let isBinding (m: Match) : bool =
    m.Success && not (literalNames.Contains m.Groups[1].Value)

  /// The arms of one handler that bind the exception to a name and so catch everything: the arm that follows its `with`
  /// on the same line, later arms on that line, and the `|` arms on the lines under it.
  let bareArmsOfHandler (lines: string array) (index: int) (start: int) : int =
    let line = lines[index]
    let rest = line.Substring(start + 4)
    let withIndent = indentOf line
    let restEmpty = rest.Trim().Length = 0
    let inlineFirst = match isBinding (firstArm.Match rest) with | true -> 1 | false -> 0
    let inlineLater =
      match nestedConstruct.IsMatch rest with
      | true -> 0
      | false ->
        laterArm.Matches rest
        |> Seq.filter (fun m -> rest.Substring(0, m.Index).Trim().Length > 0 && isBinding m)
        |> Seq.length
    let continuation =
      match restEmpty || start = withIndent with
      | false -> 0
      | true ->
        let mutable armIndent = match restEmpty with | true -> -1 | false -> withIndent
        let mutable count = 0
        let mutable j = index + 1
        while j < lines.Length do
          let next = lines[j]
          match isBlank next with
          | true -> j <- j + 1
          | false ->
            let indent = indentOf next
            match armIndent with
            | -1 ->
              match armLine.IsMatch next && indent >= withIndent with
              | true -> armIndent <- indent
              | false -> j <- lines.Length
            | arms when indent > arms -> j <- j + 1
            | arms when indent = arms ->
              match armLine.IsMatch next with
              | true ->
                (match isBinding (firstArm.Match next) with
                 | true -> count <- count + 1
                 | false -> ())
                j <- j + 1
              | false -> j <- lines.Length
            | _ -> j <- lines.Length
        count
    inlineFirst + inlineLater + continuation

  /// The handlers that catch everything under a name: `with ex ->`, `with | ex ->`, and a trailing `| ex ->` arm after
  /// typed arms. A `with _ ->` is counted by `withWildcards`, never here.
  let namedCatchAllsIn (lines: string array) : int =
    let mutable total = 0
    for i in 0 .. lines.Length - 1 do
      let withs, _ = withsOf lines[i]
      for (start, owner) in withs do
        let resolved =
          match owner, start = indentOf lines[i] with
          | WithOwner.Neither, true -> ownerAbove lines i
          | kept, _ -> kept
        match resolved with
        | WithOwner.Try -> total <- total + bareArmsOfHandler lines i start
        | WithOwner.Match -> ()
        | WithOwner.Neither -> ()
    total

  let namedCatchAlls (text: string) : int =
    codeOnly text |> fst |> namedCatchAllsIn

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
         keyOf project Metric.WithWildcard, sum Scan.withWildcards
         keyOf project Metric.NamedCatchAll, sum Scan.namedCatchAlls ])
     |> Map.ofList)

/// What each project may still carry. Ratchet down, never up. A key that is not here has a budget of zero.
let budgets : (string * int) list =
  [ "SageFs.Core: option fields", 158
    "SageFs.Core: bool fields", 55
    "SageFs.Core: wildcard over owned union", 253
    "SageFs.Core: with wildcard", 201
    "SageFs.Core: named catch-all", 201
    "SageFs: option fields", 83
    "SageFs: bool fields", 22
    "SageFs: wildcard over owned union", 65
    "SageFs: with wildcard", 77
    "SageFs: named catch-all", 161
    "SageFs.Host: option fields", 0
    "SageFs.Host: bool fields", 0
    "SageFs.Host: wildcard over owned union", 2
    "SageFs.Host: with wildcard", 7
    "SageFs.Host: named catch-all", 13
    "SageFs.FsiHost: option fields", 0
    "SageFs.FsiHost: bool fields", 1
    "SageFs.FsiHost: wildcard over owned union", 0
    "SageFs.FsiHost: with wildcard", 10
    "SageFs.FsiHost: named catch-all", 15 ]

/// The budget table as data, so `--ratchets --tighten` can lower it.
let budgetTable =
  TestInfrastructure.Ratchet.table
    { Name = "domain budgets"
      SourceFile = "SageFs.Tests/DomainBudgetsTests.fs"
      Budgets = budgets
      Actual = fun () -> counted.Value |> Map.toList }

/// The named catch-all count of source lines joined the way a file is.
let namedCount (lines: string list) : int = Scan.namedCatchAlls (String.concat "\n" lines)

/// Every case is (what it shows, source lines, expected count); the failure message names the case.
let expectNamedCounts (why: string) (cases: (string * string list * int) list) : unit =
  for (label, lines, expected) in cases do
    namedCount lines |> Expect.equal (sprintf "%s: %s" why label) expected

/// Names a handler can bind its exception to. Leaves out the words F# reserves or this scanner treats on its own.
let bindingNames : Gen<string> =
  let reserved =
    set [ "true"; "false"; "null"; "when"; "match"; "try"; "with"; "fun"; "function"; "as"; "in"; "of"; "if"; "then"
          "else"; "do"; "let"; "and"; "or"; "to"; "for"; "new"; "use"; "val"; "end"; "not" ]
  gen {
    let! first = Gen.elements [ 'a' .. 'z' ]
    let! length = Gen.choose (0, 7)
    let! rest = Gen.listOfLength length (Gen.elements (List.concat [ [ 'a' .. 'z' ]; [ '0' .. '9' ]; [ '_'; '\'' ] ]))
    return System.String(Array.ofList (first :: rest)) }
  |> Gen.filter (fun name -> not (reserved.Contains name))

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

    testCase "WHY — a handler that binds the exception to a name catches everything, so each one-line spelling of it is counted" <| fun _ ->
      expectNamedCounts "one-line handler"
        [ "with ex", [ "let a = try f () with ex -> 1" ], 1
          "with e", [ "let a = try f () with e -> 1" ], 1
          "with exn", [ "let a = try f () with exn -> 1" ], 1
          "with err", [ "let a = try f () with err -> 1" ], 1
          "with bar then name", [ "let a = try f () with | ex -> 1" ], 1
          "underscore-prefixed name", [ "let a = try f () with _ex -> 1" ], 1
          "typed arm then a bare arm on the same line", [ "let a = try f () with :? IOException -> 0 | ex -> 1" ], 1
          "inside a computation expression", [ "async { try f () with ex -> () }" ], 1
          "a try in parentheses before a match", [ "match (try f () with ex -> 1) with | A -> 0 | B -> 1" ], 1
          "two handlers", [ "let a = try f () with ex -> 1"; "let b = try g () with e -> 2" ], 2 ]

    testCase "WHY — a handler written over several lines is counted at the arm that binds the exception, whichever arm it is" <| fun _ ->
      expectNamedCounts "multi-line handler"
        [ "with ex -> on its own line",
          [ "try"; "  f ()"; "with ex ->"; "  g ex" ], 1
          "typed arm, then a bare arm",
          [ "try"; "  f ()"; "with"; "| :? IOException -> 0"; "| ex -> 1" ], 1
          "typed arm after with, bare arm on the next line",
          [ "try f ()"; "with :? IOException -> 0"; "| ex -> 1" ], 1
          "indented under a let",
          [ "let r ="; "  try"; "    f ()"; "  with"; "  | :? A -> 0"; "  | e -> 1" ], 1
          "a string at column 0 inside the try does not hide it",
          [ "let f () ="; "  try"; "    cmd.Text <- \"SELECT 1;"; "SELECT 2;\""; "    run ()"; "  with ex -> 1" ], 1
          "a try on one line and its with on the next",
          [ "let r = try f ()"; "        with ex -> 1" ], 1 ]

    testCase "WHY — a typed handler is not a catch-all, so a typed arm, a guarded arm and an alias on a typed arm are not counted" <| fun _ ->
      expectNamedCounts "typed handler"
        [ "one typed arm", [ "let a = try f () with :? IOException -> 0" ], 0
          "typed arms only, over several lines", [ "try f ()"; "with"; "| :? A -> 1"; "| :? B -> 2" ], 0
          "typed arm with an alias", [ "let a = try f () with :? IOException as io -> io.Message" ], 0
          "guarded name", [ "let a = try f () with ex when ex.Message = \"x\" -> 1" ], 0
          "match nested in a typed arm", [ "try f ()"; "with"; "| :? A ->"; "  match z with"; "  | n -> 1"; "| :? B -> 2" ], 0
          "the match around a one-line typed handler",
          [ "match q with"; "| A -> try f () with :? B -> 1"; "| other -> 2" ], 0
          "a pipe-forward line under a with is not an arm",
          [ "let a ="; "  try f ()"; "  with :? A -> 0"; "  |> g" ], 0 ]

    testCase "WHY — a match over data is not an exception handler, so a bare name in its arm is not counted" <| fun _ ->
      expectNamedCounts "match over data"
        [ "name after with", [ "let a = match x with ex -> 1" ], 0
          "bar then name", [ "let a = match x with | ex -> 1" ], 0
          "name arm on its own line", [ "let g x ="; "  match x with"; "  | n -> n" ], 0
          "bool arms are not bindings", [ "let a = match x with true -> 1 | false -> 0" ], 0
          "a match in parentheses", [ "let a = (match x with n -> n)" ], 0
          "with on its own line closing a match", [ "let a ="; "  match"; "    x"; "  with"; "  | n -> n" ], 0
          "a record copy", [ "let a = { r with Name = n }" ], 0
          "an identifier ending in a prime is not the keyword", [ "let with' = 1"; "let a = match x with n' -> n'" ], 0
          "a match, then a try in parentheses: only the try's with is a handler",
          [ "match (try f () with ex -> 1) with | A -> 0 | B -> 1" ], 1 ]

    testCase "WHY — comments and string literals hold no handlers, so text inside them is not counted" <| fun _ ->
      expectNamedCounts "comment or string"
        [ "whole-line comment", [ "// try f () with ex -> 1" ], 0
          "trailing comment", [ "let a = 1 // try f () with ex -> 1" ], 0
          "block comment", [ "(* try f () with ex -> 1 *)"; "let a = 1" ], 0
          "regular string", [ "let s = \"try f () with ex -> 1\"" ], 0
          "verbatim string", [ "let s = @\"try f () with ex -> 1\"" ], 0
          "triple-quoted string", [ "let s = \"\"\"try f () with ex -> 1\"\"\"" ], 0
          "a quote char literal does not open a string", [ "let q = '\"'"; "let a = try f () with ex -> 1" ], 1 ]

    testCase "WHY — a with _ -> belongs to the wildcard metric alone, so the named count never repeats it" <| fun _ ->
      let text = String.concat "\n" [ "let a = try f () with _ -> 0"; "let b = try f () with | _ -> 0"; "let c = try f () with ex -> 0" ]
      Scan.withWildcards text |> Expect.equal "the wildcard metric sees the two wildcard handlers" 2
      Scan.namedCatchAlls text |> Expect.equal "the named metric sees only the handler that binds a name" 1

    testCase "WHY — the known blind spots stay visible, so a handler the scanner cannot place is a recorded gap and not a surprise" <| fun _ ->
      expectNamedCounts "known blind spot"
        [ "a with that is not first on its line, with its try on an earlier line",
          [ "let r = try"; "          f () with ex -> 1" ], 0
          "a with indented deeper than its try",
          [ "let r ="; "  try"; "    f ()"; "    with ex -> 1" ], 0
          "a multi-line handler whose last arm is a wildcard, which neither wildcard metric sees",
          [ "let r ="; "  try"; "    f ()"; "  with"; "  | :? A -> 0"; "  | _ -> 1" ], 0 ]
      let wildcardArm = String.concat "\n" [ "let r ="; "  try"; "    f ()"; "  with"; "  | :? A -> 0"; "  | _ -> 1" ]
      Scan.withWildcards wildcardArm |> Expect.equal "the multi-line wildcard arm is not seen by the wildcard metric either" 0

    testProperty "WHY — any lowercase name in a handler is a catch-all in every layout, and the wildcard metric never sees it" <| fun () ->
      Prop.forAll (Arb.fromGen bindingNames) (fun name ->
        [ sprintf "let a = try f () with %s -> 1" name
          sprintf "let a = try f () with | %s -> 1" name
          sprintf "try\n  f ()\nwith %s ->\n  1" name
          sprintf "try\n  f ()\nwith\n| :? A -> 0\n| %s -> 1" name ]
        |> List.forall (fun text -> Scan.namedCatchAlls text = 1 && Scan.withWildcards text = 0))

    testProperty "WHY — the same name after a match is never counted" <| fun () ->
      Prop.forAll (Arb.fromGen bindingNames) (fun name ->
        [ sprintf "let a = match x with %s -> 1" name
          sprintf "let a = match x with | %s -> 1" name
          sprintf "match x with\n| A -> 0\n| %s -> 1" name ]
        |> List.forall (fun text -> Scan.namedCatchAlls text = 0))

    testProperty "WHY — a wildcard handler beside a named one is counted once by each metric" <| fun () ->
      Prop.forAll (Arb.fromGen bindingNames) (fun name ->
        let text = sprintf "let a = try f () with _ -> 0\nlet b = try f () with %s -> 1" name
        Scan.withWildcards text = 1 && Scan.namedCatchAlls text = 1)

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
      (core Metric.NamedCatchAll, 0) |> Expect.isGreaterThan "SageFs.Core has handlers that bind the exception to a name"
      for project in Project.all do
        (counted.Value[keyOf project Metric.NamedCatchAll], 0)
        |> Expect.isGreaterThan (sprintf "%s has handlers that bind the exception to a name, so a pattern that stops matching cannot read as a clean tree" (Project.directory project))

    testCase "WHY — every shipped file reads to the end in code state, so an unclosed quote cannot silently blank the counts after it" <| fun _ ->
      let unclosed =
        Project.all
        |> List.collect (fun project -> sourceFilesOf project |> List.ofArray)
        |> List.filter (fun path -> snd (Scan.codeOnly (File.ReadAllText path)) <> Scan.Lexing.Code)
        |> List.map (fun path -> Path.GetRelativePath(repoRoot, path))
      unclosed |> Expect.isEmpty (sprintf "every file ends outside a string. Unclosed: %s" (String.concat "; " unclosed))
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
      over
      |> Expect.isEmpty (sprintf "every count is within its budget (model the case as a union that says why, never raise a number). Over: %s" (String.concat "; " over))

    testCase "WHY: a budget above the real count is stale, so room that was already won back cannot be spent again" <| fun _ ->
      let stale =
        budgets
        |> List.choose (fun (key, budget) ->
          let count = Map.tryFind key counted.Value |> Option.defaultValue 0
          match count < budget with
          | true -> Some (sprintf "%s has %d but its budget is %d: lower it with --ratchets --tighten" key count budget)
          | false -> None)
      stale
      |> Expect.isEmpty (sprintf "every budget equals the current count. Stale: %s" (String.concat "; " stale))

    scanShapeTests
    scanReachTests
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.SizeProxy
