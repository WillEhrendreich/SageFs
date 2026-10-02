// scripts/ship.fsx [<commit>]   run it as: dotnet fsi scripts/ship.fsx [-- <commit>]
//
// Bump the version, gate that commit on this machine, then push exactly it to master.
//
// Pushes the GATED sha, never a moving HEAD: other agents may commit while
// the gate runs, and their commits were not gated. Publish follows from the
// self-hosted "main build" job, which promotes this gate's release bundle.
//
// The bump lives here, not in a commit hook, so an ordinary commit never
// touches Directory.Build.props and parallel worktrees never collide on it.
// One push to master is one release, so one ship is one bump. Pass a commit to
// gate and push something that already carries its bump (a re-run after a fix,
// or a commit made elsewhere); the bump is skipped then, and pre-push still
// refuses anything that doesn't raise the version.
//
// Before it bumps anything or calls the gate it builds SageFs.Tests in Release and runs the ratchet lane
// (`SageFs.Tests.dll --ratchets`) on the working tree, which has to be exactly the commit being shipped.

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

// ---- running things ------------------------------------------------------------------------------------

/// What a finished process left behind.
type Outcome = { Code: int; Output: string }

let private start (cwd: string) (exe: string) (args: string list) (capture: bool) : Outcome =
  let psi = ProcessStartInfo(exe)
  args |> List.iter psi.ArgumentList.Add
  psi.WorkingDirectory <- cwd
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- capture
  psi.RedirectStandardError <- capture
  use p = Process.Start psi
  match capture with
  | false ->
    p.WaitForExit()
    { Code = p.ExitCode; Output = "" }
  | true ->
    let stdout = p.StandardOutput.ReadToEndAsync()
    let stderr = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    { Code = p.ExitCode; Output = stdout.Result + stderr.Result }

/// Stop the ship with a reason, the way `set -e` plus an echo to stderr did.
let private die (lines: string list) : 'a =
  lines |> List.iter (eprintfn "%s")
  exit 1

let scriptsDir = __SOURCE_DIRECTORY__

let repo =
  match start scriptsDir "git" [ "rev-parse"; "--show-toplevel" ] true with
  | { Code = 0; Output = root } -> root.Trim()
  | _ -> die [ "ship: this script is not inside a git checkout." ]

/// A git command in the repo, output captured and trimmed. Fails the ship when git does.
let private git (args: string list) : string =
  match start repo "git" ("-C" :: repo :: args) true with
  | { Code = 0; Output = text } -> text.Trim()
  | { Output = text } -> die [ sprintf "ship: git %s failed: %s" (String.Join(" ", args)) (text.Trim()) ]

/// A command that talks to the terminal as it runs; a non-zero exit stops the ship.
let private mustRun (what: string) (cwd: string) (exe: string) (args: string list) : unit =
  match (start cwd exe args false).Code with
  | 0 -> ()
  | code -> die [ sprintf "ship: %s failed (exit %d)." what code ]

// ---- the ratchet lane ------------------------------------------------------------------------------------

let private testDll = "SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll"

/// The failures in a lane's output by name: each `[E]` line, then the message line under it.
let private failuresIn (output: string) : string list =
  let lines = output.Replace("\r\n", "\n").Split('\n')
  [ for i in 0 .. lines.Length - 1 do
      match lines[i].StartsWith "[E] " with
      | false -> ()
      | true ->
        yield lines[i]
        if i + 1 < lines.Length then yield "    " + lines[i + 1] ]

/// The ratchets (line and blocking-call budgets, literal counts, a stale generated page, CI wiring) are pure
/// reads of the tree, so they run here, seconds after one Release build, instead of twenty minutes into the
/// gate. They read the WORKING TREE, so it has to be exactly the commit being shipped: a green run on some
/// other tree proves nothing about it.
let runRatchets (target: string) : unit =
  let head = git [ "rev-parse"; "HEAD" ]
  let tracked = git [ "status"; "--porcelain"; "--untracked-files=no" ]
  match head = target && tracked = "" with
  | false ->
    die
      [ sprintf "ship: the ratchets read the working tree, which must be exactly %s (HEAD, no tracked changes)." (target.Substring(0, 8))
        "      commit or stash the rest, or ship from a clean worktree of that commit." ]
  | true -> ()
  printfn "ship: building SageFs.Tests (Release), then the ratchets"
  mustRun "the SageFs.Tests build" repo "dotnet" [ "build"; Path.Combine(repo, "SageFs.Tests"); "-c"; "Release"; "--nologo"; "-v"; "minimal" ]
  // From the repo root: three ratchets find the repo by walking up from the working directory.
  // The output is kept so a red run can name the failing ratchets without the lane's other noise.
  let lane = start repo "dotnet" [ testDll; "--ratchets" ] true
  let trustLine = lane.Output.Replace("\r\n", "\n").Split('\n') |> Array.tryFind (fun l -> l.StartsWith "TRUST")
  match lane.Code with
  | 0 -> trustLine |> Option.iter (printfn "%s")
  | _ ->
    let failures = failuresIn lane.Output
    die
      ([ "ship: a ratchet is red; nothing was bumped or gated. The failing ratchets:" ]
       @ (match failures with
          | [] -> lane.Output.Replace("\r\n", "\n").Split('\n') |> Array.rev |> Array.truncate 40 |> Array.rev |> List.ofArray
          | named -> named)
       @ (trustLine |> Option.toList)
       @ [ sprintf "      reproduce: dotnet %s --ratchets" testDll
           "      a budget that only went DOWN is fixed by adding --tighten to that command." ])

