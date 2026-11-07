namespace SageFs

open System

/// What the start-up import did with one saved value.
type ImportOutcome =
  /// The saved value is now the variable's value in this process, and every worker it starts inherits it.
  | Imported of variable: string * text: string
  /// The variable was already set by the person, so the saved value is left in the file and does not apply.
  | LeftToEnvironment of variable: string
  /// The saved value says to leave the variable unset (the machine tier's `auto`).
  | SavedMeansUnset of variable: string
  /// The layer file or the saved value could not be read, so the variable is left as it was. The daemon logs why.
  | NotUsable of variable: string * reason: string

/// The first thing the executable does after it knows its data directory, before anything reads `Timeouts`:
/// put what the Settings page saved into the process environment, so a saved wait is the wait in force after a
/// restart. `Timeouts` freezes each value the first time it is read, and every worker inherits the environment,
/// so the daemon and its workers agree without more plumbing.
///
/// A variable the person set always wins over a saved value, the order `SageFsConfig.chooseMcpPort` already uses.
/// A variable this import set is a saved value and not a person's override, so `processInputs` hides it from the
/// environment layer and the row reads "global", not "environment".
[<RequireQualifiedAccess>]
module SettingsBootstrap =

  /// The import, driven by injected reads and writes so a test never touches the process environment.
  let importSavedWith
    (readEnv: string -> EnvRead)
    (setEnv: string -> string -> unit)
    (globalDir: string)
    (settings: EnvBacked list)
    : ImportOutcome list =
    match SettingsStore.readLayerChecked (SettingsStore.globalPath globalDir) with
    | SettingsStore.LayerRead.Unreadable reason ->
      // One unreadable file is one report, so the log is not repeated per setting.
      [ NotUsable(SettingsStore.FileName, reason) ]
    | SettingsStore.LayerRead.Layer saved ->
      settings
      |> List.choose (fun setting ->
        let d = setting.Descriptor
        match Map.tryFind d.Key saved with
        | None -> None
        | Some raw ->
          match readEnv setting.Variable with
          | EnvSet _ -> Some (LeftToEnvironment setting.Variable)
          | EnvUnset ->
            match d.Parse raw with
            | Error why -> Some (NotUsable(setting.Variable, sprintf "%s: %s" d.Key (ConfigError.describe why)))
            | Ok value ->
              match setting.EnvText value with
              | None -> Some (SavedMeansUnset setting.Variable)
              | Some text ->
                setEnv setting.Variable text
                Some (Imported(setting.Variable, text)))

  let private importedNames = Collections.Generic.HashSet<string>()
  let mutable private lastOutcomes : ImportOutcome list = []

  /// The import against the real process environment. Call it before anything reads `Timeouts`. It does not
  /// touch `Timeouts`, `SageFsConfig` or `SettingsCatalog`, whose files would read them on load.
  let importSaved (globalDir: string) : ImportOutcome list =
    let outcomes =
      importSavedWith
        (ExternalInputs.ofEnvironment Environment.GetEnvironmentVariable)
        (fun name text -> Environment.SetEnvironmentVariable(name, text))
        globalDir
        EnvBackedRows.all
    lock importedNames (fun () ->
      for outcome in outcomes do
        match outcome with
        | Imported(variable, _) -> importedNames.Add variable |> ignore
        | LeftToEnvironment _
        | SavedMeansUnset _
        | NotUsable _ -> ()
      lastOutcomes <- outcomes)
    outcomes

  /// What the last import did, for the daemon's log.
  let lastImport () : ImportOutcome list = lock importedNames (fun () -> lastOutcomes)

  /// Whether this process's variable is a person's. One this import set is a saved value; the machine tier the
  /// daemon works out for itself and publishes is not a person's either, unless they forced one.
  let isPersonsVariable (name: string) : bool =
    let imported = lock importedNames (fun () -> importedNames.Contains name)
    let derivedTier =
      name = MachineTier.envVar
      && (match MachineStartup.current () with
          | MachineStartup.Established.Done { Override = OverrideUse.NotSet } -> true
          | MachineStartup.Established.Done _
          | MachineStartup.Established.NotYet -> false)
    not imported && not derivedTier

  /// The environment of a daemon this process starts. This process imported saved values into its own environment; the
  /// daemon it starts imports them again from the same file, and must not mistake the copies for a person's variables,
  /// which would make a saved value read "environment". A variable the person set is kept.
  let withoutImported (environment: Collections.Generic.IDictionary<string, string | null>) : unit =
    let names = lock importedNames (fun () -> importedNames |> Seq.toList)
    for name in names do
      environment.Remove name |> ignore

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
    lock importedNames (fun () -> launched <- Some { McpPort = mcpPort; Flags = flagsOf args })

  let launch () : Launch option = lock importedNames (fun () -> launched)

  /// The inputs the page resolves against in the running daemon: the real environment minus the names that are not a
  /// person's, and the flags the daemon was started with.
  let processInputs () : ExternalInputs =
    let read = ExternalInputs.ofEnvironment Environment.GetEnvironmentVariable
    { Environment =
        (fun name ->
          match isPersonsVariable name with
          | true -> read name
          | false -> EnvUnset)
      Flags = launch () |> Option.map (fun l -> l.Flags) |> Option.defaultValue Map.empty }
