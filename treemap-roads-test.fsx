open System

type TRect =
  { X: float32; Z: float32; W: float32; H: float32 }

module TRect =
  let area r = r.W * r.H
  let create x z w h : TRect = { X = x; Z = z; W = w; H = h }
  let inset margin (r: TRect) =
    { X = r.X + margin; Z = r.Z + margin
      W = max 0.1f (r.W - 2.0f * margin)
      H = max 0.1f (r.H - 2.0f * margin) }

/// A road segment with endpoints and tier (8=boulevard, 4=avenue, 2=street, 1=alley)
type RoadSeg =
  { X1: float32; Z1: float32; X2: float32; Z2: float32; Tier: int }

/// Hierarchical treemap: recursively subdivides, emitting roads at each level.
/// Level 0 = districts (boulevard tier 8)
/// Level 1 = modules (avenue tier 4)
/// Level 2+ = functions (street tier 2, alley tier 1)
let hierarchicalTreemap
  (items: (string * float32) list)
  (bounds: TRect)
  (margin: float32)
  (tier: int)
  : (string * TRect) list * RoadSeg list =

  match items with
  | [] -> [], []
  | [(name, _)] -> [(name, TRect.inset margin bounds)], []
  | _ ->

  let totalWeight = items |> List.sumBy snd
  if totalWeight <= 0.0f then [], []
  else

  let totalArea = TRect.area bounds
  let normalized =
    items
    |> List.sortByDescending snd
    |> List.map (fun (name, w) -> name, (w / totalWeight) * totalArea)

  let placements = ResizeArray<string * TRect>()
  let roads = ResizeArray<RoadSeg>()

  let rec layout (remaining: (string * float32) list) (rect: TRect) =
    match remaining with
    | [] -> ()
    | [(name, _)] ->
      placements.Add((name, TRect.inset margin rect))
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

    // Place row items
    let mutable offset = 0.0f
    for (name, area) in row do
      let itemLen = area / rowLen
      let r =
        if isWide then
          TRect.create rect.X (rect.Z + offset) rowLen itemLen
        else
          TRect.create (rect.X + offset) rect.Z itemLen rowLen
      placements.Add((name, TRect.inset margin r))
      offset <- offset + itemLen

    // Emit road along the subdivision line between this row and the remainder
    match rest with
    | [] -> ()
    | _ ->
      if isWide then
        // Vertical subdivision line at rect.X + rowLen
        let roadX = rect.X + rowLen
        roads.Add({ X1 = roadX; Z1 = rect.Z; X2 = roadX; Z2 = rect.Z + rect.H; Tier = tier })
      else
        // Horizontal subdivision line at rect.Z + rowLen
        let roadZ = rect.Z + rowLen
        roads.Add({ X1 = rect.X; Z1 = roadZ; X2 = rect.X + rect.W; Z2 = roadZ; Tier = tier })

    let nextRect =
      if isWide then
        TRect.create (rect.X + rowLen) rect.Z (rect.W - rowLen) rect.H
      else
        TRect.create rect.X (rect.Z + rowLen) rect.W (rect.H - rowLen)
    layout rest nextRect

  layout normalized bounds
  (placements |> Seq.toList, roads |> Seq.toList)


// ── Tests ──
let mutable passed = 0
let mutable failed = 0
let check (name: string) (cond: bool) =
  if cond then passed <- passed + 1; printfn "  ✅ %s" name
  else failed <- failed + 1; printfn "  ❌ %s" name

let approxEq (exp: float32) (act: float32) = abs (act - exp) < 0.5f

printfn "=== Hierarchical Treemap + Roads Tests ==="

// 1. Single item → no roads
let (p1, r1) = hierarchicalTreemap [("A", 10.0f)] (TRect.create 0.0f 0.0f 100.0f 100.0f) 1.0f 8
check "single item: 1 placement" (p1.Length = 1)
check "single item: no roads" (r1.IsEmpty)
let (_, rect1) = p1.[0]
check "single item: inset applied" (rect1.X > 0.0f && rect1.Z > 0.0f)

// 2. Two items → one road
let (p2, r2) = hierarchicalTreemap [("A", 1.0f); ("B", 1.0f)] (TRect.create 0.0f 0.0f 100.0f 50.0f) 0.5f 4
check "two items: 2 placements" (p2.Length = 2)
check "two items: 1 road" (r2.Length = 1)
check "two items: road tier" (r2.[0].Tier = 4)

// 3. Roads have correct tier
let (_, r3) = hierarchicalTreemap [ for i in 1..5 -> sprintf "f%d" i, float32 (i * 10) ] (TRect.create 0.0f 0.0f 200.0f 200.0f) 0.3f 2
check "5 items: roads have tier 2" (r3 |> List.forall (fun r -> r.Tier = 2))

