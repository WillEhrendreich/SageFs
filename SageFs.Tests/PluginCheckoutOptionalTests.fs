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
/// Every variable the trust ledger machinery reads begins with this. A child that inherited one would record its own
/// row in the real ledger.
[<Literal>]
let TrustVariablePrefix = "SAGEFS_TRUST_"

/// The start info with the trust variables taken out of its environment.
let withoutTrustVariables (psi: ProcessStartInfo) : ProcessStartInfo =
  let trust = psi.Environment.Keys |> Seq.filter (fun key -> key.StartsWith(TrustVariablePrefix, StringComparison.Ordinal)) |> List.ofSeq
  for key in trust do psi.Environment.Remove key |> ignore
  psi

let childStartInfo (dll: string) (arguments: string list) : ProcessStartInfo =
  let psi = ProcessStartInfo("dotnet")
  psi.ArgumentList.Add dll
  for argument in arguments do psi.ArgumentList.Add argument
  psi.Environment["SAGEFS_NVIM_DIR"] <- Path.Combine(Path.GetTempPath(), "no-sagefs-nvim-" + Guid.NewGuid().ToString "N")
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  withoutTrustVariables psi

let private runWithoutPlugin (arguments: string list) : Task<int * string> =
  task {
    let psi = childStartInfo (typeof<Marker>.Assembly.Location) arguments
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

    testCase "WHY — the child never sees the trust ledger, because a nested run appends its own row to the real ledger and can show a phantom red tier" <| fun _ ->
      // A start info copies the parent's environment when it is built, so a parent that holds the ledger variable
      // is modelled by putting it in the info, the way the copy would have.
      let inherited = ProcessStartInfo("dotnet")
      inherited.Environment[TestInfrastructure.TrustSignal.LedgerEnvironmentVariable] <- "/tmp/never-the-real-ledger"
      inherited.Environment["SAGEFS_TRUST_ANOTHER"] <- "also a trust variable"
      inherited.Environment["SAGEFS_NVIM_DIR"] <- "not a trust variable"
      let child = withoutTrustVariables inherited
      child.Environment.Keys
      |> Seq.filter (fun key -> key.StartsWith TrustVariablePrefix)
      |> List.ofSeq
      |> Expect.isEmpty "no SAGEFS_TRUST_ variable reaches the child"
      child.Environment["SAGEFS_NVIM_DIR"] |> Expect.equal "a variable that is not a trust variable is kept" "not a trust variable"

    testCase "WHY — the child start info this file builds carries no trust variable, because the strip is applied where the child is made" <| fun _ ->
      (childStartInfo "SageFs.Tests.dll" [ "--summary" ]).Environment.Keys
      |> Seq.filter (fun key -> key.StartsWith TrustVariablePrefix)
      |> List.ofSeq
      |> Expect.isEmpty "none of the child's variables starts with SAGEFS_TRUST_"
  ]
