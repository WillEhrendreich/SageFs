namespace SageFs

open System

/// What the start-up resolution did with one saved value. Always written with its type name: `Applied` and
/// `NotUsable` are words other unions in the product use.
[<RequireQualifiedAccess>]
type ImportOutcome =
  /// The saved value is in the snapshot, so this process and every worker it starts use it.
  | InSnapshot of variable: string * text: string
  /// The variable was already set by the person, so the saved value is left in the file and does not apply.
  | LeftToEnvironment of variable: string
  /// The saved value says to leave the variable unset (the machine tier's `auto`).
  | SavedMeansUnset of variable: string
  /// The layer file or the saved value could not be read, so the variable is left as it was. The daemon logs why.
  | NotUsable of variable: string * reason: string
  /// The key belongs to a retired setting (`RetiredSettings`): the value stays in the file and nothing reads it. The note
  /// says what decides that wait now.
  | IgnoredRetired of key: string * note: string
  /// The key is in a family only env-backed rows own, and no row or retired setting has it: it stays in the file and
  /// nothing reads it.
  | IgnoredUnknown of key: string

/// What the saved settings come to: the snapshot to run on, and what became of each saved value.
type SavedSettings =
  { Snapshot: SettingsSnapshot
    Outcomes: ImportOutcome list }

/// The first thing the executable does after it knows its data directory, before anything reads `Timeouts`: work out
/// which of the values the Settings page saved apply, and keep them in a snapshot (`SettingsInForce`), so a saved wait
/// is the wait in force after a restart. `Timeouts` freezes each value the first time it is read, so the snapshot has
/// to be established before that. Workers get the same snapshot as an argument. Nothing is written to the
/// environment, so a daemon this process starts inherits only what a person set and works its own snapshot out.
///
/// A variable the person set always wins over a saved value, the order `SageFsConfig.chooseMcpPort` already uses:
/// the saved value is left out of the snapshot and the row reads "environment".
[<RequireQualifiedAccess>]
module SettingsBootstrap =

  /// The resolution, driven by an injected environment read so a test never touches the process environment.
  let resolveSavedWith
    (readEnv: string -> EnvRead)
    (globalDir: string)
    (settings: EnvBacked list)
    : SavedSettings =
    match SettingsStore.readLayerChecked (SettingsStore.globalPath globalDir) with
    | SettingsStore.LayerRead.Unreadable reason ->
      // One unreadable file is one report, so the log is not repeated per setting.
      { Snapshot = SettingsSnapshot.empty
        Outcomes = [ ImportOutcome.NotUsable(SettingsStore.FileName, reason) ] }
    | SettingsStore.LayerRead.Layer saved ->
      let step (snapshot: SettingsSnapshot, outcomes: ImportOutcome list) (setting: EnvBacked) =
        let d = setting.Descriptor
        match Map.tryFind d.Key saved with
        | None -> snapshot, outcomes
        | Some raw ->
          match readEnv setting.Variable with
          | EnvSet _ -> snapshot, ImportOutcome.LeftToEnvironment setting.Variable :: outcomes
          | EnvUnset ->
            match d.Parse raw with
            | Error why ->
              snapshot, ImportOutcome.NotUsable(setting.Variable, sprintf "%s: %s" d.Key (ConfigError.describe why)) :: outcomes
            | Ok value ->
              match setting.EnvText value with
              | None -> snapshot, ImportOutcome.SavedMeansUnset setting.Variable :: outcomes
              | Some text ->
                match SettingsSnapshot.add setting.Variable text snapshot with
                | Ok next -> next, ImportOutcome.InSnapshot(setting.Variable, text) :: outcomes
                | Error why ->
                  snapshot, ImportOutcome.NotUsable(setting.Variable, sprintf "%s: the text %A cannot be carried (%A)" d.Key text why) :: outcomes
      let snapshot, outcomes = settings |> List.fold step (SettingsSnapshot.empty, [])
      // A key no row reads: retired ones say what decides the wait now, and an unknown one in a family these rows own
      // is named. Nothing is removed from the file, so downgrading never loses a value.
      let rowKeys = settings |> List.map (fun s -> s.Descriptor.Key) |> Set.ofList
      let ignored =
        saved
        |> Map.toList
        |> List.map fst
        |> List.filter (fun key -> not (Set.contains key rowKeys))
        |> List.choose (fun key ->
          match RetiredSettings.all |> List.tryFind (fun r -> r.Key = key) with
          | Some retired -> Some (ImportOutcome.IgnoredRetired(key, RetiredSettings.describe retired))
          | None ->
            match RetiredSettings.ownsKey key with
            | true -> Some (ImportOutcome.IgnoredUnknown key)
            | false -> None)
      { Snapshot = snapshot; Outcomes = List.rev outcomes @ ignored }

  let private gate = obj ()
  let mutable private lastOutcomes : ImportOutcome list = []

  /// The resolution against the real process environment. Call it before anything reads `Timeouts`. It does not
  /// touch `Timeouts`, `SageFsConfig` or `SettingsCatalog`, whose files would read them on load.
  let resolveSaved (globalDir: string) : SavedSettings =
    let resolved =
      resolveSavedWith
        (ExternalInputs.ofEnvironment Environment.GetEnvironmentVariable)
        globalDir
        EnvBackedRows.all
    lock gate (fun () -> lastOutcomes <- resolved.Outcomes)
    resolved

  /// What the last resolution did, for the daemon's log.
  let lastImport () : ImportOutcome list = lock gate (fun () -> lastOutcomes)

  /// The flags that set a setting, and the name the page shows for each. A switch maps to `on`.
  [<Literal>]
  let McpPortFlag = "--mcp-port"
  [<Literal>]
  let NoResumeFlag = "--no-resume"

  /// The flags a daemon was started with, for the settings a flag can set. Anything else on the line is not a setting.
  let flagsOf (args: string list) : Map<string, string> =
    let rec loop (acc: Map<string, string>) (rest: string list) =
      match rest with
      | McpPortFlag :: value :: tail -> loop (Map.add McpPortFlag value acc) tail
      | NoResumeFlag :: tail -> loop (Map.add NoResumeFlag "on" acc) tail
      | _ :: tail -> loop acc tail
      | [] -> acc
    loop Map.empty args

  /// What the running daemon is: the port it bound and the flags it was started with. Recorded once by the daemon so
  /// the page can show what is in use, and read from nowhere else.
  type Launch = { McpPort: int; Flags: Map<string, string> }

  let mutable private launched : Launch option = None

  let recordLaunch (mcpPort: int) (args: string list) : unit =
    lock gate (fun () -> launched <- Some { McpPort = mcpPort; Flags = flagsOf args })

  let launch () : Launch option = lock gate (fun () -> launched)

  /// The inputs the page resolves against in the running daemon: the real environment, which holds only what a
  /// person set, and the flags the daemon was started with.
  let processInputs () : ExternalInputs =
    { Environment = ExternalInputs.ofEnvironment Environment.GetEnvironmentVariable
      Flags = launch () |> Option.map (fun l -> l.Flags) |> Option.defaultValue Map.empty }
