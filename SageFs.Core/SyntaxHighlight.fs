namespace SageFs

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection

/// A colored span within a line: start column, length, and fg color as packed RGB (0x00RRGGBB).
[<Struct>]
type ColorSpan = { Start: int; Length: int; Fg: uint32 }

/// Tree-sitter based syntax highlighting for F# code.
module SyntaxHighlight =

  open TreeSitter

  /// Lazy-initialized tree-sitter F# language, parser, and highlight query.
  let resources =
    lazy
      try
        // Find the native DLL — check platform-specific paths
        let asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        let rid =
          match Environment.OSVersion.Platform, Runtime.InteropServices.RuntimeInformation.OSArchitecture with
          | PlatformID.Win32NT, Runtime.InteropServices.Architecture.X64 -> "win-x64"
          | PlatformID.Win32NT, Runtime.InteropServices.Architecture.Arm64 -> "win-arm64"
          | PlatformID.Unix, Runtime.InteropServices.Architecture.X64 ->
            match Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.OSX) with
            | true -> "osx-x64"
            | false -> "linux-x64"
          | PlatformID.Unix, Runtime.InteropServices.Architecture.Arm64 ->
            match Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.OSX) with
            | true -> "osx-arm64"
            | false -> "linux-arm64"
          | _ -> "win-x64"
        let libName =
          match Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.Windows) with
          | true -> "tree-sitter-fsharp.dll"
          | false ->
            match Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.OSX) with
            | true -> "libtree-sitter-fsharp.dylib"
            | false -> "libtree-sitter-fsharp.so"
        let candidates = [
          Path.Combine(asmDir, "runtimes", rid, "native", libName)
          Path.Combine(asmDir, libName)
          Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", libName)
          // Fallback: try win-x64 path (original behavior)
          Path.Combine(asmDir, "runtimes", "win-x64", "native", "tree-sitter-fsharp.dll")
        ]
        let dllPath =
          candidates
          |> List.tryFind File.Exists
        match dllPath with
        | None ->
          eprintfn "SyntaxHighlight: native library not found for %s. Searched: %s" rid (String.Join(", ", candidates))
          None
        | Some path ->

        let lang = new Language(path, "tree_sitter_fsharp")

        // Load highlights.scm from embedded resource
        let asm = Assembly.GetExecutingAssembly()
        let queryText =
          use stream = asm.GetManifestResourceStream("highlights.scm")
          match isNull stream with
          | true -> failwith "highlights.scm embedded resource not found"
          | false -> ()
          use reader = new StreamReader(stream)
          reader.ReadToEnd()

        let query = new Query(lang, queryText)
        Some (lang, query)
      with ex ->
        eprintfn "SyntaxHighlight init failed: %s" ex.Message
        None

  /// Cache of (code + theme keyword color) → per-line ColorSpan arrays.
  /// Uses code string directly as key (no SHA256) — 130x faster lookup.
  let cache = ConcurrentDictionary<string, ColorSpan array array>()

  /// Tokenize F# code into per-line color spans using tree-sitter.
  /// Returns an array of arrays: one ColorSpan array per line.
  let tokenize (theme: ThemeConfig) (code: string) : ColorSpan array array =
    match String.IsNullOrEmpty code with
    | true -> [||]
    | false ->
      let key = String.Concat(code, "\x00", theme.SynKeyword)
      cache.GetOrAdd(key, fun _ ->
        match resources.Value with
        | None ->
          // Fallback: no highlighting, return empty spans for each line
          let lineCount = code.Split('\n').Length
          Array.init lineCount (fun _ -> [||])
        | Some (lang, query) ->
          use parser = new Parser(lang)
          use tree = parser.Parse(code)
          let root = tree.RootNode
          let result = query.Execute(root)

          // Build per-line span lists
          let lines = code.Split('\n')
          let spanLists = Array.init lines.Length (fun _ -> ResizeArray<ColorSpan>())

          for capture in result.Captures do
            let node = capture.Node
            let captureName = capture.Name
            let fg = Theme.hexToRgb (Theme.tokenColorOfCapture theme captureName)

            let startRow = int node.StartPosition.Row
            let startCol = int node.StartPosition.Column
            let endRow = int node.EndPosition.Row
            let endCol = int node.EndPosition.Column

            match startRow = endRow with
            | true ->
              // Single-line capture
              match startRow < spanLists.Length with
              | true ->
                spanLists.[startRow].Add({ Start = startCol; Length = endCol - startCol; Fg = fg })
              | false -> ()
            | false ->
              // Multi-line capture — split across lines
              match startRow < spanLists.Length with
              | true ->
                let firstLineLen = lines.[startRow].Length - startCol
                spanLists.[startRow].Add({ Start = startCol; Length = max 0 firstLineLen; Fg = fg })
              | false -> ()
              for row in (startRow + 1) .. (min (endRow - 1) (spanLists.Length - 1)) do
                spanLists.[row].Add({ Start = 0; Length = lines.[row].Length; Fg = fg })
              match endRow < spanLists.Length with
              | true ->
                spanLists.[endRow].Add({ Start = 0; Length = endCol; Fg = fg })
              | false -> ()

          // Sort spans by start position per line and convert to arrays
          spanLists
          |> Array.map (fun sl ->
            sl.Sort(fun a b -> compare a.Start b.Start)
            sl.ToArray()))

  /// Clear the highlight cache (call after theme changes).
  let clearCache () = cache.Clear()

  /// Check if tree-sitter highlighting is available.
  let isAvailable () =
    match resources.Value with
    | Some _ -> true
    | None -> false
