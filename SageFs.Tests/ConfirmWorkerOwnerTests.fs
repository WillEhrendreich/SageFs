module SageFs.Tests.ConfirmWorkerOwnerTests

open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

/// The real owner of the throwaway workers over a fake world: no process, no build, no HTTP. The decisions are
/// `ThrowawayBook`'s and are proven under seeded chaos by `ConfirmThrowawaySimTests`; this proves the owner performs them:
/// it starts what it is told to start, stops what it is told to stop, and answers every ask.

let private session (n: int) : SessionId =
  match SessionId.validate (sprintf "%08x" n) with
  | Ok id -> id
  | Error reason -> invalidOp reason

let private spec : ThrowawaySpec = { Targets = [ SessionProjectTarget.Bare ]; WorkingDir = "/work"; Opening = NamespaceOpening.LeftClosed }

/// A world whose workers come up when the test says so, and whose stops are heard the moment they happen.
type World(buildAnswer: Result<string, SageFsError>) =
  let retired = ConcurrentQueue<int * string>()
  let stopped = ConcurrentDictionary<int, TaskCompletionSource<unit>>()
  let comesUp = ConcurrentDictionary<int, TaskCompletionSource<Result<string, string>>>()
  let spawned = ConcurrentQueue<int>()
  let firstSpawn = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
  let mutable nextPid = 1000

  let stoppedOf (pid: int) = stopped.GetOrAdd (pid, fun _ -> TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
  let comesUpOf (pid: int) = comesUp.GetOrAdd (pid, fun _ -> TaskCompletionSource<Result<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously))

  member _.Retired : (int * string) list = List.ofSeq retired
  member _.Spawned : int list = List.ofSeq spawned
  /// Completes with the pid of the first worker started.
  member _.SpawnedTask : Task<int> = firstSpawn.Task
  /// Completes when this worker has been stopped.
  member _.StoppedTask (pid: int) : Task<unit> = (stoppedOf pid).Task
  /// The worker with this pid answers as Ready on this URL.
  member _.BringUp (pid: int) (url: string) : unit = (comesUpOf pid).TrySetResult (Ok url) |> ignore
  member _.Refuse (pid: int) (reason: string) : unit = (comesUpOf pid).TrySetResult (Error reason) |> ignore
  member this.Ports : ThrowawayPorts =
    { SpecOf = fun _ -> async { return Some spec }
      Build = fun _ -> async { return buildAnswer }
      Spawn =
        fun _ _ ->
          let pid = System.Threading.Interlocked.Increment &nextPid
          spawned.Enqueue pid
          firstSpawn.TrySetResult pid |> ignore
          Result.Ok
            { ProcessId = pid
              AwaitReady = async { return! Async.AwaitTask (comesUpOf pid).Task }
              Retire =
                fun url ->
                  async {
                    retired.Enqueue (pid, url)
                    (stoppedOf pid).TrySetResult () |> ignore
                  } } }

let private url (pid: int) = sprintf "http://127.0.0.1:%d" pid

