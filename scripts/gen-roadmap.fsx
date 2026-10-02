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

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

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
