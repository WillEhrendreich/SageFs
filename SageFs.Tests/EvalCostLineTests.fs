/// The cost line of docs/plans/eval-cost-line.md: a slow or allocation-heavy eval ends its reply with one line
/// carrying the time, the bytes allocated and the GC counts. A quick, light eval carries nothing.
module SageFs.Tests.EvalCostLineTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs

let private thresholds = EvalCost.productThresholds

/// A duration the test makes up as a cost's elapsed time; nothing waits on it.
let private seconds (value: float) : TimeSpan = TimeSpan.FromSeconds value

let private costOf (elapsedMs: int64) (allocated: int64) (gen: PositiveInt) : EvalCost =
  { Elapsed = TimeSpan.FromMilliseconds(float elapsedMs)
    Allocated = allocated
    Gen0 = gen.Get % 1000
    Gen1 = gen.Get % 100
    Gen2 = gen.Get % 10 }

/// A cost under both thresholds, from a seed.
let private lightCost (seed: PositiveInt) : EvalCost =
  let slowMs = int64 thresholds.Slow.TotalMilliseconds
  costOf (int64 seed.Get % slowMs) (int64 seed.Get * 1_013L % thresholds.HeavyAllocation) seed

/// A cost at or over one threshold, from a seed. Even seeds are over the time threshold only, odd seeds over the allocation threshold only.
let private notableCost (seed: PositiveInt) (extra: PositiveInt) : EvalCost =
  let slowMs = int64 thresholds.Slow.TotalMilliseconds
  match seed.Get % 2 with
  | 0 -> costOf (slowMs + int64 extra.Get % 120000L) (int64 seed.Get * 1_013L % thresholds.HeavyAllocation) seed
  | _ -> costOf (int64 seed.Get % slowMs) (thresholds.HeavyAllocation + int64 extra.Get * 4_099L) seed

[<Tests>]
let evalCostLineTests =
  testList "Eval cost line" [

    testProperty "WHY - an eval under both thresholds carries no cost line" <| fun (seed: PositiveInt) ->
      EvalCost.line thresholds (lightCost seed) = None

    testCase "WHY - an eval exactly at a threshold is annotated, because the threshold is inclusive" <| fun _ ->
      let atTime = { EvalCost.unmeasured with Elapsed = thresholds.Slow }
      let atBytes = { EvalCost.unmeasured with Allocated = thresholds.HeavyAllocation }
      EvalCost.line thresholds atTime |> Expect.isSome "a time at the threshold gets a line"
      EvalCost.line thresholds atBytes |> Expect.isSome "an allocation at the threshold gets a line"

    testProperty "WHY - an eval at or over either threshold carries one line with all five figures" <| fun (seed: PositiveInt) (extra: PositiveInt) ->
      let cost = notableCost seed extra
      match EvalCost.line thresholds cost |> Option.bind EvalCost.tryParseLine with
      | None -> false
      | Some back ->
        let elapsedOff = abs (back.Elapsed - cost.Elapsed).TotalMilliseconds
        let sameCounts = back.Gen0 = cost.Gen0 && back.Gen1 = cost.Gen1 && back.Gen2 = cost.Gen2
        // The line prints the time to the hundredth of a second and the bytes to a tenth of their unit, so the figures come back to that precision.
        sameCounts && elapsedOff <= 5.0 + 1e-6

    testProperty "WHY - the line read back and written again is the same line, so the reader and the writer cannot drift" <| fun (seed: PositiveInt) (extra: PositiveInt) ->
      let cost = notableCost seed extra
      let first = EvalCost.line thresholds cost
      let second = first |> Option.bind EvalCost.tryParseLine |> Option.bind (EvalCost.line thresholds)
      first.IsSome && first = second

    testCase "WHY - the line has the shape FsiMcp 0.3.0 prints, so an agent that knows one knows the other" <| fun _ ->
      let cost = { Elapsed = seconds 0.84; Allocated = 312400000L; Gen0 = 14; Gen1 = 3; Gen2 = 1 }
      EvalCost.line thresholds cost
      |> Expect.equal "the shape" (Some "[0.84 s · 312.4 MB allocated · GCs gen0 14 / gen1 3 / gen2 1]")

    testCase "WHY - a reading that went backwards is a zero, never a negative figure" <| fun _ ->
      let cost = EvalCost.measured (TimeSpan.FromSeconds -1.0) (500L, 100L) (5, 3) (2, 2) (9, 1)
      cost |> Expect.equal "all zero" EvalCost.unmeasured
  ]

