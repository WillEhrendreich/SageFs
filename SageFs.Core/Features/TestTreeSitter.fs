namespace SageFs.Features.LiveTesting

open System
open System.IO
open System.Reflection
open SageFs.Utils

/// Tree-sitter based test discovery for F# source files.
/// Parses source code and returns SourceTestLocation array for detected test attributes.
module TestTreeSitter =

  open TreeSitter

  [<RequireQualifiedAccess>]
  type NativeAvailability =
    | Available of runtimeId: string * libraryPath: string
    | Degraded of runtimeId: string * reason: string * searchedPaths: string list

  module NativeAvailability =
    let isAvailable = function
      | NativeAvailability.Available _ -> true
      | NativeAvailability.Degraded _ -> false

    let describe = function
      | NativeAvailability.Available (runtimeId, libraryPath) ->
        sprintf "tree-sitter test discovery available on %s via %s" runtimeId libraryPath
      | NativeAvailability.Degraded (runtimeId, reason, searchedPaths) ->
        sprintf
          "tree-sitter test discovery degraded on %s: %s. Searched: %s"
          runtimeId
          reason
          (String.Join(", ", searchedPaths))

  type private ResourceState = {
    Availability: NativeAvailability
    Resources: (Language * Query) option
  }

  let private detectRuntimeId () =
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

  let private nativeLibraryName () =
    match Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.Windows) with
    | true -> "tree-sitter-fsharp.dll"
    | false ->
      match Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.OSX) with
      | true -> "libtree-sitter-fsharp.dylib"
      | false -> "libtree-sitter-fsharp.so"

  let private candidatePaths (asmDir: string) (runtimeId: string) (libName: string) = [
    Path.Combine(asmDir, "runtimes", runtimeId, "native", libName)
    Path.Combine(asmDir, libName)
    Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeId, "native", libName)
    Path.Combine(asmDir, "runtimes", "win-x64", "native", "tree-sitter-fsharp.dll")
  ]

  /// Lazy-initialized tree-sitter F# language and test query.
  /// Shared across all calls — parse is per-invocation but query compilation is one-time.
  let private resources =
    lazy
      let asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
      let runtimeId = detectRuntimeId ()
      let libName = nativeLibraryName ()
      let candidates = candidatePaths asmDir runtimeId libName

      let degraded reason = {
        Availability = NativeAvailability.Degraded (runtimeId, reason, candidates)
        Resources = None
      }

      try
        match candidates |> List.tryFind File.Exists with
        | None ->
          Log.warn "TestTreeSitter: native library not found for %s. Searched: %s" runtimeId (String.Join(", ", candidates))
          degraded (sprintf "native library '%s' not found" libName)
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
          {
            Availability = NativeAvailability.Available (runtimeId, path)
            Resources = Some (lang, query)
          }
      with ex ->
        Log.error "TestTreeSitter init failed: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        degraded (sprintf "initialization failed: %s" ex.Message)

  /// Discover test locations in F# source code.
  /// Returns SourceTestLocation array with attribute name, file path, line, and column.
  let discover (filePath: string) (code: string) : SourceTestLocation array =
    match String.IsNullOrWhiteSpace code with
    | true -> Array.empty
    | false ->
      match resources.Value.Resources with
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

  /// Report whether tree-sitter test discovery is available or degraded.
  let availability () : NativeAvailability =
    resources.Value.Availability

  /// Describe the current tree-sitter test discovery availability in one line.
  let describeAvailability () : string =
    availability () |> NativeAvailability.describe

  /// Check if tree-sitter test discovery is available.
  let isAvailable () : bool =
    availability () |> NativeAvailability.isAvailable
