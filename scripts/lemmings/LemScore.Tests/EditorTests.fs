module LemScore.Tests.EditorTests

open System
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open LemDrive
open LemRun

let private editor : EditorSandbox.Editor =
  { Home = "/home/u"; Workspace = "/tmp/lem/r/w"; Out = "/tmp/lem/r/out"; NvimBin = "/home/u/.local/share/bob/nvim-bin/nvim"
    PluginDir = "/home/u/Work/sagefs.nvim"; DaemonPort = 37749; HasGit = true }

let private pairsOf (flag: string) (args: string list) : (string * string) list =
  args |> List.indexed |> List.choose (fun (i, a) -> if a = flag then Some (args[i + 1], args[i + 2]) else None)

[<Tests>]
let neovimEditor =
  testList "the Neovim editor sandbox (as data)" [
    testCase "only the workspace and the tmux socket directory are writable" <| fun _ ->
      EditorSandbox.args editor |> pairsOf "--bind" |> Expect.equal "writable" [ "/tmp/lem/r/w", "/tmp/lem/r/w"; "/tmp/lem/r/out/tmux", "/tmp/lem/r/out/tmux" ]

    testCase "the repository, the install, the plugin and the editor's own files are read-only, and out/ is otherwise empty" <| fun _ ->
      let args = EditorSandbox.args editor
      let ro = args |> pairsOf "--ro-bind" |> List.map fst
      for p in [ "/tmp/lem/r/w/.git"; "/home/u/.local/share/bob"; "/home/u/Work/sagefs.nvim"; "/tmp/lem/r/out/ui" ] do ro |> Expect.contains p p
      args |> List.pairwise |> Expect.contains "out is a tmpfs, laid over before its two exceptions" ("--tmpfs", "/tmp/lem/r/out")
      // later mounts win: .git comes after the writable workspace, and the exceptions after the tmpfs
      let index (a: string) (b: string) = args |> List.pairwise |> List.findIndex (fun p -> p = (a, b))
      (index "--bind" "/tmp/lem/r/w" < index "--ro-bind" "/tmp/lem/r/w/.git") |> Expect.isTrue ".git over the workspace"
      (index "--tmpfs" "/tmp/lem/r/out" < index "--bind" "/tmp/lem/r/out/tmux") |> Expect.isTrue "tmux over the tmpfs"

    testCase "a workspace with no .git has nothing to lay over" <| fun _ ->
      EditorSandbox.args { editor with HasGit = false } |> pairsOf "--ro-bind" |> List.map fst |> List.contains "/tmp/lem/r/w/.git" |> Expect.isFalse "no .git mount"

    testCase "the run's bin, the dotnet home and the credential directory are not mounted at all" <| fun _ ->
      let mounted = EditorSandbox.args editor |> fun a -> (pairsOf "--bind" a @ pairsOf "--ro-bind" a) |> List.map fst
      for absent in [ "/tmp/lem/r/bin"; "/tmp/lem/r/dotnethome"; "/tmp/lem/r/cmdchome"; "/home/u/.commandcode"; "/home/u/.nuget"; "/home/u/.ssh" ] do
        mounted |> List.contains absent |> Expect.isFalse (sprintf "%s is not mounted" absent)

    testCase "the install directory is two levels above the binary" <| fun _ ->
      EditorSandbox.installDir "/home/u/.local/share/bob/nvim-bin/nvim" |> Expect.equal "bob" "/home/u/.local/share/bob"

    testCase "the lemming gets the workspace read-only, an empty MCP file, the socket, the driver and the docs" <| fun _ ->
      let bwrap, env = EditorSandbox.lemmingExtras editor "/store/drive/d1"
      bwrap |> pairsOf "--ro-bind" |> Expect.equal "read-only" [ "/tmp/lem/r/w", "/tmp/lem/r/w"; "/tmp/lem/r/out/ui/empty-mcp.json", "/tmp/lem/r/w/.mcp.json"; "/store/drive/d1", "/lem/drive"; "/tmp/lem/r/out/docs", "/lem/docs" ]
      bwrap |> pairsOf "--bind" |> Expect.equal "the socket directory" [ "/tmp/lem/r/out/ipc", "/tmp/lem/r/out/ipc" ]
      env |> Expect.equal "driver socket" [ "LEM_DRIVE_SOCKET", "/tmp/lem/r/out/ipc/drive.sock" ]

    testCase "git on a workspace the editor could write is pinned off from running programs" <| fun _ ->
      EditorSandbox.harnessGitPins |> Expect.contains "no fsmonitor" "core.fsmonitor=false"
      EditorSandbox.harnessGitPins |> Expect.contains "no hooks" "core.hooksPath=/dev/null"
  ]

