namespace SageFs.Features.LiveTesting

open System
open System.IO
open System.Reflection

/// Tree-sitter based test discovery for F# source files.
/// Parses source code and returns SourceTestLocation array for detected test attributes.
module TestTreeSitter =

  open TreeSitter

  /// Lazy-initialized tree-sitter F# language and test query.
  /// Shared across all calls — parse is per-invocation but query compilation is one-time.
  let resources =
    lazy
      try
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
        let asm = Assembly.GetExecutingAssembly()
        let queryText =
          use stream = asm.GetManifestResourceStream("tests.scm")
          if isNull stream then
            failwith "tests.scm embedded resource not found"
          use reader = new StreamReader(stream)
          reader.ReadToEnd()
        let query = new Query(lang, queryText)
        Some (lang, query)
      with ex ->
        eprintfn "TestTreeSitter init failed: %s" ex.Message
        None

  /// Discover test locations in F# source code.
  /// Returns SourceTestLocation array with attribute name, file path, line, and column.
  let discover (filePath: string) (code: string) : SourceTestLocation array =
    if String.IsNullOrWhiteSpace code then Array.empty
    else
      match resources.Value with
      | None -> Array.empty
      | Some (lang, query) ->
        use parser = new Parser(lang)
        use tree = parser.Parse(code)
        let root = tree.RootNode
        let result = query.Execute(root)

        let locations = ResizeArray<SourceTestLocation>()
        let mutable currentAttr = ""

        for capture in result.Captures do
          let node = capture.Node
          match capture.Name with
          | "test.attribute" ->
            currentAttr <- code.Substring(int node.StartIndex, int node.EndIndex - int node.StartIndex)
          | "test.name" ->
            if currentAttr.Length > 0 then
              let funcName = code.Substring(int node.StartIndex, int node.EndIndex - int node.StartIndex)
              locations.Add {
                AttributeName = currentAttr
                FunctionName = funcName
                FilePath = filePath
                Line = int node.StartPosition.Row + 1
                Column = int node.StartPosition.Column
              }
              currentAttr <- ""
          | _ -> ()

        locations.ToArray()

  /// Check if tree-sitter test discovery is available.
  let isAvailable () : bool =
    resources.Value.IsSome
