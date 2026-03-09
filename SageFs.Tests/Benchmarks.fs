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

// 6. CellGrid overlay (monoid composition)
type CellGridOverlay() =
  let mutable baseGrid = CellGrid.create 1 1
  let mutable overlayGrid = CellGrid.create 1 1

  [<Params(60, 120)>]
  member val Rows = 0 with get, set

  [<Params(200, 400)>]
  member val Cols = 0 with get, set

  [<GlobalSetup>]
  member this.Setup() =
    baseGrid <- CellGrid.rent this.Rows this.Cols
    overlayGrid <- CellGrid.rent this.Rows this.Cols
    // Fill base with content
    for i in 0 .. baseGrid.Cells.Length - 1 do
      baseGrid.Cells.[i] <- { Char = 'A'; Fg = 0xFFFFFFu; Bg = 0u; Attrs = CellAttrs.None }
    // Fill overlay sparsely (25% coverage)
    for i in 0 .. overlayGrid.Cells.Length - 1 do
      match i % 4 = 0 with
      | true -> overlayGrid.Cells.[i] <- { Char = 'B'; Fg = 0xFF0000u; Bg = 0u; Attrs = CellAttrs.None }
      | false -> ()

  [<Benchmark>]
  member _.Overlay() =
    CellGrid.overlay baseGrid overlayGrid

// 7. AnsiEmitter diff throughput
type AnsiEmitDiff() =
  let mutable prev = CellGrid.create 1 1
  let mutable curr = CellGrid.create 1 1

  [<Params(60, 120)>]
  member val Rows = 0 with get, set

  [<Params(200)>]
  member val Cols = 0 with get, set

  [<GlobalSetup>]
  member this.Setup() =
    prev <- CellGrid.rent this.Rows this.Cols
    curr <- CellGrid.rent this.Rows this.Cols
    for i in 0 .. prev.Cells.Length - 1 do
      prev.Cells.[i] <- { Char = 'A'; Fg = 0xFFFFFFu; Bg = 0u; Attrs = CellAttrs.None }
      curr.Cells.[i] <- prev.Cells.[i]
    // Change 5% of cells (typical typing scenario)
    let rng = Random(42)
    for _ in 0 .. (curr.Cells.Length * 5 / 100) - 1 do
      let idx = rng.Next(curr.Cells.Length)
      curr.Cells.[idx] <- { Char = 'X'; Fg = 0x00FF00u; Bg = 0u; Attrs = CellAttrs.None }

  [<Benchmark(Baseline = true)>]
  member _.FullEmit() =
    AnsiEmitter.emit curr 0 0

  [<Benchmark>]
  member _.DiffEmit() =
    AnsiEmitter.emitDiff prev curr 0 0

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
        .AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.Full)
    let types =
      [| typeof<CellGridAllocation>
         typeof<MsgLabelThroughput>
         typeof<ManifestRoundtrip>
         typeof<ErrorDescribe>
         typeof<SseFormatting>
         typeof<CellGridOverlay>
         typeof<AnsiEmitDiff> |]
    BenchmarkSwitcher.FromTypes(types).Run(argv, config) |> ignore
    0
