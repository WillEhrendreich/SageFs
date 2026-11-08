namespace SageFs

open System
open System.Text

/// Why a snapshot could not be built or read.
type SnapshotError =
  /// A variable name that is empty or holds a character outside letters, digits and `_`.
  | IllegalName of name: string
  /// A text that is empty or spans lines, so it cannot be one `NAME=text` line.
  | IllegalText of name: string
  /// The argument a worker was started with is not base 64.
  | NotBase64 of reason: string
  /// A line of the argument with no `NAME=` in front of its text.
  | MalformedLine of lineNumber: int * line: string

/// The settings a process uses beyond its environment, resolved once when the daemon starts: what the Settings page
/// saved (where the person set no variable) and the machine tier the daemon worked out. It is a value. The daemon
/// hands it to each worker as one argument, so the daemon's own environment stays what the person set and a child
/// daemon inherits nothing it must strip.
///
/// The constructor is private so every snapshot holds names and texts that survive the `NAME=text` line format.
type SettingsSnapshot = private SettingsSnapshot of Map<string, string>

[<RequireQualifiedAccess>]
module SettingsSnapshot =

  let empty : SettingsSnapshot = SettingsSnapshot Map.empty

  let private nameIsLegal (name: string) : bool =
    name.Length > 0 && name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')

  let private textIsLegal (text: string) : bool =
    text.Length > 0 && not (text.Contains '\n') && not (text.Contains '\r')

  let private allOrFirstError (results: Result<'a, 'e> list) : Result<'a list, 'e> =
    results
    |> List.fold
      (fun acc next ->
        match acc, next with
        | Error e, _ -> Error e
        | Ok _, Error e -> Error e
        | Ok items, Ok item -> Ok (item :: items))
      (Ok [])
    |> Result.map List.rev

  let private checkPair (name: string, text: string) : Result<(string * string), SnapshotError> =
    match nameIsLegal name, textIsLegal text with
    | false, _ -> Error (IllegalName name)
    | _, false -> Error (IllegalText name)
    | true, true -> Ok (name, text)

  /// The snapshot of these variables and texts. A later pair for the same name replaces an earlier one.
  let ofPairs (pairs: (string * string) list) : Result<SettingsSnapshot, SnapshotError> =
    pairs
    |> List.map checkPair
    |> allOrFirstError
    |> Result.map (fun checkedPairs -> SettingsSnapshot (Map.ofList checkedPairs))

  let toPairs (SettingsSnapshot map: SettingsSnapshot) : (string * string) list = Map.toList map

  let tryFind (name: string) (SettingsSnapshot map: SettingsSnapshot) : string option = Map.tryFind name map

  let isEmpty (SettingsSnapshot map: SettingsSnapshot) : bool = Map.isEmpty map

  /// This snapshot with one more value, or the value replaced. The text must be a legal one.
  let add (name: string) (text: string) (snapshot: SettingsSnapshot) : Result<SettingsSnapshot, SnapshotError> =
    ofPairs (toPairs snapshot @ [ name, text ])

  /// The text a worker is handed: `NAME=text` lines, as base 64 so the command line needs no quoting. Empty for an
  /// empty snapshot.
  let encode (snapshot: SettingsSnapshot) : string =
    toPairs snapshot
    |> List.map (fun (name, text) -> name + "=" + text)
    |> String.concat "\n"
    |> Encoding.UTF8.GetBytes
    |> Convert.ToBase64String

  let private parseLine (index: int) (line: string) : Result<string * string, SnapshotError> =
    match line.IndexOf '=' with
    | at when at > 0 -> Ok (line.Substring(0, at), line.Substring(at + 1))
    | _ -> Error (MalformedLine(index + 1, line))

  /// The snapshot a worker's argument holds, or the first thing wrong with it.
  let decode (encoded: string) : Result<SettingsSnapshot, SnapshotError> =
    // Base 64 never decodes to more bytes than it has characters, so this buffer always fits.
    let buffer : byte[] = Array.zeroCreate encoded.Length
    let bytes =
      match Convert.TryFromBase64String(encoded, Span<byte>(buffer)) with
      | true, written -> Ok (Array.sub buffer 0 written)
      | false, _ -> Error (NotBase64 "the text is not valid base 64")
    bytes
    |> Result.bind (fun raw ->
      Encoding.UTF8.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries)
      |> Array.toList
      |> List.mapi parseLine
      |> allOrFirstError
      |> Result.bind ofPairs)

/// What happened when a process established the snapshot it runs on.
[<RequireQualifiedAccess>]
type Establishment =
  /// This is the process's snapshot now, and nothing read a setting before it.
  | Established
  /// This is the process's snapshot now, but something read a setting first, so a value that freezes on its
  /// first read (`Timeouts`) holds what the environment said. The caller logs it.
  | EstablishedAfterReads
  /// The process already runs on this snapshot.
  | AlreadyEstablished
  /// The process already runs on a different snapshot, which stays.
  | Conflicting of kept: SettingsSnapshot

