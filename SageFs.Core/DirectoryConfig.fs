namespace SageFs

open System
open System.IO

/// Per-directory configuration via .SageFs/config.fsx.
/// Provides project lists, auto-load preferences, init scripts, default args, and keybindings.
type DirectoryConfig = {
  Projects: string list
  AutoLoad: bool
  InitScript: string option
  DefaultArgs: string list
  Keybindings: KeyMap
  ThemeOverrides: Map<string, byte>
}

module DirectoryConfig =
  let empty = {
    Projects = []
    AutoLoad = true
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
  let parse (content: string) : DirectoryConfig =
    let lines = content.Split('\n') |> Array.map (fun l -> l.Trim())
    let mutable projects = []
    let mutable autoLoad = true
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

    { Projects = projects
      AutoLoad = autoLoad
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
