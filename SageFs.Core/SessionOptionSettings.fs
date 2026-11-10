namespace SageFs

/// The three session options a person can set from the Settings page, as rows of the settings layers: the garbage collector, the
/// environment variables of the session's host and the folder its builds write to. Each is read when a session is created in a
/// directory (`NextSession`), from the repo layer of that directory over the global layer, and a `config.fsx` in the directory
/// that sets the same option wins (`SessionOptions.overlay`). A running session keeps the options it started with.
///
/// The parsers here are the only place a typed text becomes a value, and they use the same checks as `config.fsx`
/// (`SessionOptions`), so a refused entry says the same thing wherever it was written.
module SessionOptionSettings =

  [<Literal>]
  let GcKey = "session.gc"

  [<Literal>]
  let EnvironmentKey = "session.environment"

  [<Literal>]
  let ArtifactsKey = "session.artifactsPath"

  let private gcNames : string list = GcMode.all |> List.map GcMode.name

  let private gcValue (choice: GcMode) : SettingValue =
    VEnum(EnumValue.builtIn gcNames (GcMode.name choice))

  let private gcOf (value: SettingValue) : GcMode =
    match SettingValue.enum value |> Option.map EnumValue.value |> Option.map GcMode.parse with
    | Some(Result.Ok choice) -> choice
    | Some(Result.Error _)
    | None -> GcMode.standard

  let private refusedAs (refusals: SessionOptions.Refusal list) : ConfigError =
    Malformed(refusals |> List.map SessionOptions.describe |> String.concat " ")

  let gcSetting : SettingDescriptor =
    { Key = GcKey
      Name = "Garbage collector for new sessions"
      Description =
        GcMode.all
        |> List.map (fun choice -> sprintf "%s: %s" (GcMode.name choice) (GcMode.consequence choice))
        |> String.concat " "
        |> sprintf "Which garbage collector a new session's FSI host runs under, so code is timed under the collector it will run with. %s A config.fsx in the directory that sets Gc wins over this row."
      Category = SessionSettings
      Scope = RepoOverridable
      Applicability = NextSession
      Default = gcValue GcMode.standard
      Parse =
        fun raw ->
          match GcMode.parse raw with
          | Result.Ok choice -> Ok(gcValue choice)
          | Result.Error _ -> Error(NotAMember(raw, gcNames))
      Render = fun value -> GcMode.name (gcOf value)
      Apply = ignore
      External = NoExternalSource
      Current = InUseReadEachTime }

  let private namedValuesOf (entries: EnvEntry list) : SettingValue =
    VNamedValues(NamedValues(entries |> List.map (fun (EnvEntry(name, value)) -> name, value)))

  let private entriesOf (value: SettingValue) : EnvEntry list =
    match SettingValue.namedValues value with
    | Some(NamedValues pairs) -> pairs |> List.map EnvEntry
    | None -> []

  let environmentSetting : SettingDescriptor =
    { Key = EnvironmentKey
      Name = "Environment for new sessions"
      Description =
        "Environment variables for a new session's FSI host and nothing else, written NAME=value and separated by ';'. A name SageFs removes from every process it starts is refused, and a name SageFs sets itself keeps SageFs's value. A value that holds ';' goes in SessionEnv in config.fsx, and a config.fsx that sets SessionEnv wins over this row."
      Category = SessionSettings
      Scope = RepoOverridable
      Applicability = NextSession
      Default = namedValuesOf []
      Parse =
        fun raw ->
          match SessionOptions.parseEnvText raw with
          | Ok entries -> Ok(namedValuesOf entries)
          | Error refusals -> Error(refusedAs refusals)
      Render = fun value -> SessionOptions.renderEnvText (entriesOf value)
      Apply = ignore
      External = NoExternalSource
      Current = InUseReadEachTime }

  let private artifactsValue (artifacts: ArtifactsDir) : Result<SettingValue, ConfigError> =
    DirectoryPath.create (SessionOptions.renderArtifactsText artifacts) |> Result.map VDir

  let private artifactsOf (value: SettingValue) : ArtifactsDir =
    match SettingValue.dir value with
    | Some path when DirectoryPath.isSet path -> ArtifactsIn(DirectoryPath.value path)
    | Some _
    | None -> ProjectChooses

  let artifactsSetting : SettingDescriptor =
    { Key = ArtifactsKey
      Name = "Artifacts folder for new sessions"
      Description =
        "An absolute folder where a new session's own builds and its project load put bin and obj, so nothing is written under the project. Leave it empty to build where the project says. A project that sets ArtifactsPath in its own build files keeps its choice, and the session says so. A config.fsx in the directory that sets Artifacts wins over this row."
      Category = SessionSettings
      Scope = RepoOverridable
      Applicability = NextSession
      Default = VDir DirectoryPath.unset
      Parse =
        fun raw ->
          match SessionOptions.parseArtifactsText raw with
          | Ok artifacts -> artifactsValue artifacts
          | Error refusal -> Error(refusedAs [ refusal ])
      Render = fun value -> SessionOptions.renderArtifactsText (artifactsOf value)
      Apply = ignore
      External = NoExternalSource
      Current = InUseReadEachTime }

  /// The rows, in the order the page lists them.
  let all : SettingDescriptor list = [ gcSetting; environmentSetting; artifactsSetting ]

  /// The option a row sets.
  let optionOfKey (key: string) : SessionOptions.OptionName option =
    match key with
    | GcKey -> Some SessionOptions.GcOption
    | EnvironmentKey -> Some SessionOptions.EnvOption
    | ArtifactsKey -> Some SessionOptions.ArtifactsOption
    | _ -> None

  /// What a row says under its value about the last session started in the directory: that `config.fsx` set the option and so
  /// won over the saved value, and every refusal of this option on the way, each with its reason and next step. A directory with
  /// no session started yet has nothing to say, and neither does a row that is not one of these.
  let notesFor (resolution: SessionOptions.Resolution option) (key: string) : string list =
    match resolution, optionOfKey key with
    | Some resolution, Some option ->
      let fileNote =
        match SessionOptions.sourceOf resolution option with
        | SessionOptions.FromConfigFile ->
          [ "The last session started in this directory took this from config.fsx, which wins over the saved value. Change it there, or remove it from config.fsx to use this row." ]
        | SessionOptions.FromSettings
        | SessionOptions.FromNothing -> []
      fileNote @ (resolution.Refusals |> List.filter (fun refusal -> SessionOptions.optionOf refusal = option) |> List.map SessionOptions.describe)
    | Some _, None
    | None, _ -> []

  /// The options the settings layers at `paths` give a new session, and the options whose saved value could not be read (each
  /// falls back to its default and is named, so a damaged file never stops a session from starting).
  let optionsAt (paths: ConfigPaths) : SessionOptions.Options * SessionOptions.Refusal list =
    let read (option: SessionOptions.OptionName) (descriptor: SettingDescriptor) (ofValue: SettingValue -> 'a) : 'a * SessionOptions.Refusal list =
      match SettingsCatalog.resolve paths descriptor with
      | Ok provenance -> ofValue provenance.Effective, []
      | Error why -> ofValue descriptor.Default, [ SessionOptions.SettingUnreadable(option, ConfigError.describe why) ]
    let gc, gcRefusals = read SessionOptions.GcOption gcSetting gcOf
    let env, envRefusals = read SessionOptions.EnvOption environmentSetting entriesOf
    let artifacts, artifactsRefusals = read SessionOptions.ArtifactsOption artifactsSetting artifactsOf
    { SessionOptions.none with Gc = gc; Env = env; Artifacts = artifacts }, gcRefusals @ envRefusals @ artifactsRefusals
