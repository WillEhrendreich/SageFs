namespace SageFs

open System
open System.Runtime.InteropServices

/// ANSI terminal emitter — converts CellGrid to ANSI escape string.
/// Data-oriented design:
///   Arena buffer: thread-local pre-allocated char[], reused across frames.
///   Lookup table: pre-computed byte→string for zero-allocation color formatting.
///   Run-length batching: scans ahead through same-color cells, writes chars without escape overhead.
///   SIMD diff: MemoryMarshal.AsBytes + SequenceEqual for vectorized bulk cell comparison.
module AnsiEmitter =

  // ═══ Arena buffer — thread-local, pre-allocated, zero per-frame allocation ═══
  // Each thread gets its own 1MB char buffer. Grows if needed (rare).
  // The only allocation per emit call is the final `new string(buf, 0, pos)`.
  let private arena =
    new System.Threading.ThreadLocal<char[]>(fun () -> Array.zeroCreate (1024 * 1024))

  let private getArena (minSize: int) : char[] =
    let buf = arena.Value
    match buf.Length >= minSize with
    | true -> buf
    | false ->
      let newBuf = Array.zeroCreate (max (buf.Length * 2) minSize)
      arena.Value <- newBuf
      newBuf

  // ═══ Lookup tables — zero-allocation value formatting ═══
  let private byteStrs = Array.init 256 string
  let private escFg = "\x1b[38;2;"
  let private escBg = "\x1b[48;2;"

  // Cell struct size for SIMD byte-level comparison
  let private cellSize =
    System.Runtime.CompilerServices.Unsafe.SizeOf<Cell>()

  // ═══ Write primitives (inline → zero call overhead) ═══

  let inline private ws (buf: char[]) (p: byref<int>) (s: string) =
    s.CopyTo(0, buf, p, s.Length)
    p <- p + s.Length

  let inline private wc (buf: char[]) (p: byref<int>) (c: char) =
    buf.[p] <- c
    p <- p + 1

  // Write byte (0-255) via pre-computed lookup — no int formatting
  let inline private wb (buf: char[]) (p: byref<int>) (v: int) =
    let s = byteStrs.[v &&& 0xFF]
    s.CopyTo(0, buf, p, s.Length)
    p <- p + s.Length

  // Write small int (1-999) as decimal digits directly — no allocation
  let inline private wi (buf: char[]) (p: byref<int>) (n: int) =
    match n < 10 with
    | true ->
      buf.[p] <- char (n + 48)
      p <- p + 1
    | false ->
      match n < 100 with
      | true ->
        buf.[p] <- char (n / 10 + 48)
        buf.[p + 1] <- char (n % 10 + 48)
        p <- p + 2
      | false ->
        buf.[p] <- char (n / 100 + 48)
        buf.[p + 1] <- char ((n / 10) % 10 + 48)
        buf.[p + 2] <- char (n % 10 + 48)
        p <- p + 3

  // ═══ Escape sequence writers ═══

  let inline private writeAttrs
    (buf: char[]) (p: byref<int>) (attrs: CellAttrs)
    (lastFg: byref<uint32>) (lastBg: byref<uint32>) (lastAttrs: byref<CellAttrs>) =
    ws buf &p "\x1b[0m"
    lastFg <- 0x00FFFFFFu; lastBg <- 0u; lastAttrs <- CellAttrs.None
    match attrs &&& CellAttrs.Bold = CellAttrs.Bold with
    | true -> ws buf &p "\x1b[1m"
    | false -> ()
    match attrs &&& CellAttrs.Dim = CellAttrs.Dim with
    | true -> ws buf &p "\x1b[2m"
    | false -> ()
    match attrs &&& CellAttrs.Inverse = CellAttrs.Inverse with
    | true -> ws buf &p "\x1b[7m"
    | false -> ()
    lastAttrs <- attrs

  let inline private writeFg (buf: char[]) (p: byref<int>) (rgb: uint32) =
    ws buf &p escFg
    wb buf &p (int (rgb >>> 16))
    wc buf &p ';'
    wb buf &p (int (rgb >>> 8))
    wc buf &p ';'
    wb buf &p (int rgb)
    wc buf &p 'm'

  let inline private writeBg (buf: char[]) (p: byref<int>) (rgb: uint32) =
    ws buf &p escBg
    wb buf &p (int (rgb >>> 16))
    wc buf &p ';'
    wb buf &p (int (rgb >>> 8))
    wc buf &p ';'
    wb buf &p (int rgb)
    wc buf &p 'm'

  let inline private writeCursor (buf: char[]) (p: byref<int>) (row1: int) (col1: int) =
    ws buf &p "\x1b["
    wi buf &p row1
    wc buf &p ';'
    wi buf &p col1
    wc buf &p 'H'

  // ═══ Emit: full frame with run-length color batching ═══
  // After emitting a color change, scans ahead through consecutive cells that
  // share the same Fg+Bg+Attrs, writing their chars without any escape overhead.
  // For typical screens (~80% uniform color regions), this skips most escape writes.
  let emit (grid: CellGrid) (cursorRow: int) (cursorCol: int) : string =
    let rows = CellGrid.rows grid
    let cols = CellGrid.cols grid
    let buf = getArena (rows * cols * 40 + 128)
    let mutable p = 0

    ws buf &p "\x1b[?25l"
    ws buf &p "\x1b[H"

    let mutable lastFg = 0x00FFFFFFu
    let mutable lastBg = 0u
    let mutable lastAttrs = CellAttrs.None

    for row in 0 .. rows - 1 do
      ws buf &p "\x1b["
      wi buf &p (row + 1)
      ws buf &p ";1H"
      let rowBase = row * cols
      let mutable col = 0
      while col < cols do
        let cell = grid.Cells.[rowBase + col]

        match cell.Attrs <> lastAttrs with
        | true -> writeAttrs buf &p cell.Attrs &lastFg &lastBg &lastAttrs
        | false -> ()
        match cell.Fg <> lastFg with
        | true -> writeFg buf &p cell.Fg; lastFg <- cell.Fg
        | false -> ()
        match cell.Bg <> lastBg with
        | true -> writeBg buf &p cell.Bg; lastBg <- cell.Bg
        | false -> ()

        // Write char then batch-scan same-color run
        buf.[p] <- cell.Char
        p <- p + 1
        col <- col + 1
        let mutable scanning = col < cols
        while scanning do
          let nc = grid.Cells.[rowBase + col]
          match nc.Fg = lastFg && nc.Bg = lastBg && nc.Attrs = lastAttrs with
          | true ->
            buf.[p] <- nc.Char
            p <- p + 1
            col <- col + 1
            scanning <- col < cols
          | false -> scanning <- false

    ws buf &p "\x1b[0m"
    writeCursor buf &p (cursorRow + 1) (cursorCol + 1)
    ws buf &p "\x1b[?25h"

    new string(buf, 0, p)

  let emitGridOnly (grid: CellGrid) : string =
    let rows = CellGrid.rows grid
    let cols = CellGrid.cols grid
    let buf = getArena (rows * cols * 40 + 64)
    let mutable p = 0
    let mutable lastFg = 0x00FFFFFFu
    let mutable lastBg = 0u
    let mutable lastAttrs = CellAttrs.None

    for row in 0 .. rows - 1 do
      ws buf &p "\x1b["
      wi buf &p (row + 1)
      ws buf &p ";1H"
      let rowBase = row * cols
      let mutable col = 0
      while col < cols do
        let cell = grid.Cells.[rowBase + col]
        match cell.Attrs <> lastAttrs with
        | true -> writeAttrs buf &p cell.Attrs &lastFg &lastBg &lastAttrs
        | false -> ()
        match cell.Fg <> lastFg with
        | true -> writeFg buf &p cell.Fg; lastFg <- cell.Fg
        | false -> ()
        match cell.Bg <> lastBg with
        | true -> writeBg buf &p cell.Bg; lastBg <- cell.Bg
        | false -> ()
        buf.[p] <- cell.Char
        p <- p + 1
        col <- col + 1
        let mutable scanning = col < cols
        while scanning do
          let nc = grid.Cells.[rowBase + col]
          match nc.Fg = lastFg && nc.Bg = lastBg && nc.Attrs = lastAttrs with
          | true ->
            buf.[p] <- nc.Char
            p <- p + 1
            col <- col + 1
            scanning <- col < cols
          | false -> scanning <- false

    ws buf &p "\x1b[0m"
    new string(buf, 0, p)

  // ═══ SIMD-accelerated diff scan ═══
  // Reinterprets Cell[] as byte spans, compares in 16-cell chunks using
  // SequenceEqual (which uses SSE2/AVX2 vectorized comparison under the hood).
  // Returns (anyChanged, exceededThreshold). Upper-bound counting: if any cell
  // in a chunk differs, all cells in that chunk count toward the threshold.
  // This is conservative (may over-bail) but eliminates per-cell overhead for
  // the ~95% of cells that are typically unchanged between frames.
  let private diffScan
    (curr: Cell[]) (prev: Cell[]) (total: int) (threshold: int)
    : struct(bool * bool) =
    let chunkCells = 16
    let chunkBytes = cellSize * chunkCells
    let currBytes = MemoryMarshal.AsBytes(System.Span<Cell>(curr, 0, total))
    let prevBytes = MemoryMarshal.AsBytes(System.Span<Cell>(prev, 0, total))
    let totalBytes = currBytes.Length
    let mutable byteOff = 0
    let mutable upperBound = 0
    let mutable bailed = false
    let mutable anyChanged = false

    // Vectorized chunk pass
    while byteOff + chunkBytes <= totalBytes && not bailed do
      match currBytes.Slice(byteOff, chunkBytes).SequenceEqual(
              prevBytes.Slice(byteOff, chunkBytes)) with
      | true -> ()
      | false ->
        anyChanged <- true
        upperBound <- upperBound + chunkCells
        match upperBound > threshold with
        | true -> bailed <- true
        | false -> ()
      byteOff <- byteOff + chunkBytes

    // Scalar remainder (< 16 cells at tail)
    let mutable cellIdx = byteOff / cellSize
    while cellIdx < total && not bailed do
      match curr.[cellIdx] <> prev.[cellIdx] with
      | true ->
        anyChanged <- true
        upperBound <- upperBound + 1
        match upperBound > threshold with
        | true -> bailed <- true
        | false -> ()
      | false -> ()
      cellIdx <- cellIdx + 1

    struct(anyChanged, bailed)

  /// Diff-emit: only emit cells that differ between prev and current grid.
  /// Falls back to full emit when grids differ in size or >30% cells changed.
  /// SIMD-accelerated counting pass bails early when threshold is exceeded.
  let emitDiff (prev: CellGrid) (curr: CellGrid) (cursorRow: int) (cursorCol: int) : string =
    match prev.Rows <> curr.Rows || prev.Cols <> curr.Cols with
    | true -> emit curr cursorRow cursorCol
    | false ->
      let total = curr.Cells.Length
      let threshold = total * 30 / 100

      let struct(anyChanged, bailed) =
        diffScan curr.Cells prev.Cells total threshold

      match bailed with
      | true -> emit curr cursorRow cursorCol
      | false ->
        match anyChanged with
        | false ->
          // Zero changes — cursor-only output
          let buf = getArena 64
          let mutable p = 0
          ws buf &p "\x1b[?25l"
          writeCursor buf &p (cursorRow + 1) (cursorCol + 1)
          ws buf &p "\x1b[?25h"
          new string(buf, 0, p)
        | true ->
          // Partial diff — emit only changed cells
          let cols = curr.Cols
          let buf = getArena (total * 10 + 64)
          let mutable p = 0
          ws buf &p "\x1b[?25l"
          let mutable lastFg = 0x00FFFFFFu
          let mutable lastBg = 0u
          let mutable lastAttrs = CellAttrs.None
          let mutable lastRow = -1
          let mutable lastCol = -1
          for i in 0 .. total - 1 do
            let cell = curr.Cells.[i]
            match cell <> prev.Cells.[i] with
            | true ->
              let row = i / cols
              let col = i % cols
              match row <> lastRow || col <> lastCol + 1 with
              | true -> writeCursor buf &p (row + 1) (col + 1)
              | false -> ()
              match cell.Attrs <> lastAttrs with
              | true -> writeAttrs buf &p cell.Attrs &lastFg &lastBg &lastAttrs
              | false -> ()
              match cell.Fg <> lastFg with
              | true -> writeFg buf &p cell.Fg; lastFg <- cell.Fg
              | false -> ()
              match cell.Bg <> lastBg with
              | true -> writeBg buf &p cell.Bg; lastBg <- cell.Bg
              | false -> ()
              buf.[p] <- cell.Char
              p <- p + 1
              lastRow <- row
              lastCol <- col
            | false -> ()
          ws buf &p "\x1b[0m"
          writeCursor buf &p (cursorRow + 1) (cursorCol + 1)
          ws buf &p "\x1b[?25h"
          new string(buf, 0, p)
