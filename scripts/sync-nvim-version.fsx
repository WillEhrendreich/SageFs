// scripts/sync-nvim-version.fsx -- keeps sagefs.nvim moving with SageFs.
// Run with: dotnet fsi scripts/sync-nvim-version.fsx -- <mode> [args]
//
//   <version>        sync: the SageFs release just made gets a plugin commit with the same version,
//                    tested and pushed. scripts/ship runs this after it pushes, so the plugin never lags.
//   --check <ver>    report whether the plugin's version matches, change nothing (exit 1 on a mismatch)
//   --compat         the daemon's apiVersion (SageFs.Core/EndpointContracts.fs) against the api_range the
//                    plugin declares in lua/sagefs/compat.lua; exit 1 when the daemon is outside it.
//                    The plugin reaches users commit by commit, so the version string is only a marker and
//                    this range is what decides whether an installed plugin can talk to this daemon.
//   --impact <sha>   every commit that touches the wire surface carries a `Plugin: done <what>` or
//                    `Plugin: n/a <why>` trailer; exit 1 listing the ones that do not. Commits at or before
//                    the sha in scripts/plugin-impact-baseline are exempt (a one-time audit covered them).
//
// The plugin repo is found at $SAGEFS_NVIM_DIR (default ~/Work/sagefs.nvim). It is its own repo so Neovim
// distribution works, and it moves with SageFs. Sync never blocks a release: a plugin repo that is missing,
// dirty, off master, diverged, or red in its own tests is reported and left alone (exit 0). Exit 1 is only for
// --check, --compat and --impact findings, and exit 64 for bad arguments.
open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

// ── named values ─────────────────────────────────────────────────────────────

let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
let nvimDir =
  match Environment.GetEnvironmentVariable "SAGEFS_NVIM_DIR" with
  | null | "" -> Path.Combine(home, "Work", "sagefs.nvim")
  | p -> p
let versionFile = Path.Combine(nvimDir, "lua", "sagefs", "version.lua")
let compatFile = Path.Combine(nvimDir, "lua", "sagefs", "compat.lua")
let endpointContracts = Path.Combine("SageFs.Core", "EndpointContracts.fs")
let baselineFile = Path.Combine("scripts", "plugin-impact-baseline")
let primaryBranch = "master"
let remote = "origin"
let versionShape = Regex(@"^\d+\.\d+\.\d+$")
let pluginTrailer = Regex(@"^Plugin: (done|n/a)", RegexOptions.Multiline)

/// The files whose changes a Neovim user could want to take advantage of.
let wireSurface =
  [ "SageFs.Core/EndpointContracts.fs"; "SageFs/McpTools.fs"; "SageFs/McpServer.fs"; "SageFs/McpLeaseWire.fs"
    "SageFs/DashboardTypes.fs"; "SageFs.Core/SessionOperations.fs"; "SageFs.Core/SessionStatusPayload.fs"
    "docs/mcp-tools.md" ]

type Mode =
  | Sync of version: string
  | Check of version: string
  | Compat
  | Impact of sha: string

/// What a run of the plugin's own tests said.
type SuiteResult =
  | Green
  | RunnerMissing
  | Red of outputFile: string

// ── processes ────────────────────────────────────────────────────────────────

let run (file: string) (args: string list) (cwd: string) (env: (string * string) list) : int * string =
  let psi = ProcessStartInfo(file)
  psi.WorkingDirectory <- cwd
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  args |> List.iter psi.ArgumentList.Add
  env |> List.iter (fun (k, v) -> psi.Environment[k] <- v)
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  p.WaitForExit()
  p.ExitCode, out.Result + err.Result

let git (repo: string) (args: string list) = run "git" ("-C" :: repo :: args) repo []
let gitOk (repo: string) (args: string list) = fst (git repo args) = 0
let gitOut (repo: string) (args: string list) =
  match git repo args with
  | 0, o -> o.Trim()
  | _, o -> failwithf "git %s: %s" (String.Join(" ", args)) o
let warn (m: string) = eprintfn "sync-nvim-version: %s" m
let say (m: string) = printfn "%s" m

let sageFsRepo = gitOut __SOURCE_DIRECTORY__ [ "rev-parse"; "--show-toplevel" ]

let firstGroup (pattern: string) (text: string) =
  let m = Regex.Match(text, pattern, RegexOptions.Multiline)
  if m.Success then Some m.Groups[1].Value else None

