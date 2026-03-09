module CodeCity

open System
open System.IO
open System.Numerics
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open Raylib_cs

#nowarn "3391"
#nowarn "9"

// ─── 2D Geometry (Tensor Field Layout) ──────────────────────────

[<Struct>]
type Vec2 =
  { X: float32; Y: float32 }

module Vec2 =
  let Zero = { X = 0.0f; Y = 0.0f }
  let Create (x: float32, y: float32) = { X = x; Y = y }
  let add a b = { X = a.X + b.X; Y = a.Y + b.Y }
  let sub a b = { X = a.X - b.X; Y = a.Y - b.Y }
  let scale s v = { X = v.X * s; Y = v.Y * s }
  let dot a b = a.X * b.X + a.Y * b.Y
  let lengthSq v = dot v v
  let length v = sqrt (lengthSq v)
  let normalize v =
    let len = length v
    if len < 1e-10f then Zero
    else { X = v.X / len; Y = v.Y / len }
  let distanceToSq a b = lengthSq (sub a b)
  let distanceTo a b = sqrt (distanceToSq a b)
  let negate v = { X = -v.X; Y = -v.Y }


// ─── Squarified Treemap (Bruls, Huizing, van Wijk 2000) ─────

type TRect =
  { X: float32; Z: float32; W: float32; H: float32 }

module TRect =
  let area r = r.W * r.H
  let create x z w h : TRect = { X = x; Z = z; W = w; H = h }
  let inset margin (r: TRect) =
    // Guard: margin can't exceed 25% of dimension to prevent spatial overflow
    let mw = min margin (r.W / 4.0f)
    let mh = min margin (r.H / 4.0f)
    { X = r.X + mw; Z = r.Z + mh
      W = max 0.1f (r.W - 2.0f * mw)
      H = max 0.1f (r.H - 2.0f * mh) }
  let centerX r = r.X + r.W / 2.0f
  let centerZ r = r.Z + r.H / 2.0f

/// Squarified treemap layout (Bruls, Huizing, van Wijk 2000).
/// Places weighted items into a bounding rectangle with near-square aspect ratios.
/// Items are placed from largest to smallest.
let squarifiedTreemap (items: ('a * float32) list) (bounds: TRect) : ('a * TRect) list =
  if items.IsEmpty || bounds.W <= 0.0f || bounds.H <= 0.0f then []
  else
    let sorted = items |> List.sortByDescending snd
    let totalWeight = sorted |> List.sumBy snd |> max 0.001f
    let totalArea = bounds.W * bounds.H
    let areas = sorted |> List.map (fun (id, w) -> (id, w / totalWeight * totalArea))
    let results = ResizeArray<'a * TRect>()
    let mutable rect = bounds
    let remaining = areas |> List.toArray
    let mutable idx = 0
    while idx < remaining.Length do
      let shorterSide = min rect.W rect.H
      let horizontal = rect.W < rect.H
      let mutable rowStart = idx
      let mutable rowArea = 0.0f
      let mutable bestWorst = System.Double.MaxValue
      let mutable keep = true
      while idx < remaining.Length && keep do
        let (_, area) = remaining.[idx]
        let newRowArea = rowArea + area
        let s = float newRowArea
        let w = float shorterSide
        let s2 = s * s
        let w2 = w * w
        let mutable maxAspect = 0.0
        for j in rowStart .. idx do
          let (_, a) = remaining.[j]
          let a' = float a
          let aspect = max (w2 * a' / s2) (s2 / (w2 * a'))
          maxAspect <- max maxAspect aspect
        if rowArea = 0.0f || maxAspect <= bestWorst then
          rowArea <- newRowArea
          bestWorst <- maxAspect
          idx <- idx + 1
        else
          keep <- false
      if rowArea > 0.0f then
        let stripThickness = rowArea / shorterSide
        let mutable offset = 0.0f
        for j in rowStart .. idx - 1 do
          let (id, area) = remaining.[j]
          let itemLength = area / stripThickness
          let itemRect =
            if horizontal then
              TRect.create (rect.X + offset) rect.Z itemLength stripThickness
            else
              TRect.create rect.X (rect.Z + offset) stripThickness itemLength
          results.Add(id, itemRect)
          offset <- offset + itemLength
        if horizontal then
          rect <- { rect with Z = rect.Z + stripThickness; H = rect.H - stripThickness }
        else
          rect <- { rect with X = rect.X + stripThickness; W = rect.W - stripThickness }
    results |> Seq.toList

// ─── Organic Jitter ──────────────────────────────────────────

let hashJitter (name: string) : float32 =
  let h = abs (name.GetHashCode())
  float32 (h % 1000) / 1000.0f

let rotationJitter (name: string) (maxDeg: float32) : float32 =
  (hashJitter name - 0.5f) * 2.0f * maxDeg

// ─── Deterministic Hash + RNG ─────────────────────────────────

/// FNV-1a hash — stable across .NET versions (unlike GetHashCode)
let fnvHash (s: string) =
  let mutable h = 0x811c9dc5u
  for c in s do
    h <- h ^^^ (uint32 c)
    h <- h * 0x01000193u
  h

// ─── Procedural Compound Building Generation ──────────────────

/// Direction a face points outward from a sub-cube
type FaceDir = FdNorth | FdSouth | FdEast | FdWest

/// A sub-cube in a compound building (coordinates relative to building center)
type SubCube =
  { CX: float32; CZ: float32
    HW: float32; HD: float32
    HeightScale: float32 }

/// An attachment face on the boundary of existing geometry
type AttachFace =
  { Owner: int
    FX: float32; FZ: float32
    Dir: FaceDir
    HalfLen: float32 }

let oppositeDir = function
  | FdNorth -> FdSouth | FdSouth -> FdNorth
  | FdEast -> FdWest   | FdWest -> FdEast

/// Generate compound building footprint from cyclomatic complexity.
/// Returns array of sub-cubes (relative to building center at 0,0).
/// Deterministic: same qualName always produces same shape.
/// Uses "campus planner" algorithm: iterative wing addition with face-attachment list.
let generateCompound (qualName: string) (complexity: int) (lotHW: float32) (lotHD: float32) : SubCube[] =
  let mutable rng = fnvHash qualName
  let inline next() = rng <- rng * 1664525u + 1013904223u; rng
  let inline float01() = float32 (next() &&& 0xFFFFu) / 65535.0f
  let inline range lo hi = lo + float01() * (hi - lo)

  if complexity <= 1 || lotHW < 0.2f || lotHD < 0.2f then
    [| { CX = 0f; CZ = 0f; HW = lotHW * 0.75f; HD = lotHD * 0.75f; HeightScale = 1.0f } |]
  else
    let minWing = 0.15f

    // Central mass: 28-48% of lot (smaller core → more room for wings)
    let coreHW = lotHW * range 0.28f 0.48f |> max minWing
    let coreHD = lotHD * range 0.28f 0.48f |> max minWing
    let core = { CX = 0f; CZ = 0f; HW = coreHW; HD = coreHD; HeightScale = 1.0f }

    let cubes = ResizeArray<SubCube>(min complexity 30)
    cubes.Add(core)

    let cubeBounds (cube: SubCube) =
      (cube.CX - cube.HW, cube.CX + cube.HW, cube.CZ - cube.HD, cube.CZ + cube.HD)

    let overlap1d a0 a1 b0 b1 =
      min a1 b1 - max a0 b0

    let overlapArea (a: SubCube) (b: SubCube) =
      let ax0, ax1, az0, az1 = cubeBounds a
      let bx0, bx1, bz0, bz1 = cubeBounds b
      let ox = overlap1d ax0 ax1 bx0 bx1
      let oz = overlap1d az0 az1 bz0 bz1
      if ox > 0.0001f && oz > 0.0001f then ox * oz else 0.0f

    let facesOf owner (c: SubCube) =
      [| { Owner = owner; FX = c.CX; FZ = c.CZ - c.HD; Dir = FdNorth; HalfLen = c.HW }
         { Owner = owner; FX = c.CX; FZ = c.CZ + c.HD; Dir = FdSouth; HalfLen = c.HW }
         { Owner = owner; FX = c.CX + c.HW; FZ = c.CZ; Dir = FdEast;  HalfLen = c.HD }
         { Owner = owner; FX = c.CX - c.HW; FZ = c.CZ; Dir = FdWest;  HalfLen = c.HD } |]

    let maxExtrusion (f: AttachFace) =
      match f.Dir with
      | FdNorth -> f.FZ + lotHD   | FdSouth -> lotHD - f.FZ
      | FdEast  -> lotHW - f.FX   | FdWest  -> f.FX + lotHW

    let faceBlocked (current: SubCube[]) (face: AttachFace) =
      let eps = 0.0001f
      let ownerCube = current.[face.Owner]
      let ox0, ox1, oz0, oz1 = cubeBounds ownerCube
      current
      |> Array.mapi (fun idx cube -> idx, cube)
      |> Array.exists (fun (idx, cube) ->
        if idx = face.Owner then false
        else
          let cx0, cx1, cz0, cz1 = cubeBounds cube
          match face.Dir with
          | FdNorth ->
            abs (cz1 - oz0) <= eps
            && overlap1d cx0 cx1 ox0 ox1 >= (ox1 - ox0) - eps
          | FdSouth ->
            abs (cz0 - oz1) <= eps
            && overlap1d cx0 cx1 ox0 ox1 >= (ox1 - ox0) - eps
          | FdEast ->
            abs (cx0 - ox1) <= eps
            && overlap1d cz0 cz1 oz0 oz1 >= (oz1 - oz0) - eps
          | FdWest ->
            abs (cx1 - ox0) <= eps
            && overlap1d cz0 cz1 oz0 oz1 >= (oz1 - oz0) - eps)

    let exposedFaces (current: SubCube[]) =
      current
      |> Array.mapi (fun idx cube -> facesOf idx cube)
      |> Array.collect id
      |> Array.filter (fun face ->
        maxExtrusion face >= minWing * 2.0f
        && not (faceBlocked current face))

    let envelope (current: SubCube[]) =
      current
      |> Array.fold (fun (minX, maxX, minZ, maxZ) cube ->
        let x0, x1, z0, z1 = cubeBounds cube
        (min minX x0, max maxX x1, min minZ z0, max maxZ z1))
        (Single.PositiveInfinity, Single.NegativeInfinity, Single.PositiveInfinity, Single.NegativeInfinity)

    // Let complexity scale shape growth directly.
    // Spatial constraints come from face exhaustion, not an arbitrary global cap.
    let wingBudget = max 0 (complexity - 1)
    for _ in 1 .. wingBudget do
      let current = cubes.ToArray()
      let faces = exposedFaces current

      if faces.Length > 0 then
        let minX, maxX, minZ, maxZ = envelope current
        let oldArea = (maxX - minX) * (maxZ - minZ)
        let sampleCount = min 8 faces.Length

        let candidates =
          [| for _ in 1 .. sampleCount do
               let face = faces.[int (next() % uint32 faces.Length)]
               let maxExt = maxExtrusion face

               let wingHalfLen = face.HalfLen * range 0.30f 0.70f |> max minWing
               let wingDepth = maxExt * range 0.20f 0.50f |> max (minWing * 2.0f)
               let wingDepthHalf = wingDepth / 2.0f

               let slack = max 0.0f (face.HalfLen - wingHalfLen)
               let offset = range -0.3f 0.3f * slack

               let (wcx, wcz, whw, whd) =
                 match face.Dir with
                 | FdNorth -> (face.FX + offset, face.FZ - wingDepthHalf, wingHalfLen, wingDepthHalf)
                 | FdSouth -> (face.FX + offset, face.FZ + wingDepthHalf, wingHalfLen, wingDepthHalf)
                 | FdEast  -> (face.FX + wingDepthHalf, face.FZ + offset, wingDepthHalf, wingHalfLen)
                 | FdWest  -> (face.FX - wingDepthHalf, face.FZ + offset, wingDepthHalf, wingHalfLen)

               let wcx' = wcx |> max (-lotHW + whw) |> min (lotHW - whw)
               let wcz' = wcz |> max (-lotHD + whd) |> min (lotHD - whd)
               let wing =
                 { CX = wcx'
                   CZ = wcz'
                   HW = whw
                   HD = whd
                   HeightScale = range 0.55f 1.0f }

               let area = 4.0f * wing.HW * wing.HD
               let overlap = current |> Array.sumBy (overlapArea wing)
               let overlapRatio =
                 if area <= 0.0001f then 1.0f
                 else overlap / area

               let wx0, wx1, wz0, wz1 = cubeBounds wing
               let newMinX = min minX wx0
               let newMaxX = max maxX wx1
               let newMinZ = min minZ wz0
               let newMaxZ = max maxZ wz1
               let newArea = (newMaxX - newMinX) * (newMaxZ - newMinZ)
               let bboxExpansion = max 0.0f (newArea - oldArea)
               let frontierSides =
                 (if wx0 < minX - 0.001f then 1 else 0)
                 + (if wx1 > maxX + 0.001f then 1 else 0)
                 + (if wz0 < minZ - 0.001f then 1 else 0)
                 + (if wz1 > maxZ + 0.001f then 1 else 0)
               let fullyInside =
                 wx0 >= minX && wx1 <= maxX
                 && wz0 >= minZ && wz1 <= maxZ
               let novelty = max 0.0f (area - overlap)
               let score =
                 novelty * 1.5f
                 + bboxExpansion * 1.2f
                 + float32 frontierSides * area * 0.35f
                 - overlap * 4.0f
                 - (if fullyInside then area * 1.5f else 0.0f)

               if overlapRatio <= 0.15f then Some (score, wing) else None |]
          |> Array.choose id

        if candidates.Length > 0 then
          let _, bestWing = candidates |> Array.maxBy fst
          cubes.Add(bestWing)

    cubes.ToArray()

// ─── Data Model ───────────────────────────────────────────────

type FuncDef =
  { Name: string
    QualifiedName: string
    FilePath: string
    RelPath: string
    Module: string
    Project: string
    DeclarationStartLine: int
    DeclarationStartColumn: int
    StartLine: int
    EndLine: int
    LineCount: int
    Body: string[]
    CallRefs: string list
    CallSites: CallSite list }

and CallSite =
  { RefText: string
    NamePath: string list
    StartLine: int
    StartColumn: int
    EndColumn: int }

type CallEdge =
  { From: string  // QualifiedName
    To: string
    Weight: int }

// ─── Building Typology ───────────────────────────────────────
// Six discrete types that shape visual appearance and lot spacing.
// Classification is heat-first (callers dominate perceived importance),
// then line count.  Drives color palette, height scale, and yard width.
type BuildingType = Shed | Cottage | Rowhouse | Commercial | Tower | Skyscraper

type FuncBuilding =
  { Func: FuncDef
    Heat: float32         // 0..1 normalized (callers)
    CallerCount: int
    CalleeCount: int
    Complexity: int       // McCabe cyclomatic complexity
    BuildingType: BuildingType
    GitAgeDays: float32
    X: float32; Z: float32
    W: float32; D: float32
    H: float32
    Rotation: float32     // degrees, for organic jitter
    Color: Color
    RoofColor: Color
    District: string }

type Road =
  { FromFunc: string
    ToFunc: string
    FromPos: Vector3
    ToPos: Vector3
    Weight: int
    Color: Color
    Organic: float32 }

type District =
  { Name: string
    FuncCount: int
    TotalLines: int
    Color: Color }

type RelatedBuilding =
  { Building: FuncBuilding
    Weight: int }

type Rect2D =
  { X: float32; Z: float32; W: float32; D: float32 }

type ModuleBlock =
  { Module: string; Project: string; Rect: TRect; Color: Color }

// ─── Connected Road Graph Types ──────────────────────────────
// Roads are a proper graph: nodes (intersections) + edges (road segments).
// Every subdivision creates 2 nodes + 1 road edge + splits 2 boundary edges.
// Blocks are leaf rectangles bounded by graph edges.
// Every building is guaranteed street access because blocks ARE road-bounded.

type RoadClass = Boulevard | Avenue | Street | Lane | Alley

type StreetStatus = Planned | Built

type StreetId = StreetId of int

module StreetId =
  let value (StreetId value) = value

type StreetTraffic =
  { Volume: float32
    MaxVolume: float32 }

type ResidentTrip =
  { StartStreet: StreetId
    EndStreet: StreetId
    Volume: float32
    Route: StreetId list }

type PathError =
  | UnknownStreet of StreetId
  | NoRouteBetweenStreets of StreetId * StreetId

type TripError =
  | MissingStreet of StreetId
  | NegativeTrafficAfterRemoval of StreetId

type MajorStreet =
  { Id: StreetId
    Segment: TRect
    Status: StreetStatus
    Residents: float32
    Traffic: StreetTraffic }

type MinorStreet =
  { Id: StreetId
    Segment: TRect
    Status: StreetStatus
    QuarterRect: TRect
    Residents: float32
    Traffic: StreetTraffic }

type FrontageEdge = North | South | East | West

type LandUseType =
  | ResidentialUse
  | CommercialUse
  | IndustrialUse
  | ParkUse
  | CivicUse
  | MixedUseZone

type BuildingEnvelope =
  { Rect: TRect
    Area: float32
    FrontageEdge: FrontageEdge
    FrontSetback: float32
    SideSetback: float32
    BackSetback: float32
    NFloors: float32 }

type LotEconomics =
  { Price: float32
    FloorSpace: float32
    Residents: float32 }

type ValuationCurve =
  | Step
  | LinearUp
  | LinearDown
  | GainUp
  | GainDown

type LotMetric =
  | LotArea
  | FrontageAccess
  | TrafficVolume
  | SameUseCluster
  | NeighborInfluence of LandUseType

type LandUseValuation =
  { Metric: LotMetric
    Curve: ValuationCurve
    Min: float32
    Max: float32
    Weight: float32 }

type LandUseDefinition =
  { LandUseType: LandUseType
    Valuations: LandUseValuation list }

type LandUseGoal =
  { LandUseType: LandUseType
    TargetPercent: float32 }

type BuildingSubstitutionFactor =
  { Curve: ValuationCurve
    Min: float32
    Max: float32
    Weight: float32 }

type BuildingSubstitutionConfig =
  { AgeFactor: BuildingSubstitutionFactor
    PriceGapFactor: BuildingSubstitutionFactor
    Seed: int }

type BuildingSubstitutionStats =
  { EvaluatedLots: int
    ReplacedLots: int
    RetainedLots: int
    MeanProbability: float32 }

type LandUseCadence =
  { StepYears: float32
    ReevaluationYears: float32
    ElapsedSinceLastEvaluationYears: float32 }

type LandUseSimulationConfig =
  { Goals: LandUseGoal list
    Definitions: LandUseDefinition list
    AveragePricePerSqm: float32
    GlobalWeight: float32
    LocalWeight: float32
    GoalScale: float32
    AttemptsFraction: float32
    RejectedDeltaThreshold: float32
    Seed: int
    Cadence: LandUseCadence }

type LandUseUpdateStats =
  { EvaluatedLots: int
    Attempts: int
    Accepted: int
    Rejected: int
    SkippedByCadence: bool
    GlobalValueBefore: float32
    GlobalValueAfter: float32 }

type LandUseSubdivisionRule =
  { MaxLotArea: float32
    MinWidthLengthRatio: float32
    RetryOffsets: float32 list }

type BlockSubdivisionStats =
  { EvaluatedRects: int
    AcceptedSplits: int
    RejectedCandidates: int
    StreetEdgeRetries: int
    NonStreetEdgeFallbacks: int
    TerminalLots: int
    MaxDepth: int }

type PlannedBlock =
  { Rect: TRect
    QuarterRect: TRect
    LandUseType: LandUseType
    LandUseValue: float32
    StreetFacingEdges: FrontageEdge list
    Lots: PlannedLot list }
and PlannedLot =
  { Rect: TRect
    FrontageEdge: FrontageEdge
    BlockRect: TRect
    LandUseType: LandUseType
    LandUseValue: float32
    Envelope: BuildingEnvelope option
    FrontingStreetStatus: StreetStatus
    Price: float32
    FloorSpace: float32
    Residents: float32
    BuildingAgeYears: float32 }

type QuarterPlan =
  { Rect: TRect
    MinorStreets: MinorStreet list
    Blocks: PlannedBlock list
    LandUseType: LandUseType
    LandUseValue: float32 }

type DistrictPlan =
  { MajorStreets: MajorStreet list
    Quarters: QuarterPlan list }

type MajorStreetGrowthPlan =
  { GrowthCenters: Vec2 list
    MajorStreets: MajorStreet list
    QuarterRects: TRect list }

type SimulationStage =
  | UpdateTrafficSimulationStage
  | PromotePlannedStreetsStage
  | UpdateLandUseSimulationStage

type StageSnapshot =
  { Stage: SimulationStage
    Plan: DistrictPlan }

type SimulationTrace =
  { Seed: int option
    Initial: DistrictPlan
    Stages: StageSnapshot list
    Final: DistrictPlan }

type SimulationTimelineConfig =
  { Steps: int
    StepYears: float32
    LandUseReevaluationYears: float32
    PromotionLagSteps: int
    BuildingSubstitution: BuildingSubstitutionConfig option }

type CanonicalMajorStreet =
  { StreetId: StreetId
    StreetRect: TRect
    StreetStatus: StreetStatus
    StreetResidents: float32
    StreetTrafficVolume: float32
    StreetMaxVolume: float32 }

type CanonicalMinorStreet =
  { StreetId: StreetId
    StreetRect: TRect
    StreetStatus: StreetStatus
    StreetResidents: float32
    StreetTrafficVolume: float32
    StreetMaxVolume: float32 }

type CanonicalLot =
  { LotRect: TRect
    LotFrontageEdge: FrontageEdge
    LotLandUseType: LandUseType
    LotLandUseValue: float32
    LotFrontingStreetStatus: StreetStatus
    LotResidents: float32 }

type CanonicalBlock =
  { BlockRect: TRect
    BlockLandUseType: LandUseType
    BlockLandUseValue: float32
    CanonicalLots: CanonicalLot list }

type CanonicalQuarter =
  { QuarterRect: TRect
    QuarterLandUseType: LandUseType
    QuarterLandUseValue: float32
    CanonicalMinorStreets: CanonicalMinorStreet list
    CanonicalBlocks: CanonicalBlock list }

type CanonicalDistrictPlan =
  { CanonicalMajorStreets: CanonicalMajorStreet list
    CanonicalQuarters: CanonicalQuarter list }

type CanonicalStageSnapshot =
  { CanonicalStage: SimulationStage
    CanonicalPlan: CanonicalDistrictPlan }

type CanonicalSimulationTrace =
  { TraceSeed: int option
    InitialPlan: CanonicalDistrictPlan
    StagePlans: CanonicalStageSnapshot list
    FinalPlan: CanonicalDistrictPlan }

type SimulationStageDelta =
  { TrafficUpdatedStreetCount: int
    TotalTrafficVolumeDelta: float32
    PromotedMajorStreetCount: int
    PromotedMinorStreetCount: int
    QuarterLandUseChangedCount: int
    BlockLandUseChangedCount: int
    TotalBlockLandUseValueDelta: float32
    LotLandUseChangedCount: int
    TotalLotLandUseValueDelta: float32
    ReplacedLotCount: int }

type TimelineStepSnapshot =
  { StepIndex: int
    ElapsedYears: float32
    Plan: DistrictPlan
    LandUseStats: LandUseUpdateStats
    BuildingSubstitutionStats: BuildingSubstitutionStats
    Delta: SimulationStageDelta }

type SimulationTimeline =
  { Seed: int option
    Initial: DistrictPlan
    Steps: TimelineStepSnapshot list
    Final: DistrictPlan }

type InstrumentedStageSnapshot =
  { Stage: SimulationStage
    Plan: DistrictPlan
    Elapsed: TimeSpan
    Delta: SimulationStageDelta }

type InstrumentedSimulationTrace =
  { Seed: int option
    Initial: DistrictPlan
    Stages: InstrumentedStageSnapshot list
    Final: DistrictPlan
    TotalElapsed: TimeSpan }

type SimulationStageBenchmark =
  { Stage: SimulationStage
    Iterations: int
    MinElapsed: TimeSpan
    MedianElapsed: TimeSpan
    MaxElapsed: TimeSpan
    LastObservedDelta: SimulationStageDelta }

type SimulationBenchmarkSummary =
  { WarmupIterations: int
    MeasuredIterations: int
    StageBenchmarks: SimulationStageBenchmark list
    TotalMinElapsed: TimeSpan
    TotalMedianElapsed: TimeSpan
    TotalMaxElapsed: TimeSpan
    CanonicalResult: CanonicalSimulationTrace }

type BenchmarkFixtureSize =
  | Tiny
  | Medium
  | Target

type SimulationBenchmarkBudget =
  { ReferenceLabel: string
    TargetMedianElapsed: TimeSpan }

type SimulationBenchmarkBudgetStatus =
  | NotEvaluated
  | WithinReference
  | OverReference of TimeSpan

type SimulationBenchmarkCounters =
  { PathComputations: int
    TripsGenerated: int
    SplitCandidatesTried: int
    ResultStreetCount: int
    ResultBlockCount: int
    ResultLotCount: int }

type SimulationBenchmarkCase =
  { Name: string
    Size: BenchmarkFixtureSize
    Seed: int
    Initial: DistrictPlan
    Budget: SimulationBenchmarkBudget option }

type SimulationBenchmarkCaseSummary =
  { Case: SimulationBenchmarkCase
    Summary: SimulationBenchmarkSummary
    Counters: SimulationBenchmarkCounters
    BudgetStatus: SimulationBenchmarkBudgetStatus }

module MajorStreet =
  let private emptyTraffic = { Volume = 0.0f; MaxVolume = 0.0f }
  let planned segment =
    { Id = StreetId 0
      Segment = segment
      Status = Planned
      Residents = 0.0f
      Traffic = emptyTraffic }
  let built segment =
    { Id = StreetId 0
      Segment = segment
      Status = Built
      Residents = 0.0f
      Traffic = emptyTraffic }

module MinorStreet =
  let planned quarterRect segment =
    { Id = StreetId 0
      Segment = segment
      Status = Planned
      QuarterRect = quarterRect
      Residents = 0.0f
      Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
  let built quarterRect segment =
    { Id = StreetId 0
      Segment = segment
      Status = Built
      QuarterRect = quarterRect
      Residents = 0.0f
      Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }

module PlannedBlock =
  let create quarterRect rect =
    { Rect = rect
      QuarterRect = quarterRect
      LandUseType = ResidentialUse
      LandUseValue = 0.5f
      StreetFacingEdges = [ North; South; West; East ]
      Lots = [] }

module PlannedLot =
  let create blockRect frontageEdge rect =
    { Rect = rect
      FrontageEdge = frontageEdge
      BlockRect = blockRect
      LandUseType = ResidentialUse
      LandUseValue = 0.5f
      Envelope = None
      FrontingStreetStatus = Planned
      Price = 0.0f
      FloorSpace = 0.0f
      Residents = 0.0f
      BuildingAgeYears = 0.0f }

module QuarterPlan =
  let create rect minorStreets blocks =
    { Rect = rect
      MinorStreets = minorStreets
      Blocks = blocks
      LandUseType = ResidentialUse
      LandUseValue = 0.5f }

module RoadClass =
  // Real-world proportional widths (avg building ~1.5 units)
  // Alley ~3m, street ~7m, avenue ~12m, boulevard ~22m relative to ~15m buildings
  let width = function
    | Boulevard -> 2.4f | Avenue -> 1.6f | Street -> 1.0f
    | Lane -> 0.7f | Alley -> 0.5f
  let fromDepth = function
    | 0 -> Avenue | 1 -> Avenue | 2 -> Street | 3 -> Lane | _ -> Alley
  let tier = function
    | Boulevard -> 8 | Avenue -> 6 | Street -> 4 | Lane -> 3 | Alley -> 2
  /// Road surface color, brightness decreasing from Boulevard (warm stone) → Alley (near-black).
  /// Encodes the Weber hierarchy visually so growth topology is legible.
  let color = function
    | Boulevard -> (180uy, 170uy, 150uy)  // warm stone
    | Avenue    -> ( 90uy,  90uy,  95uy)  // neutral grey
    | Street    -> ( 65uy,  65uy,  70uy)  // darker grey
    | Lane      -> ( 50uy,  50uy,  55uy)  // dim
    | Alley     -> ( 38uy,  38uy,  42uy)  // near-black

let streetWidthFromTraffic (trafficVolume: float32) =
  1.0f + MathF.Sqrt(max 0.0f trafficVolume) * 0.3f

let streetMaxVolumeFromTraffic (trafficVolume: float32) =
  streetWidthFromTraffic trafficVolume * 6.0f

let private normalizeStreetTraffic (traffic: StreetTraffic) =
  let clampedVolume = max 0.0f traffic.Volume
  let maxVolume =
    match traffic.MaxVolume > 0.0f with
    | true -> traffic.MaxVolume
    | false -> streetMaxVolumeFromTraffic clampedVolume
  { Volume = clampedVolume
    MaxVolume = maxVolume }

let indexStreetNetwork (plan: DistrictPlan) : DistrictPlan =
  let existingIds =
    (plan.MajorStreets |> List.map _.Id)
    @ (plan.Quarters |> List.collect (fun quarter -> quarter.MinorStreets |> List.map _.Id))
    |> List.filter (fun streetId -> StreetId.value streetId > 0)
    |> Set.ofList

  let nextFreshId =
    let mutable nextId =
      existingIds
      |> Seq.map StreetId.value
      |> Seq.append [ 0 ]
      |> Seq.max
      |> fun currentMax -> currentMax + 1
    fun () ->
      let fresh = StreetId nextId
      nextId <- nextId + 1
      fresh

  let assignId used streetId =
    match StreetId.value streetId > 0 && not (Set.contains streetId used) with
    | true -> streetId, Set.add streetId used
    | false ->
        let fresh = nextFreshId()
        fresh, Set.add fresh used

  let usedAfterMajors, majorStreets =
    ((Set.empty, []), plan.MajorStreets)
    ||> List.fold (fun (used, streets) street ->
      let streetId, nextUsed = assignId used street.Id
      let indexed =
        { street with
            Id = streetId
            Traffic = normalizeStreetTraffic street.Traffic }
      nextUsed, streets @ [ indexed ])

  let _, quarters =
    ((usedAfterMajors, []), plan.Quarters)
    ||> List.fold (fun (used, quarters) quarter ->
      let nextUsed, minorStreets =
        ((used, []), quarter.MinorStreets)
        ||> List.fold (fun (innerUsed, streets) street ->
          let streetId, updatedUsed = assignId innerUsed street.Id
          let indexed =
            { street with
                Id = streetId
                Traffic = normalizeStreetTraffic street.Traffic }
          updatedUsed, streets @ [ indexed ])
      nextUsed, quarters @ [ { quarter with MinorStreets = minorStreets } ])

  { plan with
      MajorStreets = majorStreets
      Quarters = quarters }

/// Alias for test visibility (DomainTests opens the module-level namespace).
let roadColorForClass = RoadClass.color

let private corridorSpans (startPos: float32) (totalSize: float32) (corridors: TRect list) (isVertical: bool) =
  let boundsEnd = startPos + totalSize
  let ordered =
    corridors
    |> List.map (fun corridor ->
      let cStart = if isVertical then corridor.X else corridor.Z
      let cSize = if isVertical then corridor.W else corridor.H
      (cStart, cStart + cSize))
    |> List.sortBy fst
  let spans = ResizeArray<float32 * float32>()
  let mutable cursor = startPos
  for (cStart, cEnd) in ordered do
    let gapEnd = min boundsEnd cStart
    let gapSize = gapEnd - cursor
    if gapSize > 0.25f then
      spans.Add(cursor, gapSize)
    cursor <- max cursor (min boundsEnd cEnd)
  let tail = boundsEnd - cursor
  if tail > 0.25f then
    spans.Add(cursor, tail)
  spans |> Seq.toList

let private splitRectByCorridors (rect: TRect) (verticals: TRect list) (horizontals: TRect list) =
  let xSpans = corridorSpans rect.X rect.W verticals true
  let zSpans = corridorSpans rect.Z rect.H horizontals false
  [ for (x, w) in xSpans do
      for (z, h) in zSpans do
        if w > 0.25f && h > 0.25f then
          yield TRect.create x z w h ]

let private rectsOverlap (a: TRect) (b: TRect) =
  a.X < b.X + b.W
  && b.X < a.X + a.W
  && a.Z < b.Z + b.H
  && b.Z < a.Z + a.H

let private jitteredSplitPositions (startPos: float32) (size: float32) (segments: int) (organic: float32) (rng: Random) =
  if segments <= 1 then []
  else
    let nominal = size / float32 segments
    let jitterLimit = nominal * (0.08f + organic * 0.12f)
    [ for i in 1 .. segments - 1 ->
        let basePos = startPos + nominal * float32 i
        let jitter = (rng.NextSingle() - 0.5f) * 2.0f * jitterLimit
        let minPos = startPos + nominal * float32 i - nominal * 0.3f
        let maxPos = startPos + nominal * float32 i + nominal * 0.3f
        basePos + jitter |> max minPos |> min maxPos ]

let private allocateByWeights total (weights: float32 list) =
  if total <= 0 || weights.IsEmpty then []
  else
    let safeWeights = weights |> List.map (max 0.001f)
    let sumWeights = safeWeights |> List.sum |> max 0.001f
    let raw =
      safeWeights
      |> List.map (fun weight ->
        let exact = float32 total * weight / sumWeights
        let baseCount = int (MathF.Floor exact)
        (baseCount, exact - float32 baseCount))
    let baseTotal = raw |> List.sumBy fst
    let remaining = total - baseTotal
    let order =
      raw
      |> List.mapi (fun idx (_, frac) -> idx, frac)
      |> List.sortByDescending snd
      |> List.map fst
    raw
    |> List.mapi (fun idx (baseCount, _) ->
      let extra =
        order
        |> List.truncate remaining
        |> List.contains idx
      baseCount + if extra then 1 else 0)

let private blockScore (outerRect: TRect) (blockRect: TRect) =
  let cx = TRect.centerX blockRect
  let cz = TRect.centerZ blockRect
  let ox = TRect.centerX outerRect
  let oz = TRect.centerZ outerRect
  let dx = cx - ox
  let dz = cz - oz
  let dist = sqrt (dx * dx + dz * dz)
  TRect.area blockRect - dist * 0.35f

let private districtBlocks (plan: DistrictPlan) =
  plan.Quarters |> List.collect (fun quarter -> quarter.Blocks)

let private districtMinorStreets (plan: DistrictPlan) =
  plan.Quarters |> List.collect (fun quarter -> quarter.MinorStreets)

let private clamp01f value =
  value |> max 0.0f |> min 1.0f

let private rectCompactness (rect: TRect) =
  let longest = max rect.W rect.H
  match longest <= 0.0f with
  | true -> 0.0f
  | false -> min rect.W rect.H / longest

let private landUseSuitability (landUseType: LandUseType) (rect: TRect) (organic: float32) =
  let compactness = rectCompactness rect
  let areaScore = clamp01f (TRect.area rect / 600.0f)
  match landUseType with
  | ResidentialUse -> clamp01f (0.35f + compactness * 0.35f + organic * 0.30f)
  | CommercialUse -> clamp01f (0.30f + compactness * 0.40f + (1.0f - organic) * 0.20f + areaScore * 0.10f)
  | IndustrialUse -> clamp01f (0.30f + areaScore * 0.45f + (1.0f - compactness) * 0.25f)
  | ParkUse -> clamp01f (0.20f + organic * 0.45f + areaScore * 0.35f)
  | CivicUse -> clamp01f (0.25f + compactness * 0.30f + areaScore * 0.20f + organic * 0.25f)
  | MixedUseZone -> clamp01f (0.35f + compactness * 0.30f + areaScore * 0.15f + organic * 0.20f)

let private dominantLandUse (rect: TRect) (organic: float32) =
  [ ResidentialUse; CommercialUse; IndustrialUse; ParkUse; CivicUse; MixedUseZone ]
  |> List.map (fun landUseType -> landUseType, landUseSuitability landUseType rect organic)
  |> List.maxBy snd

let evaluateLotLandUseValue (lot: PlannedLot) =
  let frontageBonus =
    match lot.FrontingStreetStatus with
    | Planned -> 0.05f
    | Built -> 0.18f
  landUseSuitability lot.LandUseType lot.Rect lot.LandUseValue
  + frontageBonus
  |> clamp01f

let private economyMarginForLandUse landUseType =
  match landUseType with
  | ResidentialUse -> 0.70f
  | CommercialUse -> 0.82f
  | IndustrialUse -> 0.68f
  | ParkUse -> 0.12f
  | CivicUse -> 0.50f
  | MixedUseZone -> 0.76f

let private residentDensityForLandUse landUseType =
  match landUseType with
  | ResidentialUse -> 0.085f
  | CommercialUse -> 0.020f
  | IndustrialUse -> 0.008f
  | ParkUse -> 0.002f
  | CivicUse -> 0.010f
  | MixedUseZone -> 0.040f

let private zeroLotEconomics =
  { Price = 0.0f
    FloorSpace = 0.0f
    Residents = 0.0f }

let private frontSetbackForLandUse landUseType =
  match landUseType with
  | ResidentialUse -> 0.45f
  | CommercialUse
  | MixedUseZone -> 0.25f
  | IndustrialUse -> 0.35f
  | ParkUse
  | CivicUse -> 0.60f

let private sideSetbackForLandUse landUseType =
  match landUseType with
  | ResidentialUse
  | CivicUse -> 0.20f
  | CommercialUse
  | MixedUseZone -> 0.12f
  | IndustrialUse -> 0.18f
  | ParkUse -> 0.30f

let private backSetbackForLandUse landUseType =
  match landUseType with
  | ResidentialUse
  | CivicUse -> 0.28f
  | CommercialUse
  | MixedUseZone -> 0.20f
  | IndustrialUse -> 0.24f
  | ParkUse -> 0.36f

let private buildEnvelopeForFrontage (frontageEdge: FrontageEdge) (lot: PlannedLot) =
  let frontSetback = frontSetbackForLandUse lot.LandUseType
  let sideSetback = sideSetbackForLandUse lot.LandUseType
  let backSetback = backSetbackForLandUse lot.LandUseType
  let envelopeRect =
    match frontageEdge with
    | North ->
        TRect.create
          (lot.Rect.X + sideSetback)
          (lot.Rect.Z + frontSetback)
          (lot.Rect.W - sideSetback * 2.0f)
          (lot.Rect.H - frontSetback - backSetback)
    | South ->
        TRect.create
          (lot.Rect.X + sideSetback)
          (lot.Rect.Z + backSetback)
          (lot.Rect.W - sideSetback * 2.0f)
          (lot.Rect.H - frontSetback - backSetback)
    | West ->
        TRect.create
          (lot.Rect.X + frontSetback)
          (lot.Rect.Z + sideSetback)
          (lot.Rect.W - frontSetback - backSetback)
          (lot.Rect.H - sideSetback * 2.0f)
    | East ->
        TRect.create
          (lot.Rect.X + backSetback)
          (lot.Rect.Z + sideSetback)
          (lot.Rect.W - frontSetback - backSetback)
          (lot.Rect.H - sideSetback * 2.0f)
  match envelopeRect.W <= 0.0f || envelopeRect.H <= 0.0f with
  | true -> None
  | false ->
      let area = TRect.area envelopeRect
      let nFloors =
        match area <= 0.0f with
        | true -> 0.0f
        | false -> lot.FloorSpace / area
      Some
        { Rect = envelopeRect
          Area = area
          FrontageEdge = frontageEdge
          FrontSetback = frontSetback
          SideSetback = sideSetback
          BackSetback = backSetback
          NFloors = nFloors }

let computeBuildingEnvelope (lot: PlannedLot) =
  match lot.FrontingStreetStatus with
  | Planned -> None
  | Built -> buildEnvelopeForFrontage lot.FrontageEdge lot

let computeLotEconomics avgPricePerSqm meanLotLandUseValue (lot: PlannedLot) =
  let area = TRect.area lot.Rect
  match area <= 0.0f || avgPricePerSqm <= 0.0f || meanLotLandUseValue <= 0.0f || lot.LandUseValue <= 0.0f with
  | true -> zeroLotEconomics
  | false ->
      let relativeValue = lot.LandUseValue / meanLotLandUseValue
      let price = area * avgPricePerSqm * relativeValue
      let floorSpace = price * economyMarginForLandUse lot.LandUseType
      let residents = floorSpace * residentDensityForLandUse lot.LandUseType
      { Price = price
        FloorSpace = floorSpace
        Residents = residents }

let private withLotDerivedState (lot: PlannedLot) =
  let landUseValue = evaluateLotLandUseValue lot
  let economics =
    computeLotEconomics 100.0f landUseValue { lot with LandUseValue = landUseValue }
  let enriched =
    { lot with
        LandUseValue = landUseValue
        Price = economics.Price
        FloorSpace = economics.FloorSpace
        Residents = economics.Residents }
  { enriched with Envelope = computeBuildingEnvelope enriched }

module Scenario =
  let rectangularBlock quarterRect rect landUseType landUseValue : PlannedBlock =
    PlannedBlock.create quarterRect rect
    |> fun block ->
      { block with
          LandUseType = landUseType
          LandUseValue = landUseValue }

  let rectangularLot blockRect rect frontageEdge streetStatus landUseType landUseValue : PlannedLot =
    PlannedLot.create blockRect frontageEdge rect
    |> fun lot ->
      { lot with
          LandUseType = landUseType
          LandUseValue = landUseValue
          FrontingStreetStatus = streetStatus }
    |> withLotDerivedState

  let singleQuarter rect : DistrictPlan =
    let block = rectangularBlock rect rect ResidentialUse 0.5f
    { MajorStreets = []
      Quarters = [ QuarterPlan.create rect [] [ block ] ] }
    |> indexStreetNetwork

  let singlePlannedMajorStreet segment : DistrictPlan =
    { MajorStreets = [ MajorStreet.planned segment ]
      Quarters = [] }
    |> indexStreetNetwork

  let singleBuiltMinorStreet quarterRect segment : DistrictPlan =
    let block = rectangularBlock quarterRect quarterRect ResidentialUse 0.5f
    let quarter =
      QuarterPlan.create quarterRect [ MinorStreet.built quarterRect segment ] [ block ]
    { MajorStreets = []
      Quarters = [ quarter ] }
    |> indexStreetNetwork

let planHierarchicalDistrict (rect: TRect) (moduleDemand: int) (organic: float32) (rng: Random) : DistrictPlan =
  if moduleDemand <= 0 || rect.W < 2.0f || rect.H < 2.0f then
    { MajorStreets = []
      Quarters = [] }
  else
    let majorWidth = max 1.2f (RoadClass.width Avenue * (0.95f + organic * 0.2f))
    let minorWidth = max 0.7f (RoadClass.width Street * (0.9f + organic * 0.15f))
    let crossAxis =
      moduleDemand >= 4
      && rect.W >= majorWidth * 2.0f + 8.0f
      && rect.H >= majorWidth * 2.0f + 8.0f
    let verticalMajor =
      if crossAxis then
        let mid = rect.X + rect.W / 2.0f + (rng.NextSingle() - 0.5f) * organic * (rect.W * 0.08f)
        [ MajorStreet.planned (TRect.create (mid - majorWidth / 2.0f) rect.Z majorWidth rect.H) ]
      else []
    let horizontalMajor =
      if crossAxis then
        let mid = rect.Z + rect.H / 2.0f + (rng.NextSingle() - 0.5f) * organic * (rect.H * 0.08f)
        [ MajorStreet.planned (TRect.create rect.X (mid - majorWidth / 2.0f) rect.W majorWidth) ]
      else []
    let majorRoads = verticalMajor @ horizontalMajor
    let quarterRects =
      splitRectByCorridors rect (verticalMajor |> List.map _.Segment) (horizontalMajor |> List.map _.Segment)
    let districts = if quarterRects.IsEmpty then [ rect ] else quarterRects
    let quarterDemand =
      districts
      |> List.map TRect.area
      |> allocateByWeights moduleDemand
    let planQuarter (quarterRect: TRect) (targetBlocks: int) : QuarterPlan option =
      if targetBlocks <= 0 then None
      elif targetBlocks = 1 || quarterRect.W < 4.0f || quarterRect.H < 4.0f then
        let landUseType, landUseValue = dominantLandUse quarterRect organic
        let block =
          PlannedBlock.create quarterRect quarterRect
          |> fun planned ->
            { planned with
                LandUseType = landUseType
                LandUseValue = landUseValue }
        Some
          (QuarterPlan.create quarterRect [] [ block ]
           |> fun quarter ->
             { quarter with
                 LandUseType = landUseType
                 LandUseValue = landUseValue })
      else
        let quarterLandUseType, quarterLandUseValue = dominantLandUse quarterRect organic
        let aspect = quarterRect.W / max 0.1f quarterRect.H
        let cols =
          max 1 (int (MathF.Ceiling(MathF.Sqrt(float32 targetBlocks * aspect))))
        let rows =
          max 1 (int (MathF.Ceiling(float32 targetBlocks / float32 cols)))
        let verticalCuts = jitteredSplitPositions quarterRect.X quarterRect.W cols organic rng
        let horizontalCuts = jitteredSplitPositions quarterRect.Z quarterRect.H rows organic rng
        let verticalRoads =
          verticalCuts
          |> List.map (fun cut -> MinorStreet.planned quarterRect (TRect.create (cut - minorWidth / 2.0f) quarterRect.Z minorWidth quarterRect.H))
        let horizontalRoads =
          horizontalCuts
          |> List.map (fun cut -> MinorStreet.planned quarterRect (TRect.create quarterRect.X (cut - minorWidth / 2.0f) quarterRect.W minorWidth))
        let blocks =
          splitRectByCorridors quarterRect (verticalRoads |> List.map _.Segment) (horizontalRoads |> List.map _.Segment)
          |> List.map (fun blockRect ->
            let blockLandUseType, blockLandUseValue =
              dominantLandUse blockRect ((quarterLandUseValue + organic) * 0.5f |> clamp01f)
            PlannedBlock.create quarterRect blockRect
            |> fun block ->
              { block with
                  LandUseType = blockLandUseType
                  LandUseValue = blockLandUseValue })
        Some
          (QuarterPlan.create quarterRect (verticalRoads @ horizontalRoads) blocks
           |> fun quarter ->
             { quarter with
                 LandUseType = quarterLandUseType
                 LandUseValue = quarterLandUseValue })
    let quarters =
      List.zip districts quarterDemand
      |> List.choose (fun (quarterRect, demand) -> planQuarter quarterRect demand)
    { MajorStreets = majorRoads
      Quarters =
        quarters
        |> List.sortByDescending (fun quarter -> quarter.Blocks |> List.sumBy (fun block -> blockScore rect block.Rect)) }
    |> indexStreetNetwork

let planHierarchicalDistrictFromSeed seed rect moduleDemand organic =
  planHierarchicalDistrict rect moduleDemand organic (Random seed)

let benchmarkDistrictForSize size seed =
  match size with
  | Tiny ->
      planHierarchicalDistrictFromSeed seed (TRect.create 0.0f 0.0f 32.0f 32.0f) 6 0.20f
  | Medium ->
      planHierarchicalDistrictFromSeed seed (TRect.create 0.0f 0.0f 64.0f 64.0f) 14 0.30f
  | Target ->
      planHierarchicalDistrictFromSeed seed (TRect.create 0.0f 0.0f 96.0f 96.0f) 24 0.35f

let benchmarkDistrict seed =
  benchmarkDistrictForSize Target seed

let private simulationStages =
  [ UpdateTrafficSimulationStage
    PromotePlannedStreetsStage
    UpdateLandUseSimulationStage ]

let subdivisionRuleForLandUse landUseType =
  match landUseType with
  | ResidentialUse ->
      { MaxLotArea = 120.0f
        MinWidthLengthRatio = 0.35f
        RetryOffsets = [ 0.5f; 0.4f; 0.6f; 0.33f; 0.67f ] }
  | CommercialUse ->
      { MaxLotArea = 180.0f
        MinWidthLengthRatio = 0.28f
        RetryOffsets = [ 0.5f; 0.45f; 0.55f; 0.35f; 0.65f ] }
  | IndustrialUse ->
      { MaxLotArea = 400.0f
        MinWidthLengthRatio = 0.18f
        RetryOffsets = [ 0.5f; 0.4f; 0.6f ] }
  | ParkUse ->
      { MaxLotArea = Single.PositiveInfinity
        MinWidthLengthRatio = 0.0f
        RetryOffsets = [ 0.5f ] }
  | CivicUse ->
      { MaxLotArea = 260.0f
        MinWidthLengthRatio = 0.24f
        RetryOffsets = [ 0.5f; 0.4f; 0.6f ] }
  | MixedUseZone ->
      { MaxLotArea = 150.0f
        MinWidthLengthRatio = 0.30f
        RetryOffsets = [ 0.5f; 0.45f; 0.55f; 0.35f; 0.65f ] }

let private frontageEdgePreference edge =
  match edge with
  | North -> 0
  | South -> 1
  | West -> 2
  | East -> 3

let private frontageEdgeLength (rect: TRect) edge =
  match edge with
  | North
  | South -> rect.W
  | West
  | East -> rect.H

let private normalizedStreetFacingEdges (block: PlannedBlock) =
  let edges =
    match block.StreetFacingEdges |> List.distinct with
    | [] -> [ North; South; West; East ]
    | xs -> xs
  edges
  |> List.sortBy (fun edge -> -frontageEdgeLength block.Rect edge, frontageEdgePreference edge)

let selectLongestStreetFacingEdge (block: PlannedBlock) =
  normalizedStreetFacingEdges block |> List.tryHead

let private orthogonalSplitRects edge offset (rect: TRect) =
  let clampedOffset = offset |> max 0.2f |> min 0.8f
  match edge with
  | North
  | South ->
      let splitWidth = rect.W * clampedOffset
      let remainderWidth = rect.W - splitWidth
      match splitWidth > 0.1f, remainderWidth > 0.1f with
      | true, true ->
          Some (
            TRect.create rect.X rect.Z splitWidth rect.H,
            TRect.create (rect.X + splitWidth) rect.Z remainderWidth rect.H)
      | _ -> None
  | West
  | East ->
      let splitHeight = rect.H * clampedOffset
      let remainderHeight = rect.H - splitHeight
      match splitHeight > 0.1f, remainderHeight > 0.1f with
      | true, true ->
          Some (
            TRect.create rect.X rect.Z rect.W splitHeight,
            TRect.create rect.X (rect.Z + splitHeight) rect.W remainderHeight)
      | _ -> None

let private rectWidthLengthRatio (rect: TRect) =
  let longest = max rect.W rect.H
  match longest <= 0.0f with
  | true -> 0.0f
  | false -> min rect.W rect.H / longest

let private makeLot (block: PlannedBlock) (frontageEdge: FrontageEdge) (rect: TRect) : PlannedLot =
  PlannedLot.create block.Rect frontageEdge rect
  |> fun lot ->
    { lot with
        LandUseType = block.LandUseType
        LandUseValue = block.LandUseValue
        FrontingStreetStatus = Planned }
  |> withLotDerivedState

let private zeroSubdivisionStats =
  { EvaluatedRects = 0
    AcceptedSplits = 0
    RejectedCandidates = 0
    StreetEdgeRetries = 0
    NonStreetEdgeFallbacks = 0
    TerminalLots = 0
    MaxDepth = 0 }

let subdivideBlockByLandUseWithStats (block: PlannedBlock) =
  let rule = subdivisionRuleForLandUse block.LandUseType
  let frontageEdge =
    selectLongestStreetFacingEdge block |> Option.defaultValue North
  let streetEdges = normalizedStreetFacingEdges block
  let fallbackEdges =
    [ North; South; West; East ]
    |> List.except streetEdges
    |> List.sortBy (fun edge -> -frontageEdgeLength block.Rect edge, frontageEdgePreference edge)

  let rec subdivide depth stats rect =
    let stats =
      { stats with
          EvaluatedRects = stats.EvaluatedRects + 1
          MaxDepth = max stats.MaxDepth depth }
    let canTerminate =
      TRect.area rect <= rule.MaxLotArea
      || rect.W <= 1.0f
      || rect.H <= 1.0f
      || depth >= 12
    match canTerminate with
    | true ->
        [ makeLot block frontageEdge rect ],
        { stats with TerminalLots = stats.TerminalLots + 1 }
    | false ->
        let rec tryCandidates isStreetEdge attemptStats edges =
          match edges with
          | [] -> None, attemptStats
          | edge :: remainingEdges ->
              let rec tryOffsets currentStats offsets =
                match offsets with
                | [] -> tryCandidates isStreetEdge currentStats remainingEdges
                | offset :: remainingOffsets ->
                    match orthogonalSplitRects edge offset rect with
                    | Some (a, b)
                      when rectWidthLengthRatio a >= rule.MinWidthLengthRatio
                           && rectWidthLengthRatio b >= rule.MinWidthLengthRatio ->
                        Some (a, b),
                        { currentStats with AcceptedSplits = currentStats.AcceptedSplits + 1 }
                    | _ ->
                        tryOffsets
                          { currentStats with
                              RejectedCandidates = currentStats.RejectedCandidates + 1
                              StreetEdgeRetries =
                                currentStats.StreetEdgeRetries + (if isStreetEdge then 1 else 0) }
                          remainingOffsets
              tryOffsets attemptStats rule.RetryOffsets

        let splitResult, streetStats = tryCandidates true stats streetEdges
        match splitResult with
        | Some (firstRect, secondRect) ->
            let firstLots, firstStats = subdivide (depth + 1) streetStats firstRect
            let secondLots, secondStats = subdivide (depth + 1) firstStats secondRect
            firstLots @ secondLots, secondStats
        | None ->
            let fallbackResult, fallbackStats = tryCandidates false streetStats fallbackEdges
            match fallbackResult with
            | Some (firstRect, secondRect) ->
                let fallbackStats =
                  { fallbackStats with
                      NonStreetEdgeFallbacks = fallbackStats.NonStreetEdgeFallbacks + 1 }
                let firstLots, firstStats = subdivide (depth + 1) fallbackStats firstRect
                let secondLots, secondStats = subdivide (depth + 1) firstStats secondRect
                firstLots @ secondLots, secondStats
            | None ->
                [ makeLot block frontageEdge rect ],
                { fallbackStats with TerminalLots = fallbackStats.TerminalLots + 1 }

  subdivide 0 zeroSubdivisionStats block.Rect

let subdivideBlockIntoLots (block: PlannedBlock) (lotCount: int) : PlannedLot list =
  let blockRect = block.Rect
  if lotCount <= 0 || blockRect.W < 0.5f || blockRect.H < 0.5f then []
  else
    let splitAlongX = blockRect.W >= blockRect.H
    let frontageEdge =
      selectLongestStreetFacingEdge block
      |> Option.defaultValue (if splitAlongX then North else West)
    [ for i in 0 .. lotCount - 1 do
        if splitAlongX then
          let x0 = blockRect.X + blockRect.W * float32 i / float32 lotCount
          let x1 = blockRect.X + blockRect.W * float32 (i + 1) / float32 lotCount
          yield makeLot block frontageEdge (TRect.create x0 blockRect.Z (x1 - x0) blockRect.H)
        else
          let z0 = blockRect.Z + blockRect.H * float32 i / float32 lotCount
          let z1 = blockRect.Z + blockRect.H * float32 (i + 1) / float32 lotCount
          yield makeLot block frontageEdge (TRect.create blockRect.X z0 blockRect.W (z1 - z0)) ]

let subdivideBlockByLandUse (block: PlannedBlock) =
  subdivideBlockByLandUseWithStats block |> fst

type private StreetView =
  { Id: StreetId
    Segment: TRect
    Status: StreetStatus
    Residents: float32
    Traffic: StreetTraffic }

let private streetLength (segment: TRect) =
  max segment.W segment.H

let private allStreetViews (plan: DistrictPlan) =
  [ yield! plan.MajorStreets |> List.map (fun street ->
      { Id = street.Id
        Segment = street.Segment
        Status = street.Status
        Residents = street.Residents
        Traffic = street.Traffic })
    yield! plan.Quarters |> List.collect (fun quarter ->
      quarter.MinorStreets
      |> List.map (fun street ->
        { Id = street.Id
          Segment = street.Segment
          Status = street.Status
          Residents = street.Residents
          Traffic = street.Traffic })) ]

let private tryFindStreetView streetId (plan: DistrictPlan) =
  allStreetViews plan |> List.tryFind (fun street -> street.Id = streetId)

let private rectsTouchOrOverlap (a: TRect) (b: TRect) =
  let eps = 0.01f
  not (
    a.X > b.X + b.W + eps
    || b.X > a.X + a.W + eps
    || a.Z > b.Z + b.H + eps
    || b.Z > a.Z + a.H + eps)

let private overlapLength a0 a1 b0 b1 =
  max 0.0f (min a1 b1 - max a0 b0)

let private streetTouchesLotFrontage (frontageEdge: FrontageEdge) (streetSegment: TRect) (lot: PlannedLot) =
  let eps = 0.05f
  match frontageEdge with
  | North ->
      overlapLength streetSegment.X (streetSegment.X + streetSegment.W) lot.Rect.X (lot.Rect.X + lot.Rect.W) > eps
      && streetSegment.Z <= lot.Rect.Z + eps
      && streetSegment.Z + streetSegment.H >= lot.Rect.Z - eps
  | South ->
      let south = lot.Rect.Z + lot.Rect.H
      overlapLength streetSegment.X (streetSegment.X + streetSegment.W) lot.Rect.X (lot.Rect.X + lot.Rect.W) > eps
      && streetSegment.Z <= south + eps
      && streetSegment.Z + streetSegment.H >= south - eps
  | West ->
      overlapLength streetSegment.Z (streetSegment.Z + streetSegment.H) lot.Rect.Z (lot.Rect.Z + lot.Rect.H) > eps
      && streetSegment.X <= lot.Rect.X + eps
      && streetSegment.X + streetSegment.W >= lot.Rect.X - eps
  | East ->
      let east = lot.Rect.X + lot.Rect.W
      overlapLength streetSegment.Z (streetSegment.Z + streetSegment.H) lot.Rect.Z (lot.Rect.Z + lot.Rect.H) > eps
      && streetSegment.X <= east + eps
      && streetSegment.X + streetSegment.W >= east - eps

let private builtStreetFrontagesInPlan (plan: DistrictPlan) (lot: PlannedLot) =
  allStreetViews plan
  |> List.filter (fun street -> street.Status = Built)
  |> List.collect (fun street ->
    [ North; South; West; East ]
    |> List.choose (fun frontageEdge ->
      match streetTouchesLotFrontage frontageEdge street.Segment lot with
      | true -> Some (frontageEdge, street.Traffic.Volume)
      | false -> None))

let private preferredBuiltFrontageInPlan (plan: DistrictPlan) (lot: PlannedLot) =
  builtStreetFrontagesInPlan plan lot
  |> List.sortBy (fun (frontageEdge, trafficVolume) -> -trafficVolume, frontageEdgePreference frontageEdge)
  |> List.tryHead
  |> Option.map fst

let computeBuildingEnvelopeInPlan (plan: DistrictPlan) (lot: PlannedLot) =
  preferredBuiltFrontageInPlan plan lot
  |> Option.bind (fun frontageEdge -> buildEnvelopeForFrontage frontageEdge lot)

let private mapMajorStreetById streetId updater (plan: DistrictPlan) =
  { plan with
      MajorStreets =
        plan.MajorStreets
        |> List.map (fun street ->
          match street.Id = streetId with
          | true -> updater street
          | false -> street) }

let private mapMinorStreetById streetId updater (plan: DistrictPlan) =
  { plan with
      Quarters =
        plan.Quarters
        |> List.map (fun quarter ->
          { quarter with
              MinorStreets =
                quarter.MinorStreets
                |> List.map (fun street ->
                  match street.Id = streetId with
                  | true -> updater street
                  | false -> street) }) }

let private planHasExplicitStreetResidents (plan: DistrictPlan) =
  allStreetViews plan |> List.exists (fun street -> street.Residents > 0.0f)

let private blockResidentEstimate (block: PlannedBlock) =
  let residentsPerArea =
    match block.LandUseType with
    | ResidentialUse -> 0.070f
    | MixedUseZone -> 0.045f
    | CivicUse -> 0.015f
    | CommercialUse -> 0.020f
    | IndustrialUse -> 0.012f
    | ParkUse -> 0.004f
  TRect.area block.Rect * residentsPerArea * (0.7f + block.LandUseValue * 0.8f)

let private withDerivedStreetResidents (plan: DistrictPlan) =
  let indexedPlan = indexStreetNetwork plan
  match planHasExplicitStreetResidents indexedPlan with
  | true -> indexedPlan
  | false ->
      let updateMajorResidents (street: MajorStreet) =
        let touchingBlocks =
          indexedPlan.Quarters
          |> List.collect _.Blocks
          |> List.filter (fun block -> rectsTouchOrOverlap block.Rect street.Segment)
        let residents =
          touchingBlocks
          |> List.sumBy blockResidentEstimate
          |> (*) 0.6f
        { street with Residents = residents }
      let updateQuarterResidents (quarter: QuarterPlan) =
        { quarter with
            MinorStreets =
              quarter.MinorStreets
              |> List.map (fun street ->
                let touchingBlocks =
                  quarter.Blocks
                  |> List.filter (fun block -> rectsTouchOrOverlap block.Rect street.Segment)
                let residents =
                  match touchingBlocks with
                  | [] ->
                      quarter.Blocks
                      |> List.sumBy blockResidentEstimate
                      |> fun total -> total / max 1.0f (float32 quarter.MinorStreets.Length)
                  | xs -> xs |> List.sumBy blockResidentEstimate
                { street with Residents = residents }) }
      { indexedPlan with
          MajorStreets = indexedPlan.MajorStreets |> List.map updateMajorResidents
          Quarters = indexedPlan.Quarters |> List.map updateQuarterResidents }

let private buildStreetAdjacency (plan: DistrictPlan) =
  let streets = allStreetViews plan
  streets
  |> List.map (fun street ->
    let neighbors =
      streets
      |> List.choose (fun candidate ->
        match candidate.Id = street.Id || not (rectsTouchOrOverlap street.Segment candidate.Segment) with
        | true -> None
        | false -> Some candidate.Id)
    street.Id, neighbors)
  |> Map.ofList

let shortestStreetPath (plan: DistrictPlan) (startStreet: StreetId) (endStreet: StreetId) : Result<StreetId list, PathError> =
  let indexedPlan = indexStreetNetwork plan
  let streets = allStreetViews indexedPlan
  let streetMap = streets |> List.map (fun street -> street.Id, street) |> Map.ofList
  match Map.containsKey startStreet streetMap, Map.containsKey endStreet streetMap with
  | false, _ -> Error (UnknownStreet startStreet)
  | _, false -> Error (UnknownStreet endStreet)
  | true, true when startStreet = endStreet -> Ok [ startStreet ]
  | true, true ->
      let adjacency = buildStreetAdjacency indexedPlan
      let infinity = Single.PositiveInfinity
      let rec loop (unvisited: Set<StreetId>) (distances: Map<StreetId, float32>) (previous: Map<StreetId, StreetId>) =
        match Set.isEmpty unvisited with
        | true -> distances, previous
        | false ->
            let current =
              unvisited
              |> Seq.minBy (fun streetId -> distances |> Map.tryFind streetId |> Option.defaultValue infinity)
            let currentDistance = distances |> Map.tryFind current |> Option.defaultValue infinity
            match Single.IsPositiveInfinity currentDistance || current = endStreet with
            | true -> distances, previous
            | false ->
                let nextDistances, nextPrevious =
                  adjacency
                  |> Map.tryFind current
                  |> Option.defaultValue []
                  |> List.fold (fun (stateDistances, statePrevious) neighbor ->
                    match Set.contains neighbor unvisited with
                    | false -> stateDistances, statePrevious
                    | true ->
                        let candidateDistance =
                          currentDistance
                          + (streetMap |> Map.find neighbor |> fun street -> streetLength street.Segment)
                        let knownDistance = stateDistances |> Map.tryFind neighbor |> Option.defaultValue infinity
                        match candidateDistance < knownDistance with
                        | true ->
                            stateDistances |> Map.add neighbor candidateDistance,
                            statePrevious |> Map.add neighbor current
                        | false -> stateDistances, statePrevious)
                    (distances, previous)
                loop (Set.remove current unvisited) nextDistances nextPrevious
      let initialDistances =
        streets
        |> List.fold (fun state street ->
          let distance =
            match street.Id = startStreet with
            | true -> 0.0f
            | false -> infinity
          state |> Map.add street.Id distance) Map.empty
      let distances, previous = loop (streets |> List.map _.Id |> Set.ofList) initialDistances Map.empty
      match distances |> Map.tryFind endStreet |> Option.defaultValue infinity |> Single.IsPositiveInfinity with
      | true -> Error (NoRouteBetweenStreets (startStreet, endStreet))
      | false ->
          let rec rebuild route current =
            match current = startStreet, previous |> Map.tryFind current with
            | true, _ -> current :: route
            | false, Some prior -> rebuild (current :: route) prior
            | false, None -> route
          Ok (rebuild [] endStreet)

let applyResidentTrip (trip: ResidentTrip) (plan: DistrictPlan) : Result<DistrictPlan, TripError> =
  let indexedPlan = indexStreetNetwork plan
  trip.Route
  |> List.fold (fun state streetId ->
    match state with
    | Error _ -> state
    | Ok current ->
        match tryFindStreetView streetId current with
        | None -> Error (MissingStreet streetId)
        | Some _ ->
            let updateTraffic (traffic: StreetTraffic) =
              let nextVolume = traffic.Volume + trip.Volume
              { Volume = nextVolume
                MaxVolume = streetMaxVolumeFromTraffic nextVolume }
            let nextPlan =
              current
              |> mapMajorStreetById streetId (fun street -> { street with Traffic = updateTraffic street.Traffic })
              |> mapMinorStreetById streetId (fun street -> { street with Traffic = updateTraffic street.Traffic })
            Ok nextPlan)
    (Ok indexedPlan)

let removeResidentTrip (trip: ResidentTrip) (plan: DistrictPlan) : Result<DistrictPlan, TripError> =
  let indexedPlan = indexStreetNetwork plan
  trip.Route
  |> List.fold (fun state streetId ->
    match state with
    | Error _ -> state
    | Ok current ->
        match tryFindStreetView streetId current with
        | None -> Error (MissingStreet streetId)
        | Some street when street.Traffic.Volume < trip.Volume -> Error (NegativeTrafficAfterRemoval streetId)
        | Some _ ->
            let updateTraffic (traffic: StreetTraffic) =
              let nextVolume = max 0.0f (traffic.Volume - trip.Volume)
              { Volume = nextVolume
                MaxVolume = streetMaxVolumeFromTraffic nextVolume }
            let nextPlan =
              current
              |> mapMajorStreetById streetId (fun street -> { street with Traffic = updateTraffic street.Traffic })
              |> mapMinorStreetById streetId (fun street -> { street with Traffic = updateTraffic street.Traffic })
            Ok nextPlan)
    (Ok indexedPlan)

let generateResidentTrips (plan: DistrictPlan) : ResidentTrip list =
  let residentPlan = withDerivedStreetResidents plan
  let streets =
    allStreetViews residentPlan
    |> List.filter (fun street -> street.Residents > 0.0f)
  let routeDistance route =
    route
    |> List.sumBy (fun streetId ->
      residentPlan
      |> tryFindStreetView streetId
      |> Option.map (fun street -> streetLength street.Segment)
      |> Option.defaultValue 0.0f)
  let tripVolume residents =
    max 1.0f (MathF.Ceiling(residents * 0.25f))
  streets
  |> List.map (fun origin ->
    let reachableTargets =
      streets
      |> List.filter (fun candidate -> candidate.Id <> origin.Id)
      |> List.choose (fun candidate ->
        match shortestStreetPath residentPlan origin.Id candidate.Id with
        | Ok route ->
            let distance = max 1.0f (routeDistance route)
            let attraction = candidate.Residents / (distance * distance)
            Some (candidate.Id, route, attraction)
        | Error _ -> None)
    match reachableTargets with
    | [] ->
        { StartStreet = origin.Id
          EndStreet = origin.Id
          Volume = tripVolume origin.Residents
          Route = [ origin.Id ] }
    | candidates ->
        let destinationId, route, _ =
          candidates
          |> List.maxBy (fun (destinationId, _, attraction) -> attraction, StreetId.value destinationId)
        { StartStreet = origin.Id
          EndStreet = destinationId
          Volume = tripVolume origin.Residents
          Route = route })

let updateTrafficSimulation (plan: DistrictPlan) : DistrictPlan =
  let residentPlan = withDerivedStreetResidents plan
  let zeroTraffic =
    { residentPlan with
        MajorStreets =
          residentPlan.MajorStreets
          |> List.map (fun street ->
            { street with
                Traffic =
                  { Volume = 0.0f
                    MaxVolume = streetMaxVolumeFromTraffic 0.0f } })
        Quarters =
          residentPlan.Quarters
          |> List.map (fun quarter ->
            { quarter with
                MinorStreets =
                  quarter.MinorStreets
                  |> List.map (fun street ->
                    { street with
                        Traffic =
                          { Volume = 0.0f
                            MaxVolume = streetMaxVolumeFromTraffic 0.0f } }) }) }
  generateResidentTrips residentPlan
  |> List.fold (fun current trip ->
     match applyResidentTrip trip current with
     | Ok nextPlan -> nextPlan
     | Error error -> invalidOp (sprintf "Failed to apply generated trip %A due to %A" trip error))
     zeroTraffic

let private streetPromotionThreshold = 8.0f

let private promoteQualifiedStreetsWithLag promotionLagSteps (priorQualifications: Map<StreetId, int>) (plan: DistrictPlan) =
  let indexedPlan = indexStreetNetwork plan
  let requiredSteps = max 1 promotionLagSteps
  let nextStatuses, nextQualifications =
    allStreetViews indexedPlan
    |> List.fold (fun (statusMap, qualificationMap) street ->
      match street.Status with
      | Built ->
          Map.add street.Id Built statusMap,
          qualificationMap
      | Planned when street.Traffic.Volume >= streetPromotionThreshold ->
          let streak =
            (priorQualifications |> Map.tryFind street.Id |> Option.defaultValue 0) + 1
          match streak >= requiredSteps with
          | true ->
              Map.add street.Id Built statusMap,
              qualificationMap
          | false ->
              Map.add street.Id Planned statusMap,
              Map.add street.Id streak qualificationMap
      | Planned ->
          Map.add street.Id Planned statusMap,
          qualificationMap)
      (Map.empty, Map.empty)
  let promoteMajor (street: MajorStreet) =
    { street with
        Status = nextStatuses |> Map.tryFind street.Id |> Option.defaultValue street.Status }
  let promoteMinor (street: MinorStreet) =
    { street with
        Status = nextStatuses |> Map.tryFind street.Id |> Option.defaultValue street.Status }
  let promotedQuarters =
    indexedPlan.Quarters
    |> List.map (fun quarter ->
      { quarter with
          MinorStreets = quarter.MinorStreets |> List.map promoteMinor })
  { indexedPlan with
      MajorStreets = indexedPlan.MajorStreets |> List.map promoteMajor
      Quarters = promotedQuarters },
  nextQualifications

let promotePlannedStreets (plan: DistrictPlan) : DistrictPlan =
  promoteQualifiedStreetsWithLag 1 Map.empty plan
  |> fst

let applyValuationCurve curve minValue maxValue value =
  let span = max 0.0001f (maxValue - minValue)
  let normalized = clamp01f ((value - minValue) / span)
  match curve with
  | Step ->
      match normalized >= 0.5f with
      | true -> 1.0f
      | false -> 0.0f
  | LinearUp -> normalized
  | LinearDown -> 1.0f - normalized
  | GainUp -> normalized * normalized
  | GainDown -> 1.0f - normalized * normalized

let private evaluateBuildingSubstitutionFactor (factor: BuildingSubstitutionFactor) value =
  applyValuationCurve factor.Curve factor.Min factor.Max value
  * max 0.0f factor.Weight

let computeBuildingSubstitutionProbability (config: BuildingSubstitutionConfig) buildingAgeYears currentPrice potentialPrice =
  let agePressure =
    evaluateBuildingSubstitutionFactor config.AgeFactor (max 0.0f buildingAgeYears)
  let priceGapPressure =
    max 0.0f (potentialPrice - currentPrice)
    |> evaluateBuildingSubstitutionFactor config.PriceGapFactor
  clamp01f (agePressure + priceGapPressure)

let applyBuildingSubstitution stepYears roll probability (potential: LotEconomics) (current: PlannedLot) =
  match roll < probability with
  | true ->
      { current with
          Price = potential.Price
          FloorSpace = potential.FloorSpace
          Residents = potential.Residents
          BuildingAgeYears = 0.0f }
  | false ->
      { current with
          BuildingAgeYears = max 0.0f current.BuildingAgeYears + max 0.0f stepYears }

let computeGlobalLandUseValue (goals: LandUseGoal list) goalScale (lots: PlannedLot list) =
  let totalArea =
    lots
    |> List.sumBy (fun lot -> TRect.area lot.Rect)
  match totalArea <= 0.0f || goals.IsEmpty with
  | true -> 0.0f
  | false ->
      let safeScale = max 0.0001f goalScale
      goals
      |> List.sumBy (fun goal ->
        let percent =
          lots
          |> List.filter (fun lot -> lot.LandUseType = goal.LandUseType)
          |> List.sumBy (fun lot -> TRect.area lot.Rect)
          |> fun area -> area / totalArea
        let delta = (percent - goal.TargetPercent) / safeScale
        delta * delta)
      |> (*) -1.0f

let ensureBlockLotsForLandUseSimulation (block: PlannedBlock) =
  match block.Lots with
  | [] ->
      let frontageEdge =
        selectLongestStreetFacingEdge block |> Option.defaultValue North
      let lot =
        PlannedLot.create block.Rect frontageEdge block.Rect
        |> fun seeded ->
          { seeded with
              LandUseType = block.LandUseType
              LandUseValue = block.LandUseValue }
      { block with Lots = [ lot ] }
  | _ -> block

let private landUseBlockLots (plan: DistrictPlan) =
  plan.Quarters
  |> List.collect (fun quarter -> quarter.Blocks)
  |> List.collect _.Lots

let private lotContextStatuses (plan: DistrictPlan) (lot: PlannedLot) =
  allStreetViews plan
  |> List.filter (fun street -> rectsTouchOrOverlap street.Segment lot.BlockRect || rectsTouchOrOverlap street.Segment lot.Rect)
  |> List.map _.Status

let private inferLotFrontageStatus (plan: DistrictPlan) (lot: PlannedLot) =
  let statuses = lotContextStatuses plan lot
  match statuses |> List.exists ((=) Built), statuses |> List.exists ((=) Planned) with
  | true, _ -> Built
  | false, true -> Planned
  | false, false -> lot.FrontingStreetStatus

let private lotMetricValue (plan: DistrictPlan) (lot: PlannedLot) candidateType metric =
  let sameBlockLots =
    plan.Quarters
    |> List.collect _.Blocks
    |> List.tryFind (fun block -> block.Rect = lot.BlockRect)
    |> Option.map _.Lots
    |> Option.defaultValue []
  let sameQuarterLots =
    plan.Quarters
    |> List.tryFind (fun quarter -> quarter.Blocks |> List.exists (fun block -> block.Rect = lot.BlockRect))
    |> Option.map (fun quarter -> quarter.Blocks |> List.collect _.Lots)
    |> Option.defaultValue sameBlockLots
  let touchingTraffic =
    allStreetViews plan
    |> List.filter (fun street -> rectsTouchOrOverlap street.Segment lot.BlockRect || rectsTouchOrOverlap street.Segment lot.Rect)
    |> List.map (fun street -> street.Traffic.Volume)
  let ratioBy selector (items: PlannedLot list) =
    match items with
    | [] -> 0.0f
    | xs ->
        let total = xs |> List.length |> float32
        let matching = xs |> List.filter selector |> List.length |> float32
        matching / max 1.0f total
  match metric with
  | LotArea -> TRect.area lot.Rect
  | FrontageAccess ->
      match inferLotFrontageStatus plan lot with
      | Built -> 1.0f
      | Planned -> 0.0f
  | TrafficVolume -> touchingTraffic |> List.fold max 0.0f
  | SameUseCluster -> ratioBy (fun peer -> peer.LandUseType = candidateType) sameBlockLots
  | NeighborInfluence landUseType -> ratioBy (fun peer -> peer.LandUseType = landUseType) sameQuarterLots

let evaluateLocalLotLandUseValue (definitions: LandUseDefinition list) (plan: DistrictPlan) (lot: PlannedLot) candidateType =
  match definitions |> List.tryFind (fun definition -> definition.LandUseType = candidateType) with
  | None -> evaluateLotLandUseValue { lot with LandUseType = candidateType }
  | Some definition ->
      let weights =
        definition.Valuations
        |> List.sumBy (fun valuation -> max 0.0f valuation.Weight)
      match definition.Valuations.IsEmpty || weights <= 0.0f with
      | true -> 0.0f
      | false ->
          definition.Valuations
          |> List.sumBy (fun valuation ->
            let weight = max 0.0f valuation.Weight / weights
            let metricValue = lotMetricValue plan lot candidateType valuation.Metric
            let bounded = applyValuationCurve valuation.Curve valuation.Min valuation.Max metricValue
            bounded * weight)
          |> clamp01f

let private weightedLandUseSummary areaSelector valueSelector items =
  let weighted =
    items
    |> List.groupBy (fun (landUseType, _, _) -> landUseType)
    |> List.map (fun (landUseType, entries) ->
      let totalArea = entries |> List.sumBy areaSelector
      let weightedValue =
        match totalArea <= 0.0f with
        | true -> 0.0f
        | false ->
            entries
            |> List.sumBy (fun entry -> areaSelector entry * valueSelector entry)
            |> fun total -> total / totalArea
      landUseType, totalArea, weightedValue)
  match weighted with
  | [] -> ResidentialUse, 0.0f
  | xs ->
      xs
      |> List.maxBy (fun (landUseType, totalArea, weightedValue) -> totalArea, weightedValue, landUseType)
      |> fun (landUseType, _, weightedValue) -> landUseType, weightedValue

let recomputeBlockDominantLandUse (block: PlannedBlock) =
  let normalized = ensureBlockLotsForLandUseSimulation block
  let entries =
    normalized.Lots
    |> List.map (fun lot -> lot.LandUseType, TRect.area lot.Rect, lot.LandUseValue)
  let landUseType, landUseValue = weightedLandUseSummary (fun (_, area, _) -> area) (fun (_, _, value) -> value) entries
  { normalized with
      LandUseType = landUseType
      LandUseValue = landUseValue }

let recomputeQuarterDominantLandUse (quarter: QuarterPlan) =
  let entries =
    quarter.Blocks
    |> List.map (fun block -> block.LandUseType, TRect.area block.Rect, block.LandUseValue)
  let landUseType, landUseValue = weightedLandUseSummary (fun (_, area, _) -> area) (fun (_, _, value) -> value) entries
  { quarter with
      LandUseType = landUseType
      LandUseValue = landUseValue }

let private allLotKeys (plan: DistrictPlan) =
  let rectKey (rect: TRect) = rect.X, rect.Z, rect.W, rect.H
  plan.Quarters
  |> List.collect (fun quarter -> quarter.Blocks)
  |> List.collect _.Lots
  |> List.map (fun lot -> rectKey lot.BlockRect, rectKey lot.Rect, lot.FrontageEdge)
  |> List.sort

let private meanLotLandUseValue (lots: PlannedLot list) =
  match lots with
  | [] -> 0.0f
  | xs -> xs |> List.averageBy (fun lot -> max 0.0f lot.LandUseValue)

let private mutateLotLandUse targetKey candidateType (plan: DistrictPlan) =
  let rectKey (rect: TRect) = rect.X, rect.Z, rect.W, rect.H
  let updateLot (lot: PlannedLot) =
    match (rectKey lot.BlockRect, rectKey lot.Rect, lot.FrontageEdge) = targetKey with
    | true -> { lot with LandUseType = candidateType }
    | false -> lot
  { plan with
      Quarters =
        plan.Quarters
        |> List.map (fun quarter ->
          { quarter with
              Blocks =
                quarter.Blocks
                |> List.map (fun (block: PlannedBlock) ->
                  { block with Lots = block.Lots |> List.map updateLot }) }) }

let private refreshLandUsePlan (config: LandUseSimulationConfig) (plan: DistrictPlan) =
  let withLots =
    { plan with
        Quarters =
          plan.Quarters
          |> List.map (fun quarter ->
            { quarter with
                Blocks = quarter.Blocks |> List.map ensureBlockLotsForLandUseSimulation }) }
  let repricedInput =
    { withLots with
        Quarters =
          withLots.Quarters
          |> List.map (fun quarter ->
            let refreshedBlocks =
              quarter.Blocks
              |> List.map (fun block ->
                let refreshedLots =
                  block.Lots
                  |> List.map (fun lot ->
                    let seeded =
                      { lot with FrontingStreetStatus = inferLotFrontageStatus withLots lot }
                    let landUseValue =
                      evaluateLocalLotLandUseValue config.Definitions withLots seeded seeded.LandUseType
                    { seeded with LandUseValue = landUseValue })
                { block with Lots = refreshedLots })
            { quarter with Blocks = refreshedBlocks }) }
  let sharedMeanLandUseValue =
    repricedInput
    |> landUseBlockLots
    |> meanLotLandUseValue
  let refreshedQuarters =
    repricedInput.Quarters
    |> List.map (fun quarter ->
      let refreshedBlocks =
        quarter.Blocks
        |> List.map (fun block ->
          let refreshedLots =
            block.Lots
            |> List.map (fun lot ->
              let economics =
                computeLotEconomics config.AveragePricePerSqm sharedMeanLandUseValue lot
              let enriched =
                { lot with
                    Price = economics.Price
                    FloorSpace = economics.FloorSpace
                    Residents = economics.Residents }
              { enriched with Envelope = computeBuildingEnvelopeInPlan repricedInput enriched })
          { block with Lots = refreshedLots }
          |> recomputeBlockDominantLandUse)
      { quarter with Blocks = refreshedBlocks }
      |> recomputeQuarterDominantLandUse)
  { repricedInput with Quarters = refreshedQuarters }

let private planLandUseScore (config: LandUseSimulationConfig) (plan: DistrictPlan) =
  let lots = landUseBlockLots plan
  let globalValue = computeGlobalLandUseValue config.Goals config.GoalScale lots
  let localValue =
    let totalArea = lots |> List.sumBy (fun lot -> TRect.area lot.Rect)
    match totalArea <= 0.0f with
    | true -> 0.0f
    | false ->
        lots
        |> List.sumBy (fun lot -> TRect.area lot.Rect * lot.LandUseValue)
        |> fun weighted -> weighted / totalArea
  config.GlobalWeight * globalValue + config.LocalWeight * localValue, globalValue

let private availableLandUseTypes (config: LandUseSimulationConfig) (plan: DistrictPlan) =
  (config.Definitions |> List.map _.LandUseType)
  @ (config.Goals |> List.map _.LandUseType)
  @ (landUseBlockLots plan |> List.map _.LandUseType)
  |> List.distinct

let private defaultLandUseSimulationConfig =
  { Goals =
      [ { LandUseType = ResidentialUse; TargetPercent = 0.45f }
        { LandUseType = CommercialUse; TargetPercent = 0.15f }
        { LandUseType = IndustrialUse; TargetPercent = 0.10f }
        { LandUseType = ParkUse; TargetPercent = 0.10f }
        { LandUseType = CivicUse; TargetPercent = 0.05f }
        { LandUseType = MixedUseZone; TargetPercent = 0.15f } ]
    Definitions =
      [ { LandUseType = ResidentialUse
          Valuations =
            [ { Metric = LotArea; Curve = LinearUp; Min = 0.0f; Max = 120.0f; Weight = 0.35f }
              { Metric = FrontageAccess; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 0.30f }
              { Metric = NeighborInfluence ResidentialUse; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 0.35f } ] }
        { LandUseType = CommercialUse
          Valuations =
            [ { Metric = FrontageAccess; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 0.30f }
              { Metric = TrafficVolume; Curve = LinearUp; Min = 0.0f; Max = 16.0f; Weight = 0.40f }
              { Metric = NeighborInfluence CommercialUse; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 0.30f } ] }
        { LandUseType = IndustrialUse
          Valuations =
            [ { Metric = LotArea; Curve = LinearUp; Min = 0.0f; Max = 240.0f; Weight = 0.55f }
              { Metric = TrafficVolume; Curve = LinearUp; Min = 0.0f; Max = 12.0f; Weight = 0.25f }
              { Metric = FrontageAccess; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 0.20f } ] }
        { LandUseType = ParkUse
          Valuations =
            [ { Metric = LotArea; Curve = LinearUp; Min = 0.0f; Max = 180.0f; Weight = 0.50f }
              { Metric = FrontageAccess; Curve = LinearDown; Min = 0.0f; Max = 1.0f; Weight = 0.20f }
              { Metric = NeighborInfluence ParkUse; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 0.30f } ] }
        { LandUseType = CivicUse
          Valuations =
            [ { Metric = LotArea; Curve = LinearUp; Min = 0.0f; Max = 160.0f; Weight = 0.40f }
              { Metric = FrontageAccess; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 0.30f }
              { Metric = TrafficVolume; Curve = LinearUp; Min = 0.0f; Max = 12.0f; Weight = 0.30f } ] }
        { LandUseType = MixedUseZone
          Valuations =
            [ { Metric = FrontageAccess; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 0.30f }
              { Metric = TrafficVolume; Curve = LinearUp; Min = 0.0f; Max = 14.0f; Weight = 0.35f }
              { Metric = SameUseCluster; Curve = LinearDown; Min = 0.0f; Max = 1.0f; Weight = 0.35f } ] } ]
    AveragePricePerSqm = 100.0f
    GlobalWeight = 0.65f
    LocalWeight = 0.35f
    GoalScale = 1.0f
    AttemptsFraction = 0.5f
    RejectedDeltaThreshold = 0.15f
    Seed = 42
    Cadence =
      { StepYears = 1.0f
        ReevaluationYears = 1.0f
        ElapsedSinceLastEvaluationYears = 1.0f } }

let private defaultBuildingSubstitutionConfig =
  { AgeFactor =
      { Curve = LinearUp
        Min = 0.0f
        Max = 40.0f
        Weight = 0.35f }
    PriceGapFactor =
      { Curve = LinearUp
        Min = 0.0f
        Max = 100.0f
        Weight = 0.65f }
    Seed = 42 }

let updateLandUseSimulationWith (config: LandUseSimulationConfig) (plan: DistrictPlan) : DistrictPlan * LandUseUpdateStats =
  match config.Cadence.ElapsedSinceLastEvaluationYears < config.Cadence.ReevaluationYears with
  | true ->
      plan,
      { EvaluatedLots = 0
        Attempts = 0
        Accepted = 0
        Rejected = 0
        SkippedByCadence = true
        GlobalValueBefore = 0.0f
        GlobalValueAfter = 0.0f }
  | false ->
      let initialPlan = plan |> indexStreetNetwork |> refreshLandUsePlan config
      let initialScore, initialGlobal = planLandUseScore config initialPlan
      let lotKeys = allLotKeys initialPlan
      let availableTypes = availableLandUseTypes config initialPlan
      let lotCount = lotKeys.Length
      let attempts =
        match lotCount <= 0 with
        | true -> 0
        | false ->
            MathF.Ceiling(float32 lotCount * max 0.0f config.AttemptsFraction)
            |> int
            |> max 1
            |> min lotCount
      let rng = Random(config.Seed)
      let finalPlan, accepted, rejected, finalScore, finalGlobal =
        ((initialPlan, 0, 0, initialScore, initialGlobal), [ 0 .. attempts - 1 ])
        ||> List.fold (fun (currentPlan, accepted, rejected, currentScore, currentGlobal) _ ->
          match lotCount <= 0 with
          | true -> currentPlan, accepted, rejected, currentScore, currentGlobal
          | false ->
              let lotIndex = rng.Next(lotCount)
              let targetKey = lotKeys.[lotIndex]
              let currentLot =
                currentPlan.Quarters
                |> List.collect _.Blocks
                |> List.collect _.Lots
                |> List.find (fun lot ->
                  let rectKey (rect: TRect) = rect.X, rect.Z, rect.W, rect.H
                  (rectKey lot.BlockRect, rectKey lot.Rect, lot.FrontageEdge) = targetKey)
              let candidateTypes =
                availableTypes
                |> List.filter (fun landUseType -> landUseType <> currentLot.LandUseType)
              match candidateTypes with
              | [] -> currentPlan, accepted, rejected, currentScore, currentGlobal
              | xs ->
                  let candidateType = xs.[rng.Next(xs.Length)]
                  let candidatePlan =
                    currentPlan
                    |> mutateLotLandUse targetKey candidateType
                    |> refreshLandUsePlan config
                  let candidateScore, candidateGlobal = planLandUseScore config candidatePlan
                  let delta = candidateScore - currentScore
                  let accept =
                    match delta >= 0.0f with
                    | true -> true
                    | false ->
                        let loss = -delta
                        let threshold = max 0.0001f config.RejectedDeltaThreshold
                        loss < threshold
                        && float32 (rng.NextDouble()) < 1.0f - (loss / threshold)
                  match accept with
                  | true -> candidatePlan, accepted + 1, rejected, candidateScore, candidateGlobal
                  | false -> currentPlan, accepted, rejected + 1, currentScore, currentGlobal)
      finalPlan,
      { EvaluatedLots = lotCount
        Attempts = attempts
        Accepted = accepted
        Rejected = rejected
        SkippedByCadence = false
        GlobalValueBefore = initialGlobal
        GlobalValueAfter = finalGlobal }

let updateLandUseSimulation (plan: DistrictPlan) : DistrictPlan =
  updateLandUseSimulationWith defaultLandUseSimulationConfig plan
  |> fst

let private floatHash32 (value: float32) =
  BitConverter.SingleToInt32Bits value

let private stableSubstitutionRoll seed stepIndex (lot: PlannedLot) =
  let mutable hash = 17
  let inline mix value =
    hash <- (hash * 31) + value
  mix seed
  mix stepIndex
  mix (floatHash32 lot.BlockRect.X)
  mix (floatHash32 lot.BlockRect.Z)
  mix (floatHash32 lot.BlockRect.W)
  mix (floatHash32 lot.BlockRect.H)
  mix (floatHash32 lot.Rect.X)
  mix (floatHash32 lot.Rect.Z)
  mix (floatHash32 lot.Rect.W)
  mix (floatHash32 lot.Rect.H)
  mix (match lot.FrontageEdge with | North -> 1 | South -> 2 | East -> 3 | West -> 4)
  let normalized = float32 (uint32 hash &&& 0x00FFFFFFu) / float32 0x01000000u
  clamp01f normalized

let updateBuildingSubstitutionWith (config: BuildingSubstitutionConfig) stepIndex stepYears (currentPlan: DistrictPlan) (potentialPlan: DistrictPlan) : DistrictPlan * BuildingSubstitutionStats =
  let rectKey (rect: TRect) = rect.X, rect.Z, rect.W, rect.H
  let substitutionLotKey (lot: PlannedLot) =
    rectKey lot.BlockRect, rectKey lot.Rect, lot.FrontageEdge
  let currentLots =
    currentPlan.Quarters
    |> List.collect _.Blocks
    |> List.collect _.Lots
    |> List.map (fun lot -> substitutionLotKey lot, lot)
    |> Map.ofList
  let mutable evaluatedLots = 0
  let mutable replacedLots = 0
  let mutable retainedLots = 0
  let mutable totalProbability = 0.0f
  let updatedQuarters =
    potentialPlan.Quarters
    |> List.map (fun quarter ->
      let updatedBlocks =
        quarter.Blocks
        |> List.map (fun block ->
          let updatedLots =
            block.Lots
            |> List.map (fun potentialLot ->
              match currentLots |> Map.tryFind (substitutionLotKey potentialLot) with
              | None -> potentialLot
              | Some currentLot ->
                  let probability =
                    computeBuildingSubstitutionProbability config currentLot.BuildingAgeYears currentLot.Price potentialLot.Price
                  let roll = stableSubstitutionRoll config.Seed stepIndex potentialLot
                  let updatedEconomics =
                    applyBuildingSubstitution stepYears roll probability
                      { Price = potentialLot.Price
                        FloorSpace = potentialLot.FloorSpace
                        Residents = potentialLot.Residents }
                      currentLot
                  let replaced = roll < probability
                  evaluatedLots <- evaluatedLots + 1
                  totalProbability <- totalProbability + probability
                  match replaced with
                  | true -> replacedLots <- replacedLots + 1
                  | false -> retainedLots <- retainedLots + 1
                  { potentialLot with
                      Price = updatedEconomics.Price
                      FloorSpace = updatedEconomics.FloorSpace
                      Residents = updatedEconomics.Residents
                      BuildingAgeYears = updatedEconomics.BuildingAgeYears
                      Envelope =
                        match replaced with
                        | true -> potentialLot.Envelope
                        | false -> currentLot.Envelope })
          { block with Lots = updatedLots })
      { quarter with Blocks = updatedBlocks })
  let meanProbability =
    match evaluatedLots <= 0 with
    | true -> 0.0f
    | false -> totalProbability / float32 evaluatedLots
  { potentialPlan with Quarters = updatedQuarters },
  { EvaluatedLots = evaluatedLots
    ReplacedLots = replacedLots
    RetainedLots = retainedLots
    MeanProbability = meanProbability }

let updateBuildingSubstitution stepIndex stepYears currentPlan potentialPlan =
  updateBuildingSubstitutionWith defaultBuildingSubstitutionConfig stepIndex stepYears currentPlan potentialPlan
  |> fst

let simulateUrbanStep (plan: DistrictPlan) : DistrictPlan =
  plan
  |> updateTrafficSimulation
  |> promotePlannedStreets
  |> updateLandUseSimulation

let runSimulationStage stage (plan: DistrictPlan) : DistrictPlan =
  match stage with
  | UpdateTrafficSimulationStage -> updateTrafficSimulation plan
  | PromotePlannedStreetsStage -> promotePlannedStreets plan
  | UpdateLandUseSimulationStage -> updateLandUseSimulation plan

let private countBuiltMajorStreets (plan: DistrictPlan) =
  plan.MajorStreets
  |> List.filter (fun street -> street.Status = Built)
  |> List.length

let private countBuiltMinorStreets (plan: DistrictPlan) =
  plan.Quarters
  |> List.collect _.MinorStreets
  |> List.filter (fun street -> street.Status = Built)
  |> List.length

let private planRectKey (rect: TRect) =
  rect.X, rect.Z, rect.W, rect.H

let private quarterMap (plan: DistrictPlan) =
  plan.Quarters
  |> List.map (fun quarter -> planRectKey quarter.Rect, quarter)
  |> Map.ofList

let private blockMap (plan: DistrictPlan) =
  plan.Quarters
  |> List.collect _.Blocks
  |> List.map (fun block -> planRectKey block.Rect, block)
  |> Map.ofList

let private lotKey (lot: PlannedLot) =
  planRectKey lot.BlockRect, planRectKey lot.Rect, lot.FrontageEdge

let private lotMap (plan: DistrictPlan) =
  plan.Quarters
  |> List.collect _.Blocks
  |> List.collect _.Lots
  |> List.map (fun lot -> lotKey lot, lot)
  |> Map.ofList

let private streetMap (plan: DistrictPlan) =
  allStreetViews plan
  |> List.map (fun street -> street.Id, street)
  |> Map.ofList

let summarizeSimulationStageDelta (beforePlan: DistrictPlan) (afterPlan: DistrictPlan) : SimulationStageDelta =
  let beforeQuarterMap = quarterMap beforePlan
  let beforeBlockMap = blockMap beforePlan
  let beforeLotMap = lotMap beforePlan
  let beforeStreetMap = streetMap beforePlan
  let trafficUpdatedStreetCount, totalTrafficVolumeDelta =
    allStreetViews afterPlan
    |> List.fold (fun (changedCount, totalDelta) street ->
      match beforeStreetMap |> Map.tryFind street.Id with
      | Some beforeStreet ->
          let volumeDelta = abs (beforeStreet.Traffic.Volume - street.Traffic.Volume)
          let changed =
            volumeDelta > 1e-5f
            || abs (beforeStreet.Traffic.MaxVolume - street.Traffic.MaxVolume) > 1e-5f
          (changedCount + (if changed then 1 else 0), totalDelta + volumeDelta)
      | None ->
          (changedCount + 1, totalDelta + abs street.Traffic.Volume))
      (0, 0.0f)
  let promotedMajorStreetCount = max 0 (countBuiltMajorStreets afterPlan - countBuiltMajorStreets beforePlan)
  let promotedMinorStreetCount = max 0 (countBuiltMinorStreets afterPlan - countBuiltMinorStreets beforePlan)
  let quarterLandUseChangedCount =
    afterPlan.Quarters
    |> List.sumBy (fun quarter ->
      match beforeQuarterMap |> Map.tryFind (planRectKey quarter.Rect) with
      | Some beforeQuarter when beforeQuarter.LandUseType <> quarter.LandUseType
                           || abs (beforeQuarter.LandUseValue - quarter.LandUseValue) > 1e-5f -> 1
      | _ -> 0)
  let blockLandUseChangedCount, totalBlockLandUseValueDelta =
    afterPlan.Quarters
    |> List.collect _.Blocks
    |> List.fold (fun (changedCount, totalDelta) block ->
      match beforeBlockMap |> Map.tryFind (planRectKey block.Rect) with
      | Some beforeBlock ->
          let valueDelta = abs (beforeBlock.LandUseValue - block.LandUseValue)
          let changed =
            beforeBlock.LandUseType <> block.LandUseType
            || valueDelta > 1e-5f
          (changedCount + (if changed then 1 else 0), totalDelta + valueDelta)
      | None ->
          (changedCount + 1, totalDelta + abs block.LandUseValue))
      (0, 0.0f)
  let lotLandUseChangedCount, totalLotLandUseValueDelta =
    afterPlan.Quarters
    |> List.collect _.Blocks
    |> List.collect _.Lots
    |> List.fold (fun (changedCount, totalDelta) lot ->
      match beforeLotMap |> Map.tryFind (lotKey lot) with
      | Some beforeLot ->
          let valueDelta = abs (beforeLot.LandUseValue - lot.LandUseValue)
          let changed =
            beforeLot.LandUseType <> lot.LandUseType
            || beforeLot.FrontingStreetStatus <> lot.FrontingStreetStatus
            || valueDelta > 1e-5f
          (changedCount + (if changed then 1 else 0), totalDelta + valueDelta)
      | None ->
          (changedCount + 1, totalDelta + abs lot.LandUseValue))
      (0, 0.0f)
  { TrafficUpdatedStreetCount = trafficUpdatedStreetCount
    TotalTrafficVolumeDelta = totalTrafficVolumeDelta
    PromotedMajorStreetCount = promotedMajorStreetCount
    PromotedMinorStreetCount = promotedMinorStreetCount
    QuarterLandUseChangedCount = quarterLandUseChangedCount
    BlockLandUseChangedCount = blockLandUseChangedCount
    TotalBlockLandUseValueDelta = totalBlockLandUseValueDelta
    LotLandUseChangedCount = lotLandUseChangedCount
    TotalLotLandUseValueDelta = totalLotLandUseValueDelta
    ReplacedLotCount = 0 }

type SimulationTimelineState =
  { Plan: DistrictPlan
    ElapsedYears: float32
    LandUseElapsedYears: float32
    PromotionQualificationSteps: Map<StreetId, int> }

let simulateUrbanTimelineWith (landUseConfig: LandUseSimulationConfig) (config: SimulationTimelineConfig) seed (initial: DistrictPlan) : SimulationTimeline =
  if config.Steps < 0 then
    invalidArg "config.Steps" "Simulation timeline steps must be non-negative."
  if config.StepYears <= 0.0f then
    invalidArg "config.StepYears" "Simulation timeline step years must be positive."
  if config.LandUseReevaluationYears < 0.0f then
    invalidArg "config.LandUseReevaluationYears" "Simulation timeline reevaluation years cannot be negative."
  if config.PromotionLagSteps <= 0 then
    invalidArg "config.PromotionLagSteps" "Simulation timeline promotion lag must be at least one step."

  let landUseConfigFor elapsedSinceLastEvaluationYears =
    { landUseConfig with
        Cadence =
          { StepYears = config.StepYears
            ReevaluationYears = config.LandUseReevaluationYears
            ElapsedSinceLastEvaluationYears = elapsedSinceLastEvaluationYears } }

  let nextLandUseElapsedYears elapsedSinceLastEvaluationYears (stats: LandUseUpdateStats) =
    match stats.SkippedByCadence, config.LandUseReevaluationYears <= 0.0f with
    | true, _ -> elapsedSinceLastEvaluationYears
    | false, true -> 0.0f
    | false, false ->
        let rec subtractHorizon remainingYears =
          match remainingYears >= config.LandUseReevaluationYears with
          | true -> subtractHorizon (remainingYears - config.LandUseReevaluationYears)
          | false -> remainingYears
        subtractHorizon elapsedSinceLastEvaluationYears

  let initialState =
    { Plan = initial |> indexStreetNetwork
      ElapsedYears = 0.0f
      LandUseElapsedYears = 0.0f
      PromotionQualificationSteps = Map.empty }

  let snapshotsRev, finalState =
    (([], initialState), [ 1 .. config.Steps ])
    ||> List.fold (fun (snapshots, state) stepIndex ->
      let trafficUpdated = updateTrafficSimulation state.Plan
      let promotedPlan, promotionQualifications =
        promoteQualifiedStreetsWithLag config.PromotionLagSteps state.PromotionQualificationSteps trafficUpdated
      let elapsedYears = state.ElapsedYears + config.StepYears
      let landUseElapsedYears = state.LandUseElapsedYears + config.StepYears
      let potentialPlan, landUseStats =
        promotedPlan
        |> updateLandUseSimulationWith (landUseConfigFor landUseElapsedYears)
      let nextPlan, substitutionStats =
        match config.BuildingSubstitution with
        | Some substitutionConfig ->
            updateBuildingSubstitutionWith substitutionConfig stepIndex config.StepYears promotedPlan potentialPlan
        | None ->
            potentialPlan,
            { EvaluatedLots = 0
              ReplacedLots = 0
              RetainedLots = 0
              MeanProbability = 0.0f }
      let indexedNextPlan = nextPlan |> indexStreetNetwork
      let delta =
        { summarizeSimulationStageDelta state.Plan indexedNextPlan with
            ReplacedLotCount = substitutionStats.ReplacedLots }
      let snapshot =
        { StepIndex = stepIndex
          ElapsedYears = elapsedYears
          Plan = indexedNextPlan
          LandUseStats = landUseStats
          BuildingSubstitutionStats = substitutionStats
          Delta = delta }
      let nextState =
        { Plan = indexedNextPlan
          ElapsedYears = elapsedYears
          LandUseElapsedYears = nextLandUseElapsedYears landUseElapsedYears landUseStats
          PromotionQualificationSteps = promotionQualifications }
      snapshot :: snapshots,
      nextState)

  { Seed = seed
    Initial = initialState.Plan
    Steps = snapshotsRev |> List.rev
    Final = finalState.Plan }

let simulateUrbanTimeline (config: SimulationTimelineConfig) seed (initial: DistrictPlan) : SimulationTimeline =
  simulateUrbanTimelineWith defaultLandUseSimulationConfig config seed initial

let stepWithTrace seed initial : SimulationTrace =
  let snapshots, finalPlan =
    (([], initial), simulationStages)
    ||> List.fold (fun (snapshots, plan) stage ->
      let nextPlan = runSimulationStage stage plan
      let snapshot =
        { Stage = stage
          Plan = nextPlan }
      (snapshots @ [ snapshot ], nextPlan))
  { Seed = seed
    Initial = initial
    Stages = snapshots
    Final = finalPlan }

let stepWithInstrumentation seed initial : InstrumentedSimulationTrace =
  let snapshots, finalPlan, totalElapsed =
    (([], initial, TimeSpan.Zero), simulationStages)
    ||> List.fold (fun (snapshots, plan, totalElapsed) stage ->
      let stopwatch = System.Diagnostics.Stopwatch.StartNew()
      let nextPlan = runSimulationStage stage plan
      stopwatch.Stop()
      let delta = summarizeSimulationStageDelta plan nextPlan
      let snapshot =
        { Stage = stage
          Plan = nextPlan
          Elapsed = stopwatch.Elapsed
          Delta = delta }
      (snapshots @ [ snapshot ], nextPlan, totalElapsed + stopwatch.Elapsed))
  { Seed = seed
    Initial = initial
    Stages = snapshots
    Final = finalPlan
    TotalElapsed = totalElapsed }

let private rectSortKey (rect: TRect) =
  rect.X, rect.Z, rect.W, rect.H

let canonicalizeDistrictPlan (plan: DistrictPlan) : CanonicalDistrictPlan =
  let normalizeMajorStreetIds (streets: CanonicalMajorStreet list) =
    streets
    |> List.sortBy (fun street ->
      street.StreetStatus,
      rectSortKey street.StreetRect,
      street.StreetResidents,
      street.StreetTrafficVolume,
      street.StreetMaxVolume)
    |> List.mapi (fun index street -> { street with StreetId = StreetId index })

  let normalizeMinorStreetIds (streets: CanonicalMinorStreet list) =
    streets
    |> List.sortBy (fun street ->
      street.StreetStatus,
      rectSortKey street.StreetRect,
      street.StreetResidents,
      street.StreetTrafficVolume,
      street.StreetMaxVolume)
    |> List.mapi (fun index street -> { street with StreetId = StreetId index })

  let canonicalMajorStreet (street: MajorStreet) : CanonicalMajorStreet =
    { StreetId = street.Id
      StreetRect = street.Segment
      StreetStatus = street.Status
      StreetResidents = street.Residents
      StreetTrafficVolume = street.Traffic.Volume
      StreetMaxVolume = street.Traffic.MaxVolume }
  let canonicalMinorStreet (street: MinorStreet) : CanonicalMinorStreet =
    { StreetId = street.Id
      StreetRect = street.Segment
      StreetStatus = street.Status
      StreetResidents = street.Residents
      StreetTrafficVolume = street.Traffic.Volume
      StreetMaxVolume = street.Traffic.MaxVolume }
  let canonicalLot (lot: PlannedLot) : CanonicalLot =
    { LotRect = lot.Rect
      LotFrontageEdge = lot.FrontageEdge
      LotLandUseType = lot.LandUseType
      LotLandUseValue = lot.LandUseValue
      LotFrontingStreetStatus = lot.FrontingStreetStatus
      LotResidents = lot.Residents }
  let canonicalBlock (block: PlannedBlock) : CanonicalBlock =
    { BlockRect = block.Rect
      BlockLandUseType = block.LandUseType
      BlockLandUseValue = block.LandUseValue
      CanonicalLots =
        block.Lots
        |> List.map canonicalLot
        |> List.sortBy (fun lot ->
          rectSortKey lot.LotRect,
          lot.LotFrontageEdge,
          lot.LotLandUseType,
          lot.LotLandUseValue,
          lot.LotFrontingStreetStatus,
          lot.LotResidents) }
  let canonicalQuarter (quarter: QuarterPlan) : CanonicalQuarter =
    { QuarterRect = quarter.Rect
      QuarterLandUseType = quarter.LandUseType
      QuarterLandUseValue = quarter.LandUseValue
      CanonicalMinorStreets =
        quarter.MinorStreets
        |> List.map canonicalMinorStreet
        |> normalizeMinorStreetIds
      CanonicalBlocks =
        quarter.Blocks
        |> List.map canonicalBlock
        |> List.sortBy (fun block -> block.BlockLandUseType, rectSortKey block.BlockRect, block.BlockLandUseValue) }
  { CanonicalMajorStreets =
      plan.MajorStreets
      |> List.map canonicalMajorStreet
      |> normalizeMajorStreetIds
    CanonicalQuarters =
      plan.Quarters
      |> List.map canonicalQuarter
      |> List.sortBy (fun quarter -> rectSortKey quarter.QuarterRect, quarter.QuarterLandUseType, quarter.QuarterLandUseValue) }

let canonicalizeSimulationTrace (trace: SimulationTrace) : CanonicalSimulationTrace =
  { TraceSeed = trace.Seed
    InitialPlan = canonicalizeDistrictPlan trace.Initial
    StagePlans =
      trace.Stages
      |> List.map (fun snapshot ->
        { CanonicalStage = snapshot.Stage
          CanonicalPlan = canonicalizeDistrictPlan snapshot.Plan })
    FinalPlan = canonicalizeDistrictPlan trace.Final }

let private medianTimeSpan (durations: TimeSpan list) =
  let orderedTicks =
    durations
    |> List.map _.Ticks
    |> List.sort
  match orderedTicks with
  | [] -> TimeSpan.Zero
  | xs ->
      let mid = xs.Length / 2
      match xs.Length % 2 = 0 with
      | true -> TimeSpan.FromTicks((xs.[mid - 1] + xs.[mid]) / 2L)
      | false -> TimeSpan.FromTicks(xs.[mid])

let private asSimulationTrace (trace: InstrumentedSimulationTrace) : SimulationTrace =
  { Seed = trace.Seed
    Initial = trace.Initial
    Stages =
      trace.Stages
      |> List.map (fun snapshot ->
        { Stage = snapshot.Stage
          Plan = snapshot.Plan })
    Final = trace.Final }

let benchmarkSimulationStep warmupIterations measuredIterations seed initial : SimulationBenchmarkSummary =
  match warmupIterations < 0 with
  | true -> invalidArg (nameof warmupIterations) "warmupIterations must be non-negative"
  | false -> ()
  match measuredIterations <= 0 with
  | true -> invalidArg (nameof measuredIterations) "measuredIterations must be positive"
  | false -> ()

  let runBenchmarkIteration () = stepWithInstrumentation seed initial

  for _ in 1 .. warmupIterations do
    runBenchmarkIteration () |> ignore

  let measuredRuns =
    [ for _ in 1 .. measuredIterations -> runBenchmarkIteration () ]

  let canonicalResult =
    measuredRuns.Head
    |> asSimulationTrace
    |> canonicalizeSimulationTrace

  measuredRuns
  |> List.iter (fun trace ->
    let candidate = trace |> asSimulationTrace |> canonicalizeSimulationTrace
    if candidate <> canonicalResult then
      invalidOp "Benchmark runs produced inconsistent canonical results")

  let stageBenchmarks =
    simulationStages
    |> List.map (fun stage ->
      let stageRuns =
        measuredRuns
        |> List.map (fun trace ->
          trace.Stages
          |> List.find (fun snapshot -> snapshot.Stage = stage))
      let durations = stageRuns |> List.map _.Elapsed
      { Stage = stage
        Iterations = measuredIterations
        MinElapsed = durations |> List.min
        MedianElapsed = medianTimeSpan durations
        MaxElapsed = durations |> List.max
        LastObservedDelta = stageRuns |> List.last |> _.Delta })

  let totalDurations = measuredRuns |> List.map _.TotalElapsed
  { WarmupIterations = warmupIterations
    MeasuredIterations = measuredIterations
    StageBenchmarks = stageBenchmarks
    TotalMinElapsed = totalDurations |> List.min
    TotalMedianElapsed = medianTimeSpan totalDurations
    TotalMaxElapsed = totalDurations |> List.max
    CanonicalResult = canonicalResult }

let evaluateBenchmarkBudget budget (summary: SimulationBenchmarkSummary) =
  match budget with
  | None -> NotEvaluated
  | Some target when summary.TotalMedianElapsed <= target.TargetMedianElapsed -> WithinReference
  | Some target -> OverReference (summary.TotalMedianElapsed - target.TargetMedianElapsed)

let private benchmarkCountersFor initial final =
  let indexedInitial = initial |> indexStreetNetwork
  let indexedFinal = final |> indexStreetNetwork
  let tripsGenerated =
    generateResidentTrips indexedInitial
    |> List.length
  let residentStreetCount =
    withDerivedStreetResidents indexedInitial
    |> allStreetViews
    |> List.filter (fun street -> street.Residents > 0.0f)
    |> List.length
  let splitCandidatesTried =
    indexedInitial.Quarters
    |> List.collect _.Blocks
    |> List.sumBy (fun block ->
      let _, stats = subdivideBlockByLandUseWithStats block
      stats.AcceptedSplits + stats.RejectedCandidates)
  { PathComputations = residentStreetCount * max 0 (residentStreetCount - 1)
    TripsGenerated = tripsGenerated
    SplitCandidatesTried = splitCandidatesTried
    ResultStreetCount = allStreetViews indexedFinal |> List.length
    ResultBlockCount =
      indexedFinal.Quarters
      |> List.sumBy (fun quarter -> quarter.Blocks.Length)
    ResultLotCount =
      indexedFinal.Quarters
      |> List.sumBy (fun quarter ->
        quarter.Blocks
        |> List.sumBy (fun block -> block.Lots.Length)) }

let benchmarkSimulationCases warmupIterations measuredIterations (cases: SimulationBenchmarkCase list) =
  cases
  |> List.map (fun benchmarkCase ->
    let summary =
      benchmarkSimulationStep warmupIterations measuredIterations (Some benchmarkCase.Seed) benchmarkCase.Initial
    let finalPlan = simulateUrbanStep benchmarkCase.Initial
    let counters = benchmarkCountersFor benchmarkCase.Initial finalPlan
    { Case = benchmarkCase
      Summary = summary
      Counters = counters
      BudgetStatus = evaluateBenchmarkBudget benchmarkCase.Budget summary })

let private corridorToRoad (organic: float32) (roadClass: RoadClass) (status: StreetStatus) (segment: TRect) =
  let (cr, cg, cb) = RoadClass.color roadClass
  let alpha, tint =
    match status with
    | Planned -> 192uy, 18
    | Built -> 255uy, 0
  let inline channel value =
    let lifted = int value + tint
    byte (min 255 lifted)
  let isVertical = segment.H >= segment.W
  if isVertical then
    let centerX = TRect.centerX segment
    let hw = segment.W / 2.0f
    { FromFunc = ""
      ToFunc = ""
      FromPos = Vector3(centerX, hw, segment.Z)
      ToPos = Vector3(centerX, hw, segment.Z + segment.H)
      Weight = RoadClass.tier roadClass
      Color = Color(channel cr, channel cg, channel cb, alpha)
      Organic = organic }
  else
    let centerZ = TRect.centerZ segment
    let hw = segment.H / 2.0f
    { FromFunc = ""
      ToFunc = ""
      FromPos = Vector3(segment.X, hw, centerZ)
      ToPos = Vector3(segment.X + segment.W, hw, centerZ)
      Weight = RoadClass.tier roadClass
      Color = Color(channel cr, channel cg, channel cb, alpha)
      Organic = organic }

// ─── Building Typology Functions ─────────────────────────────

/// Classify a function by line count, complexity, and caller heat.
/// Heat [0,1] = normalised caller count; this is the dominant axis —
/// a tiny but extremely hot function becomes a Skyscraper.
let classifyBuilding (lineCount: int) (_complexity: int) (heat: float32) : BuildingType =
  match heat, lineCount with
  | h, lc when h >= 0.85f || lc >= 600 -> Skyscraper
  | h, lc when h >= 0.65f || lc >= 300 -> Tower
  | h, lc when h >= 0.40f || lc >= 100 -> Commercial
  | h, lc when h >= 0.20f || lc >= 40  -> Rowhouse
  | h, lc when h >= 0.05f || lc >= 8   -> Cottage
  | _                                   -> Shed

module BuildingType =
  /// Yard-spacing multiplier relative to the base road segment spacing.
  /// > 1 → more yard/garden space; < 1 → denser urban packing.
  let spacingMultiplier = function
    | Shed       -> 2.2f   // sparse; open plots between tiny sheds
    | Cottage    -> 1.8f   // suburban yard feel
    | Rowhouse   -> 1.0f   // tight urban rows
    | Commercial -> 1.1f   // slight setback
    | Tower      -> 0.90f  // office-park density
    | Skyscraper -> 0.75f  // maximum packing

  /// Characteristic wall color for a building type.
  /// Uses deterministic hash-jitter for per-building variation, and shifts
  /// warm (recent commit) vs cool (old commit) based on days since last commit.
  let wallColor (bt: BuildingType) (funcName: string) (ageDays: float32) : Color =
    let br, bg, bb =
      match bt with
      | Shed       -> 158.0f, 138.0f, 108.0f   // weathered wood / tan
      | Cottage    -> 210.0f, 175.0f, 140.0f   // warm cream
      | Rowhouse   -> 163.0f, 108.0f,  82.0f   // brownstone / terracotta
      | Commercial -> 193.0f, 193.0f, 198.0f   // light stone / concrete
      | Tower      -> 152.0f, 164.0f, 175.0f   // steel / glass gray-blue
      | Skyscraper -> 120.0f, 137.0f, 152.0f   // dark reflective glass
    let ageNorm   = min 1.0f (ageDays / 365.0f)  // 0 = freshly committed, 1 = 1+ year old
    let warmShift = (1.0f - ageNorm) * 22.0f     // fresh: orange-warm boost on R channel
    let coolShift = ageNorm * 18.0f               // old: blue-gray boost on B channel
    let jR = hashJitter (funcName + "wr") * 24.0f - 12.0f + warmShift
    let jG = hashJitter (funcName + "wg") * 24.0f - 12.0f
    let jB = hashJitter (funcName + "wb") * 24.0f - 12.0f + coolShift
    let clamp v = byte (min 255.0f (max 0.0f v))
    Color(clamp (br + jR), clamp (bg + jG), clamp (bb + jB), 255uy)

  /// Roof color: residential types use warm clay tones; commercial/tower
  /// use the district color; skyscraper uses dark glass.
  let roofColor (bt: BuildingType) (districtColor: Color) : Color =
    match bt with
    | Shed       -> Color(88uy,  78uy, 62uy, 255uy)   // dark wood shingles
    | Cottage    -> Color(168uy, 78uy, 58uy, 255uy)   // terracotta tiles
    | Rowhouse   -> Color(138uy, 68uy, 52uy, 255uy)   // dark brick
    | Commercial -> districtColor                      // flat roof, district tint
    | Tower      -> districtColor                      // flat roof, district tint
    | Skyscraper -> Color(72uy,  92uy, 112uy, 255uy)  // dark reflective glass top

  /// Alpha byte encoding for GLSL building-type decode (0-5 → Shed..Skyscraper).
  /// Terrain, roads, and sidewalks keep alpha=255 (not a building type).
  let alpha (bt: BuildingType) : byte =
    match bt with
    | Shed        -> 0uy
    | Cottage     -> 1uy
    | Rowhouse    -> 2uy
    | Commercial  -> 3uy
    | Tower       -> 4uy
    | Skyscraper  -> 5uy

  /// Type-aware building height.  Heat boosts Tower/Skyscraper above their
  /// line-count baseline so the hottest functions dominate the skyline.
  let height (bt: BuildingType) (lineCount: int) (heat: float32) : float32 =
    let lc = float32 lineCount
    let h =
      match bt with
      | Shed       -> max 1.5f  (MathF.Log(lc + 1.0f) * 1.2f)
      | Cottage    -> max 2.5f  (MathF.Log(lc + 1.0f) * 2.0f)
      | Rowhouse   -> max 4.0f  (MathF.Log(lc + 1.0f) * 3.5f)
      | Commercial -> max 6.0f  (MathF.Log(lc + 1.0f) * 5.0f)
      | Tower      -> max 8.0f  (MathF.Log(lc + 1.0f) * 6.0f + heat * 10.0f)
      | Skyscraper -> max 12.0f (MathF.Log(lc + 1.0f) * 6.5f + heat * 15.0f)
    min 55.0f h

// ─── Git Metadata ────────────────────────────────────────────

type GitMeta =
  { CommitCount: int
    FirstCommitDate: DateTimeOffset
    LastCommitDate: DateTimeOffset }

module GitMeta =
  let empty =
    { CommitCount = 0
      FirstCommitDate = DateTimeOffset.Now
      LastCommitDate = DateTimeOffset.Now }

// ─── Git-Driven Organic Growth ───────────────────────────────

/// Parse `git log --follow --format="%H|%aI"` output into GitMeta.
/// Pure — pass log text from a file or process; no IO happens here.
let parseGitLog (logOutput: string) : GitMeta =
  let lines =
    logOutput.Split('\n')
    |> Array.filter (fun l -> l.Contains('|'))
  if lines.Length = 0 then GitMeta.empty
  else
    let dates =
      lines
      |> Array.choose (fun line ->
        let idx = line.IndexOf('|')
        if idx < 0 then None
        else
          match DateTimeOffset.TryParse(line.Substring(idx + 1).Trim()) with
          | true, d -> Some d
          | _ -> None)
    { CommitCount = lines.Length
      FirstCommitDate = if dates.Length > 0 then dates |> Array.min else DateTimeOffset.Now
      LastCommitDate  = if dates.Length > 0 then dates |> Array.max else DateTimeOffset.Now }

/// Organic growth factor [0..1].
/// 0 = brand-new regular-grid suburb; 1 = ancient, bustling organic neighbourhood.
/// Age saturates at 5 years; commit-activity saturates at 100 commits.
let organicFactor (ageDays: float32) (commitCount: int) : float32 =
  let ageFactor      = min 1.0f (ageDays / 1825.0f)
  let activityFactor = min 1.0f (float32 commitCount / 100.0f)
  min 1.0f (ageFactor * 0.7f + activityFactor * 0.3f)

/// Aggregate organic factor for a district from all its file-level git histories.
let districtOrganicFactor (today: DateTimeOffset) (metas: GitMeta seq) : float32 =
  let items = metas |> Seq.toList
  if items.IsEmpty then 0.0f
  else
    let avgAge     = items |> List.averageBy (fun m -> (today - m.FirstCommitDate).TotalDays |> float32)
    let avgCommits = items |> List.averageBy (fun m -> float32 m.CommitCount) |> int
    organicFactor avgAge avgCommits



type NodeId = NodeId of int
type EdgeId = EdgeId of int
type HalfEdgeId = HalfEdgeId of int
type FaceId = FaceId of int

module NodeId = let value (NodeId v) = v
module EdgeId = let value (EdgeId v) = v
module HalfEdgeId = let value (HalfEdgeId v) = v
module FaceId = let value (FaceId v) = v

type GrowthState = Unfinished | Finished

type WNode =
  { Id: NodeId; Pos: Vec2; Class: RoadClass
    Growth: GrowthState; Valence: int }

type WEdge =
  { Id: EdgeId; A: NodeId; B: NodeId
    Class: RoadClass; Width: float32 }

type WHalf =
  { Id: HalfEdgeId; Origin: NodeId; Twin: HalfEdgeId
    Next: HalfEdgeId; Prev: HalfEdgeId
    Edge: EdgeId; Face: FaceId option }

type WFace =
  { Id: FaceId; HalfEdge: HalfEdgeId; Class: RoadClass }

/// Weber city graph with half-edge data structure for face/cycle detection.
/// Paper §3: "we used a half-edge data structure... cycles can be easily found via traversal."
type WeberGraph() =
  let ns = ResizeArray<WNode>()
  let es = ResizeArray<WEdge>()
  let hs = ResizeArray<WHalf>()
  let fs = ResizeArray<WFace>()
  let outs = ResizeArray<ResizeArray<HalfEdgeId>>()

  member _.NodeCount = ns.Count
  member _.EdgeCount = es.Count
  member _.FaceCount = fs.Count
  member _.Nodes = ns :> seq<WNode>
  member _.Edges = es :> seq<WEdge>
  member _.Faces = fs :> seq<WFace>
  member _.N (NodeId i) = ns.[i]
  member _.E (EdgeId i) = es.[i]
  member _.H (HalfEdgeId i) = hs.[i]
  member _.F (FaceId i) = fs.[i]

  member _.AddNode(pos: Vec2, cls: RoadClass) =
    let id = NodeId ns.Count
    ns.Add({ Id = id; Pos = pos; Class = cls; Growth = Unfinished; Valence = 0 })
    outs.Add(ResizeArray())
    id

  member _.MarkFinished(NodeId i) =
    ns.[i] <- { ns.[i] with Growth = Finished }

  member private _.ResetTopology() =
    es.Clear()
    hs.Clear()
    fs.Clear()
    outs.Clear()
    for i in 0 .. ns.Count - 1 do
      let node = ns.[i]
      ns.[i] <- { node with Valence = 0 }
      outs.Add(ResizeArray())

  member private this.RebuildTopology(edgeSpecs: (NodeId * NodeId * RoadClass * float32) list) =
    this.ResetTopology()
    for (a, b, cls, width) in edgeSpecs do
      this.AddEdge(a, b, cls, width) |> ignore

  member this.SplitEdge(edgeId: EdgeId, point: Vec2) =
    let edge = es.[EdgeId.value edgeId]
    let startPos = ns.[NodeId.value edge.A].Pos
    let endPos = ns.[NodeId.value edge.B].Pos
    let endpointSnapSq = 0.05f * 0.05f
    if Vec2.distanceToSq point startPos <= endpointSnapSq then edge.A
    elif Vec2.distanceToSq point endPos <= endpointSnapSq then edge.B
    else
      let splitNode = this.AddNode(point, edge.Class)
      let retainedEdges =
        es
        |> Seq.filter (fun existing -> existing.Id <> edgeId)
        |> Seq.map (fun existing -> existing.A, existing.B, existing.Class, existing.Width)
        |> Seq.toList
      this.RebuildTopology (
        retainedEdges
        @ [ edge.A, splitNode, edge.Class, edge.Width
            splitNode, edge.B, edge.Class, edge.Width ])
      splitNode

  member _.OutgoingDirs(nid: NodeId) : Vec2 list =
    let n = ns.[NodeId.value nid]
    [ for heId in outs.[NodeId.value nid] do
        let he = hs.[HalfEdgeId.value heId]
        let tgt = ns.[NodeId.value (hs.[HalfEdgeId.value he.Twin].Origin)]
        Vec2.normalize (Vec2.sub tgt.Pos n.Pos) ]

  /// Angle from node a to node b
  member _.Angle (NodeId a) (NodeId b) =
    let pa = ns.[a].Pos
    let pb = ns.[b].Pos
    atan2 (pb.Y - pa.Y) (pb.X - pa.X)

  /// Find outgoing half-edge at node just BEFORE angle in CCW order.
  member this.FindPrevCCW (nid: NodeId) (angle: float32) : HalfEdgeId option =
    let outList = outs.[NodeId.value nid]
    if outList.Count = 0 then None
    else
      let mutable bestIdx = 0
      let mutable bestDiff = System.Single.MaxValue
      for i in 0 .. outList.Count - 1 do
        let he = hs.[HalfEdgeId.value outList.[i]]
        let a = this.Angle he.Origin (hs.[HalfEdgeId.value he.Twin].Origin)
        // Signed angular diff: how far CCW from 'a' to our target angle
        let mutable diff = angle - a
        if diff < 0.0f then diff <- diff + 2.0f * MathF.PI
        if diff >= 2.0f * MathF.PI then diff <- diff - 2.0f * MathF.PI
        // We want the outgoing HE with the SMALLEST positive CCW gap to our angle
        // but going BACKWARDS (i.e., the one just before us).
        // That's the one with the LARGEST CCW gap from itself to our angle
        // Or equivalently: the one that is closest CW from our angle
        let cwDiff = 2.0f * MathF.PI - diff
        if cwDiff < bestDiff || (cwDiff < bestDiff + 0.001f && i < bestIdx) then
          bestDiff <- cwDiff
          bestIdx <- i
      Some outList.[bestIdx]

  /// Insert half-edge into CCW-sorted outgoing list at node
  member this.InsertOutgoing (nid: NodeId) (heId: HalfEdgeId) =
    let outList = outs.[NodeId.value nid]
    let he = hs.[HalfEdgeId.value heId]
    let angle = this.Angle he.Origin (hs.[HalfEdgeId.value he.Twin].Origin)
    let mutable pos = outList.Count
    for i in 0 .. outList.Count - 1 do
      let eHe = hs.[HalfEdgeId.value outList.[i]]
      let eAngle = this.Angle eHe.Origin (hs.[HalfEdgeId.value eHe.Twin].Origin)
      if pos = outList.Count && angle < eAngle then
        pos <- i
    outList.Insert(pos, heId)

  /// Core operation: add edge between two nodes, link half-edges, detect faces.
  /// Returns list of newly detected face IDs.
  member this.AddEdge(a: NodeId, b: NodeId, cls: RoadClass, width: float32) : FaceId list =
    let eid = EdgeId es.Count
    es.Add({ Id = eid; A = a; B = b; Class = cls; Width = width })

    let heAB = HalfEdgeId hs.Count
    let heBA = HalfEdgeId (hs.Count + 1)

    // Create with placeholder next/prev — will be linked below
    hs.Add({ Id = heAB; Origin = a; Twin = heBA; Next = heBA; Prev = heBA; Edge = eid; Face = None })
    hs.Add({ Id = heBA; Origin = b; Twin = heAB; Next = heAB; Prev = heAB; Edge = eid; Face = None })

    // Link at node A
    let ai = HalfEdgeId.value heAB
    let bi = HalfEdgeId.value heBA
    match this.FindPrevCCW a (this.Angle a b) with
    | None ->
      // First edge at A
      hs.[bi] <- { hs.[bi] with Next = heAB }
      hs.[ai] <- { hs.[ai] with Prev = heBA }
    | Some prevOutId ->
      let prevInId = hs.[HalfEdgeId.value prevOutId].Twin
      let prevInIdx = HalfEdgeId.value prevInId
      let oldNext = hs.[prevInIdx].Next
      let oldNextIdx = HalfEdgeId.value oldNext
      hs.[prevInIdx] <- { hs.[prevInIdx] with Next = heAB }
      hs.[ai] <- { hs.[ai] with Prev = prevInId }
      hs.[bi] <- { hs.[bi] with Next = oldNext }
      hs.[oldNextIdx] <- { hs.[oldNextIdx] with Prev = heBA }

    this.InsertOutgoing a heAB

    // Link at node B
    match this.FindPrevCCW b (this.Angle b a) with
    | None ->
      hs.[ai] <- { hs.[ai] with Next = heBA }
      hs.[bi] <- { hs.[bi] with Prev = heAB }
    | Some prevOutId ->
      let prevInId = hs.[HalfEdgeId.value prevOutId].Twin
      let prevInIdx = HalfEdgeId.value prevInId
      let oldNext = hs.[prevInIdx].Next
      let oldNextIdx = HalfEdgeId.value oldNext
      hs.[prevInIdx] <- { hs.[prevInIdx] with Next = heBA }
      hs.[bi] <- { hs.[bi] with Prev = prevInId }
      hs.[ai] <- { hs.[ai] with Next = oldNext }
      hs.[oldNextIdx] <- { hs.[oldNextIdx] with Prev = heAB }

    this.InsertOutgoing b heBA

    // Update valences
    let na = ns.[NodeId.value a]
    ns.[NodeId.value a] <- { na with Valence = na.Valence + 1 }
    let nb = ns.[NodeId.value b]
    ns.[NodeId.value b] <- { nb with Valence = nb.Valence + 1 }

    // Detect new faces by traversing from both new half-edges
    let mutable newFaces = []
    for startHe in [heAB; heBA] do
      let mutable cur = hs.[HalfEdgeId.value startHe].Next
      let mutable count = 1
      let mutable allUnassigned = true
      while cur <> startHe && count < hs.Count do
        if hs.[HalfEdgeId.value cur].Face.IsSome then allUnassigned <- false
        cur <- hs.[HalfEdgeId.value cur].Next
        count <- count + 1
      if cur = startHe && count >= 3 && count < hs.Count / 2 + 10 then
        // Compute signed area to distinguish interior from exterior
        let mutable area = 0.0f
        let mutable c = startHe
        let mutable first = true
        while first || c <> startHe do
          first <- false
          let h = hs.[HalfEdgeId.value c]
          let p = ns.[NodeId.value h.Origin].Pos
          let nh = hs.[HalfEdgeId.value h.Next]
          let q = ns.[NodeId.value nh.Origin].Pos
          area <- area + (p.X * q.Y - q.X * p.Y)
          c <- h.Next
        area <- area * 0.5f
        if area > 0.5f then // positive = CCW = interior face
          let fid = FaceId fs.Count
          fs.Add({ Id = fid; HalfEdge = startHe; Class = cls })
          // Assign face to all half-edges in cycle
          let mutable c2 = startHe
          let mutable f2 = true
          while f2 || c2 <> startHe do
            f2 <- false
            hs.[HalfEdgeId.value c2] <- { hs.[HalfEdgeId.value c2] with Face = Some fid }
            c2 <- hs.[HalfEdgeId.value c2].Next
          newFaces <- fid :: newFaces
    newFaces

  /// Compute polygon vertices of a face by walking half-edges (on demand, not stored)
  member this.FacePolygon(fid: FaceId) : Vec2 list =
    let f = fs.[FaceId.value fid]
    let start = f.HalfEdge
    let mutable result = []
    let mutable cur = start
    let mutable first = true
    while first || cur <> start do
      first <- false
      result <- ns.[NodeId.value (hs.[HalfEdgeId.value cur].Origin)].Pos :: result
      cur <- hs.[HalfEdgeId.value cur].Next
    List.rev result

  member this.FaceArea(fid: FaceId) : float32 =
    let poly = this.FacePolygon(fid)
    let mutable area = 0.0f
    for i in 0 .. poly.Length - 1 do
      let p = poly.[i]
      let q = poly.[(i + 1) % poly.Length]
      area <- area + (p.X * q.Y - q.X * p.Y)
    abs (area * 0.5f)

// ─── F# Function Extractor ───────────────────────────────────

let excludeDirs =
  set [
    // Build outputs & tooling
    "bin"; "obj"; "runtimes"; "nupkg"; "packages"; "vendor"
    // JS/Node
    "node_modules"; "dist"; "build"; "out"
    // CI / test artifacts
    "coverage-report"; "test-results"; "BenchmarkDotNet.Artifacts"; "playwright-report"
    // VCS & IDE
    ".git"; ".github"; ".vs"; ".idea"; "__pycache__"
    // Rust/Java/other language build dirs
    "target"
  ]

let private fcsChecker = FSharpChecker.Create(keepAssemblyContents = true)
let private pathEquals (left: string) (right: string) =
  String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase)

let private moduleMetadata root filePath =
  let rel = Path.GetRelativePath(root, filePath).Replace('\\', '/')
  let parts = rel.Split('/')
  let project = if parts.Length > 1 then parts.[0] else "root"
  let defaultModuleName = Path.GetFileNameWithoutExtension(filePath)
  rel, project, defaultModuleName

type ProjectContext =
  { ProjectFile: string
    Root: string
    SourceFiles: string list
    CompilerArgs: string[] }

let private tryGetJsonProperty (name: string) (element: System.Text.Json.JsonElement) =
  let mutable value = Unchecked.defaultof<System.Text.Json.JsonElement>
  if element.TryGetProperty(name, &value) then Some value else None

let private normalizeLangVersion (value: string) =
  let trimmed = value.Trim()
  match Int32.TryParse(trimmed) with
  | true, version when version >= 11 -> "preview"
  | _ -> trimmed

let private normalizeCompilerReferencePath (path: string) =
  match OperatingSystem.IsWindows(), path.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase) with
  | true, true ->
      let candidate = @"C:\PROGRA~1" + path.Substring(@"C:\Program Files".Length)
      match File.Exists(candidate) with
      | true -> candidate
      | false -> path
  | _ -> path

let private tryLoadProjectContext projectFile =
  try
    let normalizedProjectFile = Path.GetFullPath(projectFile)
    let root = Path.GetDirectoryName(normalizedProjectFile)
    let psi =
      Diagnostics.ProcessStartInfo(
        "dotnet",
        sprintf "msbuild \"%s\" /t:ResolveReferences \"-getItem:Compile;ReferencePath\" \"-getProperty:OutputType;DefineConstants;LangVersion;IntermediateOutputPath;TargetFrameworkMoniker\"" normalizedProjectFile)
    psi.WorkingDirectory <- root
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    use proc = Diagnostics.Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    match proc.ExitCode with
    | 0 ->
        let doc = System.Text.Json.JsonDocument.Parse(stdout)
        let items =
          tryGetJsonProperty "Items" doc.RootElement
          |> Option.defaultValue doc.RootElement
        let properties =
          tryGetJsonProperty "Properties" doc.RootElement
          |> Option.defaultValue doc.RootElement

        let compileFiles =
          match tryGetJsonProperty "Compile" items with
          | Some compileItems ->
              compileItems.EnumerateArray()
              |> Seq.choose (tryGetJsonProperty "FullPath")
              |> Seq.choose (fun item -> item.GetString() |> Option.ofObj)
              |> Seq.map Path.GetFullPath
              |> Seq.toList
          | None -> []

        let referencePaths =
          match tryGetJsonProperty "ReferencePath" items with
          | Some referenceItems ->
              referenceItems.EnumerateArray()
              |> Seq.choose (tryGetJsonProperty "FullPath")
              |> Seq.choose (fun item -> item.GetString() |> Option.ofObj)
              |> Seq.map Path.GetFullPath
              |> Seq.distinct
              |> Seq.toList
          | None -> []

        let propertyValue name =
          properties
          |> tryGetJsonProperty name
          |> Option.bind (fun value -> value.GetString() |> Option.ofObj)
          |> Option.defaultValue ""

        let compilerArgs = ResizeArray<string>()
        let intermediateOutputPath = propertyValue "IntermediateOutputPath"
        let targetFrameworkMoniker = propertyValue "TargetFrameworkMoniker"
        let generatedSourceFiles =
          [
            Path.Combine(root, intermediateOutputPath, Path.GetFileNameWithoutExtension(normalizedProjectFile) + ".AssemblyInfo.fs")
            Path.Combine(root, intermediateOutputPath, targetFrameworkMoniker + ".AssemblyAttributes.fs")
          ]
          |> List.filter File.Exists
        compilerArgs.Add "--simpleresolution"
        compilerArgs.Add "--noframework"
        compilerArgs.Add "--nocopyfsharpcore"
        compilerArgs.Add "--targetprofile:netcore"
        compilerArgs.Add(
          match propertyValue "OutputType" with
          | value when value.Equals("Exe", StringComparison.OrdinalIgnoreCase) -> "--target:exe"
          | _ -> "--target:library")

        match propertyValue "LangVersion" with
        | value when String.IsNullOrWhiteSpace(value) -> ()
        | value -> compilerArgs.Add("--langversion:" + normalizeLangVersion value)

        match propertyValue "DefineConstants" with
        | value when String.IsNullOrWhiteSpace(value) -> ()
        | value ->
            value.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.iter (fun define -> compilerArgs.Add("--define:" + define))

        referencePaths
        |> List.iter (fun referencePath -> compilerArgs.Add("-r:" + normalizeCompilerReferencePath referencePath))

        compileFiles
        |> List.iter compilerArgs.Add

        Some
          { ProjectFile = normalizedProjectFile
            Root = root
            SourceFiles = compileFiles @ generatedSourceFiles
            CompilerArgs = compilerArgs.ToArray() }
    | _ ->
        let _ = stderr
        None
  with _ -> None

let private parsingOptionsFor filePath =
  let projectOptions =
    fcsChecker.GetProjectOptionsFromCommandLineArgs(
      filePath,
      [|
        "--targetprofile:netcore"
        "--noframework"
        "--langversion:preview"
        "--target:library"
        filePath
      |])
  fcsChecker.GetParsingOptionsFromProjectOptions(projectOptions) |> fst

let private identText (ident: Ident) = ident.idText

let private longIdentText (idents: Ident list) =
  idents
  |> List.map identText
  |> String.concat "."

let private synLongIdentText (synLongIdent: SynLongIdent) =
  synLongIdent.LongIdent
  |> Seq.toList
  |> longIdentText

let private modulePathText (idents: Ident list) =
  match idents |> longIdentText with
  | "" -> ""
  | text -> text

let private qualifyPath prefix suffix =
  match prefix, suffix with
  | "", value -> value
  | value, "" -> value
  | prefix, value when value.StartsWith(prefix + ".", StringComparison.Ordinal) -> value
  | prefix, value -> sprintf "%s.%s" prefix value

let private sliceLines (lines: string[]) (startLine: int) (endLine: int) =
  let startIdx = max 0 (startLine - 1)
  let endIdx = min (lines.Length - 1) (endLine - 1)
  match startIdx <= endIdx && startIdx < lines.Length with
  | true -> lines.[startIdx .. endIdx]
  | false -> [||]

let rec private tryBindingName pat =
  match pat with
  | SynPat.LongIdent(longDotId = synLongIdent) ->
      synLongIdent.LongIdent
      |> Seq.tryLast
      |> Option.map identText
  | SynPat.Named(SynIdent(ident, _), _, _, _) -> Some (identText ident)
  | SynPat.Paren(inner, _) -> tryBindingName inner
  | SynPat.Typed(inner, _, _) -> tryBindingName inner
  | SynPat.Attrib(inner, _, _) -> tryBindingName inner
  | _ -> None

let rec private bindingDeclarationRange pat =
  match pat with
  | SynPat.LongIdent(longDotId = synLongIdent) ->
      synLongIdent.LongIdent
      |> Seq.tryLast
      |> Option.map (fun ident -> ident.idRange)
  | SynPat.Named(SynIdent(ident, _), _, _, _) -> Some ident.idRange
  | SynPat.Paren(inner, _) -> bindingDeclarationRange inner
  | SynPat.Typed(inner, _, _) -> bindingDeclarationRange inner
  | SynPat.Attrib(inner, _, _) -> bindingDeclarationRange inner
  | _ -> None

let private mkCallSite refText namePath (siteRange: range) =
  { RefText = refText
    NamePath = namePath
    StartLine = siteRange.StartLine
    StartColumn = siteRange.StartColumn
    EndColumn = siteRange.EndColumn }

let rec private tryOperatorName expr =
  match expr with
  | SynExpr.Ident ident -> Some (identText ident)
  | SynExpr.LongIdent(_, synLongIdent, _, _) ->
      synLongIdent.LongIdent
      |> Seq.tryLast
      |> Option.map identText
  | SynExpr.Paren(inner, _, _, _) -> tryOperatorName inner
  | SynExpr.Typed(inner, _, _) -> tryOperatorName inner
  | _ -> None

let private isForwardPipeOperator name =
  match name with
  | "op_PipeRight"
  | "op_PipeRight2"
  | "op_PipeRight3"
  | "|>"
  | "||>"
  | "|||>" -> true
  | _ -> false

let private isBackwardPipeOperator name =
  match name with
  | "op_PipeLeft"
  | "op_PipeLeft2"
  | "op_PipeLeft3"
  | "<|"
  | "<||"
  | "<|||" -> true
  | _ -> false

let rec private stripTrivialExprLayers expr =
  match expr with
  | SynExpr.Paren(inner, _, _, _) -> stripTrivialExprLayers inner
  | SynExpr.Typed(inner, _, _) -> stripTrivialExprLayers inner
  | _ -> expr

let rec private isFunctionBindingPattern pat =
  match pat with
  | SynPat.LongIdent(argPats = argPats) -> not argPats.Patterns.IsEmpty
  | SynPat.Paren(inner, _) -> isFunctionBindingPattern inner
  | SynPat.Typed(inner, _, _) -> isFunctionBindingPattern inner
  | SynPat.Attrib(inner, _, _) -> isFunctionBindingPattern inner
  | _ -> false

let rec private directCallSites expr =
  match expr with
  | SynExpr.Ident ident ->
      [ mkCallSite (identText ident) [ identText ident ] ident.idRange ]
  | SynExpr.LongIdent(_, synLongIdent, _, _) ->
      [ mkCallSite
          (synLongIdentText synLongIdent)
          (synLongIdent.LongIdent |> Seq.toList |> List.map identText)
          synLongIdent.Range ]
  | SynExpr.Paren(inner, _, _, _) -> directCallSites inner
  | SynExpr.Typed(inner, _, _) -> directCallSites inner
  | _ -> []

/// Collect only calls that execute in the current evaluation context.
let rec private collectCallSites expr =
  match expr with
  | SynExpr.App(_, _, funcExpr, argExpr, _) ->
      match stripTrivialExprLayers funcExpr with
      | SynExpr.App(_, _, operatorExpr, leftExpr, _) ->
          match tryOperatorName operatorExpr with
          | Some operatorName when isForwardPipeOperator operatorName ->
              directCallSites argExpr @ collectCallSites leftExpr @ collectCallSites argExpr
          | Some operatorName when isBackwardPipeOperator operatorName ->
              directCallSites leftExpr @ collectCallSites leftExpr @ collectCallSites argExpr
          | _ ->
              directCallSites funcExpr @ collectCallSites funcExpr @ collectCallSites argExpr
      | SynExpr.Lambda(_, _, _, body, _, _, _) ->
          collectCallSites body @ collectCallSites argExpr
      | _ ->
          directCallSites funcExpr @ collectCallSites funcExpr @ collectCallSites argExpr
  | SynExpr.LetOrUse(_, _, _, _, bindings, body, _, _) ->
      let bindingCalls =
        bindings
        |> Seq.toList
        |> List.collect (fun (SynBinding(headPat = headPat; expr = expr)) ->
          match isFunctionBindingPattern headPat with
          | true -> []
          | false -> collectCallSites expr)
      bindingCalls @ collectCallSites body
  | SynExpr.Sequential(_, _, first, second, _, _) ->
      collectCallSites first @ collectCallSites second
  | SynExpr.Paren(inner, _, _, _) -> collectCallSites inner
  | SynExpr.Typed(inner, _, _) -> collectCallSites inner
  | SynExpr.Lambda _ -> []
  | SynExpr.Match(_, inputExpr, clauses, _, _) ->
      let clauseCalls =
        clauses
        |> Seq.toList
        |> List.collect (fun (SynMatchClause(whenExpr = whenExpr; resultExpr = resultExpr)) ->
          let guardCalls =
            whenExpr
            |> Option.map collectCallSites
            |> Option.defaultValue []
          guardCalls @ collectCallSites resultExpr)
      collectCallSites inputExpr @ clauseCalls
  | SynExpr.IfThenElse(condition, thenExpr, elseExpr, _, _, _, _) ->
      collectCallSites condition
      @ collectCallSites thenExpr
      @ (elseExpr |> Option.map collectCallSites |> Option.defaultValue [])
  | _ -> []

let private tryExtractFuncDef rel filePath project lines containerPath binding =
  let (SynBinding(headPat = headPat; expr = expr)) = binding
  match tryBindingName headPat with
  | Some name ->
      let declarationRange =
        bindingDeclarationRange headPat
        |> Option.defaultValue binding.RangeOfBindingWithRhs
      let qualifiedName = qualifyPath containerPath name
      let bodyLines =
        sliceLines lines binding.RangeOfBindingWithRhs.StartLine binding.RangeOfBindingWithRhs.EndLine
      let callSites = collectCallSites expr
      Some
        { Name = name
          QualifiedName = qualifiedName
          FilePath = filePath
          RelPath = rel
          Module = containerPath
          Project = project
          DeclarationStartLine = declarationRange.StartLine
          DeclarationStartColumn = declarationRange.StartColumn
          StartLine = binding.RangeOfBindingWithRhs.StartLine
          EndLine = binding.RangeOfBindingWithRhs.EndLine
          LineCount = bodyLines.Length
          Body = bodyLines
          CallRefs = callSites |> List.map (fun site -> site.RefText)
          CallSites = callSites }
  | None -> None

let private isSupportedExplicitMemberBinding binding =
  let (SynBinding(headPat = headPat; valData = SynValData(memberFlags = memberFlags))) = binding
  match memberFlags with
  | Some flags when
      flags.MemberKind.IsPropertyGet
      || flags.MemberKind.IsPropertySet
      || flags.MemberKind.IsPropertyGetSet
      || flags.MemberKind.IsConstructor
      || flags.MemberKind.IsClassConstructor -> false
  | _ -> isFunctionBindingPattern headPat

let rec private extractTypeMemberDecls rel filePath project lines typePath memberDecls =
  [
    for memberDecl in memberDecls do
      match memberDecl with
      | SynMemberDefn.Member(binding, _) ->
          match isSupportedExplicitMemberBinding binding, tryExtractFuncDef rel filePath project lines typePath binding with
          | true, Some funcDef -> yield funcDef
          | _ -> ()
      | SynMemberDefn.NestedType(typeDefn, _, _) ->
          yield! extractTypeDefn rel filePath project lines typePath typeDefn
      | SynMemberDefn.Interface(_, _, members, _) ->
          match members with
          | Some nestedMembers -> yield! extractTypeMemberDecls rel filePath project lines typePath (nestedMembers |> Seq.toList)
          | None -> ()
      | _ -> ()
  ]

and private extractTypeDefn rel filePath project lines modulePath typeDefn =
  let (SynTypeDefn(typeInfo = typeInfo; typeRepr = typeRepr; members = members)) = typeDefn
  let (SynComponentInfo(longId = longId)) = typeInfo
  let typeName =
    longId
    |> Seq.toList
    |> modulePathText
  let typePath = qualifyPath modulePath typeName
  let objectModelMembers =
    match typeRepr with
    | SynTypeDefnRepr.ObjectModel(_, memberDecls, _) -> memberDecls |> Seq.toList
    | _ -> []
  let allMemberDecls =
    objectModelMembers @ (members |> Seq.toList)
    |> List.distinctBy (fun memberDecl ->
      let range = memberDecl.Range
      range.StartLine, range.StartColumn, range.EndLine, range.EndColumn)
  extractTypeMemberDecls rel filePath project lines typePath allMemberDecls

let rec private extractModuleDecls rel filePath project defaultModuleName lines modulePath decls =
  [
    for decl in decls do
      match decl with
      | SynModuleDecl.Let(_, bindings, _) ->
          let containerPath =
            match modulePath with
            | "" -> defaultModuleName
            | value -> value
          for binding in bindings do
            match tryExtractFuncDef rel filePath project lines containerPath binding with
            | Some funcDef -> yield funcDef
            | None -> ()
      | SynModuleDecl.Types(typeDefns, _) ->
          let containerPath =
            match modulePath with
            | "" -> defaultModuleName
            | value -> value
          for typeDefn in typeDefns do
            yield! extractTypeDefn rel filePath project lines containerPath typeDefn
      | SynModuleDecl.NestedModule(componentInfo, _, nestedDecls, _, _, _) ->
          let (SynComponentInfo(longId = longId)) = componentInfo
          let nestedName =
            longId
            |> Seq.toList
            |> modulePathText
          let nextModulePath =
            qualifyPath modulePath nestedName
          yield! extractModuleDecls rel filePath project defaultModuleName lines nextModulePath nestedDecls
       | _ -> ()
    ]

let private extractFunctionsFromParseTree root filePath lines parsedInput =
  let rel, project, defaultModuleName = moduleMetadata root filePath
  match parsedInput with
  | ParsedInput.ImplFile implFile ->
      implFile.Contents
      |> Seq.toList
      |> List.collect (fun moduleOrNamespace ->
        let (SynModuleOrNamespace(longId = longId; decls = decls)) = moduleOrNamespace
        let modulePath =
          longId
          |> Seq.toList
          |> modulePathText
        extractModuleDecls rel filePath project defaultModuleName lines modulePath decls)
  | _ -> []

let extractFunctions (root: string) (filePath: string) : FuncDef list =
  try
    let lines = File.ReadAllLines(filePath)
    let source = String.Join(Environment.NewLine, lines)
    let parsingOptions = parsingOptionsFor filePath
    let parseResults =
      fcsChecker.ParseFile(filePath, SourceText.ofString source, parsingOptions)
      |> Async.RunSynchronously
    extractFunctionsFromParseTree root filePath lines parseResults.ParseTree
  with _ -> []

/// Scan all .fs files and extract functions
let scanFunctions (root: string) : FuncDef list =
  let rec walk (dir: string) =
    seq {
      let dirName = Path.GetFileName(dir)
      if not (excludeDirs.Contains(dirName)) then
        for f in Directory.EnumerateFiles(dir, "*.fs") do
          yield! extractFunctions root f
        for sub in Directory.EnumerateDirectories(dir) do
         yield! walk sub
     }
  walk root |> Seq.toList

let scanFunctionsForProject (projectFile: string) : FuncDef list =
  match tryLoadProjectContext projectFile with
  | Some projectContext ->
      projectContext.SourceFiles
      |> List.collect (extractFunctions projectContext.Root)
  | None ->
      scanFunctions (Path.GetDirectoryName(Path.GetFullPath(projectFile)))

/// Run `git log --follow --format="%H|%aI"` for one file and parse the output.
let getGitMetaForFile (repoRoot: string) (filePath: string) : GitMeta =
  try
    let relPath = IO.Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/')
    let psi = Diagnostics.ProcessStartInfo("git", sprintf "log --follow --format=\"%%H|%%aI\" -- \"%s\"" relPath)
    psi.WorkingDirectory <- repoRoot
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use proc = Diagnostics.Process.Start(psi)
    let output = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    parseGitLog output
  with _ -> GitMeta.empty

/// Collect git metadata keyed by absolute file path for all distinct source files.
let scanGitMeta (repoRoot: string) (funcs: FuncDef list) : Map<string, GitMeta> =
  funcs
  |> List.map (fun f -> f.FilePath)
  |> List.distinct
  |> List.filter (fun p -> p <> "")
  |> List.map (fun path -> path, getGitMetaForFile repoRoot path)
  |> Map.ofList


// ─── Call Graph Builder ───────────────────────────────────────

/// Common F# identifiers that create false call-graph edges
let commonNames =
  set [
    // F# keywords/stdlib that look like function calls
    "name"; "value"; "key"; "item"; "result"; "error"; "state"; "acc"
    "msg"; "args"; "opts"; "config"; "ctx"; "env"; "cmd"; "evt"
    "input"; "output"; "data"; "info"; "text"; "line"; "path"; "file"
    "list"; "map"; "set"; "seq"; "array"; "option"; "some"; "none"
    "true"; "false"; "null"; "unit"; "string"; "int"; "float"; "bool"
    "async"; "task"; "action"; "func"; "handler"; "callback"
    "head"; "tail"; "fst"; "snd"; "not"; "max"; "min"; "abs"
    "init"; "iter"; "fold"; "filter"; "choose"; "collect"
    "tryFind"; "tryHead"; "tryPick"; "exists"; "forall"
    "length"; "count"; "isEmpty"; "contains"; "append"
    "create"; "make"; "build"; "parse"; "format"; "render"
    "get"; "set"; "add"; "remove"; "update"; "delete"; "clear"
    "read"; "write"; "send"; "recv"; "open"; "close"; "start"; "stop"
    "log"; "debug"; "warn"; "trace"; "check"; "test"; "run"
    "with"; "yield"; "return"; "match"; "when"; "then"; "else"; "done"
    "from"; "into"; "down"; "next"; "prev"; "left"; "right"
    "color"; "width"; "height"; "size"; "rect"; "pos"; "vec"
    "encode"; "decode"; "serialize"; "deserialize"
    "toList"; "toArray"; "toSeq"; "ofList"; "ofArray"; "ofSeq"
    "toString"; "ignore"; "failwith"; "raise"; "sprintf"; "printfn"
  ]

/// Build call graph from AST-derived call references.
let buildCallGraph (funcs: FuncDef list) : CallEdge list =
  let funcsByName =
    funcs
    |> List.groupBy (fun f -> f.Name)
    |> Map.ofList

  let resolveUnqualifiedCall (caller: FuncDef) (name: string) : FuncDef option =
    match Map.tryFind name funcsByName with
    | None -> None
    | Some (candidates: FuncDef list) ->
      let sameModule =
        candidates
        |> List.filter (fun (candidate: FuncDef) -> candidate.Module = caller.Module)
      match sameModule with
      | [target] when target.QualifiedName <> caller.QualifiedName -> Some target
      | [_] -> None
      | _ ->
        match candidates with
        | [target] when target.QualifiedName <> caller.QualifiedName -> Some target
        | _ -> None

  let resolveQualifiedCall (caller: FuncDef) (callRef: string) : FuncDef option =
    match funcs |> List.tryFind (fun f -> f.QualifiedName = callRef) with
    | Some target when target.QualifiedName <> caller.QualifiedName -> Some target
    | Some _ -> None
    | None ->
      let suffixMatches =
        funcs
        |> List.filter (fun f ->
          f.QualifiedName <> caller.QualifiedName
          && f.QualifiedName.EndsWith("." + callRef, StringComparison.Ordinal))
      let preferred =
        suffixMatches
        |> List.filter (fun f ->
          f.Module = caller.Module
          || f.Module.StartsWith(caller.Module + ".", StringComparison.Ordinal))
      match preferred with
      | [target] -> Some target
      | _ ->
        match suffixMatches with
        | [target] -> Some target
        | _ -> None

  [
    for caller in funcs do
      let callCounts =
        caller.CallRefs
        |> List.countBy id

      for (callRef, weight) in callCounts do
        if callRef.Contains(".") then
          match resolveQualifiedCall caller callRef with
          | Some callee ->
              { From = caller.QualifiedName
                To = callee.QualifiedName
                Weight = weight }
          | None -> ()
        else
          match resolveUnqualifiedCall caller callRef with
          | Some callee ->
              { From = caller.QualifiedName
                To = callee.QualifiedName
                Weight = weight }
            | None -> ()
  ]

let private rangeContainedInFunction (funcDef: FuncDef) (symbolRange: range) =
  pathEquals funcDef.FilePath symbolRange.FileName
  && symbolRange.StartLine >= funcDef.StartLine
  && symbolRange.EndLine <= funcDef.EndLine

let private tryFindFunctionByDeclaration (funcs: FuncDef list) (symbol: FSharpSymbol) =
  match symbol with
  | :? FSharpMemberOrFunctionOrValue as memberOrFunction ->
      let fullNameMatch =
        match memberOrFunction.FullName with
        | null | "" -> None
        | fullName ->
            funcs
            |> List.tryFind (fun funcDef -> funcDef.QualifiedName = fullName)
      let tryByDeclarationLocation () =
        let declarationRange = memberOrFunction.DeclarationLocation
        let exactMatch =
          funcs
          |> List.tryFind (fun funcDef ->
            pathEquals funcDef.FilePath declarationRange.FileName
            && funcDef.DeclarationStartLine = declarationRange.StartLine
            && funcDef.DeclarationStartColumn = declarationRange.StartColumn
            && funcDef.Name = memberOrFunction.DisplayName)
        match exactMatch with
        | Some funcDef -> Some funcDef
        | None ->
            funcs
            |> List.tryFind (fun funcDef ->
              pathEquals funcDef.FilePath declarationRange.FileName
              && declarationRange.StartLine >= funcDef.StartLine
              && declarationRange.StartLine <= funcDef.EndLine
              && funcDef.Name = memberOrFunction.DisplayName)
      match fullNameMatch with
      | Some funcDef -> Some funcDef
      | None ->
          try
            tryByDeclarationLocation ()
          with :? InvalidOperationException ->
            None
  | _ -> None

let private tryResolveCallSite
  (checkResults: FSharpCheckFileResults)
  (lineText: string)
  (callSite: CallSite)
  =
  let tryPrimaryLookup column =
    checkResults.GetSymbolUseAtLocation(callSite.StartLine, column, lineText, callSite.NamePath)

  let tryFallbackLookup column =
    checkResults.GetSymbolUsesAtLocation(callSite.StartLine, column, lineText, callSite.NamePath)
    |> List.tryFind (fun symbolUse -> symbolUse.IsFromUse && not symbolUse.IsFromDefinition)

  [ callSite.EndColumn
    max callSite.StartColumn (callSite.EndColumn - 1) ]
  |> List.distinct
  |> List.tryPick (fun column ->
    match tryPrimaryLookup column with
    | Some symbolUse when symbolUse.IsFromUse && not symbolUse.IsFromDefinition -> Some symbolUse.Symbol
    | _ -> tryFallbackLookup column |> Option.map (fun symbolUse -> symbolUse.Symbol))

let buildSemanticCallGraphForProject (projectFile: string) (funcs: FuncDef list) : CallEdge list =
  match tryLoadProjectContext projectFile with
  | Some projectContext ->
      let semanticChecker = FSharpChecker.Create(keepAssemblyContents = true)
      let sourceFiles = projectContext.SourceFiles |> List.map Path.GetFullPath
      let sourceFileSet = sourceFiles |> Set.ofList
      let otherOptions =
        projectContext.CompilerArgs
        |> Array.filter (fun arg ->
          match Path.IsPathRooted(arg) with
          | true -> not (sourceFileSet.Contains(Path.GetFullPath(arg)))
          | false -> true)
      let baseProjectOptions =
        semanticChecker.GetProjectOptionsFromCommandLineArgs(projectContext.ProjectFile, otherOptions)
      let projectOptions =
        { baseProjectOptions with
            SourceFiles = sourceFiles |> List.toArray }
      semanticChecker.ParseAndCheckProject(projectOptions) |> Async.RunSynchronously |> ignore
      let checkResultsByPath =
        projectContext.SourceFiles
        |> List.choose (fun filePath ->
          let fullPath = Path.GetFullPath(filePath)
          let sourceText = File.ReadAllText(fullPath)
          let _, checkResults =
            semanticChecker.GetBackgroundCheckResultsForFileInProject(fullPath, projectOptions, sourceText)
            |> Async.RunSynchronously
          match checkResults.Diagnostics |> Array.exists (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error) with
          | true -> None
          | false -> Some (fullPath, checkResults))
        |> Map.ofList
      let fileLinesByPath =
        projectContext.SourceFiles
        |> List.map (fun filePath -> Path.GetFullPath(filePath), File.ReadAllLines(filePath))
        |> Map.ofList

      [
        for caller in funcs do
          match Map.tryFind (Path.GetFullPath(caller.FilePath)) checkResultsByPath, Map.tryFind (Path.GetFullPath(caller.FilePath)) fileLinesByPath with
          | Some checkResults, Some fileLines ->
              for callSite in caller.CallSites do
                let lineIndex = callSite.StartLine - 1
                if lineIndex >= 0 && lineIndex < fileLines.Length then
                  let lineText = fileLines.[lineIndex]
                  match tryResolveCallSite checkResults lineText callSite |> Option.bind (tryFindFunctionByDeclaration funcs) with
                  | Some callee when caller.QualifiedName <> callee.QualifiedName ->
                      { From = caller.QualifiedName
                        To = callee.QualifiedName
                        Weight = 1 }
                  | _ -> ()
          | _ -> ()
      ]
  | None ->
      buildCallGraph funcs

/// Deduplicate edges and sum weights
let deduplicateEdges (edges: CallEdge list) : CallEdge list =
  edges
  |> List.groupBy (fun e -> e.From, e.To)
  |> List.map (fun ((f, t), es) ->
    { From = f; To = t; Weight = es |> List.sumBy (fun e -> e.Weight) })

/// Alias for deduplicateEdges (used by buildCity)
let mergeCallEdges = deduplicateEdges

// ─── Heat Metrics ─────────────────────────────────────────────

/// Compute heat (0..1) for each function based on how many callers it has
let computeHeat (funcs: FuncDef list) (edges: CallEdge list) : Map<string, float32 * int * int> =
  let callerCounts =
    edges
    |> List.groupBy (fun e -> e.To)
    |> List.map (fun (target, es) ->
      target, es |> List.map (fun e -> e.From) |> List.distinct |> List.length)
    |> Map.ofList

  let calleeCounts =
    edges
    |> List.groupBy (fun e -> e.From)
    |> List.map (fun (source, es) ->
      source, es |> List.map (fun e -> e.To) |> List.distinct |> List.length)
    |> Map.ofList

  let maxCallers =
    let vals = callerCounts |> Map.values |> Seq.toList
    match vals with
    | [] -> 1.0f
    | xs -> float32 (max 1 (List.max xs))

  funcs
  |> List.map (fun f ->
    let callers = callerCounts |> Map.tryFind f.QualifiedName |> Option.defaultValue 0
    let callees = calleeCounts |> Map.tryFind f.QualifiedName |> Option.defaultValue 0
    let heat = float32 callers / maxCallers
    f.QualifiedName, (heat, callers, callees))
  |> Map.ofList

// ─── Cyclomatic Complexity ────────────────────────────────────

/// McCabe cyclomatic complexity from F# function body lines.
/// Baseline 1 + branching constructs: if, elif, match cases (|),
/// &&, ||, for, while, try/with
let computeComplexity (body: string[]) : int =
  let mutable count = 1
  for line in body do
    let t = line.TrimStart()
    if t.StartsWith("if ") || t.StartsWith("elif ") then
      count <- count + 1
    if t.StartsWith("| ") && not (t.StartsWith("||")) then
      count <- count + 1
    let mutable idx = 0
    while idx < line.Length - 1 do
      if line.[idx] = '&' && line.[idx+1] = '&' then
        count <- count + 1
        idx <- idx + 2
      elif line.[idx] = '|' && line.[idx+1] = '|' then
        count <- count + 1
        idx <- idx + 2
      else
        idx <- idx + 1
    if t.StartsWith("for ") || t.StartsWith("while ") then
      count <- count + 1
    if t.StartsWith("try") && (t.Length = 3 || not (Char.IsLetterOrDigit(t.[3]))) then
      count <- count + 1
  count

// ─── Catmull-Rom Spline ──────────────────────────────────────

let splineSegments = 3  // subdivisions per road segment (most roads are straight now)

/// Catmull-Rom spline interpolation between p1 and p2.
/// p0, p3 are outer control points that influence curvature.
let catmullRom (p0x: float32, p0z: float32) (p1x: float32, p1z: float32)
               (p2x: float32, p2z: float32) (p3x: float32, p3z: float32)
               (t: float32) : float32 * float32 =
  let t2 = t * t
  let t3 = t2 * t
  let x =
    0.5f * (2.0f * p1x
            + (-p0x + p2x) * t
            + (2.0f * p0x - 5.0f * p1x + 4.0f * p2x - p3x) * t2
            + (-p0x + 3.0f * p1x - 3.0f * p2x + p3x) * t3)
  let z =
    0.5f * (2.0f * p1z
            + (-p0z + p2z) * t
            + (2.0f * p0z - 5.0f * p1z + 4.0f * p2z - p3z) * t2
            + (-p0z + 3.0f * p1z - 3.0f * p2z + p3z) * t3)
  (x, z)

/// Subdivide spline into N evenly-spaced parameter samples.
let subdivideSpline p0 p1 p2 p3 (segments: int) : (float32 * float32) list =
  [ for i in 0 .. segments do
      let t = float32 i / float32 segments
      catmullRom p0 p1 p2 p3 t ]
// ─── Weber Street Expansion Algorithm (Paper §3) ────────────

let alignToMajorAxis (origin: Vec2) (proposed: Vec2) : Vec2 =
  let delta = Vec2.sub proposed origin
  if abs delta.X >= abs delta.Y then Vec2.Create(proposed.X, origin.Y)
  else Vec2.Create(origin.X, proposed.Y)

let private normalizeAngleTau angle =
  let tau = 2.0f * MathF.PI
  let normalized = angle % tau
  if normalized < 0.0f then normalized + tau else normalized

let private directionAngle (dir: Vec2) =
  normalizeAngleTau (MathF.Atan2(dir.Y, dir.X))

let private directionFromAngle angle =
  Vec2.Create(MathF.Cos(angle), MathF.Sin(angle))

let private directionDeltaDegrees (a: Vec2) (b: Vec2) =
  let dot =
    Vec2.dot (Vec2.normalize a) (Vec2.normalize b)
    |> max -1.0f
    |> min 1.0f
  MathF.Acos(dot) * 180.0f / MathF.PI

let private signedDirectionTurnRadians (fromDir: Vec2) (toDir: Vec2) =
  let a = Vec2.normalize fromDir
  let b = Vec2.normalize toDir
  let cross = a.X * b.Y - a.Y * b.X
  let dot = Vec2.dot a b |> max -1.0f |> min 1.0f
  MathF.Atan2(cross, dot)

let private tryGetNodeDriftBias (driftBiasByNode: System.Collections.Generic.Dictionary<int, float32>) (nid: NodeId) =
  match driftBiasByNode.TryGetValue(NodeId.value nid) with
  | true, bias -> bias
  | _ -> 0.0f

let private tryGetNodeLengthFactor (lengthFactorByNode: System.Collections.Generic.Dictionary<int, float32>) (nid: NodeId) =
  match lengthFactorByNode.TryGetValue(NodeId.value nid) with
  | true, factor when factor > 0.0f -> factor
  | _ -> 1.0f

let private tryGetNodeBranchDistance (branchDistanceByNode: System.Collections.Generic.Dictionary<int, float32>) (nid: NodeId) =
  match branchDistanceByNode.TryGetValue(NodeId.value nid) with
  | true, distance when distance >= 0.0f -> distance
  | _ -> 0.0f

let private tryGetNodeBranchTargetFactor (branchTargetFactorByNode: System.Collections.Generic.Dictionary<int, float32>) (nid: NodeId) =
  match branchTargetFactorByNode.TryGetValue(NodeId.value nid) with
  | true, factor when factor > 0.0f -> factor
  | _ -> 1.0f

let private clampOrganicLengthFactor factor =
  factor
  |> max 0.78f
  |> min 1.24f

let private clampOrganicBranchTargetFactor factor =
  factor
  |> max 0.62f
  |> min 1.85f

let private sampleOrganicContinuationLength previousFactor (rng: Random) =
  let seeded = clampOrganicLengthFactor (0.84f + rng.NextSingle() * 0.32f)
  if abs (previousFactor - 1.0f) < 0.025f then
    seeded
  else
    let carried = clampOrganicLengthFactor (previousFactor + (rng.NextSingle() - 0.5f) * 0.16f)
    if rng.NextSingle() < 0.76f then carried else seeded

let private sampleOrganicContinuationTurn previousBias (rng: Random) =
  let previousSign =
    if previousBias > 0.04f then 1.0f
    elif previousBias < -0.04f then -1.0f
    else 0.0f
  let turnSign =
    if previousSign = 0.0f then
      if rng.NextSingle() < 0.5f then -1.0f else 1.0f
    elif rng.NextSingle() < 0.78f then previousSign
    else -previousSign
  let magnitude = (10.0f + rng.NextSingle() * 8.0f) * MathF.PI / 180.0f
  let jitter = (rng.NextSingle() - 0.5f) * 0.08f
  turnSign * magnitude + jitter

let private initializeOrganicBranchDistances
  (g: WeberGraph)
  (branchDistanceByNode: System.Collections.Generic.Dictionary<int, float32>)
  (branchTargetFactorByNode: System.Collections.Generic.Dictionary<int, float32>)
  (segmentLength: float32)
  (rng: Random)
  =
  g.Nodes
  |> Seq.iter (fun n ->
      if n.Growth = Unfinished && not (branchDistanceByNode.ContainsKey(NodeId.value n.Id)) then
        let seeded =
          segmentLength * (0.55f + rng.NextSingle() * 0.85f)
        branchDistanceByNode.[NodeId.value n.Id] <- seeded)
  g.Nodes
  |> Seq.iter (fun n ->
      if n.Growth = Unfinished && not (branchTargetFactorByNode.ContainsKey(NodeId.value n.Id)) then
        let seeded =
          clampOrganicBranchTargetFactor (0.68f + rng.NextSingle() * 0.95f)
        branchTargetFactorByNode.[NodeId.value n.Id] <- seeded)

let private sampleOrganicBranchTargetFactor previousFactor (rng: Random) =
  let seeded = clampOrganicBranchTargetFactor (0.68f + rng.NextSingle() * 0.95f)
  if abs (previousFactor - 1.0f) < 0.05f then
    seeded
  else
    let carried = clampOrganicBranchTargetFactor (previousFactor + (rng.NextSingle() - 0.5f) * 0.48f)
    match rng.NextSingle() < 0.62f with
    | true -> carried
    | false -> seeded

let private straightCorridorBias (dirs: Vec2 list) =
  let hasOpposingPair threshold =
    dirs
    |> List.exists (fun dirA ->
        dirs
        |> List.exists (fun dirB ->
            not (obj.ReferenceEquals(dirA, dirB))
            && directionDeltaDegrees dirA (Vec2.negate dirB) <= threshold))
  match dirs.Length with
  | 2 when hasOpposingPair 18.0f -> 1.18f
  | 3 when hasOpposingPair 24.0f -> 1.12f
  | _ -> 1.0f

let private sampleOrganicNode
  (g: WeberGraph)
  (centers: Vec2[])
  (focus: float32)
  (rng: Random)
  (minTier: int)
  (segmentLength: float32)
  (branchDistanceByNode: System.Collections.Generic.Dictionary<int, float32>)
  (branchTargetFactorByNode: System.Collections.Generic.Dictionary<int, float32>)
  : NodeId option =
  let candidates = ResizeArray<NodeId>()
  let weights = ResizeArray<float32>()
  for n in g.Nodes do
    if n.Growth = Unfinished && n.Valence < 4 && RoadClass.tier n.Class >= minTier then
      let minDistSq =
        centers |> Array.map (fun c -> Vec2.distanceToSq n.Pos c) |> Array.min
      let baseWeight = exp(-float (focus * minDistSq)) |> float32
      let spacingBias =
        match n.Valence with
        | 0 -> 1.0f
        | 1 -> 1.08f
        | 2
        | 3 ->
            let branchDistance = tryGetNodeBranchDistance branchDistanceByNode n.Id
            let targetDistance =
              segmentLength * tryGetNodeBranchTargetFactor branchTargetFactorByNode n.Id
              |> max 1.0f
            let readiness =
              branchDistance / targetDistance
              |> max 0.0f
              |> min 2.0f
            let spacingBias =
              if readiness < 0.92f then
                0.02f + readiness * readiness * 0.08f
              else
                min 2.4f (0.45f + (readiness - 0.92f) * 2.8f)
            spacingBias * straightCorridorBias (g.OutgoingDirs n.Id)
        | _ -> 0.0f
      let weight = baseWeight * spacingBias
      if weight > 0.0f then
        candidates.Add(n.Id)
        weights.Add(weight)
  if candidates.Count = 0 then None
  else
    let total = weights |> Seq.sum
    let mutable r = rng.NextSingle() * total
    let mutable i = 0
    while i < weights.Count - 1 && r > weights.[i] do
      r <- r - weights.[i]
      i <- i + 1
    Some candidates.[i]

let private gapDirectedExpansion (dirs: Vec2 list) (rng: Random) =
  let angles = dirs |> List.map directionAngle |> List.sort
  let gaps =
    [ for i in 0 .. angles.Length - 1 do
        let startAngle = angles.[i]
        let endAngle =
          if i < angles.Length - 1 then angles.[i + 1]
          else angles.[0] + 2.0f * MathF.PI
        startAngle, endAngle - startAngle ]
    |> List.sortByDescending snd
  match gaps with
  | [] -> None
  | (startAngle, gapSize) :: remaining ->
      let nearMaxThreshold = gapSize * 0.9f
      let candidates =
        (startAngle, gapSize)
        :: (remaining |> List.takeWhile (fun (_, candidateGap) -> candidateGap >= nearMaxThreshold))
      let chosenStart, chosenGap = candidates.[rng.Next(List.length candidates)]
      let skew = 0.32f + rng.NextSingle() * 0.36f
      let jitter = (rng.NextSingle() - 0.5f) * min 0.18f (chosenGap * 0.12f)
      directionFromAngle (chosenStart + chosenGap * skew + jitter)
      |> Vec2.normalize
      |> Some

let clipProposalToBounds (rect: TRect) (origin: Vec2) (proposed: Vec2) : Vec2 =
  let clampX value = max rect.X (min (rect.X + rect.W) value)
  let clampZ value = max rect.Z (min (rect.Z + rect.H) value)
  if abs (proposed.X - origin.X) < 0.001f then
    Vec2.Create(clampX origin.X, clampZ proposed.Y)
  elif abs (proposed.Y - origin.Y) < 0.001f then
    Vec2.Create(clampX proposed.X, clampZ origin.Y)
  else
    Vec2.Create(clampX proposed.X, clampZ proposed.Y)

let private rectContainsVec2 (rect: TRect) (pt: Vec2) =
  pt.X >= rect.X - 0.001f
  && pt.X <= rect.X + rect.W + 0.001f
  && pt.Y >= rect.Z - 0.001f
  && pt.Y <= rect.Z + rect.H + 0.001f

let private rectBoundaryMidpoints (rect: TRect) =
  let left = rect.X
  let right = rect.X + rect.W
  let top = rect.Z
  let bottom = rect.Z + rect.H
  [|
    Vec2.Create(rect.X + rect.W / 2.0f, top)
    Vec2.Create(right, rect.Z + rect.H / 2.0f)
    Vec2.Create(rect.X + rect.W / 2.0f, bottom)
    Vec2.Create(left, rect.Z + rect.H / 2.0f)
  |]

let private isBoundaryPoint (rect: TRect) (pt: Vec2) =
  let left = abs (pt.X - rect.X) < 0.01f
  let right = abs (pt.X - (rect.X + rect.W)) < 0.01f
  let top = abs (pt.Y - rect.Z) < 0.01f
  let bottom = abs (pt.Y - (rect.Z + rect.H)) < 0.01f
  left || right || top || bottom

let private lerpVec2 (a: Vec2) (b: Vec2) t =
  Vec2.Create(
    a.X + (b.X - a.X) * t,
    a.Y + (b.Y - a.Y) * t)

let private clampVec2ToRect (rect: TRect) (pt: Vec2) =
  Vec2.Create(
    max rect.X (min (rect.X + rect.W) pt.X),
    max rect.Z (min (rect.Z + rect.H) pt.Y))

let private lineRectIntersections (rect: TRect) (origin: Vec2) (dir: Vec2) =
  let eps = 0.001f
  let duplicateToleranceSq = 0.05f * 0.05f
  let intersections = ResizeArray<float32 * Vec2>()
  let addCandidate t =
    let pt = clampVec2ToRect rect (Vec2.add origin (Vec2.scale t dir))
    if rectContainsVec2 rect pt then
      let alreadySeen =
        intersections
        |> Seq.exists (fun (_, existing) -> Vec2.distanceToSq existing pt <= duplicateToleranceSq)
      if not alreadySeen then
        intersections.Add(t, pt)
  if abs dir.X > eps then
    addCandidate ((rect.X - origin.X) / dir.X)
    addCandidate (((rect.X + rect.W) - origin.X) / dir.X)
  if abs dir.Y > eps then
    addCandidate ((rect.Z - origin.Y) / dir.Y)
    addCandidate (((rect.Z + rect.H) - origin.Y) / dir.Y)
  intersections
  |> Seq.sortBy fst
  |> Seq.map snd
  |> Seq.toList

let private edgeToCorridorRect (g: WeberGraph) (edge: WEdge) =
  let a = (g.N edge.A).Pos
  let b = (g.N edge.B).Pos
  let dx = b.X - a.X
  let dz = b.Y - a.Y
  if abs dx >= abs dz then
    let x = min a.X b.X
    let z = min a.Y b.Y - edge.Width / 2.0f
    TRect.create x z (abs dx) edge.Width
  else
    let x = min a.X b.X - edge.Width / 2.0f
    let z = min a.Y b.Y
    TRect.create x z edge.Width (abs dz)

let private faceToQuarterRect (rect: TRect) (poly: Vec2 list) =
  if poly.Length < 3 then None
  else
    let xs = poly |> List.map (fun pt -> pt.X)
    let zs = poly |> List.map (fun pt -> pt.Y)
    let minX = xs |> List.min
    let maxX = xs |> List.max
    let minZ = zs |> List.min
    let maxZ = zs |> List.max
    let quarter = TRect.create minX minZ (maxX - minX) (maxZ - minZ)
    let validVertices = poly |> List.forall (rectContainsVec2 rect)
    if validVertices && quarter.W > 1.5f && quarter.H > 1.5f then Some quarter else None

let private quarterRectKey (rect: TRect) =
  let round1 value = MathF.Round(value, 1, MidpointRounding.AwayFromZero)
  round1 rect.X, round1 rect.Z, round1 rect.W, round1 rect.H

let private seedGridMajorStreetGrowthGraph (rect: TRect) =
  let g = WeberGraph()
  let left = rect.X
  let right = rect.X + rect.W
  let top = rect.Z
  let bottom = rect.Z + rect.H
  let center = Vec2.Create(TRect.centerX rect, TRect.centerZ rect)
  let northMid, eastMid, southMid, westMid =
    let mids = rectBoundaryMidpoints rect
    mids.[0], mids.[1], mids.[2], mids.[3]

  let topLeft = g.AddNode(Vec2.Create(left, top), Avenue)
  let topMid = g.AddNode(northMid, Avenue)
  let topRight = g.AddNode(Vec2.Create(right, top), Avenue)
  let rightMid = g.AddNode(eastMid, Avenue)
  let bottomRight = g.AddNode(Vec2.Create(right, bottom), Avenue)
  let bottomMid = g.AddNode(southMid, Avenue)
  let bottomLeft = g.AddNode(Vec2.Create(left, bottom), Avenue)
  let leftMid = g.AddNode(westMid, Avenue)

  let ringNodes = [ topLeft; topMid; topRight; rightMid; bottomRight; bottomMid; bottomLeft; leftMid ]
  ringNodes
  |> List.pairwise
  |> List.iter (fun (a, b) -> g.AddEdge(a, b, Avenue, RoadClass.width Avenue) |> ignore)
  g.AddEdge(leftMid, topLeft, Avenue, RoadClass.width Avenue) |> ignore

  let centerId = g.AddNode(center, Avenue)
  [ northMid; eastMid; southMid; westMid ]
  |> List.iter (fun boundary ->
    let armPos =
      Vec2.Create(
        center.X + (boundary.X - center.X) * 0.55f,
        center.Y + (boundary.Y - center.Y) * 0.55f)
    let armId = g.AddNode(armPos, Avenue)
    g.AddEdge(centerId, armId, Avenue, RoadClass.width Avenue) |> ignore
    let boundaryId =
      [ topMid; rightMid; bottomMid; leftMid ]
      |> List.find (fun nid ->
        let pos = (g.N nid).Pos
        abs (pos.X - boundary.X) < 0.01f && abs (pos.Y - boundary.Y) < 0.01f)
    g.AddEdge(armId, boundaryId, Avenue, RoadClass.width Avenue) |> ignore)

  [ topLeft; topMid; topRight; rightMid; bottomRight; bottomMid; bottomLeft; leftMid ]
  |> List.iter g.MarkFinished

  g, [| center |]

let private seedOrganicMajorStreetGrowthGraph (rect: TRect) (rng: Random) =
  let g = WeberGraph()
  let center = Vec2.Create(TRect.centerX rect, TRect.centerZ rect)
  let interiorRect = TRect.inset (min rect.W rect.H * 0.12f) rect
  let xSign = if rng.NextSingle() < 0.5f then -1.0f else 1.0f
  let ySign = if rng.NextSingle() < 0.5f then -1.0f else 1.0f
  let anchor =
    Vec2.Create(
      center.X + rect.W * (0.10f + rng.NextSingle() * 0.10f) * xSign,
      center.Y + rect.H * (0.08f + rng.NextSingle() * 0.10f) * ySign)
    |> clampVec2ToRect interiorRect
  let baseAngle = (18.0f + rng.NextSingle() * 54.0f) * MathF.PI / 180.0f
  let angle =
    match rng.Next(4) with
    | 0 -> baseAngle
    | 1 -> MathF.PI - baseAngle
    | 2 -> -baseAngle
    | _ -> baseAngle - MathF.PI
  let dir = Vec2.Create(MathF.Cos(angle), MathF.Sin(angle)) |> Vec2.normalize
  let perp = Vec2.Create(-dir.Y, dir.X)
  match lineRectIntersections rect anchor dir with
  | portalStart :: remaining ->
      let portalEnd = remaining |> List.tryLast |> Option.defaultValue portalStart
      let span = Vec2.distanceTo portalStart portalEnd
      if span < 6.0f then
        seedGridMajorStreetGrowthGraph rect
      else
        let spineStartPos = lerpVec2 portalStart portalEnd (0.26f + rng.NextSingle() * 0.08f)
        let spineEndPos = lerpVec2 portalStart portalEnd (0.62f + rng.NextSingle() * 0.10f)
        let flankDepth = min rect.W rect.H * (0.10f + rng.NextSingle() * 0.05f)
        let flankLead = span * (0.06f + rng.NextSingle() * 0.04f)
        let flankA =
          Vec2.add spineStartPos (Vec2.add (Vec2.scale flankLead dir) (Vec2.scale flankDepth perp))
          |> clampVec2ToRect interiorRect
        let flankB =
          Vec2.add spineEndPos (Vec2.add (Vec2.scale -flankLead dir) (Vec2.scale (-flankDepth * 0.85f) perp))
          |> clampVec2ToRect interiorRect
        let portalStartId = g.AddNode(portalStart, Avenue)
        let spineStartId = g.AddNode(spineStartPos, Avenue)
        let flankAId = g.AddNode(flankA, Avenue)
        let spineEndId = g.AddNode(spineEndPos, Avenue)
        let portalEndId = g.AddNode(portalEnd, Avenue)
        let flankBId = g.AddNode(flankB, Avenue)
        g.AddEdge(portalStartId, spineStartId, Avenue, RoadClass.width Avenue) |> ignore
        g.AddEdge(spineStartId, flankAId, Avenue, RoadClass.width Avenue) |> ignore
        g.AddEdge(flankAId, spineEndId, Avenue, RoadClass.width Avenue) |> ignore
        g.AddEdge(spineEndId, portalEndId, Avenue, RoadClass.width Avenue) |> ignore
        g.AddEdge(spineEndId, flankBId, Avenue, RoadClass.width Avenue) |> ignore
        g.AddEdge(flankBId, spineStartId, Avenue, RoadClass.width Avenue) |> ignore
        g.MarkFinished(portalStartId)
        g.MarkFinished(portalEndId)
        g, [| spineStartPos; spineEndPos; flankA; flankB |]
  | _ ->
      seedGridMajorStreetGrowthGraph rect

/// Line segment intersection. Returns intersection point and parameter t along (a→b).
let segIntersect (a: Vec2) (b: Vec2) (c: Vec2) (d: Vec2) : (Vec2 * float32) option =
  let dx = b.X - a.X
  let dy = b.Y - a.Y
  let ex = d.X - c.X
  let ey = d.Y - c.Y
  let denom = dx * ey - dy * ex
  if abs denom < 1e-8f then None
  else
    let t = ((c.X - a.X) * ey - (c.Y - a.Y) * ex) / denom
    let u = ((c.X - a.X) * dy - (c.Y - a.Y) * dx) / denom
    if t > 0.01f && t < 0.99f && u > 0.01f && u < 0.99f then
      Some (Vec2.Create(a.X + t * dx, a.Y + t * dy), t)
    else None

/// Test 1 — Intersection: shorten segment to first intersection with existing edges.
type IntersectionHit =
  | NoIntersection
  | HitNode of NodeId
  | HitEdgeInterior of EdgeId * Vec2 * float32

/// Test 1 — Intersection: classify the first hit against the existing planar graph.
let findClosestIntersection (g: WeberGraph) (origin: Vec2) (proposed: Vec2) : IntersectionHit =
  let mutable bestHit = NoIntersection
  let mutable bestT = 1.0f
  let endpointSnapSq = 0.05f * 0.05f
  for e in g.Edges do
    let ea = (g.N e.A).Pos
    let eb = (g.N e.B).Pos
    match segIntersect origin proposed ea eb with
    | Some (pt, t) when t < bestT ->
        let edgeDelta = Vec2.sub eb ea
        let edgeLenSq = Vec2.lengthSq edgeDelta
        let edgeT =
          if edgeLenSq < 1e-6f then 0.0f
          else Vec2.dot (Vec2.sub pt ea) edgeDelta / edgeLenSq
        bestT <- t
        bestHit <-
          if Vec2.distanceToSq pt ea <= endpointSnapSq then HitNode e.A
          elif Vec2.distanceToSq pt eb <= endpointSnapSq then HitNode e.B
          else HitEdgeInterior (e.Id, pt, edgeT)
    | _ -> ()
  bestHit

let adaptIntersection (g: WeberGraph) (origin: Vec2) (proposed: Vec2) : Vec2 =
  match findClosestIntersection g origin proposed with
  | NoIntersection -> proposed
  | HitNode nodeId -> (g.N nodeId).Pos
  | HitEdgeInterior (_, pt, _) -> pt

/// Test 2 — Enlargement: extend by 1.5× to detect near-miss intersections.
let adaptEnlargement (g: WeberGraph) (origin: Vec2) (proposed: Vec2) : Vec2 =
  let dir = Vec2.sub proposed origin
  let extended = Vec2.add proposed (Vec2.scale 0.5f dir)
  let mutable found = None
  let mutable bestDist = System.Single.MaxValue
  for e in g.Edges do
    let ea = (g.N e.A).Pos
    let eb = (g.N e.B).Pos
    match segIntersect proposed extended ea eb with
    | Some (pt, _) ->
      let d = Vec2.distanceToSq origin pt
      if d < bestDist then
        bestDist <- d
        found <- Some pt
    | None -> ()
  match found with Some pt -> pt | None -> proposed

/// Test 3 — Snapping: snap endpoint to nearest existing node within snap_r.
/// THIS is how T-junctions and crossroads are created.
let adaptSnapping (g: WeberGraph) (proposed: Vec2) (snapR: float32) (exclude: NodeId) : NodeId option =
  let mutable bestId = None
  let mutable bestDist = snapR * snapR
  for n in g.Nodes do
    if n.Id <> exclude then
      let d = Vec2.distanceToSq proposed n.Pos
      if d < bestDist then
        bestDist <- d
        bestId <- Some n.Id
  bestId

let private chooseOrganicSnapTarget (g: WeberGraph) (originId: NodeId) (origin: Vec2) (proposed: Vec2) (snapR: float32) : NodeId option =
  let proposedDelta = Vec2.sub proposed origin
  if Vec2.lengthSq proposedDelta < 1e-6f then
    None
  else
    let proposedDir = Vec2.normalize proposedDelta
    let existingDirs = g.OutgoingDirs(originId)
    let headingThreshold = 35.0f
    let duplicateAngleThreshold = 18.0f
    let shortArmThreshold = max 2.0f (Vec2.length proposedDelta * 0.55f)
    g.Nodes
    |> Seq.choose (fun n ->
      if n.Id = originId then
        None
      else
        let delta = Vec2.sub n.Pos origin
        let distSq = Vec2.lengthSq delta
        if distSq >= snapR * snapR || distSq < 1e-6f then
          None
        else
          let snapDir = Vec2.normalize delta
          let headingDelta = directionDeltaDegrees proposedDir snapDir
          let duplicateShortArm =
            distSq <= shortArmThreshold * shortArmThreshold
            && existingDirs |> List.exists (fun existingDir -> directionDeltaDegrees snapDir existingDir <= duplicateAngleThreshold)
          if headingDelta > headingThreshold || duplicateShortArm then
            None
          else
            let dist = sqrt distSq
            let score = dist + headingDelta * (snapR / 60.0f)
            Some (score, n.Id))
    |> Seq.sortBy fst
    |> Seq.tryHead
    |> Option.map snd

/// Sample an unfinished node weighted by distance to growth centers.
/// P(node[i]) ∝ exp(-f · ‖pos - center‖²)  (Paper §3.1)
let sampleNode (g: WeberGraph) (centers: Vec2[]) (focus: float32) (rng: Random) (minTier: int) : NodeId option =
  let candidates = ResizeArray<NodeId>()
  let weights = ResizeArray<float32>()
  for n in g.Nodes do
    if n.Growth = Unfinished && n.Valence < 4 && RoadClass.tier n.Class >= minTier then
      let minDistSq =
        centers |> Array.map (fun c -> Vec2.distanceToSq n.Pos c) |> Array.min
      let w = exp(-float (focus * minDistSq)) |> float32
      candidates.Add(n.Id)
      weights.Add(w)
  if candidates.Count = 0 then None
  else
    let total = weights |> Seq.sum
    let mutable r = rng.NextSingle() * total
    let mutable i = 0
    while i < weights.Count - 1 && r > weights.[i] do
      r <- r - weights.[i]
      i <- i + 1
    Some candidates.[i]

/// Expand from a node: generate proposed segment endpoint.
/// Direction by valence (Paper §3.1, Figure 5), pattern = grid or organic.
let expandNode
  (g: WeberGraph)
  (driftBiasByNode: System.Collections.Generic.Dictionary<int, float32>)
  (lengthFactorByNode: System.Collections.Generic.Dictionary<int, float32>)
  (nid: NodeId)
  (rng: Random)
  (isGrid: bool)
  (length: float32)
  : (Vec2 * float32 * float32) option =
  let n = g.N nid
  let dirs = g.OutgoingDirs(nid)
  match n.Valence with
  | 0 ->
    let angle = rng.NextSingle() * MathF.PI * 2.0f
    let lengthFactor =
      if isGrid then 1.0f
      else sampleOrganicContinuationLength 1.0f rng
    Some (Vec2.add n.Pos (Vec2.scale (length * lengthFactor) (Vec2.Create(MathF.Cos(angle), MathF.Sin(angle)))), 0.0f, lengthFactor)
  | 1 ->
    // Valence 1: continue forward with a persistent organic drift rather than ruler-straight extensions.
    let opposite = Vec2.negate dirs.[0]
    let lengthFactor =
      if isGrid then 1.0f
      else sampleOrganicContinuationLength (tryGetNodeLengthFactor lengthFactorByNode nid) rng
    let dev =
      if isGrid then 0.0f
      else sampleOrganicContinuationTurn (tryGetNodeDriftBias driftBiasByNode nid) rng
    let cos = MathF.Cos(dev)
    let sin = MathF.Sin(dev)
    let rotated = Vec2.Create(opposite.X * cos - opposite.Y * sin, opposite.X * sin + opposite.Y * cos)
    Some (Vec2.add n.Pos (Vec2.scale (length * lengthFactor) rotated), dev, lengthFactor)
  | 2 ->
    // Valence 2: turn left or right
    let dot = dirs |> List.pairwise |> List.tryHead |> Option.map (fun (a, b) -> a.X * b.X + a.Y * b.Y) |> Option.defaultValue 0.0f
    let referenceDir =
      if dot < -0.85f then dirs.[0]
      else Vec2.negate (Vec2.normalize (Vec2.add dirs.[0] dirs.[1]))
    let lengthFactor =
      if isGrid then 1.0f
      else sampleOrganicContinuationLength (tryGetNodeLengthFactor lengthFactorByNode nid) rng
    let nextDir =
      if isGrid then
        let baseDir = referenceDir
        let perp =
          if rng.NextSingle() < 0.5f then Vec2.Create(-baseDir.Y, baseDir.X)
          else Vec2.Create(baseDir.Y, -baseDir.X)
        let dev = 0.0f
        let cos = MathF.Cos(dev)
        let sin = MathF.Sin(dev)
        Vec2.Create(perp.X * cos - perp.Y * sin, perp.X * sin + perp.Y * cos)
      else
        match gapDirectedExpansion dirs rng with
        | Some dir -> dir
        | None ->
            let baseDir = referenceDir
            let perp =
              if rng.NextSingle() < 0.5f then Vec2.Create(-baseDir.Y, baseDir.X)
              else Vec2.Create(baseDir.Y, -baseDir.X)
            let dev = (rng.NextSingle() - 0.5f) * 0.3f
            let cos = MathF.Cos(dev)
            let sin = MathF.Sin(dev)
            Vec2.Create(perp.X * cos - perp.Y * sin, perp.X * sin + perp.Y * cos)
    let driftBias =
      if isGrid then 0.0f
      else signedDirectionTurnRadians referenceDir nextDir
    Some (Vec2.add n.Pos (Vec2.scale (length * lengthFactor) nextDir), driftBias, lengthFactor)
  | 3 ->
    // Valence 3: find largest angular gap, bisect it
    let lengthFactor =
      if isGrid then 1.0f
      else sampleOrganicContinuationLength (tryGetNodeLengthFactor lengthFactorByNode nid) rng
    let dir =
      if isGrid then
        let angles = dirs |> List.map (fun d -> atan2 d.Y d.X) |> List.sort
        let gaps =
          [ for i in 0 .. angles.Length - 1 do
              let a1 = angles.[i]
              let a2 = if i < angles.Length - 1 then angles.[i + 1] else angles.[0] + 2.0f * MathF.PI
              (a2 - a1, a1 + (a2 - a1) / 2.0f) ]
        let (_, bisect) = gaps |> List.maxBy fst
        Vec2.Create(MathF.Cos(bisect), MathF.Sin(bisect))
      else
        gapDirectedExpansion dirs rng
        |> Option.defaultValue (Vec2.negate (Vec2.normalize (dirs |> List.reduce Vec2.add)))
    Some (Vec2.add n.Pos (Vec2.scale (length * lengthFactor) dir), tryGetNodeDriftBias driftBiasByNode nid, lengthFactor)
  | _ -> None // valence >= 4: finished

/// The main Weber street growth loop (Paper §3, Table 2).
/// Same algorithm for major and minor streets — only parameters differ.
let growStreets
  (g: WeberGraph) (centers: Vec2[]) (rng: Random)
  (cls: RoadClass) (isGrid: bool)
  (length: float32) (snapR: float32) (sMin: float32)
  (maxEdges: int) (focus: float32) (bounds: TRect option) =
  let minTier = RoadClass.tier cls
  let driftBiasByNode = System.Collections.Generic.Dictionary<int, float32>()
  let lengthFactorByNode = System.Collections.Generic.Dictionary<int, float32>()
  let branchDistanceByNode = System.Collections.Generic.Dictionary<int, float32>()
  let branchTargetFactorByNode = System.Collections.Generic.Dictionary<int, float32>()
  if not isGrid then
    initializeOrganicBranchDistances g branchDistanceByNode branchTargetFactorByNode length rng
  let mutable added = 0
  let mutable stuck = 0
  while added < maxEdges && stuck < 80 do
    let candidate =
      if isGrid then sampleNode g centers focus rng minTier
      else sampleOrganicNode g centers focus rng minTier length branchDistanceByNode branchTargetFactorByNode
    match candidate with
    | None -> stuck <- stuck + 1
    | Some nid ->
      let priorValence = (g.N nid).Valence
      let parentBranchDistance =
        if isGrid then 0.0f else tryGetNodeBranchDistance branchDistanceByNode nid
      let parentBranchTargetFactor =
        if isGrid then 1.0f else tryGetNodeBranchTargetFactor branchTargetFactorByNode nid
      let isContinuation = priorValence <= 1
      match expandNode g driftBiasByNode lengthFactorByNode nid rng isGrid length with
      | None ->
          g.MarkFinished(nid)
          stuck <- stuck + 1
      | Some (proposed0, driftBias, lengthFactor) ->
          let origin = (g.N(nid).Pos)
          let proposed1 =
            if isGrid then alignToMajorAxis (g.N(nid).Pos) proposed0
            else proposed0
          let proposed =
            match bounds with
            | Some rect -> clipProposalToBounds rect (g.N(nid).Pos) proposed1
            | None -> proposed1
          // Apply legality tests in sequence (Paper §3.1, Figure 6), but make crossings topologically real.
          match findClosestIntersection g origin proposed with
          | HitNode existingId ->
              let segLen = Vec2.distanceTo origin (g.N existingId).Pos
              let branchDistance =
                if isGrid then 0.0f
                elif isContinuation then parentBranchDistance + segLen
                else 0.0f
              let branchTargetFactor =
                if isGrid then 1.0f
                elif isContinuation then parentBranchTargetFactor
                else sampleOrganicBranchTargetFactor parentBranchTargetFactor rng
              if segLen < sMin then
                g.MarkFinished(nid)
                stuck <- stuck + 1
              else
                if not isGrid then
                  branchDistanceByNode.[NodeId.value nid] <- branchDistance
                  branchTargetFactorByNode.[NodeId.value nid] <- branchTargetFactor
                  branchDistanceByNode.[NodeId.value existingId] <- 0.0f
                  branchTargetFactorByNode.[NodeId.value existingId] <-
                    sampleOrganicBranchTargetFactor branchTargetFactor rng
                g.AddEdge(nid, existingId, cls, RoadClass.width cls) |> ignore
                added <- added + 1
                stuck <- 0
          | HitEdgeInterior (edgeId, pt, _) ->
              let segLen = Vec2.distanceTo origin pt
              let branchDistance =
                if isGrid then 0.0f
                elif isContinuation then parentBranchDistance + segLen
                else 0.0f
              let branchTargetFactor =
                if isGrid then 1.0f
                elif isContinuation then parentBranchTargetFactor
                else sampleOrganicBranchTargetFactor parentBranchTargetFactor rng
              if segLen < sMin then
                g.MarkFinished(nid)
                stuck <- stuck + 1
              else
                let splitNode = g.SplitEdge(edgeId, pt)
                if not isGrid && abs driftBias > 0.04f then
                  driftBiasByNode.[NodeId.value splitNode] <- driftBias
                if not isGrid then
                  lengthFactorByNode.[NodeId.value splitNode] <- lengthFactor
                  branchDistanceByNode.[NodeId.value nid] <- branchDistance
                  branchTargetFactorByNode.[NodeId.value nid] <- branchTargetFactor
                  branchDistanceByNode.[NodeId.value splitNode] <- 0.0f
                  branchTargetFactorByNode.[NodeId.value splitNode] <-
                    sampleOrganicBranchTargetFactor branchTargetFactor rng
                g.AddEdge(nid, splitNode, cls, RoadClass.width cls) |> ignore
                added <- added + 1
                stuck <- 0
          | NoIntersection ->
              let p2 = adaptEnlargement g origin proposed
              let segLen = Vec2.distanceTo origin p2
              let branchDistance =
                if isGrid then 0.0f
                elif isContinuation then parentBranchDistance + segLen
                else 0.0f
              let branchTargetFactor =
                if isGrid then 1.0f
                elif isContinuation then parentBranchTargetFactor
                else sampleOrganicBranchTargetFactor parentBranchTargetFactor rng
              if segLen < sMin then
                g.MarkFinished(nid)
                stuck <- stuck + 1
              else
                let snapTarget =
                  if isGrid then adaptSnapping g p2 snapR nid
                  else chooseOrganicSnapTarget g nid origin p2 snapR
                match snapTarget with
                | Some existingId ->
                    // Snap! Creates T-junction or crossroad
                    if not isGrid then
                      branchDistanceByNode.[NodeId.value nid] <- branchDistance
                      branchTargetFactorByNode.[NodeId.value nid] <- branchTargetFactor
                      branchDistanceByNode.[NodeId.value existingId] <- 0.0f
                      branchTargetFactorByNode.[NodeId.value existingId] <-
                        sampleOrganicBranchTargetFactor branchTargetFactor rng
                    g.AddEdge(nid, existingId, cls, RoadClass.width cls) |> ignore
                    added <- added + 1
                    stuck <- 0
                | None ->
                    let newId = g.AddNode(p2, cls)
                    if not isGrid && abs driftBias > 0.04f then
                      driftBiasByNode.[NodeId.value newId] <- driftBias
                    if not isGrid then
                      lengthFactorByNode.[NodeId.value newId] <- lengthFactor
                      branchDistanceByNode.[NodeId.value nid] <- branchDistance
                      branchTargetFactorByNode.[NodeId.value nid] <- branchTargetFactor
                      branchDistanceByNode.[NodeId.value newId] <- branchDistance
                      branchTargetFactorByNode.[NodeId.value newId] <- branchTargetFactor
                    g.AddEdge(nid, newId, cls, RoadClass.width cls) |> ignore
                    added <- added + 1
                    stuck <- 0

let private buildMajorStreetGrowth (rect: TRect) (moduleDemand: int) (organic: float32) (rng: Random) =
  if rect.W < 8.0f || rect.H < 8.0f || moduleDemand <= 0 then
    { GrowthCenters = [ Vec2.Create(TRect.centerX rect, TRect.centerZ rect) ]
      MajorStreets = []
      QuarterRects = [] },
    []
  else
    let segmentLength = max 4.0f (min rect.W rect.H / 5.0f)
    let snapRadius = max 1.5f (segmentLength * 0.45f)
    let minSegmentLength = max 1.5f (segmentLength * 0.35f)
    let focus = max 0.0025f (0.01f * (1.0f - organic * 0.6f))
    let maxEdges = max 2 (moduleDemand + 2)
    let isGrid = organic < 0.35f
    let g, centers =
      match isGrid with
      | true -> seedGridMajorStreetGrowthGraph rect
      | false -> seedOrganicMajorStreetGrowthGraph rect rng
    growStreets g centers rng Avenue isGrid segmentLength snapRadius minSegmentLength maxEdges focus (Some rect)
    if not isGrid then
      let streetLength = max 3.8f (segmentLength * 0.68f)
      let streetSnapRadius = max 1.35f (streetLength * 0.52f)
      let streetMinSegmentLength = max 1.2f (streetLength * 0.34f)
      let streetEdges = max 4 (moduleDemand + 1)
      let streetFocus = focus * 1.18f
      growStreets g centers rng Street false streetLength streetSnapRadius streetMinSegmentLength streetEdges streetFocus (Some rect)

    let majorStreets =
      g.Edges
      |> Seq.filter (fun edge ->
          if edge.Class <> Avenue then
            false
          else
            let a = (g.N edge.A).Pos
            let b = (g.N edge.B).Pos
            not (isBoundaryPoint rect a && isBoundaryPoint rect b))
      |> Seq.map (fun edge -> MajorStreet.planned (edgeToCorridorRect g edge))
      |> Seq.filter (fun street -> street.Segment.W > 0.5f && street.Segment.H > 0.5f)
      |> Seq.toList

    let roads =
      let roadColor roadClass status =
        let (cr, cg, cb) = RoadClass.color roadClass
        let alpha, tint =
          match status with
          | Planned -> 192uy, 18
          | Built -> 255uy, 0
        let inline channel value =
          let lifted = int value + tint
          byte (min 255 lifted)
        Color(channel cr, channel cg, channel cb, alpha)

      g.Edges
      |> Seq.filter (fun edge ->
        let a = (g.N edge.A).Pos
        let b = (g.N edge.B).Pos
        not (isBoundaryPoint rect a && isBoundaryPoint rect b))
      |> Seq.map (fun edge ->
        let a = (g.N edge.A).Pos
        let b = (g.N edge.B).Pos
        { FromFunc = ""
          ToFunc = ""
          FromPos = Vector3(a.X, edge.Width / 2.0f, a.Y)
          ToPos = Vector3(b.X, edge.Width / 2.0f, b.Y)
          Weight = RoadClass.tier edge.Class
          Color = roadColor edge.Class Planned
          Organic = organic })
      |> Seq.toList

    let quarterRects =
      g.Faces
      |> Seq.choose (fun face -> g.FacePolygon(face.Id) |> faceToQuarterRect rect)
      |> Seq.distinctBy quarterRectKey
      |> Seq.sortByDescending TRect.area
      |> Seq.fold (fun kept next ->
        if kept |> List.exists (fun existing -> rectsOverlap existing next) then kept
        else kept @ [ next ]) []

    { GrowthCenters = centers |> Array.toList
      MajorStreets = majorStreets
      QuarterRects = quarterRects },
    roads

let planMajorStreetGrowth (rect: TRect) (moduleDemand: int) (organic: float32) (rng: Random) : MajorStreetGrowthPlan =
  buildMajorStreetGrowth rect moduleDemand organic rng |> fst

let private roadEndpoints (road: Road) =
  Vec2.Create(road.FromPos.X, road.FromPos.Z), Vec2.Create(road.ToPos.X, road.ToPos.Z)

let private roadLength (road: Road) =
  let a, b = roadEndpoints road
  Vec2.distanceTo a b

let private pointLineDistance (origin: Vec2) (dir: Vec2) (point: Vec2) =
  abs ((point.X - origin.X) * dir.Y - (point.Y - origin.Y) * dir.X)

let private collinearRoads tolerance (a: Road) (b: Road) =
  let a0, a1 = roadEndpoints a
  let b0, b1 = roadEndpoints b
  let dirA = Vec2.normalize (Vec2.sub a1 a0)
  let dirB = Vec2.normalize (Vec2.sub b1 b0)
  match Vec2.lengthSq dirA < 1e-4f || Vec2.lengthSq dirB < 1e-4f with
  | true -> false
  | false ->
      let cross = abs (dirA.X * dirB.Y - dirA.Y * dirB.X)
      let lineTolerance = tolerance + max a.FromPos.Y b.FromPos.Y
      cross <= 0.035f
      && pointLineDistance a0 dirA b0 <= lineTolerance
      && pointLineDistance a0 dirA b1 <= lineTolerance

let private roadProjectionInterval (dir: Vec2) (road: Road) =
  let a, b = roadEndpoints road
  let pa = Vec2.dot a dir
  let pb = Vec2.dot b dir
  min pa pb, max pa pb

let private roadsCanMerge (a: Road) (b: Road) =
  match collinearRoads 0.15f a b with
  | false -> false
  | true ->
      let a0, a1 = roadEndpoints a
      let dir = Vec2.normalize (Vec2.sub a1 a0)
      let aMin, aMax = roadProjectionInterval dir a
      let bMin, bMax = roadProjectionInterval dir b
      let gap = max aMin bMin - min aMax bMax
      gap <= 0.4f

let private mergeRoadPair (a: Road) (b: Road) =
  let points =
    let a0, a1 = roadEndpoints a
    let b0, b1 = roadEndpoints b
    [| a0; a1; b0; b1 |]

  let mutable bestA = points.[0]
  let mutable bestB = points.[1]
  let mutable bestDist = Vec2.distanceToSq bestA bestB

  for i in 0 .. points.Length - 1 do
    for j in i + 1 .. points.Length - 1 do
      let dist = Vec2.distanceToSq points.[i] points.[j]
      if dist > bestDist then
        bestDist <- dist
        bestA <- points.[i]
        bestB <- points.[j]

  let startPt, endPt =
    match bestA.X < bestB.X || (abs (bestA.X - bestB.X) < 0.001f && bestA.Y <= bestB.Y) with
    | true -> bestA, bestB
    | false -> bestB, bestA

  let color =
    match a.Weight >= b.Weight with
    | true -> a.Color
    | false -> b.Color

  { FromFunc = a.FromFunc
    ToFunc = a.ToFunc
    FromPos = Vector3(startPt.X, max a.FromPos.Y b.FromPos.Y, startPt.Y)
    ToPos = Vector3(endPt.X, max a.ToPos.Y b.ToPos.Y, endPt.Y)
    Weight = max a.Weight b.Weight
    Color = color
    Organic = max a.Organic b.Organic }

let canonicalizeRoads (roads: Road list) =
  let ordered =
    roads
    |> List.filter (fun road -> roadLength road > 0.1f)
    |> List.sortByDescending roadLength

  let merged = ResizeArray<Road>()

  for road in ordered do
    let mutable current = road
    let mutable searchIndex = 0
    let mutable keepSearching = true

    while keepSearching && searchIndex < merged.Count do
      match roadsCanMerge current merged.[searchIndex] with
      | true ->
          current <- mergeRoadPair current merged.[searchIndex]
          merged.RemoveAt(searchIndex)
          searchIndex <- 0
      | false ->
          searchIndex <- searchIndex + 1

      keepSearching <- searchIndex < merged.Count

    merged.Add current

  merged
  |> Seq.toList
  |> List.sortBy (fun road ->
    let a, b = roadEndpoints road
    min a.X b.X, min a.Y b.Y, max a.X b.X, max a.Y b.Y)

type private FrontageStrip =
  { Start: Vec2
    Dir: Vec2
    Normal: Vec2
    Length: float32
    HalfWidth: float32
    RoadClass: RoadClass }

let private roadClassForWeight weight =
  match weight with
  | w when w >= RoadClass.tier Boulevard -> Boulevard
  | w when w >= RoadClass.tier Avenue -> Avenue
  | w when w >= RoadClass.tier Street -> Street
  | w when w >= RoadClass.tier Lane -> Lane
  | _ -> Alley

let private clipSegmentToRect (rect: TRect) (startPt: Vec2) (endPt: Vec2) : (Vec2 * Vec2) option =
  let dx = endPt.X - startPt.X
  let dz = endPt.Y - startPt.Y
  let mutable t0 = 0.0f
  let mutable t1 = 1.0f

  let inline update p q =
    if abs p < 1e-6f then
      q >= 0.0f
    else
      let r = q / p
      if p < 0.0f then
        if r > t1 then false
        else
          if r > t0 then t0 <- r
          true
      else
        if r < t0 then false
        else
          if r < t1 then t1 <- r
          true

  if update -dx (startPt.X - rect.X)
     && update dx (rect.X + rect.W - startPt.X)
     && update -dz (startPt.Y - rect.Z)
     && update dz (rect.Z + rect.H - startPt.Y) then
    let clippedStart = Vec2.Create(startPt.X + dx * t0, startPt.Y + dz * t0)
    let clippedEnd = Vec2.Create(startPt.X + dx * t1, startPt.Y + dz * t1)
    Some (clippedStart, clippedEnd)
  else
    None

let private frontageWeight roadClass length =
  let multiplier =
    match roadClass with
    | Boulevard -> 1.75f
    | Avenue -> 1.40f
    | Street -> 1.0f
    | Lane -> 0.78f
    | Alley -> 0.60f
  length * multiplier

let private frontageBaseDepth roadClass =
  match roadClass with
  | Boulevard -> 2.8f
  | Avenue -> 2.4f
  | Street -> 1.9f
  | Lane -> 1.5f
  | Alley -> 1.15f

let private frontageSetback roadClass buildingType =
  let roadOffset =
    match roadClass with
    | Boulevard -> 0.58f
    | Avenue -> 0.46f
    | Street -> 0.34f
    | Lane -> 0.24f
    | Alley -> 0.18f
  let typeOffset =
    match buildingType with
    | Shed | Cottage -> 0.12f
    | Rowhouse -> 0.08f
    | Commercial -> 0.04f
    | Tower -> 0.0f
    | Skyscraper -> -0.03f
  max 0.18f (roadOffset + typeOffset)

/// Scale building footprint by cyclomatic complexity.
/// Returns a multiplier >= 1.0 so more complex functions read slightly larger.
let complexityFootprintFactor (complexity: int) : float32 =
  1.0f + MathF.Log(float32 complexity + 1.0f) * 0.15f

let private frontageFootprint roadClass buildingType parcelWidth complexity =
  let baseWidth, depthScale =
    match buildingType with
    | Shed -> max 0.58f (parcelWidth * 0.78f), 0.54f
    | Cottage -> max 0.72f (parcelWidth * 0.84f), 0.62f
    | Rowhouse -> max 0.70f (parcelWidth * 0.80f), 0.72f
    | Commercial -> max 0.82f (parcelWidth * 0.86f), 0.76f
    | Tower -> max 0.90f (parcelWidth * 0.84f), 0.82f
    | Skyscraper -> max 0.98f (parcelWidth * 0.88f), 0.88f
  let complexityScale = complexityFootprintFactor complexity |> min 1.18f
  let widthAlongRoad =
    min (parcelWidth * 0.96f) (baseWidth * complexityScale)
    |> max 0.55f
  let depth =
    min (parcelWidth * 0.72f) (frontageBaseDepth roadClass * depthScale * min 1.08f complexityScale)
    |> max 0.45f
  widthAlongRoad, depth

let private orientedBounds width depth angleDegrees =
  let angleRadians = angleDegrees * MathF.PI / 180.0f
  let c = abs (MathF.Cos angleRadians)
  let s = abs (MathF.Sin angleRadians)
  width * c + depth * s, width * s + depth * c

let private clampPointToRect (rect: TRect) (x: float32, z: float32) =
  let maxX = rect.X + rect.W
  let maxZ = rect.Z + rect.H
  min maxX (max rect.X x), min maxZ (max rect.Z z)

let private uniqueHullPoints (points: (float32 * float32) list) =
  points
  |> List.distinctBy (fun (x, z) -> MathF.Round(x * 100.0f), MathF.Round(z * 100.0f))

let private cross2D (ox: float32, oz: float32) (ax: float32, az: float32) (bx: float32, bz: float32) =
  (ax - ox) * (bz - oz) - (az - oz) * (bx - ox)

let private convexHull (points: (float32 * float32) list) =
  let sorted =
    points
    |> uniqueHullPoints
    |> List.sortBy id

  match sorted with
  | [] | [_] | [_; _] -> sorted
  | _ ->
      let buildHalf ordered =
        let hull = ResizeArray<float32 * float32>()
        for point in ordered do
          while hull.Count >= 2 &&
                cross2D hull.[hull.Count - 2] hull.[hull.Count - 1] point <= 0.0f do
            hull.RemoveAt(hull.Count - 1)
          hull.Add point
        hull |> Seq.toList

      let lower = buildHalf sorted
      let upper = buildHalf (sorted |> List.rev)
      (lower |> List.take (lower.Length - 1))
      @ (upper |> List.take (upper.Length - 1))

let private roadSurfaceRibbonPoints (rect: TRect) (road: Road) =
  let startPt = Vec2.Create(road.FromPos.X, road.FromPos.Z)
  let endPt = Vec2.Create(road.ToPos.X, road.ToPos.Z)
  match clipSegmentToRect rect startPt endPt with
  | Some (clippedStart, clippedEnd) ->
      let delta = Vec2.sub clippedEnd clippedStart
      let len = Vec2.length delta
      if len <= 0.5f then []
      else
        let dir = Vec2.normalize delta
        let normal = Vec2.Create(-dir.Y, dir.X)
        let pad = max 0.55f (max road.FromPos.Y road.ToPos.Y + 0.45f)
        let samples = max 2 (int (MathF.Ceiling(len / 6.0f)))
        [ for idx in 0 .. samples do
            let t = float32 idx / float32 samples
            let along = Vec2.add clippedStart (Vec2.scale (t * len) dir)
            let left = Vec2.add along (Vec2.scale pad normal)
            let right = Vec2.add along (Vec2.scale -pad normal)
            yield clampPointToRect rect (left.X, left.Y)
            yield clampPointToRect rect (right.X, right.Y) ]
  | None -> []

let private buildingSurfacePoints (rect: TRect) (building: FuncBuilding) =
  [ clampPointToRect rect (building.X, building.Z)
    clampPointToRect rect (building.X + building.W, building.Z)
    clampPointToRect rect (building.X + building.W, building.Z + building.D)
    clampPointToRect rect (building.X, building.Z + building.D)
    clampPointToRect rect (building.X + building.W / 2.0f, building.Z + building.D / 2.0f) ]

let computeBlockSurfaceHull (block: ModuleBlock) (blockBuildings: FuncBuilding list) (roads: Road list) =
  let fallback =
    let inset = TRect.inset 0.8f block.Rect
    [ inset.X, inset.Z
      inset.X + inset.W, inset.Z
      inset.X + inset.W, inset.Z + inset.H
      inset.X, inset.Z + inset.H ]

  let points =
    (roads |> List.collect (roadSurfaceRibbonPoints block.Rect))
    @ (blockBuildings |> List.collect (buildingSurfacePoints block.Rect))
    |> uniqueHullPoints

  match points.Length >= 3 with
  | false -> fallback
  | true ->
      let hull = convexHull points
      if hull.Length >= 3 then hull else fallback

/// Point-in-polygon test (ray casting)
let pointInPoly (poly: Vec2 list) (px: float32) (pz: float32) : bool =
  let mutable inside = false
  let n = poly.Length
  let mutable j = n - 1
  for i in 0 .. n - 1 do
    let pi = poly.[i]
    let pj = poly.[j]
    if (pi.Y > pz) <> (pj.Y > pz) &&
       px < (pj.X - pi.X) * (pz - pi.Y) / (pj.Y - pi.Y) + pi.X then
      inside <- not inside
    j <- i
  inside

/// Minimum distance from point (px,pz) to the nearest polygon boundary edge.
/// Used to verify road-primary placement: every building should be within lot depth of an edge.
let distanceToPoly (poly: Vec2 list) (px: float32) (pz: float32) : float32 =
  let n = poly.Length
  let mutable minDist = Single.MaxValue
  for i in 0 .. n - 1 do
    let a = poly.[i]
    let b = poly.[(i + 1) % n]
    let dx = b.X - a.X
    let dz = b.Y - a.Y
    let lenSq = dx * dx + dz * dz
    let t =
      if lenSq < 1e-10f then 0.0f
      else min 1.0f (max 0.0f (((px - a.X) * dx + (pz - a.Y) * dz) / lenSq))
    let nearX = a.X + t * dx
    let nearZ = a.Y + t * dz
    let dist = sqrt ((px - nearX) * (px - nearX) + (pz - nearZ) * (pz - nearZ))
    if dist < minDist then minDist <- dist
  minDist

let placeBuildingsInLots
  (lots: PlannedLot list)
  (funcs: FuncDef list)
  (heatMap: Map<string, float32 * int * int>)
  (districtColor: Color)
  (rng: Random)
  (gitMeta: Map<string, GitMeta>)
  : FuncBuilding list =
  let assignedLots = lots |> List.truncate funcs.Length
  let assignedFuncs =
    funcs
    |> List.sortByDescending (fun f -> f.LineCount)
    |> List.truncate assignedLots.Length
  let pairs = List.zip assignedLots assignedFuncs
  [ for (lot, f) in pairs do
      let heat, callers, callees =
        heatMap |> Map.tryFind f.QualifiedName |> Option.defaultValue (0.0f, 0, 0)
      let complexity = computeComplexity f.Body
      let bt = classifyBuilding f.LineCount complexity heat
      let coverage =
        match bt with
        | Shed | Cottage -> 0.55f
        | Rowhouse -> 0.70f
        | Commercial | Tower -> 0.82f
        | Skyscraper -> 0.90f
      let frontageBias =
        match bt with
        | Shed | Cottage -> 1.15f
        | Rowhouse -> 1.0f
        | _ -> 0.75f
      let baseMargin = (1.0f - sqrt coverage) * min lot.Rect.W lot.Rect.H * 0.5f
      let lotMargin = min (min (lot.Rect.W / 4.0f) (lot.Rect.H / 4.0f)) (max 0.05f (baseMargin * frontageBias))
      let frontageDepthScale =
        match bt with
        | Shed | Cottage -> 0.55f
        | Rowhouse -> 0.72f
        | Commercial | Tower -> 0.84f
        | Skyscraper -> 0.92f
      let frontageSetback = lotMargin * 0.5f
      let bodyDepth = max 0.25f (lot.Rect.H * frontageDepthScale - frontageSetback)
      let bodyWidth = max 0.25f (lot.Rect.W * frontageDepthScale - frontageSetback)
      let envelope =
        match lot.FrontageEdge with
        | North ->
            TRect.create
              (lot.Rect.X + lotMargin)
              (lot.Rect.Z + frontageSetback)
              (max 0.25f (lot.Rect.W - lotMargin * 2.0f))
              (min (max 0.25f bodyDepth) (max 0.25f (lot.Rect.H - frontageSetback - lotMargin)))
        | South ->
            let h = min (max 0.25f bodyDepth) (max 0.25f (lot.Rect.H - frontageSetback - lotMargin))
            TRect.create
              (lot.Rect.X + lotMargin)
              (lot.Rect.Z + lot.Rect.H - frontageSetback - h)
              (max 0.25f (lot.Rect.W - lotMargin * 2.0f))
              h
        | West ->
            TRect.create
              (lot.Rect.X + frontageSetback)
              (lot.Rect.Z + lotMargin)
              (min (max 0.25f bodyWidth) (max 0.25f (lot.Rect.W - frontageSetback - lotMargin)))
              (max 0.25f (lot.Rect.H - lotMargin * 2.0f))
        | East ->
            let w = min (max 0.25f bodyWidth) (max 0.25f (lot.Rect.W - frontageSetback - lotMargin))
            TRect.create
              (lot.Rect.X + lot.Rect.W - frontageSetback - w)
              (lot.Rect.Z + lotMargin)
              w
              (max 0.25f (lot.Rect.H - lotMargin * 2.0f))
      let ageDays =
        gitMeta |> Map.tryFind f.FilePath
        |> Option.map (fun m -> float32 (DateTimeOffset.Now - m.LastCommitDate).TotalDays)
        |> Option.defaultValue 180.0f
      let rotation =
        match lot.FrontageEdge with
        | North | South -> 0.0f
        | East | West -> 90.0f
      let jitter = (rng.NextSingle() - 0.5f) * 3.0f * max 0.25f (1.0f - coverage)
      yield {
        Func = f
        Heat = heat
        CallerCount = callers
        CalleeCount = callees
        Complexity = complexity
        BuildingType = bt
        GitAgeDays = ageDays
        X = envelope.X
        Z = envelope.Z
        W = max 0.25f envelope.W
        D = max 0.25f envelope.H
        H = BuildingType.height bt f.LineCount heat
        Rotation = rotation + jitter
        Color = BuildingType.wallColor bt f.Name ageDays
        RoofColor = BuildingType.roofColor bt districtColor
        District = f.Module } ]

/// Road-primary building packer (replaces packInBlock for Weber parcels).
/// Places buildings in a row along each polygon edge, set back by a sidewalk margin.
/// Every face polygon edge borders a road, so every building gets guaranteed road frontage.
/// No interior-grid broadcasting — no landlocked buildings.
let packAlongEdges
  (poly: Vec2 list)
  (funcs: FuncDef list)
  (heatMap: Map<string, float32 * int * int>)
  (districtColor: Color)
  (rng: Random)
  (gitMeta: Map<string, GitMeta>)
  : FuncBuilding list =
  if poly.Length < 3 || funcs.IsEmpty then []
  else
    let n = poly.Length
    // Pre-compute edge geometry: (startPt, normDirX, normDirZ, length)
    let edges =
      [| for i in 0 .. n - 1 ->
           let a = poly.[i]
           let b = poly.[(i + 1) % n]
           let dx = b.X - a.X
           let dz = b.Y - a.Y
           let len = sqrt (dx * dx + dz * dz)
           if len < 1e-6f then (a, 0.0f, 0.0f, 0.0f)
           else (a, dx / len, dz / len, len) |]
    let totalLen = edges |> Array.sumBy (fun (_, _, _, l) -> l) |> max 0.001f

    let sortedFuncs = funcs |> List.sortByDescending (fun f -> f.LineCount)
    let mutable remaining = sortedFuncs
    let allBuildings = ResizeArray<FuncBuilding>()

    let placeChunk (chunk: FuncDef list) (a: Vec2) (dirX: float32) (dirZ: float32) (edgeLen: float32) =
      // Determine inward normal (left normal of edge dir for CCW polygon).
      // Verify by testing midpoint + small step; flip if outside.
      let nx0 = -dirZ
      let nz0 = dirX
      let midX = a.X + dirX * edgeLen * 0.5f
      let midZ = a.Y + dirZ * edgeLen * 0.5f
      let (nx, nz) =
        if pointInPoly poly (midX + nx0 * 0.15f) (midZ + nz0 * 0.15f) then (nx0, nz0)
        else (-nx0, -nz0)
      let spacing = edgeLen / float32 (chunk.Length + 1)
      let roadAngle = MathF.Atan2(dirZ, dirX) * 180.0f / MathF.PI
      chunk |> List.iteri (fun idx f ->
        let heat, callers, callees =
          heatMap |> Map.tryFind f.QualifiedName |> Option.defaultValue (0.0f, 0, 0)
        let complexity = computeComplexity f.Body
        let bt  = classifyBuilding f.LineCount complexity heat
        let typeSetback =
          match bt with
          | Shed | Cottage -> 0.8f
          | Rowhouse       -> 0.55f
          | _              -> 0.35f
        let t  = float32 (idx + 1) * spacing
        let px = a.X + t * dirX + nx * typeSetback
        let pz = a.Y + t * dirZ + nz * typeSetback
        if pointInPoly poly px pz then
          let coverageRatio =
            match bt with
            | Shed | Cottage -> 0.45f
            | Rowhouse       -> 0.65f
            | Commercial | Tower -> 0.80f
            | Skyscraper     -> 0.90f
          let fp = max 0.3f (min (spacing * coverageRatio) (MathF.Log(float32 f.LineCount + 1.0f) * 0.5f + 0.3f)) * complexityFootprintFactor complexity
          let ageDays =
            gitMeta |> Map.tryFind f.FilePath
            |> Option.map (fun m -> float32 (DateTimeOffset.Now - m.LastCommitDate).TotalDays)
            |> Option.defaultValue 180.0f
          let jitter = (rng.NextSingle() - 0.5f) * 4.0f
          allBuildings.Add {
            Func = f; Heat = heat; CallerCount = callers; CalleeCount = callees
            Complexity = complexity
            BuildingType = bt
            GitAgeDays = ageDays
            X = px - fp / 2.0f; Z = pz - fp / 2.0f
            W = fp; D = fp
            H = BuildingType.height bt f.LineCount heat
            Rotation  = roadAngle + jitter
            Color     = BuildingType.wallColor bt f.Name ageDays
            RoofColor = BuildingType.roofColor bt districtColor
            District  = f.Module })

    // Distribute functions proportionally by edge length
    for ei in 0 .. edges.Length - 1 do
      let (a, dirX, dirZ, edgeLen) = edges.[ei]
      if edgeLen >= 0.5f && not remaining.IsEmpty then
        let isLast = ei = edges.Length - 1
        let count =
          if isLast then remaining.Length
          else max 0 (int (float32 funcs.Length * edgeLen / totalLen + 0.5f))
        let take = min count remaining.Length
        if take > 0 then
          let chunk   = remaining |> List.take take
          remaining  <- remaining |> List.skip take
          placeChunk chunk a dirX dirZ edgeLen

    // Any overflow spills to the longest edge
    if not remaining.IsEmpty then
      let (a, dirX, dirZ, edgeLen) = edges |> Array.maxBy (fun (_, _, _, l) -> l)
      if edgeLen >= 0.5f then placeChunk remaining a dirX dirZ edgeLen

    allBuildings |> Seq.toList
/// Every building is accessible via an alley — no landlocked buildings.
/// Returns (buildings, alleyRoads) where alleyRoads are lane-centerline segments.
/// Road-primary building placement for Weber districts.
/// Places buildings on BOTH sides of each road segment with perpendicular setback.
/// No DCEL face polygon traversal — buildings are placed directly adjacent to visible roads.
/// Falls back to packAlongEdges on the block perimeter if no internal roads exist.
let packAlongRoads
  (roads: Road list)
  (blockBounds: TRect)
  (funcs: FuncDef list)
  (heatMap: Map<string, float32 * int * int>)
  (districtColor: Color)
  (rng: Random)
  (gitMeta: Map<string, GitMeta>)
  : FuncBuilding list =
  if funcs.IsEmpty then []
  else
    let frontages =
      [ for road in roads do
          let startPt = Vec2.Create(road.FromPos.X, road.FromPos.Z)
          let endPt = Vec2.Create(road.ToPos.X, road.ToPos.Z)
          match clipSegmentToRect blockBounds startPt endPt with
          | Some (clippedStart, clippedEnd) ->
              let delta = Vec2.sub clippedEnd clippedStart
              let len = Vec2.length delta
              if len > 0.9f then
                let dir = Vec2.normalize delta
                let leftNormal = Vec2.Create(-dir.Y, dir.X)
                let roadClass = roadClassForWeight road.Weight
                let halfWidth = max road.FromPos.Y road.ToPos.Y
                yield { Start = clippedStart
                        Dir = dir
                        Normal = leftNormal
                        Length = len
                        HalfWidth = halfWidth
                        RoadClass = roadClass }
                yield { Start = clippedStart
                        Dir = dir
                        Normal = Vec2.negate leftNormal
                        Length = len
                        HalfWidth = halfWidth
                        RoadClass = roadClass }
          | None -> () ]

    if frontages.IsEmpty then
      // No internal Weber roads — fall back to block-perimeter packing
      let poly =
        [ Vec2.Create(blockBounds.X,                 blockBounds.Z)
          Vec2.Create(blockBounds.X + blockBounds.W, blockBounds.Z)
          Vec2.Create(blockBounds.X + blockBounds.W, blockBounds.Z + blockBounds.H)
          Vec2.Create(blockBounds.X,                 blockBounds.Z + blockBounds.H) ]
      packAlongEdges poly funcs heatMap districtColor rng gitMeta
    else
      let sortedFrontages =
        frontages
        |> List.sortByDescending (fun frontage -> frontageWeight frontage.RoadClass frontage.Length)
      let frontageCounts =
        sortedFrontages
        |> List.map (fun frontage -> frontageWeight frontage.RoadClass frontage.Length)
        |> allocateByWeights funcs.Length
      let sortedFuncs = funcs |> List.sortByDescending (fun f -> f.LineCount)
      let mutable remaining = sortedFuncs
      let allBuildings = ResizeArray<FuncBuilding>()
      let bx0 = blockBounds.X
      let bx1 = blockBounds.X + blockBounds.W
      let bz0 = blockBounds.Z
      let bz1 = blockBounds.Z + blockBounds.H
      let eps = 0.6f  // small tolerance for near-boundary placements

      let placeFrontage (frontage: FrontageStrip) (chunk: FuncDef list) =
        let spacing = frontage.Length / float32 (chunk.Length + 1)
        let roadAngle = MathF.Atan2(frontage.Dir.Y, frontage.Dir.X) * 180.0f / MathF.PI
        let parcelGap = min 0.75f (max 0.18f (spacing * 0.18f))
        chunk |> List.iteri (fun idx f ->
          let heat, callers, callees =
            heatMap |> Map.tryFind f.QualifiedName |> Option.defaultValue (0.0f, 0, 0)
          let complexity = computeComplexity f.Body
          let bt = classifyBuilding f.LineCount complexity heat
          let parcelWidth = max 0.70f (spacing - parcelGap)
          let widthAlongRoad, depth = frontageFootprint frontage.RoadClass bt parcelWidth complexity
          let setback = frontage.HalfWidth + frontageSetback frontage.RoadClass bt
          let alongJitter = (rng.NextSingle() - 0.5f) * parcelGap * 0.35f
          let centerAlong = float32 (idx + 1) * spacing + alongJitter
          let center =
            let alongPoint = Vec2.add frontage.Start (Vec2.scale centerAlong frontage.Dir)
            Vec2.add alongPoint (Vec2.scale (setback + depth / 2.0f) frontage.Normal)
          let worldW, worldD = orientedBounds widthAlongRoad depth roadAngle
          let px = center.X - worldW / 2.0f
          let pz = center.Y - worldD / 2.0f
          if px >= bx0 - eps && px + worldW <= bx1 + eps && pz >= bz0 - eps && pz + worldD <= bz1 + eps then
            let ageDays =
              gitMeta |> Map.tryFind f.FilePath
              |> Option.map (fun m -> float32 (DateTimeOffset.Now - m.LastCommitDate).TotalDays)
              |> Option.defaultValue 180.0f
            let jitter = (rng.NextSingle() - 0.5f) * 2.0f
            allBuildings.Add {
              Func = f; Heat = heat; CallerCount = callers; CalleeCount = callees
              Complexity = complexity
              BuildingType = bt
              GitAgeDays = ageDays
              X = px; Z = pz
              W = worldW; D = worldD
              H = BuildingType.height bt f.LineCount heat
              Rotation  = roadAngle + jitter
              Color     = BuildingType.wallColor bt f.Name ageDays
              RoofColor = BuildingType.roofColor bt districtColor
              District  = f.Module })

      for (frontage, count) in List.zip sortedFrontages frontageCounts do
        if not remaining.IsEmpty then
          let take = min count remaining.Length
          if take > 0 && frontage.Length > 0.9f then
            let chunk = remaining |> List.take take
            remaining <- remaining |> List.skip take
            placeFrontage frontage chunk

      // Overflow spills to the strongest frontage corridor.
      if not remaining.IsEmpty then
        let strongestFrontage = sortedFrontages |> List.head
        placeFrontage strongestFrontage remaining

      // Ultimate fallback: if every placement was out-of-bounds, use perimeter packing
      if allBuildings.Count = 0 then
        let poly =
          [ Vec2.Create(blockBounds.X,                 blockBounds.Z)
            Vec2.Create(blockBounds.X + blockBounds.W, blockBounds.Z)
            Vec2.Create(blockBounds.X + blockBounds.W, blockBounds.Z + blockBounds.H)
            Vec2.Create(blockBounds.X,                 blockBounds.Z + blockBounds.H) ]
        packAlongEdges poly funcs heatMap districtColor rng gitMeta
      else
        allBuildings |> Seq.toList

/// Spec-driven district layout: hierarchical streets induce blocks, blocks subdivide into frontage lots,
/// and buildings are placed inside those lots instead of being broadcast across a pre-shaped rectangle.
let layoutWeberDistrict
  (rect: TRect) (funcs: FuncDef list)
  (heatMap: Map<string, float32 * int * int>)
  (districtColor: Color)
  (globalMaxComplexity: int) (globalMaxLineCount: int)
  (organic: float32) (rng: Random)
  (gitMeta: Map<string, GitMeta>)
  : FuncBuilding list * Road list =
  ignore (globalMaxComplexity, globalMaxLineCount)
  if funcs.IsEmpty then ([], [])
  elif rect.W < 1.5f || rect.H < 1.5f then ([], [])
  else
    let blockDemand =
      max 1 (int (MathF.Ceiling(float32 funcs.Length / 4.0f)))
    let growthPlan, grownRoads = buildMajorStreetGrowth rect blockDemand organic rng
    let districtRoads = canonicalizeRoads grownRoads
    let frontageRoads =
      districtRoads
      |> List.filter (fun road -> road.Weight >= RoadClass.tier Avenue)
      |> function
        | [] -> districtRoads
        | arterialRoads -> arterialRoads
    let buildings =
      packAlongRoads frontageRoads rect funcs heatMap districtColor rng gitMeta
    (buildings, districtRoads)


let districtPalette =
  [| Color(70uy, 130uy, 180uy, 255uy)    // steel blue
     Color(178uy, 102uy, 68uy, 255uy)    // terracotta
     Color(85uy, 150uy, 90uy, 255uy)     // sage green
     Color(160uy, 120uy, 80uy, 255uy)    // sandstone
     Color(130uy, 90uy, 145uy, 255uy)    // muted purple
     Color(170uy, 140uy, 100uy, 255uy)   // warm tan
     Color(100uy, 140uy, 120uy, 255uy)   // teal gray
     Color(150uy, 100uy, 90uy, 255uy)    // clay
     Color(110uy, 130uy, 160uy, 255uy)   // slate blue
     Color(140uy, 130uy, 100uy, 255uy)   // khaki
     Color(120uy, 110uy, 140uy, 255uy)   // dusty violet
     Color(130uy, 150uy, 110uy, 255uy) |] // olive

// ─── Inter-district Arterial Network ────────────────────────────────────────
// Coupling-weighted Boulevard roads along shared boundaries between adjacent districts.
// Panels Seemann + Bill + Holden consensus: boundary-midpoint approach, not centroid-to-centroid.

/// Find pairs of adjacent module blocks sharing a boundary edge within eps tolerance.
let findAdjacentBlocks (blocks: ModuleBlock[]) (eps: float32) : (ModuleBlock * ModuleBlock) list =
  [ for i in 0 .. blocks.Length - 2 do
      for j in i + 1 .. blocks.Length - 1 do
        let r1 = blocks.[i].Rect
        let r2 = blocks.[j].Rect
        let zOv = min (r1.Z + r1.H) (r2.Z + r2.H) - max r1.Z r2.Z
        let xOv = min (r1.X + r1.W) (r2.X + r2.W) - max r1.X r2.X
        let vb = abs ((r1.X + r1.W) - r2.X) < eps || abs ((r2.X + r2.W) - r1.X) < eps
        let hb = abs ((r1.Z + r1.H) - r2.Z) < eps || abs ((r2.Z + r2.H) - r1.Z) < eps
        if vb && zOv > eps then yield blocks.[i], blocks.[j]
        elif hb && xOv > eps then yield blocks.[i], blocks.[j] ]

/// Count call edges crossing between two named modules (symmetrical).
let crossDistrictCallCount (callEdges: CallEdge list) (m1: string) (m2: string) : int =
  let inMod (m: string) (fn: string) = fn.StartsWith(m + ".") || fn = m
  callEdges
  |> List.sumBy (fun e ->
    if (inMod m1 e.From && inMod m2 e.To) || (inMod m2 e.From && inMod m1 e.To)
    then e.Weight
    else 0)

/// Build Boulevard roads along shared boundaries between adjacent module blocks.
/// Road halfWidth (in FromPos.Y) scales logarithmically with cross-district call coupling.
let buildArterialNetwork (blocks: ModuleBlock[]) (callEdges: CallEdge list) : Road list =
  let eps = 1.05f  // captures same-project (gap=0) and cross-project (gap≈1.0) adjacency
  let baseHW = RoadClass.width Boulevard / 2.0f
  let clrR, clrG, clrB = RoadClass.color Boulevard
  findAdjacentBlocks blocks eps
  |> List.map (fun (b1, b2) ->
    let r1 = b1.Rect
    let r2 = b2.Rect
    let coupling = crossDistrictCallCount callEdges b1.Module b2.Module
    let hw = baseHW * (1.0f + MathF.Log(float32 coupling + 1.0f) * 0.3f)
    let clr = Color(clrR, clrG, clrB, 255uy)
    let isVert =
      abs ((r1.X + r1.W) - r2.X) < eps || abs ((r2.X + r2.W) - r1.X) < eps
    if isVert then
      let bx = if abs ((r1.X + r1.W) - r2.X) < eps then r1.X + r1.W else r2.X + r2.W
      let zLo = max r1.Z r2.Z
      let zHi = min (r1.Z + r1.H) (r2.Z + r2.H)
      { FromFunc = b1.Module; ToFunc = b2.Module
        FromPos = Vector3(bx, hw, zLo)
        ToPos   = Vector3(bx, hw, zHi)
        Weight = coupling; Color = clr; Organic = 0.0f }
    else
      let bz = if abs ((r1.Z + r1.H) - r2.Z) < eps then r1.Z + r1.H else r2.Z + r2.H
      let xLo = max r1.X r2.X
      let xHi = min (r1.X + r1.W) (r2.X + r2.W)
      { FromFunc = b1.Module; ToFunc = b2.Module
        FromPos = Vector3(xLo, hw, bz)
        ToPos   = Vector3(xHi, hw, bz)
        Weight = coupling; Color = clr; Organic = 0.0f })

let buildVisibleRoadNetwork (_projectZones: ('a * TRect) list) (primaryRoads: Road list) : Road list =
  let seen = System.Collections.Generic.HashSet<struct(float32 * float32 * float32 * float32 * int)>()
  primaryRoads
  |> List.filter (fun road ->
    let key =
      struct (
        min road.FromPos.X road.ToPos.X,
        min road.FromPos.Z road.ToPos.Z,
        max road.FromPos.X road.ToPos.X,
        max road.FromPos.Z road.ToPos.Z,
        road.Weight)
    seen.Add(key))

// ─── Day/Night Cycle ────────────────────────────────────────────────────────

/// Compute window-lighting night scale from sun elevation.
/// Elevation 1.0 = noon (scale 1.0 → normal lit bias), -1.0 = midnight (scale 0.2 → more windows lit).
let nightScaleForElevation (elevation: float32) : float32 =
  0.2f + 0.8f * ((elevation + 1.0f) / 2.0f)

/// Build the entire city using squarified treemap layout (panel P0 recommendation).
/// Two-level hierarchy: project zones → module blocks. Modules sorted by call-graph centrality.
/// Returns city layout plus deduplicated call edges for interactive relationship overlays.
let buildCity (repoRoot: string) (projectFile: string option) =
  let rng = Random(42)
  let funcs =
    match projectFile with
    | Some file -> scanFunctionsForProject file
    | None -> scanFunctions repoRoot
  let rawEdges =
    match projectFile with
    | Some file -> buildSemanticCallGraphForProject file funcs
    | None -> buildCallGraph funcs
  let callEdges = mergeCallEdges rawEdges
  let heatMap = computeHeat funcs callEdges
  // Gather git history for every source file — drives organic-vs-grid per district
  let gitMetaByFile = scanGitMeta repoRoot funcs
  let today = DateTimeOffset.Now
  let projects = funcs |> List.map (fun f -> f.Project) |> List.distinct
  let moduleGroups = funcs |> List.groupBy (fun f -> f.Module)

  // Module call-graph centrality: total outgoing call weight per module (panel Q4)
  let moduleCentrality =
    callEdges
    |> List.groupBy (fun e ->
      let parts = e.From.Split('.')
      parts |> Array.take (max 1 (parts.Length - 1)) |> String.concat ".")
    |> List.map (fun (m, edges) -> (m, edges |> List.sumBy (fun e -> e.Weight)))
    |> Map.ofList

  // City dimensions — target ~55% building density, 30% roads
  let avgFootprint = 2.0f
  let totalBuildingArea = float32 funcs.Length * avgFootprint * avgFootprint
  let totalArea = totalBuildingArea / 0.70f
  let citySide = sqrt totalArea
  let halfCity = citySide / 2.0f
  let cityRect = TRect.create (-halfCity) (-halfCity) citySide citySide

  // Level 1: Project zones (area proportional to function count)
  let avenueGap = 2.4f
  let projectItems =
    projects
    |> List.map (fun proj ->
      let count = funcs |> List.filter (fun f -> f.Project = proj) |> List.length
      (proj, float32 count))
    |> List.sortByDescending snd
  let projectZones = squarifiedTreemap projectItems (TRect.inset (avenueGap / 2.0f) cityRect)

  // Level 2: Module blocks are assigned from project-scale street-induced blocks
  let streetGap = 1.0f
  let allBlocks = ResizeArray<ModuleBlock>()
  let mutable allPrimaryRoads = []
  let mutable colorIdx = 0
  for (proj, zone) in projectZones do
    let projModules =
      moduleGroups
      |> List.filter (fun (_, fns) -> fns.[0].Project = proj)
      |> List.sortByDescending (fun (modName, _) ->
        moduleCentrality |> Map.tryFind modName |> Option.defaultValue 0)
    let insetZone = TRect.inset (streetGap / 2.0f) zone
    let projectFileMetas =
      projModules
      |> List.collect snd
      |> List.map (fun f -> gitMetaByFile |> Map.tryFind f.FilePath |> Option.defaultValue GitMeta.empty)
    let projectOrganic = districtOrganicFactor today projectFileMetas
    let projectPlan, projectRoads =
      buildMajorStreetGrowth insetZone projModules.Length projectOrganic (Random(int (fnvHash proj)))
    let assignedRects =
      match projectPlan.QuarterRects with
      | [] -> [ insetZone ]
      | xs ->
          xs
          |> List.sortByDescending (blockScore insetZone)
          |> List.truncate projModules.Length
    let fallbackRects =
      assignedRects
      |> List.append (List.init (max 0 (projModules.Length - assignedRects.Length)) (fun _ -> insetZone))
    allPrimaryRoads <-
      allPrimaryRoads
      @ projectRoads
    for ((modName, _), rect) in List.zip projModules fallbackRects do
      let color = districtPalette.[colorIdx % districtPalette.Length]
      allBlocks.Add({ Module = modName; Project = proj; Rect = rect; Color = color })
      colorIdx <- colorIdx + 1

  // Pack buildings into each module block using street-induced sub-blocks and frontage lots
  let mutable allBuildings = []
  let mutable allDistricts = []
  let mutable allAlleyRoads = []

  // Compute global max complexity for normalized height curve
  let globalMaxComplexity =
    moduleGroups |> List.collect snd
    |> List.map (fun f -> computeComplexity f.Body)
    |> List.max |> max 1
  let globalMaxLineCount =
    moduleGroups |> List.collect snd
    |> List.map (fun f -> f.LineCount)
    |> List.max |> max 1

  for block in allBlocks do
    let modFuncs = moduleGroups |> List.find (fun (m, _) -> m = block.Module) |> snd
    let district = { Name = block.Module; FuncCount = modFuncs.Length
                     TotalLines = modFuncs |> List.sumBy (fun f -> f.LineCount)
                     Color = block.Color }
    allDistricts <- district :: allDistricts

    // Deterministic per-district RNG — each module always gets the same road layout
    let districtRng = Random(int (fnvHash block.Module))
    // Aggregate git history for all files in this module to compute organic factor
    let fileMetas =
      modFuncs
      |> List.map (fun f -> gitMetaByFile |> Map.tryFind f.FilePath |> Option.defaultValue GitMeta.empty)
    let organic = districtOrganicFactor today fileMetas
    let bldgs, weberRoads =
      layoutWeberDistrict block.Rect modFuncs heatMap block.Color globalMaxComplexity globalMaxLineCount organic districtRng gitMetaByFile
    allBuildings <- allBuildings @ bldgs
    allAlleyRoads <- allAlleyRoads @ weberRoads
    ignore rng  // global rng still here for future use

  // Inter-district arterial Boulevards at block boundaries, width scaled by call coupling
  let arterials = buildArterialNetwork (allBlocks.ToArray()) callEdges
  let primaryRoads = canonicalizeRoads (allPrimaryRoads @ arterials)
  let alleyRoads = canonicalizeRoads (allAlleyRoads @ arterials)
  let roads = buildVisibleRoadNetwork projectZones primaryRoads

  let buildings = allBuildings |> List.rev
  let districts = allDistricts |> List.rev
  let blocks = allBlocks.ToArray()
  printfn "City built: %d buildings, %d districts, %d roads, %d blocks, %d alleys"
    buildings.Length districts.Length roads.Length blocks.Length alleyRoads.Length
  (buildings, districts, roads, blocks, callEdges, alleyRoads)

let buildRelationMaps
  (buildings: FuncBuilding[])
  (edges: CallEdge list)
  : Map<string, RelatedBuilding list> * Map<string, RelatedBuilding list> =
  let buildingByName =
    buildings
    |> Array.map (fun b -> b.Func.QualifiedName, b)
    |> Map.ofArray

  let toRelationMap (pairs: (string * RelatedBuilding) list) =
    pairs
    |> List.groupBy fst
    |> List.map (fun (key, rels) ->
      key,
      (rels
       |> List.map snd
       |> List.sortByDescending (fun rel -> rel.Weight)))
    |> Map.ofList

  let outgoing =
    edges
    |> List.choose (fun edge ->
      match Map.tryFind edge.To buildingByName with
      | Some target ->
        Some (edge.From, { Building = target; Weight = edge.Weight })
      | None -> None)
    |> toRelationMap

  let incoming =
    edges
    |> List.choose (fun edge ->
      match Map.tryFind edge.From buildingByName with
      | Some source ->
        Some (edge.To, { Building = source; Weight = edge.Weight })
      | None -> None)
    |> toRelationMap

  (incoming, outgoing)

let ellipsize (maxChars: int) (text: string) =
  if text.Length <= maxChars then text
  elif maxChars <= 3 then text.Substring(0, maxChars)
  else text.Substring(0, maxChars - 3) + "..."

let buildingCenter (b: FuncBuilding) =
  Vector3(b.X + b.W / 2.0f, b.H / 2.0f + 0.5f, b.Z + b.D / 2.0f)

let buildingRoofCenter (b: FuncBuilding) =
  Vector3(b.X + b.W / 2.0f, b.H + 0.2f, b.Z + b.D / 2.0f)


// ─── CBool Helper ─────────────────────────────────────────────

/// Convert Raylib's CBool to F# bool
let inline rb (v: CBool) : bool = CBool.op_Implicit(v)

// ─── Orbital Camera ───────────────────────────────────────────

type FpsCamera =
  { mutable Position: Vector3
    mutable Yaw: float32    // horizontal angle (radians)
    mutable Pitch: float32  // vertical angle (radians)
    mutable Fov: float32
    mutable MoveSpeed: float32 }

module FpsCamera =
  let create (pos: Vector3) =
    { Position = pos
      Yaw = MathF.PI * 1.25f  // looking toward origin
      Pitch = -1.40f           // nearly straight down for road layout evaluation
      Fov = 60.0f
      MoveSpeed = 80.0f }

  let forward (cam: FpsCamera) =
    Vector3(MathF.Cos(cam.Pitch) * MathF.Cos(cam.Yaw),
            MathF.Sin(cam.Pitch),
            MathF.Cos(cam.Pitch) * MathF.Sin(cam.Yaw))

  let movementVectors (cam: FpsCamera) =
    let fwd = Vector3.Normalize(forward cam)
    let lateral = Vector3.Cross(fwd, Vector3.UnitY)
    let right =
      if lateral.LengthSquared() > 0.000001f then
        Vector3.Normalize(lateral)
      else
        Vector3(-MathF.Sin(cam.Yaw), 0.0f, MathF.Cos(cam.Yaw))
    (fwd, right)

  let toCamera3D (cam: FpsCamera) =
    let mutable c = Camera3D()
    c.Position <- cam.Position
    c.Target <- cam.Position + forward cam
    c.Up <- Vector3.UnitY
    c.FovY <- cam.Fov
    c.Projection <- CameraProjection.Perspective
    c

  let update (cam: FpsCamera) (captured: bool) =
    let dt = Raylib.GetFrameTime()

    // Mouse look on right-click
    if rb (Raylib.IsMouseButtonDown(MouseButton.Right)) then
      let delta = Raylib.GetMouseDelta()
      cam.Yaw <- cam.Yaw + delta.X * 0.003f
      cam.Pitch <- cam.Pitch - delta.Y * 0.003f
      cam.Pitch <- max -1.4f (min 1.4f cam.Pitch)

    // Scroll adjusts move speed
    let wheel = Raylib.GetMouseWheelMove()
    if wheel <> 0.0f then
      cam.MoveSpeed <- max 10.0f (min 500.0f (cam.MoveSpeed + wheel * 10.0f))

    // WASD + QE movement
    let fwd, right = movementVectors cam
    let speed = cam.MoveSpeed * dt
    if rb (Raylib.IsKeyDown(KeyboardKey.W)) then
      cam.Position <- cam.Position + fwd * speed
    if rb (Raylib.IsKeyDown(KeyboardKey.S)) then
      cam.Position <- cam.Position - fwd * speed
    if rb (Raylib.IsKeyDown(KeyboardKey.A)) then
      cam.Position <- cam.Position - right * speed
    if rb (Raylib.IsKeyDown(KeyboardKey.D)) then
      cam.Position <- cam.Position + right * speed
    if rb (Raylib.IsKeyDown(KeyboardKey.Q)) then
      cam.Position <- cam.Position + Vector3.UnitY * speed
    if rb (Raylib.IsKeyDown(KeyboardKey.E)) then
      cam.Position <- cam.Position - Vector3.UnitY * speed
    // Shift for sprint
    if rb (Raylib.IsKeyDown(KeyboardKey.LeftShift)) then
      let boost = speed * 2.0f
      if rb (Raylib.IsKeyDown(KeyboardKey.W)) then
        cam.Position <- cam.Position + fwd * boost
      if rb (Raylib.IsKeyDown(KeyboardKey.S)) then
        cam.Position <- cam.Position - fwd * boost
      if rb (Raylib.IsKeyDown(KeyboardKey.A)) then
        cam.Position <- cam.Position - right * boost
      if rb (Raylib.IsKeyDown(KeyboardKey.D)) then
        cam.Position <- cam.Position + right * boost

// ─── GPU Mesh-Based Rendering ────────────────────────────────
// ALL static geometry (ground, districts, roads, buildings) is baked into GPU meshes at startup.
// This gives ~5 draw calls per frame instead of 11,000+ immediate-mode DrawCubeV calls.

let skyColor = Color(12uy, 12uy, 28uy, 255uy)

// Module-level inline helpers for writing vertices — Span<T> is byref-like and can't be captured by closures
let inline setV (verts: Span<float32>) i x y z =
  let j = i * 3 in verts.[j] <- x; verts.[j+1] <- y; verts.[j+2] <- z
let inline setN (norms: Span<float32>) i x y z =
  let j = i * 3 in norms.[j] <- x; norms.[j+1] <- y; norms.[j+2] <- z
let inline setC (cols: Span<byte>) i (r: byte) (g: byte) (b: byte) (a: byte) =
  let j = i * 4 in cols.[j] <- r; cols.[j+1] <- g; cols.[j+2] <- b; cols.[j+3] <- a

/// Add a quad (2 triangles = 6 verts) to mesh arrays.
let inline addQuadToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int)
  (ax: float32) (ay: float32) (az: float32)
  (bx: float32) (by: float32) (bz: float32)
  (cx: float32) (cy: float32) (cz: float32)
  (dx: float32) (dy: float32) (dz: float32)
  (nx: float32) (ny: float32) (nz: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  setV verts (vi+0) ax ay az; setN norms (vi+0) nx ny nz; setC cols (vi+0) r g b a
  setV verts (vi+1) bx by bz; setN norms (vi+1) nx ny nz; setC cols (vi+1) r g b a
  setV verts (vi+2) cx cy cz; setN norms (vi+2) nx ny nz; setC cols (vi+2) r g b a
  setV verts (vi+3) ax ay az; setN norms (vi+3) nx ny nz; setC cols (vi+3) r g b a
  setV verts (vi+4) cx cy cz; setN norms (vi+4) nx ny nz; setC cols (vi+4) r g b a
  setV verts (vi+5) dx dy dz; setN norms (vi+5) nx ny nz; setC cols (vi+5) r g b a

let private rotationSinCos (rotationDegrees: float32) =
  let radians = rotationDegrees * MathF.PI / 180.0f
  MathF.Cos radians, MathF.Sin radians

let inline private rotateOffsetXZ (cosA: float32) (sinA: float32) (x: float32) (z: float32) =
  x * cosA - z * sinA, x * sinA + z * cosA

let inline private rotatePointXZ (cx: float32) (cz: float32) (cosA: float32) (sinA: float32) (x: float32) (z: float32) =
  let dx = x - cx
  let dz = z - cz
  let rx, rz = rotateOffsetXZ cosA sinA dx dz
  cx + rx, cz + rz

let inline private rotateNormalXZ (cosA: float32) (sinA: float32) (nx: float32) (nz: float32) =
  rotateOffsetXZ cosA sinA nx nz

let private addRotatedQuadToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int)
  (centerX: float32) (centerZ: float32)
  (cosA: float32) (sinA: float32)
  (ax: float32) (ay: float32) (az: float32)
  (bx: float32) (by: float32) (bz: float32)
  (cx: float32) (cy: float32) (cz: float32)
  (dx: float32) (dy: float32) (dz: float32)
  (nx: float32) (ny: float32) (nz: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  let ax', az' = rotatePointXZ centerX centerZ cosA sinA ax az
  let bx', bz' = rotatePointXZ centerX centerZ cosA sinA bx bz
  let cx', cz' = rotatePointXZ centerX centerZ cosA sinA cx cz
  let dx', dz' = rotatePointXZ centerX centerZ cosA sinA dx dz
  let nx', nz' = rotateNormalXZ cosA sinA nx nz
  addQuadToArrays verts norms cols vi
    ax' ay az'
    bx' by bz'
    cx' cy cz'
    dx' dy dz'
    nx' ny nz'
    r g b a

/// Add a cube (6 faces × 6 verts = 36 verts) to mesh arrays.
let inline addCubeToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int) (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hh: float32) (hd: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  let x0 = cx - hw
  let x1 = cx + hw
  let y0 = cy - hh
  let y1 = cy + hh
  let z0 = cz - hd
  let z1 = cz + hd
  // +Y top
  addQuadToArrays verts norms cols vi x0 y1 z0 x0 y1 z1 x1 y1 z1 x1 y1 z0 0.0f 1.0f 0.0f r g b a
  // -Y bottom
  addQuadToArrays verts norms cols (vi+6) x0 y0 z0 x1 y0 z0 x1 y0 z1 x0 y0 z1 0.0f -1.0f 0.0f r g b a
  // +Z front
  addQuadToArrays verts norms cols (vi+12) x0 y0 z1 x1 y0 z1 x1 y1 z1 x0 y1 z1 0.0f 0.0f 1.0f r g b a
  // -Z back
  addQuadToArrays verts norms cols (vi+18) x1 y0 z0 x0 y0 z0 x0 y1 z0 x1 y1 z0 0.0f 0.0f -1.0f r g b a
  // +X right
  addQuadToArrays verts norms cols (vi+24) x1 y0 z1 x1 y0 z0 x1 y1 z0 x1 y1 z1 1.0f 0.0f 0.0f r g b a
  // -X left
  addQuadToArrays verts norms cols (vi+30) x0 y0 z0 x0 y0 z1 x0 y1 z1 x0 y1 z0 -1.0f 0.0f 0.0f r g b a

let private addOrientedCubeToArraysWithRotation
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int) (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hh: float32) (hd: float32)
  (cosA: float32) (sinA: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  let x0 = cx - hw
  let x1 = cx + hw
  let y0 = cy - hh
  let y1 = cy + hh
  let z0 = cz - hd
  let z1 = cz + hd
  addRotatedQuadToArrays verts norms cols vi
    cx cz cosA sinA
    x0 y1 z0
    x0 y1 z1
    x1 y1 z1
    x1 y1 z0
    0.0f 1.0f 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi+6)
    cx cz cosA sinA
    x0 y0 z0
    x1 y0 z0
    x1 y0 z1
    x0 y0 z1
    0.0f -1.0f 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi+12)
    cx cz cosA sinA
    x0 y0 z1
    x1 y0 z1
    x1 y1 z1
    x0 y1 z1
    0.0f 0.0f 1.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi+18)
    cx cz cosA sinA
    x1 y0 z0
    x0 y0 z0
    x0 y1 z0
    x1 y1 z0
    0.0f 0.0f -1.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi+24)
    cx cz cosA sinA
    x1 y0 z1
    x1 y0 z0
    x1 y1 z0
    x1 y1 z1
    1.0f 0.0f 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi+30)
    cx cz cosA sinA
    x0 y0 z0
    x0 y0 z1
    x0 y1 z1
    x0 y1 z0
    -1.0f 0.0f 0.0f
    r g b a

let addOrientedCubeToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int) (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hh: float32) (hd: float32)
  (rotationDegrees: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  let cosA, sinA = rotationSinCos rotationDegrees
  addOrientedCubeToArraysWithRotation verts norms cols vi cx cy cz hw hh hd cosA sinA r g b a

/// Catmull-Rom control points for a road segment.
let roadControlPoints
  (from: float32 * float32) (to': float32 * float32)
  (prevOpt: (float32 * float32) option) (nextOpt: (float32 * float32) option)
  : (float32 * float32) * (float32 * float32) * (float32 * float32) * (float32 * float32) =
  let (fx, fz) = from
  let (tx, tz) = to'
  let dx, dz = tx - fx, tz - fz
  let p0 = match prevOpt with Some p -> p | None -> (fx - dx, fz - dz)
  let p3 = match nextOpt with Some p -> p | None -> (tx + dx, tz + dz)
  (p0, from, to', p3)

/// Add an oriented flat road quad (top + bottom faces = 12 verts) to the mesh arrays.
/// Roads follow the actual direction from A to B at any angle.
let inline addRoadQuadToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int)
  (fromX: float32) (fromZ: float32) (toX: float32) (toZ: float32)
  (y: float32) (halfH: float32) (halfW: float32)
  (r: byte) (g: byte) (b: byte) (a: byte)
  : int =

  let dx = toX - fromX
  let dz = toZ - fromZ
  let len = MathF.Sqrt(dx * dx + dz * dz)
  if len < 0.01f then 0
  else

  // Perpendicular direction in XZ plane (left of forward)
  let px = -dz / len * halfW
  let pz = dx / len * halfW

  let yTop = y + halfH
  let yBot = y - halfH

  // Top face: 2 triangles, normal (0, 1, 0), CCW winding from above
  // CCW from +Y: from+perp → to+perp → from-perp (cross product gives +Y normal)
  setV verts (vi+0) (fromX+px) yTop (fromZ+pz); setN norms (vi+0) 0.0f 1.0f 0.0f; setC cols (vi+0) r g b a
  setV verts (vi+1) (toX+px)   yTop (toZ+pz);   setN norms (vi+1) 0.0f 1.0f 0.0f; setC cols (vi+1) r g b a
  setV verts (vi+2) (fromX-px) yTop (fromZ-pz); setN norms (vi+2) 0.0f 1.0f 0.0f; setC cols (vi+2) r g b a
  setV verts (vi+3) (toX+px)   yTop (toZ+pz);   setN norms (vi+3) 0.0f 1.0f 0.0f; setC cols (vi+3) r g b a
  setV verts (vi+4) (toX-px)   yTop (toZ-pz);   setN norms (vi+4) 0.0f 1.0f 0.0f; setC cols (vi+4) r g b a
  setV verts (vi+5) (fromX-px) yTop (fromZ-pz); setN norms (vi+5) 0.0f 1.0f 0.0f; setC cols (vi+5) r g b a

  // Bottom face: normal (0, -1, 0), CCW from -Y
  setV verts (vi+6)  (fromX+px) yBot (fromZ+pz); setN norms (vi+6)  0.0f -1.0f 0.0f; setC cols (vi+6)  r g b a
  setV verts (vi+7)  (fromX-px) yBot (fromZ-pz); setN norms (vi+7)  0.0f -1.0f 0.0f; setC cols (vi+7)  r g b a
  setV verts (vi+8)  (toX+px)   yBot (toZ+pz);   setN norms (vi+8)  0.0f -1.0f 0.0f; setC cols (vi+8)  r g b a
  setV verts (vi+9)  (fromX-px) yBot (fromZ-pz); setN norms (vi+9)  0.0f -1.0f 0.0f; setC cols (vi+9)  r g b a
  setV verts (vi+10) (toX-px)   yBot (toZ-pz);   setN norms (vi+10) 0.0f -1.0f 0.0f; setC cols (vi+10) r g b a
  setV verts (vi+11) (toX+px)   yBot (toZ+pz);   setN norms (vi+11) 0.0f -1.0f 0.0f; setC cols (vi+11) r g b a

  12 // verts written

/// Add a curved spline road to the mesh arrays. Each road is subdivided into
/// `segments` quads along a Catmull-Rom curve. Returns total verts written.
let inline addSplineRoadToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int)
  (fromX: float32) (fromZ: float32) (toX: float32) (toZ: float32)
  (y: float32) (halfH: float32) (halfW: float32)
  (r: byte) (g: byte) (b: byte) (a: byte)
  (segments: int)
  : int =
  let (p0, p1, p2, p3) = roadControlPoints (fromX, fromZ) (toX, toZ) None None
  let mutable written = 0
  for i in 0 .. segments - 1 do
    let t0 = float32 i / float32 segments
    let t1 = float32 (i + 1) / float32 segments
    let (cx1, cz1) = catmullRom p0 p1 p2 p3 t0
    let (cx2, cz2) = catmullRom p0 p1 p2 p3 t1
    let dx = cx2 - cx1
    let dz = cz2 - cz1
    let len = MathF.Sqrt(dx * dx + dz * dz)
    if len > 0.001f then
      let nx = -dz / len * halfW
      let nz = dx / len * halfW
      let yTop = y + halfH
      let yBot = y - halfH
      let idx = vi + written
      // Top face (6 verts)
      setV verts (idx+0) (cx1+nx) yTop (cz1+nz); setN norms (idx+0) 0.0f 1.0f 0.0f; setC cols (idx+0) r g b a
      setV verts (idx+1) (cx2+nx) yTop (cz2+nz); setN norms (idx+1) 0.0f 1.0f 0.0f; setC cols (idx+1) r g b a
      setV verts (idx+2) (cx1-nx) yTop (cz1-nz); setN norms (idx+2) 0.0f 1.0f 0.0f; setC cols (idx+2) r g b a
      setV verts (idx+3) (cx2+nx) yTop (cz2+nz); setN norms (idx+3) 0.0f 1.0f 0.0f; setC cols (idx+3) r g b a
      setV verts (idx+4) (cx2-nx) yTop (cz2-nz); setN norms (idx+4) 0.0f 1.0f 0.0f; setC cols (idx+4) r g b a
      setV verts (idx+5) (cx1-nx) yTop (cz1-nz); setN norms (idx+5) 0.0f 1.0f 0.0f; setC cols (idx+5) r g b a
      // Bottom face (6 verts)
      setV verts (idx+6)  (cx1+nx) yBot (cz1+nz); setN norms (idx+6)  0.0f -1.0f 0.0f; setC cols (idx+6)  r g b a
      setV verts (idx+7)  (cx1-nx) yBot (cz1-nz); setN norms (idx+7)  0.0f -1.0f 0.0f; setC cols (idx+7)  r g b a
      setV verts (idx+8)  (cx2+nx) yBot (cz2+nz); setN norms (idx+8)  0.0f -1.0f 0.0f; setC cols (idx+8)  r g b a
      setV verts (idx+9)  (cx1-nx) yBot (cz1-nz); setN norms (idx+9)  0.0f -1.0f 0.0f; setC cols (idx+9)  r g b a
      setV verts (idx+10) (cx2-nx) yBot (cz2-nz); setN norms (idx+10) 0.0f -1.0f 0.0f; setC cols (idx+10) r g b a
      setV verts (idx+11) (cx2+nx) yBot (cz2+nz); setN norms (idx+11) 0.0f -1.0f 0.0f; setC cols (idx+11) r g b a
      written <- written + 12
  written

// ─── Array-based wrappers for testing (same logic, Array instead of Span) ─────

let inline setVArr (verts: float32[]) i x y z =
  let j = i * 3 in verts.[j] <- x; verts.[j+1] <- y; verts.[j+2] <- z
let inline setNArr (norms: float32[]) i x y z =
  let j = i * 3 in norms.[j] <- x; norms.[j+1] <- y; norms.[j+2] <- z
let inline setCArr (cols: byte[]) i (r: byte) (g: byte) (b: byte) (a: byte) =
  let j = i * 4 in cols.[j] <- r; cols.[j+1] <- g; cols.[j+2] <- b; cols.[j+3] <- a

let addCubeToArraysArr
  (verts: float32[]) (norms: float32[]) (cols: byte[])
  (vi: int) (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hh: float32) (hd: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  let v = verts.AsSpan()
  let n = norms.AsSpan()
  let c = cols.AsSpan()
  addCubeToArrays v n c vi cx cy cz hw hh hd r g b a

let addOrientedCubeToArraysArr
  (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hh: float32) (hd: float32)
  (rotationDegrees: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) : float32[] * float32[] * byte[] =
  let v = Array.zeroCreate<float32> (36 * 3)
  let n = Array.zeroCreate<float32> (36 * 3)
  let c = Array.zeroCreate<byte>    (36 * 4)
  addOrientedCubeToArrays (v.AsSpan()) (n.AsSpan()) (c.AsSpan()) 0 cx cy cz hw hh hd rotationDegrees r g b a
  v, n, c

let addRoadQuadToArraysArr
  (verts: float32[]) (norms: float32[]) (cols: byte[])
  (vi: int)
  (fromX: float32) (fromZ: float32) (toX: float32) (toZ: float32)
  (y: float32) (halfH: float32) (halfW: float32)
  (r: byte) (g: byte) (b: byte) (a: byte)
  : int =
  let v = verts.AsSpan()
  let n = norms.AsSpan()
  let c = cols.AsSpan()
  addRoadQuadToArrays v n c vi fromX fromZ toX toZ y halfH halfW r g b a

/// Fan-tessellate a convex hull polygon into the GPU mesh (top + bottom faces).
/// Returns number of vertices written.
let addHullSlabToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int) (hull: (float32 * float32) list)
  (y: float32) (thickness: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) : int =
  match hull with
  | [] | [_] | [_; _] -> 0
  | _ ->
    let mutable idx = vi
    let yTop = y + thickness * 0.5f
    let yBot = y - thickness * 0.5f
    let p0x, p0z = hull.[0]
    for i in 1 .. hull.Length - 2 do
      let pix, piz = hull.[i]
      let pjx, pjz = hull.[i + 1]
      // Top face (normal up) — pass vertex index directly; setV/setN/setC multiply internally
      setV verts idx p0x yTop p0z
      setN norms idx 0.0f 1.0f 0.0f
      setC cols idx r g b a
      idx <- idx + 1
      setV verts idx pix yTop piz
      setN norms idx 0.0f 1.0f 0.0f
      setC cols idx r g b a
      idx <- idx + 1
      setV verts idx pjx yTop pjz
      setN norms idx 0.0f 1.0f 0.0f
      setC cols idx r g b a
      idx <- idx + 1
      // Bottom face (normal down, reversed winding)
      setV verts idx p0x yBot p0z
      setN norms idx 0.0f -1.0f 0.0f
      setC cols idx r g b a
      idx <- idx + 1
      setV verts idx pjx yBot pjz
      setN norms idx 0.0f -1.0f 0.0f
      setC cols idx r g b a
      idx <- idx + 1
      setV verts idx pix yBot piz
      setN norms idx 0.0f -1.0f 0.0f
      setC cols idx r g b a
      idx <- idx + 1
    idx - vi

/// Write body sub-cubes for a compound building. Each SubCube gets its own height.
/// cx, cz = building center (world coords); baseH = base building height.
/// Returns the number of vertices written (36 per sub-cube).
let addCompoundBody
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int) (cubes: SubCube[])
  (cx: float32) (cz: float32) (baseH: float32)
  (cosA: float32) (sinA: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) : int =
  let mutable off = 0
  for cube in cubes do
    let h = baseH * cube.HeightScale
    let cy = h / 2.0f + 0.02f
    let hh = h / 2.0f
    let offX, offZ = rotateOffsetXZ cosA sinA cube.CX cube.CZ
    addOrientedCubeToArraysWithRotation verts norms cols (vi + off)
      (cx + offX) cy (cz + offZ) cube.HW hh cube.HD cosA sinA r g b a
    off <- off + 36
  off

/// Write a pitched gable roof for a single sub-cube position (always 36 verts).
/// Faces: left slope, right slope, front gable (degenerate tri), back gable, bottom, ridge cap.
/// Ridge runs along Z; pitch is in X direction.
let addGableToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int)
  (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hd: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  let cosA, sinA = rotationSinCos 0.0f
  let apexH = hw * 0.6f
  let apexY = cy + apexH
  let slopeLen = MathF.Sqrt(apexH * apexH + hw * hw)
  let snx = apexH / slopeLen   // right-slope normal X
  let sny = hw    / slopeLen   // both slopes normal Y
  addRotatedQuadToArrays verts norms cols (vi + 0)
    cx cz cosA sinA
    (cx-hw) cy    (cz-hd)   (cx-hw) cy    (cz+hd)
    (cx)    apexY (cz+hd)   (cx)    apexY (cz-hd)
    (-snx) sny 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 6)
    cx cz cosA sinA
    (cx+hw) cy    (cz-hd)   (cx)    apexY (cz-hd)
    (cx)    apexY (cz+hd)   (cx+hw) cy    (cz+hd)
    snx sny 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 12)
    cx cz cosA sinA
    (cx+hw) cy    (cz-hd)   (cx-hw) cy    (cz-hd)
    (cx)    apexY (cz-hd)   (cx)    apexY (cz-hd)
    0.0f 0.0f -1.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 18)
    cx cz cosA sinA
    (cx-hw) cy    (cz+hd)   (cx+hw) cy    (cz+hd)
    (cx)    apexY (cz+hd)   (cx)    apexY (cz+hd)
    0.0f 0.0f 1.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 24)
    cx cz cosA sinA
    (cx-hw) cy (cz-hd)   (cx+hw) cy (cz-hd)
    (cx+hw) cy (cz+hd)   (cx-hw) cy (cz+hd)
    0.0f -1.0f 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 30)
    cx cz cosA sinA
    (cx-0.025f) apexY (cz-hd)   (cx+0.025f) apexY (cz-hd)
    (cx+0.025f) apexY (cz+hd)   (cx-0.025f) apexY (cz+hd)
    0.0f 1.0f 0.0f
    r g b a

let private addOrientedGableToArraysWithRotation
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int)
  (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hd: float32)
  (cosA: float32) (sinA: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  let apexH = hw * 0.6f
  let apexY = cy + apexH
  let slopeLen = MathF.Sqrt(apexH * apexH + hw * hw)
  let snx = apexH / slopeLen   // right-slope normal X
  let sny = hw    / slopeLen   // both slopes normal Y
  addRotatedQuadToArrays verts norms cols (vi + 0)
    cx cz cosA sinA
    (cx-hw) cy    (cz-hd)   (cx-hw) cy    (cz+hd)
    (cx)    apexY (cz+hd)   (cx)    apexY (cz-hd)
    (-snx) sny 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 6)
    cx cz cosA sinA
    (cx+hw) cy    (cz-hd)   (cx)    apexY (cz-hd)
    (cx)    apexY (cz+hd)   (cx+hw) cy    (cz+hd)
    snx sny 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 12)
    cx cz cosA sinA
    (cx+hw) cy    (cz-hd)   (cx-hw) cy    (cz-hd)
    (cx)    apexY (cz-hd)   (cx)    apexY (cz-hd)
    0.0f 0.0f -1.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 18)
    cx cz cosA sinA
    (cx-hw) cy    (cz+hd)   (cx+hw) cy    (cz+hd)
    (cx)    apexY (cz+hd)   (cx)    apexY (cz+hd)
    0.0f 0.0f 1.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 24)
    cx cz cosA sinA
    (cx-hw) cy (cz-hd)   (cx+hw) cy (cz-hd)
    (cx+hw) cy (cz+hd)   (cx-hw) cy (cz+hd)
    0.0f -1.0f 0.0f
    r g b a
  addRotatedQuadToArrays verts norms cols (vi + 30)
    cx cz cosA sinA
    (cx-0.025f) apexY (cz-hd)   (cx+0.025f) apexY (cz-hd)
    (cx+0.025f) apexY (cz+hd)   (cx-0.025f) apexY (cz+hd)
    0.0f 1.0f 0.0f
    r g b a

let addOrientedGableToArrays
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int)
  (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hd: float32)
  (rotationDegrees: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) =
  let cosA, sinA = rotationSinCos rotationDegrees
  addOrientedGableToArraysWithRotation verts norms cols vi cx cy cz hw hd cosA sinA r g b a

/// Array-backed wrapper for addGableToArrays (testing only — no GPU allocation).
/// Returns (positionFloats, normalFloats, colorBytes) for 36 verts.
let addGableToArraysArr
  (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hd: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) : float32[] * float32[] * byte[] =
  let v = Array.zeroCreate<float32> (36 * 3)
  let n = Array.zeroCreate<float32> (36 * 3)
  let c = Array.zeroCreate<byte>    (36 * 4)
  addGableToArrays (v.AsSpan()) (n.AsSpan()) (c.AsSpan()) 0 cx cy cz hw hd r g b a
  v, n, c

let addOrientedGableToArraysArr
  (cx: float32) (cy: float32) (cz: float32)
  (hw: float32) (hd: float32)
  (rotationDegrees: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) : float32[] * float32[] * byte[] =
  let v = Array.zeroCreate<float32> (36 * 3)
  let n = Array.zeroCreate<float32> (36 * 3)
  let c = Array.zeroCreate<byte>    (36 * 4)
  addOrientedGableToArrays (v.AsSpan()) (n.AsSpan()) (c.AsSpan()) 0 cx cy cz hw hd rotationDegrees r g b a
  v, n, c

/// Write roof caps for a compound building (matching each sub-cube).
/// Returns the number of vertices written (36 per sub-cube).
let addCompoundRoof
  (verts: Span<float32>) (norms: Span<float32>) (cols: Span<byte>)
  (vi: int) (cubes: SubCube[])
  (cx: float32) (cz: float32) (baseH: float32) (roofHH: float32)
  (cosA: float32) (sinA: float32)
  (r: byte) (g: byte) (b: byte) (a: byte) : int =
  let pad = 0.02f
  let mutable off = 0
  for cube in cubes do
    let h = baseH * cube.HeightScale
    let roofY = h + 0.06f
    let offX, offZ = rotateOffsetXZ cosA sinA cube.CX cube.CZ
    addOrientedCubeToArraysWithRotation verts norms cols (vi + off)
      (cx + offX) roofY (cz + offZ)
      (cube.HW + pad) roofHH (cube.HD + pad) cosA sinA r g b a
    off <- off + 36
  off

/// Ray-box intersection test for mouse picking
let rayIntersectsBox (ray: Ray) (b: FuncBuilding) : float32 option =
  let bmin = Vector3(b.X, 0.5f, b.Z)
  let bmax = Vector3(b.X + b.W, 0.5f + b.H, b.Z + b.D)
  let bb = BoundingBox(bmin, bmax)
  let collision = Raylib.GetRayCollisionBox(ray, bb)
  if CBool.op_Implicit(collision.Hit) then Some collision.Distance
  else None

/// Build GPU mesh: layered luminance (panel Q5) — ground → road → sidewalk → block fill → curbs → buildings
/// Number of spline segments to use for a road of the given length and organic factor.
/// organic=0 → 1 segment (straight); organic=1 → 4× more segments (very curved).
let segmentCountForOrganic (roadLen: float32) (organic: float32) : int =
  let lengthSegs = max 1 (int (roadLen / 1.5f))
  let organicMult = max 1 (int (organic * 3.0f + 1.0f))
  max 1 (lengthSegs * organicMult)

let buildStaticMesh (buildings: FuncBuilding[]) (blocks: ModuleBlock[]) (cityExtent: float32) (alleyRoads: Road list) =
  // Pre-compute compound shapes for all buildings (deterministic from function name)
  let compounds =
    buildings |> Array.map (fun b ->
      generateCompound b.Func.QualifiedName b.Complexity (b.W / 2.0f) (b.D / 2.0f))
  let totalBuildingCubes = compounds |> Array.sumBy (fun c -> c.Length)
  let maxCubes = compounds |> Array.map (fun c -> c.Length) |> Array.max
  let avgCubes = float totalBuildingCubes / float buildings.Length
  printfn "Compound shapes: %d total cubes (avg %.1f, max %d per building), %d verts"
    totalBuildingCubes avgCubes maxCubes (totalBuildingCubes * 72)

  let buildingsByDistrict =
    buildings
    |> Array.groupBy _.District
    |> Map.ofArray

  let blockSurfaceHulls =
    blocks
    |> Array.map (fun block ->
      let blockBuildings =
        buildingsByDistrict
        |> Map.tryFind block.Module
        |> Option.defaultValue [||]
        |> Array.toList
      let localRoads =
        alleyRoads
        |> List.filter (fun road ->
          let startPt = Vec2.Create(road.FromPos.X, road.FromPos.Z)
          let endPt = Vec2.Create(road.ToPos.X, road.ToPos.Z)
          clipSegmentToRect block.Rect startPt endPt |> Option.isSome)
      block, computeBlockSurfaceHull block blockBuildings localRoads

      )

  let hullEdgeCount (hull: (float32 * float32) list) =
    match hull.Length < 2 with
    | true -> 0
    | false -> hull.Length

  let hullFillVerts (hull: (float32 * float32) list) =
    max 0 (hull.Length - 2) * 6

  // Vertex counts per layer:
  let groundVerts = 6 + 6    // dark ground + city-wide road surface
  let blockFillVerts = blockSurfaceHulls |> Array.sumBy (fun (_, hull) -> hullFillVerts hull)
  // Spline roads: per segment = 12 verts (top+bottom quad strip), bounded by road.Organic
  let alleyVerts =
    alleyRoads |> List.sumBy (fun road ->
      let x1 = road.FromPos.X
      let z1 = road.FromPos.Z
      let x2 = road.ToPos.X
      let z2 = road.ToPos.Z
      let len = MathF.Sqrt((x2-x1)*(x2-x1) + (z2-z1)*(z2-z1))
      segmentCountForOrganic len road.Organic * 12)
  let sidewalkVerts = blockSurfaceHulls |> Array.sumBy (fun (_, hull) -> hullEdgeCount hull * 12)
  let curbVerts = blockSurfaceHulls |> Array.sumBy (fun (_, hull) -> hullEdgeCount hull * 36)
  let buildingVerts = totalBuildingCubes * 72 // body (36) + roof (36) per sub-cube
  let totalVerts = groundVerts + blockFillVerts + alleyVerts + sidewalkVerts + curbVerts + buildingVerts

  let mutable mesh = Mesh()
  mesh.VertexCount <- totalVerts
  mesh.TriangleCount <- totalVerts / 3

  let verts = Array.zeroCreate<float32>(totalVerts * 3)
  let norms = Array.zeroCreate<float32>(totalVerts * 3)
  let cols = Array.zeroCreate<byte>(totalVerts * 4)
  let v = verts.AsSpan()
  let n = norms.AsSpan()
  let c = cols.AsSpan()

  let mutable vi = 0

  // Layer 0: Ground plane — very dark base, visible at city edges (luminance ~0.08)
  let gs = cityExtent * 1.5f |> max 200.0f
  addQuadToArrays v n c vi (-gs) -0.01f (-gs) (-gs) -0.01f gs gs -0.01f gs gs -0.01f (-gs) 0.0f 1.0f 0.0f 20uy 20uy 24uy 255uy
  vi <- vi + 6

  // Layer 1: Road surface — lightest ground, covering full city extent (luminance ~0.28)
  // The gaps between treemap cells naturally become visible roads
  let rs = cityExtent * 1.1f |> max 150.0f
  addQuadToArrays v n c vi (-rs) 0.005f (-rs) (-rs) 0.005f rs rs 0.005f rs rs 0.005f (-rs) 0.0f 1.0f 0.0f 65uy 65uy 70uy 255uy
  vi <- vi + 6

  let sidewalkW = 0.3f
  let curbH = 0.08f
  let curbW = 0.05f

  // Layer 2: Block fill — road/frontage-derived hulls instead of rectangular module slabs
  for (_, hull) in blockSurfaceHulls do
    let written = addHullSlabToArrays v n c vi hull 0.015f 0.004f 30uy 30uy 34uy 255uy
    vi <- vi + written

  // Layer 2.5: Internal road surfaces — spline quads for organic roads, straight for grid roads
  // Half-width packed in road.FromPos.Y by layoutWeberDistrict
  for road in alleyRoads do
    let hw = road.FromPos.Y
    let x1 = road.FromPos.X
    let z1 = road.FromPos.Z
    let x2 = road.ToPos.X
    let z2 = road.ToPos.Z
    let dx = x2 - x1
    let dz = z2 - z1
    let len = MathF.Sqrt(dx * dx + dz * dz)
    if hw > 0.001f && len > 0.01f then
      let segs = segmentCountForOrganic len road.Organic
      let written = addSplineRoadToArrays v n c vi x1 z1 x2 z2 0.020f 0.003f hw road.Color.R road.Color.G road.Color.B 255uy segs
      vi <- vi + written

  // Layer 3: Sidewalk strips — narrow ribbons following the frontage-derived hull
  for (_, hull) in blockSurfaceHulls do
    if hull.Length >= 2 then
      for i in 0 .. hull.Length - 1 do
        let x1, z1 = hull.[i]
        let x2, z2 = hull.[(i + 1) % hull.Length]
        let written = addRoadQuadToArrays v n c vi x1 z1 x2 z2 0.01f 0.002f (sidewalkW / 2.0f) 48uy 48uy 52uy 255uy
        vi <- vi + written

  // Layer 4: Curb geometry — thin raised strips tracing the same frontage-derived hull
  for (_, hull) in blockSurfaceHulls do
    if hull.Length >= 2 then
      for i in 0 .. hull.Length - 1 do
        let x1, z1 = hull.[i]
        let x2, z2 = hull.[(i + 1) % hull.Length]
        let dx = x2 - x1
        let dz = z2 - z1
        let len = MathF.Sqrt(dx * dx + dz * dz)
        if len > 0.05f then
          let angle = MathF.Atan2(dz, dx) * 180.0f / MathF.PI
          addOrientedCubeToArrays v n c vi
            ((x1 + x2) / 2.0f) (curbH / 2.0f) ((z1 + z2) / 2.0f)
            (len / 2.0f) (curbH / 2.0f) (curbW / 2.0f)
            angle
            42uy 42uy 46uy 255uy
          vi <- vi + 36

  // Layer 5: Buildings — procedural compound body + shape-matching roof cap
  for i in 0 .. buildings.Length - 1 do
    let b = buildings.[i]
    let compound = compounds.[i]
    let cx = b.X + b.W / 2.0f
    let cz = b.Z + b.D / 2.0f
    let cosA, sinA = rotationSinCos b.Rotation
    let bodyH = b.H - 0.08f |> max 0.2f
    // Encode building type in alpha channel for per-type GLSL window profiles + glass specular
    let btAlpha = BuildingType.alpha b.BuildingType
    let vertsAdded =
      addCompoundBody v n c vi compound cx cz bodyH cosA sinA
        b.Color.R b.Color.G b.Color.B btAlpha
    vi <- vi + vertsAdded
    // Roof: Shed/Cottage get a pitched gable on the main sub-cube; all others use flat slabs
    let roofHH = 0.04f
    let roofVerts =
      match b.BuildingType with
      | Shed | Cottage ->
        let main = compound.[0]
        let mainH = bodyH * main.HeightScale
        let eaveCy = mainH + 0.06f
        let pad = 0.02f
        let mainOffX, mainOffZ = rotateOffsetXZ cosA sinA main.CX main.CZ
        addOrientedGableToArraysWithRotation v n c vi
          (cx + mainOffX) eaveCy (cz + mainOffZ)
          (main.HW + pad) (main.HD + pad) cosA sinA
          b.RoofColor.R b.RoofColor.G b.RoofColor.B 255uy
        let wingsVerts =
          if compound.Length > 1 then
            addCompoundRoof v n c (vi+36) compound.[1..] cx cz bodyH roofHH cosA sinA
              b.RoofColor.R b.RoofColor.G b.RoofColor.B 255uy
          else 0
        36 + wingsVerts
      | _ ->
        addCompoundRoof v n c vi compound cx cz bodyH roofHH cosA sinA
          b.RoofColor.R b.RoofColor.G b.RoofColor.B 255uy
    vi <- vi + roofVerts

  // Trim to actual vertex count
  mesh.VertexCount <- vi
  mesh.TriangleCount <- vi / 3

  // Upload to GPU
  mesh.AllocVertices()
  mesh.AllocNormals()
  mesh.AllocColors()
  System.Runtime.InteropServices.Marshal.Copy(verts, 0, NativeInterop.NativePtr.toNativeInt mesh.Vertices, vi * 3)
  System.Runtime.InteropServices.Marshal.Copy(norms, 0, NativeInterop.NativePtr.toNativeInt mesh.Normals, vi * 3)
  System.Runtime.InteropServices.Marshal.Copy(cols, 0, NativeInterop.NativePtr.toNativeInt mesh.Colors, vi * 4)

  Raylib.UploadMesh(&mesh, false)
  mesh

// ─── Color Utilities ──────────────────────────────────────────

let darken (c: Color) (factor: float32) =
  Color(byte (float32 c.R * factor), byte (float32 c.G * factor),
        byte (float32 c.B * factor), c.A)

let brighten (c: Color) (factor: float32) =
  Color(byte (min 255.0f (float32 c.R * factor)),
        byte (min 255.0f (float32 c.G * factor)),
        byte (min 255.0f (float32 c.B * factor)), c.A)

let heatColor (t: float32) =
  let t = max 0.0f (min 1.0f t)
  if t < 0.5f then
    let s = t * 2.0f
    Color(byte (40.0f + s * 200.0f), byte (80.0f - s * 40.0f), byte (200.0f - s * 160.0f), 255uy)
  else
    let s = (t - 0.5f) * 2.0f
    Color(byte (240.0f + s * 15.0f), byte (40.0f - s * 30.0f), byte (40.0f - s * 30.0f), 255uy)

type UiTextTheme =
  { HudTitle: int
    HudStats: int
    HudControls: int
    HudCapture: int
    LegendTitle: int
    LegendEntry: int
    HeatTitle: int
    HeatLabel: int
    TooltipTitle: int
    TooltipBody: int
    SelectionTitle: int
    SelectionBody: int
    SelectionLineHeight: int
    DistrictTitle: int
    DistrictSubtitle: int
    Status: int }

type ScreenRect =
  { X: int
    Y: int
    W: int
    H: int }

type DistrictLabelStyle =
  | CompactDistrictLabel
  | DetailedDistrictLabel

type DistrictLabelCandidate =
  { District: District
    ScreenPos: Vector2
    ProjectedArea: float32
    CameraDistance: float32 }

type DistrictLabelPlacement =
  { District: District
    Bounds: ScreenRect
    Style: DistrictLabelStyle
    Title: string
    Subtitle: string option
    Priority: float32 }

let defaultUiTextTheme =
  { HudTitle = 28
    HudStats = 20
    HudControls = 16
    HudCapture = 17
    LegendTitle = 21
    LegendEntry = 18
    HeatTitle = 17
    HeatLabel = 15
    TooltipTitle = 20
    TooltipBody = 16
    SelectionTitle = 20
    SelectionBody = 16
    SelectionLineHeight = 24
    DistrictTitle = 24
    DistrictSubtitle = 16
    Status = 16 }

let compactUiTextTheme (theme: UiTextTheme) =
  { theme with
      HudTitle = 22
      HudStats = 16
      HudControls = 13
      HudCapture = 14
      LegendTitle = 17
      LegendEntry = 14
      HeatTitle = 15
      HeatLabel = 12
      TooltipTitle = 18
      TooltipBody = 14
      SelectionTitle = 18
      SelectionBody = 14
      SelectionLineHeight = 20
      DistrictTitle = 18
      DistrictSubtitle = 14
      Status = 13 }

let estimateUiTextWidth (size: int) (text: string) =
  match String.IsNullOrWhiteSpace text with
  | true -> 0
  | false -> int (MathF.Ceiling(float32 text.Length * float32 size * 0.58f))

let private screenRectOverlaps padding (a: ScreenRect) (b: ScreenRect) =
  not (
    a.X + a.W + padding <= b.X
    || b.X + b.W + padding <= a.X
    || a.Y + a.H + padding <= b.Y
    || b.Y + b.H + padding <= a.Y)

let maxDistrictLabelCount screenW screenH =
  let area = screenW * screenH
  if area >= 2560 * 1440 then 14
  elif area >= 1920 * 1080 then 12
  else 9

let summarizeLegendDistricts maxEntries (districts: District list) =
  let visible =
    districts
    |> List.sortByDescending (fun district -> district.FuncCount, district.TotalLines, district.Name)
    |> List.truncate (max 0 maxEntries)
  let hiddenCount = max 0 (districts.Length - visible.Length)
  visible, hiddenCount

let private districtLabelStyle (candidate: DistrictLabelCandidate) =
  if candidate.ProjectedArea >= 65000.0f && candidate.CameraDistance <= 260.0f then DetailedDistrictLabel
  else CompactDistrictLabel

let private districtLabelPriority screenW screenH (candidate: DistrictLabelCandidate) =
  let viewportCenter = Vector2(float32 screenW * 0.5f, float32 screenH * 0.5f)
  let diagonal = max 1.0f (viewportCenter.Length())
  let centerBias =
    1.0f - min 1.0f (Vector2.Distance(candidate.ScreenPos, viewportCenter) / diagonal)
  candidate.ProjectedArea
  + float32 candidate.District.FuncCount * 220.0f
  + float32 candidate.District.TotalLines * 0.09f
  + centerBias * 6000.0f
  - candidate.CameraDistance * 18.0f

let planDistrictLabelPlacements screenW screenH (theme: UiTextTheme) (candidates: DistrictLabelCandidate list) =
  let placementFor candidate =
    let style = districtLabelStyle candidate
    let title = candidate.District.Name
    let subtitle =
      match style with
      | DetailedDistrictLabel -> Some (sprintf "%d funcs · %d LOC" candidate.District.FuncCount candidate.District.TotalLines)
      | CompactDistrictLabel -> None
    let titleSize =
      match style with
      | DetailedDistrictLabel -> theme.DistrictTitle
      | CompactDistrictLabel -> theme.DistrictSubtitle
    let titleWidth = estimateUiTextWidth titleSize title
    let subtitleWidth =
      subtitle
      |> Option.map (estimateUiTextWidth theme.DistrictSubtitle)
      |> Option.defaultValue 0
    let width =
      match subtitle with
      | Some _ -> max titleWidth subtitleWidth + 18
      | None -> titleWidth + 16
    let height =
      match subtitle with
      | Some _ -> theme.DistrictTitle + theme.DistrictSubtitle + 14
      | None -> theme.DistrictSubtitle + 10
    { District = candidate.District
      Bounds =
        { X = int candidate.ScreenPos.X - width / 2
          Y = int candidate.ScreenPos.Y - height / 2
          W = width
          H = height }
      Style = style
      Title = title
      Subtitle = subtitle
      Priority = districtLabelPriority screenW screenH candidate }

  let maxLabels = maxDistrictLabelCount screenW screenH
  let eligible =
    candidates
    |> List.filter (fun candidate ->
      candidate.ProjectedArea >= 3000.0f
      && candidate.CameraDistance <= 900.0f)
    |> List.map placementFor
    |> List.sortByDescending _.Priority

  let rec choose (accepted: DistrictLabelPlacement list) remaining =
    match List.length accepted >= maxLabels, remaining with
    | true, _
    | _, [] -> List.rev accepted
    | false, next :: rest ->
        let overlaps =
          accepted
          |> List.exists (fun acceptedPlacement -> screenRectOverlaps 10 next.Bounds acceptedPlacement.Bounds)
        match overlaps with
        | true -> choose accepted rest
        | false -> choose (next :: accepted) rest

  choose [] eligible

type SsaoSettings =
  { BufferScale: int
    Radius: float32
    Bias: float32
    Strength: float32 }

let defaultSsaoSettings =
  { BufferScale = 1
    Radius = 1.8f
    Bias = 0.03f
    Strength = 0.55f }

let ssaoBufferSize (settings: SsaoSettings) (screenW: int) (screenH: int) =
  let scale = max 1 settings.BufferScale
  (max 1 (screenW / scale), max 1 (screenH / scale))

let uiTextShadow = Color(0uy, 0uy, 0uy, 210uy)

let mutable uiFont: Font = Unchecked.defaultof<Font>

let measureUiText (text: string) (size: int) =
  if uiFont.BaseSize > 0 then
    int (Raylib.MeasureTextEx(uiFont, text, float32 size, float32 size * 0.06f).X)
  else
    Raylib.MeasureText(text, size)

let drawUiText (text: string) (x: int) (y: int) (size: int) (color: Color) =
  if uiFont.BaseSize > 0 then
    let fsize   = float32 size
    let spacing = fsize * 0.06f
    Raylib.DrawTextEx(uiFont, text, Vector2(float32 x + 1.5f, float32 y + 1.5f), fsize, spacing, uiTextShadow)
    Raylib.DrawTextEx(uiFont, text, Vector2(float32 x, float32 y), fsize, spacing, color)
  else
    Raylib.DrawText(text, x + 1, y + 1, size, uiTextShadow)
    Raylib.DrawText(text, x, y, size, color)

let selectedBuildingColor = Color(255uy, 225uy, 120uy, 255uy)
let incomingRelationColor = Color(90uy, 180uy, 255uy, 255uy)
let outgoingRelationColor = Color(255uy, 150uy, 90uy, 255uy)

let drawBuildingOutline (b: FuncBuilding) (pad: float32) (color: Color) =
  let center = buildingCenter b
  Raylib.DrawCubeWires(center, b.W + pad, b.H + pad, b.D + pad, color)

/// Cylinder radius for a relationship arc, scaled by normalized call weight [0,1].
/// Monotone increasing; bounded [0.02, 0.08] so arcs are visible but not overwhelming.
let arcRadius (normalizedWeight: float32) : float32 =
  0.02f + 0.06f * (max 0.0f (min 1.0f normalizedWeight))

/// Arc apex height based on spatial distance and call weight.
/// Heavier relationships arc higher so they don't visually collide with lighter ones.
let arcHeight (dist: float32) (normalizedWeight: float32) : float32 =
  let w = max 0.0f (min 1.0f normalizedWeight)
  (2.5f + dist * 0.18f) * (1.0f + w * 0.4f)

/// Returns true if a district label at the given screen position should be rendered.
/// Clips off-screen and behind-camera labels.
let shouldRenderLabel (screenPos: Vector2) (screenW: int) (screenH: int) (inFront: bool) : bool =
  inFront
  && screenPos.X >= -50.0f && screenPos.X <= float32 screenW + 50.0f
  && screenPos.Y >= -50.0f && screenPos.Y <= float32 screenH + 50.0f

let drawRelationArc (fromPos: Vector3) (toPos: Vector3) (normalizedWeight: float32) (color: Color) =
  let segments = 16
  let dist = Vector3.Distance(fromPos, toPos)
  let apex = arcHeight dist normalizedWeight
  let radius = arcRadius normalizedWeight
  let pointAt (t: float32) =
    let basePoint: Vector3 = Vector3.Lerp(fromPos, toPos, t)
    let lift = MathF.Sin(t * MathF.PI) * apex
    Vector3(basePoint.X, basePoint.Y + lift, basePoint.Z)
  let mutable prev = pointAt 0.0f
  for i in 1 .. segments do
    let t = float32 i / float32 segments
    let next = pointAt t
    let mid = Vector3.Lerp(prev, next, 0.5f)
    Raylib.DrawCylinder(mid, radius, radius, Vector3.Distance(prev, next) * 1.05f, 5, color)
    prev <- next
  // Arrowhead cone at destination — topRadius=0 makes it a cone pointing upward at toPos
  Raylib.DrawCylinder(toPos, 0.0f, radius * 2.5f, radius * 4.0f, 8, color)

let drawSelectionOverlay
  (hovered: FuncBuilding option)
  (selected: FuncBuilding option)
  (incoming: RelatedBuilding list)
  (outgoing: RelatedBuilding list)
  (showCallLinks: bool) =

  match selected with
  | Some current ->
    drawBuildingOutline current 0.38f selectedBuildingColor
    drawBuildingOutline current 0.14f Color.White

    incoming
    |> List.iter (fun rel ->
      drawBuildingOutline rel.Building 0.18f incomingRelationColor)

    outgoing
    |> List.iter (fun rel ->
      drawBuildingOutline rel.Building 0.30f outgoingRelationColor)

    if showCallLinks then
      let currentRoof = buildingRoofCenter current

      let maxInWeight = incoming |> List.map (fun r -> r.Weight) |> List.append [1] |> List.max
      let maxOutWeight = outgoing |> List.map (fun r -> r.Weight) |> List.append [1] |> List.max

      incoming
      |> List.iter (fun rel ->
        let w = float32 rel.Weight / float32 maxInWeight
        drawRelationArc (buildingRoofCenter rel.Building) currentRoof w incomingRelationColor)

      outgoing
      |> List.iter (fun rel ->
        let w = float32 rel.Weight / float32 maxOutWeight
        drawRelationArc currentRoof (buildingRoofCenter rel.Building) w outgoingRelationColor)
  | None -> ()

  match hovered with
  | Some hoveredBuilding ->
    let hoveredName = hoveredBuilding.Func.QualifiedName
    let selectedName =
      selected
      |> Option.map (fun current -> current.Func.QualifiedName)
    if selectedName <> Some hoveredName then
      drawBuildingOutline hoveredBuilding 0.18f (Color(255uy, 255uy, 100uy, 255uy))
  | None -> ()

// ─── 2D Overlays ──────────────────────────────────────────────

let drawDistrictLabels2D
  (districtRects: (District * Rect2D * (float32 * float32) list) list)
  (camera: Camera3D)
  (theme: UiTextTheme) =
  let sw = Raylib.GetScreenWidth()
  let sh = Raylib.GetScreenHeight()
  let camForward = Vector3.Normalize(camera.Target - camera.Position)
  let projectedArea (rect: Rect2D) =
    let corners =
      [ Vector3(rect.X, 1.0f, rect.Z)
        Vector3(rect.X + rect.W, 1.0f, rect.Z)
        Vector3(rect.X, 1.0f, rect.Z + rect.D)
        Vector3(rect.X + rect.W, 1.0f, rect.Z + rect.D) ]
      |> List.map (fun corner -> Raylib.GetWorldToScreen(corner, camera))
    let xs = corners |> List.map _.X
    let ys = corners |> List.map _.Y
    let width = (xs |> List.max) - (xs |> List.min)
    let height = (ys |> List.max) - (ys |> List.min)
    max 0.0f width * max 0.0f height

  let candidates =
    districtRects
    |> List.choose (fun (district, rect, _) ->
      let center3D = Vector3(rect.X + rect.W / 2.0f, 1.0f, rect.Z + rect.D / 2.0f)
      let inFront = Vector3.Dot(center3D - camera.Position, camForward) > 1.0f
      let screenPos = Raylib.GetWorldToScreen(center3D, camera)
      if shouldRenderLabel screenPos sw sh inFront then
        Some
          { District = district
            ScreenPos = screenPos
            ProjectedArea = projectedArea rect
            CameraDistance = Vector3.Distance(center3D, camera.Position) }
      else None)

  for placement in planDistrictLabelPlacements sw sh theme candidates do
    let bx = placement.Bounds.X
    let by = placement.Bounds.Y
    let bw = placement.Bounds.W
    let bh = placement.Bounds.H
    match placement.Style with
    | CompactDistrictLabel ->
        Raylib.DrawRectangle(bx, by, bw, bh, Color(8uy, 8uy, 16uy, 125uy))
        Raylib.DrawRectangle(bx, by, 4, bh, placement.District.Color)
        drawUiText placement.Title (bx + 9) (by + 4) theme.DistrictSubtitle placement.District.Color
    | DetailedDistrictLabel ->
        Raylib.DrawRectangle(bx, by, bw, bh, Color(8uy, 8uy, 16uy, 145uy))
        Raylib.DrawRectangle(bx, by, 4, bh, placement.District.Color)
        drawUiText placement.Title (bx + 10) (by + 4) theme.DistrictTitle placement.District.Color
        placement.Subtitle
        |> Option.iter (fun subtitle ->
          drawUiText subtitle (bx + 10) (by + 6 + theme.DistrictTitle) theme.DistrictSubtitle (darken placement.District.Color 0.82f))

let drawTooltip (b: FuncBuilding) (mx: int) (my: int) (theme: UiTextTheme) =
  let typeLabel =
    match b.BuildingType with
    | Shed       -> "⌂ Shed"
    | Cottage    -> "⌂ Cottage"
    | Rowhouse   -> "⌂ Rowhouse"
    | Commercial -> "▣ Commercial"
    | Tower      -> "▲ Tower"
    | Skyscraper -> "◆ Skyscraper"
  let lines = [|
    sprintf "%s.%s" b.Func.Module b.Func.Name
    sprintf "%s:%d-%d" b.Func.RelPath b.Func.StartLine b.Func.EndLine
    sprintf "%d lines  ·  heat %.0f%%  ·  %s" b.Func.LineCount (b.Heat * 100.0f) typeLabel
    sprintf "%d callers  ·  %d callees" b.CallerCount b.CalleeCount
    sprintf "District: %s" b.District
  |]
  let pad = 10
  let lineHeights =
    lines
    |> Array.mapi (fun i _ ->
      if i = 0 then theme.TooltipTitle + 6
      else theme.TooltipBody + 5)
  let maxTextW =
    lines
    |> Array.mapi (fun i l ->
      let size = if i = 0 then theme.TooltipTitle else theme.TooltipBody
      measureUiText l size)
    |> Array.max
  let boxW = maxTextW + pad * 2
  let boxH = (lineHeights |> Array.sum) + pad * 2
  let bx = min (mx + 16) (Raylib.GetScreenWidth() - boxW - 10)
  let by = max 10 (my - boxH - 10)

  Raylib.DrawRectangle(bx - 1, by - 1, boxW + 2, boxH + 2, b.Color)
  Raylib.DrawRectangle(bx, by, boxW, boxH, Color(12uy, 12uy, 22uy, 240uy))

  let mutable lineY = by + pad
  for i in 0 .. lines.Length - 1 do
    let color =
      match i with
      | 0 -> brighten b.Color 1.3f
      | 1 -> Color(140uy, 140uy, 160uy, 255uy)
      | 2 -> heatColor b.Heat
      | _ -> Color(200uy, 200uy, 210uy, 255uy)
    let size = if i = 0 then theme.TooltipTitle else theme.TooltipBody
    drawUiText lines.[i] (bx + pad) lineY size color
    lineY <- lineY + lineHeights.[i]

let drawHUD
  (buildings: FuncBuilding list)
  (districts: District list)
  (roads: Road list)
  (captured: bool)
  (selected: FuncBuilding option)
  (showCallLinks: bool)
  (theme: UiTextTheme) =

  let totalFuncs = buildings.Length
  let totalLOC = buildings |> List.sumBy (fun b -> b.Func.LineCount)
  let hottest =
    buildings
    |> List.sortByDescending (fun b -> b.Heat)
    |> List.tryHead

  let panelW = 430
  let panelH = 154
  Raylib.DrawRectangle(8, 8, panelW, panelH, Color(8uy, 8uy, 16uy, 185uy))
  Raylib.DrawRectangleLines(8, 8, panelW, panelH, Color(77uy, 201uy, 240uy, 70uy))

  drawUiText "SageFs Code City — Function View" 16 14 theme.HudTitle (Color(77uy, 201uy, 240uy, 255uy))
  drawUiText
    (sprintf "%d functions  ·  %d LOC  ·  %d districts" totalFuncs totalLOC districts.Length)
    16 46 theme.HudStats (Color(190uy, 190uy, 200uy, 255uy))
  drawUiText
    (sprintf "%d roads" roads.Length)
    16 68 theme.HudStats (Color(170uy, 170uy, 185uy, 255uy))

  let mutable infoY = 92

  match selected with
  | Some pinned ->
    drawUiText
      (sprintf "Pinned: %s" (ellipsize 44 (sprintf "%s.%s" pinned.Func.Module pinned.Func.Name)))
      16 infoY theme.HudStats selectedBuildingColor
    infoY <- infoY + theme.HudStats + 6
  | None -> ()

  match hottest with
  | Some h ->
    drawUiText
      (sprintf "Hottest: %s.%s (%d callers)" h.Func.Module h.Func.Name h.CallerCount)
      16 infoY theme.HudStats (heatColor h.Heat)
  | None -> ()

  let controlsY = infoY + theme.HudStats + 10
  drawUiText "Right-drag orbit  ·  Scroll zoom  ·  Mid-drag pan"
    16 controlsY theme.HudControls (Color(130uy, 130uy, 150uy, 255uy))
  drawUiText "WASD/QE move  ·  R reset  ·  F focus hottest"
    16 (controlsY + theme.HudControls + 5) theme.HudControls (Color(130uy, 130uy, 150uy, 255uy))
  drawUiText
    (sprintf "Click pin  ·  Esc clear  ·  C links %s  ·  Tab mouse" (if showCallLinks then "ON" else "OFF"))
    16 (controlsY + (theme.HudControls + 5) * 2) theme.HudControls
    (if showCallLinks then outgoingRelationColor else Color(150uy, 150uy, 165uy, 255uy))
  let captureLabel =
    if captured then "Mouse captured" else "Mouse free"
  let captureColor =
    if captured then Color(255uy, 200uy, 60uy, 255uy)
    else Color(100uy, 180uy, 100uy, 255uy)
  drawUiText captureLabel 16 (controlsY + (theme.HudControls + 5) * 3 + 1) theme.HudCapture captureColor

let drawLegend (districts: District list) (theme: UiTextTheme) =
  let screenW = Raylib.GetScreenWidth()
  let summary, hiddenCount = summarizeLegendDistricts 5 districts
  let panelW = 260
  let lineH = theme.LegendEntry + 7
  let extraRows = if hiddenCount > 0 then 1 else 0
  let panelH = theme.LegendTitle + 18 + (summary.Length + extraRows) * lineH + 10
  let px = screenW - panelW - 8
  let py = 8

  Raylib.DrawRectangle(px, py, panelW, panelH, Color(8uy, 8uy, 16uy, 170uy))
  Raylib.DrawRectangleLines(px, py, panelW, panelH, Color(80uy, 80uy, 100uy, 60uy))
  drawUiText "Districts (top 5)" (px + 8) (py + 8) theme.LegendTitle (Color(200uy, 200uy, 210uy, 255uy))

  for i in 0 .. summary.Length - 1 do
    let d = summary.[i]
    let y = py + theme.LegendTitle + 14 + i * lineH
    Raylib.DrawRectangle(px + 8, y + 3, 14, 14, d.Color)
    drawUiText
      (sprintf "%s (%d)" (ellipsize 18 d.Name) d.FuncCount)
      (px + 28) (y + 2) theme.LegendEntry (Color(190uy, 190uy, 200uy, 255uy))

  if hiddenCount > 0 then
    let y = py + theme.LegendTitle + 14 + summary.Length * lineH
    drawUiText (sprintf "+%d more districts" hiddenCount) (px + 8) (y + 2) theme.LegendEntry (Color(150uy, 150uy, 165uy, 255uy))

/// Heat scale legend
let drawHeatScale (theme: UiTextTheme) =
  let screenH = Raylib.GetScreenHeight()
  let px = 8
  let panelW = 250
  let panelH = 58
  let py = screenH - panelH - 8
  Raylib.DrawRectangle(px, py, panelW, panelH, Color(8uy, 8uy, 16uy, 210uy))
  Raylib.DrawRectangleLines(px, py, panelW, panelH, Color(80uy, 80uy, 100uy, 80uy))
  drawUiText "Heat: callers" (px + 8) (py + 5) theme.HeatTitle (Color(170uy, 170uy, 185uy, 255uy))
  // Draw gradient bar
  for i in 0 .. 199 do
    let t = float32 i / 199.0f
    let c = heatColor t
    Raylib.DrawRectangle(px + 8 + i, py + 26, 1, 16, c)
  drawUiText "cold" (px + 8) (py + 42) theme.HeatLabel (Color(40uy, 80uy, 200uy, 255uy))
  drawUiText "hot" (px + 182) (py + 42) theme.HeatLabel (Color(255uy, 40uy, 40uy, 255uy))

let drawSelectionPanel
  (selected: FuncBuilding option)
  (incoming: RelatedBuilding list)
  (outgoing: RelatedBuilding list)
  (showCallLinks: bool)
  (theme: UiTextTheme) =

  let screenW = Raylib.GetScreenWidth()
  let screenH = Raylib.GetScreenHeight()
  let panelW = 480

  match selected with
  | None ->
    let panelH = 82
    let px = screenW - panelW - 8
    let py = screenH - panelH - 8
    Raylib.DrawRectangle(px, py, panelW, panelH, Color(8uy, 8uy, 16uy, 215uy))
    Raylib.DrawRectangleLines(px, py, panelW, panelH, Color(70uy, 70uy, 90uy, 120uy))
    drawUiText "Pinned selection" (px + 12) (py + 10) theme.SelectionTitle selectedBuildingColor
    drawUiText
      "Left-click a building to inspect detected callers and callees."
      (px + 12) (py + 38) theme.SelectionBody (Color(190uy, 190uy, 200uy, 255uy))
  | Some pinned ->
    let shownIncoming = incoming |> List.truncate 5
    let shownOutgoing = outgoing |> List.truncate 5
    let extraIncoming = max 0 (incoming.Length - shownIncoming.Length)
    let extraOutgoing = max 0 (outgoing.Length - shownOutgoing.Length)

    let lines = ResizeArray<string * Color * int>()
    let addLine size text color = lines.Add(text, color, size)

    addLine theme.SelectionTitle (sprintf "Pinned: %s.%s" pinned.Func.Module pinned.Func.Name) selectedBuildingColor
    addLine theme.SelectionBody (sprintf "%s:%d-%d" pinned.Func.RelPath pinned.Func.StartLine pinned.Func.EndLine) (Color(160uy, 160uy, 175uy, 255uy))
    let typeStr =
      match pinned.BuildingType with
      | Shed       -> "Shed" | Cottage    -> "Cottage" | Rowhouse   -> "Rowhouse"
      | Commercial -> "Commercial" | Tower      -> "Tower" | Skyscraper -> "Skyscraper"
    addLine theme.SelectionBody (sprintf "%d lines  ·  complexity %d  ·  heat %.0f%%  ·  %s"
      pinned.Func.LineCount pinned.Complexity (pinned.Heat * 100.0f) typeStr) (Color(215uy, 215uy, 225uy, 255uy))
    addLine theme.SelectionBody (sprintf "%d callers  ·  %d callees  ·  link arcs %s"
      incoming.Length outgoing.Length (if showCallLinks then "visible" else "hidden")) (Color(190uy, 190uy, 200uy, 255uy))
    addLine theme.SelectionTitle "Detected callers" incomingRelationColor
    if shownIncoming.IsEmpty then
      addLine theme.SelectionBody "  none detected" (Color(145uy, 145uy, 160uy, 255uy))
    else
      shownIncoming
      |> List.iter (fun rel ->
        addLine theme.SelectionBody
          (sprintf "  %2dx  %s"
            rel.Weight
            (ellipsize 42 (sprintf "%s.%s" rel.Building.Func.Module rel.Building.Func.Name)))
          (Color(205uy, 225uy, 255uy, 255uy)))
      if extraIncoming > 0 then
        addLine theme.SelectionBody (sprintf "  +%d more callers" extraIncoming) (Color(150uy, 190uy, 235uy, 255uy))
    addLine theme.SelectionTitle "Detected callees" outgoingRelationColor
    if shownOutgoing.IsEmpty then
      addLine theme.SelectionBody "  none detected" (Color(145uy, 145uy, 160uy, 255uy))
    else
      shownOutgoing
      |> List.iter (fun rel ->
        addLine theme.SelectionBody
          (sprintf "  %2dx  %s"
            rel.Weight
            (ellipsize 42 (sprintf "%s.%s" rel.Building.Func.Module rel.Building.Func.Name)))
          (Color(255uy, 220uy, 200uy, 255uy)))
      if extraOutgoing > 0 then
        addLine theme.SelectionBody (sprintf "  +%d more callees" extraOutgoing) (Color(240uy, 185uy, 150uy, 255uy))
    addLine theme.SelectionBody "Left-click empty space or press Esc to clear selection." (Color(150uy, 150uy, 165uy, 255uy))

    let panelH =
      20
      + (lines |> Seq.sumBy (fun (_, _, size) -> size + 5))
      + 12
    let px = screenW - panelW - 8
    let py = screenH - panelH - 8

    Raylib.DrawRectangle(px, py, panelW, panelH, Color(8uy, 8uy, 16uy, 220uy))
    Raylib.DrawRectangleLines(px, py, panelW, panelH, Color(80uy, 80uy, 100uy, 120uy))

    let mutable y = py + 10
    for (text, color, size) in lines do
      drawUiText text (px + 12) y size color
      y <- y + size + 5

// ─── SageFs Integration ───────────────────────────────────────

/// Parse the workingDirectory field from SageFs's /api/daemon-info JSON response.
let parseDaemonInfoJson (json: string) : string option =
  try
    let doc = System.Text.Json.JsonDocument.Parse(json)
    let wd = doc.RootElement.GetProperty("workingDirectory").GetString()
    if String.IsNullOrWhiteSpace(wd) then None
    else Some wd
  with _ -> None

/// Resolve repo root from CLI args, SageFs query result, and fallback (pure, no IO).
/// Priority: explicit argv[0] > SageFs working dir > fallback dir.
let resolveRepoRootPure (argv: string[]) (sageFsDir: string option) (fallbackDir: string) : string =
  match argv |> Array.tryHead with
  | Some p when not (String.IsNullOrWhiteSpace(p)) -> p
  | _ ->
    match sageFsDir with
    | Some d -> d
    | None -> fallbackDir

/// Query the SageFs dashboard HTTP server for the active project's working directory.
let tryQuerySageFsRoot (dashboardPort: int) : string option =
  try
    use client = new System.Net.Http.HttpClient()
    client.Timeout <- TimeSpan.FromMilliseconds(700.0)
    let url = sprintf "http://localhost:%d/api/daemon-info" dashboardPort
    let json = client.GetStringAsync(url).Result
    parseDaemonInfoJson json
  with _ -> None

let tryResolveProjectFile (path: string) : string option =
  let tryFromDirectory dir =
    if Directory.Exists(dir) then
      Directory.EnumerateFiles(dir, "*.fsproj")
      |> Seq.sort
      |> Seq.toList
      |> function
         | [projectFile] -> Some (Path.GetFullPath(projectFile))
         | _ -> None
    else None

  match path with
  | value when String.IsNullOrWhiteSpace(value) -> None
  | value when File.Exists(value) && Path.GetExtension(value).Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ->
      Some (Path.GetFullPath(value))
  | value when Directory.Exists(value) -> tryFromDirectory value
  | value when File.Exists(value) -> tryFromDirectory (Path.GetDirectoryName(value))
  | _ -> None

// ─── Main Loop ────────────────────────────────────────────────

[<EntryPoint>]
let main argv =
  let explicitPath =
    argv
    |> Array.tryHead
    |> Option.filter (fun path -> not (String.IsNullOrWhiteSpace(path)))
  let repoRoot =
    match explicitPath with
    | Some path ->
      if File.Exists(path) then Path.GetDirectoryName(path)  // .fsproj/.sln file
      else path
    | _ ->
      // Auto-detect from SageFs daemon (dashboard port = MCP port + 1)
      let mcpPort =
        match Environment.GetEnvironmentVariable("SageFs_MCP_PORT") with
        | s when String.IsNullOrWhiteSpace(s) -> 37749
        | s -> match Int32.TryParse(s) with true, p -> p | _ -> 37749
      let sageFsDir = tryQuerySageFsRoot (mcpPort + 1)
      // Walk up from CWD for any solution file if SageFs not available
      let fallback =
        let mutable dir = Directory.GetCurrentDirectory()
        let mutable found = false
        while not found && dir.Length > 3 do
          let hasSln =
            Directory.EnumerateFiles(dir, "*.slnx") |> Seq.isEmpty |> not ||
            Directory.EnumerateFiles(dir, "*.sln") |> Seq.isEmpty |> not
          if hasSln then found <- true
          else dir <- Directory.GetParent(dir).FullName
        if found then dir else Directory.GetCurrentDirectory()
      resolveRepoRootPure [||] sageFsDir fallback
  let projectFile =
    match explicitPath |> Option.bind tryResolveProjectFile with
    | Some project -> Some project
    | None -> tryResolveProjectFile repoRoot

  let cityName = Path.GetFileName(repoRoot)
  printfn "Scanning %s..." repoRoot
  let buildings, districts, roads, blocks, callEdges, alleyRoads = buildCity repoRoot projectFile
  let buildingArray = buildings |> List.toArray
  let incomingRelations, outgoingRelations = buildRelationMaps buildingArray callEdges
  let maxRenderedRelations = 8
  let sortedRoads = roads |> List.sortByDescending (fun r -> r.Weight) |> List.truncate 500 |> List.toArray
  let hottest = buildings |> List.sortByDescending (fun b -> b.Heat) |> List.tryHead

  // Validate coordinates
  let badBuildings =
    buildingArray |> Array.filter (fun b ->
      Single.IsNaN(b.X) || Single.IsNaN(b.Z) || Single.IsInfinity(b.X) || Single.IsInfinity(b.Z))
  if badBuildings.Length > 0 then
    printfn "WARNING: %d buildings have bad coordinates!" badBuildings.Length

  Raylib.SetConfigFlags(ConfigFlags.ResizableWindow ||| ConfigFlags.Msaa4xHint)
  Raylib.InitWindow(1600, 900, sprintf "%s — Code City" cityName)
  Raylib.SetTargetFPS(60)
  Rlgl.SetClipPlanes(1.0, 5000.0)

  // Load a crisp system monospace font for UI text; falls back to Raylib default
  let tryLoadFont (path: string) =
    if File.Exists(path) then
      let f = Raylib.LoadFont(path)
      if f.BaseSize > 0 then Some f else None
    else None
  uiFont <-
    tryLoadFont @"C:\Windows\Fonts\consola.ttf"
    |> Option.orElse (tryLoadFont @"C:\Windows\Fonts\cour.ttf")
    |> Option.orElse (tryLoadFont @"C:\Windows\Fonts\lucon.ttf")
    |> Option.map (fun f -> Raylib.SetTextureFilter(f.Texture, TextureFilter.Bilinear); f)
    |> Option.defaultValue (Raylib.GetFontDefault())

  let cityExtent =
    if buildingArray.Length = 0 then 100.0f
    else
      buildingArray |> Array.map (fun b ->
        max (abs (b.X + b.W)) (max (abs b.X) (max (abs (b.Z + b.D)) (abs b.Z))))
      |> Array.max

  let mutable staticMesh = buildStaticMesh buildingArray blocks cityExtent alleyRoads

  // Lighting shader
  let vsSource = """
#version 330
in vec3 vertexPosition;
in vec3 vertexNormal;
in vec4 vertexColor;
uniform mat4 mvp;
uniform mat4 matModel;
uniform mat3 matNormal;
out vec3 fragPos;
out vec3 fragNormal;
out vec4 fragColor;
void main() {
  fragPos = (matModel * vec4(vertexPosition, 1.0)).xyz;
  fragNormal = normalize(matNormal * vertexNormal);
  fragColor = vertexColor;
  gl_Position = mvp * vec4(vertexPosition, 1.0);
}
"""
  let fsSource = """
#version 330
in vec3 fragPos;
in vec3 fragNormal;
in vec4 fragColor;
out vec4 finalColor;
uniform vec3 lightDir;
uniform vec3 ambient;
uniform vec3 cameraPos;
uniform vec3 fogColor;
uniform float fogDensity;
uniform float time;
uniform float nightScale;
uniform float sunElevation;
void main() {
  // Per-type window grid profiles: [scaleU, scaleV, litBias, borderPct]
  // Index: Shed=0 Cottage=1 Rowhouse=2 Commercial=3 Tower=4 Skyscraper=5
  vec4 wp[6];
  wp[0] = vec4(0.35, 0.25, 0.60, 0.25);
  wp[1] = vec4(0.55, 0.40, 0.55, 0.20);
  wp[2] = vec4(0.90, 0.55, 0.50, 0.16);
  wp[3] = vec4(1.10, 0.70, 0.45, 0.14);
  wp[4] = vec4(1.80, 1.20, 0.40, 0.08);
  wp[5] = vec4(2.40, 1.80, 0.35, 0.04);

  // Decode building type from vertex alpha (0-5 = building types, 255 = terrain/road)
  float rawAlpha = fragColor.a * 255.0;
  bool isBuilding = rawAlpha < 6.5;
  int bType = clamp(int(rawAlpha + 0.5), 0, 5);

  vec3 n = normalize(fragNormal);
  float diff = max(dot(n, lightDir), 0.0);
  vec3 lit = fragColor.rgb * (ambient + vec3(diff * 0.65));

  // Specular on flat roofs (warm sunlight catch)
  if (abs(n.y) > 0.9) {
    vec3 viewDir = normalize(cameraPos - fragPos);
    vec3 halfVec = normalize(lightDir + viewDir);
    float spec = pow(max(dot(n, halfVec), 0.0), 32.0);
    lit += vec3(1.0, 0.95, 0.85) * spec * 0.15;
  }

  float wallness = 1.0 - abs(n.y);

  // Glass-wall specular on Tower and Skyscraper vertical faces
  if (wallness > 0.5 && isBuilding && bType >= 4) {
    vec3 viewDirW = normalize(cameraPos - fragPos);
    vec3 halfVecW = normalize(lightDir + viewDirW);
    float specW = pow(max(dot(n, halfVecW), 0.0), 64.0);
    lit += vec3(0.88, 0.93, 1.0) * specW * 0.10;
  }

  // Procedural windows with per-type grid profile
  if (wallness > 0.5 && fragPos.y > 0.3 && isBuilding) {
    vec4 prof = wp[bType];
    float u = abs(n.x) > abs(n.z) ? fragPos.z : fragPos.x;
    float v = fragPos.y;
    vec2 cell = vec2(u * prof.x, v * prof.y);
    vec2 grid = fract(cell);
    float wx = floor(cell.x);
    float wy = floor(cell.y);
    float hash = fract(sin(wx * 127.1 + wy * 311.7) * 43758.5453);
    float flicker = sin(time * 0.5 + hash * 6.28) * 0.05;
    float isLit = step(prof.z * nightScale + flicker, hash);
    float brd = prof.w;
    float inWindow = step(brd, grid.x) * step(brd, grid.y)
                   * (1.0 - step(1.0 - brd, grid.x)) * (1.0 - step(1.0 - brd, grid.y));
    vec3 windowEmit = vec3(1.0, 0.93, 0.7) * 0.5 * inWindow * isLit;
    float darken = inWindow * (1.0 - isLit) * 0.12;
    lit = lit * (1.0 - darken) + windowEmit;
    vec2 windowCenter = vec2(0.5, 0.47);
    float distToCenter = length(grid - windowCenter);
    float glow = isLit * smoothstep(0.7, 0.1, distToCenter) * 0.15;
    lit += vec3(1.0, 0.9, 0.65) * glow;
  }

  // Warm glow at building bases
  if (wallness > 0.5 && fragPos.y > 0.02 && fragPos.y < 0.35) {
    float glow = smoothstep(0.35, 0.05, fragPos.y) * 0.1;
    lit += vec3(0.35, 0.28, 0.18) * glow;
  }

  // Atmospheric sky gradient fog — shifts from deep night to twilight as sun rises
  vec3 viewDir = normalize(fragPos - cameraPos);
  float upness = max(0.0, viewDir.y);
  float dayT = max(0.0, sunElevation);
  vec3 horizonColor = mix(vec3(0.03, 0.02, 0.05), vec3(0.20, 0.16, 0.28), dayT);
  vec3 zenithColor  = mix(vec3(0.01, 0.01, 0.03), vec3(0.05, 0.04, 0.12), dayT);
  vec3 skyGrad = mix(horizonColor, zenithColor, smoothstep(0.0, 0.5, upness));

  float dist = length(fragPos - cameraPos);
  float fog = 1.0 - exp(-fogDensity * dist * dist);
  fog = clamp(fog, 0.0, 1.0);
  finalColor = vec4(mix(lit, skyGrad, fog), 1.0);
}
"""
  let lightShader = Raylib.LoadShaderFromMemory(vsSource, fsSource)
  let lightDirLoc = Raylib.GetShaderLocation(lightShader, "lightDir")
  let ambientLoc = Raylib.GetShaderLocation(lightShader, "ambient")
  let cameraPosLoc = Raylib.GetShaderLocation(lightShader, "cameraPos")
  let fogDensityLoc = Raylib.GetShaderLocation(lightShader, "fogDensity")
  let fogColorLoc = Raylib.GetShaderLocation(lightShader, "fogColor")
  let sunDir = Vector3.Normalize(Vector3(0.4f, 0.8f, 0.3f))
  Raylib.SetShaderValue(lightShader, lightDirLoc, sunDir, ShaderUniformDataType.Vec3)
  let ambientVal = Vector3(0.65f, 0.65f, 0.70f)
  Raylib.SetShaderValue(lightShader, ambientLoc, ambientVal, ShaderUniformDataType.Vec3)
  let fogDens = 0.00003f
  Raylib.SetShaderValue(lightShader, fogDensityLoc, fogDens, ShaderUniformDataType.Float)
  let fogCol = Vector3(float32 skyColor.R / 255.0f, float32 skyColor.G / 255.0f, float32 skyColor.B / 255.0f)
  Raylib.SetShaderValue(lightShader, fogColorLoc, fogCol, ShaderUniformDataType.Vec3)
  let timeLoc = Raylib.GetShaderLocation(lightShader, "time")
  let nightScaleLoc = Raylib.GetShaderLocation(lightShader, "nightScale")
  let sunElevationLoc = Raylib.GetShaderLocation(lightShader, "sunElevation")
  Raylib.SetShaderValue(lightShader, nightScaleLoc, 1.0f, ShaderUniformDataType.Float)
  Raylib.SetShaderValue(lightShader, sunElevationLoc, 0.8f, ShaderUniformDataType.Float)

  let mutable material = Raylib.LoadMaterialDefault()
  material.Shader <- lightShader

  // ─── SSAO Pipeline ──────────────────────────────────────────────
  let screenW = Raylib.GetScreenWidth()
  let screenH = Raylib.GetScreenHeight()
  let aoW, aoH = ssaoBufferSize defaultSsaoSettings screenW screenH
  let bloomW = max 1 (screenW / 2)
  let bloomH = max 1 (screenH / 2)

  let makeTexture2D (id: uint32) w h (fmt: PixelFormat) =
    let mutable t = Texture2D()
    t.Id <- id; t.Width <- w; t.Height <- h; t.Mipmaps <- 1; t.Format <- fmt; t

  // Scene RT with depth-as-texture (sampleable for SSAO)
  let createSceneRT (w: int) (h: int) =
    let fboId = Rlgl.LoadFramebuffer()
    if fboId > 0u then
      Rlgl.EnableFramebuffer fboId
      let colorId = Rlgl.LoadTexture(Unchecked.defaultof<voidptr>, w, h, PixelFormat.UncompressedR8G8B8A8, 1)
      Rlgl.FramebufferAttach(fboId, colorId, FramebufferAttachType.ColorChannel0, FramebufferAttachTextureType.Texture2D, 0)
      let depthId = Rlgl.LoadTextureDepth(w, h, false) // false = texture, not renderbuffer
      Rlgl.FramebufferAttach(fboId, depthId, FramebufferAttachType.Depth, FramebufferAttachTextureType.Texture2D, 0)
      let ok = rb (Rlgl.FramebufferComplete fboId)
      Rlgl.DisableFramebuffer()
      if ok then
        let mutable rt = RenderTexture2D()
        rt.Id <- fboId
        rt.Texture <- makeTexture2D colorId w h PixelFormat.UncompressedR8G8B8A8
        rt.Depth <- makeTexture2D depthId w h PixelFormat.UncompressedR8G8B8A8
        Some rt
      else
        printfn "SSAO: Scene framebuffer not complete"
        None
    else
      printfn "SSAO: Failed to create framebuffer"
      None

  let sceneRTOpt = createSceneRT screenW screenH
  let ssaoAvailable = sceneRTOpt.IsSome
  let sceneRT =
    match sceneRTOpt with
    | Some rt -> rt
    | None -> Raylib.LoadRenderTexture(screenW, screenH)
  let ssaoRT = Raylib.LoadRenderTexture(aoW, aoH)
  let blurRT = Raylib.LoadRenderTexture(aoW, aoH)
  let brightRT = Raylib.LoadRenderTexture(bloomW, bloomH)
  let bloomHRT = Raylib.LoadRenderTexture(bloomW, bloomH)
  let bloomVRT = Raylib.LoadRenderTexture(bloomW, bloomH)
  if ssaoAvailable then printfn "SSAO: Pipeline ready (%dx%d scene, %dx%d AO)" screenW screenH aoW aoH
  else printfn "SSAO: Depth texture not supported — SSAO disabled"

  // Post-process vertex shader (shared by SSAO, blur, composite)
  let postVS = """
#version 330
in vec3 vertexPosition;
in vec2 vertexTexCoord;
out vec2 fragTexCoord;
uniform mat4 mvp;
void main() {
  fragTexCoord = vertexTexCoord;
  gl_Position = mvp * vec4(vertexPosition, 1.0);
}
"""

  let ssaoFS = """
#version 330
in vec2 fragTexCoord;
out vec4 finalColor;
uniform sampler2D texture0;
uniform vec2 texelSize;
uniform float ssaoRadius;
uniform float ssaoBias;
uniform float nearPlane;
uniform float farPlane;

float linearizeDepth(float d) {
  float ndc = d * 2.0 - 1.0;
  return (2.0 * nearPlane * farPlane) / (farPlane + nearPlane - ndc * (farPlane - nearPlane));
}

const vec2 poissonDisk[12] = vec2[](
  vec2(-0.326, -0.406), vec2(-0.840, -0.074), vec2(-0.696,  0.457),
  vec2(-0.203,  0.621), vec2( 0.962, -0.195), vec2( 0.473, -0.480),
  vec2( 0.519,  0.767), vec2( 0.185, -0.893), vec2( 0.507,  0.064),
  vec2( 0.896,  0.412), vec2(-0.322, -0.933), vec2(-0.792, -0.598)
);

void main() {
  float depth = texture(texture0, fragTexCoord).r;
  if (depth >= 0.9999) { finalColor = vec4(1.0); return; }

  float centerLin = linearizeDepth(depth);
  float occlusion = 0.0;

  // Per-pixel hash rotation to break banding
  float angle = fract(sin(dot(fragTexCoord * 4127.0, vec2(12.9898, 78.233))) * 43758.5453) * 6.2832;
  float ca = cos(angle);
  float sa = sin(angle);

  // Depth-scaled sample radius; tighter cap prevents blotchy under-sampled regions.
  float pixelRadius = clamp(ssaoRadius * 72.0 / centerLin, 1.0, 12.0);

  for (int i = 0; i < 12; i++) {
    vec2 offset = poissonDisk[i];
    offset = vec2(offset.x * ca - offset.y * sa, offset.x * sa + offset.y * ca);
    vec2 sampleUV = fragTexCoord + offset * pixelRadius * texelSize;

    float sd = texture(texture0, sampleUV).r;
    if (sd >= 0.9999) continue;

    float sampleLin = linearizeDepth(sd);
    float depthDiff = centerLin - sampleLin;

    float rangeCheck = smoothstep(0.0, 1.0, ssaoRadius * 5.0 / (abs(depthDiff) + 0.01));
    occlusion += smoothstep(ssaoBias * 0.5, ssaoBias * 2.0, depthDiff) * rangeCheck;
  }

  float ao = 1.0 - (occlusion / 12.0);
  ao = clamp(pow(ao, 1.08), 0.0, 1.0);
  finalColor = vec4(ao, ao, ao, 1.0);
}
"""

  let blurFS = """
#version 330
in vec2 fragTexCoord;
out vec4 finalColor;
uniform sampler2D texture0;
uniform vec2 texelSize;

void main() {
  float result = 0.0;
  float weight = 0.0;
  float center = texture(texture0, fragTexCoord).r;
  for (int x = -2; x <= 2; x++) {
    for (int y = -2; y <= 2; y++) {
      vec2 off = vec2(float(x), float(y)) * texelSize;
      float s = texture(texture0, fragTexCoord + off).r;
      float w = exp(-float(x*x + y*y) / 4.5) * exp(-abs(s - center) * 10.0);
      result += s * w;
      weight += w;
    }
  }
  finalColor = vec4(vec3(result / weight), 1.0);
}
"""

  let compositeFS = """
#version 330
in vec2 fragTexCoord;
out vec4 finalColor;
uniform sampler2D texture0;
uniform sampler2D aoTex;
uniform sampler2D bloomTex;
uniform float ssaoStrength;
uniform float bloomIntensity;

void main() {
  vec3 scene = texture(texture0, fragTexCoord).rgb;
  float ao = texture(aoTex, fragTexCoord).r;
  ao = mix(1.0, ao, ssaoStrength);
  vec3 bloom = texture(bloomTex, fragTexCoord).rgb;
  finalColor = vec4(scene * ao + bloom * bloomIntensity, 1.0);
}
"""

  let brightExtractFS = """
#version 330
in vec2 fragTexCoord;
out vec4 finalColor;
uniform sampler2D texture0;
uniform float bloomThreshold;
uniform float bloomSoftKnee;

void main() {
  vec3 color = texture(texture0, fragTexCoord).rgb;
  float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
  float knee = bloomThreshold * bloomSoftKnee;
  float soft = luma - bloomThreshold + knee;
  soft = clamp(soft, 0.0, 2.0 * knee);
  soft = soft * soft / (4.0 * knee + 0.0001);
  float w = max(soft, luma - bloomThreshold) / max(luma, 0.0001);
  finalColor = vec4(color * max(w, 0.0), 1.0);
}
"""

  let bloomBlurFS = """
#version 330
in vec2 fragTexCoord;
out vec4 finalColor;
uniform sampler2D texture0;
uniform vec2 blurDirection;

void main() {
  float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
  vec3 result = texture(texture0, fragTexCoord).rgb * weights[0];
  for (int i = 1; i < 5; i++) {
    vec2 offset = blurDirection * float(i);
    result += texture(texture0, fragTexCoord + offset).rgb * weights[i];
    result += texture(texture0, fragTexCoord - offset).rgb * weights[i];
  }
  finalColor = vec4(result, 1.0);
}
"""

  let ssaoShader = Raylib.LoadShaderFromMemory(postVS, ssaoFS)
  let ssaoTexelSizeLoc = Raylib.GetShaderLocation(ssaoShader, "texelSize")
  let ssaoRadiusLoc = Raylib.GetShaderLocation(ssaoShader, "ssaoRadius")
  let ssaoBiasLoc = Raylib.GetShaderLocation(ssaoShader, "ssaoBias")
  let ssaoNearLoc = Raylib.GetShaderLocation(ssaoShader, "nearPlane")
  let ssaoFarLoc = Raylib.GetShaderLocation(ssaoShader, "farPlane")

  let blurShader = Raylib.LoadShaderFromMemory(postVS, blurFS)
  let blurTexelSizeLoc = Raylib.GetShaderLocation(blurShader, "texelSize")

  let compositeShader = Raylib.LoadShaderFromMemory(postVS, compositeFS)
  let aoTexLoc = Raylib.GetShaderLocation(compositeShader, "aoTex")
  let ssaoStrengthLoc = Raylib.GetShaderLocation(compositeShader, "ssaoStrength")

  // Set static SSAO uniforms
  let aoTexel = Vector2(1.0f / float32 aoW, 1.0f / float32 aoH)
  Raylib.SetShaderValue(ssaoShader, ssaoTexelSizeLoc, aoTexel, ShaderUniformDataType.Vec2)
  Raylib.SetShaderValue(ssaoShader, ssaoNearLoc, 1.0f, ShaderUniformDataType.Float)
  Raylib.SetShaderValue(ssaoShader, ssaoFarLoc, 5000.0f, ShaderUniformDataType.Float)
  Raylib.SetShaderValue(blurShader, blurTexelSizeLoc, aoTexel, ShaderUniformDataType.Vec2)
  Raylib.SetShaderValue(ssaoShader, ssaoRadiusLoc, defaultSsaoSettings.Radius, ShaderUniformDataType.Float)
  Raylib.SetShaderValue(ssaoShader, ssaoBiasLoc, defaultSsaoSettings.Bias, ShaderUniformDataType.Float)
  Raylib.SetShaderValue(compositeShader, ssaoStrengthLoc, defaultSsaoSettings.Strength, ShaderUniformDataType.Float)
  printfn "SSAO shader locs: texel=%d radius=%d bias=%d near=%d far=%d aoTex=%d strength=%d"
    ssaoTexelSizeLoc ssaoRadiusLoc ssaoBiasLoc ssaoNearLoc ssaoFarLoc aoTexLoc ssaoStrengthLoc

  // ─── Bloom Pipeline ──────────────────────────────────────────────
  let brightExtractShader = Raylib.LoadShaderFromMemory(postVS, brightExtractFS)
  let bloomThresholdLoc = Raylib.GetShaderLocation(brightExtractShader, "bloomThreshold")
  let bloomSoftKneeLoc = Raylib.GetShaderLocation(brightExtractShader, "bloomSoftKnee")

  let bloomBlurShader = Raylib.LoadShaderFromMemory(postVS, bloomBlurFS)
  let blurDirectionLoc = Raylib.GetShaderLocation(bloomBlurShader, "blurDirection")

  let bloomTexLoc = Raylib.GetShaderLocation(compositeShader, "bloomTex")
  let bloomIntensityLoc = Raylib.GetShaderLocation(compositeShader, "bloomIntensity")

  Raylib.SetShaderValue(brightExtractShader, bloomThresholdLoc, 0.7f, ShaderUniformDataType.Float)
  Raylib.SetShaderValue(brightExtractShader, bloomSoftKneeLoc, 0.5f, ShaderUniformDataType.Float)
  Raylib.SetShaderValue(compositeShader, bloomIntensityLoc, 0.35f, ShaderUniformDataType.Float)
  printfn "Bloom: Pipeline ready (threshold=0.7 knee=0.5 intensity=0.35)"

  let cam = FpsCamera.create (Vector3(0.0f, cityExtent * 0.8f, cityExtent * 0.3f))
  cam.Pitch <- -1.2f
  cam.Yaw <- 0.0f

  let mutable highlighted : FuncBuilding option = None
  let mutable selected : FuncBuilding option = None
  let mutable mouseCaptured = false
  let mutable diagnosticMode = false
  let mutable lightingEnabled = true
  let mutable showCallLinks = true
  let defaultShader = Raylib.LoadMaterialDefault().Shader
  let uiTextTheme = compactUiTextTheme defaultUiTextTheme

  let mutable ssaoEnabled = ssaoAvailable
  let mutable ssaoDebug = false
  let mutable ssaoRadius = defaultSsaoSettings.Radius
  let mutable ssaoBias = defaultSsaoSettings.Bias
  let mutable ssaoStrength = defaultSsaoSettings.Strength
  let mutable bloomEnabled = ssaoAvailable
  let mutable bloomThreshold = 0.7f
  let mutable bloomIntensity = 0.35f
  let mutable totalTime = 0.0f

  while not (rb (Raylib.WindowShouldClose())) do
    let dt = Raylib.GetFrameTime()
    totalTime <- totalTime + dt
    Raylib.SetShaderValue(lightShader, timeLoc, totalTime, ShaderUniformDataType.Float)

    // Day/night cycle — sun orbits at 0.04 rad/s (full cycle ~157 seconds)
    let sunAngle = totalTime * 0.04f
    let sunElev = MathF.Sin(sunAngle)  // -1.0 (night) to 1.0 (noon)
    let dynSunDir = Vector3.Normalize(Vector3(MathF.Cos(sunAngle) * 0.7f, max 0.08f (sunElev * 0.9f + 0.2f), 0.4f))
    let ns = nightScaleForElevation sunElev
    Raylib.SetShaderValue(lightShader, lightDirLoc, dynSunDir, ShaderUniformDataType.Vec3)
    Raylib.SetShaderValue(lightShader, nightScaleLoc, ns, ShaderUniformDataType.Float)
    Raylib.SetShaderValue(lightShader, sunElevationLoc, sunElev, ShaderUniformDataType.Float)
    // Dynamic sky background: deep purple-black at night, slightly warmer at day
    let sk = int (30.0f + max 0.0f sunElev * 14.0f)
    let dynSkyColor = Color(byte sk, byte (sk - 4), byte (sk + 16), 255uy)

    if rb (Raylib.IsKeyPressed(KeyboardKey.Tab)) then
      mouseCaptured <- not mouseCaptured
      if mouseCaptured then Raylib.DisableCursor()
      else Raylib.EnableCursor()

    if rb (Raylib.IsKeyPressed(KeyboardKey.O)) then
      diagnosticMode <- not diagnosticMode

    if rb (Raylib.IsKeyPressed(KeyboardKey.L)) then
      lightingEnabled <- not lightingEnabled
      material.Shader <- if lightingEnabled then lightShader else defaultShader

    if rb (Raylib.IsKeyPressed(KeyboardKey.C)) then
      showCallLinks <- not showCallLinks

    if rb (Raylib.IsKeyPressed(KeyboardKey.Escape)) then
      selected <- None

    // SSAO controls: P=cycle(off/on/debug), U/J=radius, I/K=bias
    if rb (Raylib.IsKeyPressed(KeyboardKey.P)) && ssaoAvailable then
      if ssaoDebug then ssaoEnabled <- false; ssaoDebug <- false
      elif ssaoEnabled then ssaoDebug <- true
      else ssaoEnabled <- true
    if rb (Raylib.IsKeyPressed(KeyboardKey.U)) then ssaoRadius <- min 10.0f (ssaoRadius + 0.5f)
    if rb (Raylib.IsKeyPressed(KeyboardKey.J)) then ssaoRadius <- max 0.5f (ssaoRadius - 0.5f)
    if rb (Raylib.IsKeyPressed(KeyboardKey.I)) then ssaoBias <- min 0.1f (ssaoBias + 0.005f)
    if rb (Raylib.IsKeyPressed(KeyboardKey.K)) then ssaoBias <- max 0.001f (ssaoBias - 0.005f)

    // Bloom controls: B=toggle, N/M=intensity, comma/period=threshold
    if rb (Raylib.IsKeyPressed(KeyboardKey.B)) && ssaoAvailable then
      bloomEnabled <- not bloomEnabled
    if rb (Raylib.IsKeyPressed(KeyboardKey.N)) then
      bloomIntensity <- min 1.0f (bloomIntensity + 0.05f)
      Raylib.SetShaderValue(compositeShader, bloomIntensityLoc, bloomIntensity, ShaderUniformDataType.Float)
    if rb (Raylib.IsKeyPressed(KeyboardKey.M)) then
      bloomIntensity <- max 0.0f (bloomIntensity - 0.05f)
      Raylib.SetShaderValue(compositeShader, bloomIntensityLoc, bloomIntensity, ShaderUniformDataType.Float)
    if rb (Raylib.IsKeyPressed(KeyboardKey.Comma)) then
      bloomThreshold <- max 0.1f (bloomThreshold - 0.05f)
      Raylib.SetShaderValue(brightExtractShader, bloomThresholdLoc, bloomThreshold, ShaderUniformDataType.Float)
    if rb (Raylib.IsKeyPressed(KeyboardKey.Period)) then
      bloomThreshold <- min 1.5f (bloomThreshold + 0.05f)
      Raylib.SetShaderValue(brightExtractShader, bloomThresholdLoc, bloomThreshold, ShaderUniformDataType.Float)

    FpsCamera.update cam mouseCaptured

    if rb (Raylib.IsKeyPressed(KeyboardKey.R)) then
      cam.Position <- Vector3(0.0f, cityExtent * 0.8f, cityExtent * 0.3f)
      cam.Yaw <- 0.0f
      cam.Pitch <- -1.2f

    if rb (Raylib.IsKeyPressed(KeyboardKey.F)) then
      match hottest with
      | Some h ->
        let tx = h.X + h.W / 2.0f
        let tz = h.Z + h.D / 2.0f
        cam.Position <- Vector3(tx - 15.0f, h.H + 10.0f, tz - 15.0f)
        let dir = Vector3.Normalize(Vector3(tx, h.H / 2.0f, tz) - cam.Position)
        cam.Yaw <- MathF.Atan2(dir.Z, dir.X)
        cam.Pitch <- MathF.Asin(dir.Y)
      | None -> ()

    let camera3D = FpsCamera.toCamera3D cam
    Raylib.SetShaderValue(lightShader, cameraPosLoc, cam.Position, ShaderUniformDataType.Vec3)

    if not mouseCaptured && not diagnosticMode then
      let ray = Raylib.GetScreenToWorldRay(Raylib.GetMousePosition(), camera3D)
      let mutable bestDist = System.Single.MaxValue
      let mutable bestBuilding : FuncBuilding option = None
      for i in 0 .. buildingArray.Length - 1 do
        let b = buildingArray.[i]
        match rayIntersectsBox ray b with
        | Some dist when dist < bestDist ->
          bestDist <- dist
          bestBuilding <- Some b
        | _ -> ()
      highlighted <- bestBuilding
      if rb (Raylib.IsMouseButtonPressed(MouseButton.Left)) then
        match bestBuilding, selected with
        | Some hoveredBuilding, Some current
          when current.Func.QualifiedName = hoveredBuilding.Func.QualifiedName ->
          selected <- None
        | Some hoveredBuilding, _ ->
          selected <- Some hoveredBuilding
        | None, _ ->
          selected <- None
    else
      highlighted <- None

    let selectedIncomingAll, selectedOutgoingAll =
      match selected with
      | Some pinned ->
        let key = pinned.Func.QualifiedName
        incomingRelations |> Map.tryFind key |> Option.defaultValue [],
        outgoingRelations |> Map.tryFind key |> Option.defaultValue []
      | None -> [], []

    let renderedIncoming = selectedIncomingAll |> List.truncate maxRenderedRelations
    let renderedOutgoing = selectedOutgoingAll |> List.truncate maxRenderedRelations

    Raylib.BeginDrawing()
    Raylib.ClearBackground(dynSkyColor)

    if diagnosticMode then
      Raylib.BeginMode3D(camera3D)
      Raylib.DrawCube(Vector3(0.0f, 1.0f, 0.0f), 2.0f, 2.0f, 2.0f, Color.Red)
      Raylib.DrawCube(Vector3(5.0f, 0.5f, 0.0f), 1.0f, 1.0f, 1.0f, Color.Green)
      Raylib.DrawCube(Vector3(0.0f, 0.5f, 5.0f), 1.0f, 1.0f, 1.0f, Color.Blue)
      Raylib.DrawGrid(20, 1.0f)
      Raylib.EndMode3D()

    elif ssaoEnabled then
      // Update per-frame SSAO uniforms
      Raylib.SetShaderValue(ssaoShader, ssaoRadiusLoc, ssaoRadius, ShaderUniformDataType.Float)
      Raylib.SetShaderValue(ssaoShader, ssaoBiasLoc, ssaoBias, ShaderUniformDataType.Float)
      Raylib.SetShaderValue(compositeShader, ssaoStrengthLoc, ssaoStrength, ShaderUniformDataType.Float)

      // Pass 1: Scene → sceneRT (with sampleable depth)
      Raylib.BeginTextureMode(sceneRT)
      Raylib.ClearBackground(dynSkyColor)
      Raylib.BeginMode3D(camera3D)
      Raylib.DrawMesh(staticMesh, material, Matrix4x4.Identity)
      drawSelectionOverlay highlighted selected renderedIncoming renderedOutgoing showCallLinks
      Raylib.EndMode3D()
      Raylib.EndTextureMode()

      // Pass 2: SSAO — sample depth texture at full AO resolution
      Raylib.BeginTextureMode(ssaoRT)
      Raylib.ClearBackground(Color.White)
      Raylib.BeginShaderMode(ssaoShader)
      let srcDepth = Rectangle(0.0f, 0.0f, float32 screenW, float32 -screenH)
      let dstAo = Rectangle(0.0f, 0.0f, float32 aoW, float32 aoH)
      Raylib.DrawTexturePro(sceneRT.Depth, srcDepth, dstAo, Vector2.Zero, 0.0f, Color.White)
      Raylib.EndShaderMode()
      Raylib.EndTextureMode()

      // Pass 3: Bilateral blur (5×5, edge-preserving)
      Raylib.BeginTextureMode(blurRT)
      Raylib.ClearBackground(Color.White)
      Raylib.BeginShaderMode(blurShader)
      let srcAo = Rectangle(0.0f, 0.0f, float32 aoW, float32 -aoH)
      Raylib.DrawTextureRec(ssaoRT.Texture, srcAo, Vector2.Zero, Color.White)
      Raylib.EndShaderMode()
      Raylib.EndTextureMode()

      // Pass 4-6: Bloom (bright extract → H blur → V blur)
      if bloomEnabled then
        // Extract bright pixels from scene
        Raylib.BeginTextureMode(brightRT)
        Raylib.ClearBackground(Color.Black)
        Raylib.BeginShaderMode(brightExtractShader)
        let srcScene = Rectangle(0.0f, 0.0f, float32 screenW, float32 -screenH)
        let dstBloom = Rectangle(0.0f, 0.0f, float32 bloomW, float32 bloomH)
        Raylib.DrawTexturePro(sceneRT.Texture, srcScene, dstBloom, Vector2.Zero, 0.0f, Color.White)
        Raylib.EndShaderMode()
        Raylib.EndTextureMode()

        // Horizontal Gaussian blur
        let hDir = Vector2(1.0f / float32 bloomW, 0.0f)
        Raylib.SetShaderValue(bloomBlurShader, blurDirectionLoc, hDir, ShaderUniformDataType.Vec2)
        Raylib.BeginTextureMode(bloomHRT)
        Raylib.ClearBackground(Color.Black)
        Raylib.BeginShaderMode(bloomBlurShader)
        let srcBright = Rectangle(0.0f, 0.0f, float32 bloomW, float32 -bloomH)
        Raylib.DrawTextureRec(brightRT.Texture, srcBright, Vector2.Zero, Color.White)
        Raylib.EndShaderMode()
        Raylib.EndTextureMode()

        // Vertical Gaussian blur
        let vDir = Vector2(0.0f, 1.0f / float32 bloomH)
        Raylib.SetShaderValue(bloomBlurShader, blurDirectionLoc, vDir, ShaderUniformDataType.Vec2)
        Raylib.BeginTextureMode(bloomVRT)
        Raylib.ClearBackground(Color.Black)
        Raylib.BeginShaderMode(bloomBlurShader)
        let srcBloomH = Rectangle(0.0f, 0.0f, float32 bloomW, float32 -bloomH)
        Raylib.DrawTextureRec(bloomHRT.Texture, srcBloomH, Vector2.Zero, Color.White)
        Raylib.EndShaderMode()
        Raylib.EndTextureMode()

      // Final composite: scene × AO + bloom → screen
      if ssaoDebug then
        let srcAO = Rectangle(0.0f, 0.0f, float32 aoW, float32 -aoH)
        let dstFull = Rectangle(0.0f, 0.0f, float32 screenW, float32 screenH)
        Raylib.DrawTexturePro(blurRT.Texture, srcAO, dstFull, Vector2.Zero, 0.0f, Color.White)
      else
        Raylib.BeginShaderMode(compositeShader)
        Raylib.SetShaderValueTexture(compositeShader, aoTexLoc, blurRT.Texture)
        if bloomEnabled then
          Raylib.SetShaderValueTexture(compositeShader, bloomTexLoc, bloomVRT.Texture)
          Raylib.SetShaderValue(compositeShader, bloomIntensityLoc, bloomIntensity, ShaderUniformDataType.Float)
        else
          Raylib.SetShaderValue(compositeShader, bloomIntensityLoc, 0.0f, ShaderUniformDataType.Float)
        let srcScene = Rectangle(0.0f, 0.0f, float32 screenW, float32 -screenH)
        Raylib.DrawTextureRec(sceneRT.Texture, srcScene, Vector2.Zero, Color.White)
        Raylib.EndShaderMode()

    else
      // Direct render — no SSAO overhead
      Raylib.BeginMode3D(camera3D)
      Raylib.DrawMesh(staticMesh, material, Matrix4x4.Identity)
      drawSelectionOverlay highlighted selected renderedIncoming renderedOutgoing showCallLinks
      Raylib.EndMode3D()

    // 2D HUD
    let districtRects =
      districts |> List.choose (fun d ->
        blocks |> Array.tryFind (fun b -> b.Module = d.Name)
        |> Option.map (fun b ->
          (d, { X = b.Rect.X; Z = b.Rect.Z; W = b.Rect.W; D = b.Rect.H }, [])))
    drawDistrictLabels2D districtRects camera3D uiTextTheme
    drawHUD buildings districts (sortedRoads |> List.ofArray) mouseCaptured selected showCallLinks uiTextTheme
    drawLegend districts uiTextTheme
    drawHeatScale uiTextTheme
    drawSelectionPanel selected selectedIncomingAll selectedOutgoingAll showCallLinks uiTextTheme

    // SSAO + Bloom status line
    if ssaoEnabled then
      let ssaoLabel = if ssaoDebug then "SSAO: DEBUG" else "SSAO: ON"
      let bloomLabel = if bloomEnabled then sprintf "BLOOM: ON T=%.2f I=%.2f" bloomThreshold bloomIntensity else "BLOOM: OFF"
      drawUiText
        (sprintf "%s  R=%.1f B=%.3f S=%.2f | %s" ssaoLabel ssaoRadius ssaoBias ssaoStrength bloomLabel)
        10 (screenH - 26) uiTextTheme.Status Color.Yellow

    // Tooltip for highlighted building
    match highlighted with
    | Some b ->
      let mx = int (Raylib.GetMousePosition().X) + 16
      let my = int (Raylib.GetMousePosition().Y)
      drawTooltip b mx my uiTextTheme
    | None -> ()

    Raylib.EndDrawing()

  Raylib.CloseWindow()
  0
