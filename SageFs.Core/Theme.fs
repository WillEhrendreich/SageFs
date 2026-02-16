namespace SageFs

/// Theme configuration record — all named color values (256-color indices).
type ThemeConfig = {
  FgDefault: byte; FgDim: byte; FgGreen: byte; FgRed: byte
  FgYellow: byte; FgCyan: byte; FgBlue: byte; FgMagenta: byte
  BgDefault: byte; BgPanel: byte; BgEditor: byte
  BgSelection: byte; BgStatus: byte; BgFocus: byte
  BorderNormal: byte; BorderFocus: byte
  ColorPass: byte; ColorFail: byte; ColorWarn: byte; ColorInfo: byte
}

/// Named color palette — abstract color IDs (256-color indices).
/// TUI maps directly to ANSI 256-color. Raylib maps to RGB via a palette table.
module Theme =
  let defaults : ThemeConfig = {
    FgDefault = 255uy; FgDim = 245uy; FgGreen = 114uy; FgRed = 203uy
    FgYellow = 179uy; FgCyan = 116uy; FgBlue = 75uy; FgMagenta = 176uy
    BgDefault = 0uy; BgPanel = 235uy; BgEditor = 234uy
    BgSelection = 238uy; BgStatus = 236uy; BgFocus = 237uy
    BorderNormal = 240uy; BorderFocus = 75uy
    ColorPass = 114uy; ColorFail = 203uy; ColorWarn = 179uy; ColorInfo = 116uy
  }

  /// Apply partial overrides from a map of name -> byte value onto a base config
  let withOverrides (overrides: Map<string, byte>) (base': ThemeConfig) : ThemeConfig =
    let g key def = overrides |> Map.tryFind key |> Option.defaultValue def
    { FgDefault = g "fgDefault" base'.FgDefault
      FgDim = g "fgDim" base'.FgDim
      FgGreen = g "fgGreen" base'.FgGreen
      FgRed = g "fgRed" base'.FgRed
      FgYellow = g "fgYellow" base'.FgYellow
      FgCyan = g "fgCyan" base'.FgCyan
      FgBlue = g "fgBlue" base'.FgBlue
      FgMagenta = g "fgMagenta" base'.FgMagenta
      BgDefault = g "bgDefault" base'.BgDefault
      BgPanel = g "bgPanel" base'.BgPanel
      BgEditor = g "bgEditor" base'.BgEditor
      BgSelection = g "bgSelection" base'.BgSelection
      BgStatus = g "bgStatus" base'.BgStatus
      BgFocus = g "bgFocus" base'.BgFocus
      BorderNormal = g "borderNormal" base'.BorderNormal
      BorderFocus = g "borderFocus" base'.BorderFocus
      ColorPass = g "colorPass" base'.ColorPass
      ColorFail = g "colorFail" base'.ColorFail
      ColorWarn = g "colorWarn" base'.ColorWarn
      ColorInfo = g "colorInfo" base'.ColorInfo }

  // Module-level convenience aliases (backward-compatible)
  let fgDefault   = defaults.FgDefault
  let fgDim       = defaults.FgDim
  let fgGreen     = defaults.FgGreen
  let fgRed       = defaults.FgRed
  let fgYellow    = defaults.FgYellow
  let fgCyan      = defaults.FgCyan
  let fgBlue      = defaults.FgBlue
  let fgMagenta   = defaults.FgMagenta
  let bgDefault   = defaults.BgDefault
  let bgPanel     = defaults.BgPanel
  let bgEditor    = defaults.BgEditor
  let bgSelection = defaults.BgSelection
  let bgStatus    = defaults.BgStatus
  let bgFocus     = defaults.BgFocus
  let borderNormal = defaults.BorderNormal
  let borderFocus  = defaults.BorderFocus
  let colorPass    = defaults.ColorPass
  let colorFail    = defaults.ColorFail
  let colorWarn    = defaults.ColorWarn
  let colorInfo    = defaults.ColorInfo

  /// Parse theme lines from config.fsx format:
  ///   let theme = [ "fgDefault", 255; "bgPanel", 235 ]
  let parseConfigLines (lines: string array) : Map<string, byte> =
    let mutable overrides = Map.empty
    let mutable inTheme = false
    for line in lines do
      let trimmed = line.Trim()
      if trimmed.StartsWith("let theme") || trimmed.StartsWith("let Theme") then
        inTheme <- true
      if inTheme then
        let mutable i = 0
        while i < trimmed.Length do
          let q1 = trimmed.IndexOf('"', i)
          if q1 >= 0 then
            let q2 = trimmed.IndexOf('"', q1 + 1)
            if q2 > q1 then
              let name = trimmed.Substring(q1 + 1, q2 - q1 - 1)
              let comma = trimmed.IndexOf(',', q2 + 1)
              if comma >= 0 then
                let rest = trimmed.Substring(comma + 1).TrimStart()
                let numStr =
                  rest |> Seq.takeWhile System.Char.IsDigit |> System.String.Concat
                match System.Byte.TryParse(numStr) with
                | true, v -> overrides <- Map.add name v overrides
                | _ -> ()
                i <- comma + 1
              else i <- trimmed.Length
            else i <- trimmed.Length
          else i <- trimmed.Length
        if trimmed.Contains(']') && inTheme && not (trimmed.StartsWith("let")) then
          inTheme <- false
    overrides
