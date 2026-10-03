// Check every roadmap item's landmark against the tree, and check the GENERATED page matches.
// I use the real SageFs.Roadmap code (the same code gen-roadmap.fsx and the test run), so there
// is no hand-rolled parsing of Capability.fs-style blocks here.
//
// Two questions:
//   1. Is any item printed as Built on the page but its landmark is NOT actually present?
//   2. Does re-running the generator reproduce the checked-in page byte for byte?
#load "roadmap/Roadmap.fs"
#load "roadmap/RoadmapItems.fs"

open System.IO
open SageFs.Roadmap

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let read (relative: string) : FileState =
  let path = Path.Combine(repoRoot, relative)
  match File.Exists path with
  | true -> Lines(File.ReadAllLines path)
  | false -> Missing

printfn "=== 1. items the page calls Built, checked against the tree ==="
let builtItems =
  SageFs.RoadmapItems.items
  |> List.filter (fun item ->
    match standing read item with
    | Built _ -> true
    | OpenAt _ -> false)

let mutable lies = 0
for item in builtItems do
  match item.Arrival with
  | Marked lm ->
    let ok = landmarkPresent read lm
    if not ok then
      lies <- lies + 1
      printfn "  LYING: '%s' is rendered Built but %s has no non-comment line containing '%s'"
        item.Title lm.Path lm.Symbol
  | NoLandmarkYet -> ()

printfn "items rendered Built: %d; of those with a MISSING landmark: %d" builtItems.Length lies

printfn ""
printfn "=== 2. every item with a landmark: is it present? ==="
let marked =
  SageFs.RoadmapItems.items
  |> List.filter (fun i -> match i.Arrival with Marked _ -> true | NoLandmarkYet -> false)
printfn "items naming a landmark: %d" marked.Length
for item in marked do
  match item.Arrival with
  | Marked lm ->
    printfn "  %-7s %s / %s   [%s]" (if landmarkPresent read lm then "PRESENT" else "absent") lm.Path lm.Symbol item.Title
  | NoLandmarkYet -> ()

printfn ""
printfn "=== 3. does the generator reproduce the checked-in page? ==="
let rendered = render read SageFs.RoadmapItems.items
let target = Path.Combine(repoRoot, "docs", "roadmap.md")
let onDisk = File.ReadAllText target
printfn "rendered %d chars; on disk %d chars; identical: %b" rendered.Length onDisk.Length (rendered = onDisk)
if rendered <> onDisk then
  File.WriteAllText(Path.Combine(__SOURCE_DIRECTORY__, "roadmap-rendered.md"), rendered)
  printfn "  (wrote the freshly rendered page to scratchpad/roadmap-rendered.md to diff)"

printfn ""
printfn "=== 4. the count line the page prints ==="
rendered.Split('\n')
|> Array.filter (fun l -> l.StartsWith "On the page today")
|> Array.iter (fun l -> printfn "%s" l)

if lies > 0 then exit 1
exit 0