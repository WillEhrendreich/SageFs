/// One `dotnet build` per hot-reload fixture per runtime, then copies.
///
/// About a hundred host cases each copied a fixture into a run dir of their own and built it, for 3 to 4 CPU seconds
/// apiece, and a fixture differs between cases only by runtime. The cache builds a fixture once per process, in a tree of
/// its own, and every case copies the outputs (`bin` and `obj`) into its run dir. The copy is for the host, which loads
/// the project from the run dir and reads the file times to tell an edited source from a built one, so the copies keep the
/// times the build left.
///
/// It is per process on purpose, and gone when the process is: a key made of everything a build reads can be trusted
/// within one run, and nothing here is trusted across runs. Any doubt is a real build in the case's own dir: a cached
/// build that failed (the case then sees its own build's failure), a cached tree that no longer matches what the build
/// wrote, a copy that is short a file.
module SageFs.Tests.FixtureBuildCache

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Everything that decides the bytes a fixture build produces. Two builds with equal inputs are the same build.
type BuildInputs =
  { /// Every file of the run dir the build reads: (name relative to the run dir, SHA-256 of its content in hex).
    Tree: (string * string) list
    /// The repo files whose settings reach every fixture build (central package versions, build props, the SDK pin):
    /// (name, digest). A fixture lives under the repo on purpose, so these apply to it.
    Shared: (string * string) list
    /// The SDK that builds it, as `dotnet --version` says.
    Sdk: string
    /// The build command line, the project named by its file name so the run dir's own path stays out of the key.
    Command: string list }

/// Where a run dir's build came from.
[<RequireQualifiedAccess>]
type Served =
  /// Copied out of the cache's tree.
  | FromCache
  /// Built by the case itself, in its own run dir.
  | BuiltInPlace

/// What a build writes into a run dir.
let outputDirectories = [ "bin"; "obj" ]

/// What a case writes into its run dir apart from the build: not part of what a build reads.
let configuredDirectory = ".SageFs"

let hex (bytes: byte[]) : string = Convert.ToHexString(bytes).ToLowerInvariant()

let digestOf (path: string) : string = hex (SHA256.HashData(File.ReadAllBytes path))

/// The key of a build: a SHA-256 over every input, each name and value length-prefixed so a name and a digest that run
/// together cannot be read as another pair. The order files are listed in does not matter.
let keyOf (inputs: BuildInputs) : string =
  let part (kind: string) (name: string) (value: string) =
    sprintf "%s\n%d:%s\n%d:%s\n" kind name.Length name value.Length value
  let ordered (pairs: (string * string) list) = pairs |> List.sortBy (fun (name, digest) -> name, digest)
  [ yield! ordered inputs.Tree |> List.map (fun (name, digest) -> part "tree" name digest)
    yield! ordered inputs.Shared |> List.map (fun (name, digest) -> part "shared" name digest)
    yield part "sdk" "" inputs.Sdk
    yield! inputs.Command |> List.mapi (fun index argument -> part "argument" (string index) argument) ]
  |> String.concat ""
  |> Encoding.UTF8.GetBytes
  |> SHA256.HashData
  |> hex

let relativeName (root: string) (file: string) : string =
  Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')

let firstSegment (relative: string) : string = relative.Split('/').[0]

/// Every file of a run dir a build reads, with its digest: all of them but the build's own outputs and what the case
/// configures. Sorted by name.
let treeOf (runDir: string) : (string * string) list =
  Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories)
  |> Seq.map (fun file -> relativeName runDir file, file)
  |> Seq.filter (fun (relative, _) ->
    let top = firstSegment relative
    not (List.contains top outputDirectories) && top <> configuredDirectory)
  |> Seq.sortBy fst
  |> Seq.map (fun (relative, file) -> relative, digestOf file)
  |> List.ofSeq

/// The repo files whose settings reach a fixture build, with their digests. A file the repo does not have is left out.
let repoInputs (repoRoot: string) : (string * string) list =
  [ "Directory.Build.props"; "Directory.Build.targets"; "Directory.Packages.props"; "nuget.config"; "global.json" ]
  |> List.choose (fun name ->
    let path = Path.Combine(repoRoot, name)
    match File.Exists path with
    | true -> Some (name, digestOf path)
    | false -> None)

