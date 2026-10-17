/// A scan that cannot finish has to say so. The panel button is disabled while a scan runs, so a scan that never ends
/// leaves "Scanning..." on the screen with nothing able to clear it. Two things can make that happen: a git whose
/// output a child process keeps open after git itself has exited, and live facts that never arrive.
module SageFs.Tests.HygieneStallTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.HygieneGather
open SageFs.Tests.HygieneSandbox

let private scanFailureOf (repo: string) : string =
  match HygieneService.viewFor repo with
  | HygieneService.HygieneView.ScanFailed(_, reason) -> reason
  | other -> failtestf "the panel should show a failed scan, it shows %A" other

/// Wait for a refresh to end, however it ends, but not forever.
let private endsWithin (refresh: Task<HygieneService.Snapshot>) : Task<unit> =
  task {
    let! _ = Task.WhenAny(refresh :> Task, Task.Delay TestTimeouts.patience)
    ()
  }

[<Tests>]
let tests =
  testList "Workspace hygiene: a scan that cannot finish says so" [
    testTask "a git that exits but leaves a child holding its output is cut off at the bound" {
      match OperatingSystem.IsWindows() with
      | true -> skiptest "the fake git is a shell script"
      | false ->
        use sb = new Sandbox()
        let script = Path.Combine(sb.Root, "fake-git")
        // It prints, backgrounds a sleeper that inherits the output pipe, and exits at once. Git itself is gone and the pipe is not.
        let outlives = int TestTimeouts.stalledWorkBound.TotalSeconds + 3
        File.WriteAllText(script, sprintf "#!/bin/sh\nsleep %d &\necho done\nexit 0\n" outlives)
        File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        let git = runGitWith script TestTimeouts.stalledWorkBound
        let! result = git sb.Root [ "status" ]
        match result with
        | GitResult.Unavailable detail -> detail |> Expect.stringContains "says it ran out of time" "timed out"
        | other -> failtestf "git whose output stayed open should be reported unavailable, got %A" other
    }

    testTask "a scan whose live facts never arrive ends as a failed scan that says so" {
      use sb = new Sandbox()
      let loc = HygieneService.locationsFor sb.Repo
      let never () : Task<LiveFacts> = TaskCompletionSource<LiveFacts>().Task
      do! endsWithin (HygieneService.Cache.refreshAsyncWithin TestTimeouts.stalledWorkBound loc never)
      scanFailureOf sb.Repo |> Expect.stringContains "says the scan did not finish" "did not finish"
    }

    testTask "a scan whose live facts block their thread ends as a failed scan too" {
      use sb = new Sandbox()
      let loc = HygieneService.locationsFor sb.Repo
      let release = new ManualResetEventSlim(false)
      // The block is the subject: a provider that holds its thread is what the scan must not be held by. It lasts until the
      // `finally` below releases it, so the case costs one pool thread for the length of the bound.
      let blocked () : Task<LiveFacts> =
        release.WaitHandle.WaitOne() |> ignore
        Task.FromResult(Unchecked.defaultof<LiveFacts>)
      try
        do! endsWithin (HygieneService.Cache.refreshAsyncWithin TestTimeouts.stalledWorkBound loc blocked)
        scanFailureOf sb.Repo |> Expect.stringContains "says the scan did not finish" "did not finish"
      finally
        release.Set()
        release.Dispose()
    }
  ]
