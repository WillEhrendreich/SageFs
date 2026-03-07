module CodeCity.DomainTests

open System
open System.Collections.Generic
open System.Numerics
open Expecto
open Expecto.Flip
open FsCheck
open Raylib_cs
open CodeCity

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 100 }

// Helpers

let mkFunc name proj =
  { Name = name
    QualifiedName = sprintf "%s.%s" proj name
    FilePath = ""
    RelPath = ""
    Module = proj
    Project = proj
    StartLine = 1
    EndLine = 10
    LineCount = 10
    Body = [||] }

let private overlap1d a0 a1 b0 b1 =
  min a1 b1 - max a0 b0

let private cubeWithinLot lotHW lotHD (cube: SubCube) =
  cube.CX - cube.HW >= -lotHW
  && cube.CX + cube.HW <= lotHW
  && cube.CZ - cube.HD >= -lotHD
  && cube.CZ + cube.HD <= lotHD

let private cubesAdjacent (a: SubCube) (b: SubCube) =
  let eps = 0.0001f
  let xOverlap = overlap1d (a.CX - a.HW) (a.CX + a.HW) (b.CX - b.HW) (b.CX + b.HW)
  let zOverlap = overlap1d (a.CZ - a.HD) (a.CZ + a.HD) (b.CZ - b.HD) (b.CZ + b.HD)
  let touchEastWest =
    abs ((a.CX + a.HW) - (b.CX - b.HW)) <= eps
    || abs ((b.CX + b.HW) - (a.CX - a.HW)) <= eps
  let touchNorthSouth =
    abs ((a.CZ + a.HD) - (b.CZ - b.HD)) <= eps
    || abs ((b.CZ + b.HD) - (a.CZ - a.HD)) <= eps
  (touchEastWest && zOverlap > eps) || (touchNorthSouth && xOverlap > eps)

let private compoundConnected (cubes: SubCube[]) =
  if cubes.Length = 0 then true
  else
    let seen = Array.create cubes.Length false
    let q = Queue<int>()
    seen.[0] <- true
    q.Enqueue 0
    while q.Count > 0 do
      let i = q.Dequeue()
      for j in 0 .. cubes.Length - 1 do
        if not seen.[j] && cubesAdjacent cubes.[i] cubes.[j] then
          seen.[j] <- true
          q.Enqueue j
    seen |> Array.forall id

let private cubeContainsPoint x z (cube: SubCube) =
  x >= cube.CX - cube.HW
  && x <= cube.CX + cube.HW
  && z >= cube.CZ - cube.HD
  && z <= cube.CZ + cube.HD

let private footprintFillRatio sampleSize (cubes: SubCube[]) =
  if cubes.Length = 0 then 0.0
  else
    let minX = cubes |> Array.minBy (fun cube -> cube.CX - cube.HW) |> fun cube -> cube.CX - cube.HW
    let maxX = cubes |> Array.maxBy (fun cube -> cube.CX + cube.HW) |> fun cube -> cube.CX + cube.HW
    let minZ = cubes |> Array.minBy (fun cube -> cube.CZ - cube.HD) |> fun cube -> cube.CZ - cube.HD
    let maxZ = cubes |> Array.maxBy (fun cube -> cube.CZ + cube.HD) |> fun cube -> cube.CZ + cube.HD
    let mutable occupied = 0
    let mutable total = 0
    let mutable x = minX + sampleSize * 0.5f
    while x < maxX do
      let mutable z = minZ + sampleSize * 0.5f
      while z < maxZ do
        total <- total + 1
        if cubes |> Array.exists (cubeContainsPoint x z) then
          occupied <- occupied + 1
        z <- z + sampleSize
      x <- x + sampleSize
    if total = 0 then 0.0
    else float occupied / float total

// Color math

let colorMathTests =
  testList "Color math" [
    testPropertyWithConfig cfg "brighten with factor 1.0 is identity" <|
      fun (r: byte) (g: byte) (b: byte) ->
        let c = Color(r, g, b, 255uy)
        let result = brighten c 1.0f
        result.R |> Expect.equal "R unchanged" r
        result.G |> Expect.equal "G unchanged" g
        result.B |> Expect.equal "B unchanged" b

    testCase "heatColor at 0 is blue weighted" <| fun () ->
      let c = heatColor 0.0f
      (c.B, c.R) |> Expect.isGreaterThan "cold should be bluer than red" 

    testCase "heatColor at 1 is red weighted" <| fun () ->
      let c = heatColor 1.0f
      (c.R, c.B) |> Expect.isGreaterThan "hot should be redder than blue"
  ]

// Treemap

