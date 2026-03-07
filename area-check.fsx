type TRect = { X: float32; Z: float32; W: float32; H: float32 }
module TRect =
  let area r = r.W * r.H
  let create x z w h : TRect = { X = x; Z = z; W = w; H = h }
let squarifiedTreemap (items: (string * float32) list) (bounds: TRect) : (string * TRect) list =
  match items with
  | [] -> []
  | _ ->
  let totalWeight = items |> List.sumBy snd
  if totalWeight <= 0.0f then []
  else
  let totalArea = TRect.area bounds
  let normalized = items |> List.sortByDescending snd |> List.map (fun (name, w) -> name, (w / totalWeight) * totalArea)
  let results = ResizeArray<string * TRect>()
  let rec layout (remaining: (string * float32) list) (rect: TRect) =
    match remaining with
    | [] -> ()
    | [(name, _)] -> results.Add((name, rect))
    | _ ->
    let isWide = rect.W >= rect.H
    let sideLen = if isWide then rect.H else rect.W
    let worstRatio (row: (string * float32) list) =
      let rowArea = row |> List.sumBy snd
      let rowLen = rowArea / sideLen
      row |> List.map (fun (_, a) -> let itemLen = a / rowLen in max (itemLen / sideLen) (sideLen / itemLen)) |> List.max
    let rec fillRow (row: (string * float32) list) (rest: (string * float32) list) =
      match rest with
      | [] -> row, []
      | next :: tail ->
        let candidate = row @ [next]
        match row with
        | [] -> fillRow candidate tail
        | _ -> if worstRatio candidate <= worstRatio row then fillRow candidate tail else row, rest
    let row, rest = fillRow [] remaining
    let rowArea = row |> List.sumBy snd
    let rowLen = rowArea / sideLen
    let mutable offset = 0.0f
    for (name, area) in row do
      let itemLen = area / rowLen
      let r = if isWide then TRect.create rect.X (rect.Z + offset) rowLen itemLen else TRect.create (rect.X + offset) rect.Z itemLen rowLen
      results.Add((name, r))
      offset <- offset + itemLen
    let nextRect = if isWide then TRect.create (rect.X + rowLen) rect.Z (rect.W - rowLen) rect.H else TRect.create rect.X (rect.Z + rowLen) rect.W (rect.H - rowLen)
    layout rest nextRect
  layout normalized bounds
  results |> Seq.toList
let r10 = squarifiedTreemap [ for i in 1..1000 -> sprintf ""fn%d"" i, float32 (1 + i % 50) ] (TRect.create 0.0f 0.0f 500.0f 500.0f)
let total10 = r10 |> List.sumBy (fun (_, r) -> TRect.area r)
printfn ""Total area: %f (expected 250000)"" total10
printfn ""Diff: %f"" (abs (total10 - 250000.0f))
