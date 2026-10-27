/// Where a case sits: the category rules, read top to bottom. It lives apart from
/// `ExpectoCases` because the FSI host compiles the live-testing executors, and
/// `FsiHost.fsproj` takes no packages on purpose (nothing from SageFs may conflict
/// with the user's packages), so a file carrying the TreeSitter parse surface could
/// not join that list. `ExpectoCases` re-exports these functions so the plan's
/// `ExpectoCases.categoryOf` contract still holds on the daemon side.
namespace SageFs.Features.LiveTesting

/// Why a case sits in the category it did: the list it is in, the file it lives
/// in, what its body does, or nothing at all (Unit is the default).
[<RequireQualifiedAccess>]
type CategoryWhy =
  | FromListLabel of string
  | FromFilePath of string
  | FromBodyIo of string
  | FromDefault

/// Everything `categoryOf` may read: the enclosing list labels (outermost first,
/// the case's own name not among them), the file path, and the case's own text.
/// A discovery that saw no source leaves the last two empty, so those rules
/// simply do not fire.
type CategoryEvidence =
  { ListLabels: string list
    FilePath: string
    Body: string }

module LiveTestingCategories =

  // ---- step 6: categoryOf ----------------------------------------------------------------

  /// The category names a label or a path may carry, in the order `categoryOf`
  /// tries them: unit, integration, browser, e2e, property, architecture, benchmark.
  /// Two words map to one category on purpose: "e2e" runs with the browser tests.
  let categoryWords : (string * TestCategory) list =
    [ "unit", TestCategory.Unit
      "integration", TestCategory.Integration
      "browser", TestCategory.Browser
      "e2e", TestCategory.Browser
      "property", TestCategory.Property
      "architecture", TestCategory.Architecture
      "benchmark", TestCategory.Benchmark ]

  /// The words of a label or a file path: lowercased and split on anything that is
  /// not a letter or a digit, so a word is matched whole or not at all. "building"
  /// never supplies "ui", and the token "integrationtests" is not "integration".
  let categoryEvidenceTokens (text: string) : Set<string> =
    System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
    |> Array.toList
    |> Set.ofList

  /// What in a body makes a case an integration test: a network call, a database,
  /// or a child process. The longer names come first so an Npgsql body reports
  /// NpgsqlConnection instead of the shorter name it contains. File., Path. and
  /// System.IO are deliberately absent: a unit test that joins paths would leave
  /// the on-every-change set for doing ordinary file work, and the rule is to
  /// fail toward running.
  let bodyIoPatterns : string list =
    [ "HttpClient"
      "Socket"
      "NpgsqlConnection"
      "SqliteConnection"
      "SqlConnection"
      "Process.Start" ]

  /// Where a case sits: the nearest enclosing list first, then the file it lives
  /// in, then what its body does. Every step fails toward Unit, so a case only
  /// leaves the on-every-change set on evidence a person would want it to leave on.
  let categoryOf (evidence: CategoryEvidence) : TestCategory * CategoryWhy =
    let firstWord (tokens: Set<string>) =
      categoryWords
      |> List.tryPick (fun (word, category) -> if tokens.Contains word then Some (word, category) else None)
    let nearestLabel =
      evidence.ListLabels
      |> List.rev
      |> List.tryPick (fun label ->
        firstWord (categoryEvidenceTokens label)
        |> Option.map (fun (_, category) -> category, label))
    match nearestLabel with
    | Some (category, label) -> category, CategoryWhy.FromListLabel label
    | None ->
      match firstWord (categoryEvidenceTokens evidence.FilePath) with
      | Some (word, category) -> category, CategoryWhy.FromFilePath word
      | None ->
        match bodyIoPatterns |> List.tryFind (fun pattern -> evidence.Body.Contains pattern) with
        | Some pattern -> TestCategory.Integration, CategoryWhy.FromBodyIo pattern
        | None -> TestCategory.Unit, CategoryWhy.FromDefault
