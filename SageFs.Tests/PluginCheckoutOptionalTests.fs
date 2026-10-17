/// sagefs.nvim is a separate repository that the SSE parity tests read. A contributor with a fresh clone and no plugin next
/// to it used to get no tests at all: the files were read while Expecto was still discovering, the read threw, and the
/// assembly died with a stack trace and no hint that a second repository was needed. A missing checkout is now a handful of
/// failing tests that say what to do, and every other test still runs.
module SageFs.Tests.PluginCheckoutOptionalTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip

/// Only here so the child process can be pointed at this test assembly.
type private Marker = class end

/// Run this very assembly in a child process with sagefs.nvim pointed at a folder that is not there. Both streams are
/// drained as they arrive: a listing of every test is large, and a pipe nobody reads blocks the child.
let private runWithoutPlugin (arguments: string list) : Task<int * string> =
  task {
    let dll = typeof<Marker>.Assembly.Location
    let psi = ProcessStartInfo("dotnet")
    psi.ArgumentList.Add dll
    for argument in arguments do psi.ArgumentList.Add argument
    psi.Environment["SAGEFS_NVIM_DIR"] <- Path.Combine(Path.GetTempPath(), "no-sagefs-nvim-" + Guid.NewGuid().ToString "N")
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    let proc = Process.Start psi
    try
      let output = proc.StandardOutput.ReadToEndAsync()
      let errors = proc.StandardError.ReadToEndAsync()
      use bound = new CancellationTokenSource(TestTimeouts.childExit)
      do! proc.WaitForExitAsync bound.Token
      let! text = output
      let! errorText = errors
      return proc.ExitCode, text + errorText
    finally
      try if not proc.HasExited then proc.Kill(true) with _ -> ()
      try proc.Dispose() with _ -> ()
  }

let private lastLines (text: string) : string =
  let lines = text.Split('\n')
  String.Join("\n", lines |> Array.skip (max 0 (lines.Length - 8)))

[<Tests>]
let tests =
  testList "A missing sagefs.nvim checkout" [
    testTask "does not stop the test assembly from being discovered" {
      let! exitCode, output = runWithoutPlugin [ "--list-tests" ]
      exitCode |> Expect.equal (sprintf "discovery lists the tests and exits cleanly; the end of its output was:\n%s" (lastLines output)) 0
      output
      |> Expect.stringContains "the Neovim parity tests are still listed, so they can fail with a message and not vanish" "Neovim handles every daemon SSE event"
    }

    testTask "fails the tests that need it, and says how to fix that" {
      let! exitCode, output = runWithoutPlugin [ "--summary"; "--filter-test-list"; "Neovim handles every daemon SSE event" ]
      (exitCode <> 0) |> Expect.isTrue "the Neovim parity tests fail when the plugin is not there"
      output |> Expect.stringContains "the failure names the variable that fixes it" "SAGEFS_NVIM_DIR"
      output |> Expect.stringContains "the failure names the repository to clone" "WillEhrendreich/sagefs.nvim"
    }
  ]