/// How a setting's variable is read. The snapshot answers first and the environment second: a value the daemon
/// resolved already folded in what the person set (a person's variable beats a saved value when the snapshot is
/// built), and a worker has the daemon's snapshot, so every process answers the same.
[<RequireQualifiedAccess>]
module SettingsInForce =

  /// Pure: the text for `name` from the snapshot, else from `environment`. `null` when neither has text.
  let lookup (environment: string -> string | null) (snapshot: SettingsSnapshot) (name: string) : string | null =
    match SettingsSnapshot.tryFind name snapshot with
    | Some text -> text
    | None -> environment name

  type private Cell = { Snapshot: SettingsSnapshot option; ReadBefore: bool }

  let private gate = obj ()
  let mutable private cell : Cell = { Snapshot = None; ReadBefore = false }

  /// Make this the snapshot the process runs on. The first call decides. Call it before anything reads a setting.
  let establish (snapshot: SettingsSnapshot) : Establishment =
    lock gate (fun () ->
      match cell.Snapshot with
      | Some kept when kept = snapshot -> Establishment.AlreadyEstablished
      | Some kept -> Establishment.Conflicting kept
      | None ->
        cell <- { cell with Snapshot = Some snapshot }
        match cell.ReadBefore with
        | true -> Establishment.EstablishedAfterReads
        | false -> Establishment.Established)

  /// The snapshot the process runs on; empty before one is established.
  let current () : SettingsSnapshot =
    lock gate (fun () -> cell.Snapshot |> Option.defaultValue SettingsSnapshot.empty)

  /// The text of a setting's variable for this process: the snapshot, else the real environment. Use this in place
  /// of `Environment.GetEnvironmentVariable` for every variable a settings row names.
  let read (name: string) : string | null =
    let snapshot =
      lock gate (fun () ->
        match cell.Snapshot with
        | Some established -> established
        | None ->
          cell <- { cell with ReadBefore = true }
          SettingsSnapshot.empty)
    lookup Environment.GetEnvironmentVariable snapshot name

/// Why a worker's command line could not be read.
type WorkerCommandLineError =
  | MissingSessionId
  | MissingHttpPort
  | InvalidHttpPort of text: string
  | InvalidSettings of SnapshotError

/// What the FSI host is started with: `<sessionId> <httpPort> [<snapshot>]`. The port is OS-assigned (`0`), and the
/// snapshot is the daemon's, so the worker scales and limits its waits as the daemon does. The project list and the
/// session's flags still travel as environment variables (`Args.buildWorkerSpawnConfig`): they are the session's,
/// not settings.
type WorkerCommandLine =
  { SessionId: string
    HttpPort: int
    Settings: SettingsSnapshot }

[<RequireQualifiedAccess>]
module WorkerCommandLine =

  /// The argument text for a worker of this session. An empty snapshot adds no argument.
  let render (sessionId: string) (settings: SettingsSnapshot) : string =
    match SettingsSnapshot.isEmpty settings with
    | true -> sprintf "%s 0" sessionId
    | false -> sprintf "%s 0 %s" sessionId (SettingsSnapshot.encode settings)

  /// What a worker reads from its arguments.
  let parse (argv: string[]) : Result<WorkerCommandLine, WorkerCommandLineError> =
    match Array.tryItem 0 argv, Array.tryItem 1 argv with
    | None, _ -> Error MissingSessionId
    | Some _, None -> Error MissingHttpPort
    | Some sessionId, Some portText ->
      match Int32.TryParse portText with
      | false, _ -> Error (InvalidHttpPort portText)
      | true, port ->
        let settings =
          match Array.tryItem 2 argv with
          | None -> Ok SettingsSnapshot.empty
          | Some encoded -> SettingsSnapshot.decode encoded |> Result.mapError InvalidSettings
        settings |> Result.map (fun s -> { SessionId = sessionId; HttpPort = port; Settings = s })

/// Why the isolated FSI host's command line could not be read.
type FsiHostCommandLineError =
  /// Not `--args-file <path>` with an optional `--settings <snapshot>` after it.
  | NotAnArgsFileCommand
  | InvalidHostSettings of SnapshotError

/// What the isolated FSI host is started with: `--args-file <path> [--settings <snapshot>]`. The worker passes the
/// snapshot it runs on, so the host (where the user's own code runs) reads the same waits and switches.
type FsiHostCommandLine =
  { ArgsFile: string
    HostSettings: SettingsSnapshot }

[<RequireQualifiedAccess>]
module FsiHostCommandLine =

  [<Literal>]
  let ArgsFileFlag = "--args-file"

  [<Literal>]
  let SettingsFlag = "--settings"

  /// The arguments to start a host with, in order. An empty snapshot adds no flag.
  let render (argsFile: string) (settings: SettingsSnapshot) : string list =
    match SettingsSnapshot.isEmpty settings with
    | true -> [ ArgsFileFlag; argsFile ]
    | false -> [ ArgsFileFlag; argsFile; SettingsFlag; SettingsSnapshot.encode settings ]

  let parse (argv: string[]) : Result<FsiHostCommandLine, FsiHostCommandLineError> =
    match Array.toList argv with
    | [ ArgsFileFlag; argsFile ] -> Ok { ArgsFile = argsFile; HostSettings = SettingsSnapshot.empty }
    | [ ArgsFileFlag; argsFile; SettingsFlag; encoded ] ->
      SettingsSnapshot.decode encoded
      |> Result.mapError InvalidHostSettings
      |> Result.map (fun s -> { ArgsFile = argsFile; HostSettings = s })
    | _ -> Error NotAnArgsFileCommand
