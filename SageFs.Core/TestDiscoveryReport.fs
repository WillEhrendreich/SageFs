namespace SageFs

open SageFs.WorkerProtocol

/// What a worker said when asked for its tests.
[<RequireQualifiedAccess>]
type TestDiscoveryReport =
  | Discovered of tests: Features.LiveTesting.TestCase array * providers: Features.LiveTesting.ProviderDescription list
  | DiscoveryFailed of reason: string

module TestDiscoveryReport =
  let ofResponse (response: WorkerResponse) : TestDiscoveryReport =
    match response with
    | WorkerResponse.InitialTestDiscovery (tests, providers) -> TestDiscoveryReport.Discovered (tests, providers)
    | WorkerResponse.WorkerError err -> TestDiscoveryReport.DiscoveryFailed (SageFsError.describe err)
    | other ->
      let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(other, typeof<WorkerResponse>)
      TestDiscoveryReport.DiscoveryFailed (sprintf "Unexpected reply to test discovery: %s" case.Name)
