/// The file system primitives workspace hygiene needs, in one place that depends on nothing but System: where a
/// path really is (every link followed, so the gate sees where a removal would land) and how big a tree is.
module SageFs.HygieneFs

open System
open System.IO
open SageFs.WorkspaceHygiene

/// Follow every symlink in a path. A path that does not exist resolves to itself; one that loops, or goes too
/// deep, is an error.
let resolvePath (path: string) : Result<string, ResolveFailure> =
  // The kernel's own bound on symlink chains is 40 on Linux; a path that needs more is looping.
  let maxLinks = 40
  let parts (p: string) = Guard.normalize p |> fun n -> n.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
  let rec walk (current: string) (remaining: string list) (links: int) : Result<string, ResolveFailure> =
    match remaining with
    | [] -> Result.Ok current
    | segment :: rest ->
      let next = (match current with | "/" -> "/" + segment | _ -> current + "/" + segment)
      let target =
        try
          match FileInfo(next).LinkTarget with
          | null -> None
          | t -> Some t
        with _ -> None
      match target with
      | None -> walk next rest links
      | Some t ->
        match links >= maxLinks with
        | true -> Result.Error(ResolveFailure.TooManyLinks path)
        | false ->
          let absolute = (match Path.IsPathRooted t with | true -> t | false -> current + "/" + t)
          walk "/" (parts absolute @ rest) (links + 1)
  try walk "/" (parts path) 0
  with ex -> Result.Error(ResolveFailure.Unreadable(path, ex.Message))

/// Total file size under a directory, links not followed, what cannot be read counted as nothing.
let directorySize (dir: string) : int64 =
  try
    let options = EnumerationOptions(RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint)
    DirectoryInfo(dir).EnumerateFiles("*", options) |> Seq.sumBy (fun f -> try f.Length with _ -> 0L)
  with _ -> 0L
