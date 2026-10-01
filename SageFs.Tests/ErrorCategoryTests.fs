/// Which family a `SageFsError` belongs to (client, server, gateway, infra,
/// overload) used to be five separate 40-line matches over every case, kept
/// consistent by one test that noticed when a case ended up in two of them or in
/// none. There is now one exhaustive `category`, and the five predicates are
/// derived from it, so a case cannot be in two families and a new case cannot be
/// left out (the compiler refuses the incomplete match).
///
/// The table below is the assignment as it stood before that change, generated
/// from the five predicates, so the refactor is checked to have moved no case.
/// Reassigning a case is a deliberate edit to this file.
module SageFs.Tests.ErrorCategoryTests

open System
open Expecto
open Expecto.Flip
open SageFs

let private golden : (string * ErrorCategory) list =
  [ "ToolNotAvailable", ErrorCategory.Client
    "SessionNotFound", ErrorCategory.Client
    "NoActiveSessions", ErrorCategory.Client
    "AmbiguousSessions", ErrorCategory.Client
    "SessionCreationFailed", ErrorCategory.Internal
    "NeedsRebuild", ErrorCategory.Client
    "DuplicateSession", ErrorCategory.Infra
    "UnsafeSessionPath", ErrorCategory.Client
    "ProjectFrameworkNotHostable", ErrorCategory.Client
    "SessionStopFailed", ErrorCategory.Internal
    "SessionSwitchFailed", ErrorCategory.Internal
    "SupervisorBusy", ErrorCategory.Overload
    "MemoryPressureRefused", ErrorCategory.Overload
    "SessionNotRoutable", ErrorCategory.Client
    "WorkerCommunicationFailed", ErrorCategory.Gateway
    "WorkerSpawnFailed", ErrorCategory.Gateway
    "WorkerTimeout", ErrorCategory.Gateway
    "WorkerHttpError", ErrorCategory.Gateway
    "PipeClosed", ErrorCategory.Gateway
    "EvalFailed", ErrorCategory.Internal
    "ResetFailed", ErrorCategory.Internal
    "HardResetFailed", ErrorCategory.Internal
    "BuildFailed", ErrorCategory.Internal
    "ScriptLoadFailed", ErrorCategory.Internal
    "CheckFailed", ErrorCategory.Internal
    "CompletionFailed", ErrorCategory.Internal
    "CancelFailed", ErrorCategory.Internal
    "EvalSupersededByReset", ErrorCategory.Internal
    "FsiHostCrashed", ErrorCategory.Gateway
    "WarmupOpenFailed", ErrorCategory.Internal
    "WarmupContextFailed", ErrorCategory.Internal
    "HotReloadFailed", ErrorCategory.Internal
    "HotReloadStateError", ErrorCategory.Internal
    "AppRunFailed", ErrorCategory.Internal
    "RestartLimitExceeded", ErrorCategory.Infra
    "DaemonStartFailed", ErrorCategory.Internal
    "DaemonNotRunning", ErrorCategory.Client
    "PortInUse", ErrorCategory.Infra
    "SseConnectionError", ErrorCategory.Gateway
    "JsonParseError", ErrorCategory.Client
    "CohortActionFailed", ErrorCategory.Client
    "Unexpected", ErrorCategory.Internal ]

/// One value of every case, built by reflection with a plain argument for each field.
let private everyError : (string * SageFsError) list =
  FSharp.Reflection.FSharpType.GetUnionCases(typeof<SageFsError>)
  |> Array.map (fun case ->
    let args =
      case.GetFields()
      |> Array.map (fun f ->
        match f.PropertyType with
        | t when t = typeof<string> -> box ""
        | t when t = typeof<int> -> box 0
        | t when t = typeof<float> -> box 0.0
        | t when t = typeof<exn> -> box (Exception "test")
        | t when t = typeof<string list> -> box ([] : string list)
        | t when t = typeof<SessionState> -> box SessionState.Uninitialized
        | t when t = typeof<BuildDiagnostic list> -> box ([ BuildDiagnostic.ofLine "test error" ] : BuildDiagnostic list)
        | t when t = typeof<ProjectCompatibility.UnsupportedTfmReason> -> box ProjectCompatibility.UnsupportedTfmReason.NetFramework
        | t when t = typeof<HostCrash> -> box ({ Exit = ExitedWith 134; Output = "test" } : HostCrash)
        | _ -> null)
    case.Name, FSharp.Reflection.FSharpValue.MakeUnion(case, args) :?> SageFsError)
  |> Array.toList

[<Tests>]
let tests =
  testList "SageFsError.category" [
    testCase "WHY — the table names every case there is and nothing else, so a new case has to be placed here on purpose" <| fun _ ->
      everyError |> List.map fst |> List.sort
      |> Expect.equal "the cases in the type and the cases in the table are the same set" (golden |> List.map fst |> List.sort)

    testCase "WHY — every case is in the family it was in before the classifiers were derived from one function" <| fun _ ->
      for name, error in everyError do
        let expected = golden |> List.find (fun (n, _) -> n = name) |> snd
        SageFsError.category error |> Expect.equal (sprintf "%s" name) expected

    testCase "WHY — each predicate is true for exactly its own family, so no case is ever in two or in none" <| fun _ ->
      for name, error in everyError do
        let flags =
          [ ErrorCategory.Client, SageFsError.isClientError error
            ErrorCategory.Internal, SageFsError.isServerError error
            ErrorCategory.Gateway, SageFsError.isGatewayError error
            ErrorCategory.Infra, SageFsError.isInfraError error
            ErrorCategory.Overload, SageFsError.isOverloadError error ]
        flags |> List.filter snd |> List.map fst
        |> Expect.equal (sprintf "%s is in exactly its own family" name) [ SageFsError.category error ]
  ]
