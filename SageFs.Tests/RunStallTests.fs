module SageFs.Tests.RunStallTests

/// `RunStall` (`SageFs.Core/Features/RunStall.fs`): when a test run is over because it stopped reporting, and when it is not over
/// because it is slow. The worker used to cancel a run two minutes after it started and the landing gate gave up two and a half
/// minutes after it asked, so a run that was still reporting was cut at a clock (one cohort run: 19067 tests asked for, 18529
/// never reported). These cases pin the judgement, the loop around it, and the watch that cancels a real run. The simulation in
/// `RunStallSimTests` folds the same loop through virtual time.
open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTesting.RunStall

let window : TimeSpan = Timeouts.testRunStallWindow ()

let reporting (reported: int) (requested: int) : Look = Look.Reporting (reported, requested)

/// Fold `judge` over looks one slice apart, stopping at the first verdict that is not KeepWaiting. The index of the look that
/// ended it, and the verdict.
let judgeAlong (looks: Look list) : (int * Verdict) option =
  looks
  |> List.mapi (fun i look -> i, look)
  |> List.fold
    (fun (state: Look option * (int * Verdict) option) (i, look) ->
      match state with
      | _, Some _ -> state
      | previous, None ->
        match judge previous look with
        | Verdict.KeepWaiting -> Some look, None
        | verdict -> Some look, Some (i, verdict))
    (None, None)
  |> snd

let simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

[<Tests>]
let judgeTests =
  testList "RunStall.judge" [

    testCase "WHY — the first look never calls a run stalled, because there is nothing to compare it with" <| fun _ ->
      judge None Look.NotStarted |> Expect.equal "not started" Verdict.KeepWaiting
      judge None (reporting 0 10) |> Expect.equal "reporting nothing yet" Verdict.KeepWaiting
      judge None (reporting 9 10) |> Expect.equal "reporting nearly all" Verdict.KeepWaiting

    testCase "WHY — a count that rose across a window is progress, however small the rise" <| fun _ ->
      judge (Some (reporting 3 10)) (reporting 4 10) |> Expect.equal "one more reported" Verdict.KeepWaiting

    testCase "WHY — the same count a window later is a stall, and it names how many reported and how many did not" <| fun _ ->
      judge (Some (reporting 3 10)) (reporting 3 10)
      |> Expect.equal "stalled with the counts" (Verdict.Stalled (StallCause.StoppedReporting (3, 7)))

    testCase "WHY — a count that fell is no progress either (a result map replaced under the run)" <| fun _ ->
      judge (Some (reporting 5 10)) (reporting 4 10)
      |> Expect.equal "stalled" (Verdict.Stalled (StallCause.StoppedReporting (4, 6)))

    testCase "WHY — a run that never started is a stall at the second look, and one that starts in between is progress" <| fun _ ->
      judge (Some Look.NotStarted) Look.NotStarted |> Expect.equal "never started" (Verdict.Stalled StallCause.NeverStarted)
      judge (Some Look.NotStarted) (reporting 0 10) |> Expect.equal "started, nothing yet" Verdict.KeepWaiting

    testCase "WHY — every requested test reported and the completion never came is still a stall, with nothing left unreported" <| fun _ ->
      judge (Some (reporting 10 10)) (reporting 10 10)
      |> Expect.equal "stalled with none unreported" (Verdict.Stalled (StallCause.StoppedReporting (10, 0)))

    testCase "WHY — a finished run is Finished at any look, with or without a previous one" <| fun _ ->
      judge None Look.Finished |> Expect.equal "first look" Verdict.Finished
      judge (Some (reporting 3 10)) Look.Finished |> Expect.equal "after reporting" Verdict.Finished
      judge (Some Look.NotStarted) Look.Finished |> Expect.equal "after not started" Verdict.Finished

    testPropertyWithConfig simConfig
      "a run is never ended while its count rises, and always ended at the first look that does not rise"
      <| fun (counts: NonNegativeInt list) ->
        let counts = counts |> List.map (fun (NonNegativeInt n) -> n)
        match counts with
        | [] -> ()
        | _ ->
          let requested = List.max counts + 3
          let looks = counts |> List.map (fun reported -> reporting reported requested)
          let firstQuiet = counts |> List.pairwise |> List.tryFindIndex (fun (earlier, later) -> later <= earlier) |> Option.map ((+) 1)
          match judgeAlong looks, firstQuiet with
          | None, None -> ()
          | Some (index, Verdict.Stalled (StallCause.StoppedReporting (reported, unreported))), Some expectedIndex ->
            index |> Expect.equal "the look that ended it is the first that did not rise" expectedIndex
            (reported, unreported) |> Expect.equal "the counts it names" (counts[index], requested - counts[index])
          | ended, expected -> failtestf "looks %A: ended %A but the first quiet look is %A" counts ended expected

    testPropertyWithConfig simConfig
      "a strictly rising count is never called stalled, however many windows it goes on for"
      <| fun (steps: PositiveInt list) ->
        let counts = steps |> List.map (fun (PositiveInt n) -> n) |> List.scan (+) 0
        let requested = List.last counts + 1
        judgeAlong (counts |> List.map (fun reported -> reporting reported requested))
        |> Expect.isNone "never ended"

    testPropertyWithConfig simConfig
      "after any run of looks that rose, one quiet look ends it"
      <| fun (steps: PositiveInt list) ->
        let counts = steps |> List.map (fun (PositiveInt n) -> n) |> List.scan (+) 1
        let last = List.last counts
        let requested = last + 5
        let looks = (counts @ [ last ]) |> List.map (fun reported -> reporting reported requested)
        judgeAlong looks
        |> Expect.equal "ended at the repeated look, naming the count" (Some (counts.Length, Verdict.Stalled (StallCause.StoppedReporting (last, 5))))
  ]

