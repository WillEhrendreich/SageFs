/// Pure decisions for running a project on a .NET runtime newer (or otherwise different) than
/// the one the FSI host was built for. No IO here: callers read the runtimeconfig.json
/// and the installed runtimes, and apply the resulting environment.
///
/// Why this exists: a net11 project needs the net11 runtime's framework assemblies, but a host built
/// for net10 runs on the net10 runtime by default, so warmup fails with e.g.
/// "Microsoft.Extensions.Logging.Abstractions, Version=11.0.0.0 is not referenced". A newer
/// runtime can host older-targeted code, so the fix is to launch the host on the newer runtime
/// (roll-forward) only when the project needs it, and otherwise leave the launch untouched.
module SageFs.RuntimeCompat

open System
open System.Text.Json

/// Whether the required runtime is a released version or a preview/RC.
type RuntimeStability =
  | Stable
  | Prerelease

/// The runtime a project was built against, read from its runtimeconfig.json.
type RuntimeRequirement = { Major: int; Stability: RuntimeStability }

/// Why the runtime a project needs could not be determined.
type RuntimeConfigError =
  | NoProjects
  | ProjectNotBuilt of project: string
  | RuntimeConfigNotFound of configName: string * searchedIn: string
  | RuntimeConfigUnreadable of path: string * detail: string
  | NotJsonRuntimeConfig of detail: string
  | NoFrameworkVersion
  | UnrecognisedFrameworkVersion of text: string

let describeConfigError (error: RuntimeConfigError) : string =
  match error with
  | NoProjects -> "the session has no projects"
  | ProjectNotBuilt project -> sprintf "%s has not been built (no bin/ directory)" project
  | RuntimeConfigNotFound(configName, searchedIn) -> sprintf "no %s under %s (project not built?)" configName searchedIn
  | RuntimeConfigUnreadable(path, detail) -> sprintf "could not read %s: %s" path detail
  | NotJsonRuntimeConfig detail -> sprintf "runtimeconfig.json is not valid JSON: %s" detail
  | NoFrameworkVersion -> "runtimeconfig.json names no framework version"
  | UnrecognisedFrameworkVersion text -> sprintf "unrecognised framework version '%s'" text

/// What to do about the host's runtime for one session.
type RuntimeChoice =
  /// The host's own runtime satisfies the project: change nothing.
  | HostFits
  /// The project needs a newer runtime and one is installed: launch the host with roll-forward.
  | RollForward of major: int * stability: RuntimeStability
  /// The project needs a newer runtime and none is installed: the user must install one.
  | RuntimeMissing of major: int
  /// The requirement could not be determined (e.g. the project is not built yet): change nothing.
  | Unknown of reason: RuntimeConfigError

let private parseVersionString (text: string) : Result<RuntimeRequirement, RuntimeConfigError> =
  let numeric, stability =
    match text.IndexOf '-' with
    | -1 -> text, Stable
    | dash -> text.Substring(0, dash), Prerelease
  match Version.TryParse numeric with
  | true, version -> Ok { Major = version.Major; Stability = stability }
  | false, _ -> Error(UnrecognisedFrameworkVersion text)

/// Read the runtime a project needs from the text of its `<name>.runtimeconfig.json`.
/// Takes the highest major across `framework` / `frameworks` (an ASP.NET project lists both
/// Microsoft.NETCore.App and Microsoft.AspNetCore.App).
let parseRuntimeRequirement (json: string) : Result<RuntimeRequirement, RuntimeConfigError> =
  try
    use document = JsonDocument.Parse json
    let versionsOf (element: JsonElement) =
      match element.TryGetProperty "version" with
      | true, version when version.ValueKind = JsonValueKind.String -> [ version.GetString() ]
      | _ -> []
    let frameworkVersions =
      match document.RootElement.TryGetProperty "runtimeOptions" with
      | true, options ->
        let single =
          match options.TryGetProperty "framework" with
          | true, framework -> versionsOf framework
          | false, _ -> []
        let many =
          match options.TryGetProperty "frameworks" with
          | true, frameworks when frameworks.ValueKind = JsonValueKind.Array ->
            [ for framework in frameworks.EnumerateArray() do yield! versionsOf framework ]
          | _ -> []
        single @ many
      | false, _ -> []
    match frameworkVersions with
    | [] -> Error NoFrameworkVersion
    | versions ->
      let parsed = versions |> List.map parseVersionString
      let firstError = parsed |> List.tryPick (function Error e -> Some e | Ok _ -> None)
      let requirements = parsed |> List.choose (function Ok r -> Some r | Error _ -> None)
      match firstError, requirements with
      | Some error, [] -> Error error
      | _, requirements ->
        // Highest major wins; if any framework at that major is a prerelease, the requirement is one.
        let top = requirements |> List.map (fun r -> r.Major) |> List.max
        let atTop = requirements |> List.filter (fun r -> r.Major = top)
        let stability = if atTop |> List.exists (fun r -> r.Stability = Prerelease) then Prerelease else Stable
        Ok { Major = top; Stability = stability }
  with :? JsonException as ex ->
    Error(NotJsonRuntimeConfig ex.Message)

