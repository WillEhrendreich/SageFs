module SageFs.Tests.ProjectResolutionTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.WorkerProtocol

let private handle = { Pid = 4242; Port = Some 5000 }

let private projectTarget (path: string) = SessionProjectTarget.Project path
let private projectTargets (paths: string list) = paths |> List.map projectTarget

[<Tests>]
let projectResolutionClassifyTests =
  testList "ProjectResolution.classify" [
    testCase "WHY — an explicit bare target is a genuine scratch session, not a broken one" <| fun _ ->
      ProjectResolution.classify [ SessionProjectTarget.Bare ] 0
      |> Expect.equal "bare target, no resolved projects: fine" ProjectResolution.NoneRequested

    testCase "WHY — an explicit project that resolves to nothing is the exact bug that shipped Ready with loadedProjects: []" <| fun _ ->
      ProjectResolution.classify (projectTargets [ "Foo.fsproj" ]) 0
      |> Expect.equal "named but unresolved" (ProjectResolution.RequestedButUnresolved [ "Foo.fsproj" ])

    testCase "WHY — an explicit project that DOES resolve is the ordinary, quiet case" <| fun _ ->
      ProjectResolution.classify (projectTargets [ "Foo.fsproj" ]) 1
      |> Expect.equal "named and resolved" ProjectResolution.Resolved

    testCase "WHY — an explicit non-empty request is authoritative even if it resolves to zero" <| fun _ ->
      ProjectResolution.classify (projectTargets [ "Foo.fsproj"; "Bar.fsproj" ]) 0
      |> Expect.equal "explicit request is authoritative" (ProjectResolution.RequestedButUnresolved [ "Foo.fsproj"; "Bar.fsproj" ])

    testPropertyWithConfig FsCheckConfig.defaultConfig "PROPERTY — resolvedCount > 0 is never RequestedButUnresolved" <|
      fun (paths: NonEmptyArray<string>) (PositiveInt resolvedCount) ->
        match ProjectResolution.classify (projectTargets (paths.Get |> Array.toList)) resolvedCount with
        | ProjectResolution.RequestedButUnresolved _ -> false
        | ProjectResolution.NoneRequested
        | ProjectResolution.Resolved -> true

    testPropertyWithConfig FsCheckConfig.defaultConfig "PROPERTY — an explicit bare target is always NoneRequested" <|
      fun (resolvedCount: int) ->
        ProjectResolution.classify [ SessionProjectTarget.Bare ] resolvedCount = ProjectResolution.NoneRequested

    testPropertyWithConfig FsCheckConfig.defaultConfig "PROPERTY — a non-empty explicit request that resolves to zero always names exactly what was requested" <|
      fun (explicitNonEmpty: NonEmptyArray<string>) ->
        let explicit = explicitNonEmpty.Get |> Array.toList
        match ProjectResolution.classify (projectTargets explicit) 0 with
        | ProjectResolution.RequestedButUnresolved requested -> requested = explicit
        | _ -> false
  ]

[<Tests>]
let projectResolutionUnresolvedReasonTests =
  testList "ProjectResolution.unresolvedReason" [
    testCase "WHY — the reason names exactly what was requested so the reader can act on it" <| fun _ ->
      let reason = ProjectResolution.unresolvedReason [ "Foo.fsproj" ]
      reason.Contains "Foo.fsproj" |> Expect.isTrue "names the requested project"
      reason.Contains "loadedProjects: []" |> Expect.isTrue "names the observed symptom"

    testCase "WHY — multiple requested projects are all named, not truncated to one" <| fun _ ->
      let reason = ProjectResolution.unresolvedReason [ "Foo.fsproj"; "Bar.fsproj" ]
      reason.Contains "Foo.fsproj" |> Expect.isTrue "names the first"
      reason.Contains "Bar.fsproj" |> Expect.isTrue "names the second"
  ]