[<Tests>]
let windowTests =
  testList "RunStall.effectiveWindow" [

    testCase "WHY — a test may take the whole per-test timeout to report, so the window is never under twice that" <| fun _ ->
      let perTest = window + window
      effectiveWindow window perTest |> Expect.equal "stretched to twice the per-test timeout" (perTest + perTest)

    testCase "a stall window already past twice the per-test timeout is kept as it is" <| fun _ ->
      let perTest = TimeSpan.FromTicks (window.Ticks / 4L)
      effectiveWindow window perTest |> Expect.equal "kept" window

    testCase "the boundary: a window exactly twice the per-test timeout is kept" <| fun _ ->
      let perTest = TimeSpan.FromTicks (window.Ticks / 2L)
      effectiveWindow window perTest |> Expect.equal "kept at the boundary" window
  ]

/// Ports over a script of looks, one per slice: parking never finishes the run, observing reads the next look.
let scriptedPorts (looks: Look list) : Ports * (unit -> int) =
  let remaining = ref looks
  let seen = ref 0
  let observe () =
    async {
      match remaining.Value with
      | next :: rest ->
        remaining.Value <- rest
        seen.Value <- seen.Value + 1
        return next
      | [] -> return Look.Finished
    }
  { Park = (fun _ -> async { return false }); Observe = observe }, (fun () -> seen.Value)

[<Tests>]
let awaitTests =
  testList "RunStall.await" [

    testAsync "WHY — a run that reports in every window is waited for through any number of windows, then ends when it finishes" {
      let looks = [ for reported in 1 .. 500 -> reporting reported 1000 ] @ [ Look.Finished ]
      let ports, looksTaken = scriptedPorts looks
      let! outcome = await window ports
      outcome |> Expect.equal "finished" Outcome.Finished
      looksTaken () |> Expect.equal "every look was taken" 501
    }

    testAsync "WHY — a run whose count stops is ended at the second look with the same count, naming the window" {
      let looks = [ reporting 1 10; reporting 2 10; reporting 2 10; reporting 3 10 ]
      let ports, looksTaken = scriptedPorts looks
      let! outcome = await window ports
      outcome |> Expect.equal "stalled" (Outcome.Stalled (StallCause.StoppedReporting (2, 8), window))
      looksTaken () |> Expect.equal "it stopped looking once it decided" 3
    }

    testAsync "a finish while parked ends the wait without a look" {
      let looked = ref false
      let ports : Ports =
        { Park = (fun _ -> async { return true })
          Observe = (fun () -> async { looked.Value <- true; return reporting 0 1 }) }
      let! outcome = await window ports
      outcome |> Expect.equal "finished" Outcome.Finished
      looked.Value |> Expect.isFalse "no look was needed"
    }

    testAsync "the window handed to the park is the window given" {
      let parked = ResizeArray<TimeSpan>()
      let ports : Ports =
        { Park = (fun slice -> async { parked.Add slice; return false })
          Observe = (fun () -> async { return Look.Finished }) }
      let! _ = await window ports
      parked |> Seq.toList |> Expect.equal "one park of one window" [ window ]
    }
  ]

