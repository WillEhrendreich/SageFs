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

// Locate the repo at RUNTIME, walking up from this script's own location until we
// find the solution file. A build-time constant would bake in the directory the
// script was COMPILED, which is not where it RUNS.
let repoRoot =
  let rec walk (dir: string) (depth: int) : string =
    if depth > 24 then "" else
    let full =
      try
        let f = Path.GetFullPath dir
        let r = Path.GetPathRoot f
        if f = r then f else Path.TrimEndingDirectorySeparator f
      with _ -> dir
    if File.Exists(Path.Combine(full, "SageFs.slnx")) then full
    else
      let parent = Path.GetDirectoryName full
      if System.String.IsNullOrEmpty parent || parent = full then ""
      else walk parent (depth + 1)
  // Start from the directory this script lives in. A build-time constant only names where it was
  // built, so the walk up to SageFs.slnx is what locates the repo where the code actually runs.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let repo =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else repoRoot

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
  File.WriteAllText(Path.Combine(repoRoot, "scripts", "roadmap-rendered.md"), rendered)
  printfn "  (wrote the freshly rendered page to scratchpad/roadmap-rendered.md to diff)"

printfn ""
printfn "=== 4. the count line the page prints ==="
rendered.Split('\n')
|> Array.filter (fun l -> l.StartsWith "On the page today")
|> Array.iter (fun l -> printfn "%s" l)

if lies > 0 then exit 1
exit 0