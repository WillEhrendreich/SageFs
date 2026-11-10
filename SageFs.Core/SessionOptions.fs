namespace SageFs

open System
open System.IO

/// What a directory's `.SageFs/config.fsx` asks of the session that starts in it, checked and ready to hand to the
/// FSI host: the garbage collector it runs under, extra compiler arguments and environment variables. Pure. The edge (`IsolatedFsiSession.start`) reads the result.
module SessionOptions =

  /// Why one entry of the config was not honored. The rest of the config still is.
  type Refusal =
    | EnvNameEmpty
    | EnvNameInvalid of name: string
    | EnvNamePoisoned of name: string
    /// Text in an environment setting that is not `NAME=value`.
    | EnvEntryNotAssignment of text: string
    | ArtifactsPathEmpty
    | ArtifactsPathNotAbsolute of path: string
    | ArtifactsPathUnderProc of path: string
    /// The session asked for an artifacts path and the project's own build files already set one, which stays.
    | ArtifactsClaimedByProject of asked: string * projectSets: string
    /// A saved setting that could not be read back, so the default stands for that option.
    | SettingUnreadable of option: OptionName * reason: string

  /// The three options a person can set for a session, named once so a refusal, a source and a row all say which one they mean.
  and OptionName =
    | GcOption
    | EnvOption
    | ArtifactsOption

  /// The option a refusal is about.
  let optionOf (refusal: Refusal) : OptionName =
    match refusal with
    | EnvNameEmpty
    | EnvNameInvalid _
    | EnvNamePoisoned _
    | EnvEntryNotAssignment _ -> EnvOption
    | ArtifactsPathEmpty
    | ArtifactsPathNotAbsolute _
    | ArtifactsPathUnderProc _
    | ArtifactsClaimedByProject _ -> ArtifactsOption
    | SettingUnreadable(option, _) -> option

  let describe (refusal: Refusal) : string =
    match refusal with
    | EnvEntryNotAssignment text ->
      sprintf "'%s' is not written as NAME=value, so it was dropped. → Write each entry as NAME=value and separate entries with ';'. A value that holds ';' goes in SessionEnv in config.fsx." text
    | ArtifactsClaimedByProject(asked, projectSets) ->
      sprintf "The session asked for artifacts path %s, and this project's own build files already set ArtifactsPath to %s, so the project's choice stays and the session's was ignored. The project's files win because they are shared with your team and CI, and the build and the project load then read the same folder. → Remove ArtifactsPath from the project's props to use the session's, or remove the Artifacts setting." asked projectSets
    | SettingUnreadable(option, reason) ->
      sprintf "The saved %s setting could not be read (%s), so the default was used. → Save a new value from the Settings page, or fix the settings file." (match option with GcOption -> "garbage collector" | EnvOption -> "environment" | ArtifactsOption -> "artifacts path") reason
    | EnvNameEmpty ->
      "A session environment entry has an empty name, so it was dropped. → Give the variable a name in SessionEnv."
    | EnvNameInvalid name ->
      sprintf "'%s' cannot be an environment variable name (it holds '=' or a NUL), so it was dropped. → Rename it in SessionEnv." name
    | EnvNamePoisoned name ->
      sprintf "%s is one of the variables SageFs removes from every process it starts, because it makes `dotnet` load the wrong SDK, so it was dropped. → Remove it from SessionEnv." name
    | ArtifactsPathEmpty -> "Artifacts names an empty path, so it was ignored. → Give an absolute directory, or remove the setting."
    | ArtifactsPathNotAbsolute path ->
      sprintf "Artifacts path '%s' is not absolute, so it was ignored. → Give the whole path, for example /home/me/artifacts/app." path
    | ArtifactsPathUnderProc path -> sprintf "Artifacts path %s is under /proc, which nothing can build into, so it was ignored. → Pick a directory on disk." path

  /// A user variable that has the same name as one SageFs sets. SageFs's value is the one the host gets.
  type Clash =
    { Name: string
      UserValue: string
      SageFsValue: string }

  let describeClash (clash: Clash) : string =
    sprintf "SessionEnv sets %s=%s, and SageFs sets it to %s for the host, so SageFs's value is used." clash.Name clash.UserValue clash.SageFsValue

  type Options =
    { Gc: GcMode
      /// The user's variables that passed the checks, in the order written, the last of a repeated name kept.
      Env: EnvEntry list
      /// Where the session's builds write `bin` and `obj`; absolute, or the project's own choice.
      Artifacts: ArtifactsDir
      /// Extra arguments for the FSI host, after the ones SageFs builds.
      FsiArgs: string list }

  /// What a directory with no config gets. The same as before any option existed.
  let none : Options =
    { Gc = WorkstationGc
      Env = []
      Artifacts = ProjectChooses
      FsiArgs = [] }

  let private invalidNameChars = [| '='; '\000' |]

  let private checkEnvName (name: string) : Result<string, Refusal> =
    match String.IsNullOrWhiteSpace name with
    | true -> Error EnvNameEmpty
    | false ->
      match name.IndexOfAny invalidNameChars >= 0 with
      | true -> Error(EnvNameInvalid name)
      | false ->
        match ProcessEnvironment.poisonedVariables |> List.exists (fun p -> String.Equals(p, name, StringComparison.Ordinal)) with
        | true -> Error(EnvNamePoisoned name)
        | false -> Ok name

  let private checkArtifacts (path: string) : Result<string, Refusal> =
    match String.IsNullOrWhiteSpace path with
    | true -> Error ArtifactsPathEmpty
    | false ->
      let trimmed = path.Trim()
      match Path.IsPathRooted trimmed with
      | false -> Error(ArtifactsPathNotAbsolute path)
      | true ->
        match trimmed = "/proc" || trimmed.StartsWith("/proc/", StringComparison.Ordinal) with
        | true -> Error(ArtifactsPathUnderProc path)
        | false -> Ok trimmed

  /// The options a directory's config asks for, and the entries that were refused (said, never silent).
  let ofConfig (config: DirectoryConfig) : Options * Refusal list =
    let checkedEnv = config.SessionEnv |> List.map (fun (EnvEntry(name, value)) -> checkEnvName name |> Result.map (fun n -> n, value))
    let refusedEnv = checkedEnv |> List.choose (function Error r -> Some r | Ok _ -> None)
    let keptEnv =
      checkedEnv
      |> List.choose (function Ok entry -> Some entry | Error _ -> None)
      |> List.rev
      |> List.distinctBy fst
      |> List.rev
      |> List.map EnvEntry
    let artifacts, refusedArtifacts =
      match config.Artifacts with
      | ProjectChooses -> ProjectChooses, []
      | ArtifactsIn path ->
        match checkArtifacts path with
        | Ok kept -> ArtifactsIn kept, []
        | Error refusal -> ProjectChooses, [ refusal ]
    { Gc = config.Gc
      Env = keptEnv
      Artifacts = artifacts
      FsiArgs = config.DefaultArgs },
    refusedEnv @ refusedArtifacts

  /// Environment entries as the settings file and the page write them: `NAME=value`, separated by `;`. Every entry is checked
  /// by the same rules as `SessionEnv` in config.fsx, and a text with any bad entry is refused whole, naming each, so a typo
  /// is not saved as something shorter than what was typed.
  let parseEnvText (text: string) : Result<EnvEntry list, Refusal list> =
    let pieces =
      (match text with null -> "" | t -> t).Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
      |> Array.toList
    let checkedPieces =
      pieces
      |> List.map (fun piece ->
        match piece.IndexOf '=' with
        | -1 -> Error(EnvEntryNotAssignment piece)
        | at ->
          let name = piece.Substring(0, at).Trim()
          checkEnvName name |> Result.map (fun kept -> EnvEntry(kept, piece.Substring(at + 1))))
    match checkedPieces |> List.choose (function Error refusal -> Some refusal | Ok _ -> None) with
    | [] -> Ok(checkedPieces |> List.choose (function Ok entry -> Some entry | Error _ -> None))
    | refusals -> Error refusals

  /// The text `parseEnvText` reads back to the same entries.
  let renderEnvText (entries: EnvEntry list) : string =
    entries |> List.map (fun (EnvEntry(name, value)) -> name + "=" + value) |> String.concat ";"

  /// An artifacts path as the settings file and the page write it: empty leaves the choice to the project.
  let parseArtifactsText (text: string) : Result<ArtifactsDir, Refusal> =
    match String.IsNullOrWhiteSpace text with
    | true -> Ok ProjectChooses
    | false -> checkArtifacts text |> Result.map ArtifactsIn

  let renderArtifactsText (artifacts: ArtifactsDir) : string =
    match artifacts with
    | ProjectChooses -> ""
    | ArtifactsIn path -> path

  /// What a project's own build files say about where its output goes, found by asking MSBuild with nothing of the session's
  /// in the way.
  type ProjectClaim =
    | ProjectLeavesArtifacts
    | ProjectSetsArtifacts of path: string

  /// A project that already sets `ArtifactsPath` keeps it. The session's request is dropped with the reason, because the build
  /// and the project load must read the same folder and the project's files are the ones shared with the team and CI. An
  /// equal path is no conflict.
  let reconcileArtifacts (claim: ProjectClaim) (options: Options) : Options * Refusal list =
    match options.Artifacts, claim with
    | ArtifactsIn asked, ProjectSetsArtifacts theirs when not (String.Equals(Path.TrimEndingDirectorySeparator asked, Path.TrimEndingDirectorySeparator theirs, StringComparison.Ordinal)) ->
      { options with Artifacts = ProjectChooses }, [ ArtifactsClaimedByProject(asked, theirs) ]
    | ArtifactsIn _, ProjectSetsArtifacts _
    | ArtifactsIn _, ProjectLeavesArtifacts
    | ProjectChooses, _ -> options, []

  /// Where one option's value came from.
  type OptionSource =
    | FromConfigFile
    | FromSettings
    | FromNothing

  /// The options a session will run with, with where each came from and what was refused on the way.
  type Resolution =
    { Options: Options
      GcFrom: OptionSource
      EnvFrom: OptionSource
      ArtifactsFrom: OptionSource
      Refusals: Refusal list }

  let sourceOf (resolution: Resolution) (option: OptionName) : OptionSource =
    match option with
    | GcOption -> resolution.GcFrom
    | EnvOption -> resolution.EnvFrom
    | ArtifactsOption -> resolution.ArtifactsFrom

  /// Options that came from no file and no setting.
  let unresolved : Resolution =
    { Options = none; GcFrom = FromNothing; EnvFrom = FromNothing; ArtifactsFrom = FromNothing; Refusals = [] }

  /// The options of a directory: what `config.fsx` sets wins, option by option, over what the settings layers hold. The file is
  /// the more specific of the two, written by hand beside the project. A file option left at its default says nothing, so the
  /// settings value stands; that is also why `config.fsx` cannot ask for the default over a saved Server choice.
  let overlay (settings: Options) (file: Options) (refusals: Refusal list) : Resolution =
    let pick (fromFile: 'a) (fromSettings: 'a) (nothing: 'a) : 'a * OptionSource =
      match fromFile <> nothing, fromSettings <> nothing with
      | true, _ -> fromFile, FromConfigFile
      | false, true -> fromSettings, FromSettings
      | false, false -> nothing, FromNothing
    let gc, gcFrom = pick file.Gc settings.Gc none.Gc
    let env, envFrom = pick file.Env settings.Env none.Env
    let artifacts, artifactsFrom = pick file.Artifacts settings.Artifacts none.Artifacts
    { Options = { Gc = gc; Env = env; Artifacts = artifacts; FsiArgs = file.FsiArgs }
      GcFrom = gcFrom
      EnvFrom = envFrom
      ArtifactsFrom = artifactsFrom
      Refusals = refusals }

  /// The environment variables the choice of garbage collector needs. SageFs owns these names.
  let gcEnvironment (gc: GcMode) : (string * string) list =
    match gc with
    | ServerGc -> [ "DOTNET_gcServer", "1" ]
    | WorkstationGc -> []

  /// The one variable the options travel to a worker in. A session's options are resolved once, when it is created, and this
  /// is the only way a worker, the builds it starts and its project loader learn them: they read this and nothing else, so they
  /// cannot disagree with each other or with the daemon.
  let carrierVariable : string = "SAGEFS_SESSION_OPTIONS"

  let encode (options: Options) : string = Json.serialize Json.standard options

  let decode (text: string) : Result<Options, JsonError> = Json.deserialize<Options> Json.standard text

  /// The environment a worker is started with for these options: the carrier, or nothing when the options ask for nothing.
  let workerEnvironment (options: Options) : (string * string) list =
    match options = none with
    | true -> []
    | false -> [ carrierVariable, encode options ]

  /// The options a process was started with. No carrier is no options; a carrier that cannot be read is an error that says why,
  /// not read as no options.
  let fromEnvironment (lookup: string -> string) : Result<Options, JsonError> =
    match lookup carrierVariable with
    | null
    | "" -> Ok none
    | text -> decode text

  /// The MSBuild properties the options set, for a build's environment and the project loader's global properties.
  let buildProperties (options: Options) : (string * string) list =
    match options.Artifacts with
    | ArtifactsIn path -> [ "ArtifactsPath", path ]
    | ProjectChooses -> []

  /// A value as MSBuild's `-p:` switch reads it: `%`, `;` and `,` are separators or escapes there.
  let escapeSwitchValue (value: string) : string =
    value.Replace("%", "%25").Replace(";", "%3B").Replace(",", "%2C")

  /// The properties as `-p:` arguments for `dotnet build`. A global property overrides what the project's own files set, which
  /// an environment variable does not, so the build agrees with the project load (which passes the same properties as global
  /// ones). A project that sets the property itself is handled before this: `reconcileArtifacts` drops the session's request.
  let buildArguments (options: Options) : string list =
    buildProperties options |> List.map (fun (name, value) -> sprintf "-p:%s=%s" name (escapeSwitchValue value))

  /// `buildProperties` of the carrier in a process's environment; none when there is no carrier or it cannot be read (the worker
  /// reports an unreadable one when it starts, in `SessionOptionsSource`).
  let propertiesFromEnvironment (lookup: string -> string) : (string * string) list =
    match fromEnvironment lookup with
    | Ok options -> buildProperties options
    | Error _ -> []

  /// `buildArguments` of the carrier in a process's environment, for the builds a worker starts.
  let argumentsFromEnvironment (lookup: string -> string) : string list =
    match fromEnvironment lookup with
    | Ok options -> buildArguments options
    | Error _ -> []

  /// The host's environment: what SageFs built (`sageFsEnv`), then the collector's, then the user's, where a
  /// name SageFs sets keeps SageFs's value and is reported as a clash.
  let hostEnvironment (sageFsEnv: (string * string) list) (options: Options) : (string * string) list * Clash list =
    let owned = sageFsEnv @ gcEnvironment options.Gc
    let userEnv = options.Env |> List.map (fun (EnvEntry(name, value)) -> name, value)
    let ownedValue name = owned |> List.tryFindBack (fun (n, _) -> String.Equals(n, name, StringComparison.Ordinal)) |> Option.map snd
    let clashes =
      userEnv
      |> List.choose (fun (name, value) ->
        match ownedValue name with
        | Some sageFsValue -> Some { Name = name; UserValue = value; SageFsValue = sageFsValue }
        | None -> None)
    let userOnly = userEnv |> List.filter (fun (name, _) -> (ownedValue name).IsNone)
    owned @ userOnly, clashes

  /// Who reads a field of the config. Every field of `DirectoryConfig` has a row, and a test fails when one does not.
  type Reader =
    /// The dashboard's create-session flow (the one client that consults `Load`).
    | DashboardCreate
    /// The warmup that opens namespaces, read when any client creates a session.
    | Warmup
    /// The live-bindings pane.
    | LiveValuePane
    /// `SessionOptions.ofConfig`: the FSI host's arguments, environment and builds.
    | SessionHost

  let fieldReaders : (string * Reader) list =
    [ "Load", DashboardCreate
      "AutoOpenNamespaces", Warmup
      "ValueWalk", LiveValuePane
      "DefaultArgs", SessionHost
      "Gc", SessionHost
      "SessionEnv", SessionHost
      "Artifacts", SessionHost ]