[<Tests>]
let describeTests =
  testList "RunStall.describeCause" [
    testCase "WHY — a stall names how many reported and how many did not, so a landing report can say which kind of failure it is" <| fun _ ->
      let said = describeCause window (StallCause.StoppedReporting (538, 18529))
      said |> Expect.stringContains "the count that reported" "538 reported"
      said |> Expect.stringContains "the count that did not" "18529 did not"

    testCase "a run that never started says so, and says nothing about a count" <| fun _ ->
      describeCause window StallCause.NeverStarted |> Expect.stringContains "never started" "did not start"
      unreportedOf StallCause.NeverStarted |> Expect.isNone "no count to name"

    testCase "unreportedOf is the count the cause names" <| fun _ ->
      unreportedOf (StallCause.StoppedReporting (3, 7)) |> Expect.equal "seven" (Some 7)

    testCase "every test reported and the run did not end is told as that, not as tests that did not report" <| fun _ ->
      describeCause window (StallCause.StoppedReporting (10, 0)) |> Expect.stringContains "all reported" "all 10 tests had reported"
  ]

// -- The watch around a real run --

/// A slice the test completes itself: `Sleep` hands each park a task and queues it here, so a test decides when a window ends
/// and no test waits on a clock.
type Slices() =
  let queue = Channel.CreateUnbounded<TaskCompletionSource<unit>>()
  member _.Sleep (_: TimeSpan) (_: CancellationToken) : Task =
    let slice = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    queue.Writer.TryWrite slice |> ignore
    slice.Task :> Task
  /// The next park's slice, once the watch has parked for it. A wait on that event, bounded by a ceiling a passing run never reaches.
  member _.NextPark () : Async<TaskCompletionSource<unit>> =
    task {
      use ceiling = new CancellationTokenSource(TestTimeouts.patienceInProcess)
      try
        return! queue.Reader.ReadAsync(ceiling.Token).AsTask()
      with :? OperationCanceledException ->
        return failtest "the watch did not park for a slice"
    }
    |> Async.AwaitTask
  member this.EndWindow () : Async<unit> =
    async {
      let! slice = this.NextPark ()
      slice.SetResult ()
    }
  /// Waits until the watch has parked for another slice, without taking it. The watch parks again only after it has looked at the
  /// count, so a test that makes its next report after this cannot have it counted in the look that ended the last window.
  member _.Parked () : Async<unit> =
    task {
      use ceiling = new CancellationTokenSource(TestTimeouts.patienceInProcess)
      try
        let! _ = queue.Reader.WaitToReadAsync(ceiling.Token).AsTask()
        return ()
      with :? OperationCanceledException ->
        return failtest "the watch did not park again"
    }
    |> Async.AwaitTask