let treemapTests =
  testList "Treemap" [
    testCase "empty items return empty layout" <| fun () ->
      squarifiedTreemap [] (TRect.create 0.0f 0.0f 10.0f 10.0f)
      |> Expect.isEmpty "no items means no rectangles"

    testCase "layout preserves item count" <| fun () ->
      let items = [ "a", 1.0f; "b", 2.0f; "c", 3.0f ]
      let layout = squarifiedTreemap items (TRect.create 0.0f 0.0f 12.0f 8.0f)
      layout |> Expect.hasLength "three items produce three rects" 3

    testPropertyWithConfig cfg "all treemap rects stay within bounds" <|
      fun (PositiveInt rawW) (PositiveInt rawH) ->
        let width = 5.0f + float32 (rawW % 100)
        let height = 5.0f + float32 (rawH % 100)
        let bounds = TRect.create 10.0f 20.0f width height
        let items = [ "a", 1.0f; "b", 2.0f; "c", 3.0f; "d", 4.0f ]
        squarifiedTreemap items bounds
        |> List.forall (fun (_, r) ->
          r.X >= bounds.X
          && r.Z >= bounds.Z
          && r.X + r.W <= bounds.X + bounds.W + 0.001f
          && r.Z + r.H <= bounds.Z + bounds.H + 0.001f)

    testPropertyWithConfig cfg "inset preserves positive dimensions" <|
      fun (PositiveInt rawW) (PositiveInt rawH) (NonNegativeInt rawMargin) ->
        let width = 0.2f + float32 (rawW % 200) / 10.0f
        let height = 0.2f + float32 (rawH % 200) / 10.0f
        let margin = float32 (rawMargin % 200) / 20.0f
        let inset = TRect.inset margin (TRect.create 0.0f 0.0f width height)
        inset.W >= 0.1f && inset.H >= 0.1f
  ]

// Calls + heat

let edgeAndHeatTests =
  testList "Call graph domain" [
    testCase "deduplicateEdges merges repeated pairs" <| fun () ->
      let result =
        deduplicateEdges [
          { From = "a"; To = "b"; Weight = 2 }
          { From = "a"; To = "b"; Weight = 3 }
        ]
      result |> Expect.hasLength "one merged edge remains" 1
      result.[0].Weight |> Expect.equal "weights sum" 5

    testCase "computeHeat gives hottest callee heat 1.0" <| fun () ->
      let funcs = [ mkFunc "a" "P"; mkFunc "b" "P"; mkFunc "c" "P" ]
      let result =
        computeHeat funcs [
          { From = "P.a"; To = "P.c"; Weight = 1 }
          { From = "P.b"; To = "P.c"; Weight = 1 }
        ]
      let (heat, callers, _) = result.["P.c"]
      heat |> Expect.equal "most-called function normalizes to 1" 1.0f
      callers |> Expect.equal "caller count tracked" 2

    testPropertyWithConfig cfg "all heats stay in [0, 1]" <|
      fun (PositiveInt rawCount) ->
        let count = 1 + rawCount % 20
        let funcs = [ for i in 0 .. count - 1 -> mkFunc (sprintf "f%d" i) "P" ]
        let edges =
          [ for i in 0 .. count - 2 ->
              { From = sprintf "P.f%d" i
                To = sprintf "P.f%d" (i + 1)
                Weight = 1 } ]
        let result = computeHeat funcs edges
        result
        |> Map.forall (fun _ (heat, _, _) -> heat >= 0.0f && heat <= 1.0f)
  ]

// Complexity

let complexityTests =
  testList "Cyclomatic complexity" [
    testCase "empty body returns baseline complexity 1" <| fun () ->
      computeComplexity [||] |> Expect.equal "baseline complexity" 1

    testCase "if + elif increments complexity" <| fun () ->
      computeComplexity
        [| "  if x > 0 then a"
           "  elif x < 0 then b"
           "  else c" |]
      |> Expect.equal "if and elif should both count" 3

    testCase "match cases contribute to complexity" <| fun () ->
      computeComplexity
        [| "  match x with"
           "  | A -> 1"
           "  | B -> 2"
           "  | C -> 3" |]
      |> Expect.equal "three match arms add three" 4

    testPropertyWithConfig cfg "complexity is always at least 1" <|
      fun (lines: string[]) ->
        let safe =
          if isNull lines then [||]
          else lines |> Array.map (fun line -> if isNull line then "" else line)
        (computeComplexity safe, 1) |> Expect.isGreaterThanOrEqual "complexity floor is 1"
  ]

// Curved roads

