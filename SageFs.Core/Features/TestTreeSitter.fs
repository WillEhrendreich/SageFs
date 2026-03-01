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
          Path.Combine(asmDir, "runtimes", "win-x64", "native", "tree-sitter-fsharp.dll")
        ]
        let dllPath =
          candidates
          |> List.tryFind File.Exists
        match dllPath with
        | None ->
          eprintfn "TestTreeSitter: native library not found for %s. Searched: %s" rid (String.Join(", ", candidates))
          None
        | Some path ->
        let lang = new Language(path, "tree_sitter_fsharp")
        let asm = Assembly.GetExecutingAssembly()
        let queryText =
          use stream = asm.GetManifestResourceStream("tests.scm")
          match isNull stream with
          | true -> failwith "tests.scm embedded resource not found"
          | false -> ()
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
    match String.IsNullOrWhiteSpace code with
    | true -> Array.empty
    | false ->
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
            match currentAttr.Length > 0 with
            | true ->
              let funcName = code.Substring(int node.StartIndex, int node.EndIndex - int node.StartIndex)
              locations.Add {
                AttributeName = currentAttr
                FunctionName = funcName
                FilePath = filePath
                Line = int node.StartPosition.Row + 1
                Column = int node.StartPosition.Column
              }
              currentAttr <- ""
            | false -> ()
          | _ -> ()

        locations.ToArray()

  /// Check if tree-sitter test discovery is available.
  let isAvailable () : bool =
    resources.Value.IsSome