let waitFor (running: Task<'a>) : Async<'a> =
  async {
    let! finished = Task.WhenAny(running :> Task, Task.Delay TestTimeouts.patienceInProcess) |> Async.AwaitTask
    match Object.ReferenceEquals(finished, running) with
    | true -> return! Async.AwaitTask running
    | false -> return failtest "the watch did not end"
  }

let signal () : TaskCompletionSource<unit> =
  TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let waitForSignal (what: string) (reached: TaskCompletionSource<unit>) : Async<unit> =
  async {
    let! finished = Task.WhenAny(reached.Task :> Task, Task.Delay TestTimeouts.patienceInProcess) |> Async.AwaitTask
    match Object.ReferenceEquals(finished, reached.Task) with
    | true -> ()
    | false -> failtestf "never reached: %s" what
  }

/// A task that completes when `token` is cancelled, as a hung test that gives up when the run is cancelled does.
let whenCancelled (token: CancellationToken) : Task =
  let cancelled = signal ()
  token.Register(fun () -> cancelled.TrySetResult () |> ignore) |> ignore
  cancelled.Task :> Task

[<Tests>]
let watchTests =
  testList "RunStall.watchWith" [

    testAsync "WHY — a run that stops reporting is cancelled, the work sees the cancel, and the stall names the count that did not report" {
      let slices = Slices()
      let reportedThree = signal ()
      let sawCancel = ref false
      let work (reported: unit -> unit) (token: CancellationToken) : Async<unit> =
        async {
          reported (); reported (); reported ()
          reportedThree.SetResult ()
          do! Async.AwaitTask (whenCancelled token)
          sawCancel.Value <- true
          token.ThrowIfCancellationRequested()
        }
      let running = Started.asTask (watchWith slices.Sleep window 5 CancellationToken.None work)
      do! waitForSignal "three reports" reportedThree
      do! slices.EndWindow ()
      do! slices.EndWindow ()
      let! ended = waitFor running
      ended |> Expect.equal "stalled with the counts" (RunEnd.Stalled (StallCause.StoppedReporting (3, 2), window))
      sawCancel.Value |> Expect.isTrue "the work was cancelled"
    }

    testAsync "WHY — a run that reports in every window is never cancelled, however many windows it takes, and completes" {
      let slices = Slices()
      let steps = 40
      let gates = [| for _ in 1 .. steps -> signal () |]
      let made = [| for _ in 1 .. steps -> signal () |]
      let sawCancel = ref false
      let work (reported: unit -> unit) (token: CancellationToken) : Async<unit> =
        async {
          token.Register(fun () -> sawCancel.Value <- true) |> ignore
          for step in 0 .. steps - 1 do
            do! Async.AwaitTask gates[step].Task
            reported ()
            made[step].SetResult ()
        }
      let running = Started.asTask (watchWith slices.Sleep window steps CancellationToken.None work)
      // One report before each window ends: the count has risen at every look, over forty windows. The last report ends the
      // work, so the watch may finish before it parks for another window, and no window is ended after it.
      for step in 0 .. steps - 1 do
        gates[step].SetResult ()
        do! waitForSignal (sprintf "report %d" step) made[step]
        match step < steps - 1 with
        | true ->
          do! slices.EndWindow ()
          do! slices.Parked ()
        | false -> ()
      let! ended = waitFor running
      ended |> Expect.equal "completed" RunEnd.Completed
      sawCancel.Value |> Expect.isFalse "never cancelled"
    }

    testAsync "the caller's own token ends the run as Cancelled, not as a stall" {
      let slices = Slices()
      use caller = new CancellationTokenSource()
      let work (_: unit -> unit) (token: CancellationToken) : Async<unit> =
        async {
          do! Async.AwaitTask (whenCancelled token)
          token.ThrowIfCancellationRequested()
        }
      let running = Started.asTask (watchWith slices.Sleep window 5 caller.Token work)
      let! _ = slices.NextPark ()
      caller.Cancel ()
      let! ended = waitFor running
      ended |> Expect.equal "cancelled" RunEnd.Cancelled
    }

    testAsync "a failure in the work is the work's own and reaches the caller" {
      let slices = Slices()
      let work (_: unit -> unit) (_: CancellationToken) : Async<unit> = async { failwith "the runner broke" }
      let running = Started.asTask (watchWith slices.Sleep window 5 CancellationToken.None work)
      let! outcome = waitFor running |> Async.Catch
      match outcome with
      | Choice2Of2 failure -> failure.GetBaseException().Message |> Expect.equal "the work's message" "the runner broke"
      | Choice1Of2 ended -> failtestf "expected the work's failure, got %A" ended
    }

    testAsync "WHY — the real executor under the watch: a chunk that hangs is cancelled and the stall names the count that had reported" {
      let slices = Slices()
      let cases : TestCase array =
        [| for i in 0 .. 9 ->
             { Id = TestId.TestId (sprintf "Fixture.t%d" i)
               FullName = sprintf "Fixture.t%d" i
               DisplayName = sprintf "t%d" i
               NamePath = []
               Origin = TestOrigin.ReflectionOnly
               Labels = []
               Framework = TestFramework.Expecto
               Category = TestCategory.Unit } |]
      let hangStarted = signal ()
      let release = signal ()
      // Tests 0 to 3 pass at once; tests 4 and 5 start and hang until the run is cancelled.
      let runTest (case: TestCase) : Async<TestResult> =
        async {
          match case.DisplayName with
          | "t4" | "t5" ->
            hangStarted.TrySetResult () |> ignore
            do! Async.AwaitTask release.Task
            return TestResult.Passed TimeSpan.Zero
          | _ -> return TestResult.Passed TimeSpan.Zero
        }
      let work (reported: unit -> unit) (token: CancellationToken) : Async<unit> =
        token.Register(fun () -> release.TrySetResult () |> ignore) |> ignore
        TestOrchestrator.executeFiltered runTest (fun _ -> reported ()) 2 cases token
      let running = Started.asTask (watchWith slices.Sleep window cases.Length CancellationToken.None work)
      // The chunk that hangs starts after the four tests before it have reported.
      do! waitForSignal "the hanging chunk" hangStarted
      do! slices.EndWindow ()
      do! slices.EndWindow ()
      let! ended = waitFor running
      match ended with
      | RunEnd.Stalled (StallCause.StoppedReporting (reported, unreported), _) ->
        reported |> Expect.equal "four had reported when the stall was called" 4
        unreported |> Expect.equal "six had not" 6
      | other -> failtestf "expected the hung run to be cancelled as stalled, got %A" other
    }
  ]
