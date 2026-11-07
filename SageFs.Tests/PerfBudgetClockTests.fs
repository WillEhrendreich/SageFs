/// A perf budget claims what the code costs. On a busy machine, wall time also holds the time the thread spent
/// waiting for a core, which says nothing about the code: one `Cohort perf budgets` case failed once at 35.9 s in a
/// default run beside other work, with the code unchanged. `PerfBudget.nowMs` reads the calling thread's CPU time
/// where the host has it, so the budgets in this suite judge the work and not the load.
module SageFs.Tests.PerfBudgetClockTests

open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open Expecto
open Expecto.Flip

[<Tests>]
let tests =
  testList "Perf budget clock" [

    testCase "WHY — on Linux and macOS the budgets read the thread's CPU time, so a busy machine cannot fail a budget on work that did not change" <| fun _ ->
      let hasThreadClock = RuntimeInformation.IsOSPlatform OSPlatform.Linux || RuntimeInformation.IsOSPlatform OSPlatform.OSX
      let expected = match hasThreadClock with | true -> PerfBudget.ThreadCpu | false -> PerfBudget.Wall
      PerfBudget.clock |> Expect.equal (sprintf "the clock the budgets read (%s)" (PerfBudget.describe expected)) expected

    testCase "WHY — a thread that waits has used no CPU, so the time it spends waiting for a core is not charged to a budget, while a wall clock charges all of it" <| fun _ ->
      let waitedMs = TestTimeouts.cpuClockWait.TotalMilliseconds
      use gate = new ManualResetEventSlim(false)
      let wallStart = Stopwatch.GetTimestamp ()
      let before = PerfBudget.nowMs ()
      gate.Wait TestTimeouts.cpuClockWait |> ignore
      let used = PerfBudget.nowMs () - before
      let wall = Stopwatch.GetElapsedTime(wallStart).TotalMilliseconds
      // The twin: the wall clock saw the wait, so a budget on it would have been charged for the wait.
      Expect.isGreaterThanOrEqual "a wall clock reads the wait" (wall, waitedMs * 0.9)
      match PerfBudget.clock with
      | PerfBudget.ThreadCpu -> Expect.isLessThan (sprintf "the CPU clock read %.2f ms of a %.0f ms wait" used waitedMs) (used, waitedMs / 4.0)
      | PerfBudget.Wall -> Expect.isGreaterThanOrEqual "on a host without a thread clock the budget reads wall time, which holds the wait" (used, waitedMs * 0.9)

    testCase "WHY — work the thread does is on its clock, so a budget can still fail" <| fun _ ->
      let mutable sink = 0.0
      let before = PerfBudget.nowMs ()
      for i in 1 .. TestMagnitudes.clockSpinIterations do
        sink <- sink + sqrt (float i)
      let used = PerfBudget.nowMs () - before
      Expect.isGreaterThan (sprintf "spinning read %.4f ms (sink %f)" used sink) (used, 0.0)
  ]
