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
/// live-test watcher said within `TestTimeouts.briefPatience`.
let private watchAfter (save: string -> unit) = task {
  let dir = Directory.CreateTempSubdirectory "sagefs-watch-"
  let target = Path.Combine(dir.FullName, "Hello.fs")
  File.WriteAllText(target, "module Hello\nlet x = 1\n")
  let reloaded = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
  use manager = new LiveTestWatcherManager(ignore, (fun _ path -> reloaded.TrySetResult path |> ignore), None)
  try
    manager.AddDirectory(dir.FullName, sid)
    // AddDirectory posts to the watcher actor's mailbox, so the watcher is
    // created asynchronously. WatchedDirectories is a PostAndReply, so it only
    // returns once the queued AddDirectory has been processed and the
    // FileSystemWatcher armed — without this barrier the save below can race
    // ahead of watcher creation and be missed. (In production saves happen long
    // after a session is added, so this race never occurs there.)
    manager.WatchedDirectories |> ignore
    // A fixed settle cannot say when the OS watcher starts delivering events, and a save that lands before it does
    // is never reported. So the save is made again, once per `repeatActionEvery`, until the watcher reports one or the
    // ceiling passes: the case proves that a save of this SHAPE is reported, not that a particular instant is.
    let started = Diagnostics.Stopwatch.StartNew()
    let mutable outcome = NothingReported
    while outcome = NothingReported && started.Elapsed < TestTimeouts.briefPatience do
      save target
      let! winner = Task.WhenAny(reloaded.Task :> Task, Task.Delay TestTimeouts.repeatActionEvery)
      match obj.ReferenceEquals(winner, reloaded.Task) with
      | true -> outcome <- Reloaded reloaded.Task.Result
      | false -> ()
    return outcome
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

/// Watch a fresh directory holding App.fsproj (compiling A.fs and B.fs), run `save` on it until the watcher dispatches a
/// build-input change, and return that change within `TestTimeouts.briefPatience`.
let private buildInputChangeAfter (save: string -> unit) = task {
  let dir = Directory.CreateTempSubdirectory "sagefs-watch-input-"
  let project = Path.Combine(dir.FullName, "App.fsproj")
  File.WriteAllText(project, """<Project><PropertyGroup><X>1</X></PropertyGroup><ItemGroup><Compile Include="A.fs" /><Compile Include="B.fs" /></ItemGroup></Project>""")
  let changed = TaskCompletionSource<string * Features.ProjectFileChange.Selection>(TaskCreationOptions.RunContinuationsAsynchronously)
  let dispatch (msg: SageFs.SageFsMsg) =
    match msg with
    | SageFs.SageFsMsg.BuildInputChanged (path, selection) -> changed.TrySetResult((path, selection)) |> ignore
    | _ -> ()
  use manager = new LiveTestWatcherManager(dispatch, (fun _ _ -> ()), None)
  try
    manager.AddDirectory(dir.FullName, sid)
    manager.WatchedDirectories |> ignore
    let started = Diagnostics.Stopwatch.StartNew()
    let mutable outcome = None
    while outcome.IsNone && started.Elapsed < TestTimeouts.briefPatience do
      save project
      let! winner = Task.WhenAny(changed.Task :> Task, Task.Delay TestTimeouts.repeatActionEvery)
      match obj.ReferenceEquals(winner, changed.Task) with
      | true -> outcome <- Some changed.Task.Result
      | false -> ()
    return outcome
  finally
    manager.RemoveDirectory(dir.FullName, sid)
    dir.Delete true
}

[<Tests>]
let buildInputTests =
  testList "Live-test watcher build inputs" [
    testTask "WHY — LiveTestWatcherManager — a property change in a watched project file reaches live testing as a whole-suite run, because the watcher used to see .fs and .fsx only" {
      let! outcome =
        buildInputChangeAfter (fun project ->
          File.WriteAllText(project, """<Project><PropertyGroup><X>2</X></PropertyGroup><ItemGroup><Compile Include="A.fs" /><Compile Include="B.fs" /></ItemGroup></Project>"""))
      match outcome with
      | Some (path: string, Features.ProjectFileChange.Selection.WholeSuite _) ->
        Path.GetFileName path |> Expect.equal "the project file" "App.fsproj"
      | other -> failtestf "expected a whole-suite run for App.fsproj, got %A" other
    }

    testTask "WHY — LiveTestWatcherManager — a compile-list edit to a watched project file narrows to the files at and after the edit, diffed against the text the watcher read when it started" {
      let! outcome =
        buildInputChangeAfter (fun project ->
          File.WriteAllText(project, """<Project><PropertyGroup><X>1</X></PropertyGroup><ItemGroup><Compile Include="A.fs" /><Compile Include="New.fs" /><Compile Include="B.fs" /></ItemGroup></Project>"""))
      match outcome with
      | Some (_, selection) ->
        selection |> Expect.equal "New.fs and the file after it" (Features.ProjectFileChange.Selection.NarrowToFiles [ "New.fs"; "B.fs" ])
      | None -> failtest "the compile-list edit never reached live testing"
    }

    testTask "WHY — LiveTestWatcherManager — a changed data file in the project's folder reaches live testing as a whole-suite run" {
      let! outcome =
        buildInputChangeAfter (fun project ->
          File.WriteAllText(Path.Combine(Path.GetDirectoryName project, "orders.json"), sprintf "{ \"stamp\": \"%s\" }" (Guid.NewGuid().ToString())))
      match outcome with
      | Some (path: string, Features.ProjectFileChange.Selection.WholeSuite _) ->
        Path.GetFileName path |> Expect.equal "the data file" "orders.json"
      | other -> failtestf "expected a whole-suite run for orders.json, got %A" other
    }
  ]
