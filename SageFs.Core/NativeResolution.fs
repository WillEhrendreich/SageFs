namespace SageFs

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader

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
