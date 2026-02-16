namespace SageFs.Gui

open Raylib_cs
open SageFs

/// Raylib emitter — converts Cell[,] to Raylib draw calls.
/// Each cell is drawn as a colored rectangle + character.
module RaylibEmitter =
  /// Emit the grid as Raylib draw calls. Must be called between BeginDrawing/EndDrawing.
  let emit (grid: Cell[,]) (font: Font) (cellW: int) (cellH: int) (fontSize: int) =
    let rows = CellGrid.rows grid
    let cols = CellGrid.cols grid

    for row in 0 .. rows - 1 do
      for col in 0 .. cols - 1 do
        let cell = grid.[row, col]
        let x = col * cellW
        let y = row * cellH

        // Background rectangle
        let bgColor = RaylibPalette.toColor cell.Bg
        Raylib.DrawRectangle(x, y, cellW, cellH, bgColor)

        // Character (skip spaces for performance)
        if cell.Char <> ' ' then
          let fgColor = RaylibPalette.toColor cell.Fg
          let text = string cell.Char
          Raylib.DrawTextEx(font, text, System.Numerics.Vector2(float32 x, float32 y), float32 fontSize, 0.0f, fgColor)