[<Tests>]
let vscodeWindow =
  testList "the VS Code window (as data)" [
    testCase "the run directory is writable, with its evidence, tools and settings laid over read-only" <| fun _ ->
      let overlays = [ "/tmp/lem/r/out"; "/tmp/lem/r/bin"; "/tmp/lem/r/.lem/vsc/User/settings.json" ]
      let args = VscRun.windowArgs "/home/u" "/tmp/lem/r" ":81" [ "/store/drive/d1" ] overlays
      args |> pairsOf "--bind" |> Expect.equal "writable" [ "/tmp/lem/r", "/tmp/lem/r" ]
      let ro = args |> pairsOf "--ro-bind" |> List.map fst
      for p in overlays @ [ "/store/drive/d1"; "/tmp/.X11-unix/X81"; "/home/u/.dotnet" ] do ro |> Expect.contains p p
      let index (a: string) (b: string) = args |> List.pairwise |> List.findIndex (fun p -> p = (a, b))
      (index "--bind" "/tmp/lem/r" < index "--ro-bind" "/tmp/lem/r/out") |> Expect.isTrue "overlays come after the writable run directory"

    testCase "it sees only its own display, no Wayland and no real XDG directory" <| fun _ ->
      let args = VscRun.windowArgs "/home/u" "/tmp/lem/r" ":81" [] []
      let env = args |> pairsOf "--setenv"
      env |> Expect.contains "its display" ("DISPLAY", ":81")
      env |> Expect.contains "private home" ("HOME", "/tmp/lem/r/.lem/home")
      for name in [ "WAYLAND_DISPLAY"; "XDG_RUNTIME_DIR" ] do env |> List.exists (fun (k, _) -> k = name) |> Expect.isFalse name
      args |> Expect.contains "cleared" "--clearenv"
      args |> pairsOf "--ro-bind" |> List.map fst |> Expect.contains "only that display's socket" "/tmp/.X11-unix/X81"
      args |> pairsOf "--ro-bind" |> List.exists (fun (p, _) -> p = "/tmp/.X11-unix" || p = "/tmp/.X11-unix/X0") |> Expect.isFalse "never the real desktop's socket"

    testCase "VS Code opens the run's workspace with its own profile, the extension from this repo and a debug port" <| fun _ ->
      let args = VscRun.codeArgs "/tmp/lem/r" 39201 "/tmp/lem/r/.lem/ext/sagefs"
      args |> Expect.contains "profile" "--user-data-dir=/tmp/lem/r/.lem/vsc"
      args |> Expect.contains "extension" "--extensionDevelopmentPath=/tmp/lem/r/.lem/ext/sagefs"
      args |> Expect.contains "cdp" "--remote-debugging-port=39201"
      args |> Expect.contains "x11" "--ozone-platform=x11"
      List.last args |> Expect.equal "the workspace is last" "/tmp/lem/r/w"

    testCase "the profile is valid JSON, points the extension at the shared daemon and never lets it start one" <| fun _ ->
      use doc = JsonDocument.Parse(VscRun.settingsJson 37749 37750 None)
      doc.RootElement.GetProperty("sagefs.mcpPort").GetInt32() |> Expect.equal "mcp" 37749
      doc.RootElement.GetProperty("sagefs.dashboardPort").GetInt32() |> Expect.equal "dashboard" 37750
      doc.RootElement.GetProperty("sagefs.autoStart").GetBoolean() |> Expect.isFalse "autoStart off"
      doc.RootElement.GetProperty("terminal.integrated.defaultProfile.linux").GetString() |> Expect.equal "no terminal" "none"
      use extra = JsonDocument.Parse(VscRun.settingsJson 1 2 (Some "\"foo.bar\": 1"))
      extra.RootElement.GetProperty("foo.bar").GetInt32() |> Expect.equal "extra setting" 1

    testCase "the keys that open a terminal or the developer tools are unbound" <| fun _ ->
      use doc = JsonDocument.Parse VscRun.keybindingsJson
      let commands = [ for e in doc.RootElement.EnumerateArray() -> e.GetProperty("command").GetString() ]
      commands |> Expect.contains "terminal" "-workbench.action.terminal.toggleTerminal"
      commands |> Expect.contains "devtools" "-workbench.action.toggleDevTools"

    testCase "a tool is a one-line launcher the kernel runs, with no shell script behind it" <| fun _ ->
      VscRun.launcherStub [] [ "dotnet"; "/s/LemDrive.dll"; "vsc"; "click" ] |> Expect.equal "stub" "#!/usr/bin/env -S dotnet /s/LemDrive.dll vsc click\n"
      VscRun.launcherStub [ "LEM_SAGEFS_REAL", "dotnet /b/SageFs.dll" ] [ "dotnet"; "/s/LemDrive.dll"; "shim"; "sagefs" ]
      |> Expect.equal "shim" "#!/usr/bin/env -S LEM_SAGEFS_REAL=\"dotnet /b/SageFs.dll\" dotnet /s/LemDrive.dll shim sagefs\n"
      VscRun.toolVerbs |> Expect.equal "the verbs the tools doc names" [ "snapshot"; "click"; "key"; "type"; "palette"; "open"; "wait"; "shot" ]

    testCase "a window is on the real desktop only if it was not there before, counting duplicates" <| fun _ ->
      VscRun.newWindows [ "a"; "b" ] [ "a"; "b" ] |> Expect.isEmpty "none"
      VscRun.newWindows [ "a" ] [ "a"; "b" ] |> Expect.equal "one new" [ "b" ]
      VscRun.newWindows [ "a" ] [ "a"; "a" ] |> Expect.equal "a second window of the same title" [ "a" ]
      VscRun.newWindows [ "a"; "a" ] [ "a" ] |> Expect.isEmpty "a window closed is not a leak"

    testCase "a tour names its fixture in a comment, and asks for a second session on a line of its own" <| fun _ ->
      VscRun.tourFixture "# fixture: falco-hello\nwait 1\n" |> Expect.equal "named" "falco-hello"
      VscRun.tourFixture "# nothing\nwait 1\n" |> Expect.equal "default" "demoenv"
      VscRun.usesOtherSession "wait 1\nother-session\n" |> Expect.isTrue "yes"
      VscRun.usesOtherSession "# other-session in a comment\nwait 1\n" |> Expect.isFalse "a comment is not the command"

    testCase "ui-edit-reeval runs the suite afterwards, ui-find-help expects no session of its own" <| fun _ ->
      VscRun.taskKind "ui-edit-reeval" |> Expect.equal "suite" VscRun.RunsSuite
      VscRun.taskKind "ui-find-help" |> Expect.equal "no session" VscRun.NoSessionExpected
      VscRun.taskKind "ui-eval" |> Expect.equal "ordinary" VscRun.Ordinary
  ]