/// Decide how to launch the host for a session.
/// `hostMajor` is the runtime major the host would run on by default; `installedMajors` are
/// the runtime majors installed on this machine.
let decide (hostMajor: int) (installedMajors: int list) (requirement: Result<RuntimeRequirement, RuntimeConfigError>) : RuntimeChoice =
  match requirement with
  | Error reason -> Unknown reason
  | Ok required when required.Major <= hostMajor -> HostFits
  | Ok required ->
    match installedMajors |> List.exists (fun installed -> installed >= required.Major) with
    | true -> RollForward(required.Major, required.Stability)
    | false -> RuntimeMissing required.Major

/// Environment variables to set on the host process for a choice. Empty unless rolling forward.
let rollForwardEnv (choice: RuntimeChoice) : (string * string) list =
  match choice with
  | RollForward(_, Stable) -> [ "DOTNET_ROLL_FORWARD", "LatestMajor" ]
  | RollForward(_, Prerelease) -> [ "DOTNET_ROLL_FORWARD", "LatestMajor"; "DOTNET_ROLL_FORWARD_TO_PRERELEASE", "1" ]
  | HostFits
  | RuntimeMissing _
  | Unknown _ -> []

/// A user-facing explanation, with what to do about it, for choices that need the user.
let describe (choice: RuntimeChoice) : string =
  match choice with
  | RuntimeMissing major ->
    sprintf "This project targets .NET %d but no .NET %d (or newer) runtime is installed. Install the .NET %d runtime or SDK from https://dotnet.microsoft.com/download and start the session again." major major major
  | Unknown reason -> sprintf "Could not determine which .NET runtime the project needs (%s); using the default runtime." (describeConfigError reason)
  | RollForward(major, Prerelease) -> sprintf "Running this session on the newest installed .NET runtime (>= %d, including previews) because the project targets a preview of .NET %d." major major
  | RollForward(major, Stable) -> sprintf "Running this session on the newest installed .NET runtime (>= %d) because the project targets .NET %d." major major
  | HostFits -> "The default runtime satisfies this project."

/// Why no shared-framework directory could be chosen.
type FrameworkDirError = NoFrameworkForMajor of major: int * available: string list

let describeFrameworkDirError (error: FrameworkDirError) : string =
  match error with
  | NoFrameworkForMajor(major, available) ->
    sprintf "no shared framework directory for .NET %d among [%s]" major (String.concat ", " available)

/// Pick the shared-framework directory (e.g. Microsoft.AspNetCore.App/<dir>) whose reference assemblies
/// match the runtime the host is ACTUALLY running on: exact major.minor.patch, else the highest of the
/// same major. Never a different major, which is what "newest directory" got wrong on a machine with
/// several runtimes installed. Directory names may carry a prerelease suffix ("11.0.0-rc.1.26425.128").
let selectFrameworkDir (running: Version) (dirNames: string list) : Result<string, FrameworkDirError> =
  let parsed =
    dirNames
    |> List.choose (fun name ->
      match Version.TryParse(name.Split('-').[0]) with
      | true, version -> Some(version, name)
      | false, _ -> None)
  let sameMajor = parsed |> List.filter (fun (version, _) -> version.Major = running.Major)
  let exact =
    sameMajor |> List.filter (fun (version, _) -> version.Minor = running.Minor && version.Build = running.Build)
  match exact |> List.sortByDescending snd, sameMajor |> List.sortByDescending id with
  | (_, name) :: _, _ -> Ok name
  | [], (_, name) :: _ -> Ok name
  | [], [] -> Error(NoFrameworkForMajor(running.Major, dirNames))
