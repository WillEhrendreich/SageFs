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

// The MAIN checkout: the parent of git's common dir. `resolvePath` anchors a path that climbs out
// of this tree (../sagefs.nvim/...) here, so this script reads the same sibling file from every
// worktree of the repo. The test computes the same value through RepoPaths.mainCheckoutRoot, which
// reads the .git pointer off the disk; both are the answer to git rev-parse --git-common-dir with
// the trailing /.git stripped. No git answer (a tarball, a bare checkout) falls back to this root,
// where the rule is a no-op.
let mainRoot =
  let psi = System.Diagnostics.ProcessStartInfo("git")
  for a in [ "-C"; repo; "rev-parse"; "--path-format=absolute"; "--git-common-dir" ] do
    psi.ArgumentList.Add a
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  try
    use p = System.Diagnostics.Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    match p.WaitForExit 10000 && p.ExitCode = 0 && out.Trim() <> "" with
    | false -> repo
    | true ->
      let common = Path.TrimEndingDirectorySeparator(Path.GetFullPath(out.Trim()))
      match Path.GetFileName common with
      | ".git" -> (Path.GetDirectoryName common).TrimEnd(Path.DirectorySeparatorChar)
      | _ -> common
  with _ -> repo

let read (relative: string) : FileState =
  let path = resolvePath repo mainRoot relative
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
