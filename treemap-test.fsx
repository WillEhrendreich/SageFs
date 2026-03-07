type TRect =
  { X: float32; Z: float32; W: float32; H: float32 }

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
  let normalized =
    items
    |> List.sortByDescending snd
    |> List.map (fun (name, w) -> name, (w / totalWeight) * totalArea)
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
      row |> List.map (fun (_, a) ->
        let itemLen = a / rowLen
        max (itemLen / sideLen) (sideLen / itemLen))
      |> List.max
    let rec fillRow (row: (string * float32) list) (rest: (string * float32) list) =
      match rest with
      | [] -> row, []
      | next :: tail ->
        let candidate = row @ [next]
        match row with
        | [] -> fillRow candidate tail
        | _ ->
          if worstRatio candidate <= worstRatio row then
            fillRow candidate tail
          else
            row, rest
    let row, rest = fillRow [] remaining
    let rowArea = row |> List.sumBy snd
    let rowLen = rowArea / sideLen
    let mutable offset = 0.0f
    for (name, area) in row do
      let itemLen = area / rowLen
      let r =
        if isWide then
          TRect.create rect.X (rect.Z + offset) rowLen itemLen
        else
          TRect.create (rect.X + offset) rect.Z itemLen rowLen
      results.Add((name, r))
      offset <- offset + itemLen
    let nextRect =
      if isWide then
        TRect.create (rect.X + rowLen) rect.Z (rect.W - rowLen) rect.H
      else
        TRect.create rect.X (rect.Z + rowLen) rect.W (rect.H - rowLen)
    layout rest nextRect
  layout normalized bounds
  results |> Seq.toList

// ── Tests ──
let mutable passed = 0
let mutable failed = 0
let check (name: string) (cond: bool) =
  if cond then passed <- passed + 1; printfn "  ✅ %s" name
  else failed <- failed + 1; printfn "  ❌ %s" name

let approxEq (exp: float32) (act: float32) = abs (act - exp) < 0.1f

printfn "=== Squarified Treemap Tests ==="

// 1. Empty
let r1 = squarifiedTreemap [] (TRect.create 0.0f 0.0f 100.0f 100.0f)
check "empty input → empty output" (r1.IsEmpty)

// 2. Single item fills bounds
let r2 = squarifiedTreemap [("A", 50.0f)] (TRect.create 10.0f 20.0f 80.0f 60.0f)
check "single item count" (r2.Length = 1)
let (n2, rect2) = r2.[0]
check "single item name" (n2 = "A")
check "single item X" (approxEq 10.0f rect2.X)
check "single item Z" (approxEq 20.0f rect2.Z)
check "single item W" (approxEq 80.0f rect2.W)
check "single item H" (approxEq 60.0f rect2.H)

// 3. Two equal items
let bounds3 = TRect.create 0.0f 0.0f 100.0f 50.0f
let r3 = squarifiedTreemap [("A", 1.0f); ("B", 1.0f)] bounds3
check "two equal items count" (r3.Length = 2)
let total3 = r3 |> List.sumBy (fun (_, r) -> TRect.area r)
check "two equal total area" (approxEq 5000.0f total3)
let areaA3 = r3 |> List.find (fun (n, _) -> n = "A") |> snd |> TRect.area
let areaB3 = r3 |> List.find (fun (n, _) -> n = "B") |> snd |> TRect.area
check "A gets half" (approxEq 2500.0f areaA3)
check "B gets half" (approxEq 2500.0f areaB3)

// 4. Areas proportional to weights
let r4 = squarifiedTreemap [("big", 300.0f); ("med", 100.0f); ("small", 50.0f); ("tiny", 10.0f)] (TRect.create 0.0f 0.0f 100.0f 100.0f)
check "4 items count" (r4.Length = 4)
let lk4 n = r4 |> List.find (fun (name, _) -> name = n) |> snd |> TRect.area
check "big > med" (lk4 "big" > lk4 "med")
check "med > small" (lk4 "med" > lk4 "small")
check "small > tiny" (lk4 "small" > lk4 "tiny")
let ratio4 = lk4 "big" / lk4 "med"
check (sprintf "big/med ratio ~3 (got %.2f)" ratio4) (ratio4 > 2.5f && ratio4 < 3.5f)

// 5. No overlap (20 items)
let r5 = squarifiedTreemap [ for i in 1..20 -> sprintf "f%d" i, float32 (i * 10) ] (TRect.create 0.0f 0.0f 200.0f 200.0f)
let rects5 = r5 |> List.map snd
let overlaps5 =
  [ for i in 0 .. rects5.Length - 2 do
      for j in i + 1 .. rects5.Length - 1 do
        let a = rects5.[i]
        let b = rects5.[j]
        let ox = max 0.0f (min (a.X + a.W) (b.X + b.W) - max a.X b.X)
        let oz = max 0.0f (min (a.Z + a.H) (b.Z + b.H) - max a.Z b.Z)
        if ox > 0.01f && oz > 0.01f then yield (i, j) ]
check "no overlaps (20 items)" (overlaps5.IsEmpty)

// 6. All within bounds
let bounds6 = TRect.create 5.0f 10.0f 150.0f 100.0f
let r6 = squarifiedTreemap [ for i in 1..15 -> sprintf "fn%d" i, float32 (50 + i * 5) ] bounds6
let inBounds6 = r6 |> List.forall (fun (_, r) ->
  r.X >= bounds6.X - 0.01f && r.Z >= bounds6.Z - 0.01f &&
  r.X + r.W <= bounds6.X + bounds6.W + 0.1f &&
  r.Z + r.H <= bounds6.Z + bounds6.H + 0.1f)
check "all within bounds" inBounds6

// 7. Total area = bounds area
let bounds7 = TRect.create 0.0f 0.0f 120.0f 80.0f
let r7 = squarifiedTreemap [ for i in 1..10 -> sprintf "x%d" i, float32 i ] bounds7
let total7 = r7 |> List.sumBy (fun (_, r) -> TRect.area r)
check "total area = bounds area" (approxEq (TRect.area bounds7) total7)

// 8. Squarified aspect ratios
let r8 = squarifiedTreemap [ for i in 1..8 -> sprintf "b%d" i, float32 (10 + i * 5) ] (TRect.create 0.0f 0.0f 100.0f 100.0f)
let maxAR8 = r8 |> List.map (fun (_, r) -> max (r.W / r.H) (r.H / r.W)) |> List.max
check (sprintf "worst AR %.2f < 6" maxAR8) (maxAR8 < 6.0f)

// 9. All names present
let r9 = squarifiedTreemap [("alpha", 10.0f); ("beta", 20.0f); ("gamma", 5.0f)] (TRect.create 0.0f 0.0f 50.0f 50.0f)
let names9 = r9 |> List.map fst |> Set.ofList
check "all names present" (names9 = Set.ofList ["alpha"; "beta"; "gamma"])

// 10. 1000 items
let r10 = squarifiedTreemap [ for i in 1..1000 -> sprintf "fn%d" i, float32 (1 + i % 50) ] (TRect.create 0.0f 0.0f 500.0f 500.0f)
check "1000 items count" (r10.Length = 1000)
let total10 = r10 |> List.sumBy (fun (_, r) -> TRect.area r)
check "1000 items total area" (abs (total10 - 250000.0f) < 100.0f)

printfn "\n=== Results: %d passed, %d failed ===" passed failed
if failed > 0 then exit 1