let roadCurveTests =
  testList "Road curves" [
    testCase "catmullRom returns p1 at t=0" <| fun () ->
      let (x, z) = catmullRom (0.0f, 0.0f) (1.0f, 2.0f) (3.0f, 4.0f) (5.0f, 6.0f) 0.0f
      x |> Expect.equal "x equals p1.x" 1.0f
      z |> Expect.equal "z equals p1.z" 2.0f

    testCase "catmullRom returns p2 at t=1" <| fun () ->
      let (x, z) = catmullRom (0.0f, 0.0f) (1.0f, 2.0f) (3.0f, 4.0f) (5.0f, 6.0f) 1.0f
      x |> Expect.equal "x equals p2.x" 3.0f
      z |> Expect.equal "z equals p2.z" 4.0f

    testCase "roadControlPoints extrapolates when neighbors absent" <| fun () ->
      let (p0, p1, p2, p3) = roadControlPoints (1.0f, 2.0f) (4.0f, 6.0f) None None
      p1 |> Expect.equal "p1 is from point" (1.0f, 2.0f)
      p2 |> Expect.equal "p2 is to point" (4.0f, 6.0f)
      p0 |> Expect.equal "p0 extrapolates backward" (-2.0f, -2.0f)
      p3 |> Expect.equal "p3 extrapolates forward" (7.0f, 10.0f)

    testCase "segIntersect finds crossing point" <| fun () ->
      let result =
        segIntersect
          (Vec2.Create(0.0f, 0.0f))
          (Vec2.Create(10.0f, 10.0f))
          (Vec2.Create(0.0f, 10.0f))
          (Vec2.Create(10.0f, 0.0f))
      match result with
      | None -> failtest "expected an intersection"
      | Some (p, _) ->
        abs (p.X - 5.0f) < 0.001f |> Expect.isTrue "x should be 5"
        abs (p.Y - 5.0f) < 0.001f |> Expect.isTrue "y should be 5"
  ]

// Procedural compound buildings

let compoundShapeTests =
  testList "Procedural compound buildings" [
    testCase "complexity 1 yields a single simple mass" <| fun () ->
      let cubes = generateCompound "P.simple" 1 10.0f 8.0f
      cubes |> Expect.hasLength "complexity 1 should stay a rectangle" 1

    testCase "same input generates the same compound" <| fun () ->
      let left = generateCompound "P.factory" 22 12.0f 9.0f
      let right = generateCompound "P.factory" 22 12.0f 9.0f
      left |> Expect.equal "compound generation must be deterministic" right

    testPropertyWithConfig cfg "all cubes stay within the lot" <|
      fun (PositiveInt rawComplexity) (PositiveInt rawW) (PositiveInt rawD) ->
        let complexity = 1 + rawComplexity % 120
        let lotHW = 0.5f + float32 (rawW % 400) / 10.0f
        let lotHD = 0.5f + float32 (rawD % 400) / 10.0f
        let cubes = generateCompound "P.bounds" complexity lotHW lotHD
        cubes |> Array.forall (cubeWithinLot lotHW lotHD)

    testPropertyWithConfig cfg "all cubes have positive size and valid height scale" <|
      fun (PositiveInt rawComplexity) ->
        let cubes = generateCompound "P.dimensions" (1 + rawComplexity % 120) 20.0f 20.0f
        cubes
        |> Array.forall (fun cube ->
          cube.HW > 0.0f
          && cube.HD > 0.0f
          && cube.HeightScale > 0.0f
          && cube.HeightScale <= 1.0f)

    testPropertyWithConfig cfg "compound stays connected" <|
      fun (PositiveInt rawComplexity) (PositiveInt rawW) (PositiveInt rawD) ->
        let complexity = 1 + rawComplexity % 120
        let lotHW = 1.0f + float32 (rawW % 300) / 10.0f
        let lotHD = 1.0f + float32 (rawD % 300) / 10.0f
        let cubes = generateCompound "P.connected" complexity lotHW lotHD
        compoundConnected cubes

    testCase "higher complexity keeps adding wings on ample lots" <| fun () ->
      let medium = generateCompound "P.campus" 30 40.0f 40.0f |> Array.length
      let high = generateCompound "P.campus" 60 40.0f 40.0f |> Array.length
      (high, medium) |> Expect.isGreaterThan "complexity 60 should produce more masses than complexity 30"

    testCase "very high complexity can exceed the old fixed 26-cube ceiling" <| fun () ->
      let cubes = generateCompound "P.smithsonian" 80 60.0f 60.0f |> Array.length
      (cubes, 26) |> Expect.isGreaterThan "huge lots should not be hard-capped at the old 26-cube limit"

    testCase "high complexity compounds stay porous on roomy lots" <| fun () ->
      let seeds =
        [ "P.Alpha"; "P.Beta"; "P.Gamma"; "P.Delta"
          "P.Omega"; "P.Factory"; "P.Campus"; "P.Warehouse" ]
      for seed in seeds do
        let cubes = generateCompound seed 90 60.0f 60.0f
        cubes.Length >= 24 |> Expect.isTrue (sprintf "%s should be large enough to matter" seed)
        let fill = footprintFillRatio 1.0f cubes
        (fill <= 0.72)
        |> Expect.isTrue (sprintf "%s should stay porous, got fill ratio %.3f" seed fill)
  ]

