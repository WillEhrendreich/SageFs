namespace SageFs

/// Built-in theme presets for popular color schemes.
module ThemePresets =
  let kanagawa : ThemeConfig = {
    FgDefault = "#dcd7ba"; FgDim = "#727169"; FgGreen = "#98bb6c"; FgRed = "#ff5d62"
    FgYellow = "#e6c384"; FgCyan = "#7fb4ca"; FgBlue = "#7e9cd8"; FgMagenta = "#957fb8"
    BgDefault = "#1f1f28"; BgPanel = "#2a2a37"; BgEditor = "#1f1f28"
    BgSelection = "#363646"; BgStatus = "#2a2a37"; BgFocus = "#363646"
    BorderNormal = "#54546d"; BorderFocus = "#7e9cd8"
    ColorPass = "#98bb6c"; ColorFail = "#ff5d62"; ColorWarn = "#e6c384"; ColorInfo = "#7fb4ca"
    SynKeyword = "#957fb8"; SynString = "#98bb6c"; SynComment = "#727169"; SynNumber = "#d27e99"
    SynOperator = "#c0a36e"; SynType = "#7aa89f"; SynFunction = "#7e9cd8"; SynVariable = "#dcd7ba"
    SynPunctuation = "#9cabca"; SynConstant = "#ffa066"; SynModule = "#7fb4ca"
    SynAttribute = "#957fb8"; SynDirective = "#957fb8"; SynProperty = "#7fb4ca"
  }

  let tokyoNight : ThemeConfig = {
    FgDefault = "#c0caf5"; FgDim = "#565f89"; FgGreen = "#9ece6a"; FgRed = "#f7768e"
    FgYellow = "#e0af68"; FgCyan = "#7dcfff"; FgBlue = "#7aa2f7"; FgMagenta = "#bb9af7"
    BgDefault = "#1a1b26"; BgPanel = "#24283b"; BgEditor = "#1a1b26"
    BgSelection = "#283457"; BgStatus = "#24283b"; BgFocus = "#292e42"
    BorderNormal = "#3b4261"; BorderFocus = "#7aa2f7"
    ColorPass = "#9ece6a"; ColorFail = "#f7768e"; ColorWarn = "#e0af68"; ColorInfo = "#7dcfff"
    SynKeyword = "#bb9af7"; SynString = "#9ece6a"; SynComment = "#565f89"; SynNumber = "#ff9e64"
    SynOperator = "#89ddff"; SynType = "#2ac3de"; SynFunction = "#7aa2f7"; SynVariable = "#c0caf5"
    SynPunctuation = "#a9b1d6"; SynConstant = "#ff9e64"; SynModule = "#7dcfff"
    SynAttribute = "#bb9af7"; SynDirective = "#bb9af7"; SynProperty = "#73daca"
  }

  let gruvbox : ThemeConfig = {
    FgDefault = "#ebdbb2"; FgDim = "#928374"; FgGreen = "#b8bb26"; FgRed = "#fb4934"
    FgYellow = "#fabd2f"; FgCyan = "#8ec07c"; FgBlue = "#83a598"; FgMagenta = "#d3869b"
    BgDefault = "#282828"; BgPanel = "#3c3836"; BgEditor = "#282828"
    BgSelection = "#504945"; BgStatus = "#3c3836"; BgFocus = "#504945"
    BorderNormal = "#665c54"; BorderFocus = "#83a598"
    ColorPass = "#b8bb26"; ColorFail = "#fb4934"; ColorWarn = "#fabd2f"; ColorInfo = "#8ec07c"
    SynKeyword = "#fb4934"; SynString = "#b8bb26"; SynComment = "#928374"; SynNumber = "#d3869b"
    SynOperator = "#8ec07c"; SynType = "#fabd2f"; SynFunction = "#83a598"; SynVariable = "#ebdbb2"
    SynPunctuation = "#a89984"; SynConstant = "#d3869b"; SynModule = "#8ec07c"
    SynAttribute = "#fb4934"; SynDirective = "#fb4934"; SynProperty = "#83a598"
  }

  let catppuccinMocha : ThemeConfig = {
    FgDefault = "#cdd6f4"; FgDim = "#6c7086"; FgGreen = "#a6e3a1"; FgRed = "#f38ba8"
    FgYellow = "#f9e2af"; FgCyan = "#94e2d5"; FgBlue = "#89b4fa"; FgMagenta = "#cba6f7"
    BgDefault = "#1e1e2e"; BgPanel = "#313244"; BgEditor = "#1e1e2e"
    BgSelection = "#45475a"; BgStatus = "#313244"; BgFocus = "#45475a"
    BorderNormal = "#585b70"; BorderFocus = "#89b4fa"
    ColorPass = "#a6e3a1"; ColorFail = "#f38ba8"; ColorWarn = "#f9e2af"; ColorInfo = "#94e2d5"
    SynKeyword = "#cba6f7"; SynString = "#a6e3a1"; SynComment = "#6c7086"; SynNumber = "#fab387"
    SynOperator = "#89dceb"; SynType = "#f9e2af"; SynFunction = "#89b4fa"; SynVariable = "#cdd6f4"
    SynPunctuation = "#9399b2"; SynConstant = "#fab387"; SynModule = "#94e2d5"
    SynAttribute = "#cba6f7"; SynDirective = "#cba6f7"; SynProperty = "#94e2d5"
  }

  let monokai : ThemeConfig = {
    FgDefault = "#f8f8f2"; FgDim = "#75715e"; FgGreen = "#a6e22e"; FgRed = "#f92672"
    FgYellow = "#e6db74"; FgCyan = "#66d9ef"; FgBlue = "#66d9ef"; FgMagenta = "#ae81ff"
    BgDefault = "#272822"; BgPanel = "#3e3d32"; BgEditor = "#272822"
    BgSelection = "#49483e"; BgStatus = "#3e3d32"; BgFocus = "#49483e"
    BorderNormal = "#75715e"; BorderFocus = "#66d9ef"
    ColorPass = "#a6e22e"; ColorFail = "#f92672"; ColorWarn = "#e6db74"; ColorInfo = "#66d9ef"
    SynKeyword = "#f92672"; SynString = "#e6db74"; SynComment = "#75715e"; SynNumber = "#ae81ff"
    SynOperator = "#f92672"; SynType = "#66d9ef"; SynFunction = "#a6e22e"; SynVariable = "#f8f8f2"
    SynPunctuation = "#f8f8f2"; SynConstant = "#ae81ff"; SynModule = "#66d9ef"
    SynAttribute = "#a6e22e"; SynDirective = "#f92672"; SynProperty = "#66d9ef"
  }

  let dracula : ThemeConfig = {
    FgDefault = "#f8f8f2"; FgDim = "#6272a4"; FgGreen = "#50fa7b"; FgRed = "#ff5555"
    FgYellow = "#f1fa8c"; FgCyan = "#8be9fd"; FgBlue = "#8be9fd"; FgMagenta = "#bd93f9"
    BgDefault = "#282a36"; BgPanel = "#44475a"; BgEditor = "#282a36"
    BgSelection = "#44475a"; BgStatus = "#44475a"; BgFocus = "#6272a4"
    BorderNormal = "#6272a4"; BorderFocus = "#bd93f9"
    ColorPass = "#50fa7b"; ColorFail = "#ff5555"; ColorWarn = "#f1fa8c"; ColorInfo = "#8be9fd"
    SynKeyword = "#ff79c6"; SynString = "#f1fa8c"; SynComment = "#6272a4"; SynNumber = "#bd93f9"
    SynOperator = "#ff79c6"; SynType = "#8be9fd"; SynFunction = "#50fa7b"; SynVariable = "#f8f8f2"
    SynPunctuation = "#f8f8f2"; SynConstant = "#bd93f9"; SynModule = "#8be9fd"
    SynAttribute = "#50fa7b"; SynDirective = "#ff79c6"; SynProperty = "#8be9fd"
  }

  let nordic : ThemeConfig = {
    FgDefault = "#d8dee9"; FgDim = "#616e88"; FgGreen = "#a3be8c"; FgRed = "#bf616a"
    FgYellow = "#ebcb8b"; FgCyan = "#88c0d0"; FgBlue = "#81a1c1"; FgMagenta = "#b48ead"
    BgDefault = "#2e3440"; BgPanel = "#3b4252"; BgEditor = "#2e3440"
    BgSelection = "#434c5e"; BgStatus = "#3b4252"; BgFocus = "#434c5e"
    BorderNormal = "#4c566a"; BorderFocus = "#81a1c1"
    ColorPass = "#a3be8c"; ColorFail = "#bf616a"; ColorWarn = "#ebcb8b"; ColorInfo = "#88c0d0"
    SynKeyword = "#81a1c1"; SynString = "#a3be8c"; SynComment = "#616e88"; SynNumber = "#b48ead"
    SynOperator = "#81a1c1"; SynType = "#8fbcbb"; SynFunction = "#88c0d0"; SynVariable = "#d8dee9"
    SynPunctuation = "#d8dee9"; SynConstant = "#b48ead"; SynModule = "#8fbcbb"
    SynAttribute = "#81a1c1"; SynDirective = "#81a1c1"; SynProperty = "#88c0d0"
  }

  let oneDark : ThemeConfig = Theme.defaults

  /// All built-in presets as (name, config) pairs.
  let all : (string * ThemeConfig) list = [
    "One Dark", oneDark
    "Kanagawa", kanagawa
    "Tokyo Night", tokyoNight
    "Gruvbox", gruvbox
    "Catppuccin Mocha", catppuccinMocha
    "Monokai", monokai
    "Dracula", dracula
    "Nordic", nordic
  ]

  /// Find a preset by name (case-insensitive).
  let tryFind (name: string) : ThemeConfig option =
    all |> List.tryFind (fun (n, _) -> n.Equals(name, System.StringComparison.OrdinalIgnoreCase))
    |> Option.map snd

  /// Get the next theme after the given one (cycles through all presets).
  let cycleNext (current: ThemeConfig) : string * ThemeConfig =
    let idx =
      all |> List.tryFindIndex (fun (_, t) -> t = current)
      |> Option.defaultValue -1
    let next = (idx + 1) % all.Length
    all.[next]
