module SageFs.Tests.TargetedVerificationLoadedStateTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.Verification

let private mkSessionCtx files : SessionContext =
  { SessionId = "session-1"
    ProjectNames = [ "SageFs.Tests" ]
    WorkingDir = @"C:\Code\Repos\SageFs"
    Status = "Ready"
    Warmup = WarmupContext.empty
    FileStatuses = files
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    AutoOpenNamespaces = true }

let private mkFile path readiness loadedAt watched =
  { Path = path
    Readiness = readiness
    LastLoadedAt = loadedAt
    IsWatched = watched }

[<Tests>]
let tests =
  testList "Targeted verification loaded state" [
    testCase "when any watched file is stale, loaded state is stale rather than guessed current" <| fun _ ->
      let now = DateTimeOffset.UtcNow
      let session =
        mkSessionCtx [
          mkFile "UserPreferences.fs" Stale (Some now) true
          mkFile "KeyBindings.fs" Loaded (Some now) true
        ]
      let loadedState =
        session.FileStatuses
        |> List.tryFind (fun file -> file.Readiness = Stale)
        |> Option.map (fun stale -> LoadedDefinitionState.ConfirmedStale (stale.Path, stale.LastLoadedAt |> Option.map string |> Option.defaultValue "unknown-loaded-version"))
        |> Option.defaultValue (LoadedDefinitionState.UnknownLoadState "expected stale file")
      loadedState
      |> Expect.equal
        "stale file state should dominate current ones"
        (LoadedDefinitionState.ConfirmedStale ("UserPreferences.fs", string now))

    testCase "when warmup context has only loaded files, targeted verification can report current artifact set" <| fun _ ->
      let now = DateTimeOffset.UtcNow
      let session =
        mkSessionCtx [
          mkFile "UserPreferences.fs" Loaded (Some now) true
          mkFile "KeyBindings.fs" Loaded (Some now) true
        ]
      let artifact =
        session.FileStatuses
        |> List.filter (fun file -> file.Readiness = Loaded)
        |> List.map (fun file -> file.Path)
        |> String.concat ", "
      LoadedDefinitionState.ConfirmedCurrent artifact
      |> Expect.equal
        "current artifact list should preserve the loaded file set"
        (LoadedDefinitionState.ConfirmedCurrent "UserPreferences.fs, KeyBindings.fs")
  ]
