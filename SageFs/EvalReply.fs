namespace SageFs

/// What an eval's reply text carries beyond its output: the cost line (docs/plans/eval-cost-line.md).
[<RequireQualifiedAccess>]
module EvalReply =

  /// The cost line an eval's reply metadata earns: the time in the host, the bytes allocated and the GC counts, when the eval was
  /// slow or allocation-heavy enough (`EvalCost.productThresholds`). A fast, light eval, a reply with no cost, and a cost that
  /// does not read back all earn none.
  let costLine (thresholds: CostThresholds) (metadata: Map<string, string>) : string option =
    metadata
    |> Map.tryFind EvalCost.MetadataKey
    |> Option.bind (fun json ->
      match WorkerProtocol.Serialization.tryDeserialize<EvalCost> json with
      | Result.Ok cost -> EvalCost.line thresholds cost
      | Result.Error _ -> None)

  /// `text` with the eval's cost line on a line of its own at the end, or `text` as it was when the eval earned none. Used for
  /// a failed eval as much as a successful one: a failure that ran for a minute is where the cost is most wanted.
  let withCostUnder (thresholds: CostThresholds) (metadata: Map<string, string>) (text: string) : string =
    match costLine thresholds metadata with
    | None -> text
    | Some line -> sprintf "%s\n%s" (text.TrimEnd()) line

  /// `withCostUnder` the product's thresholds.
  let withCost (metadata: Map<string, string>) (text: string) : string =
    withCostUnder EvalCost.productThresholds metadata text
