/// Proves `Runtime.Neovim.fs` genuinely — real PATH resolution for
/// `kitty`/`nvim`, and a real `git archive | tar -x` pin of the ACTUAL
/// `~/Work/sagefs.nvim` checkout on this box, never a fake/in-memory stand-
/// in. This is the concrete proof behind demo-actors-plan.md §7.5's caution:
/// `~/Work/sagefs.nvim` has the unpushed `feat/coverage-review-view` branch
/// checked out while this test runs (confirmed directly before writing this
/// file: `git branch -vv` shows it as the current branch) — every assertion
/// below is that pinning to `master` neither reads nor disturbs that
/// checked-out branch or its working tree.
module SageFs.Demos.Tests.RuntimeNeovimTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Demos.Runtime.Neovim

let private pluginRepoDir = Environment.GetEnvironmentVariable "HOME" |> fun home -> Path.Combine(home, "Work", "sagefs.nvim")

let private runGit (args: string list) : int * string =
  let psi = ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)

  for a in args do
    psi.ArgumentList.Add a

  use proc = new Process(StartInfo = psi)
  proc.Start() |> ignore
  let stdout = proc.StandardOutput.ReadToEnd()
  proc.WaitForExit()
  proc.ExitCode, stdout.Trim()

let private pluginRepoAvailable () : bool = Directory.Exists(Path.Combine(pluginRepoDir, ".git"))

[<Tests>]
let tests =
  testList "Runtime.Neovim" [

    testCase "resolveKitty finds the real kitty binary on this box's PATH" <| fun _ ->
      match resolveKitty () with
      | Ok path -> File.Exists path |> Expect.isTrue (sprintf "resolved path %s actually exists" path)
      | Error message -> failtestf "expected Ok, got Error %s (kitty is confirmed installed on this box — /usr/bin/kitty)" message

    testCase "resolveNvim finds the real nvim binary on this box's PATH" <| fun _ ->
      match resolveNvim () with
      | Ok path -> File.Exists path |> Expect.isTrue (sprintf "resolved path %s actually exists" path)
      | Error message -> failtestf "expected Ok, got Error %s (nvim is confirmed installed on this box — /usr/bin/nvim)" message

    testCase "actorBinds RO-binds the plugin scratch dir at the SAME absolute path on both sides (the Runtime.fs fixed-bind pattern), and nothing else" <| fun _ ->
      let scratch = "/tmp/sagefs-demos-plugin-scratch-example"
      actorBinds scratch |> Expect.equal "one bind, host path == cell path" [ scratch, scratch ]

    testCase "actorPrologue is empty — Actors.Neovim.launch owns the whole kitty+nvim spawn itself, exactly like the Dashboard actor's Chromium launch (Island F's own precedent)" <| fun _ -> actorPrologue |> Expect.isEmpty "no shell-level prologue needed"

    testCase "nvimConfig carries the resolved commit through, never the plugin repo's currently-checked-out branch" <| fun _ ->
      let cfg = nvimConfig "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"
      cfg.PluginCommit |> Expect.equal "resolved sha, verbatim" (Some "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef")

    testCase "resolvePinnedPlugin resolves 'master' to the SAME sha as a direct `git rev-parse master` — and never touches the live working tree's own checked-out branch" <| fun _ ->
      if not (pluginRepoAvailable ()) then
        failtestf "expected ~/Work/sagefs.nvim to be a real git checkout on this box (confirmed present when this test was written) — got no .git at %s" pluginRepoDir
      else

      let branchCode, branchBefore = runGit [ "-C"; pluginRepoDir; "rev-parse"; "--abbrev-ref"; "HEAD" ]
      branchCode |> Expect.equal "git rev-parse HEAD succeeds" 0

      let scratch = Path.Combine(Path.GetTempPath(), sprintf "sagefs-demos-plugin-pin-test-%s" (Guid.NewGuid().ToString("N")))

      try
        match resolvePinnedPlugin pluginRepoDir scratch "master" |> Async.RunSynchronously with
        | Error message -> failtestf "expected Ok, got Error %s" message
        | Ok(sha, dir) ->
          let expectedCode, expectedSha = runGit [ "-C"; pluginRepoDir; "rev-parse"; "master" ]
          expectedCode |> Expect.equal "git rev-parse master succeeds" 0
          sha |> Expect.equal "resolvePinnedPlugin pins the exact same sha `git rev-parse master` reports" expectedSha
          dir |> Expect.equal "returns the scratch dir it was given" scratch

          File.Exists(Path.Combine(dir, "lua", "sagefs", "init.lua"))
          |> Expect.isTrue "the pinned checkout genuinely materialized the plugin's real Lua source (git archive really extracted it)"

          File.Exists(Path.Combine(dir, "lua", "sagefs", "commands.lua"))
          |> Expect.isTrue "commands.lua (SageFsEval/SageFsCreateSession/...) is present in the pinned checkout"

          // The whole point of `git archive`, proven directly: the live
          // repo's own checked-out branch is completely undisturbed.
          let branchCodeAfter, branchAfter = runGit [ "-C"; pluginRepoDir; "rev-parse"; "--abbrev-ref"; "HEAD" ]
          branchCodeAfter |> Expect.equal "git rev-parse HEAD still succeeds" 0
          branchAfter |> Expect.equal "the live repo's checked-out branch is byte-for-byte unchanged after pinning master via git archive" branchBefore
      finally
        try
          Directory.Delete(scratch, true)
        with _ ->
          ()

    testCase "resolvePinnedPlugin fails loud (Error), never Ok with garbage, for an unresolvable ref" <| fun _ ->
      if not (pluginRepoAvailable ()) then
        failtestf "expected ~/Work/sagefs.nvim to be a real git checkout on this box — got no .git at %s" pluginRepoDir
      else

      let scratch = Path.Combine(Path.GetTempPath(), sprintf "sagefs-demos-plugin-pin-test-bad-%s" (Guid.NewGuid().ToString("N")))

      try
        match resolvePinnedPlugin pluginRepoDir scratch "not-a-real-ref-anywhere" |> Async.RunSynchronously with
        | Error message -> message |> Expect.isNotEmpty "an actionable error message"
        | Ok(sha, _) -> failtestf "expected Error for an unresolvable ref, got Ok %s" sha
      finally
        try
          Directory.Delete(scratch, true)
        with _ ->
          ()
  ]
