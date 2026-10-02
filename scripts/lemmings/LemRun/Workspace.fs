/// The lemming's working directory: the fixture (or a copy of a SageFs checkout), a task's setup,
/// a real git repository with no remote and a baseline commit, and the skill and MCP registration a
/// new user would have. Nothing here hands the lemming more than a new user has.
module LemRun.Workspace

open System
open System.IO
open System.Text
open System.Text.Json
open LemRun.Failure
open LemScore.Program

// ---- the fixture ------------------------------------------------------------------------------

/// A fixture is a directory under fixtures/, or a copy of a SageFs checkout.
type Fixture =
  | Named of name: string
  | SagefsCopy of checkout: string

let private sagefsPrefix = "sagefs-copy:"

let parseFixture (text: string) : Fixture =
  match text.StartsWith sagefsPrefix with
  | true -> SagefsCopy (text.Substring sagefsPrefix.Length)
  | false -> Named text

/// Tracked files a lemming must not start with: agent instructions, private notes, another agent's state.
let private excludedFromCopy (path: string) : bool =
  path = "AGENTS.md" || path = "CLAUDE.md" || path = ".mcp.json"
  || path.StartsWith ".claude/" || path.StartsWith ".commandcode/" || path.StartsWith ".agents/"

/// The projects whose build output is copied too (LEM_COPY_BUILD, default SageFs.Core), so a
/// session loads quickly.
let private copyBuildProjects () : string list =
  Env.varOr "LEM_COPY_BUILD" "SageFs.Core" |> fun s -> s.Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList

/// A SageFs checkout without what would hand the lemming more than a new user has: no .git (a fresh
/// one is made after), no agent-instruction files, no private notes (only tracked files are
/// copied), and build output only for the projects a lemming loads. The checkout is never touched.
let copySagefs (source: string) (destination: string) : unit =
  match Proc.run (Proc.spec "git" [ "-C"; source; "rev-parse"; "--git-dir" ]) None with
  | { ExitCode = 0 } -> ()
  | _ -> fail (Refused (sprintf "sagefs-copy: %s is not a git checkout" source))
  let listing = Proc.run (Proc.spec "git" [ "-C"; source; "ls-files"; "-z" ]) None
  listing.Stdout.Split('\000', StringSplitOptions.RemoveEmptyEntries)
  |> Array.filter (excludedFromCopy >> not)
  |> Array.iter (fun relative ->
    let from = Path.Combine(source, relative)
    match File.Exists from with
    | false -> ()
    | true ->
      let target = Path.Combine(destination, relative)
      Directory.CreateDirectory(Path.GetDirectoryName target |> Option.ofObj |> Option.defaultValue destination) |> ignore
      File.Copy(from, target, true))
  for project in copyBuildProjects () do
    for sub in [ "bin"; "obj" ] do
      let built = Path.Combine(source, project, sub)
      match Directory.Exists built with
      | true ->
        match Store.copyTree built (Path.Combine(destination, project, sub)) with
        | Ok () -> ()
        | Error e -> fail (Refused e)
      | false -> say (sprintf "sagefs-copy: %s does not exist, so the lemming's first session will have to build" built)

/// Lays the fixture into `workdir`. A named fixture that does not exist is refused.
let copyFixture (fixture: Fixture) (workdir: string) : unit =
  match fixture with
  | SagefsCopy checkout -> copySagefs checkout workdir
  | Named name ->
    let dir = Path.Combine(Env.lemDir, "fixtures", name)
    match Directory.Exists dir with
    | false -> fail (Refused (sprintf "no fixture: %s" dir))
    | true ->
      match Store.copyTree dir workdir with
      | Ok () -> ()
      | Error e -> fail (Refused e)

// ---- task setup -------------------------------------------------------------------------------

/// Work a task needs done to the fixture BEFORE the baseline commit, so the diff is the lemming's.
/// Only tasks named here have any; everything else starts from the fixture as it is.
let setupFor (task: string) : (string -> unit) option =
  match task with
  | "sagefs-small-fix" ->
    // Seeds one off-by-one into RingBuffer.tryGet: the existing test "age beyond count returns None"
    // then fails, and the fix is one character in the one named file. This only ever runs on the
    // disposable copy; the checkout it came from is never touched.
    Some (fun workdir ->
      let file = Path.Combine(workdir, "SageFs.Core", "RingBuffer.fs")
      match replaceExact (File.ReadAllText file) "match age >= 0 && age < buf.Count with" "match age >= 0 && age <= buf.Count with" with
      | Ok updated -> File.WriteAllText(file, updated)
      | Error why -> fail (Refused (sprintf "task setup refused: %s in %s" why file)))
  | _ -> None

// ---- the git repository -----------------------------------------------------------------------

let gitIn (workdir: string) (args: string list) : Proc.Captured = Proc.run (Proc.spec "git" ("-C" :: workdir :: args)) None

