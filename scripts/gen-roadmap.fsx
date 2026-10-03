// Regenerates docs/roadmap.md from scripts/roadmap/RoadmapItems.fs.
//
//   dotnet fsi scripts/gen-roadmap.fsx
//
// The whole page is generated, so nothing in it is edited by hand. An item moves to Built when the
// landmark it names is in the tree (see scripts/roadmap/Roadmap.fs), so running this after landing
// the code is all it takes. RoadmapDocTests fails when the page and the tree disagree.

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

let page = render read SageFs.RoadmapItems.items
let target = Path.Combine(repoRoot, "docs", "roadmap.md")
File.WriteAllText(target, page)

let built =
  SageFs.RoadmapItems.items
  |> List.filter (fun item ->
    match standing read item with
    | Built _ -> true
    | OpenAt _ -> false)
  |> List.length

printfn "wrote %s: %d items, %d built" target (List.length SageFs.RoadmapItems.items) built
