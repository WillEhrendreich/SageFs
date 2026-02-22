namespace SageFs

open System.Text

/// ANSI terminal emitter — converts CellGrid to ANSI escape string.
/// Uses truecolor (24-bit) escape codes: ESC[38;2;r;g;bm for fg, ESC[48;2;r;g;bm for bg.
module AnsiEmitter =
  let esc = "\x1b["

  let emit (grid: CellGrid) (cursorRow: int) (cursorCol: int) : string =
    let rows = CellGrid.rows grid
    let cols = CellGrid.cols grid
    let sb = StringBuilder(rows * cols * 10)

    sb.Append(esc).Append("?25l") |> ignore
    sb.Append(esc).Append("H") |> ignore

    let mutable lastFg = 0x00FFFFFFu
    let mutable lastBg = 0u
    let mutable lastAttrs = CellAttrs.None

    for row in 0 .. rows - 1 do
      sb.Append(esc).Append(row + 1).Append(";1H") |> ignore
      let rowBase = row * cols
      for col in 0 .. cols - 1 do
        let cell = grid.Cells.[rowBase + col]

        if cell.Attrs <> lastAttrs then
          sb.Append(esc).Append("0m") |> ignore
          lastFg <- 0x00FFFFFFu; lastBg <- 0u; lastAttrs <- CellAttrs.None
          if cell.Attrs &&& CellAttrs.Bold = CellAttrs.Bold then
            sb.Append(esc).Append("1m") |> ignore
          if cell.Attrs &&& CellAttrs.Dim = CellAttrs.Dim then
            sb.Append(esc).Append("2m") |> ignore
          if cell.Attrs &&& CellAttrs.Inverse = CellAttrs.Inverse then
            sb.Append(esc).Append("7m") |> ignore
          lastAttrs <- cell.Attrs

        if cell.Fg <> lastFg then
          sb.Append(esc).Append("38;2;")
            .Append(int (Theme.rgbR cell.Fg)).Append(';')
            .Append(int (Theme.rgbG cell.Fg)).Append(';')
            .Append(int (Theme.rgbB cell.Fg)).Append('m') |> ignore
          lastFg <- cell.Fg

        if cell.Bg <> lastBg then
          sb.Append(esc).Append("48;2;")
            .Append(int (Theme.rgbR cell.Bg)).Append(';')
            .Append(int (Theme.rgbG cell.Bg)).Append(';')
            .Append(int (Theme.rgbB cell.Bg)).Append('m') |> ignore
          lastBg <- cell.Bg

        sb.Append(cell.Char) |> ignore

    sb.Append(esc).Append("0m") |> ignore
    sb.Append(esc).Append(cursorRow + 1).Append(';').Append(cursorCol + 1).Append('H') |> ignore
    sb.Append(esc).Append("?25h") |> ignore

    sb.ToString()

  let emitGridOnly (grid: CellGrid) : string =
    let rows = CellGrid.rows grid
    let cols = CellGrid.cols grid
    let sb = StringBuilder(rows * cols * 10)
    let mutable lastFg = 0x00FFFFFFu
    let mutable lastBg = 0u
    let mutable lastAttrs = CellAttrs.None

    for row in 0 .. rows - 1 do
      sb.Append(esc).Append(row + 1).Append(";1H") |> ignore
      let rowBase = row * cols
      for col in 0 .. cols - 1 do
        let cell = grid.Cells.[rowBase + col]
        if cell.Attrs <> lastAttrs then
          sb.Append(esc).Append("0m") |> ignore
          lastFg <- 0x00FFFFFFu; lastBg <- 0u; lastAttrs <- CellAttrs.None
          if cell.Attrs &&& CellAttrs.Bold = CellAttrs.Bold then
            sb.Append(esc).Append("1m") |> ignore
          if cell.Attrs &&& CellAttrs.Dim = CellAttrs.Dim then
            sb.Append(esc).Append("2m") |> ignore
          if cell.Attrs &&& CellAttrs.Inverse = CellAttrs.Inverse then
            sb.Append(esc).Append("7m") |> ignore
          lastAttrs <- cell.Attrs
        if cell.Fg <> lastFg then
          sb.Append(esc).Append("38;2;")
            .Append(int (Theme.rgbR cell.Fg)).Append(';')
            .Append(int (Theme.rgbG cell.Fg)).Append(';')
            .Append(int (Theme.rgbB cell.Fg)).Append('m') |> ignore
          lastFg <- cell.Fg
        if cell.Bg <> lastBg then
          sb.Append(esc).Append("48;2;")
            .Append(int (Theme.rgbR cell.Bg)).Append(';')
            .Append(int (Theme.rgbG cell.Bg)).Append(';')
            .Append(int (Theme.rgbB cell.Bg)).Append('m') |> ignore
          lastBg <- cell.Bg
        sb.Append(cell.Char) |> ignore

    sb.Append(esc).Append("0m") |> ignore
    sb.ToString()

  /// Diff-emit: only emit cells that differ between prev and current grid.
  /// Falls back to full emit when grids differ in size or >30% cells changed.
  /// Count pass bails early when threshold is exceeded.
  let emitDiff (prev: CellGrid) (curr: CellGrid) (cursorRow: int) (cursorCol: int) : string =
    if prev.Rows <> curr.Rows || prev.Cols <> curr.Cols then
      emit curr cursorRow cursorCol
    else
      let total = curr.Cells.Length
      let threshold = total * 30 / 100

      // Count pass with early bail
      let mutable changedCount = 0
      let mutable i = 0
      let mutable bailed = false
      while i < total && not bailed do
        if curr.Cells.[i] <> prev.Cells.[i] then
          changedCount <- changedCount + 1
          if changedCount > threshold then
            bailed <- true
        i <- i + 1

      if bailed then
        emit curr cursorRow cursorCol
      elif changedCount = 0 then
        let sb = StringBuilder(48)
        sb.Append(esc).Append("?25l") |> ignore
        sb.Append(esc).Append(cursorRow + 1).Append(';').Append(cursorCol + 1).Append('H') |> ignore
        sb.Append(esc).Append("?25h") |> ignore
        sb.ToString()
      else
        let cols = curr.Cols
        let sb = StringBuilder(changedCount * 30)
        sb.Append(esc).Append("?25l") |> ignore
        let mutable lastFg = 0x00FFFFFFu
        let mutable lastBg = 0u
        let mutable lastAttrs = CellAttrs.None
        let mutable lastRow = -1
        let mutable lastCol = -1
        for i in 0 .. total - 1 do
          let cell = curr.Cells.[i]
          if cell <> prev.Cells.[i] then
            let row = i / cols
            let col = i % cols
            if row <> lastRow || col <> lastCol + 1 then
              sb.Append(esc).Append(row + 1).Append(';').Append(col + 1).Append('H') |> ignore
            if cell.Attrs <> lastAttrs then
              sb.Append(esc).Append("0m") |> ignore
              lastFg <- 0x00FFFFFFu; lastBg <- 0u; lastAttrs <- CellAttrs.None
              if cell.Attrs &&& CellAttrs.Bold = CellAttrs.Bold then
                sb.Append(esc).Append("1m") |> ignore
              if cell.Attrs &&& CellAttrs.Dim = CellAttrs.Dim then
                sb.Append(esc).Append("2m") |> ignore
              if cell.Attrs &&& CellAttrs.Inverse = CellAttrs.Inverse then
                sb.Append(esc).Append("7m") |> ignore
              lastAttrs <- cell.Attrs
            if cell.Fg <> lastFg then
              sb.Append(esc).Append("38;2;")
                .Append(int (Theme.rgbR cell.Fg)).Append(';')
                .Append(int (Theme.rgbG cell.Fg)).Append(';')
                .Append(int (Theme.rgbB cell.Fg)).Append('m') |> ignore
              lastFg <- cell.Fg
            if cell.Bg <> lastBg then
              sb.Append(esc).Append("48;2;")
                .Append(int (Theme.rgbR cell.Bg)).Append(';')
                .Append(int (Theme.rgbG cell.Bg)).Append(';')
                .Append(int (Theme.rgbB cell.Bg)).Append('m') |> ignore
              lastBg <- cell.Bg
            sb.Append(cell.Char) |> ignore
            lastRow <- row
            lastCol <- col
        sb.Append(esc).Append("0m") |> ignore
        sb.Append(esc).Append(cursorRow + 1).Append(';').Append(cursorCol + 1).Append('H') |> ignore
        sb.Append(esc).Append("?25h") |> ignore
        sb.ToString()
