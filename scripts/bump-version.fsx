// scripts/bump-version.fsx   raise the patch version in Directory.Build.props and sync it into
// sagefs-vscode/package.json.
// Run with: dotnet fsi scripts/bump-version.fsx
//
// Nothing bumps on commit any more: one push to master is one release, so scripts/ship.fsx owns the bump and
// the pre-push hook (scripts/pre-push.fsx) refuses a master push whose version isn't higher than the one
// already on origin/master. Run this by hand only when you want the bump as its own step.
//
// Prints the new version on stdout and nothing else there, and stages neither file: the caller decides what
// to do with them. Messages for a human go to stderr.
//
// Exit codes: 0 bumped; 1 no <Version> in Directory.Build.props; 2 the version is not major.minor.patch;
// 9 not inside a git checkout.
#load "ReleaseRules.fs"

open System
open System.Diagnostics
open System.IO

// ── named values ─────────────────────────────────────────────────────────────

let propsFileName = "Directory.Build.props"
let packageJsonPath = Path.Combine("sagefs-vscode", "package.json")
let gitTimeout = TimeSpan.FromSeconds 30.

/// Why the script stopped. One exit code per kind.
type Failure =
  | NotInGit
  | NoVersion of propsPath: string
  | NotMajorMinorPatch of version: string

let exitCodeOf = function
  | NoVersion _ -> 1
  | NotMajorMinorPatch _ -> 2
  | NotInGit -> 9

let describe = function
  | NotInGit -> "this script is not inside a git checkout"
  | NoVersion path -> sprintf "no <Version> in %s" path
  | NotMajorMinorPatch v -> sprintf "version '%s' is not major.minor.patch with a numeric patch" v

exception Stop of Failure
let fail f = raise (Stop f)

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
      if String.IsNullOrEmpty parent || parent = full then ""
      else walk parent (depth + 1)
  // Start from the directory this script lives in, so the walk up finds the repo root at run time
  // rather than a build-time constant naming where it was compiled.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let repo =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else repoRoot

let repoRootFromGit () : string =
  let psi = ProcessStartInfo("git")
  [ "-C"; repo; "rev-parse"; "--show-toplevel" ] |> List.iter psi.ArgumentList.Add
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  match p.WaitForExit gitTimeout && p.ExitCode = 0 with
  | true -> out.Result.Trim()
  | false -> fail NotInGit

let bump () : string =
  let repo = repoRootFromGit ()
  let props = Path.Combine(repo, propsFileName)
  let propsText = if File.Exists props then File.ReadAllText props else ""
  let current = match ReleaseRules.versionIn propsText with | Some v -> v | None -> fail (NoVersion props)
  let next =
    match ReleaseRules.bumpPatch current with
    | Ok v -> v
    | Error(ReleaseRules.NotMajorMinorPatch v) -> fail (NotMajorMinorPatch v)
  File.WriteAllText(props, ReleaseRules.withPropsVersion current next propsText)
  let package = Path.Combine(repo, packageJsonPath)
  if File.Exists package then File.WriteAllText(package, ReleaseRules.withPackageVersion next (File.ReadAllText package))
  next

let code =
  try
    printfn "%s" (bump ())
    0
  with Stop f ->
    eprintfn "bump-version: %s" (describe f)
    exitCodeOf f

exit code