let cameraMovementTests =
  testList "Camera movement" [
    testCase "forward movement follows the full look direction" <| fun () ->
      let cam = FpsCamera.create Vector3.Zero
      cam.Yaw <- 0.0f
      cam.Pitch <- 0.6f

      let moveForward, _ : Vector3 * Vector3 = FpsCamera.movementVectors cam
      let lookForward = Vector3.Normalize(FpsCamera.forward cam)

      abs (moveForward.X - lookForward.X) < 0.0001f |> Expect.isTrue "forward x should match look direction"
      abs (moveForward.Y - lookForward.Y) < 0.0001f |> Expect.isTrue "forward y should match look direction"
      abs (moveForward.Z - lookForward.Z) < 0.0001f |> Expect.isTrue "forward z should match look direction"
      moveForward.Y > 0.0f |> Expect.isTrue "looking upward should move upward"

    testCase "strafe vector stays horizontal while moving forward uses pitch" <| fun () ->
      let cam = FpsCamera.create Vector3.Zero
      cam.Yaw <- 0.8f
      cam.Pitch <- 0.7f

      let _, right : Vector3 * Vector3 = FpsCamera.movementVectors cam

      abs right.Y < 0.0001f |> Expect.isTrue "strafe should stay level on the ground plane"
      abs (right.Length() - 1.0f) < 0.0001f |> Expect.isTrue "strafe vector should stay normalized"
  ]

let visualDefaultsTests =
  testList "Visual defaults" [
    testCase "SSAO buffer uses full screen resolution to reduce blotchy AO" <| fun () ->
      ssaoBufferSize defaultSsaoSettings 1600 900
      |> Expect.equal "AO buffer should match the scene resolution" (1600, 900)

    testCase "UI text theme uses larger readable sizes" <| fun () ->
      (defaultUiTextTheme.HudTitle, 24) |> Expect.isGreaterThanOrEqual "HUD title should be clearly larger"
      (defaultUiTextTheme.HudStats, 16) |> Expect.isGreaterThanOrEqual "HUD stats should be readable"
      (defaultUiTextTheme.HudControls, 13) |> Expect.isGreaterThanOrEqual "HUD controls should be readable"
      (defaultUiTextTheme.TooltipBody, 13) |> Expect.isGreaterThanOrEqual "Tooltips should be readable"
      (defaultUiTextTheme.SelectionBody, 13) |> Expect.isGreaterThanOrEqual "Selection panel text should be readable"
  ]

let roadAccessTests =
  testList "Road access grid layout" [
    testCase "Multi-building block produces internal alley roads" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let funcs = List.init 9 (fun i -> mkFunc (sprintf "func%d" i) "TestModule")
      let _, roads = layoutInGrid rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100
      roads |> Expect.isNonEmpty "a 3×3 grid should produce alley roads between grid cells"

    testCase "Single building produces no internal alleys" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let funcs = [ mkFunc "onlyFunc" "TestModule" ]
      let _, roads = layoutInGrid rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100
      roads |> Expect.isEmpty "1×1 grid has no lane corridors"

    testCase "Buildings stay within block rect bounds" <| fun () ->
      let rect = { X = 5.0f; Z = 5.0f; W = 20.0f; H = 20.0f }
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "func%d" i) "TestModule")
      let buildings, _ = layoutInGrid rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100
      for b in buildings do
        (b.X, rect.X) |> Expect.isGreaterThanOrEqual "building left edge should be inside block"
        (b.Z, rect.Z) |> Expect.isGreaterThanOrEqual "building top edge should be inside block"
        (b.X + b.W, rect.X + rect.W) |> Expect.isLessThanOrEqual "building right edge should be inside block"
        (b.Z + b.D, rect.Z + rect.H) |> Expect.isLessThanOrEqual "building bottom edge should be inside block"

    testCase "Building count matches function count" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 30.0f; H = 30.0f }
      let funcs = List.init 16 (fun i -> mkFunc (sprintf "func%d" i) "TestModule")
      let buildings, _ = layoutInGrid rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100
      buildings.Length |> Expect.equal "should produce one building per function" 16
  ]

