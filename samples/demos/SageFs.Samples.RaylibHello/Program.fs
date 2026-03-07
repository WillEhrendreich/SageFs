// ============================================================
//  🎮  Raylib Hello World — SageFs Edition
//  A GPU-rendered window that hot-reloads when you save.
//  Change the color, the text, the layout — save — it's live.
//  No restart. The window keeps running.
// ============================================================
//
//  Dependencies: Raylib-cs  (in Directory.Packages.props)
//  Run via: sagefs gui   (or load in a SageFs session)

module SageFs.Samples.RaylibHello.Program

#nowarn "3391" // implicit CBool -> bool conversion from Raylib-cs

open Raylib_cs
open System.Numerics

// ── Everything that changes goes here — make it a function ──
// SageFs hot-patches function bodies at runtime (via Harmony).
// Put your rendering logic in a top-level function and it will
// update live when you save.

// ┌─ HOT RELOAD ZONE: edit anything below, save, see it update ─┐

let backgroundColor = Color.RayWhite   // try: Color.SkyBlue, Color.DarkGray

let titleText = "Hello from SageFs + Raylib! 🦅"
let subtitleText = "Edit me and save. No restart. Just magic."

let drawFrame (time: float32) =
  // Clear the screen — ALWAYS required before drawing.
  // BeginDrawing() sets up the render state but does NOT clear the framebuffer.
  // Without ClearBackground(), the previous frame's content persists → smearing artifacts.
  Raylib.ClearBackground(backgroundColor)

  // Title
  Raylib.DrawText(titleText, 80, 120, 36, Color.DarkGray)

  // Subtitle
  Raylib.DrawText(subtitleText, 80, 175, 20, Color.Gray)

  // Animated circle — bounces left/right
  let t = time * 1.5f
  let cx = int (400.0f + System.MathF.Sin(t) * 250.0f)
  let cy = 300
  Raylib.DrawCircle(cx, cy, 30.0f, Color.SkyBlue)
  Raylib.DrawCircleLines(cx, cy, 30.0f, Color.DarkBlue)

  // Pulsing ring
  let pulse = 20.0f + 10.0f * System.MathF.Abs(System.MathF.Sin(t * 0.5f))
  Raylib.DrawCircleLines(400, 300, pulse, Color.Purple)

  // FPS counter
  Raylib.DrawFPS(10, 10)

  // Hot-reload hint
  Raylib.DrawText("💾 Save this file to see hot reload in action", 80, 530, 16, Color.LightGray)

// └─────────────────────────────────────────────────────────────┘

// ── Window setup — runs once ──
let screenWidth  = 800
let screenHeight = 600

[<EntryPoint>]
let main _argv =
  Raylib.InitWindow(screenWidth, screenHeight, "SageFs + Raylib Demo")
  Raylib.SetTargetFPS(60)

  // ── Game loop ──
  while not (Raylib.WindowShouldClose()) do
    let time = Raylib.GetTime() |> float32

    Raylib.BeginDrawing()
    // IMPORTANT: ClearBackground MUST be called inside BeginDrawing/EndDrawing.
    // BeginDrawing() sets up the render state but does NOT clear the framebuffer.
    // Without ClearBackground(), previous frame content persists → smearing artifacts.
    drawFrame time
    Raylib.EndDrawing()

  Raylib.CloseWindow()
  0

// ── What to try ──
// 1. Change `backgroundColor` to Color.DarkPurple — save — instant!
// 2. Change the title string — save — updates in the running window
// 3. Add a second animated shape in `drawFrame`
// 4. Try Raylib.DrawRectangle, Raylib.DrawTriangle, Raylib.DrawLine
// 5. Change the animation formula — sin → cos, multiply speed

// ── SageFs hot reload: how it works here ──
// • You save the file
// • SageFs sends it to F# Interactive (~100ms)
// • Harmony patches the `drawFrame` function pointer in-memory
// • Next frame, the game loop calls the NEW `drawFrame`
// • No window close. No app restart. Zero interruption.
// This is the same mechanism used for web app hot reload —
// one runtime, patched live.