let readFile path = if File.Exists path then Some (File.ReadAllText path) else None

// ── --compat ─────────────────────────────────────────────────────────────────

let compat () =
  let api = readFile (Path.Combine(sageFsRepo, endpointContracts)) |> Option.bind (firstGroup @"let apiVersion = (\d+)") |> Option.map int
  let plugin = readFile compatFile
  let bound name = plugin |> Option.bind (firstGroup (sprintf @"^  %s = (\d+)" name)) |> Option.map int
  match api, bound "min", bound "max" with
  | None, _, _ ->
    warn "could not read apiVersion from SageFs.Core/EndpointContracts.fs"
    1
  | Some api, None, _ | Some api, _, None ->
    warn (sprintf "could not read the api_range from %s. The plugin was NOT checked against apiVersion %d." compatFile api)
    0
  | Some api, Some lo, Some hi when api >= lo && api <= hi ->
    say (sprintf "sagefs.nvim understands apiVersion %d (declares %d to %d)" api lo hi)
    0
  | Some api, Some lo, Some hi ->
    warn (sprintf "SageFs speaks apiVersion %d but sagefs.nvim declares %d to %d." api lo hi)
    warn (sprintf "  Update the plugin first: change what it calls and lua/sagefs/compat.lua api_range in %s, test it, push it, then ship SageFs." nvimDir)
    1

// ── --impact ─────────────────────────────────────────────────────────────────

let impact (sha: string) =
  let baseline = readFile (Path.Combine(sageFsRepo, baselineFile)) |> Option.map (fun s -> s.Trim()) |> Option.defaultValue ""
  let exempt = baseline <> "" && gitOk sageFsRepo [ "merge-base"; "--is-ancestor"; baseline; sha ]
  let from = if exempt then baseline else gitOut sageFsRepo [ "merge-base"; sprintf "%s/%s" remote primaryBranch; sha ]
  let commits =
    gitOut sageFsRepo ([ "log"; "--no-merges"; "--format=%H"; sprintf "%s..%s" from sha; "--" ] @ wireSurface)
    |> fun o -> o.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
  let missing =
    commits |> List.filter (fun c -> not (pluginTrailer.IsMatch(gitOut sageFsRepo [ "show"; "-s"; "--format=%B"; c ])))
  match missing with
  | [] ->
    say "every commit that touches the wire surface says what happened to sagefs.nvim"
    0
  | _ ->
    warn "these commits change the wire surface without a Plugin: trailer:"
    missing |> List.iter (fun c -> eprintfn "  %s" (gitOut sageFsRepo [ "log"; "-1"; "--format=%h %s"; c ]))
    warn "  Add 'Plugin: done <what>' (and make the sagefs.nvim change) or 'Plugin: n/a <why>' to each, then ship."
    1

// ── --check and sync ─────────────────────────────────────────────────────────

let currentPluginVersion () = readFile versionFile |> Option.bind (firstGroup "\"([0-9][^\"]*)\"")
let isGitCheckout dir = Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))

let check (version: string) =
  match currentPluginVersion () with
  | Some v when v = version ->
    say (sprintf "sagefs.nvim is at %s, matching SageFs %s" v version)
    0
  | other ->
    warn (sprintf "sagefs.nvim is at %s, SageFs is at %s" (defaultArg other "unknown") version)
    1

/// The plugin's tests run under LuaJIT with the Lua 5.1 LuaRocks tree: Neovim's Lua is LuaJIT and that is the
/// only Lua the plugin supports, so the suite never runs under a newer Lua.
let runSuite () : SuiteResult =
  let rocks = Path.Combine(home, ".luarocks")
  let runner =
    let dir = Path.Combine(rocks, "lib", "luarocks", "rocks-5.1", "busted")
    if Directory.Exists dir then
      Directory.GetDirectories dir
      |> Array.map (fun d -> Path.Combine(d, "bin", "busted"))
      |> Array.tryFind File.Exists
    else None
  let luajitAvailable = fst (run "which" [ "luajit" ] nvimDir []) = 0
  match runner with
  | Some r when luajitAvailable ->
    let lib = Path.Combine(rocks, "share", "lua", "5.1")
    let cpath = Path.Combine(rocks, "lib", "lua", "5.1")
    let env =
      [ "LUA_PATH", sprintf "%s/?.lua;%s/?/init.lua;;" lib lib
        "LUA_CPATH", sprintf "%s/?.so;;" cpath ]
    match run "luajit" [ r ] nvimDir env with
    | 0, _ -> Green
    | _, output ->
      let file = Path.GetTempFileName()
      File.WriteAllText(file, output)
      Red file
  | _ -> RunnerMissing

