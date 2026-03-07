open System

// ── Organic Post-Processing ──
// Deterministic jitter: rotation and position offset from name hash

let hashJitter (name: string) : float32 =
  let h = abs (name.GetHashCode())
  float32 (h % 1000) / 1000.0f  // 0.0 .. 0.999

/// Building rotation jitter: ±maxDeg degrees, deterministic from name
let rotationJitter (name: string) (maxDeg: float32) : float32 =
  let t = hashJitter name
  (t - 0.5f) * 2.0f * maxDeg  // range: -maxDeg .. +maxDeg

/// Position offset jitter: small X,Z displacement within ±maxOffset
let positionJitter (name: string) (maxOffset: float32) : float32 * float32 =
  let h = abs (name.GetHashCode())
  let tx = float32 ((h / 7) % 1000) / 1000.0f
  let tz = float32 ((h / 13) % 1000) / 1000.0f
  ((tx - 0.5f) * 2.0f * maxOffset, (tz - 0.5f) * 2.0f * maxOffset)

// ── Tests ──
let mutable passed = 0
let mutable failed = 0
let check (name: string) (cond: bool) =
  if cond then passed <- passed + 1; printfn "  ✅ %s" name
  else failed <- failed + 1; printfn "  ❌ %s" name

printfn "=== Organic Jitter Tests ==="

// 1. hashJitter returns values in [0, 1)
for name in ["hello"; "world"; "MyModule.MyFunc"; ""; "🎉"] do
  let v = hashJitter name
  check (sprintf "hashJitter '%s' in [0,1)" name) (v >= 0.0f && v < 1.0f)

// 2. rotationJitter is deterministic
let r1 = rotationJitter "test.func" 5.0f
let r2 = rotationJitter "test.func" 5.0f
check "rotation jitter deterministic" (r1 = r2)

// 3. rotationJitter within bounds
for name in ["a"; "b"; "c"; "SomeModule.DoStuff"; "X.Y.Z.W"] do
  let r = rotationJitter name 5.0f
  check (sprintf "rotation '%s' within ±5°" name) (r >= -5.0f && r <= 5.0f)

// 4. Different names → different rotations (at least some variation)
let rotations = [ for i in 1..20 -> rotationJitter (sprintf "func%d" i) 5.0f ]
let distinct = rotations |> List.distinct |> List.length
check "20 names → multiple distinct rotations" (distinct > 5)

// 5. positionJitter deterministic
let (px1, pz1) = positionJitter "test" 0.3f
let (px2, pz2) = positionJitter "test" 0.3f
check "position jitter deterministic" (px1 = px2 && pz1 = pz2)

// 6. positionJitter within bounds
for name in ["alpha"; "beta"; "gamma"; "Module.Func"; ""] do
  let (dx, dz) = positionJitter name 0.5f
  check (sprintf "pos jitter '%s' X within ±0.5" name) (dx >= -0.5f && dx <= 0.5f)
  check (sprintf "pos jitter '%s' Z within ±0.5" name) (dz >= -0.5f && dz <= 0.5f)

// 7. Zero max → zero jitter
let r0 = rotationJitter "anything" 0.0f
check "zero maxDeg → zero rotation" (r0 = 0.0f)
let (dx0, dz0) = positionJitter "anything" 0.0f
check "zero maxOffset → zero position" (dx0 = 0.0f && dz0 = 0.0f)

// 8. Different max → proportional scaling
let r3 = rotationJitter "func1" 3.0f
let r10 = rotationJitter "func1" 10.0f
// Same name, so same normalized value; r10/r3 should be ~10/3
let expectedRatio = 10.0f / 3.0f
let actualRatio = if abs r3 > 0.001f then abs r10 / abs r3 else expectedRatio
check (sprintf "scaling: r10/r3 ratio %.2f ≈ 3.33" actualRatio) (abs (actualRatio - expectedRatio) < 0.01f)

printfn "\n=== Results: %d passed, %d failed ===" passed failed
if failed > 0 then exit 1
