namespace SageFs

/// How a directory's artifacts path travels. The daemon reads `.SageFs/config.fsx`; the builds it starts, the worker it
/// spawns and that worker's project loader each need the same path. The one SageFs variable below carries it to the
/// worker, and the loader turns it into the MSBuild property `ArtifactsPath` (a global property, so it also wins over a
/// value in the environment). Dependency-free, so the project loader can use it.
module ArtifactsChannel =

  /// The worker's variable. Named for SageFs, so a person's own `ArtifactsPath` in the environment is never mistaken for it.
  let variable : string = "SAGEFS_ARTIFACTS_PATH"

  /// The MSBuild property that moves `bin` and `obj`.
  let msbuildProperty : string = "ArtifactsPath"

  let pathOf (lookup: string -> string) : ArtifactsDir =
    match lookup variable with
    | null
    | "" -> ProjectChooses
    | path -> ArtifactsIn path

  /// The global properties the project loader passes MSBuild.
  let properties (lookup: string -> string) : (string * string) list =
    match pathOf lookup with
    | ArtifactsIn path -> [ msbuildProperty, path ]
    | ProjectChooses -> []