let sync (version: string) =
  if not (versionShape.IsMatch version) then
    warn (sprintf "'%s' is not a version like 0.6.880" version)
    64
  elif not (isGitCheckout nvimDir) then
    warn (sprintf "no plugin repo at %s (set SAGEFS_NVIM_DIR). The plugin version was NOT synced to %s." nvimDir version)
    0
  elif currentPluginVersion () = Some version then
    say (sprintf "sagefs.nvim already at %s" version)
    0
  else
    let hint = sprintf "run: dotnet fsi scripts/sync-nvim-version.fsx -- %s" version
    let branch = gitOut nvimDir [ "branch"; "--show-current" ]
    if branch <> primaryBranch then
      warn (sprintf "%s is on '%s', not %s. Not syncing; %s from a clean %s." nvimDir branch primaryBranch hint primaryBranch)
      0
    elif gitOut nvimDir [ "status"; "--porcelain"; "--untracked-files=no" ] <> "" then
      warn (sprintf "%s has uncommitted changes to tracked files. Not syncing; commit or stash them, then %s" nvimDir hint)
      0
    elif not (gitOk nvimDir [ "fetch"; "--quiet"; remote; primaryBranch ]) then
      warn "could not fetch origin for the plugin. Not syncing."
      0
    else
      let remoteRef = sprintf "%s/%s" remote primaryBranch
      let upToDate =
        if gitOk nvimDir [ "merge-base"; "--is-ancestor"; remoteRef; "HEAD" ] then true
        elif gitOk nvimDir [ "merge-base"; "--is-ancestor"; "HEAD"; remoteRef ] then gitOk nvimDir [ "merge"; "--quiet"; "--ff-only"; remoteRef ]
        else false
      if not upToDate then
        warn (sprintf "the plugin's %s has diverged from %s. Not syncing; resolve it, then %s" primaryBranch remoteRef hint)
        0
      else
        File.WriteAllText(versionFile, sprintf "return \"%s\"\n" version)
        say (sprintf "sagefs.nvim version file now says %s" version)
        match runSuite () with
        | RunnerMissing ->
          warn "busted (with luajit and the Lua 5.1 rocks tree) is not usable on this machine, so the plugin's tests were not run here; its CI will run them on the push."
          0
        | Red output ->
          warn (sprintf "the plugin's busted suite is RED. Reverting the version change and leaving it unpushed. Output: %s" output)
          git nvimDir [ "checkout"; "--"; "lua/sagefs/version.lua" ] |> ignore
          0
        | Green ->
          say "sagefs.nvim busted suite green"
          let message = sprintf "chore: release v%s\n\nMatches SageFs v%s. The plugin version always equals the SageFs release it was tested against.\n" version version
          let messageFile = Path.GetTempFileName()
          File.WriteAllText(messageFile, message)
          git nvimDir [ "commit"; "--quiet"; "-F"; messageFile; "--"; "lua/sagefs/version.lua" ] |> ignore
          File.Delete messageFile
          if gitOk nvimDir [ "push"; "--quiet"; remote; primaryBranch ] then say (sprintf "sagefs.nvim v%s pushed" version)
          else warn (sprintf "committed v%s in the plugin but the push failed. Run: git -C %s push %s %s" version nvimDir remote primaryBranch)
          0

// ── entry ────────────────────────────────────────────────────────────────────

let parse (argv: string list) : Mode option =
  match argv with
  | [ "--compat" ] -> Some Compat
  | [ "--impact"; sha ] -> Some (Impact sha)
  | [ "--check"; v ] -> Some (Check v)
  | [ v ] when not (v.StartsWith "-") -> Some (Sync v)
  | _ -> None

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

let code =
  match parse argv with
  | Some Compat -> compat ()
  | Some (Impact sha) -> impact sha
  | Some (Check v) -> check v
  | Some (Sync v) -> sync v
  | None ->
    warn "usage: dotnet fsi scripts/sync-nvim-version.fsx -- <version> | --check <version> | --compat | --impact <sha>"
    64

exit code