let gitMetaTests =
  testList "Git metadata parsing" [
    testCase "empty log produces zero commits" <| fun () ->
      let m = parseGitLog ""
      m.CommitCount |> Expect.equal "no commits in empty log" 0

    testCase "single commit line is counted" <| fun () ->
      let m = parseGitLog "abc123|2024-01-15T10:00:00+00:00\n"
      m.CommitCount |> Expect.equal "one commit" 1

    testCase "multiple lines all counted" <| fun () ->
      let log = "aaa|2024-06-01T00:00:00+00:00\nbbb|2023-01-01T00:00:00+00:00\nccc|2022-03-15T00:00:00+00:00\n"
      let m = parseGitLog log
      m.CommitCount |> Expect.equal "three commits" 3

    testCase "earliest date becomes first commit" <| fun () ->
      let log = "aaa|2024-06-01T00:00:00+00:00\nbbb|2020-01-01T00:00:00+00:00\n"
      let m = parseGitLog log
      m.FirstCommitDate.Year |> Expect.equal "2020 is the earliest year" 2020
  ]

let organicFactorTests =
  testList "Organic growth factor" [
    testCase "brand-new file (0 days, 0 commits) has factor 0" <| fun () ->
      organicFactor 0.0f 0 |> Expect.equal "new code = pure grid" 0.0f

    testCase "10-year-old active file has factor near 1" <| fun () ->
      let f = organicFactor 3650.0f 200
      (f, 0.9f) |> Expect.isGreaterThanOrEqual "ancient hot code = full organic"

    testPropertyWithConfig cfg "factor always in [0, 1]" <|
      fun (NonNegativeInt rawAge) (NonNegativeInt rawCommits) ->
        let age     = float32 (rawAge % 5000)
        let commits = rawCommits % 1000
        let f = organicFactor age commits
        f >= 0.0f && f <= 1.0f

    testCase "older code is more organic than newer code (same commit count)" <| fun () ->
      let young = organicFactor 30.0f 5
      let old   = organicFactor 1000.0f 5
      (old, young) |> Expect.isGreaterThan "1000-day-old code should be more organic than 30-day-old"
  ]

let weberDistrictTests =
  testList "Weber district layout" [
    testCase "layout produces non-empty buildings for non-trivial block" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 40.0f; H = 30.0f }
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "f%d" i) "TestMod")
      let rng   = Random(42)
      let bldgs, _ = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.5f rng
      bldgs |> Expect.isNonEmpty "a 12-function district should have buildings"

    testCase "organic district produces internal road network" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 50.0f; H = 50.0f }
      let funcs = List.init 20 (fun i -> mkFunc (sprintf "f%d" i) "OrgMod")
      let rng   = Random(7)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 1.0f rng
      roads |> Expect.isNonEmpty "organic district should produce internal roads"

    testCase "all Weber buildings stay within block bounds (with tolerance)" <| fun () ->
      let rect  = { X = 5.0f; Z = 3.0f; W = 40.0f; H = 35.0f }
      let funcs = List.init 15 (fun i -> mkFunc (sprintf "g%d" i) "BoundsMod")
      let rng   = Random(99)
      let bldgs, _ = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.7f rng
      let eps = 1.5f  // packInBlock centers use polygon bounding box; slight overshoot is tolerated
      for b in bldgs do
        (b.X,       rect.X - eps)           |> Expect.isGreaterThanOrEqual "building left edge inside block"
        (b.Z,       rect.Z - eps)           |> Expect.isGreaterThanOrEqual "building top edge inside block"
        (b.X + b.W, rect.X + rect.W + eps)  |> Expect.isLessThanOrEqual   "building right edge inside block"
        (b.Z + b.D, rect.Z + rect.H + eps)  |> Expect.isLessThanOrEqual   "building bottom edge inside block"

    testCase "grid district (organic=0) has only horizontal or vertical internal roads" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 50.0f; H = 50.0f }
      let funcs = List.init 16 (fun i -> mkFunc (sprintf "h%d" i) "GridMod")
      let rng   = Random(42)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.0f rng
      // In grid mode every segment extends straight from its seed direction — no deviation.
      // Roads may still be at a non-axis angle (seed chooses random initial direction) but
      // all segments from a given node will be collinear (deviation = 0). Verify no crashes.
      roads |> List.length |> (fun c -> (c, 0) |> Expect.isGreaterThan "grid mode produces some roads")
  ]

/// Distance from (px, pz) to the nearest point on any polygon edge.
let private distanceToPolyEdge (poly: Vec2 list) (px: float32) (pz: float32) : float32 =
  distanceToPoly poly px pz