[<Tests>]
let neovimTasks =
  testList "the Neovim task settings" [
    testCase "a task's .env is read as NAME=value lines" <| fun _ ->
      let file = Path.Combine(Path.GetTempPath(), "lem-env-" + Guid.NewGuid().ToString("N") + ".env")
      try
        File.WriteAllText(file, "# comment\nLEM_FIXTURE=demoenv\nLEM_OPEN=DemoEnv/DemoEnv.fs\nLEM_README=yes\n")
        NvimRun.readTaskSettings file |> Expect.equal "settings" { Fixture = "demoenv"; OpenFile = "DemoEnv/DemoEnv.fs"; Readme = true }
        File.WriteAllText(file, "LEM_FIXTURE='falco-hello'\nLEM_OPEN=Program.fs\nLEM_README=no\n")
        NvimRun.readTaskSettings file |> Expect.equal "quotes and no" { Fixture = "falco-hello"; OpenFile = "Program.fs"; Readme = false }
      finally File.Delete file

    testCase "every shipped task has settings that name a fixture that exists" <| fun _ ->
      let tasks = Path.Combine(Env.lemDir, "ui", "nvim", "tasks")
      for env in Directory.GetFiles(tasks, "*.env") do
        let s = NvimRun.readTaskSettings env
        Directory.Exists(Path.Combine(Env.lemDir, "fixtures", s.Fixture)) |> Expect.isTrue (sprintf "%s names fixture %s" (Path.GetFileName env) s.Fixture)
        File.Exists(Path.Combine(Env.lemDir, "fixtures", s.Fixture, s.OpenFile)) |> Expect.isTrue (sprintf "%s opens %s" (Path.GetFileName env) s.OpenFile)
        File.Exists(Path.ChangeExtension(env, ".md")) |> Expect.isTrue "and has a prompt"

    testCase "the tour ids count up under the run root" <| fun _ ->
      NvimRun.tourId "x" |> fun id -> id.StartsWith "tour-x-" |> Expect.isTrue "named for the tour"
  ]