/// The reply metadata the worker sends the daemon: the cost as the wire's JSON text.
let private metadataOf (cost: EvalCost) : Map<string, string> =
  Map.ofList [ EvalCost.MetadataKey, SageFs.WorkerProtocol.Serialization.serialize cost ]

let private textOf (result: Result<string, SageFsError>) (metadata: Map<string, string>) : string =
  SageFs.McpTools.formatWorkerEvalResult
    SageFs.WorkflowTypes.SessionWorkflow.Interactive
    (SageFs.WorkerProtocol.WorkerResponse.EvalResult("r", result, [], metadata))

[<Tests>]
let evalCostReplyTests =
  testList "Eval cost line in the reply" [

    testProperty "WHY - the structured cost and the text line agree: the line is the one `EvalCost.line` writes for the cost the host measured" <| fun (seed: PositiveInt) (extra: PositiveInt) ->
      let cost = notableCost seed extra
      let text = textOf (Result.Ok "val it: int = 1\n\n") (metadataOf cost)
      match EvalCost.line thresholds cost with
      | Some line -> text.EndsWith line && text.Contains "val it: int = 1"
      | None -> false

    testProperty "WHY - a reply to a light eval is exactly the text it would have been without a cost" <| fun (seed: PositiveInt) ->
      textOf (Result.Ok "val it: int = 1\n\n") (metadataOf (lightCost seed)) = textOf (Result.Ok "val it: int = 1\n\n") Map.empty

    testProperty "WHY - a failed eval carries its cost, because a failure that ran for a minute is where it is most wanted" <| fun (seed: PositiveInt) (extra: PositiveInt) ->
      let cost = notableCost seed extra
      let text = textOf (Result.Error(SageFsError.EvalFailed "boom")) (metadataOf cost)
      match EvalCost.line thresholds cost with
      | Some line -> text.Contains "boom" && text.EndsWith line
      | None -> false

    testCase "WHY - a cost that does not read back earns no line and breaks nothing" <| fun _ ->
      let text = textOf (Result.Ok "val it: int = 1\n\n") (Map.ofList [ EvalCost.MetadataKey, "not json" ])
      text |> Expect.equal "the plain reply" (textOf (Result.Ok "val it: int = 1\n\n") Map.empty)

    testCase "WHY - the line comes after the output and before the diagnostics, so the footer stays last" <| fun _ ->
      let cost = { EvalCost.unmeasured with Elapsed = seconds 2.0 }
      let diag : SageFs.WorkerProtocol.WorkerDiagnostic =
        { Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Warning; Message = "careful"; StartLine = 0; StartColumn = 0; EndLine = 0; EndColumn = 0; ErrorNumber = 0 }
      let text =
        SageFs.McpTools.formatWorkerEvalResult
          SageFs.WorkflowTypes.SessionWorkflow.Interactive
          (SageFs.WorkerProtocol.WorkerResponse.EvalResult("r", Result.Ok "val it: int = 1\n\n", [ diag ], metadataOf cost))
      let at (needle: string) = text.IndexOf(needle, StringComparison.Ordinal)
      (at "val it" < at "[2.00 s" && at "[2.00 s" < at "Diagnostics:") |> Expect.isTrue (sprintf "order in: %s" text)
  ]