[<Tests>]
let ownerTests =
  testList "the owner of the throwaway workers" [

    testAsync "a worker that is built and comes up answers the ask, and is stopped when its generation ends" {
      let world = World(Result.Ok "built")
      let owner = ConfirmWorkers.start world.Ports
      let asking = owner.Want (session 1) 1L |> Async.StartAsTask
      let pid = 1001
      world.BringUp pid (url pid)
      let! answer = asking |> Async.AwaitTask
      match answer with
      | Result.Ok worker ->
        worker.WorkerProcessId |> Expect.equal "the worker's pid" pid
        worker.BaseUrl |> Expect.equal "where it answers" (url pid)
      | Result.Error reason -> failtestf "expected a worker, got: %s" reason
      let! held = owner.Held ()
      held |> Expect.equal "it is held" [ pid ]
      owner.Ended (session 1) 1L
      do! world.StoppedTask pid |> Async.AwaitTask
      world.Retired |> Expect.equal "stopped, with the URL it answered on, so it is asked to shut down first" [ pid, url pid ]
      let! after = owner.Held ()
      after |> Expect.isEmpty "nothing is held once it is stopped"
    }

    testAsync "a newer generation of the same session stops the older worker" {
      let world = World(Result.Ok "built")
      let owner = ConfirmWorkers.start world.Ports
      let first = owner.Want (session 1) 1L |> Async.StartAsTask
      world.BringUp 1001 (url 1001)
      let! _ = first |> Async.AwaitTask
      let second = owner.Want (session 1) 2L |> Async.StartAsTask
      do! world.StoppedTask 1001 |> Async.AwaitTask
      world.BringUp 1002 (url 1002)
      let! answer = second |> Async.AwaitTask
      match answer with
      | Result.Ok worker -> worker.WorkerProcessId |> Expect.equal "the newer generation's worker" 1002
      | Result.Error reason -> failtestf "expected the newer worker, got: %s" reason
      let! held = owner.Held ()
      held |> Expect.equal "only the newer worker is held" [ 1002 ]
    }

    testAsync "a build that fails answers with the compiler's words, starts no worker, and holds nothing" {
      let world = World(Result.Error (SageFsError.NeedsRebuild [ "FS0039: The value 'main' is not defined" ]))
      let owner = ConfirmWorkers.start world.Ports
      let! answer = owner.Want (session 1) 1L
      match answer with
      | Result.Error reason -> reason |> Expect.stringContains "the compiler's own words" "FS0039"
      | Result.Ok worker -> failtestf "expected the build to fail, got a worker on %s" worker.BaseUrl
      world.Spawned |> Expect.isEmpty "no worker was started for a build that does not exist"
      let! held = owner.Held ()
      held |> Expect.isEmpty "nothing held"
    }

    testAsync "a worker that never reaches Ready is stopped, and the ask hears why" {
      let world = World(Result.Ok "built")
      let owner = ConfirmWorkers.start world.Ports
      let asking = owner.Want (session 1) 1L |> Async.StartAsTask
      world.Refuse 1001 "the worker went silent while starting"
      let! answer = asking |> Async.AwaitTask
      match answer with
      | Result.Error reason -> reason |> Expect.stringContains "the reason" "went silent"
      | Result.Ok worker -> failtestf "expected a refusal, got a worker on %s" worker.BaseUrl
      do! world.StoppedTask 1001 |> Async.AwaitTask
      world.Retired |> Expect.equal "the process was stopped, with no URL because it never answered" [ 1001, "" ]
    }

    testAsync "a generation that ends while its worker is still starting is told, and the worker that comes up late is stopped" {
      let world = World(Result.Ok "built")
      let owner = ConfirmWorkers.start world.Ports
      let asking = owner.Want (session 1) 1L |> Started.asTask
      owner.Ended (session 1) 1L
      let! answer = asking |> Async.AwaitTask
      match answer with
      | Result.Error reason -> reason |> Expect.stringContains "it says its confirmation ended" "ended"
      | Result.Ok worker -> failtestf "expected the ask to end with its generation, got a worker on %s" worker.BaseUrl
      // The process exists by now (or will), and comes up after nobody wants it.
      let! pid = world.SpawnedTask |> Async.AwaitTask
      world.BringUp pid (url pid)
      do! world.StoppedTask pid |> Async.AwaitTask
      world.Retired |> Expect.equal "the late worker is stopped, asked to shut down first" [ pid, url pid ]
      let! held = owner.Held ()
      held |> Expect.isEmpty "nothing is held for a generation that ended"
    }

    testAsync "a session that goes takes its worker with it" {
      let world = World(Result.Ok "built")
      let owner = ConfirmWorkers.start world.Ports
      let asking = owner.Want (session 2) 1L |> Async.StartAsTask
      world.BringUp 1001 (url 1001)
      let! _ = asking |> Async.AwaitTask
      owner.SessionGone (session 2)
      do! world.StoppedTask 1001 |> Async.AwaitTask
      let! held = owner.Held ()
      held |> Expect.isEmpty "nothing held"
    }
  ]
