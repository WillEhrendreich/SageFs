/// The switch for the metadata-delta route.
///
/// The daemon reads it once when it starts a worker. A worker started for the route gets the runtime
/// variable that lets it edit its own assemblies (`DOTNET_MODIFIABLE_ASSEMBLIES=debug`, which only means
/// anything at process start) and `SAGEFS_METADATA_DELTA` spelled out, so the worker never guesses what
/// its parent decided.
namespace SageFs.Features.MetadataDelta

/// Whether patches may be taken as metadata deltas.
[<RequireQualifiedAccess>]
type MetadataDeltaMode =
  | Off
  | On

[<RequireQualifiedAccess>]
module MetadataDeltaMode =

  /// The environment variable that sets it.
  let environmentVariable = "SAGEFS_METADATA_DELTA"

  /// The runtime's own variable, which a process has to be started with to be able to take a delta.
  let modifiableAssembliesVariable = "DOTNET_MODIFIABLE_ASSEMBLIES"

  /// What it is set to for the route.
  let modifiableAssembliesValue = "debug"

  /// What a process gets when nothing says otherwise. On, because the whole host tier and the browser journeys pass with the
  /// route on, a save is patched in about a third of the time a restart took and keeps the app's state, and being editable
  /// costs a call-heavy loop nothing measurable on a build SageFs already makes unoptimized (docs/decisions.md has the
  /// numbers). `SAGEFS_METADATA_DELTA=off` is the way back.
  let defaultMode : MetadataDeltaMode = MetadataDeltaMode.On

  /// The spellings below turn it on or off. Anything else, an empty value included, is the default.
  let parse (value: string | null) : MetadataDeltaMode =
    match value with
    | null -> defaultMode
    | text ->
      match text.Trim().ToLowerInvariant() with
      | "1"
      | "on"
      | "true" -> MetadataDeltaMode.On
      | "0"
      | "off"
      | "false" -> MetadataDeltaMode.Off
      | _ -> defaultMode

  let fromEnvironment () : MetadataDeltaMode =
    parse (System.Environment.GetEnvironmentVariable environmentVariable)

  let toText (mode: MetadataDeltaMode) : string =
    match mode with
    | MetadataDeltaMode.Off -> "off"
    | MetadataDeltaMode.On -> "on"

  /// The variables a worker is started with for this mode. Nothing for a worker that is not running hot reload:
  /// the route is a hot-reload route, and the runtime's variable has a cost, so only the processes that use it carry it.
  let workerEnvironment (hotReloadActive: bool) (mode: MetadataDeltaMode) : (string * string) list =
    match hotReloadActive, mode with
    | false, _ -> []
    | true, MetadataDeltaMode.Off -> [ environmentVariable, toText mode ]
    | true, MetadataDeltaMode.On -> [ environmentVariable, toText mode; modifiableAssembliesVariable, modifiableAssembliesValue ]
