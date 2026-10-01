/// Some SageFs.Core files are compiled somewhere that has no other SageFs file: the VS Code
/// contract tests `#load` them as plain scripts, and the FSI host embeds some as resources. Such a
/// file cannot call `Timeouts` or `SageFs.Json`, which are product-only and compile first in Core.
/// The product compiled while the script that loads the file did not, and the release gate caught
/// it only at the VS Code stage: `WorkflowTypes.fs` had been pointed at `Timeouts`. This pins the
/// rule for every file a contract script loads, so the next one fails here, in the fast suite.
module SageFs.Tests.StandaloneSourceTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private loadPattern = Regex(@"#load\s+""(?<path>[^""]*SageFs\.Core/[^""]+\.fs)""", RegexOptions.Compiled)

/// Core files that a VS Code contract script loads on its own.
let private standaloneFiles : string list =
  let tests = Path.Combine(repoRoot, "sagefs-vscode", "tests")
  match Directory.Exists tests with
  | false -> []
  | true ->
    [ for script in Directory.EnumerateFiles(tests, "*.fsx") do
        for m in loadPattern.Matches(File.ReadAllText script) do
          let relative = m.Groups["path"].Value
          yield Path.GetFullPath(Path.Combine(Path.GetDirectoryName script, relative)) ]
    |> List.distinct

/// What a standalone file must not reach for: the product-only central modules.
let private productOnly = Regex(@"\bTimeouts\.|\bSageFs\.Timeouts\b|\bJson\.(serialize|deserialize|optionsOf)\b|\bSageFs\.Json\b", RegexOptions.Compiled)

[<Tests>]
let tests =
  testList "Standalone source files" [

    testCase "WHY — the scripts that load Core files exist and name at least one, so the guard below is checking something" <| fun _ ->
      standaloneFiles |> Expect.isNonEmpty "the VS Code contract scripts load Core files"

    testCase "WHY — a Core file that a script loads on its own never references Timeouts or SageFs.Json, because the script has no other SageFs file to resolve them" <| fun _ ->
      let offenders =
        standaloneFiles
        |> List.choose (fun file ->
          match productOnly.Match(File.ReadAllText file) with
          | m when m.Success -> Some (sprintf "%s uses %s" (Path.GetRelativePath(repoRoot, file)) m.Value)
          | _ -> None)
      offenders |> Expect.isEmpty "no loaded file depends on a product-only module (name the constant in the file itself)"
  ]
