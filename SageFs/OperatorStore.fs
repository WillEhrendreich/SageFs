namespace SageFs

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open SageFs.EdgeCredential
open SageFs.OperatorFile

/// The shell around `OperatorFile`: `operator.token` in the data directory, made on first need and read back on
/// every later start. The decisions are in SageFs.Core/OperatorFile.fs; this module does the file work for each
/// effect it asks for. Only the credential's SHA-256 is held in memory by the daemon. The person's own tools read
/// the file (`sagefs member mint`), and so does anything else running as the same user, which the docs say.
module OperatorStore =

  /// The credential file's name inside the data directory.
  [<Literal>]
  let FileName = "operator.token"

  let pathIn (dataDir: string) : string = Path.Combine(dataDir, FileName)

  let ownerOnlyUnixMode : UnixFileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

  let octalOf (mode: UnixFileMode) : string = Convert.ToString(int mode, 8)

  /// What a file's mode says about who can read it. On Windows the file is given an ACL for the current user alone
  /// when it is made, and that is not read back here: nothing in this repo can run that case, so it is unverified.
  let modeOf (path: string) : FileMode =
    match OperatingSystem.IsWindows() with
    | true -> FileMode.OwnerOnly
    | false ->
      match File.GetUnixFileMode path with
      | mode when mode = ownerOnlyUnixMode -> FileMode.OwnerOnly
      | mode -> FileMode.TooWide(octalOf mode)

  /// Windows: only the current user, nothing inherited.
  let restrictToCurrentUser (path: string) : unit =
    match OperatingSystem.IsWindows() with
    | false -> ()
    | true ->
      let info = FileInfo path
      let security = System.IO.FileSystemAclExtensions.GetAccessControl info
      security.SetAccessRuleProtection(true, false)
      let user = System.Security.Principal.WindowsIdentity.GetCurrent().User
      security.AddAccessRule(
        System.Security.AccessControl.FileSystemAccessRule(user, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow))
      System.IO.FileSystemAclExtensions.SetAccessControl(info, security)

  /// Created with owner-only mode in the same call that creates it, so there is no moment it is wider.
  let writeOwnerOnly (FilePath path) (text: string) : unit =
    let options = FileStreamOptions(Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None)
    match OperatingSystem.IsWindows() with
    | true -> ()
    | false -> options.UnixCreateMode <- ownerOnlyUnixMode
    use stream = new FileStream(path, options)
    use writer = new StreamWriter(stream)
    writer.Write text
    writer.Flush()
    stream.Flush true
    restrictToCurrentUser path

  /// link(2): a new name for a file, failing if the name is taken. The one atomic "create only if absent" that
  /// publishes a whole file. File.Move without overwrite is not it: measured on this runtime, eight threads moving
  /// to one target had several succeed in the same run, each replacing the last winner.
  module Native =
    [<DllImport("libc", EntryPoint = "link", SetLastError = true)>]
    extern int unixLink(string existing, string created)

  /// Move `temp` to `target` only if `target` does not exist, atomically. On Published `temp` is gone; on any other
  /// answer it is still there, and the machine discards it.
  let publishExclusive (FilePath temp) (FilePath target) : Answer =
    match OperatingSystem.IsWindows() with
    | true ->
      // MoveFileEx without MOVEFILE_REPLACE_EXISTING fails when the target exists, atomically.
      try
        File.Move(temp, target, false)
        Answer.Published
      with :? IOException when File.Exists target -> Answer.TargetAlreadyExists
    | false ->
      match Native.unixLink(temp, target) with
      | 0 ->
        File.Delete temp
        Answer.Published
      | _ when File.Exists target -> Answer.TargetAlreadyExists
      | _ -> Answer.Failed "link(2) refused to publish operator.token and nothing is at that name"

  /// The files one run of the machine uses, named so two daemons never share a temporary file.
  type World(dataDir: string) =
    let target = pathIn dataDir
    let temp = Path.Combine(dataDir, sprintf "%s.%d.%s.tmp" FileName Environment.ProcessId (Guid.NewGuid().ToString "N"))

    member _.Target : string = target

    member _.Run(effect: Effect) : Answer =
      try
        match effect with
        | Effect.ReadTarget ->
          match File.Exists target with
          | false -> Answer.Observed Observation.Missing
          | true -> Answer.Observed(Observation.Present(File.ReadAllText target, modeOf target))
        | Effect.WriteTemp credential ->
          Directory.CreateDirectory dataDir |> ignore
          writeOwnerOnly (FilePath temp) credential
          Answer.TempWritten
        | Effect.ReadTempMode -> Answer.TempModeIs(modeOf temp)
        | Effect.PublishExclusive -> publishExclusive (FilePath temp) (FilePath target)
        | Effect.DiscardTemp ->
          File.Delete temp
          Answer.Discarded
      with
      | :? IOException as ex -> Answer.Failed ex.Message
      | :? UnauthorizedAccessException as ex -> Answer.Failed ex.Message
      | :? NotSupportedException as ex -> Answer.Failed ex.Message

  /// Run the machine to its end against the real files: the credential's hash, or the refusal and so the fix.
  let load (dataDir: string) : Result<OperatorHash, Refusal> =
    let world = World dataDir
    let rec drive (state: State, effect: Effect option) =
      match state, effect with
      | State.Finished result, _ -> result
      | _, None -> Error(Refusal.Io "the machine stopped without finishing")
      | _, Some pending -> drive (next (RandomNumberGenerator.GetBytes OperatorCredential.entropyBytes) state (world.Run pending))
    drive start

  /// What the daemon holds after it tried to make the credential: its hash, or why it has none. A daemon with none
  /// still answers the routes that need it, with the fix, and every other route is unaffected.
  [<RequireQualifiedAccess>]
  type OperatorAccess =
    | Ready of OperatorHash
    | Unavailable of Refusal

  let accessOf (result: Result<OperatorHash, Refusal>) : OperatorAccess =
    match result with
    | Ok hash -> OperatorAccess.Ready hash
    | Error refusal -> OperatorAccess.Unavailable refusal

  /// Why the person's own tools cannot get the credential.
  [<RequireQualifiedAccess>]
  type ReadRefusal =
    | NoFile of path: string
    | NotACredential of path: string
    | Unreadable of path: string * reason: string

  module ReadRefusal =
    let describe (refusal: ReadRefusal) : string =
      match refusal with
      | ReadRefusal.NoFile path -> sprintf "There is no operator credential at %s. Start the daemon once, with the same SAGEFS_DATA_DIR, and it makes one." path
      | ReadRefusal.NotACredential path -> sprintf "%s does not hold an operator credential. Delete it and restart the daemon to make a new one." path
      | ReadRefusal.Unreadable(path, reason) -> sprintf "%s could not be read: %s" path reason

  /// The credential text, for a launcher or a CLI verb that runs as the person who owns the data directory.
  let readCredential (dataDir: string) : Result<string, ReadRefusal> =
    let path = pathIn dataDir
    try
      match File.Exists path with
      | false -> Error(ReadRefusal.NoFile path)
      | true ->
        let credential = (File.ReadAllText path).Trim()
        match OperatorCredential.isWellFormed credential with
        | true -> Ok credential
        | false -> Error(ReadRefusal.NotACredential path)
    with
    | :? IOException as ex -> Error(ReadRefusal.Unreadable(path, ex.Message))
    | :? UnauthorizedAccessException as ex -> Error(ReadRefusal.Unreadable(path, ex.Message))
