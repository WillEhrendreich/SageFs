namespace SageFs

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
open System.Text.Json

/// Resolves a hosted project's UNMANAGED native dependencies.
///
/// Why this exists: when the worker hosts a user project that P/Invokes a native
/// library (e.g. Raylib-cs -> `raylib` -> `libraylib.so`), the default CoreCLR
/// native-library probe searches the worker's OWN base directory, the shared
/// framework directory, and the requesting managed assembly's own directory.
/// It does NOT search the hosted project's build output, where the build copied
/// the native lib under `runtimes/<rid>/native/`. The P/Invoke therefore throws
/// `DllNotFoundException`. If that throw lands on a background thread the worker
/// does not own (a user `new Thread(fun () -> Raylib.InitWindow ...)` in an eval),
/// the runtime escalates the UNHANDLED exception to a process-wide FailFast
/// (SIGABRT) — killing the entire worker. So the reflection scan was never the
/// culprit: an unresolved native dependency was. Making the native library
/// resolvable removes the throw at its source, which is the only structural way
/// to keep the worker alive (a genuinely unhandled exception on a foreign thread
/// cannot be swallowed after the fact on .NET Core).
///
/// The pure `candidatePaths`/`fileNameCandidates` functions decide WHERE to look
/// (data-in, data-out, no IO) so the policy is unit-testable; `install` wires the
/// decision to `AssemblyLoadContext.Default.ResolvingUnmanagedDll` and is the only
/// part that touches the filesystem or the loader. It fails CLOSED and QUIET:
/// a miss returns `IntPtr.Zero`, deferring to the default behavior (the same
/// DllNotFoundException as before), never a new failure of its own.
[<RequireQualifiedAccess>]
module NativeResolution =

  /// Platform-decorated file-name candidates for a DllImport library name,
  /// most-decorated first so `libraylib.so` is preferred over a bare `raylib`.
  /// `libraryName` is exactly what the P/Invoke requested (e.g. "raylib").
  let fileNameCandidates (isWindows: bool) (isOsx: bool) (libraryName: string) : string list =
    match String.IsNullOrWhiteSpace libraryName with
    | true -> []
    | false ->
      let ext, prefix =
        match isWindows, isOsx with
        | true, _ -> ".dll", ""
        | false, true -> ".dylib", "lib"
        | false, false -> ".so", "lib"
      // Strip a trailing known extension so we can re-decorate consistently.
      let core =
        match libraryName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) with
        | true -> libraryName.Substring(0, libraryName.Length - ext.Length)
        | false -> libraryName
      let withPrefix =
        match prefix = "" || core.StartsWith(prefix, StringComparison.Ordinal) with
        | true -> core
        | false -> prefix + core
      [ withPrefix + ext   // libraylib.so   (fully decorated — the real file)
        core + ext         // raylib.so
        libraryName ]      // raylib          (as requested, last resort)
      |> List.distinct
      |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))

  /// Ordered, de-duplicated absolute candidate paths to try for a native library,
  /// given the search-root directories and the runtime identifiers to consider.
  /// For each root: the flat directory first, then `runtimes/<rid>/native/`.
  /// If the requested name is already a rooted path, it is returned verbatim.
  let candidatePaths
    (roots: string list)
    (rids: string list)
    (isWindows: bool)
    (isOsx: bool)
    (libraryName: string)
    : string list =
    match not (String.IsNullOrWhiteSpace libraryName) && Path.IsPathRooted libraryName with
    | true -> [ libraryName ]
    | false ->
      let names = fileNameCandidates isWindows isOsx libraryName
      let subdirs =
        "" :: (rids |> List.map (fun rid -> Path.Combine("runtimes", rid, "native")))
      [ for root in roots do
          for sub in subdirs do
            for name in names do
              yield Path.GetFullPath(Path.Combine(root, sub, name)) ]
      |> List.distinct

  /// Runtime identifiers to probe, running RID first, then a portable
  /// `<os>-<arch>` fallback (they usually coincide, but a self-contained publish
  /// or a distro-specific RID can make `RuntimeIdentifier` more specific).
  let currentRids () : string list =
    let running = RuntimeInformation.RuntimeIdentifier
    let os =
      match RuntimeInformation.IsOSPlatform OSPlatform.Windows,
            RuntimeInformation.IsOSPlatform OSPlatform.OSX with
      | true, _ -> "win"
      | _, true -> "osx"
      | _ -> "linux"
    let arch =
      match RuntimeInformation.ProcessArchitecture with
      | Architecture.X64 -> "x64"
      | Architecture.Arm64 -> "arm64"
      | Architecture.X86 -> "x86"
      | Architecture.Arm -> "arm"
      | other -> string(other).ToLowerInvariant()
    [ running; sprintf "%s-%s" os arch ]
    |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
    |> List.distinct

  // Mutable, thread-safe set of search roots. The resolver callback fires on
  // arbitrary CLR threads, so reads take a snapshot under the lock.
  let private rootsLock = obj ()
  let mutable private searchRoots : string list = []
  let private registered = ref 0

  /// Add directories to probe for native libraries (idempotent, de-duplicated).
  let addRoots (dirs: string seq) : unit =
    lock rootsLock (fun () ->
      let existing = Set.ofList searchRoots
      let added =
        dirs
        |> Seq.choose (fun d ->
          match String.IsNullOrWhiteSpace d with
          | true -> None
          | false ->
            let full = try Path.GetFullPath d with _ -> d
            match existing.Contains full with
            | true -> None
            | false -> Some full)
        |> Seq.toList
      searchRoots <- searchRoots @ (added |> List.distinct))

  let private currentRoots () : string list =
    lock rootsLock (fun () -> searchRoots)

  /// Install the native-library resolver on the default load context (once).
  /// Adds `roots` to the search set. The callback is fail-closed: on any miss or
  /// error it returns `IntPtr.Zero`, letting the runtime fall back to its default
  /// probe (the pre-existing DllNotFoundException), never crashing or throwing.
  let install (log: string -> unit) (roots: string seq) : unit =
    addRoots roots
    match System.Threading.Interlocked.CompareExchange(registered, 1, 0) = 0 with
    | false -> ()
    | true ->
      let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
      let isOsx = RuntimeInformation.IsOSPlatform OSPlatform.OSX
      let rids = currentRids ()
      let handler =
        Func<Assembly, string, IntPtr>(fun (asm: Assembly) (name: string) ->
          try
            // Also probe the requesting assembly's own directory — cheap and
            // covers native libs deployed flat next to a managed assembly.
            let asmDir =
              try
                match asm.Location with
                | null | "" -> None
                | loc -> Some(Path.GetDirectoryName loc)
              with _ -> None
            let roots =
              (currentRoots () @ (asmDir |> Option.toList))
              |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
            let candidates = candidatePaths roots rids isWindows isOsx name
            let rec tryLoad remaining =
              match remaining with
              | [] -> IntPtr.Zero
              | path :: rest ->
                match File.Exists path with
                | false -> tryLoad rest
                | true ->
                  match NativeLibrary.TryLoad path with
                  | true, handle ->
                    log (sprintf "[NativeResolution] resolved '%s' -> %s" name path)
                    handle
                  | false, _ -> tryLoad rest
            tryLoad candidates
          with ex ->
            // Fail closed: never let the resolver itself become the crash.
            log (sprintf "[NativeResolution] resolver error for '%s': %s" name ex.Message)
            IntPtr.Zero)
      AssemblyLoadContext.Default.add_ResolvingUnmanagedDll handler

