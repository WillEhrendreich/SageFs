module LemScore.Tests.WorkspaceTests

open System
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open LemRun
open LemRun.Failure

let private scratch () : string =
  let dir = Path.Combine(Path.GetTempPath(), "lem-ws-" + Guid.NewGuid().ToString("N").Substring(0, 8))
  Directory.CreateDirectory dir |> ignore
  dir

let private cleanup (dir: string) = try Store.removeTree dir with _ -> ()

let private write (dir: string) (path: string) (text: string) =
  let full = Path.Combine(dir, path)
  Directory.CreateDirectory(Path.GetDirectoryName full |> Option.ofObj |> Option.defaultValue dir) |> ignore
  File.WriteAllText(full, text)

let private git (dir: string) (args: string list) = Workspace.gitIn dir args |> fun c -> c.Stdout.Trim()

[<Tests>]
let fixtures =
  testList "the workspace of a run" [
    testCase "a fixture is a name, or sagefs-copy: and a checkout" <| fun _ ->
      Workspace.parseFixture "demoenv" |> Expect.equal "named" (Workspace.Named "demoenv")
      Workspace.parseFixture "sagefs-copy:/home/x/SageFs" |> Expect.equal "copy" (Workspace.SagefsCopy "/home/x/SageFs")
      Workspace.parseFixture "sagefs-copy:/a:b" |> Expect.equal "a colon in the path survives" (Workspace.SagefsCopy "/a:b")

    testCase "a copy of a checkout holds tracked files only, and nothing that would hand the lemming more than a new user has" <| fun _ ->
      let source, target = scratch (), scratch ()
      try
        git source [ "init"; "-q" ] |> ignore
        git source [ "config"; "user.email"; "t@t.invalid" ] |> ignore
        git source [ "config"; "user.name"; "t" ] |> ignore
        for f in [ "src/a.fs"; "AGENTS.md"; "CLAUDE.md"; ".claude/settings.json"; ".commandcode/x"; ".agents/y"; ".mcp.json"; "keep/AGENTS.md" ] do write source f f
        git source [ "add"; "-A" ] |> ignore
        git source [ "commit"; "-q"; "-m"; "c" ] |> ignore
        write source "private-notes.md" "untracked"
        Workspace.copySagefs source target
        let copied = Directory.GetFiles(target, "*", SearchOption.AllDirectories) |> Array.map (fun f -> Path.GetRelativePath(target, f)) |> Array.sort |> Array.toList
        copied |> Expect.equal "only the plain tracked files" [ "keep/AGENTS.md"; "src/a.fs" ]
      finally
        cleanup source
        cleanup target

    testCase "something that is not a git checkout is refused with exit 2, and nothing is copied" <| fun _ ->
      let source, target = scratch (), scratch ()
      try
        try
          Workspace.copySagefs source target
          failtest "accepted"
        with Stop (Refused why) -> why |> Expect.stringContains "says why" "is not a git checkout"
        Directory.GetFileSystemEntries target |> Expect.isEmpty "nothing copied"
      finally
        cleanup source
        cleanup target

    testCase "the build output of the named projects is copied so a session loads quickly" <| fun _ ->
      let source, target = scratch (), scratch ()
      try
        git source [ "init"; "-q" ] |> ignore
        git source [ "config"; "user.email"; "t@t.invalid" ] |> ignore
        git source [ "config"; "user.name"; "t" ] |> ignore
        write source "SageFs.Core/a.fs" "a"
        git source [ "add"; "-A" ] |> ignore
        git source [ "commit"; "-q"; "-m"; "c" ] |> ignore
        write source "SageFs.Core/bin/Release/x.dll" "built"
        write source "SageFs.Core/obj/y.json" "obj"
        write source "Other/bin/z.dll" "not asked for"
        Workspace.copySagefs source target
        File.Exists(Path.Combine(target, "SageFs.Core", "bin", "Release", "x.dll")) |> Expect.isTrue "bin"
        File.Exists(Path.Combine(target, "SageFs.Core", "obj", "y.json")) |> Expect.isTrue "obj"
        File.Exists(Path.Combine(target, "Other", "bin", "z.dll")) |> Expect.isFalse "only the named projects"
      finally
        cleanup source
        cleanup target
  ]

