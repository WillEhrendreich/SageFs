module SageFs.Tests.ConfirmationVenueEffectTests

open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting
open SageFs.Tests.SageFsEffectHandlerTests

/// The effect that starts a confirming build, against fakes: where it runs, what it does to the session's own worker,
/// and what it says when it cannot confirm. The decision is `Venue.decide` and `BuildConfirmation.step`; this is the edge
/// that obeys them.

let private sessionId = "a1b2c3d4"

let private confirmation : Confirmation =
  { Content = AnalysisIdentity.ofContent "let add a b = a + b + 1"
    File = ""
    Evaluated = Map.empty }

/// What a confirmation's build said, caught from the dispatch the moment it is made.
type Heard =
  { Dispatch: SageFsMsg -> unit
    Answer: Task<BuildAnswer>
    Everything: unit -> SageFsMsg list }

let private listening (generation: int64) : Heard =
  let heard = ConcurrentQueue<SageFsMsg>()
  let answer = TaskCompletionSource<BuildAnswer>(TaskCreationOptions.RunContinuationsAsynchronously)
  let dispatch (msg: SageFsMsg) =
    heard.Enqueue msg
    match msg with
    | SageFsMsg.Event (TuiEvent.BuildConfirmation (_, ConfirmationEvent.BuildFinished (answered, built))) when answered = generation ->
      answer.TrySetResult built |> ignore
    | _ -> ()
  { Dispatch = dispatch; Answer = answer.Task; Everything = fun () -> List.ofSeq heard }

/// Deps whose session manager counts the restarts it is asked for, and whose confirmation ports are these.
let private depsWith (restarts: int ref) (ports: ConfirmationPorts) : EffectDeps =
  let log = TestDeps.createLog ()
  { TestDeps.singleSession log (fun _ -> WorkerResponse.WorkerReady) with
      RestartSession = fun _ _ -> async { restarts.Value <- restarts.Value + 1; return Result.Ok "restarted" }
      GetStreamingTestProxy = fun _ -> Some (fun _ _ _ _ _ -> async { return HttpWorkerClient.StreamOutcome.Completed })
      Confirmation = ports }

let private throwaway : ThrowawayWorker = { WorkerProcessId = 4242; BaseUrl = "http://127.0.0.1:1" }

let private start (deps: EffectDeps) (heard: Heard) (generation: int64) : Async<unit> =
  SageFsEffectHandler.execute deps heard.Dispatch (SageFsEffect.Confirm (sessionId, ConfirmationEffect.StartBuild (generation, confirmation)))

[<Tests>]
let confirmationVenueEffectTests =
  testList "confirming build: the venue decides, the effect obeys" [

    testAsync "a session that holds something is confirmed in a throwaway worker, and its own worker is never restarted" {
      let restarts = ref 0
      let asked = ConcurrentQueue<int64>()
      let ports =
        { ConfirmationPorts.swapOnly with
            ChooseVenue = fun _ _ -> async { return Venue.ThrowawayWorker [ KeptState.ReplDefinitions 1 ] }
            WantThrowaway = fun _ generation -> async { asked.Enqueue generation; return Result.Ok throwaway } }
      let heard = listening 1L
      do! start (depsWith restarts ports) heard 1L
      let! answer = heard.Answer |> Async.AwaitTask
      answer |> Expect.equal "the build is in a throwaway worker" BuildAnswer.BuiltInThrowaway
      restarts.Value |> Expect.equal "the session's worker was not restarted" 0
      List.ofSeq asked |> Expect.equal "one throwaway worker, for this generation" [ 1L ]
    }

    testAsync "a throwaway worker that cannot be had says why on the rows, and the session's worker is untouched" {
      let restarts = ref 0
      let ports =
        { ConfirmationPorts.swapOnly with
            ChooseVenue = fun _ _ -> async { return Venue.ThrowawayWorker [ KeptState.ReplDefinitions 2 ] }
            WantThrowaway = fun _ _ -> async { return Result.Error "FS0039: The value 'main' is not defined" } }
      let heard = listening 2L
      do! start (depsWith restarts ports) heard 2L
      let! answer = heard.Answer |> Async.AwaitTask
      match answer with
      | BuildAnswer.DidNotBuild message -> message |> Expect.stringContains "the compiler's own words" "FS0039"
      | other -> failtestf "expected the build to say it failed, got %A" other
      restarts.Value |> Expect.equal "the session's worker was left running" 0
    }

    testAsync "a refused confirmation builds nothing, starts nothing, and says what it left alone" {
      let restarts = ref 0
      let asked = ref 0
      let ports =
        { ConfirmationPorts.swapOnly with
            ChooseVenue = fun _ _ -> async { return Venue.Refused (ConfirmRefusal.MemoryPressure "the machine is almost out of memory") }
            WantThrowaway = fun _ _ -> async { asked.Value <- asked.Value + 1; return Result.Ok throwaway } }
      let heard = listening 3L
      do! start (depsWith restarts ports) heard 3L
      let! answer = heard.Answer |> Async.AwaitTask
      match answer with
      | BuildAnswer.NotAttempted reason ->
        reason |> Expect.stringContains "the memory is named" "almost out of memory"
        reason |> Expect.stringContains "the REPL state is said to be kept" "REPL state was left alone"
      | other -> failtestf "expected the confirmation to be refused, got %A" other
      restarts.Value |> Expect.equal "no restart" 0
      asked.Value |> Expect.equal "no throwaway worker asked for" 0
    }

    testAsync "a session that holds nothing is swapped as before: its worker is restarted and the tests run in it" {
      let restarts = ref 0
      let heard = listening 4L
      do! start (depsWith restarts ConfirmationPorts.swapOnly) heard 4L
      let! answer = heard.Answer |> Async.AwaitTask
      answer |> Expect.equal "the session came back up on the build" BuildAnswer.Built
      restarts.Value |> Expect.equal "one restart" 1
    }

    testAsync "abandoning a generation retires its throwaway worker, whichever way the confirmation ended" {
      let ended = ConcurrentQueue<int64>()
      let ports = { ConfirmationPorts.swapOnly with EndThrowaway = fun _ generation -> ended.Enqueue generation }
      let heard = listening 5L
      do! SageFsEffectHandler.execute (depsWith (ref 0) ports) heard.Dispatch (SageFsEffect.Confirm (sessionId, ConfirmationEffect.AbandonBuild 5L))
      List.ofSeq ended |> Expect.equal "the generation's worker is asked to go" [ 5L ]
    }
  ]
