namespace SageFs

open System
open System.IO

/// Specifies how projects/solutions should be loaded for a session.
type LoadStrategy =
  /// Load a specific solution file (.sln/.slnx)
  | Solution of path: string
  /// Load specific project files (.fsproj)
  | Projects of paths: string list
  /// Auto-detect projects/solutions from the directory (default)
  | AutoDetect
  /// Bare FSI session — no project loading
  | NoLoad

/// Per-directory configuration via .SageFs/config.fsx.
/// Provides load strategy, init scripts, default args, and keybindings.
type DirectoryConfig = {
  Load: LoadStrategy
  InitScript: string option
  DefaultArgs: string list
  Keybindings: KeyMap
  ThemeOverrides: Map<string, byte>
}

module DirectoryConfig =
  let empty = {
    Load = AutoDetect
    InitScript = None
    DefaultArgs = []
    Keybindings = Map.empty
    ThemeOverrides = Map.empty
  }

  let configDir (workingDir: string) =
    Path.Combine(workingDir, ".SageFs")

  let configPath (workingDir: string) =
    Path.Combine(configDir workingDir, "config.fsx")

  /// Parse a config.fsx file content into DirectoryConfig.
  /// Extracts let bindings for projects, autoLoad, initScript, defaultArgs.
  /// Backward-compatible: maps old Projects/AutoLoad fields to LoadStrategy.
  let parse (content: string) : DirectoryConfig =
    let lines = content.Split('\n') |> Array.map (fun l -> l.Trim())
    let mutable projects = []
    let mutable autoLoad = true
    let mutable solution = ""
    let mutable initScript = None
    let mutable defaultArgs = []

    for line in lines do
      if line.StartsWith("let projects") || line.StartsWith("let Projects") then
        let bracketStart = line.IndexOf('[')
        let bracketEnd = line.LastIndexOf(']')
        if bracketStart >= 0 && bracketEnd > bracketStart then
          let inner = line.Substring(bracketStart + 1, bracketEnd - bracketStart - 1)
          projects <-
            inner.Split([|';'|], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.Trim().Trim('"'))
            |> Array.filter (fun s -> s.Length > 0)
            |> Array.toList
      elif line.StartsWith("let solution") || line.StartsWith("let Solution") then
        let eqIdx = line.IndexOf('=')
        if eqIdx >= 0 then
          let value = line.Substring(eqIdx + 1).Trim().Trim('"')
          if value.Length > 0 then solution <- value
      elif line.StartsWith("let autoLoad") || line.StartsWith("let AutoLoad") then
        autoLoad <- line.Contains("true")
      elif line.StartsWith("let initScript") || line.StartsWith("let InitScript") then
        let eqIdx = line.IndexOf('=')
        if eqIdx >= 0 then
          let value = line.Substring(eqIdx + 1).Trim()
          if value.StartsWith("Some") then
            let inner = value.Replace("Some", "").Trim().Trim('"')
            initScript <- Some inner
          elif value.StartsWith("\"") then
            initScript <- Some (value.Trim('"'))
      elif line.StartsWith("let defaultArgs") || line.StartsWith("let DefaultArgs") then
        let bracketStart = line.IndexOf('[')
        let bracketEnd = line.LastIndexOf(']')
        if bracketStart >= 0 && bracketEnd > bracketStart then
          let inner = line.Substring(bracketStart + 1, bracketEnd - bracketStart - 1)
          defaultArgs <-
            inner.Split([|';'|], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.Trim().Trim('"'))
            |> Array.filter (fun s -> s.Length > 0)
            |> Array.toList

    // Map old fields to LoadStrategy
    let load =
      if solution.Length > 0 then Solution solution
      elif not projects.IsEmpty then Projects projects
      elif not autoLoad then NoLoad
      else AutoDetect

    { Load = load
      InitScript = initScript
      DefaultArgs = defaultArgs
      Keybindings = KeyMap.parseConfigLines lines
      ThemeOverrides = Theme.parseConfigLines lines }

  let load (workingDir: string) : DirectoryConfig option =
    let path = configPath workingDir
    if File.Exists path then
      let content = File.ReadAllText path
      Some (parse content)
    else
      None