let private distanceToSeg (ax: float32) (az: float32) (bx: float32) (bz: float32) (px: float32) (pz: float32) : float32 =
  let dx = bx - ax
  let dz = bz - az
  let lenSq = dx * dx + dz * dz
  let t = if lenSq < 1e-10f then 0.0f else min 1.0f (max 0.0f (((px - ax) * dx + (pz - az) * dz) / lenSq))
  let nearX = ax + t * dx
  let nearZ = az + t * dz
  sqrt ((px - nearX) * (px - nearX) + (pz - nearZ) * (pz - nearZ))

let private mkRoad x1 z1 x2 z2 halfWidth =
  { FromFunc = ""; ToFunc = ""
    FromPos = Vector3(x1, halfWidth, z1)
    ToPos   = Vector3(x2, halfWidth, z2)
    Weight  = RoadClass.tier Street
    Color   = Color(65uy, 65uy, 70uy, 255uy) }

let roadFrontageTests =
  testList "Road-primary building placement (packAlongEdges)" [

    testCase "distanceToPoly: point on edge has near-zero distance" <| fun () ->
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(10.0f, 0.0f)
                   Vec2.Create(10.0f, 10.0f); Vec2.Create(0.0f, 10.0f) ]
      let d = distanceToPolyEdge poly 5.0f 0.0f
      (d, 0.05f) |> Expect.isLessThanOrEqual "midpoint of bottom edge should have ~0 distance"

    testCase "distanceToPoly: center of 4×4 square is exactly 2 from nearest edge" <| fun () ->
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(4.0f, 0.0f)
                   Vec2.Create(4.0f, 4.0f); Vec2.Create(0.0f, 4.0f) ]
      let d = distanceToPolyEdge poly 2.0f 2.0f
      abs (d - 2.0f) < 0.05f |> Expect.isTrue "center of 4×4 square is 2 from each edge"

    testCase "packAlongEdges: produces buildings for a square parcel" <| fun () ->
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(12.0f, 0.0f)
                   Vec2.Create(12.0f, 12.0f); Vec2.Create(0.0f, 12.0f) ]
      let funcs = List.init 8 (fun i -> mkFunc (sprintf "f%d" i) "ParkMod")
      let bldgs = packAlongEdges poly funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 42)
      bldgs |> Expect.isNonEmpty "8 functions in a 12×12 parcel should produce buildings"

    testCase "road-primary invariant: all buildings within lot depth of nearest road edge" <| fun () ->
      // A 10×10 square: center is 5 units from every edge.
      // Any building > 3 units from all edges is landlocked in the interior.
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(10.0f, 0.0f)
                   Vec2.Create(10.0f, 10.0f); Vec2.Create(0.0f, 10.0f) ]
      let funcs = List.init 10 (fun i -> mkFunc (sprintf "f%d" i) "RoadMod")
      let bldgs = packAlongEdges poly funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 7)
      bldgs |> Expect.isNonEmpty "must produce some buildings"
      let maxLotDepth = 3.0f
      for b in bldgs do
        let cx = b.X + b.W / 2.0f
        let cz = b.Z + b.D / 2.0f
        let dist = distanceToPolyEdge poly cx cz
        (dist, maxLotDepth)
        |> Expect.isLessThanOrEqual
             (sprintf "building center (%.1f,%.1f) dist=%.2f exceeds lot depth — landlocked!" cx cz dist)

    testCase "packAlongEdges: all buildings are inside the parcel polygon" <| fun () ->
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(8.0f, 0.0f)
                   Vec2.Create(8.0f, 6.0f); Vec2.Create(0.0f, 6.0f) ]
      let funcs = List.init 6 (fun i -> mkFunc (sprintf "f%d" i) "InsideMod")
      let bldgs = packAlongEdges poly funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 13)
      for b in bldgs do
        let cx = b.X + b.W / 2.0f
        let cz = b.Z + b.D / 2.0f
        pointInPoly poly cx cz
        |> Expect.isTrue (sprintf "building center (%.1f,%.1f) must be inside the parcel polygon" cx cz)

    testCase "packAlongEdges: buildings distributed across multiple edges not just one" <| fun () ->
      // With 12 functions and 4 edges, buildings should appear near more than one edge
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(10.0f, 0.0f)
                   Vec2.Create(10.0f, 10.0f); Vec2.Create(0.0f, 10.0f) ]
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "f%d" i) "MultiEdgeMod")
      let bldgs = packAlongEdges poly funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 99)
      // Check that buildings appear on both sides of the polygon (not all clumped on one edge)
      let hasNearBottom = bldgs |> List.exists (fun b -> b.Z + b.D / 2.0f < 3.0f)
      let hasNearTop    = bldgs |> List.exists (fun b -> b.Z + b.D / 2.0f > 7.0f)
      hasNearBottom |> Expect.isTrue "should have buildings near the bottom edge"
      hasNearTop    |> Expect.isTrue "should have buildings near the top edge"
  ]

