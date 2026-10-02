module NvimTests.IsolationTests

/// WHY: a Neovim can run a shell (`:!`, `:terminal`, `:lua os.execute`, `!!sh`), and no key
/// parser can allow-list that away, so what a shell inside the editor can reach is decided by the
/// editor sandbox's mounts. A probe tour once ran `:!touch ../bin/lemdrive/X` and
/// `:!touch .git/hooks/X` as the user: the sandbox bound the whole run directory read-write, and the
/// harness later runs `dotnet <run>/bin/lemdrive/LemDrive.dll` and `git -C <w> diff` OUTSIDE every
/// sandbox. These tests start the real sandbox arguments (EditorSandbox.args, which LemRun starts the
/// editor with), run the writes a hostile `:!` would, and read back what reached the host.
open System
open System.IO
open Expecto
open Expecto.Flip
open LemDrive

let private bwrapUsable =
  let probe =
    Nvim.runProcess
      "bwrap"
      [ "--ro-bind"; "/usr"; "/usr"; "--symlink"; "usr/bin"; "/bin"; "--symlink"; "usr/lib"; "/lib"; "--symlink"; "usr/lib64"; "/lib64"; "true" ]
      []
      10_000
  match probe with
  | Result.Ok o -> o.ExitCode = 0
  | Result.Error _ -> false

/// A run directory shaped like the harness's, with a driver the harness would later execute.
type private RunTree =
  { Run: string
    Workspace: string
    Out: string
    NvimBin: string
    Plugin: string }

let private makeRunTree () : RunTree =
  let run = Path.Combine(Path.GetTempPath(), "lem-iso-" + Guid.NewGuid().ToString("N").Substring(0, 8))
  let dir (p: string) = Directory.CreateDirectory(Path.Combine(run, p)) |> ignore
  for d in [ "bin/lemdrive"; "w/.git/hooks"; "w/src"; "out/ui"; "out/tmux"; "bob/nvim-bin"; "plugin" ] do dir d
  File.WriteAllText(Path.Combine(run, "bin/lemdrive/LemDrive.dll"), "the driver")
  File.WriteAllText(Path.Combine(run, "w/.git/config"), "[core]\n")
  File.WriteAllText(Path.Combine(run, "bob/nvim-bin/nvim"), "")
  { Run = run
    Workspace = Path.Combine(run, "w")
    Out = Path.Combine(run, "out")
    NvimBin = Path.Combine(run, "bob/nvim-bin/nvim")
    Plugin = Path.Combine(run, "plugin") }

/// Runs `probe` (a shell script: the hostile `:!` this sandbox exists to contain) inside the editor
/// sandbox and returns what it printed.
let private inEditorSandbox (tree: RunTree) (probe: string) : string =
  let editor : EditorSandbox.Editor =
    { Home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
      Workspace = tree.Workspace
      Out = tree.Out
      NvimBin = tree.NvimBin
      PluginDir = tree.Plugin
      DaemonPort = 37749
      HasGit = true }
  match Nvim.runProcess "bwrap" (EditorSandbox.args editor @ [ "bash"; "-c"; probe ]) [] 60_000 with
  | Result.Ok o -> o.Output
  | Result.Error e -> failwithf "the sandbox probe did not run: %s" e

