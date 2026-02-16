namespace SageFs

/// Named color palette — abstract color IDs (256-color indices).
/// TUI maps directly to ANSI 256-color. Raylib maps to RGB via a palette table.
module Theme =
  // Foreground
  let fgDefault   = 255uy
  let fgDim       = 245uy
  let fgGreen     = 114uy
  let fgRed       = 203uy
  let fgYellow    = 179uy
  let fgCyan      = 116uy
  let fgBlue      = 75uy
  let fgMagenta   = 176uy

  // Background
  let bgDefault   = 0uy
  let bgPanel     = 235uy
  let bgEditor    = 234uy
  let bgSelection = 238uy
  let bgStatus    = 236uy
  let bgFocus     = 237uy

  // Border colors
  let borderNormal = 240uy
  let borderFocus  = 75uy

  // Semantic
  let colorPass    = fgGreen
  let colorFail    = fgRed
  let colorWarn    = fgYellow
  let colorInfo    = fgCyan
