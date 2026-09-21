namespace SageFs.Features.LiveTesting
open System
open System.IO
open System.Numerics
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open SageFs.Measures

// Split from LiveTestingTypes.fs (file-size ratchet): the dashboard treemap projection is its own slice.
module TestTreemap =
  /// Extract treemap entries from test status entries.
  /// Only includes tests with known durations (Passed/Failed).
  let fromStatusEntries (entries: TestStatusEntry array) : TestTreemapEntry array =
    entries
    |> Array.choose (fun e ->
      match e.Status with
      | TestRunStatus.Passed duration ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = duration.TotalMilliseconds; Status = TreemapStatus.Passed }
      | TestRunStatus.Failed (_, duration) ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = duration.TotalMilliseconds; Status = TreemapStatus.Failed }
      | TestRunStatus.Running ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = 0.0; Status = TreemapStatus.Running }
      | TestRunStatus.Skipped _ ->
        Some { DisplayName = e.DisplayName; FullName = e.FullName
               DurationMs = 0.0; Status = TreemapStatus.Skipped }
      | _ -> None)
    |> Array.sortByDescending (fun e -> e.DurationMs)

  /// Squarified treemap layout algorithm (Bruls, Huizing, van Wijk).
  /// Packs rectangles into a bounding box with near-square aspect ratios.
  /// Area of each rect is proportional to DurationMs.
  let layout (width: float) (height: float) (entries: TestTreemapEntry array) : TreemapRect array =
    match entries.Length with
    | 0 -> [||]
    | _ ->
      let totalMs = entries |> Array.sumBy (fun e -> max e.DurationMs 0.1)
      let totalArea = width * height
      // Normalize: each entry's area = (durationMs / totalMs) * totalArea
      let areas = entries |> Array.map (fun e -> max e.DurationMs 0.1 / totalMs * totalArea)
      let result = ResizeArray<TreemapRect>()
      let mutable x0 = 0.0
      let mutable y0 = 0.0
      let mutable w0 = width
      let mutable h0 = height
      let mutable idx = 0
      while idx < entries.Length do
        // Lay out a strip along the shorter side
        let isVertical = w0 >= h0
        let side = match isVertical with | true -> h0 | false -> w0
        // Greedily add entries to the current strip while aspect ratio improves
        let mutable stripArea = 0.0
        let mutable bestWorst = System.Double.MaxValue
        let mutable stripEnd = idx
        let mutable improving = true
        while stripEnd < entries.Length && improving do
          stripArea <- stripArea + areas.[stripEnd]
          let stripLen = stripArea / side
          // Worst aspect ratio in this strip
          let worstAspect =
            let mutable worst = 0.0
            for j in idx .. stripEnd do
              let cellLen = areas.[j] / stripLen
              let aspect = max (cellLen / stripLen) (stripLen / cellLen)
              worst <- max worst aspect
            worst
          match worstAspect <= bestWorst with
          | true ->
            bestWorst <- worstAspect
            stripEnd <- stripEnd + 1
          | false ->
            // Adding this entry made it worse — back off
            stripArea <- stripArea - areas.[stripEnd]
            improving <- false
        // If we added nothing (shouldn't happen, but safety), take one
        let stripEnd = match stripEnd = idx with | true -> idx + 1 | false -> stripEnd
        let stripLen = stripArea / side
        // Place entries in the strip
        let mutable offset = 0.0
        for j in idx .. stripEnd - 1 do
          let cellLen = areas.[j] / stripLen
          match isVertical with
          | true ->
            result.Add { Entry = entries.[j]; X = x0; Y = y0 + offset; W = stripLen; H = cellLen }
          | false ->
            result.Add { Entry = entries.[j]; X = x0 + offset; Y = y0; W = cellLen; H = stripLen }
          offset <- offset + cellLen
        // Shrink remaining area
        match isVertical with
        | true ->
          x0 <- x0 + stripLen
          w0 <- w0 - stripLen
        | false ->
          y0 <- y0 + stripLen
          h0 <- h0 - stripLen
        idx <- stripEnd
      result.ToArray()