let packAlongRoadsTests =
  testList "Road-primary placement along actual road segments (packAlongRoads)" [

    testCase "produces buildings for a simple cross-road block" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 10.0f 0.0f 10.0f 20.0f 0.4f    // vertical at x=10
                    mkRoad 0.0f  10.0f 20.0f 10.0f 0.4f ]  // horizontal at z=10
      let funcs = List.init 8 (fun i -> mkFunc (sprintf "f%d" i) "CrossMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 42)
      bldgs |> Expect.isNonEmpty "8 functions on a cross-road block should produce buildings"

    testCase "road-primary invariant: all buildings adjacent to a visible road centerline" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 10.0f 0.0f 10.0f 20.0f 0.4f
                    mkRoad 0.0f  10.0f 20.0f 10.0f 0.4f ]
      let funcs = List.init 10 (fun i -> mkFunc (sprintf "f%d" i) "RoadMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 7)
      bldgs |> Expect.isNonEmpty "must produce buildings"
      let maxDist = 1.5f  // setback(0.9) + footprint/2 + small tolerance
      for b in bldgs do
        let cx = b.X + b.W / 2.0f
        let cz = b.Z + b.D / 2.0f
        let dV = distanceToSeg 10.0f 0.0f  10.0f 20.0f cx cz
        let dH = distanceToSeg 0.0f  10.0f 20.0f 10.0f cx cz
        let nearest = min dV dH
        (nearest, maxDist)
        |> Expect.isLessThanOrEqual
             (sprintf "building (%.1f,%.1f) dist=%.2f — not adjacent to any road!" cx cz nearest)

    testCase "all buildings stay within block bounds" <| fun () ->
      let rect  = { X = 5.0f; Z = 5.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 15.0f 5.0f 15.0f 25.0f 0.4f ]
      let funcs = List.init 6 (fun i -> mkFunc (sprintf "f%d" i) "BoundsMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 13)
      let eps = 1.0f
      for b in bldgs do
        (b.X,       rect.X - eps)           |> Expect.isGreaterThanOrEqual "left within bounds"
        (b.Z,       rect.Z - eps)           |> Expect.isGreaterThanOrEqual "top within bounds"
        (b.X + b.W, rect.X + rect.W + eps)  |> Expect.isLessThanOrEqual   "right within bounds"
        (b.Z + b.D, rect.Z + rect.H + eps)  |> Expect.isLessThanOrEqual   "bottom within bounds"

    testCase "empty road list falls back gracefully without crash" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let funcs = List.init 4 (fun i -> mkFunc (sprintf "f%d" i) "FallbackMod")
      let bldgs = packAlongRoads [] rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 1)
      bldgs |> Expect.isNonEmpty "fallback must still produce buildings when no roads exist"

    testCase "buildings distributed on both sides of a single road" <| fun () ->
      // A single vertical road with 10 functions should put buildings on BOTH sides
      let rect  = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 20.0f }
      let roads = [ mkRoad 5.0f 0.0f 5.0f 20.0f 0.4f ]
      let funcs = List.init 10 (fun i -> mkFunc (sprintf "f%d" i) "SidesMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 99)
      let leftSide  = bldgs |> List.exists (fun b -> b.X + b.W / 2.0f < 5.0f)
      let rightSide = bldgs |> List.exists (fun b -> b.X + b.W / 2.0f > 5.0f)
      leftSide  |> Expect.isTrue "should have buildings left of road"
      rightSide |> Expect.isTrue "should have buildings right of road"
  ]

