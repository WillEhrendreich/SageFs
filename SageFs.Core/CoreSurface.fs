/// The public shape of a SageFs.Core build, as a program compiled against it sees it.
///
/// The worker the daemon spawns is the daemon's own SageFs.Host.dll, compiled against the daemon's own SageFs.Core. A
/// session project that builds its own SageFs.Core (a worktree of this repository) can have a Core with the same
/// assembly version and a different shape, because the version moves on a release push and not on a commit. Paired
/// with the daemon's host, that Core breaks it in ways the loader does not report: a union whose cases moved has new
/// tags, and the host's `match` on the old tags casts a message to the wrong case, so every route of the worker
/// answered 500 and its log said nothing. The shape decides whether the two can be paired.
namespace SageFs

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Security.Cryptography
open System.Text

module CoreSurface =

  /// One digest per exported type, keyed by the type's full name. A nested type has an entry of its own, and its
  /// parent's digest carries the order of the parent's nested types: that order is the tag of an F# union case.
  type Surface = Map<string, string>

  /// What a comparison of two surfaces found, the first difference in name order.
  [<RequireQualifiedAccess>]
  type SurfaceChange =
    | Unchanged
    | Changed of typeName: string
    | Added of typeName: string
    | Removed of typeName: string

  /// A surface read from a file, or why it could not be read.
  [<RequireQualifiedAccess>]
  type SurfaceReading =
    | Read of Surface
    | Unreadable of reason: string

  let publicMembers =
    BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly

  let describeMember (member_: MemberInfo) : string =
    match member_ with
    | :? FieldInfo as field when field.IsLiteral ->
      sprintf "const %s = %O" (field.ToString()) (field.GetRawConstantValue())
    | _ -> sprintf "%O %s" member_.MemberType (member_.ToString())

  /// What a program compiled against `type_` depends on: its kind, base, interfaces, the order of its nested types and
  /// the signature of each public member in declaration order.
  let digestOf (type_: Type) : string =
    let text = StringBuilder()
    let add (line: string) = text.Append(line).Append('\n') |> ignore
    add (string type_.Attributes)
    add (match type_.BaseType with | null -> "" | baseType -> baseType.ToString())
    type_.GetInterfaces() |> Array.map string |> Array.sort |> Array.iter (fun name -> add ("interface " + name))
    type_.GetNestedTypes BindingFlags.Public
    |> Array.sortBy (fun nested -> nested.MetadataToken)
    |> Array.iter (fun nested -> add ("nested " + nested.Name))
    type_.GetMembers publicMembers
    |> Array.filter (fun member_ -> member_.MemberType <> MemberTypes.NestedType)
    |> Array.sortBy (fun member_ -> member_.MetadataToken)
    |> Array.iter (fun member_ -> add (describeMember member_))
    SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())) |> Convert.ToHexString

  /// The surface of a loaded assembly. A type the compiler names with a leading `<` is left out: an F# anonymous record
  /// is a public class named by a hash of its shape, and one more of them inside the Core is not a change a program
  /// compiled against the Core can see.
  let surfaceOf (assembly: Assembly) : Surface =
    assembly.GetExportedTypes()
    |> Array.filter (fun type_ -> not (type_.Name.StartsWith("<", StringComparison.Ordinal)))
    |> Array.map (fun type_ -> type_.FullName, digestOf type_)
    |> Map.ofArray

  /// The surface of the assembly at `path`, read in a context of its own that is unloaded again. What the assembly
  /// references is looked for where this process has it, then next to the file. A file that cannot be read, or whose
  /// public types cannot be loaded, is an `Error` carrying why.
  let surfaceOfFile (path: string) : SurfaceReading =
    let fullPath = Path.GetFullPath path
    let unreadable (ex: exn) = SurfaceReading.Unreadable(sprintf "%s: %s" (ex.GetType().Name) ex.Message)
    let context = new AssemblyLoadContext("sagefs-core-surface", true)
    context.add_Resolving(
      Func<AssemblyLoadContext, AssemblyName, Assembly>(fun loading name ->
        let neighbour = Path.Combine(Path.GetDirectoryName fullPath, name.Name + ".dll")
        match File.Exists neighbour with
        | true -> loading.LoadFromAssemblyPath neighbour
        | false -> null))
    try
      try
        SurfaceReading.Read(surfaceOf (context.LoadFromAssemblyPath fullPath))
      with
      | :? IOException as ex -> unreadable ex
      | :? BadImageFormatException as ex -> unreadable ex
      | :? TypeLoadException as ex -> unreadable ex
      | :? ReflectionTypeLoadException as ex -> unreadable ex
      | :? UnauthorizedAccessException as ex -> unreadable ex
    finally
      context.Unload()

  /// What `surfaceOfFileCached` has read, by the file's path, write time and size: a read takes about two seconds on a
  /// loaded machine, and the daemon's own host copy is read for every session it spawns.
  let readCache =
    System.Collections.Concurrent.ConcurrentDictionary<string * int64 * int64, SurfaceReading>()

  /// How many readings the cache keeps (stub: it keeps all).
  let cacheCapacity = 8

  /// How many readings the cache holds now.
  let cachedCount () : int = readCache.Count

  /// `surfaceOfFile`, remembered for as long as the file is the same file.
  let surfaceOfFileCached (path: string) : SurfaceReading =
    let info = FileInfo(Path.GetFullPath path)
    match info.Exists with
    | false -> surfaceOfFile path
    | true -> readCache.GetOrAdd((info.FullName, info.LastWriteTimeUtc.Ticks, info.Length), fun _ -> surfaceOfFile path)

  /// The first difference between two surfaces, in the order of the type names, or `Unchanged`.
  let compareSurfaces (before: Surface) (after: Surface) : SurfaceChange =
    Seq.append (Map.keys before) (Map.keys after)
    |> Seq.distinct
    |> Seq.sort
    |> Seq.tryPick (fun name ->
      match Map.tryFind name before, Map.tryFind name after with
      | Some was, Some now when was = now -> None
      | Some _, Some _ -> Some(SurfaceChange.Changed name)
      | None, Some _ -> Some(SurfaceChange.Added name)
      | Some _, None -> Some(SurfaceChange.Removed name)
      | None, None -> None)
    |> Option.defaultValue SurfaceChange.Unchanged

  /// The change in words, for a log line or a refusal.
  let describeChange (change: SurfaceChange) : string =
    match change with
    | SurfaceChange.Unchanged -> "no difference"
    | SurfaceChange.Changed name -> sprintf "%s has a different public shape" name
    | SurfaceChange.Added name -> sprintf "%s exists only in the project's build" name
    | SurfaceChange.Removed name -> sprintf "%s exists only in the daemon's build" name
