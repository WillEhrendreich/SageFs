namespace SageFs

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

/// Whether a project's compiled assembly is one a hot-reload patch can be trusted
/// to reach. SageFs builds sessions with `-p:Optimize=false`, which stamps the
/// assembly's `DebuggableAttribute` with DisableOptimizations. An assembly built
/// without it (a Release build the user made by hand, and SageFs found up to date)
/// lets the JIT inline small functions into their callers, so re-pointing the
/// callee changes nothing the caller runs, and a value the compiler copied in has
/// no read left to see.
[<RequireQualifiedAccess>]
type BuildOptimization =
  /// The JIT optimizer is disabled for this assembly: a patched function is called.
  | Unoptimized
  /// The JIT may have inlined callees: a patch can be bypassed and nothing will say so.
  | Optimized
  /// Could not be read, so nothing is claimed either way. Says why.
  | Unknown of why: string

module BuildOptimization =
  /// `DebuggableAttribute.DebuggingModes.DisableOptimizations`.
  let private disableOptimizations = 0x100

  /// What an assembly's `DebuggableAttribute` says. Three different facts, so three
  /// cases: a missing attribute is a finding (the compiler's default), not an error.
  [<RequireQualifiedAccess>]
  type private Stamp =
    | NoAttribute
    | Modes of debuggingModes: int
    | Unreadable of why: string

  /// The DebuggingModes the assembly's `DebuggableAttribute` was built with, read from
  /// the metadata tables without loading the assembly.
  let private stampOf (md: MetadataReader) : Stamp =
    let debuggable =
      md.GetAssemblyDefinition().GetCustomAttributes()
      |> Seq.map md.GetCustomAttribute
      |> Seq.tryFind (fun attr ->
        match attr.Constructor.Kind with
        | HandleKind.MemberReference ->
          let parent = md.GetMemberReference(MemberReferenceHandle.op_Explicit attr.Constructor).Parent
          (match parent.Kind with
           | HandleKind.TypeReference ->
             md.GetString(md.GetTypeReference(TypeReferenceHandle.op_Explicit parent).Name) = "DebuggableAttribute"
           | _ -> false)
        | _ -> false)
    match debuggable with
    | None -> Stamp.NoAttribute
    | Some attr ->
      let blob = md.GetBlobBytes attr.Value
      match blob.Length with
      // (DebuggingModes): prolog, int32, no named arguments.
      | 8 -> Stamp.Modes (BitConverter.ToInt32(blob, 2))
      // (isJITTrackingEnabled, isJITOptimizerDisabled): prolog, bool, bool, no named arguments.
      | 6 -> Stamp.Modes (if blob.[3] = 1uy then disableOptimizations else 0)
      | n -> Stamp.Unreadable (sprintf "its DebuggableAttribute blob is %d bytes, which is neither of the two known shapes" n)

  /// Classify the assembly at `path`. Never throws: anything unreadable is `Unknown`
  /// with the reason, and a missing file says so rather than being called optimized.
  let ofAssemblyFile (path: string) : BuildOptimization =
    match File.Exists path with
    | false -> BuildOptimization.Unknown (sprintf "there is no build output at %s" path)
    | true ->
      try
        use stream = File.OpenRead path
        use pe = new PEReader(stream)
        match pe.HasMetadata with
        | false -> BuildOptimization.Unknown (sprintf "%s has no .NET metadata" path)
        | true ->
          match stampOf (pe.GetMetadataReader()) with
          | Stamp.Unreadable why -> BuildOptimization.Unknown why
          // No DebuggableAttribute at all is the compiler's default, which is optimized.
          | Stamp.NoAttribute -> BuildOptimization.Optimized
          | Stamp.Modes modes when modes &&& disableOptimizations <> 0 -> BuildOptimization.Unoptimized
          | Stamp.Modes _ -> BuildOptimization.Optimized
      with ex -> BuildOptimization.Unknown (sprintf "could not read %s: %s" path ex.Message)

  /// The words for a project whose build is optimized, with what to do about it.
  /// The one place this sentence is written, so status, the dashboard and the
  /// health verdict cannot drift apart. `None` for the two cases that need no warning.
  let warning (projectName: string) (build: BuildOptimization) : string option =
    match build with
    | BuildOptimization.Optimized ->
      Some (
        sprintf
          "%s was built with optimizations, so a hot-reload patch to one of its functions can be bypassed by inlining and a live value it holds can go untracked. Run hard_reset_fsi_session with rebuild=true: SageFs builds sessions without optimizations."
          projectName)
    | BuildOptimization.Unoptimized -> None
    // Unknown is not a warning: a project with no build output yet has nothing to be
    // wrong about, and its own "nothing loaded" verdict already says to build it.
    | BuildOptimization.Unknown _ -> None