// 4. No placements overlap (with margins)
let (p4, _) = hierarchicalTreemap [ for i in 1..20 -> sprintf "f%d" i, float32 (5 + i * 3) ] (TRect.create 0.0f 0.0f 200.0f 200.0f) 0.5f 4
let rects4 = p4 |> List.map snd
let overlaps4 =
  [ for i in 0 .. rects4.Length - 2 do
      for j in i + 1 .. rects4.Length - 1 do
        let a = rects4.[i]
        let b = rects4.[j]
        let ox = max 0.0f (min (a.X + a.W) (b.X + b.W) - max a.X b.X)
        let oz = max 0.0f (min (a.Z + a.H) (b.Z + b.H) - max a.Z b.Z)
        if ox > 0.01f && oz > 0.01f then yield (i, j) ]
check "20 items: no overlaps" (overlaps4.IsEmpty)

// 5. All placements within bounds
let bounds5 = TRect.create 10.0f 20.0f 180.0f 160.0f
let (p5, _) = hierarchicalTreemap [ for i in 1..15 -> sprintf "f%d" i, float32 (10 + i * 5) ] bounds5 1.0f 4
let inBounds5 = p5 |> List.forall (fun (_, r) ->
  r.X >= bounds5.X - 0.01f && r.Z >= bounds5.Z - 0.01f &&
  r.X + r.W <= bounds5.X + bounds5.W + 0.1f &&
  r.Z + r.H <= bounds5.Z + bounds5.H + 0.1f)
check "15 items: within bounds" inBounds5

// 6. Roads don't cross through building interiors
// Each road should be at a subdivision boundary, not through a building
let (p6, roads6) = hierarchicalTreemap [ for i in 1..10 -> sprintf "f%d" i, float32 (10 + i * 5) ] (TRect.create 0.0f 0.0f 200.0f 200.0f) 1.0f 4
let roadsCrossBuildings =
  [ for road in roads6 do
      for (name, bld) in p6 do
        // Check if road line segment passes through building interior
        let isVert = abs (road.X1 - road.X2) < 0.001f
        if isVert then
          // Vertical road at X=road.X1; crosses building if X is strictly inside
          let inside = road.X1 > bld.X + 0.01f && road.X1 < bld.X + bld.W - 0.01f
          let zOverlap = road.Z1 < bld.Z + bld.H - 0.01f && road.Z2 > bld.Z + 0.01f
          if inside && zOverlap then yield (name, road)
        else
          // Horizontal road at Z=road.Z1; crosses building if Z is strictly inside
          let inside = road.Z1 > bld.Z + 0.01f && road.Z1 < bld.Z + bld.H - 0.01f
          let xOverlap = road.X1 < bld.X + bld.W - 0.01f && road.X2 > bld.X + 0.01f
          if inside && xOverlap then yield (name, road) ]
check "roads don't cross buildings" (roadsCrossBuildings.IsEmpty)

// 7. More items produce more roads
let (_, roads7a) = hierarchicalTreemap [ for i in 1..3 -> sprintf "f%d" i, float32 i ] (TRect.create 0.0f 0.0f 100.0f 100.0f) 0.3f 4
let (_, roads7b) = hierarchicalTreemap [ for i in 1..20 -> sprintf "f%d" i, float32 i ] (TRect.create 0.0f 0.0f 100.0f 100.0f) 0.3f 4
check "more items → more roads" (roads7b.Length >= roads7a.Length)

// 8. Road segments span the current subdivision rectangle
let (_, roads8) = hierarchicalTreemap [("A", 3.0f); ("B", 2.0f); ("C", 1.0f)] (TRect.create 0.0f 0.0f 100.0f 100.0f) 0.0f 4
for road in roads8 do
  let len =
    let dx = road.X2 - road.X1
    let dz = road.Z2 - road.Z1
    sqrt (dx * dx + dz * dz)
  check (sprintf "road length > 0 (%.1f)" len) (len > 0.1f)

// 9. Margin reduces building footprints
let (pNoMargin, _) = hierarchicalTreemap [("A", 1.0f); ("B", 1.0f)] (TRect.create 0.0f 0.0f 100.0f 100.0f) 0.0f 4
let (pWithMargin, _) = hierarchicalTreemap [("A", 1.0f); ("B", 1.0f)] (TRect.create 0.0f 0.0f 100.0f 100.0f) 5.0f 4
let areaNo = pNoMargin |> List.sumBy (fun (_, r) -> TRect.area r)
let areaWith = pWithMargin |> List.sumBy (fun (_, r) -> TRect.area r)
check "margin reduces total footprint" (areaWith < areaNo)

// 10. Large scale: 500 items
let (p10, roads10) = hierarchicalTreemap [ for i in 1..500 -> sprintf "f%d" i, float32 (1 + i % 30) ] (TRect.create 0.0f 0.0f 400.0f 400.0f) 0.3f 2
check "500 items: all placed" (p10.Length = 500)
check "500 items: has roads" (roads10.Length > 0)
let names10 = p10 |> List.map fst |> Set.ofList
check "500 items: all unique names" (names10.Count = 500)

printfn "\n=== Results: %d passed, %d failed ===" passed failed
if failed > 0 then exit 1
