module SageFs.Tests.FsiProtocolThroughputTests

/// How fast the FSI host protocol's codec reads and writes the reply that grows late in a live-testing session.
///
/// Measured in production: an `AgentAfterEvalResult` grows to about 276 KB (every discovered test, every redirected
/// method), and the worker spent 262 to 331 ms (up to 1.5 s) decoding one, about 1 ms per KB. The codec reasoned
/// about the type AGAIN for every value (reflection per record, per union case, per list), which is where that time
/// went. The floors below hold the codec to a plan worked out once per type.
///
/// Each figure is the best of several runs after a warm-up, so one slow run on a loaded machine (the gate runs its
/// tiers in parallel at a load of 16 to 40) cannot fail it. The floors sit well under what an idle machine reaches
/// and far over what the per-value reflection reaches.

open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs.HostAgent
open SageFs.Features.LiveTesting
open SageFs.Middleware.EntryProbes
open SageFs.Middleware.HotReloadCore
open SageFs.FsiHost.FsiProtocol

/// Discovered tests in the reply: the bulk of its bytes.
let private discoveredTests = 600

/// Redirected methods, and the probes, lists and names that go with them.
let private redirectedMethods = 130

let private affectedTests = 50

/// Runs of the codec thrown away before timing, so the plans and the JIT are warm.
let private warmUpRuns = 2

/// Timed runs; the fastest counts.
let private timedRuns = 7

/// The size of the reply the production samples describe, in bytes: any measured reply must be in this band, so the
/// floors are about a reply of that size and not about a toy.
let private smallestRealisticReply = 200000
let private largestRealisticReply = 330000

/// Floors, in megabytes of JSON per second. Each is about the geometric mean of what the per-value reflection reaches
/// (decode 0.8 to 1.1, encode 0.8 to 1.7) and what the plan codec reaches idle (decode about 64, encode about 270),
/// so a loaded machine still clears them with a wide margin and the old codec cannot.
let private decodeFloorMBps = 8.0
let private encodeFloorMBps = 18.0

let private bytesPerMegabyte = 1048576.0

let private mkTest (index: int) : TestCase =
  { Id = TestId.TestId(sprintf "%016x" (index * 7919))
    FullName = sprintf "SageFs.Tests.Module%d.Group%d/nested list %d/case number %d does the thing" (index / 50) (index / 10) (index % 7) index
    DisplayName = sprintf "case number %d does the thing" index
    Origin =
      (match index % 5 with
       | 0 -> TestOrigin.ReflectionOnly
       | _ -> TestOrigin.SourceMapped(sprintf "/home/will/Work/SageFs/SageFs.Tests/Module%d.fs" (index / 50), 10 + index))
    Labels =
      (match index % 3 with
       | 0 -> []
       | 1 -> [ "unit" ]
       | _ -> [ "property"; "slow" ])
    Framework = TestFramework.Expecto
    Category =
      (match index % 4 with
       | 0 -> TestCategory.Unit
       | 1 -> TestCategory.Integration
       | 2 -> TestCategory.Property
       | _ -> TestCategory.Custom "wire") }

let private methodName (index: int) = sprintf "SageFs.Core.Module%d.function%d" (index / 20) index

/// What a long live-testing session sends after one save: every discovered test, and the whole detour report.
let private lateSessionReply () : Response =
  let report: AfterEvalReport =
    { UpdatedMethods = [ for index in 0 .. redirectedMethods - 1 -> methodName index ]
      DetourReport =
        { Redirected = [ for index in 0 .. redirectedMethods - 1 -> methodName index ]
          ReachedRunningProcess = [ for index in 0 .. redirectedMethods / 2 -> methodName index ]
          Ineffective = [ "SageFs.X.ineffective" ]
          RedirectedFromCompiled = [ for index in 0 .. redirectedMethods / 3 -> methodName index ]
          CompiledCandidates = [ for index in 0 .. redirectedMethods - 1 -> methodName index ]
          Probes = [ for index in 0 .. redirectedMethods - 1 -> { Id = int64 (1000 + index); Declaration = methodName index } ]
          Bindings = [ BindingOutcome.BothLegsRedirected "a.b"; BindingOutcome.Torn("c.d", "reason text") ]
          Declined = [ { Binding = "x.y"; Orphan = OrphanedLeg.GetterWithoutSetter } ]
          Failures = [ "one failure" ]
          Refusals = [ DetourRefusal.ClosureShapeChanged("decl", "detail") ] }
      LiveTest =
        { DetectedProviders =
            [ ProviderDescription.AttributeBased
                { Name = TestFramework.Expecto
                  TestAttributes = [ "Tests"; "Test" ]
                  AssemblyMarker = "Expecto" } ]
          DiscoveredTests = [| for index in 0 .. discoveredTests - 1 -> mkTest index |]
          AffectedTestIds = [| for index in 0 .. affectedTests - 1 -> TestId.TestId(sprintf "%016x" (index * 7919)) |]
          ChangedSymbolNames = [| "a"; "b" |] }
      AssemblyLoadErrors = [ AssemblyLoadError.FileNotFound("/x/y.dll", "missing") ] }
  AgentAfterEvalResult(1L, report)

/// The fastest of `timedRuns` runs of `work`, in seconds, after `warmUpRuns` it does not count.
let private fastestSeconds (work: unit -> unit) : float =
  for _ in 1..warmUpRuns do
    work ()
  [ for _ in 1..timedRuns ->
      let clock = Stopwatch.StartNew()
      work ()
      clock.Elapsed.TotalSeconds ]
  |> List.min

let private megabytesPerSecond (bytes: int) (seconds: float) = float bytes / bytesPerMegabyte / seconds

[<Tests>]
let tests =
  testList "FsiProtocol throughput" [
    testCase "the measured reply is the size of a late session's, and reads back as itself" <| fun _ ->
      let reply = lateSessionReply ()
      let wire = encodeResponse reply
      Expect.isGreaterThanOrEqual "not smaller than a real reply" (wire.Length, smallestRealisticReply)
      Expect.isLessThanOrEqual "not larger than a real reply" (wire.Length, largestRealisticReply)
      Expect.equal "the wire reads back as the reply" (Result.Ok reply) (decodeResponse wire)

    testCase "decoding a late session's AgentAfterEvalResult reaches the floor in MB/s" <| fun _ ->
      let wire = encodeResponse (lateSessionReply ())
      let seconds = fastestSeconds (fun () -> decodeResponse wire |> ignore)
      let rate = megabytesPerSecond wire.Length seconds
      printfn "decode: %d bytes in %.1f ms = %.2f MB/s (floor %.2f)" wire.Length (seconds * 1000.0) rate decodeFloorMBps
      Expect.isGreaterThanOrEqual (sprintf "decode must reach %.2f MB/s, got %.2f" decodeFloorMBps rate) (rate, decodeFloorMBps)

    testCase "encoding a late session's AgentAfterEvalResult reaches the floor in MB/s" <| fun _ ->
      let reply = lateSessionReply ()
      let bytes = (encodeResponse reply).Length
      let seconds = fastestSeconds (fun () -> encodeResponse reply |> ignore)
      let rate = megabytesPerSecond bytes seconds
      printfn "encode: %d bytes in %.1f ms = %.2f MB/s (floor %.2f)" bytes (seconds * 1000.0) rate encodeFloorMBps
      Expect.isGreaterThanOrEqual (sprintf "encode must reach %.2f MB/s, got %.2f" encodeFloorMBps rate) (rate, encodeFloorMBps)
  ]