/// git for the harness, run outside the sandboxes on a workspace an editor could write to: the
/// editor's .git is read-only, but a repository is also a list of programs to run, so those are
/// pinned off on the command line as well (a -c beats every config file).
let hardenedGit (workdir: string) (args: string list) : Proc.Captured =
  Proc.run (Proc.spec "git" (LemDrive.EditorSandbox.harnessGitPins @ [ "-C"; workdir ] @ args)) None

let private mustGit (workdir: string) (args: string list) : unit =
  match gitIn workdir args with
  | { ExitCode = 0 } -> ()
  | failed -> fail (Refused (sprintf "git %s failed in %s: %s" (String.Join(" ", args)) workdir (failed.Stdout + failed.Stderr)))

/// How a workspace's repository is made.
type Baseline =
  { UserName: string
    Message: string
    /// Lines for .git/info/exclude, so harness files stay out of `git status`.
    Excludes: string list
    /// Written to .gitignore when there is none, so build output is not the lemming's work.
    GitignoreIfAbsent: string list
    /// The run's baseline is tagged, so its diff is the lemming's.
    Tag: bool }

/// A real git repository with no remote: the lemming can commit locally and cannot push.
let initRepository (workdir: string) (b: Baseline) : unit =
  mustGit workdir [ "init"; "-q" ]
  let exclude = Path.Combine(workdir, ".git", "info", "exclude")
  Directory.CreateDirectory(Path.GetDirectoryName exclude |> Option.ofObj |> Option.defaultValue workdir) |> ignore
  File.AppendAllText(exclude, (b.Excludes |> List.map (fun l -> l + "\n") |> String.concat ""))
  mustGit workdir [ "config"; "user.name"; b.UserName ]
  mustGit workdir [ "config"; "user.email"; b.UserName + "@lem.invalid" ]
  mustGit workdir [ "config"; "commit.gpgsign"; "false" ]
  match b.GitignoreIfAbsent, File.Exists(Path.Combine(workdir, ".gitignore")) with
  | [], _ | _, true -> ()
  | lines, false -> File.WriteAllText(Path.Combine(workdir, ".gitignore"), lines |> List.map (fun l -> l + "\n") |> String.concat "")
  mustGit workdir [ "add"; "--"; "." ]
  mustGit workdir [ "commit"; "-q"; "-m"; b.Message ]
  match b.Tag with
  | true -> mustGit workdir [ "tag"; Env.Names.baselineTag ]
  | false -> ()

/// Build output and SageFs's own state are not the lemming's work.
let cmdBaseline : Baseline =
  { UserName = "lemming"; Message = "lemming baseline"; Excludes = [ "bin/"; "obj/"; ".SageFs/" ]; GitignoreIfAbsent = []; Tag = true }

/// What changed relative to where the lemming started: tracked changes and untracked files that
/// are not ignored, sorted and distinct.
let changedFiles (git: string -> string list -> Proc.Captured) (workdir: string) : string list =
  let lines (c: Proc.Captured) = c.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
  (lines (git workdir [ "diff"; "--name-only"; Env.Names.baselineTag ]) @ lines (git workdir [ "ls-files"; "--others"; "--exclude-standard" ]))
  |> List.distinct
  |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

// ---- the skill and the MCP registration --------------------------------------------------------

let private jsonText = JsonSerializerOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

/// The `.mcp.json` text, written the way the README tells a user to: `command` and `args`, with
/// SAGEFS_MCP_PORT so the bridge attaches to the running daemon instead of starting one. With no
/// bridge (an editor lemming) it lists no server at all.
let mcpJson (command: string list) (port: int) : string =
  match command with
  | [] -> "{ \"mcpServers\": {} }\n"
  | cmd :: args ->
    let q (s: string) = JsonSerializer.Serialize(s, jsonText)
    sprintf "{ \"mcpServers\": { \"sagefs\": { \"command\": %s, \"args\": [%s],\n  \"env\": { \"SAGEFS_MCP_PORT\": \"%d\" } } } }\n" (q cmd) (String.Join(", ", args |> List.map q)) port

/// The skill where Command Code finds project skills (unless the directory is already there: an
/// empty one means "no skill"), and the MCP registration. Harness files stay out of `git status`.
let install (workdir: string) (command: string list) (port: int) : unit =
  let skills = Path.Combine(workdir, ".commandcode", "skills")
  Directory.CreateDirectory skills |> ignore
  let skill = Path.Combine(skills, "sagefs")
  match Directory.Exists skill with
  | true -> ()
  | false ->
    match Store.copyTree (Path.Combine(Env.repoRoot, "skills", "sagefs")) skill with
    | Ok () -> ()
    | Error e -> fail (ToolchainMissing e)
  File.WriteAllText(Path.Combine(workdir, ".mcp.json"), mcpJson command port)
  match Directory.Exists(Path.Combine(workdir, ".git")) with
  | true -> File.AppendAllText(Path.Combine(workdir, ".git", "info", "exclude"), ".commandcode/\n.mcp.json\n")
  | false -> ()
