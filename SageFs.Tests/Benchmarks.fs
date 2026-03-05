module SageFs.Tests.Benchmarks

open System
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open BenchmarkDotNet.Toolchains.InProcess.Emit
open SageFs
open SageFs.Features
open SageFs.Features.ManifestTypes

// 1. CellGrid allocation: create (heap) vs rent (ArrayPool)
type CellGridAllocation() =
  [<Params(60, 120)>]
  member val Rows = 0 with get, set

  [<Params(200, 400)>]
  member val Cols = 0 with get, set

  [<Benchmark(Baseline = true)>]
  member this.HeapCreate() =
    CellGrid.create this.Rows this.Cols

  [<Benchmark>]
  member this.PoolRent() =
    let g = CellGrid.rent this.Rows this.Cols
    CellGrid.release g
    g

// 2. ElmLoop.msgLabel: cached reflection label throughput
type BenchMsg =
  | Alpha of int
  | Beta of string
  | Gamma of nested: BenchMsg

type MsgLabelThroughput() =
  let msgs: obj[] =
    [| Alpha 1; Beta "x"; Gamma (Alpha 2) |]
    |> Array.map box

  [<Benchmark>]
  member _.CachedLabel() =
    let mutable last = ""
    for m in msgs do
      last <- ElmLoop.msgLabel m
    last

// 3. ManifestPersistence roundtrip
type ManifestRoundtrip() =
  let testData: DaemonManifestData =
    { Entries =
        [ for i in 1..10 do
            { SessionId = sprintf "sess-%d" i
              Projects = [ sprintf "Proj%d.fsproj" i ]
              WorkingDir = sprintf "C:\\Code\\Proj%d" i
              CreatedAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
              StoppedAt = None } ]
      ActiveSessionId = Some "sess-1"
      CreatedAtMs = 1735689600000L }

  let mutable bytes = ManifestWriter.write testData

  [<Benchmark>]
  member _.Write() =
    ManifestWriter.write testData

  [<Benchmark>]
  member _.Read() =
    ManifestReader.read bytes

  [<Benchmark>]
  member _.Roundtrip() =
    let b = ManifestWriter.write testData
    ManifestReader.read b

// 4. SageFsError.describe throughput
type ErrorDescribe() =
  let cases: SageFsError[] =
    [| SageFsError.NoActiveSessions
       SageFsError.SessionNotFound "test-id"
       SageFsError.SessionCreationFailed "timeout"
       SageFsError.ToolNotAvailable("eval", SessionState.Ready, [ "reset" ]) |]

  [<Benchmark>]
  member _.DescribeAll() =
    let mutable last = ""
    for e in cases do
      last <- SageFsError.describe e
    last

// 5. SSE event formatting
type SseFormatting() =
  let sampleData = """{"sessionState":"Ready","evalCount":42}"""

  [<Benchmark>]
  member _.FormatEvent() =
    SseWriter.formatSseEvent "state" sampleData

  [<Benchmark>]
  member _.FormatRetryHint() =
    SseWriter.formatRetryHint 3000

module BenchmarkRunner =
  let run (argv: string[]) =
    let config =
      ManualConfig.Create(DefaultConfig.Instance)
        .AddJob(
          Job.ShortRun
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(10))
        .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default)
    let types =
      [| typeof<CellGridAllocation>
         typeof<MsgLabelThroughput>
         typeof<ManifestRoundtrip>
         typeof<ErrorDescribe>
         typeof<SseFormatting> |]
    BenchmarkSwitcher.FromTypes(types).Run(argv, config) |> ignore
    0
