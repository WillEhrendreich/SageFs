/// A rebuild runs off the SessionManager mailbox and answers through a reply
/// channel that is parked until the build finishes. If the build task THROWS,
/// nothing posts the completion: the caller is never answered, the session is
/// marked "rebuild in flight" forever, and every later hard reset is refused
/// with "already in progress". These pin that a build always answers.
module SageFs.Tests.SessionBuildTotalityTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let buildAlwaysAnswersTests =
  testList "SessionBuild.runBuildAsync always answers" [

    testTask "WHY — a working directory that does not exist is a BuildFailed that names it, not an exception, because an exception escaped the caller's task and left the session refusing every later hard reset" {
      let missing = Path.Combine(Path.GetTempPath(), "sagefs-no-such-dir-" + System.Guid.NewGuid().ToString("N"))
      let! result = SessionBuild.runBuildAsync [ "App.fsproj" ] missing |> Async.StartAsTask
      match result with
      | Ok _ -> failtest "there is no such directory, so nothing can have been built"
      | Error (SageFsError.BuildFailed (_, diagnostics)) ->
        diagnostics
        |> List.map (fun d -> d.Message)
        |> String.concat "\n"
        |> Expect.stringContains "the message names the directory it could not build in" missing
      | Error other -> failtestf "expected BuildFailed, got %s" (SageFsError.describe other)
    }
  ]