[<Tests>]
let setup =
  testList "task setup" [
    testCase "only sagefs-small-fix has one, and it seeds exactly one off-by-one" <| fun _ ->
      Workspace.setupFor "parse-seed" |> Expect.isNone "none"
      Workspace.setupFor "smoke" |> Expect.isNone "none"
      let dir = scratch ()
      try
        write dir "SageFs.Core/RingBuffer.fs" "let tryGet age =\n    match age >= 0 && age < buf.Count with\n    | true -> 1\n"
        (Workspace.setupFor "sagefs-small-fix").Value dir
        File.ReadAllText(Path.Combine(dir, "SageFs.Core", "RingBuffer.fs")) |> Expect.stringContains "seeded" "age <= buf.Count"
      finally cleanup dir

    testCase "a setup that does not match is refused out loud, not a silent no-op" <| fun _ ->
      let dir = scratch ()
      try
        write dir "SageFs.Core/RingBuffer.fs" "nothing to seed here"
        try
          (Workspace.setupFor "sagefs-small-fix").Value dir
          failtest "accepted"
        with Stop (Refused why) -> why |> Expect.stringContains "says why" "no match"
        File.ReadAllText(Path.Combine(dir, "SageFs.Core", "RingBuffer.fs")) |> Expect.equal "untouched" "nothing to seed here"
      finally cleanup dir
  ]

[<Tests>]
let repository =
  testList "the lemming's git repository" [
    testCase "a real repository with no remote, a baseline commit and a tag, and build output left out of the diff" <| fun _ ->
      let dir = scratch ()
      try
        write dir "a.fs" "a"
        Workspace.initRepository dir Workspace.cmdBaseline
        git dir [ "tag" ] |> Expect.equal "tag" "lem-baseline"
        git dir [ "remote" ] |> Expect.equal "no remote, so the lemming cannot push" ""
        git dir [ "log"; "--format=%s" ] |> Expect.equal "baseline" "lemming baseline"
        git dir [ "config"; "commit.gpgsign" ] |> Expect.equal "unsigned" "false"
        write dir "bin/x.dll" "built"
        write dir "obj/y" "o"
        write dir ".SageFs/z" "s"
        write dir "work.fs" "the lemming's"
        File.AppendAllText(Path.Combine(dir, "a.fs"), "b")
        Workspace.changedFiles Workspace.gitIn dir |> Expect.equal "only the lemming's work" [ "a.fs"; "work.fs" ]
      finally cleanup dir

    testCase "an editor workspace excludes the harness files and gets a .gitignore only when it has none" <| fun _ ->
      let dir, other = scratch (), scratch ()
      try
        let baseline : Workspace.Baseline =
          { UserName = "lemming"; Message = "lemming baseline"; Excludes = [ ".commandcode/"; ".mcp.json" ]; GitignoreIfAbsent = [ "bin/"; "obj/" ]; Tag = true }
        write dir "a.fs" "a"
        Workspace.initRepository dir baseline
        File.ReadAllText(Path.Combine(dir, ".gitignore")) |> Expect.equal "made" "bin/\nobj/\n"
        write other "a.fs" "a"
        write other ".gitignore" "custom\n"
        Workspace.initRepository other baseline
        File.ReadAllText(Path.Combine(other, ".gitignore")) |> Expect.equal "kept" "custom\n"
        write dir ".mcp.json" "{}"
        Workspace.changedFiles Workspace.gitIn dir |> Expect.isEmpty "harness files are not the lemming's work"
      finally
        cleanup dir
        cleanup other

    testCase "a tour's repository is untagged and its commit is the tour's own" <| fun _ ->
      let dir = scratch ()
      try
        write dir "a.fs" "a"
        Workspace.initRepository dir { UserName = "tour"; Message = "tour baseline"; Excludes = []; GitignoreIfAbsent = []; Tag = false }
        git dir [ "tag" ] |> Expect.equal "no tag" ""
        git dir [ "log"; "--format=%an|%s" ] |> Expect.equal "author" "tour|tour baseline"
      finally cleanup dir
  ]

