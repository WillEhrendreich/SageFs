/// Where the running app lives decides whether a patch can reach it.
///
/// Every session's reload agent lives in the FSI host (HostAgent.fs: the agent
/// runs "in the process that loaded those assemblies", and for an isolated
/// session that is the FSI host). An app SageFs started with `run_app` runs on a
/// thread in the WORKER. A patch therefore re-points the FSI host's copy of the
/// function and the running app never calls it, while the save was still
/// reported as "Hot reloaded 1 of 1". For such an app the only honest outcome of
/// a function change is a restart, which SageFs performs for the app it started.
module SageFs.Tests.AppPlacementTests

open Expecto
open Expecto.Flip
open SageFs.Features.ReloadPlanning

let private fn (name: string) : SourceDecl =
  { Name = name
    Kind = DeclKind.FunctionDecl
    Access = DeclAccess.Public
    Container = []
    Header = sprintf "let %s (x: int)" name
    Text = sprintf "let %s (x: int) = x" name
    StartLine = 1
    EndLine = 1 }

let private namesOf (change: ReloadChange) : string =
  match change with
  | ReloadChange.RunsOutsideAgent name -> name
  | other -> failtestf "expected RunsOutsideAgent, got %A" other

[<Tests>]
let appPlacementTests =
  testList "AppPlacement.adjust" [

    testCase "WHY — an app that runs in the worker cannot be patched, so a function change becomes a restart naming the function, because the patch would land in the FSI host's copy and be reported as landed" <| fun _ ->
      match AppPlacement.adjust AppPlacement.InWorkerProcess (ReloadPlan.PatchFunctions [ fn "Ticker.renderLine" ]) with
      | ReloadPlan.RestartRequired (first, rest) ->
        (first :: rest) |> List.map namesOf |> Expect.equal "the restart names the function that changed" [ "Ticker.renderLine" ]
      | other -> failtestf "a worker-hosted app must restart, got %A" other

    testCase "WHY — every changed function is named, in order, because the restart is reported to the user and a partial list would hide part of what changed" <| fun _ ->
      match AppPlacement.adjust AppPlacement.InWorkerProcess (ReloadPlan.PatchFunctions [ fn "A.one"; fn "A.two"; fn "A.three" ]) with
      | ReloadPlan.RestartRequired (first, rest) ->
        (first :: rest) |> List.map namesOf |> Expect.equal "all three, in file order" [ "A.one"; "A.two"; "A.three" ]
      | other -> failtestf "a worker-hosted app must restart, got %A" other

    testCase "WHY — a patch that also keeps live state is a restart too, because the app the state belongs to is out of the agent's reach exactly like its functions" <| fun _ ->
      let kept = LiveState.Kept { fn "App.count" with Kind = DeclKind.MutableValueDecl }
      match AppPlacement.adjust AppPlacement.InWorkerProcess (ReloadPlan.PatchKeepingState ([ fn "App.bump" ], kept, [])) with
      | ReloadPlan.RestartRequired (first, rest) ->
        (first :: rest) |> List.map namesOf |> Expect.equal "the function is named" [ "App.bump" ]
      | other -> failtestf "a worker-hosted app must restart, got %A" other

    testCase "WHY — a save that changed no declaration stays a non-event even for a worker-hosted app, because there is nothing to patch and nothing to restart for" <| fun _ ->
      AppPlacement.adjust AppPlacement.InWorkerProcess (ReloadPlan.PatchFunctions [])
      |> Expect.equal "still nothing to do" (ReloadPlan.PatchFunctions [])

    testCase "WHY — a plan that already requires a restart is left exactly as the planner reported it, because placement must not overwrite a more specific reason" <| fun _ ->
      let plan = ReloadPlan.RestartRequired (ReloadChange.SignatureChanged "render", [])
      AppPlacement.adjust AppPlacement.InWorkerProcess plan
      |> Expect.equal "unchanged" plan

    testCase "WHY — an app in the agent's own process (started from FSI or an init script) keeps its in-place patch, because that is the path the real-app reload tests prove works" <| fun _ ->
      let patch = ReloadPlan.PatchFunctions [ fn "Ticker.renderLine" ]
      AppPlacement.adjust AppPlacement.InAgentProcess patch
      |> Expect.equal "unchanged" patch

    testCase "WHY — the reason reads in the user's terms: which function, and that the app runs where a patch cannot reach" <| fun _ ->
      let text = ReloadChange.describe (ReloadChange.RunsOutsideAgent "Ticker.renderLine")
      text |> Expect.stringContains "names the function" "Ticker.renderLine"
      text |> Expect.stringContains "says why it cannot be patched" "worker"
  ]
