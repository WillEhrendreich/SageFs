namespace SageFs

/// Theme configuration record — all named color values (256-color indices).
type ThemeConfig = {
  FgDefault: byte; FgDim: byte; FgGreen: byte; FgRed: byte
  FgYellow: byte; FgCyan: byte; FgBlue: byte; FgMagenta: byte
  BgDefault: byte; BgPanel: byte; BgEditor: byte
  BgSelection: byte; BgStatus: byte; BgFocus: byte
  BorderNormal: byte; BorderFocus: byte
  ColorPass: byte; ColorFail: byte; ColorWarn: byte; ColorInfo: byte
  // Syntax highlighting token colors
  SynKeyword: byte; SynString: byte; SynComment: byte; SynNumber: byte
  SynOperator: byte; SynType: byte; SynFunction: byte; SynVariable: byte
  SynPunctuation: byte; SynConstant: byte; SynModule: byte
  SynAttribute: byte; SynDirective: byte; SynProperty: byte
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
    // Syntax tokens — One Dark inspired
    SynKeyword = 176uy    // magenta — let, match, type, if
    SynString = 114uy     // green — "hello"
    SynComment = 245uy    // dim gray — // comment
    SynNumber = 179uy     // yellow — 42, 3.14
    SynOperator = 116uy   // cyan — |>, +, =
    SynType = 179uy       // yellow — string, int, MyType
    SynFunction = 75uy    // blue — function names
    SynVariable = 255uy   // white — identifiers
    SynPunctuation = 245uy // dim — ( ) { } [ ]
    SynConstant = 179uy   // yellow — DU cases, Literal values
    SynModule = 116uy     // cyan — module names (List, Array, Seq)
    SynAttribute = 176uy  // magenta — [<Test>]
    SynDirective = 176uy  // magenta — #r, #load, #if
    SynProperty = 116uy   // cyan — record fields
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
      ColorInfo = g "colorInfo" base'.ColorInfo
      SynKeyword = g "synKeyword" base'.SynKeyword
      SynString = g "synString" base'.SynString
      SynComment = g "synComment" base'.SynComment
      SynNumber = g "synNumber" base'.SynNumber
      SynOperator = g "synOperator" base'.SynOperator
      SynType = g "synType" base'.SynType
      SynFunction = g "synFunction" base'.SynFunction
      SynVariable = g "synVariable" base'.SynVariable
      SynPunctuation = g "synPunctuation" base'.SynPunctuation
      SynConstant = g "synConstant" base'.SynConstant
      SynModule = g "synModule" base'.SynModule
      SynAttribute = g "synAttribute" base'.SynAttribute
      SynDirective = g "synDirective" base'.SynDirective
      SynProperty = g "synProperty" base'.SynProperty }

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

  // Syntax token aliases
  let synKeyword     = defaults.SynKeyword
  let synString      = defaults.SynString
  let synComment     = defaults.SynComment
  let synNumber      = defaults.SynNumber
  let synOperator    = defaults.SynOperator
  let synType        = defaults.SynType
  let synFunction    = defaults.SynFunction
  let synVariable    = defaults.SynVariable
  let synPunctuation = defaults.SynPunctuation
  let synConstant    = defaults.SynConstant
  let synModule      = defaults.SynModule
  let synAttribute   = defaults.SynAttribute
  let synDirective   = defaults.SynDirective
  let synProperty    = defaults.SynProperty

  /// Map a tree-sitter capture name (e.g. "@keyword", "@string") to a theme fg color.
  let tokenColorOfCapture (theme: ThemeConfig) (capture: string) : byte =
    match capture with
    | s when s.StartsWith "keyword" -> theme.SynKeyword
    | s when s.StartsWith "string" -> theme.SynString
    | s when s.StartsWith "comment" -> theme.SynComment
    | s when s.StartsWith "number" -> theme.SynNumber
    | s when s.StartsWith "operator" -> theme.SynOperator
    | s when s.StartsWith "type" -> theme.SynType
    | s when s.StartsWith "function" -> theme.SynFunction
    | s when s.StartsWith "variable.parameter" -> theme.SynVariable
    | s when s.StartsWith "variable.member" -> theme.SynProperty
    | s when s.StartsWith "variable" -> theme.SynVariable
    | s when s.StartsWith "punctuation" -> theme.SynPunctuation
    | s when s.StartsWith "constant.macro" -> theme.SynModule
    | s when s.StartsWith "constant" -> theme.SynConstant
    | s when s.StartsWith "module" -> theme.SynModule
    | s when s.StartsWith "attribute" -> theme.SynAttribute
    | s when s.StartsWith "property" -> theme.SynProperty
    | s when s.StartsWith "boolean" -> theme.SynConstant
    | s when s.StartsWith "character" -> theme.SynOperator
    | s when s.StartsWith "spell" -> theme.FgDefault // ignore @spell
    | _ -> theme.FgDefault

  /// ANSI 256-color index to approximate hex RGB for CSS.
  let ansi256ToHex (idx: byte) : string =
    let i = int idx
    if i < 16 then
      // Standard 16 colors — approximate values
      let colors = [|
        "#000000"; "#800000"; "#008000"; "#808000"; "#000080"; "#800080"; "#008080"; "#c0c0c0"
        "#808080"; "#ff0000"; "#00ff00"; "#ffff00"; "#0000ff"; "#ff00ff"; "#00ffff"; "#ffffff"
      |]
      colors.[i]
    elif i < 232 then
      // 216-color cube: 6×6×6
      let ci = i - 16
      let r = ci / 36
      let g = (ci % 36) / 6
      let b = ci % 6
      let toVal v = if v = 0 then 0 else 55 + v * 40
      sprintf "#%02x%02x%02x" (toVal r) (toVal g) (toVal b)
    else
      // Grayscale: 24 shades
      let v = 8 + (i - 232) * 10
      sprintf "#%02x%02x%02x" v v v

  /// Generate CSS custom properties from a theme config.
  let toCssVariables (theme: ThemeConfig) : string =
    [| sprintf "--fg-default: %s;" (ansi256ToHex theme.FgDefault)
       sprintf "--fg-dim: %s;" (ansi256ToHex theme.FgDim)
       sprintf "--fg-green: %s;" (ansi256ToHex theme.FgGreen)
       sprintf "--fg-red: %s;" (ansi256ToHex theme.FgRed)
       sprintf "--fg-yellow: %s;" (ansi256ToHex theme.FgYellow)
       sprintf "--fg-cyan: %s;" (ansi256ToHex theme.FgCyan)
       sprintf "--fg-blue: %s;" (ansi256ToHex theme.FgBlue)
       sprintf "--fg-magenta: %s;" (ansi256ToHex theme.FgMagenta)
       sprintf "--bg-default: %s;" (ansi256ToHex theme.BgDefault)
       sprintf "--bg-panel: %s;" (ansi256ToHex theme.BgPanel)
       sprintf "--bg-editor: %s;" (ansi256ToHex theme.BgEditor)
       sprintf "--bg-selection: %s;" (ansi256ToHex theme.BgSelection)
       sprintf "--bg-status: %s;" (ansi256ToHex theme.BgStatus)
       sprintf "--bg-focus: %s;" (ansi256ToHex theme.BgFocus)
       sprintf "--border-normal: %s;" (ansi256ToHex theme.BorderNormal)
       sprintf "--border-focus: %s;" (ansi256ToHex theme.BorderFocus)
       sprintf "--syn-keyword: %s;" (ansi256ToHex theme.SynKeyword)
       sprintf "--syn-string: %s;" (ansi256ToHex theme.SynString)
       sprintf "--syn-comment: %s;" (ansi256ToHex theme.SynComment)
       sprintf "--syn-number: %s;" (ansi256ToHex theme.SynNumber)
       sprintf "--syn-operator: %s;" (ansi256ToHex theme.SynOperator)
       sprintf "--syn-type: %s;" (ansi256ToHex theme.SynType)
       sprintf "--syn-function: %s;" (ansi256ToHex theme.SynFunction)
       sprintf "--syn-variable: %s;" (ansi256ToHex theme.SynVariable)
       sprintf "--syn-punctuation: %s;" (ansi256ToHex theme.SynPunctuation)
       sprintf "--syn-constant: %s;" (ansi256ToHex theme.SynConstant)
       sprintf "--syn-module: %s;" (ansi256ToHex theme.SynModule)
       sprintf "--syn-attribute: %s;" (ansi256ToHex theme.SynAttribute)
       sprintf "--syn-directive: %s;" (ansi256ToHex theme.SynDirective)
       sprintf "--syn-property: %s;" (ansi256ToHex theme.SynProperty)
    |]
    |> String.concat "\n  "

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
