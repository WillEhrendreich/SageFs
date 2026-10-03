// scripts/reinstall-vscode-ext.fsx   rebuild and reinstall the SageFs VS Code extension, then reload VS Code
// Run with: dotnet fsi scripts/reinstall-vscode-ext.fsx [-- --no-reload]
//
// Compiles sagefs-vscode (npm run compile), packages it (vsce package), installs the newest .vsix it finds with
// `code --install-extension --force`, and re-opens the repo in VS Code so the extension reloads.
//
// Every step is fatal on failure, with one exit code per kind.
open System
open System.Diagnostics
open System.IO

// ── named values ─────────────────────────────────────────────────────────────

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
  // Start from the directory this script lives in. A build-time constant only names where it was
  // built, so the walk up to SageFs.slnx is what locates the repo where the code actually runs.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let repo =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else repoRoot
let extensionDir = Path.Combine(repoRoot, "sagefs-vscode")
let compileTimeout = TimeSpan.FromMinutes 10.
let packageTimeout = TimeSpan.FromMinutes 10.
let installTimeout = TimeSpan.FromMinutes 5.
let reloadTimeout = TimeSpan.FromSeconds 30.

type Mode =
  | Reload
  | NoReload

/// Why the script stopped. One exit code per kind, so a caller can tell them apart.
type Failure =
  | Usage of string
  | CompileFailed
  | PackageFailed
  | NoPackage of dir: string
  | InstallFailed

let exitCodeOf = function
  | Usage _ -> 64
  | CompileFailed -> 4
  | PackageFailed -> 5
  | NoPackage _ -> 6
  | InstallFailed -> 7

let describe = function
  | Usage m -> m
  | CompileFailed -> "Compile failed"
  | PackageFailed -> "Package failed"
  | NoPackage dir -> sprintf "vsce reported success but there is no .vsix in %s" dir
  | InstallFailed -> "Install failed"

exception Stop of Failure
let fail f = raise (Stop f)
let say (s: string) = printfn "%s" s

let parse (argv: string list) : Mode =
  match argv with
  | [] -> Reload
  | [ "--no-reload" ] -> NoReload
  | other -> fail (Usage (sprintf "usage: reinstall-vscode-ext.fsx [--no-reload], not: %s" (String.Join(" ", other))))

/// npm, npx and code are .cmd shims on Windows, which a process cannot start by their bare names.
let tool (name: string) = if OperatingSystem.IsWindows() then name + ".cmd" else name

/// Runs a tool with its output going straight to the terminal; returns its exit code.
let run (file: string) (args: string list) (cwd: string) (timeout: TimeSpan) : int =
  try
    let psi = ProcessStartInfo(tool file)
    psi.WorkingDirectory <- cwd
    psi.UseShellExecute <- false
    args |> List.iter psi.ArgumentList.Add
    use p = Process.Start psi
    match p.WaitForExit timeout with
    | true -> p.ExitCode
    | false ->
      (try p.Kill true with _ -> ())
      124
  with e ->
    eprintfn "%s: %s" file e.Message
    127

let reinstall (mode: Mode) : unit =
  say "Compiling extension..."
  if run "npm" [ "run"; "compile" ] extensionDir compileTimeout <> 0 then fail CompileFailed
  say "Packaging VSIX..."
  if run "npx" [ "@vscode/vsce"; "package"; "--no-dependencies"; "--skip-license" ] extensionDir packageTimeout <> 0 then fail PackageFailed
  let vsix =
    DirectoryInfo(extensionDir).GetFiles "*.vsix"
    |> Array.sortByDescending (fun f -> f.LastWriteTime)
    |> Array.tryHead
  match vsix with
  | None -> fail (NoPackage extensionDir)
  | Some file ->
    say (sprintf "Installing %s..." file.Name)
    if run "code" [ "--install-extension"; file.FullName; "--force" ] extensionDir installTimeout <> 0 then fail InstallFailed
    match mode with
    | NoReload -> ()
    | Reload ->
      say "Reloading VSCode..."
      // Re-open the workspace folder to trigger extension reload.
      run "code" [ "-r"; repoRoot ] extensionDir reloadTimeout |> ignore
    say "Done."

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

let code =
  try
    reinstall (parse argv)
    0
  with Stop f ->
    eprintfn "reinstall-vscode-ext: %s" (describe f)
    exitCodeOf f

exit code
