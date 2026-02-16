namespace SageFs.Gui

open Raylib_cs
open SageFs

#nowarn "3391" // implicit CBool -> bool conversion from Raylib-cs

/// Raylib window loop — immediate-mode GUI rendering of CellGrid.
module RaylibMode =
  let private defaultCellW = 9
  let private defaultCellH = 18
  let private defaultFontSize = 16

  /// Run the Raylib GUI window. Renders the same CellGrid as the TUI.
  let run () =
    let gridCols = 120
    let gridRows = 40
    let windowW = gridCols * defaultCellW
    let windowH = gridRows * defaultCellH

    Raylib.SetConfigFlags(ConfigFlags.ResizableWindow)
    Raylib.InitWindow(windowW, windowH, "SageFs GUI")
    Raylib.SetTargetFPS(144)

    // Load a monospace font
    let font = Raylib.GetFontDefault()
    let grid = CellGrid.create gridRows gridCols

    while not (Raylib.WindowShouldClose()) do
      // --- UPDATE ---
      let sw = System.Diagnostics.Stopwatch.StartNew()

      // Compute layout and draw to grid
      CellGrid.clear grid
      let dt = DrawTarget.create grid (Rect.create 0 0 gridCols gridRows)
      Draw.fill dt Theme.bgPanel

      // 4-pane layout
      let left, right = Rect.splitVProp 0.65 dt.Clip
      let outputRect, editorRect = Rect.splitH (left.Height - 6) left
      let sessRect, diagRect = Rect.splitHProp 0.5 right

      let oInner = Draw.box (DrawTarget.create grid outputRect) "Output" Theme.borderNormal Theme.bgPanel
      let eInner = Draw.box (DrawTarget.create grid editorRect) "Editor" Theme.borderFocus Theme.bgEditor
      let sInner = Draw.box (DrawTarget.create grid sessRect) "Sessions" Theme.borderNormal Theme.bgPanel
      let dInner = Draw.box (DrawTarget.create grid diagRect) "Diagnostics" Theme.borderNormal Theme.bgPanel

      // Demo content
      Draw.text oInner 0 0 Theme.fgDefault Theme.bgPanel CellAttrs.None "SageFs Raylib GUI — dual-renderer mode"
      Draw.text oInner 1 0 Theme.fgGreen Theme.bgPanel CellAttrs.None "[INFO] Both TUI and GUI render the same CellGrid"
      Draw.text oInner 2 0 Theme.fgCyan Theme.bgPanel CellAttrs.None "[INFO] 144fps target — no excuses"
      for row in 3 .. oInner.Clip.Height - 1 do
        Draw.text oInner row 0 Theme.fgDim Theme.bgPanel CellAttrs.None (sprintf "  output line %d" row)
      Draw.text eInner 0 0 Theme.fgDefault Theme.bgEditor CellAttrs.None ">> let x = 42;;"
      Draw.text eInner 1 0 Theme.fgGreen Theme.bgEditor CellAttrs.None "val x: int = 42"
      Draw.text sInner 0 0 Theme.fgCyan Theme.bgPanel CellAttrs.None "session-abc123 (Ready)"
      Draw.text dInner 0 0 Theme.fgYellow Theme.bgPanel CellAttrs.None "No diagnostics"

      sw.Stop()
      let frameMs = sw.Elapsed.TotalMilliseconds
      Draw.statusBar dt
        (sprintf " Ready | session-abc123 | %d fps" (Raylib.GetFPS()))
        (sprintf "%.2f ms " frameMs)
        Theme.fgDefault Theme.bgStatus

      // --- DRAW ---
      Raylib.BeginDrawing()
      Raylib.ClearBackground(RaylibPalette.toColor Theme.bgDefault)
      RaylibEmitter.emit grid font defaultCellW defaultCellH defaultFontSize
      Raylib.EndDrawing()

    Raylib.CloseWindow()