/// Reproduces the live bug reported 2026-09-23: a session created naming
/// exactly one project (`FSharp.Compiler.Service.fsproj`) was observed
/// `Ready` with `loadedProjects: []` on `/api/sessions`, moments before a
/// later poll showed `loadedProjects` correctly populated. Two independent
/// agents polling `get_fsi_status`/`/api/sessions` read that transient state
/// and concluded SageFs was broken — the window is real even though it
/// self-heals a moment later.
///
/// `WorkerReportedReady` (SessionManager.fs) never has this bug: it sets
/// `SessionLifecycleStatus.Ready` and `ProjectRoles` together, atomically, in
/// one state update, gated by `ProjectResolution.classify`. The window
/// exists because `get_fsi_status`, `/api/sessions`, and `/health` each poll
/// the WORKER PROCESS directly for freshness, and the worker's own internal
/// `Ready` flips before the daemon's mailbox gets around to running that
/// gate. `earnedReady`/`reconcile` are the one place all three now reconcile
/// a live report, so none of them can independently leak (or, worse for
/// `get_fsi_status`, PERSIST via its write-back) an unearned `Ready`.
[<Tests>]
let earnedReadyTests =
  testList "ProjectResolution.earnedReady / reconcile — Ready must not be observable before loadedProjects is" [

    testCase "WHY — the exact repro: an explicit request, zero resolved yet, worker says Ready — not earned" <| fun _ ->
      ProjectResolution.earnedReady (projectTargets [ "src/Compiler/FSharp.Compiler.Service.fsproj" ]) 0 SessionStatus.Ready
      |> Expect.isFalse "a live Ready report for an explicit request with nothing resolved yet must not be trusted"

    testCase "WHY — once resolvedCount catches up, the SAME report is earned" <| fun _ ->
      ProjectResolution.earnedReady (projectTargets [ "src/Compiler/FSharp.Compiler.Service.fsproj" ]) 1 SessionStatus.Ready
      |> Expect.isTrue "resolvedCount > 0 means WorkerReportedReady's gate has already run and paired ProjectRoles"

    testCase "WHY — an explicit bare target is never gated here" <| fun _ ->
      ProjectResolution.earnedReady [ SessionProjectTarget.Bare ] 0 SessionStatus.Ready
      |> Expect.isTrue "an explicit bare target must not be blocked by this no-IO guard"

    testCase "WHY — non-Ready reports are never gated — only a Ready claim can be premature" <| fun _ ->
      [ SessionStatus.Starting; SessionStatus.Evaluating; SessionStatus.Building "restoring"
        SessionStatus.Faulted; SessionStatus.Restarting; SessionStatus.Stopped ]
      |> List.iter (fun reported ->
        ProjectResolution.earnedReady (projectTargets [ "Foo.fsproj" ]) 0 reported
        |> Expect.isTrue (sprintf "%A is not a Ready claim and must pass through" reported))

    testCase "WHY — reconcile refuses to promote an unearned Ready, keeping the registry's current status" <| fun _ ->
      let current = SessionLifecycleStatus.Starting handle
      match ProjectResolution.reconcile (projectTargets [ "Foo.fsproj" ]) 0 current SessionStatus.Ready with
      | ProjectResolution.ReconciledStatus.NotYetEarned kept ->
        kept |> Expect.equal "the registry's own current status is kept, not the raw worker report" current
      | other -> failtestf "an unearned Ready must be refused, got %A" other

    testCase "WHY — reconcile promotes normally once Ready is earned" <| fun _ ->
      let current = SessionLifecycleStatus.Starting handle
      match ProjectResolution.reconcile (projectTargets [ "Foo.fsproj" ]) 1 current SessionStatus.Ready with
      | ProjectResolution.ReconciledStatus.Reconciled (SessionLifecycleStatus.Ready h) ->
        h |> Expect.equal "pid/port carry over exactly as ofWorkerReport already guarantees" handle
      | other -> failtestf "an earned Ready must reconcile to Ready, got %A" other

    testCase "WHY — reconcile still lets a genuine worker-reported fault through even when unearned" <| fun _ ->
      let current = SessionLifecycleStatus.Starting handle
      match ProjectResolution.reconcile (projectTargets [ "Foo.fsproj" ]) 0 current SessionStatus.Faulted with
      | ProjectResolution.ReconciledStatus.Reconciled (SessionLifecycleStatus.Faulted _) -> ()
      | other -> failtestf "a worker-reported fault must never be swallowed by the earned-Ready guard, got %A" other

    testPropertyWithConfig FsCheckConfig.defaultConfig
      "PROPERTY — reconcile NEVER reconciles to Ready for an explicit request while resolvedCount is 0" <|
      fun (explicitNonEmpty: NonEmptyArray<string>) (current: int) ->
        let requested = explicitNonEmpty.Get |> Array.toList
        let currentStatus = SessionLifecycleStatus.Starting { Pid = current; Port = None }
        match ProjectResolution.reconcile (projectTargets requested) 0 currentStatus SessionStatus.Ready with
        | ProjectResolution.ReconciledStatus.Reconciled (SessionLifecycleStatus.Ready _) -> false
        | ProjectResolution.ReconciledStatus.NotYetEarned _ -> true
        | ProjectResolution.ReconciledStatus.Reconciled _ -> true
  ]
