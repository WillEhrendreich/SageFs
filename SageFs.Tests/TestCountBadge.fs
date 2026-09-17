/// Keeps the README test-count badge and the property-test count honest.
///
/// The numbers are derived from a live source, never hand-typed: the total is
/// the count of runnable test cases Expecto discovers in this assembly, and the
/// property count is every property-test declaration in the test source. A
/// freshness test (ReadmeBadgeTests) fails when the README drifts from these,
/// and `dotnet run --project SageFs.Tests -- --update-badge` restamps it.
///
/// Every function here was dogfooded in the SageFs REPL before it landed:
/// countLeaves against a known 7-leaf tree, the stamp/parse regexes against the
/// real README lines, and propertyTestCount against the source (the REPL caught
/// that a naive substring count conflated `ptestProperty` with `testProperty`).
module SageFs.Tests.TestCountBadge

open System.IO
open System.Text.RegularExpressions
open Expecto

/// How many runnable test cases (leaves) an Expecto test tree holds. A property
/// test is one leaf (it runs many generated cases internally), matching how the
/// runner reports "N tests".
let rec countLeaves (t: Test) : int =
  match t with
  | TestCase _ -> 1
  | TestList (ts, _) -> ts |> List.sumBy countLeaves
  | TestLabel (_, inner, _) -> countLeaves inner
  | Test.Sequenced (_, inner) -> countLeaves inner

/// Every runnable test case across all [<Tests>] in this assembly — the default
/// suite plus the integration-registered suites, counted the way Expecto
/// discovers them.
let totalTestCount () : int =
  match Impl.testFromThisAssembly () with
  | Some t -> countLeaves t
  | None -> 0

/// The test source directory (compile-time constant, same trick the snapshot
/// tests use to find their fixtures at runtime).
let testsDir = __SOURCE_DIRECTORY__

/// The README at the repository root.
let readmePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Readme.md"))

let private sourceFiles () =
  Directory.EnumerateFiles(testsDir, "*.fs", SearchOption.AllDirectories)
  |> Seq.filter (fun p ->
    let sep = Path.DirectorySeparatorChar
    not (p.Contains(sprintf "%cbin%c" sep sep)) && not (p.Contains(sprintf "%cobj%c" sep sep)))

/// Count every property-test declaration in the test source: `testProperty`,
/// its pending (`ptestProperty`) and focused (`ftestProperty`) forms, and the
/// `WithConfig` variants. Word-bounded so `ptestProperty` is not miscounted as
/// `testProperty`.
let propertyTestCount () : int =
  sourceFiles ()
  |> Seq.sumBy (fun p -> Regex.Matches(File.ReadAllText p, @"\b[pf]?testProperty(WithConfig)?\b").Count)

/// Replace the shields "tests-<message>-<color>" badge message with "<n>+".
let stampBadge (n: int) (text: string) =
  Regex.Replace(text, @"badge/tests-[^-]+-", sprintf "badge/tests-%d+-" n)

/// Replace the "<n> property-based tests" prose count.
let stampProperty (n: int) (text: string) =
  Regex.Replace(text, @"\d[\d,]*(?= property-based tests)", string n)

/// The current number in the README's tests badge, if present.
let parseBadge (text: string) : int option =
  let m = Regex.Match(text, @"badge/tests-(\d[\d,]*)\+?-")
  match m.Success with
  | true -> Some(int (m.Groups.[1].Value.Replace(",", "")))
  | false -> None

/// The current "<n> property-based tests" number in the README, if present.
let parseProperty (text: string) : int option =
  let m = Regex.Match(text, @"(\d[\d,]*)(?= property-based tests)")
  match m.Success with
  | true -> Some(int (m.Groups.[1].Value.Replace(",", "")))
  | false -> None

/// Restamp the README badge and prose from the live counts. Writes only when
/// something changed. Returns the counts and whether the file was rewritten.
let updateReadme () =
  let total = totalTestCount ()
  let properties = propertyTestCount ()
  let text = File.ReadAllText readmePath
  let updated = text |> stampBadge total |> stampProperty properties
  match updated <> text with
  | true -> File.WriteAllText(readmePath, updated)
  | false -> ()
  {| Total = total; Properties = properties; Changed = updated <> text |}