/// Which runtimes a native asset is for.
type NativeAssetScope =
  /// A `runtimeTargets` entry: the package says which runtime identifier this file is for.
  | ForRuntime of runtimeId: string
  /// An entry of the target's own `native` group: the restore was for one runtime, so the file is for it.
  | ForRestoredTarget

/// One native file a package ships, as an absolute path.
type NativeAsset = { Scope: NativeAssetScope; File: string }

/// A restored package that ships native files.
type NativePackage = { Name: string; Version: string; Assets: NativeAsset list }

/// A package that ships native libraries, but none for the runtime the host is on.
type NativeGap =
  { Name: string
    Version: string
    ShippedRuntimes: string list
    LibraryStems: string list }

/// What reading a project's restore record gave: the packages that carry native files, or why it could not be read.
type NativeAssetsReading =
  | PackagesRead of NativePackage list
  | Unreadable of reason: string

/// What the host knows about native libraries: which runtime it is on and which packages ship natives.
type NativeFacts =
  { Runtime: string
    Compatible: string list
    Packages: NativePackage list }

/// Reads the native libraries a project's restore says its packages carry (`obj/project.assets.json`), so the isolated
/// host can make them loadable and say what is wrong when one cannot be. Why the host needs this: a package's native
/// library lives in the NuGet cache under `runtimes/<rid>/native`, which the loader never searches, and a class
/// library's build output does not hold a copy (only an executable's does).
[<RequireQualifiedAccess>]
module NativeAssets =

  /// The environment variable the worker names a host's assets files in: the `project.assets.json` of each project
  /// the session loaded, separated by the platform's path separator.
  [<Literal>]
  let EnvironmentVariable = "SAGEFS_PROJECT_ASSETS"

  /// The runtime identifiers a native file may be for on this machine, most specific first: the running one, then the
  /// portable `<os>-<arch>`, then the bare OS and, off Windows, the `unix` family NuGet's runtime graph also names.
  let compatibleRuntimes (running: string) (os: string) (arch: string) : string list =
    let family =
      match os with
      | "win" -> []
      | _ -> [ sprintf "unix-%s" arch; "unix" ]
    [ [ running; sprintf "%s-%s" os arch; os ]; family ]
    |> List.concat
    |> List.filter (fun id -> not (String.IsNullOrWhiteSpace id))
    |> List.distinct

  /// A file name without its directory, `lib` prefix and extension: `libSkiaSharp.so` and `SkiaSharp.dll` are `skiasharp`.
  let libraryStem (fileName: string) : string =
    let name = Path.GetFileName fileName
    let noExtension =
      match name.IndexOf ".so." with
      | -1 -> Path.GetFileNameWithoutExtension name
      | at -> name.Substring(0, at)
    let noPrefix =
      match noExtension.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && noExtension.Length > 3 with
      | true -> noExtension.Substring 3
      | false -> noExtension
    noPrefix.ToLowerInvariant()

  let splitIdentity (identity: string) : string * string =
    match identity.LastIndexOf '/' with
    | -1 -> identity, ""
    | at -> identity.Substring(0, at), identity.Substring(at + 1)

  let members (element: JsonElement) : (string * JsonElement) list =
    match element.ValueKind with
    | JsonValueKind.Object -> [ for property in element.EnumerateObject() -> property.Name, property.Value ]
    | _ -> []

  let text (element: JsonElement) (name: string) : string =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj |> Option.defaultValue ""
    | _ -> ""

  let child (element: JsonElement) (name: string) : JsonElement =
    match element.ValueKind, element.TryGetProperty name with
    | JsonValueKind.Object, (true, value) -> value
    | _ -> JsonDocument.Parse("{}").RootElement

  /// Where one package's native files are, from a `project.assets.json`: every package that ships a native file, with
  /// absolute paths. `directoryExists` says which package folder actually holds the package (a restore may list a
  /// fallback folder first). A package whose folder cannot be found has nothing the host could load, so it is left out.
  let read (directoryExists: string -> bool) (json: string) : NativeAssetsReading =
    try
      use document = JsonDocument.Parse json
      let root = document.RootElement
      let folders = members (child root "packageFolders") |> List.map fst
      let libraries = members (child root "libraries") |> Map.ofList
      let folderOf (identity: string) : string =
        match Map.tryFind identity libraries with
        | None -> ""
        | Some library ->
          let relative = text library "path"
          match relative with
          | "" -> ""
          | _ ->
            folders
            |> List.map (fun folder -> Path.Combine(folder, relative))
            |> List.tryFind directoryExists
            |> Option.defaultValue ""
      let packages =
        [ for _, target in members (child root "targets") do
            for identity, package in members target do
              let name, version = splitIdentity identity
              let runtimeAssets =
                members (child package "runtimeTargets")
                |> List.choose (fun (path, details) ->
                  match text details "assetType", text details "rid" with
                  | "native", "" -> Some(ForRestoredTarget, path)
                  | "native", rid -> Some(ForRuntime rid, path)
                  | _ -> None)
              let targetAssets = members (child package "native") |> List.map (fun (path, _) -> ForRestoredTarget, path)
              match runtimeAssets @ targetAssets with
              | [] -> ()
              | assets ->
                match folderOf identity with
                | "" -> ()
                | folder ->
                  yield
                    { Name = name
                      Version = version
                      Assets =
                        assets
                        |> List.map (fun (scope, path) -> { Scope = scope; File = Path.GetFullPath(Path.Combine(folder, path)) }) } ]
      // A multi-target restore lists a package once per target; keep one copy of each file.
      let merged =
        packages
        |> List.groupBy (fun p -> p.Name, p.Version)
        |> List.map (fun ((name, version), group) ->
          { Name = name
            Version = version
            Assets = group |> List.collect (fun p -> p.Assets) |> List.distinct })
      PackagesRead merged
    with ex -> Unreadable ex.Message

  let applies (compatible: string list) (asset: NativeAsset) : bool =
    match asset.Scope with
    | ForRestoredTarget -> true
    | ForRuntime id -> List.contains id compatible

  /// The directories to search for native libraries: the folder of every native file that is for this machine.
  let directories (compatible: string list) (packages: NativePackage list) : string list =
    packages
    |> List.collect (fun p -> p.Assets)
    |> List.filter (applies compatible)
    |> List.choose (fun a -> Path.GetDirectoryName a.File |> Option.ofObj)
    |> List.distinct

  /// The packages that ship the library with this stem for this machine.
  let providers (compatible: string list) (stem: string) (packages: NativePackage list) : NativePackage list =
    packages
    |> List.filter (fun p -> p.Assets |> List.exists (fun a -> applies compatible a && libraryStem a.File = stem))

  /// The packages that ship native libraries but none for this machine, and whose libraries no package that does ship
  /// one for this machine provides. (SkiaSharp depends on a package for every platform; on Linux the Windows and macOS
  /// ones ship nothing for it, and that is not a gap because the Linux one provides the same library.)
  let gaps (compatible: string list) (packages: NativePackage list) : NativeGap list =
    let stemsOf (assets: NativeAsset list) = assets |> List.map (fun a -> libraryStem a.File) |> List.distinct
    let covered =
      packages
      |> List.collect (fun p -> p.Assets |> List.filter (applies compatible))
      |> stemsOf
      |> Set.ofList
    packages
    |> List.filter (fun p -> p.Assets |> List.exists (applies compatible) |> not)
    |> List.choose (fun p ->
      let stems = stemsOf p.Assets
      match stems |> List.exists (fun stem -> not (Set.contains stem covered)) with
      | false -> None
      | true ->
        Some
          { Name = p.Name
            Version = p.Version
            ShippedRuntimes =
              p.Assets
              |> List.choose (fun a ->
                match a.Scope with
                | ForRuntime id -> Some id
                | ForRestoredTarget -> None)
              |> List.distinct
              |> List.sort
            LibraryStems = stems })

  /// The facts for the assets files the daemon named: those that read, merged. A file that is missing or does not
  /// parse contributes nothing, and the failure is returned beside the facts so a caller can say it.
  let factsFrom
    (readText: string -> string)
    (directoryExists: string -> bool)
    (running: string)
    (os: string)
    (arch: string)
    (assetsFiles: string list)
    : NativeFacts * string list =
    let readings =
      assetsFiles
      |> List.map (fun file ->
        try
          match read directoryExists (readText file) with
          | PackagesRead packages -> PackagesRead packages
          | Unreadable why -> Unreadable(sprintf "%s: %s" file why)
        with ex -> Unreadable(sprintf "%s: %s" file ex.Message))
    let packages =
      readings
      |> List.collect (function
        | PackagesRead found -> found
        | Unreadable _ -> [])
      |> List.distinct
    let problems =
      readings
      |> List.choose (function
        | Unreadable why -> Some why
        | PackagesRead _ -> None)
    { Runtime = running; Compatible = compatibleRuntimes running os arch; Packages = packages }, problems

  /// This machine's facts for the assets files the daemon named, reading the real filesystem.
  let currentFacts (assetsFiles: string list) : NativeFacts * string list =
    let os =
      match RuntimeInformation.IsOSPlatform OSPlatform.Windows, RuntimeInformation.IsOSPlatform OSPlatform.OSX with
      | true, _ -> "win"
      | _, true -> "osx"
      | _ -> "linux"
    let arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
    factsFrom File.ReadAllText Directory.Exists RuntimeInformation.RuntimeIdentifier os arch assetsFiles

  /// Names the daemon passes the assets files in, separated by the platform's path separator.
  let splitFileList (value: string) : string list =
    match String.IsNullOrWhiteSpace value with
    | true -> []
    | false -> value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) |> Array.toList

