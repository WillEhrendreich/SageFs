module SageFs.Tests.LiveTestWatcherAtomicSaveTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server.DaemonMode

let private sid =
  match WorkerProtocol.SessionId.validate "aa000001" with
  | Ok id -> id
  | Error err -> failwithf "bad test session id: %A" err

/// What the watcher reported after a save, within the bound.
type private WatchOutcome =
  | Reloaded of path: string
  | NothingReported

/// Watch a fresh directory holding Hello.fs, run `save` on it, and report what the
/// live-test watcher said within five seconds.
let private watchAfter (save: string -> unit) = task {
  let dir = Directory.CreateTempSubdirectory "sagefs-watch-"
  let target = Path.Combine(dir.FullName, "Hello.fs")
  File.WriteAllText(target, "module Hello\nlet x = 1\n")
  let reloaded = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
  use manager = new LiveTestWatcherManager(ignore, (fun _ path -> reloaded.TrySetResult path |> ignore), None)
  try
    manager.AddDirectory(dir.FullName, sid)
    save target
    let! winner = Task.WhenAny(reloaded.Task :> Task, Task.Delay(TimeSpan.FromSeconds 5.0))
    return
      match obj.ReferenceEquals(winner, reloaded.Task) with
      | true -> Reloaded reloaded.Task.Result
      | false -> NothingReported
  finally
    manager.RemoveDirectory(dir.FullName, sid)
    dir.Delete true
}

[<Tests>]
let tests =
  testList "Live-test watcher saves" [
    testTask "WHY — LiveTestWatcherManager — a save that writes the file in place triggers live testing" {
      let! outcome = watchAfter (fun target -> File.WriteAllText(target, "module Hello\nlet x = 2\n"))
      let target = match outcome with Reloaded path -> Path.GetFileName path | NothingReported -> "(nothing)"
      target |> Expect.equal "the in-place save is reported" "Hello.fs"
    }

    testTask "WHY — LiveTestWatcherManager — a save that writes a temp file and renames it over the source triggers live testing because vim, JetBrains and sed all save that way" {
      let! outcome =
        watchAfter (fun target ->
          let temp = target + ".tmp"
          File.WriteAllText(temp, "module Hello\nlet x = 2\n")
          File.Move(temp, target, true))
      let target = match outcome with Reloaded path -> Path.GetFileName path | NothingReported -> "(nothing)"
      target |> Expect.equal "the atomic save is reported" "Hello.fs"
    }
  ]