[<Tests>]
let editorSandbox =
  let guarded (name: string) (body: RunTree -> unit) =
    testCase name <| fun _ ->
      if not bwrapUsable then skiptest "bubblewrap cannot start here"
      let tree = makeRunTree ()
      try body tree
      finally try Directory.Delete(tree.Run, true) with _ -> ()

  testList "the editor sandbox (what a shell inside Neovim can reach)" [
    guarded "writes to the driver the harness runs later, to .git and to the run's evidence never reach the host" <| fun tree ->
      let probe =
        String.concat
          "\n"
          [ "w() { if sh -c \"$2\" 2>/dev/null; then echo \"$1 wrote\"; else echo \"$1 denied\"; fi; }"
            sprintf "w workspace 'echo x > %s/src/new.fs'" tree.Workspace
            sprintf "w marker-in-driver-dir 'echo x > %s/bin/lemdrive/PROBE-MARKER'" tree.Run
            sprintf "w replace-the-driver 'echo evil > %s/bin/lemdrive/LemDrive.dll'" tree.Run
            sprintf "w git-hook 'echo x > %s/.git/hooks/PROBE-MARKER'" tree.Workspace
            sprintf "w git-config 'echo [core] >> %s/.git/config'" tree.Workspace
            sprintf "w evidence 'echo x > %s/PROBE-MARKER'" tree.Out ]
      let report = inEditorSandbox tree probe

      // The editor's own job still works.
      report |> Expect.stringContains "the workspace is writable" "workspace wrote"
      File.Exists(Path.Combine(tree.Workspace, "src/new.fs")) |> Expect.isTrue "and the file is on the host"

      // Nothing else reached the host, whatever the shell inside said.
      File.ReadAllText(Path.Combine(tree.Run, "bin/lemdrive/LemDrive.dll")) |> Expect.equal "the driver is untouched" "the driver"
      File.Exists(Path.Combine(tree.Run, "bin/lemdrive/PROBE-MARKER")) |> Expect.isFalse "no file next to the driver"
      File.Exists(Path.Combine(tree.Workspace, ".git/hooks/PROBE-MARKER")) |> Expect.isFalse "no git hook"
      File.ReadAllText(Path.Combine(tree.Workspace, ".git/config")) |> Expect.equal "the repository config is untouched" "[core]\n"
      File.Exists(Path.Combine(tree.Out, "PROBE-MARKER")) |> Expect.isFalse "no file among the run's evidence"

      // And the places are not even there to look at.
      report |> Expect.stringContains "the driver directory is read-only or absent, not writable" "replace-the-driver denied"
      report |> Expect.stringContains "git is read-only" "git-hook denied"

    guarded "the run's bin, dotnet home and the user's home are not mounted at all" <| fun tree ->
      let report =
        inEditorSandbox
          tree
          (String.concat
            "\n"
            [ sprintf "test -e '%s/bin' && echo bin-visible || echo bin-absent" tree.Run
              sprintf "test -e '%s/.nuget' && echo nuget-visible || echo nuget-absent" (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)
              sprintf "test -e '%s/.ssh' && echo ssh-visible || echo ssh-absent" (Environment.GetFolderPath Environment.SpecialFolder.UserProfile) ])
      report |> Expect.stringContains "the driver's directory is absent" "bin-absent"
      report |> Expect.stringContains "the NuGet cache is absent" "nuget-absent"
      report |> Expect.stringContains "the user's ssh directory is absent" "ssh-absent"
  ]

[<Tests>]
let harnessGit =
  testList "git run by the harness on the editor's workspace" [
    testCase "a repository that names an fsmonitor program does not get to run it" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "lem-git-" + Guid.NewGuid().ToString("N").Substring(0, 8))
      Directory.CreateDirectory dir |> ignore
      try
        let git (pins: string list) (args: string list) =
          match Nvim.runProcess "git" (pins @ [ "-C"; dir ] @ args) [ ("GIT_CONFIG_GLOBAL", "/dev/null") ] 30_000 with
          | Result.Ok o -> o
          | Result.Error e -> failwithf "git did not run: %s" e
        git [] [ "init"; "-q" ] |> ignore
        git [] [ "config"; "user.email"; "a@b.invalid" ] |> ignore
        git [] [ "config"; "user.name"; "n" ] |> ignore
        File.WriteAllText(Path.Combine(dir, "f"), "a")
        git [] [ "add"; "f" ] |> ignore
        git [] [ "commit"; "-q"; "-m"; "i" ] |> ignore
        let marker = Path.Combine(dir, "FSMONITOR-RAN")
        let hook = Path.Combine(dir, "hook.sh")
        File.WriteAllText(hook, sprintf "#!/bin/sh\ntouch '%s'\nprintf '\\000'\n" marker)
        File.SetUnixFileMode(hook, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        git [] [ "config"; "core.fsmonitor"; hook ] |> ignore

        // The control: without the pins the program runs, so the test below proves something.
        git [] [ "status" ] |> ignore
        File.Exists marker |> Expect.isTrue "control: an unpinned git runs the fsmonitor program"
        File.Delete marker

        git Nvim.harnessGitPins [ "status" ] |> ignore
        git Nvim.harnessGitPins [ "diff"; "--name-only" ] |> ignore
        File.Exists marker |> Expect.isFalse "the pinned git does not run it"
      finally
        try Directory.Delete(dir, true) with _ -> ()
  ]
