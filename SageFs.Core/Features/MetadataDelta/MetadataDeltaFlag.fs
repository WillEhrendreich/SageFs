/// The switch for the metadata-delta path. It is off unless the process says otherwise, and nothing in the
/// product reads it yet: the emitter is a library until the planner is wired to it.
namespace SageFs.Features.MetadataDelta

/// Whether patches may be taken as metadata deltas.
[<RequireQualifiedAccess>]
type MetadataDeltaMode =
  | Off
  | On

[<RequireQualifiedAccess>]
module MetadataDeltaMode =

  /// The environment variable that turns it on.
  let environmentVariable = "SAGEFS_METADATA_DELTA"

  /// Only the spellings below turn it on. Anything else, an empty value included, leaves it off.
  let parse (value: string | null) : MetadataDeltaMode =
    match value with
    | null -> MetadataDeltaMode.Off
    | text ->
      match text.Trim().ToLowerInvariant() with
      | "1"
      | "on"
      | "true" -> MetadataDeltaMode.On
      | _ -> MetadataDeltaMode.Off

  let fromEnvironment () : MetadataDeltaMode =
    parse (System.Environment.GetEnvironmentVariable environmentVariable)

  let toText (mode: MetadataDeltaMode) : string =
    match mode with
    | MetadataDeltaMode.Off -> "off"
    | MetadataDeltaMode.On -> "on"
