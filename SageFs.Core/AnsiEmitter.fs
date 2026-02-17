namespace SageFs

open System.Text

/// ANSI terminal emitter — converts Cell[,] to ANSI escape string.
/// Uses truecolor (24-bit) escape codes: ESC[38;2;r;g;bm for fg, ESC[48;2;r;g;bm for bg.
module AnsiEmitter =
  let private esc = "\x1b["

  let emit (grid: Cell[,]) (cursorRow: int) (cursorCol: int) : string =
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
      for col in 0 .. cols - 1 do
        let cell = grid.[row, col]

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

  let emitGridOnly (grid: Cell[,]) : string =
    let rows = CellGrid.rows grid
    let cols = CellGrid.cols grid
    let sb = StringBuilder(rows * cols * 10)
    let mutable lastFg = 0x00FFFFFFu
    let mutable lastBg = 0u
    let mutable lastAttrs = CellAttrs.None

    for row in 0 .. rows - 1 do
      sb.Append(esc).Append(row + 1).Append(";1H") |> ignore
      for col in 0 .. cols - 1 do
        let cell = grid.[row, col]
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
