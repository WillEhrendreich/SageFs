/// What the FSI host could not make right for a project used to be a log line. It is a value on the project
/// now, and a session that carries one is Degraded with the reason, so the person reading the dashboard or
/// the MCP status sees it before the wrong answer, not instead of it.
module SageFs.Tests.HostConcernTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ProjectLoading
open SageFs.WorkerProtocol

let private handle : WorkerHandle = { Pid = 4242; Port = Some 5000 }

let private role (path: string) (mode: LoadMode) : ClassifiedProject =
  { Path = path
    Role = ProjectRole.Library
    PackageRefs = []
    LoadMode = mode
    Build = BuildOptimization.Unoptimized }

let private everyConcern : HostConcern list =
  [ HostConcern.RuntimeUndetermined "runtimeconfig.json is not valid JSON: unexpected token"
    HostConcern.TargetFrameworkUnrecognised("uap10.0", "'uap10.0' is not a recognised target framework moniker")
    HostConcern.FSharpCoreRewriteFailed("App.dll", "the file is not a .NET assembly")
    HostConcern.CompilerServiceShadowed("43.13.101.0", "43.12.401.0", "10.0.401") ]

[<Tests>]
let tests =
  testList "Host concerns" [

    testCase "WHY — every concern says what it is about and what to do, because a reason with no next step is a log line" <| fun _ ->
      for concern in everyConcern do
        let text = HostConcern.describe concern
        text |> Expect.isNotEmpty "never blank"
        text |> Expect.stringContains (sprintf "%A says what to do" concern) "."

    testCase "WHY — each concern names the thing that went wrong, so the reader does not have to find it in a log" <| fun _ ->
      let named =
        [ HostConcern.RuntimeUndetermined "unexpected token", "unexpected token"
          HostConcern.TargetFrameworkUnrecognised("uap10.0", "why"), "uap10.0"
          HostConcern.FSharpCoreRewriteFailed("App.dll", "boom"), "App.dll"
          HostConcern.CompilerServiceShadowed("43.13.101.0", "43.12.401.0", "10.0.401"), "43.13.101.0" ]
      for concern, fact in named do
        HostConcern.describe concern |> Expect.stringContains (sprintf "names %s" fact) fact

    testList "withConcerns" [
      testCase "a concern lands on the project it is about and no other" <| fun _ ->
        let roles = [ role "/a/A.fsproj" LoadMode.Evaluated; role "/b/B.fsproj" LoadMode.Evaluated ]
        let concern = HostConcern.RuntimeUndetermined "x"
        withConcerns [ "/b/B.fsproj", concern ] roles
        |> List.map (fun r -> r.Path, r.LoadMode)
        |> Expect.equal
             "only B carries it"
             [ "/a/A.fsproj", LoadMode.Evaluated; "/b/B.fsproj", LoadMode.EvaluatedWithConcerns [ concern ] ]

      testCase "no concerns changes nothing" <| fun _ ->
        let roles = [ role "/a/A.fsproj" LoadMode.Evaluated ]
        withConcerns [] roles |> Expect.equal "the same roles" roles

      testCase "a project parsed by hand keeps its fallback cause, which already makes the session Degraded" <| fun _ ->
        let fallback = LoadMode.ManualFallback FallbackCause.ReturnedNothing
        let roles = [ role "/a/A.fsproj" fallback ]
        withConcerns [ "/a/A.fsproj", HostConcern.RuntimeUndetermined "x" ] roles
        |> List.map (fun r -> r.LoadMode)
        |> Expect.equal "unchanged" [ fallback ]

      testCase "the same concern twice is one concern, and concerns accumulate across calls" <| fun _ ->
        let first = HostConcern.RuntimeUndetermined "x"
        let second = HostConcern.FSharpCoreRewriteFailed("A.dll", "y")
        let roles = [ role "/a/A.fsproj" LoadMode.Evaluated ]
        roles
        |> withConcerns [ "/a/A.fsproj", first; "/a/A.fsproj", first ]
        |> withConcerns [ "/a/A.fsproj", second ]
        |> List.map (fun r -> r.LoadMode)
        |> Expect.equal "both, once each" [ LoadMode.EvaluatedWithConcerns [ first; second ] ]
    ]

    testList "health" [
      testCase "WHY — a Ready session with a project that carries a concern is Degraded with the concern's reason, not Healthy" <| fun _ ->
        for concern in everyConcern do
          let health =
            SessionHealth.classify
              (SessionLifecycleStatus.Ready handle)
              [ role "/repo/App/App.fsproj" (LoadMode.EvaluatedWithConcerns [ concern ]) ]
              None
          match health with
          | SessionHealth.Degraded reason ->
            reason |> Expect.stringContains "names the project" "App.fsproj"
            reason |> Expect.stringContains "carries the concern's own words" (HostConcern.describe concern)
          | other -> failtestf "expected Degraded for %A, got %A" concern other

      testCase "a project with no concern leaves the session Healthy, so the quiet case stays quiet" <| fun _ ->
        SessionHealth.classify (SessionLifecycleStatus.Ready handle) [ role "/repo/App/App.fsproj" LoadMode.Evaluated ] None
        |> Expect.equal "healthy" SessionHealth.Healthy
    ]
  ]
