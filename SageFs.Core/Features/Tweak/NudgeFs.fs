/// The real disk behind the nudge door's `FileSteps`: System.IO, with fsync where
/// the protocol needs a step to be durable when it returns. Every step turns an
/// IO exception into a `FileFault` value, so the protocol above it never has to
/// guess whether a step threw or failed.
module SageFs.Features.Tweak.NudgeFs

open System
open System.IO
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.NudgeIo

let faultOf (operation: FileOperation) (path: string) (ex: exn) : FileFault =
  { Operation = operation; Path = path; Reason = ex.Message }

/// What is at `path`, without following a link: a symbolic link is named as one, even a dangling one.
let kindOf (path: string) : FileKind =
  try
    let info = FileInfo path
    match info.LinkTarget with
    | null ->
      match info.Exists, Directory.Exists path with
      | true, _ -> FileKind.RegularFile
      | false, true -> FileKind.NotAFile
      | false, false -> FileKind.Missing
    | _ -> FileKind.SymbolicLink
  with _ -> FileKind.NotAFile

/// The temp file keeps the permissions of the file it will replace. It is created readable by its owner
/// only, so its content is never exposed more widely than the file it replaces, and given the real
/// file's mode once it is written.
let writeTemp (temp: string) (bytes: byte[]) : Result<unit, FileFault> =
  try
    let target = if temp.EndsWith(AtomicWrite.tempSuffix, StringComparison.Ordinal) then temp.Substring(0, temp.Length - AtomicWrite.tempSuffix.Length) else temp
    let options = FileStreamOptions(Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None)
    match OperatingSystem.IsWindows() with
    | true -> ()
    | false -> options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    use stream = new FileStream(temp, options)
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush true
    match OperatingSystem.IsWindows(), File.Exists target with
    | false, true -> File.SetUnixFileMode(temp, File.GetUnixFileMode target)
    | _ -> ()
    Ok()
  with ex -> Error(faultOf FileOperation.WritingTemp temp ex)

let append (path: string) (bytes: byte[]) : Result<unit, FileFault> =
  try
    use stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush true
    Ok()
  with ex -> Error(faultOf FileOperation.Appending path ex)

/// The production steps.
let steps : FileSteps =
  { ReadBytes = fun path -> try Ok(File.ReadAllBytes path) with ex -> Error(faultOf FileOperation.Reading path ex)
    KindOf = kindOf
    MakeDirectory = fun path -> try (Directory.CreateDirectory path |> ignore; Ok()) with ex -> Error(faultOf FileOperation.WritingTemp path ex)
    WriteTemp = writeTemp
    Rename = fun source target -> try (File.Move(source, target, true); Ok()) with ex -> Error(faultOf FileOperation.Renaming source ex)
    Append = append
    Discard = fun path -> try File.Delete path with _ -> () }

/// Where the journal for `file` in `session` lives under `tweaksDir`: one journal per file per session,
/// named by a hash of the file's path. The session id is cut down to letters, digits and dashes, so it
/// can never name a path outside the tweaks directory.
let journalPathFor (tweaksDir: string) (session: string) (file: string) : string =
  let folder = session |> String.map (fun c -> if Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' then c else '_')
  Path.Combine(tweaksDir, folder, (contentHash file).Substring(0, 16) + ".events")
