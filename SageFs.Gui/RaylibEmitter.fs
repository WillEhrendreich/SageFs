namespace SageFs.Gui

open Raylib_cs
open SageFs

/// Raylib emitter — converts Cell[,] to Raylib draw calls.
/// Each cell is drawn as a colored rectangle + character.
/// Box-drawing characters are rendered as native Raylib lines/rectangles.
module RaylibEmitter =
  let private lineThickness = 1.0f

  /// Draw box-drawing character as native Raylib lines.
  /// Returns true if the char was handled, false if it should be drawn as text.
  let inline private drawBoxChar (ch: char) (x: int) (y: int) (cw: int) (ch2: int) (fg: Color) =
    let xf = float32 x
    let yf = float32 y
    let cwf = float32 cw
    let chf = float32 ch2
    let mx = xf + cwf / 2.0f
    let my = yf + chf / 2.0f
    match ch with
    // Horizontal line ─
    | '\u2500' ->
      Raylib.DrawLineEx(System.Numerics.Vector2(xf, my), System.Numerics.Vector2(xf + cwf, my), lineThickness, fg)
      true
    // Vertical line │
    | '\u2502' ->
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, yf), System.Numerics.Vector2(mx, yf + chf), lineThickness, fg)
      true
    // Top-left corner ┌
    | '\u250C' ->
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, my), System.Numerics.Vector2(xf + cwf, my), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, my), System.Numerics.Vector2(mx, yf + chf), lineThickness, fg)
      true
    // Top-right corner ┐
    | '\u2510' ->
      Raylib.DrawLineEx(System.Numerics.Vector2(xf, my), System.Numerics.Vector2(mx, my), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, my), System.Numerics.Vector2(mx, yf + chf), lineThickness, fg)
      true
    // Bottom-left corner └
    | '\u2514' ->
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, yf), System.Numerics.Vector2(mx, my), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, my), System.Numerics.Vector2(xf + cwf, my), lineThickness, fg)
      true
    // Bottom-right corner ┘
    | '\u2518' ->
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, yf), System.Numerics.Vector2(mx, my), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(xf, my), System.Numerics.Vector2(mx, my), lineThickness, fg)
      true
    // T-junctions ├ ┤ ┬ ┴ ┼
    | '\u251C' -> // ├
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, yf), System.Numerics.Vector2(mx, yf + chf), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, my), System.Numerics.Vector2(xf + cwf, my), lineThickness, fg)
      true
    | '\u2524' -> // ┤
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, yf), System.Numerics.Vector2(mx, yf + chf), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(xf, my), System.Numerics.Vector2(mx, my), lineThickness, fg)
      true
    | '\u252C' -> // ┬
      Raylib.DrawLineEx(System.Numerics.Vector2(xf, my), System.Numerics.Vector2(xf + cwf, my), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, my), System.Numerics.Vector2(mx, yf + chf), lineThickness, fg)
      true
    | '\u2534' -> // ┴
      Raylib.DrawLineEx(System.Numerics.Vector2(xf, my), System.Numerics.Vector2(xf + cwf, my), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, yf), System.Numerics.Vector2(mx, my), lineThickness, fg)
      true
    | '\u253C' -> // ┼
      Raylib.DrawLineEx(System.Numerics.Vector2(xf, my), System.Numerics.Vector2(xf + cwf, my), lineThickness, fg)
      Raylib.DrawLineEx(System.Numerics.Vector2(mx, yf), System.Numerics.Vector2(mx, yf + chf), lineThickness, fg)
      true
    | _ -> false

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
          if not (drawBoxChar cell.Char x y cellW cellH fgColor) then
            let text = string cell.Char
            Raylib.DrawTextEx(font, text, System.Numerics.Vector2(float32 x, float32 y), float32 fontSize, 0.0f, fgColor)