/// The outputs of a tree, each with its length: what a copy has to come out as.
let manifestOf (dir: string) : (string * int64) list =
  outputDirectories
  |> List.collect (fun output ->
    let path = Path.Combine(dir, output)
    match Directory.Exists path with
    | false -> []
    | true ->
      Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
      |> Seq.map (fun file -> relativeName dir file, FileInfo(file).Length)
      |> List.ofSeq)
  |> List.sortBy fst

let clearOutputs (dir: string) : unit =
  for output in outputDirectories do
    let path = Path.Combine(dir, output)
    match Directory.Exists path with
    | true -> Directory.Delete(path, true)
    | false -> ()

/// Copy one file with the last-write time it has. The host decides a source "was edited after the build" from these.
let copyFile (source: string) (target: string) : unit =
  Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
  File.Copy(source, target, true)
  File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc source)

let copyFiles (sourceDir: string) (targetDir: string) (include': string -> bool) : unit =
  for file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories) do
    let relative = relativeName sourceDir file
    match include' relative with
    | true -> copyFile file (Path.Combine(targetDir, relative))
    | false -> ()

/// A build in a tree of its own, and what it wrote.
type CachedTree = { Dir: string; Manifest: (string * int64) list }

type Cache =
  { Root: string
    Trees: ConcurrentDictionary<string, Lazy<Task<Result<CachedTree, string>>>> }

module Cache =
  /// A cache whose trees go under `root`. The root has to be somewhere the repo's own build settings apply (under the
  /// repo): a fixture built under /tmp would resolve its packages differently.
  let create (root: string) : Cache =
    Directory.CreateDirectory root |> ignore
    { Root = root; Trees = ConcurrentDictionary<string, Lazy<Task<Result<CachedTree, string>>>>() }

  /// Take every tree away. The root stays.
  let clear (cache: Cache) : unit =
    cache.Trees.Clear()
    for tree in Directory.GetDirectories cache.Root do
      try Directory.Delete(tree, true) with _ -> ()

/// The tree for `key`: the run dir's sources, copied with their times, then built by `buildIn`.
let buildTree (cache: Cache) (key: string) (runDir: string) (buildIn: string -> Task<Result<unit, string>>) : Task<Result<CachedTree, string>> = task {
  let dir = Path.Combine(cache.Root, key)
  Directory.CreateDirectory dir |> ignore
  copyFiles runDir dir (fun relative ->
    let top = firstSegment relative
    not (List.contains top outputDirectories) && top <> configuredDirectory)
  match! buildIn dir with
  | Ok () -> return Ok { Dir = dir; Manifest = manifestOf dir }
  | Error why -> return Error why
}

/// Copy a tree's outputs into `runDir` and check it came out the way it was built.
let copyOutputs (tree: CachedTree) (runDir: string) : Result<unit, string> =
  clearOutputs runDir
  copyFiles tree.Dir runDir (fun relative -> List.contains (firstSegment relative) outputDirectories)
  match manifestOf runDir = tree.Manifest with
  | true -> Ok ()
  | false -> Error "the copied outputs are not the ones the build wrote"

/// Make `runDir` hold a build of the project in it: copied from the cache's tree for `inputs` (built there the first
/// time anyone asks, once however many ask at once), or built by `buildIn` in `runDir` itself when the cached build
/// failed or its tree no longer matches what it wrote.
let provide (cache: Cache) (inputs: BuildInputs) (runDir: string) (buildIn: string -> Task<Result<unit, string>>) : Task<Result<Served, string>> = task {
  let key = keyOf inputs
  let entry = cache.Trees.GetOrAdd(key, fun _ -> lazy (buildTree cache key runDir buildIn))
  let! built = entry.Value
  let inPlace () = task {
    clearOutputs runDir
    match! buildIn runDir with
    | Ok () -> return Ok Served.BuiltInPlace
    | Error why -> return Error why
  }
  match built with
  | Error _ -> return! inPlace ()
  | Ok tree ->
    match copyOutputs tree runDir with
    | Ok () -> return Ok Served.FromCache
    | Error _ -> return! inPlace ()
}

let shared =ConcurrentDictionary<string, Lazy<Cache>>()

/// The one cache of this process for `root`, whose trees go when the process does.
let sharedFor (root: string) : Cache =
  let entry =
    shared.GetOrAdd(root, fun r ->
      lazy
        (let cache = Cache.create r
         AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
           Cache.clear cache
           try Directory.Delete(r, true) with _ -> ())
         cache))
  entry.Value
