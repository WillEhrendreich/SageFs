/// The builds a lemming runs, kept ONCE and read-only, instead of copied into every run.
///
/// A run used to copy the whole dev build of SageFs (hundreds of MB) into its own directory so a
/// rebuild could not change a trial that was running. /tmp is tmpfs, so that was RAM, and the
/// copies were never removed. Now each distinct build is copied once into the store (off tmpfs, by
/// default ~/.local/share/sagefs-lemmings), keyed by what it is, made read-only, and mounted into
/// every sandbox that needs it. A rebuild makes a new key, so a running trial still cannot change.
module LemRun.Store

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open LemRun.Failure

/// What is kept in the store. Each kind has its own directory of `<key>` directories.
type Kind =
  | Bridge
  | Drive

module Kind =
  let toString (k: Kind) : string =
    match k with
    | Bridge -> "bridge"
    | Drive -> "drive"

/// How long a build that nobody has used is kept, and how many of the newest are always kept.
let unusedAfter = TimeSpan.FromDays 7.0
let alwaysKeep = 3

let private completeMarker = ".lem-complete"
let private lockPoll = TimeSpan.FromMilliseconds 200.0
let private lockPatience = TimeSpan.FromMinutes 15.0

let kindDir (root: string) (kind: Kind) : string = Path.Combine(root, Kind.toString kind)
let entryDir (root: string) (kind: Kind) (key: string) : string = Path.Combine(kindDir root kind, key)

/// A key is one path segment of plain characters, so it can never name anything outside the store.
let sanitize (text: string) : string =
  text
  |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '.' || c = '-' || c = '_' then c else '-')
  |> Seq.toArray
  |> String
  |> fun s -> s.Trim('-')

/// SHA-256 of the files' relative paths and bytes, in path order: the same sources always give the
/// same key, and any edit gives a new one. Hex, first `width` characters.
let hashFiles (root: string) (files: string list) (width: int) : string =
  use sha = IncrementalHash.CreateHash HashAlgorithmName.SHA256
  files
  |> List.sort
  |> List.iter (fun f ->
    sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, f) + "\n"))
    sha.AppendData(File.ReadAllBytes f))
  Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant().Substring(0, width)

let hashFile (file: string) (width: int) : string =
  use stream = File.OpenRead file
  Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant().Substring(0, width)

/// Removes a tree that may have been made read-only.
let removeTree (path: string) : unit =
  match Directory.Exists path with
  | false -> ()
  | true ->
    Proc.run (Proc.spec "chmod" [ "-R"; "u+w"; path ]) None |> ignore
    Directory.Delete(path, true)

/// Takes an exclusive file lock for the length of `work`. Another process doing the same waits, so
/// two runs that start together build or copy a thing once, not twice.
let withLock (lockFile: string) (work: unit -> 'a) : 'a =
  Directory.CreateDirectory(Path.GetDirectoryName lockFile |> Option.ofObj |> Option.defaultValue ".") |> ignore
  let deadline = DateTime.UtcNow + lockPatience
  let rec acquire () =
    try new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
    with :? IOException when DateTime.UtcNow < deadline ->
      Thread.Sleep lockPoll
      acquire ()
  use _held = acquire ()
  work ()

let private isComplete (dir: string) : bool = File.Exists(Path.Combine(dir, completeMarker))

/// Notes that the entry was just used, so it is not collected as unused.
let private touch (dir: string) : unit =
  try File.SetLastWriteTimeUtc(Path.Combine(dir, completeMarker), DateTime.UtcNow) with _ -> ()

/// The entry for `key`, made by `make` into a fresh directory the first time and read-only after.
/// `make` gets the directory to fill and returns Error to refuse (nothing is kept then).
let ensure (root: string) (kind: Kind) (key: string) (make: string -> Result<unit, string>) : Result<string, string> =
  let target = entryDir root kind key
  match isComplete target with
  | true ->
    touch target
    Ok target
  | false ->
    withLock (Path.Combine(root, ".lock")) (fun () ->
      match isComplete target with
      | true ->
        touch target
        Ok target
      | false ->
        let building = target + ".building"
        removeTree building
        removeTree target
        Directory.CreateDirectory building |> ignore
        match make building with
        | Error e ->
          removeTree building
          Error e
        | Ok () ->
          File.WriteAllText(Path.Combine(building, completeMarker), DateTime.UtcNow.ToString "o")
          // Read-only on purpose: a trial must not be able to change what the next one runs.
          Proc.run (Proc.spec "chmod" [ "-R"; "a-w"; building ]) None |> ignore
          Directory.Move(building, target)
          Ok target)

/// Entries to remove: not among the newest `alwaysKeep`, and not used for `unusedAfter`. Pure.
let collectable (now: DateTime) (entries: (string * DateTime) list) : string list =
  entries
  |> List.sortByDescending snd
  |> List.skip (min alwaysKeep entries.Length)
  |> List.filter (fun (_, used) -> now - used >= unusedAfter)
  |> List.map fst

/// Removes builds nobody has used for a while. A build in use is touched on every run, so only
/// one that has gone unused is ever removed.
let collect (root: string) (kind: Kind) : string list =
  let dir = kindDir root kind
  match Directory.Exists dir with
  | false -> []
  | true ->
    let entries =
      Directory.GetDirectories dir
      |> Array.filter (fun d -> isComplete d)
      |> Array.map (fun d -> d, File.GetLastWriteTimeUtc(Path.Combine(d, completeMarker)))
      |> Array.toList
    let gone = collectable DateTime.UtcNow entries
    gone |> List.iter removeTree
    gone

/// Copies a directory tree (reflink where the filesystem has it) into `destination`.
let copyTree (source: string) (destination: string) : Result<unit, string> =
  Directory.CreateDirectory destination |> ignore
  let code, output = Proc.runCombined "cp" [ "-a"; "--reflink=auto"; source + "/."; destination ]
  match code with
  | 0 -> Ok ()
  | _ -> Error (sprintf "copying %s failed: %s" source output)

/// Builds a .NET project into the store, once per distinct set of sources.
/// `sources` are the files the key is made from (the project's own, and what it includes).
let ensureBuild (root: string) (kind: Kind) (project: string) (sources: string list) (buildName: string) : Result<string, string> =
  let key = hashFiles (Path.GetDirectoryName project |> Option.ofObj |> Option.defaultValue ".") sources 16
  ensure root kind (buildName + "-" + key) (fun dir ->
    say (sprintf "building %s" buildName)
    let build = Proc.run (Proc.spec "dotnet" [ "build"; project; "-c"; "Release"; "-nologo"; "-v"; "quiet"; "-o"; dir ]) None
    match build.ExitCode with
    | 0 -> Ok ()
    | _ -> Error (sprintf "%s failed to build:\n%s%s" buildName build.Stdout build.Stderr))
