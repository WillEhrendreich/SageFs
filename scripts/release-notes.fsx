// Builds a release's notes from the conventional commits between the previous
// tag and this release's source SHA, grouped so they say what the release
// solved rather than listing commits.
//
//   dotnet fsi scripts/release-notes.fsx <version> <sourceSha> <outputPath>
//
// Every git call is allowed to fail: a shallow clone with no tags degrades to
// the last 20 commits, and an unreadable history still writes notes with the
// downloads section. This never blocks a publish, so it always exits 0.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions

let args = fsi.CommandLineArgs
let version = args |> Array.tryItem 1 |> Option.defaultValue "0.0.0"
let sourceSha = args |> Array.tryItem 2 |> Option.defaultValue "HEAD"
let outputPath =
  args
  |> Array.tryItem 3
  |> Option.defaultValue (Path.Combine(Path.GetTempPath(), "RELEASE_NOTES.md"))

/// git, with failure as an empty answer rather than an exception.
let git (arguments: string list) : string list =
  try
    let psi = ProcessStartInfo "git"
    for a in arguments do
      psi.ArgumentList.Add a
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    p.StandardError.ReadToEnd() |> ignore
    p.WaitForExit()
    match p.ExitCode with
    | 0 ->
      out.Replace("\r\n", "\n").Split('\n')
      |> Array.toList
      |> List.filter (fun line -> line.Trim() <> "")
    | _ -> []
  with _ -> []

git [ "fetch"; "--tags"; "--depth"; "200"; "origin" ] |> ignore

let previousTag =
  match git [ "describe"; "--tags"; "--abbrev=0"; sourceSha + "^" ] with
  | tag :: _ -> Some tag
  | [] ->
    match git [ "tag"; "--sort=-creatordate" ] with
    | tag :: _ -> Some tag
    | [] -> None

let subjects =
  let inRange =
    match previousTag with
    | Some tag -> git [ "log"; "--pretty=format:%s"; sprintf "%s..%s" tag sourceSha ]
    | None -> git [ "log"; "--pretty=format:%s"; sourceSha ]
  match inRange with
  | [] -> git [ "log"; "--pretty=format:%s"; "-20" ]
  | commits -> commits

/// The conventional-commit groups a reader cares about, in the order they're
/// shown. Anything that doesn't match a type is left out.
type Group =
  { Heading: string
    Pattern: Regex
    Commits: string list }

let groups =
  let group heading pattern = { Heading = heading; Pattern = Regex(pattern); Commits = [] }
  [ group "Fixes" @"^fix(\([^)]*\))?:"
    group "New capabilities" @"^feat(\([^)]*\))?:"
    group "Testing and proof" @"^test(\([^)]*\))?:"
    group "Maintenance" @"^(chore|docs|refactor|perf|build|ci|style)(\([^)]*\))?:" ]
  |> List.map (fun g ->
    { g with Commits = subjects |> List.filter (fun s -> g.Pattern.IsMatch s) })
  |> List.filter (fun g -> not (List.isEmpty g.Commits))

let countOf heading =
  groups
  |> List.tryFind (fun g -> g.Heading = heading)
  |> Option.map (fun g -> List.length g.Commits)
  |> Option.defaultValue 0

let fixes = countOf "Fixes"
let feats = countOf "New capabilities"

let plural n singular plural' = if n = 1 then sprintf "%d %s" n singular else sprintf "%d %s" n plural'

let headline =
  match fixes, feats with
  | 0, 0 -> "Maintenance and internals."
  | f, 0 -> sprintf "This release fixes %s." (plural f "issue" "issues")
  | 0, c -> sprintf "This release adds %s." (plural c "capability" "capabilities")
  | f, c -> sprintf "This release fixes %s and adds %s." (plural f "issue" "issues") (plural c "capability" "capabilities")

let notes = StringBuilder()
let line (text: string) = notes.AppendLine text |> ignore

line (sprintf "## SageFs v%s" version)
line ""
line headline
line ""
for group in groups do
  line (sprintf "### %s" group.Heading)
  line ""
  for commit in group.Commits do
    line (sprintf "- %s" commit)
  line ""
line "### Downloads"
line ""
line "- **NuGet**: `dotnet tool install --global SageFs`"
line (sprintf "- **VS Code extension**: `code --install-extension sagefs-vscode-%s.vsix`" version)
line "- **Neovim**: see [sagefs.nvim](https://github.com/WillEhrendreich/sagefs.nvim)"

File.WriteAllText(outputPath, notes.ToString(), UTF8Encoding false)

printfn
  "Release notes for v%s: %d commits since %s, written to %s"
  version
  (List.length subjects)
  (defaultArg previousTag "(no tag)")
  outputPath

match Environment.GetEnvironmentVariable "GITHUB_OUTPUT" with
| null | "" -> ()
| output -> File.AppendAllText(output, sprintf "notes_path=%s\n" outputPath)
