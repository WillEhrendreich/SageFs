namespace SageFs.Features.LiveTesting

// Moved out of LiveTestingTypes.fs, which is under a line budget: nothing in that file or in the FSI host reads it.

type CoverageSummary = {
  TotalProbes: int
  CoveredProbes: int
  CoveragePercent: float
  /// Downsampled density array — each element is coverage ratio in that chunk
  DensityStrip: float array
}

module CoverageSummary =
  let empty = { TotalProbes = 0; CoveredProbes = 0; CoveragePercent = 0.0; DensityStrip = [||] }

  /// Build a coverage summary from bitmaps belonging to a session.
  /// Merges all bitmaps via OR to get total coverage, then downsamples for rendering.
  let fromBitmaps (stripWidth: int) (bitmaps: CoverageBitmap seq) : CoverageSummary =
    let bmArray = bitmaps |> Seq.filter (fun bm -> bm.Count > 0) |> Seq.toArray
    match bmArray.Length with
    | 0 -> empty
    | _ ->
      let first = bmArray.[0]
      let compatible = bmArray |> Array.filter (fun bm -> bm.Count = first.Count)
      match compatible.Length with
      | 0 -> empty
      | _ ->
        let combined =
          compatible
          |> Array.reduce (fun acc bm ->
            let bits = Array.init acc.Bits.Length (fun i -> acc.Bits.[i] ||| bm.Bits.[i])
            { Bits = bits; Count = acc.Count })
        let totalProbes = combined.Count
        let coveredProbes = CoverageBitmap.popCount combined
        let coveragePercent =
          match totalProbes with
          | 0 -> 0.0
          | n -> float coveredProbes / float n * 100.0
        let bools = CoverageBitmap.toBoolArray combined
        let chunkSize = max 1 (totalProbes / stripWidth)
        let strip =
          [| for i in 0 .. stripWidth - 1 do
               let startIdx = i * chunkSize
               let endIdx = min (startIdx + chunkSize) totalProbes
               match endIdx > startIdx with
               | true ->
                 let chunk = bools.[startIdx .. endIdx - 1]
                 let hits = chunk |> Array.filter id |> Array.length
                 float hits / float chunk.Length
               | false -> 0.0 |]
        { TotalProbes = totalProbes
          CoveredProbes = coveredProbes
          CoveragePercent = coveragePercent
          DensityStrip = strip }

  /// Render compact text strip for TUI: █▓▒░· per chunk + percentage
  let toTextStrip (summary: CoverageSummary) : string =
    match summary.TotalProbes with
    | 0 -> ""
    | _ ->
      let blocks =
        summary.DensityStrip
        |> Array.map (fun d ->
          match d with
          | x when x >= 0.75 -> '\u2588'
          | x when x >= 0.50 -> '\u2593'
          | x when x >= 0.25 -> '\u2592'
          | x when x > 0.0 -> '\u2591'
          | _ -> '\u00B7')
      sprintf "%s %.0f%%" (System.String(blocks)) summary.CoveragePercent