/// Says what is wrong when a native library cannot be loaded.
[<RequireQualifiedAccess>]
module NativeDiagnosis =

  /// Whether a failure is a native library that would not load.
  type Explanation =
    | NotANativeLoadFailure
    | NativeLoadFailure of text: string

  let rec findNotFound (ex: exn) : DllNotFoundException list =
    match ex with
    | :? DllNotFoundException as missing -> [ missing ]
    | :? AggregateException as many -> many.InnerExceptions |> Seq.toList |> List.collect findNotFound
    | _ ->
      match ex.InnerException with
      | null -> []
      | inner -> findNotFound inner

  /// The library name a loader message quotes (`Unable to load shared library 'libX' or one of its dependencies`).
  let quotedLibrary (message: string) : string =
    match message.IndexOf '\'' with
    | -1 -> ""
    | start ->
      match message.IndexOf('\'', start + 1) with
      | -1 -> ""
      | stop -> message.Substring(start + 1, stop - start - 1)

  let describeGap (gap: NativeGap) : string =
    match gap.ShippedRuntimes with
    | [] -> sprintf "%s %s" gap.Name gap.Version
    | runtimes -> sprintf "%s %s (ships %s)" gap.Name gap.Version (String.Join(", ", runtimes))

  /// Said when a session starts, before any call can fail: this package ships native libraries but none for this machine.
  let gapWarning (facts: NativeFacts) (gap: NativeGap) : string =
    sprintf
      "Package %s %s ships native libraries for %s only, none for %s, so a call into it will fail to load its native library on this machine. Use a version or variant of the package that ships %s, or install the library on this machine."
      gap.Name gap.Version (String.Join(", ", gap.ShippedRuntimes)) facts.Runtime facts.Runtime

  /// What to tell the user about `failure`: which library, which runtime, which package ships it for other runtimes only,
  /// and what to do. `NotANativeLoadFailure` when no native library failed to load anywhere in the exception chain.
  let explain (facts: NativeFacts) (failure: exn) : Explanation =
    match findNotFound failure with
    | [] -> NotANativeLoadFailure
    | missing :: _ ->
      let library = quotedLibrary missing.Message
      let stem = NativeAssets.libraryStem library
      let gaps = NativeAssets.gaps facts.Compatible facts.Packages
      let named = gaps |> List.filter (fun g -> List.contains stem g.LibraryStems)
      let providers = NativeAssets.providers facts.Compatible stem facts.Packages
      let subject =
        match library with
        | "" -> "a native library"
        | name -> sprintf "the native library '%s'" name
      let advice =
        match providers, named, gaps with
        | provider :: _, _, _ ->
          sprintf
            "%s %s ships it for %s, so the loader found it and failed on something it depends on. Check what the library links to (on Linux, run ldd on it) and install what is missing."
            provider.Name provider.Version facts.Runtime
        | [], _ :: _, _ ->
          let owners =
            match named with
            | [ one ] -> sprintf "%s, which ships" (describeGap one)
            | many -> sprintf "%s, which ship" (String.Join(" and ", many |> List.map describeGap))
          sprintf
            "It comes from %s native libraries for other runtimes only, none for %s. Use a version or variant of the package that ships a %s native library, or install the library on this machine."
            owners facts.Runtime facts.Runtime
        | [], [], _ :: _ ->
          sprintf
            "These packages ship native libraries but none for %s: %s. If one of them is the owner, use a version or variant that ships %s, or install the library on this machine."
            facts.Runtime (String.Join("; ", gaps |> List.map describeGap)) facts.Runtime
        | [], [], [] ->
          sprintf
            "No package the project restored ships a native library for %s that matches it. If a package should provide it, check the package has a %s asset; otherwise install the library on this machine so the loader can find it."
            facts.Runtime facts.Runtime
      let heading = Char.ToUpperInvariant(subject.[0]).ToString() + subject.Substring 1
      NativeLoadFailure(sprintf "%s could not be loaded on %s. %s" heading facts.Runtime advice)

  /// `message` with the explanation after it when `failure` is a native load failure; `message` itself otherwise.
  let annotate (facts: NativeFacts) (failure: exn) (message: string) : string =
    match explain facts failure with
    | NotANativeLoadFailure -> message
    | NativeLoadFailure text -> sprintf "%s\n%s" message text
