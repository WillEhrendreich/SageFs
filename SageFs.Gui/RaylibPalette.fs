namespace SageFs.Gui

open Raylib_cs

/// Maps 256-color palette indices (used by Theme) to Raylib RGBA colors.
/// Standard xterm-256 color palette.
module RaylibPalette =
  /// The 256-color xterm palette as Raylib Color values
  let private palette : Color[] =
    let p = Array.create 256 Color.Black
    // Standard 16 colors (0-15)
    p.[0]  <- Color(0uy, 0uy, 0uy)         // Black
    p.[1]  <- Color(128uy, 0uy, 0uy)       // Maroon
    p.[2]  <- Color(0uy, 128uy, 0uy)       // Green
    p.[3]  <- Color(128uy, 128uy, 0uy)     // Olive
    p.[4]  <- Color(0uy, 0uy, 128uy)       // Navy
    p.[5]  <- Color(128uy, 0uy, 128uy)     // Purple
    p.[6]  <- Color(0uy, 128uy, 128uy)     // Teal
    p.[7]  <- Color(192uy, 192uy, 192uy)   // Silver
    p.[8]  <- Color(128uy, 128uy, 128uy)   // Gray
    p.[9]  <- Color(255uy, 0uy, 0uy)       // Red
    p.[10] <- Color(0uy, 255uy, 0uy)       // Lime
    p.[11] <- Color(255uy, 255uy, 0uy)     // Yellow
    p.[12] <- Color(0uy, 0uy, 255uy)       // Blue
    p.[13] <- Color(255uy, 0uy, 255uy)     // Fuchsia
    p.[14] <- Color(0uy, 255uy, 255uy)     // Aqua
    p.[15] <- Color(255uy, 255uy, 255uy)   // White

    // 216 color cube (16-231): 6x6x6 RGB
    for r in 0 .. 5 do
      for g in 0 .. 5 do
        for b in 0 .. 5 do
          let idx = 16 + 36 * r + 6 * g + b
          let rv = if r = 0 then 0uy else byte (55 + 40 * r)
          let gv = if g = 0 then 0uy else byte (55 + 40 * g)
          let bv = if b = 0 then 0uy else byte (55 + 40 * b)
          p.[idx] <- Color(rv, gv, bv)

    // 24 grayscale ramp (232-255)
    for i in 0 .. 23 do
      let v = byte (8 + 10 * i)
      p.[232 + i] <- Color(v, v, v)

    p

  /// Look up a 256-color index to get a Raylib Color
  let toColor (index: byte) : Color = palette.[int index]