// ---- the ship -------------------------------------------------------------------------------------------

let private given =
  fsi.CommandLineArgs |> Array.skip 1 |> Array.filter (fun a -> a <> "--") |> Array.tryHead

/// Bump the version (scripts/bump-version prints it and stages nothing), then commit exactly the two files.
let private bumpAndCommit () : unit =
  let version =
    match start repo (Path.Combine(scriptsDir, "bump-version")) [] true with
    | { Code = 0; Output = text } -> text.Trim()
    | { Output = text } -> die [ sprintf "ship: bump-version failed: %s" (text.Trim()) ]
  git [ "commit"; "--quiet"; "--no-verify"; "-m"; sprintf "chore: release v%s" version; "--"; "Directory.Build.props"; "sagefs-vscode/package.json" ] |> ignore
  printfn "ship: bumped to %s" version

let private sha : string =
  match given with
  | Some commit ->
    // STEP ratchets given
    let resolved = git [ "rev-parse"; commit + "^{commit}" ]
    runRatchets resolved
    resolved
  | None ->
    match git [ "status"; "--porcelain"; "--"; "Directory.Build.props"; "sagefs-vscode/package.json" ] with
    | "" -> ()
    | _ ->
      die
        [ "ship: Directory.Build.props or sagefs-vscode/package.json has uncommitted changes."
          "      commit or revert them first, so the bump lands on a clean version." ]
    // The roadmap page moves items to Built when their landmark is in the tree, so a ship refreshes it.
    // RoadmapDocTests (a ratchet) fails on a stale page, which is why it is regenerated here and committed
    // BEFORE the ratchets run: the page a ship refreshes is not a failure of the commit being shipped.
    mustRun "the roadmap refresh" repo "dotnet" [ "fsi"; Path.Combine(scriptsDir, "gen-roadmap.fsx") ]
    match git [ "status"; "--porcelain"; "--"; "docs/roadmap.md" ] with
    | "" -> ()
    | _ ->
      git [ "commit"; "--quiet"; "--no-verify"; "-m"; "docs(roadmap): refresh the roadmap"; "--"; "docs/roadmap.md" ] |> ignore
      printfn "ship: roadmap refreshed"
    // STEP ratchets head
    runRatchets (git [ "rev-parse"; "HEAD" ])
    // STEP bump
    bumpAndCommit ()
    git [ "rev-parse"; "HEAD" ]

let private fsiScript (name: string) (args: string list) = "fsi" :: Path.Combine(scriptsDir, name) :: args

// The plugin reaches users commit by commit, so a daemon change that moves the wire contract has to land
// with its plugin change first. This refuses a release the current sagefs.nvim cannot talk to.
mustRun "the plugin compatibility check" repo "dotnet" (fsiScript "sync-nvim-version.fsx" [ "--"; "--compat" ])
mustRun "the plugin impact check" repo "dotnet" (fsiScript "sync-nvim-version.fsx" [ "--"; "--impact"; sha ])

// STEP gate
mustRun "the local gate" repo (Path.Combine(scriptsDir, "local-gate")) [ sha ]

// The daemon this machine runs must be the build being shipped: install the nupkg the gate just built and
// restart the daemon on it, and prove the daemon reports that version, before anything is pushed. Open
// sessions on the daemon are dropped on purpose (Will: knocking other agents out so they get the upgraded
// SageFs is always the right call). A version-skewed daemon costs signal, so this is not optional.
mustRun "the local install" repo "dotnet" (fsiScript "install-local.fsx" [ "--"; sha; "--force" ])

mustRun "the push" repo "git" [ "-C"; repo; "push"; "origin"; sprintf "%s:refs/heads/master" sha ]
printfn "ship: pushed %s; the self-hosted runner promotes its bundle to publish" (sha.Substring(0, 8))

// sagefs.nvim is part of SageFs and its version always matches the release. This bumps, tests and
// pushes the plugin once the SageFs push has gone through. It never blocks a release: a plugin repo
// that is dirty, off master or red says so and is left alone (see scripts/sync-nvim-version.fsx).
let released =
  match start repo "git" [ "-C"; repo; "show"; sprintf "%s:Directory.Build.props" sha ] true with
  | { Code = 0; Output = props } ->
    let m = Regex.Match(props, "<Version>([^<]+)</Version>")
    match m.Success with
    | true -> m.Groups[1].Value
    | false -> ""
  | _ -> ""

match released with
| "" -> ()
| version ->
  match (start repo "dotnet" (fsiScript "sync-nvim-version.fsx" [ "--"; version ]) false).Code with
  | 0 -> ()
  | _ -> eprintfn "ship: the sagefs.nvim version sync failed; run dotnet fsi scripts/sync-nvim-version.fsx -- %s" version
