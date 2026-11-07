// Regenerates the part of docs/client-setup.md between the GENERATED markers from SageFs.Core/ClientSetup.fs.
//
//   dotnet fsi scripts/gen-client-setup-doc.fsx
//
// ClientSetup.fs is a pure file with no project dependencies, so this script loads it directly and `sagefs setup`
// prints the same text. A stale-page test fails when the page and the generator differ. The script asserts that the
// markers matched and says what it wrote.

#load "../SageFs.Core/ClientSetup.fs"

open System.IO
open SageFs

let docPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "docs", "client-setup.md") |> Path.GetFullPath
let before = File.ReadAllText docPath

match ClientSetup.replaceGenerated before ClientSetup.page with
| Error reason -> failwith (ClientSetup.describeMarkerRefusal reason)
| Ok after ->
  match after = before with
  | true -> printfn "docs/client-setup.md is already what the generator prints (%d characters)." before.Length
  | false ->
    File.WriteAllText(docPath, after)
    printfn "docs/client-setup.md rewritten: %d -> %d characters." before.Length after.Length