[<Tests>]
let registration =
  testList "the MCP registration" [
    testCase "it is written the way the README says: command, args, and the port that makes the bridge attach to the running daemon" <| fun _ ->
      let text = Workspace.mcpJson [ "dotnet"; "/store/bridge/b1/SageFs.dll"; "mcp" ] 37749
      use doc = JsonDocument.Parse text
      let server = doc.RootElement.GetProperty("mcpServers").GetProperty("sagefs")
      server.GetProperty("command").GetString() |> Expect.equal "command" "dotnet"
      [ for a in server.GetProperty("args").EnumerateArray() -> a.GetString() ] |> Expect.equal "args" [ "/store/bridge/b1/SageFs.dll"; "mcp" ]
      server.GetProperty("env").GetProperty("SAGEFS_MCP_PORT").GetString() |> Expect.equal "port" "37749"

    testCase "the published tool is registered as `sagefs mcp`" <| fun _ ->
      use doc = JsonDocument.Parse(Workspace.mcpJson [ "sagefs"; "mcp" ] 37749)
      doc.RootElement.GetProperty("mcpServers").GetProperty("sagefs").GetProperty("command").GetString() |> Expect.equal "command" "sagefs"

    testCase "an editor lemming has no server at all, and a path with a plus sign is not escaped into noise" <| fun _ ->
      use doc = JsonDocument.Parse(Workspace.mcpJson [] 37749)
      doc.RootElement.GetProperty("mcpServers").EnumerateObject() |> Seq.length |> Expect.equal "empty" 0
      Workspace.mcpJson [ "dotnet"; "/store/0.6.882+abc/SageFs.dll"; "mcp" ] 1 |> Expect.stringContains "readable" "0.6.882+abc"

    testCase "installing puts the skill and the registration in the workspace and keeps them out of git status" <| fun _ ->
      let dir = scratch ()
      try
        write dir "a.fs" "a"
        Workspace.initRepository dir Workspace.cmdBaseline
        Workspace.install dir [ "sagefs"; "mcp" ] 37749
        File.Exists(Path.Combine(dir, ".mcp.json")) |> Expect.isTrue "registration"
        Directory.Exists(Path.Combine(dir, ".commandcode", "skills", "sagefs")) |> Expect.isTrue "skill"
        Workspace.changedFiles Workspace.gitIn dir |> Expect.isEmpty "not the lemming's work"
        // idempotent
        Workspace.install dir [ "sagefs"; "mcp" ] 37749
        (File.ReadAllText(Path.Combine(dir, ".git", "info", "exclude")).Split('\n') |> Array.contains ".mcp.json") |> Expect.isTrue "excluded"

      finally cleanup dir

    testCase "an empty skill directory means no skill: the installer leaves it alone" <| fun _ ->
      let dir = scratch ()
      try
        Directory.CreateDirectory(Path.Combine(dir, ".commandcode", "skills", "sagefs")) |> ignore
        Workspace.install dir [] 37749
        Directory.GetFileSystemEntries(Path.Combine(dir, ".commandcode", "skills", "sagefs")) |> Expect.isEmpty "still empty"
      finally cleanup dir
  ]

[<Tests>]
let failures =
  testList "why a run stops, and with which exit code" [
    testCase "one exit code per kind: the documented ones" <| fun _ ->
      exitCodeOf (MissingArgument "") |> Expect.equal "missing argument" 1
      exitCodeOf (Refused "") |> Expect.equal "refused" 2
      exitCodeOf (DaemonUnavailable "") |> Expect.equal "daemon" 3
      exitCodeOf (ToolchainMissing "") |> Expect.equal "toolchain" 4
      exitCodeOf (DesktopLeak "") |> Expect.equal "leak" 5
      exitCodeOf (DaemonRestarted "") |> Expect.equal "daemon restarted" 6
  ]
