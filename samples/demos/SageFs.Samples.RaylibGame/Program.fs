// ============================================================
//  🕹️  Raylib Interactive Game Demo — SageFs Edition
//  A playable game where you can tweak rules and physics live.
//  Catch the falling stars. Edit the speed. Save. It applies immediately.
// ============================================================
//
//  Dependencies: Raylib-cs  (in Directory.Packages.props)
//  Run via: sagefs gui   (or load in a SageFs session)

module SageFs.Samples.RaylibGame.Program

#nowarn "3391" // implicit CBool -> bool conversion from Raylib-cs

open Raylib_cs
open System.Numerics

// ── Game state (pure F# records) ──
type Star = {
  X:         float32
  Y:         float32
  Speed:     float32
  Radius:    float32
  Color:     Color
}

type GameState = {
  Stars:    Star list
  Score:    int
  Lives:    int
  PlayerX:  float32
  GameOver: bool
}

// ── Configuration — edit these live! ──
// ┌─ HOT RELOAD ZONE: change numbers, save, feel the difference ─┐
let playerWidth  = 80.0f
let playerHeight = 15.0f
let playerSpeed  = 350.0f

let starMinSpeed  = 80.0f
let starMaxSpeed  = 220.0f
let starSpawnRate = 0.03f   // probability per frame of spawning a new star
let starColors    = [| Color.Gold; Color.SkyBlue; Color.Pink; Color.Lime; Color.Orange |]

let screenWidth  = 800
let screenHeight = 600
// └──────────────────────────────────────────────────────────────┘

// ── Pure game logic (no side effects) ──
let rng = System.Random()

let inline isKeyDown key : bool = Raylib.IsKeyDown key
let inline isKeyPressed key : bool = Raylib.IsKeyPressed key

let spawnStar () : Star = {
  X      = rng.NextSingle() * float32 screenWidth
  Y      = -20.0f
  Speed  = starMinSpeed + rng.NextSingle() * (starMaxSpeed - starMinSpeed)
  Radius = 6.0f + rng.NextSingle() * 8.0f
  Color  = starColors[rng.Next(starColors.Length)]
}

let catchesStar (playerX: float32) (star: Star) =
  let catchY  = float32 screenHeight - playerHeight - 20.0f
  let half    = playerWidth / 2.0f
  star.Y + star.Radius >= catchY &&
  star.X >= playerX - half &&
  star.X <= playerX + half

let updateStars (dt: float32) (playerX: float32) (stars: Star list) =
  let moved   = stars |> List.map (fun s -> { s with Y = s.Y + s.Speed * dt })
  let caught  = moved |> List.filter (catchesStar playerX)
  let missed  = moved |> List.filter (fun s -> s.Y > float32 screenHeight + 30.0f && not (catchesStar playerX s))
  let alive   = moved |> List.filter (fun s -> s.Y <= float32 screenHeight + 30.0f && not (catchesStar playerX s))
  let spawned = if rng.NextSingle() < starSpawnRate then [spawnStar()] else []
  alive @ spawned, List.length caught, List.length missed

let clamp v lo hi = max lo (min hi v)

let updatePlayer (dt: float32) (x: float32) =
  let dx =
    (if isKeyDown KeyboardKey.Left  || isKeyDown KeyboardKey.A then -1.0f else 0.0f) +
    (if isKeyDown KeyboardKey.Right || isKeyDown KeyboardKey.D then  1.0f else 0.0f)
  clamp (x + dx * playerSpeed * dt) (playerWidth / 2.0f) (float32 screenWidth - playerWidth / 2.0f)

// ── Rendering (pure function — hotpatched on save) ──
let drawGame (state: GameState) =
  Raylib.ClearBackground(Color.Black)

  // Stars
  for star in state.Stars do
    Raylib.DrawCircleV(Vector2(star.X, star.Y), star.Radius, star.Color)
    Raylib.DrawCircleLines(int star.X, int star.Y, star.Radius + 2.0f, Color.White)

  // Player paddle
  let px = int state.PlayerX - int playerWidth / 2
  let py = screenHeight - int playerHeight - 15
  Raylib.DrawRectangleRounded(
    Rectangle(float32 px, float32 py, playerWidth, playerHeight), 0.5f, 8, Color.SkyBlue)
  Raylib.DrawRectangleRoundedLines(
    Rectangle(float32 px, float32 py, playerWidth, playerHeight), 0.5f, 8, Color.White)

  // HUD
  Raylib.DrawText($"Score: {state.Score}", 10, 10, 28, Color.Gold)
  Raylib.DrawText($"Lives: {state.Lives}", screenWidth - 140, 10, 28, Color.Pink)

  // Lives bar
  for i in 0..state.Lives - 1 do
    Raylib.DrawCircle(screenWidth - 30 - i * 28, 44, 8.0f, Color.Pink)

  if state.GameOver then
    let msg = $"GAME OVER  —  Final score: {state.Score}"
    let sz  = Raylib.MeasureText(msg, 36)
    Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, Color(0uy, 0uy, 0uy, 160uy))
    Raylib.DrawText(msg, (screenWidth - sz) / 2, screenHeight / 2 - 18, 36, Color.White)
    Raylib.DrawText("Press SPACE to play again", (screenWidth - Raylib.MeasureText("Press SPACE to play again", 22)) / 2, screenHeight / 2 + 30, 22, Color.LightGray)

  Raylib.DrawText("⬅ ➡ or A D to move · catch the stars!", 10, screenHeight - 30, 16, Color.DarkGray)
  Raylib.DrawFPS(10, screenHeight - 52)

// ── Initial state ──
let initState () : GameState = {
  Stars   = [ for _ in 1..5 -> { spawnStar() with Y = rng.NextSingle() * float32 screenHeight * 0.5f } ]
  Score   = 0
  Lives   = 3
  PlayerX = float32 screenWidth / 2.0f
  GameOver = false
}

// ── Window + game loop ──
[<EntryPoint>]
let main _argv =
  Raylib.InitWindow(screenWidth, screenHeight, "⭐ Star Catcher — SageFs + Raylib")
  Raylib.SetTargetFPS(60)

  let mutable state = initState()

  while not (Raylib.WindowShouldClose()) do
    let dt = Raylib.GetFrameTime()

    state <-
      if state.GameOver then
        if isKeyPressed KeyboardKey.Space then initState() else state
      else
        let newPlayerX            = updatePlayer dt state.PlayerX
        let newStars, caught, missed = updateStars dt newPlayerX state.Stars
        let newScore  = state.Score + caught * 10
        let newLives  = state.Lives - missed
        let gameOver  = newLives <= 0
        { state with
            Stars    = newStars
            Score    = newScore
            Lives    = max 0 newLives
            PlayerX  = newPlayerX
            GameOver = gameOver }

    Raylib.BeginDrawing()
    drawGame state
    Raylib.EndDrawing()

  Raylib.CloseWindow()
  0

// ── What to tweak (hot-reload edition) ──
// 1. Increase `starMaxSpeed` to 400.0f — save — suddenly much harder
// 2. Change star colors in `starColors` — save — instant palette swap
// 3. Increase `starSpawnRate` to 0.08f — save — chaos mode
// 4. Make the player wider (playerWidth = 150.0f) — save — easy mode
// 5. Edit the `drawGame` function — change colors, add effects — save
//
// Every one of these changes applies to the running game without restart.
// SageFs patches the `drawGame`, `updateStars`, `updatePlayer` functions live.
// This is what "live development" actually means.