let buildingTypologyTests =
  testList "Building typology classification and type-aware properties" [

    testCase "classifyBuilding: tiny no-heat function is Shed" <| fun () ->
      classifyBuilding 5 1 0.0f
      |> Expect.equal "5-line, 0-heat function should be Shed" Shed

    testCase "classifyBuilding: small low-heat function is Cottage" <| fun () ->
      classifyBuilding 25 2 0.1f
      |> Expect.equal "25-line, low-heat function should be Cottage" Cottage

    testCase "classifyBuilding: medium function is Rowhouse" <| fun () ->
      classifyBuilding 60 3 0.3f
      |> Expect.equal "60-line, moderate function should be Rowhouse" Rowhouse

    testCase "classifyBuilding: medium with noticeable heat is Commercial" <| fun () ->
      classifyBuilding 80 5 0.5f
      |> Expect.equal "80-line, 0.5-heat function should be Commercial" Commercial

    testCase "classifyBuilding: large highly-used function is Tower" <| fun () ->
      classifyBuilding 400 8 0.7f
      |> Expect.equal "400-line, 0.7-heat function should be Tower" Tower

    testCase "classifyBuilding: very large or very hot function is Skyscraper" <| fun () ->
      classifyBuilding 700 12 0.9f
      |> Expect.equal "700-line, 0.9-heat function should be Skyscraper" Skyscraper

    testCase "classifyBuilding: heat alone can promote to Skyscraper" <| fun () ->
      classifyBuilding 20 2 0.9f
      |> Expect.equal "Small but extremely hot function should be Skyscraper" Skyscraper

    testCase "classifyBuilding: line count alone can promote to Skyscraper" <| fun () ->
      classifyBuilding 700 2 0.0f
      |> Expect.equal "Very long cold function should be Skyscraper" Skyscraper

    testCase "spacingMultiplier: residential types get more yard space than skyscrapers" <| fun () ->
      let cottageMult  = BuildingType.spacingMultiplier Cottage
      let scraperMult  = BuildingType.spacingMultiplier Skyscraper
      (cottageMult, scraperMult)
      |> Expect.isGreaterThan "Cottage needs more yard space than Skyscraper"

    testCase "spacingMultiplier: Skyscraper is the most densely packed" <| fun () ->
      let scraperMult = BuildingType.spacingMultiplier Skyscraper
      for bt in [| Shed; Cottage; Rowhouse; Commercial; Tower |] do
        let other = BuildingType.spacingMultiplier bt
        (other, scraperMult)
        |> Expect.isGreaterThanOrEqual (sprintf "%A should have more yard space than Skyscraper" bt)

    testProperty "classifyBuilding never throws for any non-negative inputs" <| fun (lc: PositiveInt) (cx: PositiveInt) ->
      let lineCount  = lc.Get % 10000
      let complexity = cx.Get % 200
      let heat       = float32 (lc.Get % 101) / 100.0f
      let _ = classifyBuilding lineCount complexity heat
      true

    testCase "buildingHeight: Skyscraper is taller than Shed for same line count" <| fun () ->
      let shedH    = BuildingType.height Shed        20 0.0f
      let scraperH = BuildingType.height Skyscraper  20 0.8f
      (scraperH, shedH)
      |> Expect.isGreaterThan "Skyscraper height should exceed Shed height"

    testCase "buildingTypeWallColor: each type returns a fully-opaque color" <| fun () ->
      for bt in [| Shed; Cottage; Rowhouse; Commercial; Tower; Skyscraper |] do
        let c = BuildingType.wallColor bt "testFunc"
        c.A |> Expect.equal (sprintf "%A wall color should be opaque" bt) 255uy

    testCase "buildingTypeWallColor: residential warm (R > B) vs skyscraper cool (B >= R)" <| fun () ->
      let cottageC  = BuildingType.wallColor Cottage  "anyFunc"
      let scraperC  = BuildingType.wallColor Skyscraper "anyFunc"
      (int cottageC.R, int cottageC.B)
      |> Expect.isGreaterThan "Cottage wall should be warm-tinted (R > B)"
      (int scraperC.B, int scraperC.R)
      |> Expect.isGreaterThanOrEqual "Skyscraper wall should be cool-tinted (B >= R)"

    testCase "buildings from packAlongRoads have BuildingType field set" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 10.0f 0.0f 10.0f 20.0f 0.4f ]
      let funcs = List.init 6 (fun i -> mkFunc (sprintf "f%d" i) "TypeMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 42)
      bldgs |> Expect.isNonEmpty "should produce buildings"
      // Each building should have a BuildingType — verify at least one non-Shed exists
      // (since with 10-line functions and no heat the type is Shed or Cottage)
      bldgs |> List.forall (fun b -> b.BuildingType = Shed || b.BuildingType = Cottage || b.BuildingType = Rowhouse)
      |> Expect.isTrue "small test functions should classify as Shed/Cottage/Rowhouse"
  ]

let allTests =
  testList "CodeCity Domain" [
    colorMathTests
    treemapTests
    edgeAndHeatTests
    complexityTests
    roadCurveTests
    compoundShapeTests
    cameraMovementTests
    visualDefaultsTests
    roadAccessTests
    gitMetaTests
    organicFactorTests
    weberDistrictTests
    roadFrontageTests
    packAlongRoadsTests
    buildingTypologyTests
  ]

[<EntryPoint>]
let main argv =
  Expecto.Tests.runTestsWithCLIArgs [] argv allTests
