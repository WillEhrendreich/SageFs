namespace SageFs

/// Theme configuration record — all named color values as hex RGB strings (e.g. "#c8d3f5").
type ThemeConfig = {
  FgDefault: string; FgDim: string; FgGreen: string; FgRed: string
  FgYellow: string; FgCyan: string; FgBlue: string; FgMagenta: string
  BgDefault: string; BgPanel: string; BgEditor: string
  BgSelection: string; BgStatus: string; BgFocus: string
  BorderNormal: string; BorderFocus: string
  ColorPass: string; ColorFail: string; ColorWarn: string; ColorInfo: string
  // Syntax highlighting token colors
  SynKeyword: string; SynString: string; SynComment: string; SynNumber: string
  SynOperator: string; SynType: string; SynFunction: string; SynVariable: string
  SynPunctuation: string; SynConstant: string; SynModule: string
  SynAttribute: string; SynDirective: string; SynProperty: string
}

/// Named color palette — hex RGB strings.
/// TUI converts to truecolor ANSI (38;2;r;g;b). Raylib parses hex directly. Dashboard passes through to CSS.
module Theme =
  let defaults : ThemeConfig = {
    FgDefault = "#ffffff"; FgDim = "#8b8b8b"; FgGreen = "#87d787"; FgRed = "#ff5f5f"
    FgYellow = "#d7af5f"; FgCyan = "#87d7d7"; FgBlue = "#5fafff"; FgMagenta = "#d787d7"
    BgDefault = "#000000"; BgPanel = "#262626"; BgEditor = "#1c1c1c"
    BgSelection = "#444444"; BgStatus = "#303030"; BgFocus = "#3a3a3a"
    BorderNormal = "#585858"; BorderFocus = "#5fafff"
    ColorPass = "#87d787"; ColorFail = "#ff5f5f"; ColorWarn = "#d7af5f"; ColorInfo = "#87d7d7"
    // Syntax tokens — One Dark inspired
    SynKeyword = "#d787d7"    // magenta — let, match, type, if
    SynString = "#87d787"     // green — "hello"
    SynComment = "#8b8b8b"    // dim gray — // comment
    SynNumber = "#d7af5f"     // yellow — 42, 3.14
    SynOperator = "#87d7d7"   // cyan — |>, +, =
    SynType = "#d7af5f"       // yellow — string, int, MyType
    SynFunction = "#5fafff"   // blue — function names
    SynVariable = "#ffffff"   // white — identifiers
    SynPunctuation = "#8b8b8b" // dim — ( ) { } [ ]
    SynConstant = "#d7af5f"   // yellow — DU cases, Literal values
    SynModule = "#87d7d7"     // cyan — module names (List, Array, Seq)
    SynAttribute = "#d787d7"  // magenta — [<Test>]
    SynDirective = "#d787d7"  // magenta — #r, #load, #if
    SynProperty = "#87d7d7"   // cyan — record fields
  }

  /// Apply partial overrides from a map of name -> hex value onto a base config
  let withOverrides (overrides: Map<string, string>) (base': ThemeConfig) : ThemeConfig =
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

  /// Parse hex RGB string "#RRGGBB" to packed uint32 (0x00RRGGBB).
  let hexToRgb (hex: string) : uint32 =
    match hex.Length >= 7 && hex.[0] = '#' with
    | true -> System.UInt32.Parse(hex.Substring(1, 6), System.Globalization.NumberStyles.HexNumber)
    | false -> 0u

  /// Extract R, G, B bytes from packed uint32 RGB.
  let inline rgbR (rgb: uint32) = byte (rgb >>> 16)
  let inline rgbG (rgb: uint32) = byte (rgb >>> 8)
  let inline rgbB (rgb: uint32) = byte rgb

  /// Map a tree-sitter capture name (e.g. "keyword", "string") to a theme hex color.
  let tokenColorOfCapture (theme: ThemeConfig) (capture: string) : string =
    match capture with
    | s when s.StartsWith("keyword", System.StringComparison.Ordinal) -> theme.SynKeyword
    | s when s.StartsWith("string", System.StringComparison.Ordinal) -> theme.SynString
    | s when s.StartsWith("comment", System.StringComparison.Ordinal) -> theme.SynComment
    | s when s.StartsWith("number", System.StringComparison.Ordinal) -> theme.SynNumber
    | s when s.StartsWith("operator", System.StringComparison.Ordinal) -> theme.SynOperator
    | s when s.StartsWith("type", System.StringComparison.Ordinal) -> theme.SynType
    | s when s.StartsWith("function", System.StringComparison.Ordinal) -> theme.SynFunction
    | s when s.StartsWith("variable.parameter", System.StringComparison.Ordinal) -> theme.SynVariable
    | s when s.StartsWith("variable.member", System.StringComparison.Ordinal) -> theme.SynProperty
    | s when s.StartsWith("variable", System.StringComparison.Ordinal) -> theme.SynVariable
    | s when s.StartsWith("punctuation", System.StringComparison.Ordinal) -> theme.SynPunctuation
    | s when s.StartsWith("constant.macro", System.StringComparison.Ordinal) -> theme.SynModule
    | s when s.StartsWith("constant", System.StringComparison.Ordinal) -> theme.SynConstant
    | s when s.StartsWith("module", System.StringComparison.Ordinal) -> theme.SynModule
    | s when s.StartsWith("attribute", System.StringComparison.Ordinal) -> theme.SynAttribute
    | s when s.StartsWith("property", System.StringComparison.Ordinal) -> theme.SynProperty
    | s when s.StartsWith("boolean", System.StringComparison.Ordinal) -> theme.SynConstant
    | s when s.StartsWith("character", System.StringComparison.Ordinal) -> theme.SynOperator
    | s when s.StartsWith("spell", System.StringComparison.Ordinal) -> theme.FgDefault // ignore @spell
    | _ -> theme.FgDefault

  /// Generate CSS custom properties from a theme config (hex passthrough).
  let toCssVariables (theme: ThemeConfig) : string =
    [| sprintf "--fg-default: %s;" theme.FgDefault
       sprintf "--fg-dim: %s;" theme.FgDim
       sprintf "--fg-green: %s;" theme.FgGreen
       sprintf "--fg-red: %s;" theme.FgRed
       sprintf "--fg-yellow: %s;" theme.FgYellow
       sprintf "--fg-cyan: %s;" theme.FgCyan
       sprintf "--fg-blue: %s;" theme.FgBlue
       sprintf "--fg-magenta: %s;" theme.FgMagenta
       sprintf "--bg-default: %s;" theme.BgDefault
       sprintf "--bg-panel: %s;" theme.BgPanel
       sprintf "--bg-editor: %s;" theme.BgEditor
       sprintf "--bg-selection: %s;" theme.BgSelection
       sprintf "--bg-status: %s;" theme.BgStatus
       sprintf "--bg-focus: %s;" theme.BgFocus
       sprintf "--border-normal: %s;" theme.BorderNormal
       sprintf "--border-focus: %s;" theme.BorderFocus
       sprintf "--syn-keyword: %s;" theme.SynKeyword
       sprintf "--syn-string: %s;" theme.SynString
       sprintf "--syn-comment: %s;" theme.SynComment
       sprintf "--syn-number: %s;" theme.SynNumber
       sprintf "--syn-operator: %s;" theme.SynOperator
       sprintf "--syn-type: %s;" theme.SynType
       sprintf "--syn-function: %s;" theme.SynFunction
       sprintf "--syn-variable: %s;" theme.SynVariable
       sprintf "--syn-punctuation: %s;" theme.SynPunctuation
       sprintf "--syn-constant: %s;" theme.SynConstant
       sprintf "--syn-module: %s;" theme.SynModule
       sprintf "--syn-attribute: %s;" theme.SynAttribute
       sprintf "--syn-directive: %s;" theme.SynDirective
       sprintf "--syn-property: %s;" theme.SynProperty
    |]
    |> String.concat "\n  "

  /// Parse theme lines from config.fsx format:
  ///   let theme = [ "fgDefault", "#ffffff"; "bgPanel", "#262626" ]
  let parseConfigLines (lines: string array) : Map<string, string> =
    let mutable overrides = Map.empty
    let mutable inTheme = false
    for line in lines do
      let trimmed = line.Trim()
      match trimmed.StartsWith("let theme", System.StringComparison.Ordinal) || trimmed.StartsWith("let Theme", System.StringComparison.Ordinal) with
      | true -> inTheme <- true
      | false -> ()
      match inTheme with
      | true ->
        let mutable i = 0
        while i < trimmed.Length do
          let q1 = trimmed.IndexOf('"', i)
          match q1 >= 0 with
          | true ->
            let q2 = trimmed.IndexOf('"', q1 + 1)
            match q2 > q1 with
            | true ->
              let name = trimmed.Substring(q1 + 1, q2 - q1 - 1)
              let comma = trimmed.IndexOf(',', q2 + 1)
              match comma >= 0 with
              | true ->
                let rest = trimmed.Substring(comma + 1).Trim()
                // Look for quoted hex value like "#ffffff"
                let vq1 = rest.IndexOf('"')
                match vq1 >= 0 with
                | true ->
                  let vq2 = rest.IndexOf('"', vq1 + 1)
                  match vq2 > vq1 with
                  | true ->
                    let value = rest.Substring(vq1 + 1, vq2 - vq1 - 1)
                    match value.StartsWith("#", System.StringComparison.Ordinal) with
                    | true -> overrides <- Map.add name value overrides
                    | false -> ()
                    i <- comma + 1 + vq2 + 1
                  | false -> i <- trimmed.Length
                | false -> i <- trimmed.Length
              | false -> i <- trimmed.Length
            | false -> i <- trimmed.Length
          | false -> i <- trimmed.Length
        match trimmed.Contains(']') && inTheme && not (trimmed.StartsWith("let", System.StringComparison.Ordinal)) with
        | true -> inTheme <- false
        | false -> ()
      | false -> ()
    overrides
