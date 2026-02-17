namespace SageFs

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text

/// A colored span within a line: start column, length, and fg color as packed RGB (0x00RRGGBB).
[<Struct>]
type ColorSpan = { Start: int; Length: int; Fg: uint32 }

/// Tree-sitter based syntax highlighting for F# code.
module SyntaxHighlight =

  open TreeSitter

  /// Lazy-initialized tree-sitter F# language, parser, and highlight query.
  let private resources =
    lazy
      try
        // Find the native DLL — check next to the executing assembly first
        let asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        let candidates = [
          Path.Combine(asmDir, "runtimes", "win-x64", "native", "tree-sitter-fsharp.dll")
          Path.Combine(asmDir, "tree-sitter-fsharp.dll")
          Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "tree-sitter-fsharp.dll")
        ]
        let dllPath =
          candidates
          |> List.tryFind File.Exists
          |> Option.defaultWith (fun () ->
            failwith (sprintf "tree-sitter-fsharp.dll not found. Searched: %s" (String.Join(", ", candidates))))

        let lang = new Language(dllPath, "tree_sitter_fsharp")

        // Load highlights.scm from embedded resource
        let asm = Assembly.GetExecutingAssembly()
        let queryText =
          use stream = asm.GetManifestResourceStream("highlights.scm")
          if isNull stream then
            failwith "highlights.scm embedded resource not found"
          use reader = new StreamReader(stream)
          reader.ReadToEnd()

        let query = new Query(lang, queryText)
        Some (lang, query)
      with ex ->
        eprintfn "SyntaxHighlight init failed: %s" ex.Message
        None

  /// Cache of content hash → per-line ColorSpan arrays.
  let private cache = ConcurrentDictionary<string, ColorSpan array array>()

  let private computeHash (text: string) =
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text))
    Convert.ToHexString(bytes)

  /// Tokenize F# code into per-line color spans using tree-sitter.
  /// Returns an array of arrays: one ColorSpan array per line.
  let tokenize (theme: ThemeConfig) (code: string) : ColorSpan array array =
    if String.IsNullOrEmpty code then [||]
    else
      let key = computeHash code
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

            if startRow = endRow then
              // Single-line capture
              if startRow < spanLists.Length then
                spanLists.[startRow].Add({ Start = startCol; Length = endCol - startCol; Fg = fg })
            else
              // Multi-line capture — split across lines
              if startRow < spanLists.Length then
                let firstLineLen = lines.[startRow].Length - startCol
                spanLists.[startRow].Add({ Start = startCol; Length = max 0 firstLineLen; Fg = fg })
              for row in (startRow + 1) .. (min (endRow - 1) (spanLists.Length - 1)) do
                spanLists.[row].Add({ Start = 0; Length = lines.[row].Length; Fg = fg })
              if endRow < spanLists.Length then
                spanLists.[endRow].Add({ Start = 0; Length = endCol; Fg = fg })

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
