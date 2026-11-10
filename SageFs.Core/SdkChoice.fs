/// Which .NET SDK a session's project load uses, decided from three facts: the target framework(s) the project
/// declares, the SDKs installed, and the `global.json` pin above the project (if any).
///
/// Why this exists: with no `global.json`, `dotnet` picks the NEWEST installed SDK, previews included. A machine
/// with SDK 10 and SDK 11 installed therefore builds a `net10.0` project with SDK 11, and SageFs's worker, which
/// runs on .NET 10, cannot load SDK 11's MSBuild into its own process (`ProjectLoading.shouldSkipInProcessLoad`).
/// The project is then read by hand from its `.fsproj`, which gives source files and no references, and the
/// session faulted in warmup with a compiler message (`StopProcessingExn`) that names none of that (#183).
///
/// `decide` is the pure choice. It either returns the SDK to use or a `Refusal` that carries the SDK it chose and
/// the SDK a `global.json` should pin; `describeRefusal` is the one place a refusal becomes text. The edge
/// (`refusalFor`) reads the installed SDKs and the pin and never throws.
module SageFs.SdkChoice

open System
open System.Diagnostics
open System.IO
open System.Text.Json

/// An SDK version as `dotnet --list-sdks` prints it ("10.0.402", "11.0.100-rc.2.26504.105").
/// `Pre` holds the dot-separated pre-release identifiers, empty for a release.
type SdkVersion =
  { Major: int
    Minor: int
    Patch: int
    Pre: string list
    Text: string }

module SdkVersion =
  /// The feature band: 402 is band 4, 100 is band 1.
  let band (v: SdkVersion) : int = v.Patch / 100

  let isRelease (v: SdkVersion) : bool = List.isEmpty v.Pre

  let parse (text: string) : SdkVersion option =
    let trimmed = text.Trim()
    let withoutBuild =
      match trimmed.IndexOf '+' with
      | -1 -> trimmed
      | at -> trimmed.Substring(0, at)
    let core, pre =
      match withoutBuild.IndexOf '-' with
      | -1 -> withoutBuild, []
      | at -> withoutBuild.Substring(0, at), withoutBuild.Substring(at + 1).Split('.') |> Array.toList
    match core.Split '.' |> Array.map Int32.TryParse |> Array.toList with
    | [ (true, major); (true, minor); (true, patch) ] ->
      Some { Major = major; Minor = minor; Patch = patch; Pre = pre; Text = trimmed }
    | _ -> None

  let compareIdentifier (a: string) (b: string) : int =
    match Int32.TryParse a, Int32.TryParse b with
    | (true, x), (true, y) -> compare x y
    | (true, _), (false, _) -> -1
    | (false, _), (true, _) -> 1
    | (false, _), (false, _) -> String.CompareOrdinal(a, b)

  let rec comparePre (a: string list) (b: string list) : int =
    match a, b with
    | [], [] -> 0
    | [], _ -> 1 // a release is newer than any pre-release of the same number
    | _, [] -> -1
    | x :: xs, y :: ys ->
      match compareIdentifier x y with
      | 0 -> comparePre xs ys
      | other -> other

  /// SemVer order, the way `dotnet` ranks installed SDKs.
  let compareVersions (a: SdkVersion) (b: SdkVersion) : int =
    match compare (a.Major, a.Minor, a.Patch) (b.Major, b.Minor, b.Patch) with
    | 0 -> comparePre a.Pre b.Pre
    | other -> other

  let newest (versions: SdkVersion list) : SdkVersion option =
    match versions with
    | [] -> None
    | _ -> versions |> List.reduce (fun a b -> match compareVersions a b >= 0 with | true -> a | false -> b) |> Some

/// How `global.json` lets the pinned version roll forward to what is installed (`sdk.rollForward`).
[<RequireQualifiedAccess>]
type RollForward =
  | Disable
  | Patch
  | Feature
  | Minor
  | Major
  | LatestPatch
  | LatestFeature
  | LatestMinor
  | LatestMajor

module RollForward =
  /// What `dotnet` does when `sdk.rollForward` is absent.
  let defaultPolicy : RollForward = RollForward.LatestPatch

  let parse (text: string) : RollForward option =
    match text.Trim().ToLowerInvariant() with
    | "disable" -> Some RollForward.Disable
    | "patch" -> Some RollForward.Patch
    | "feature" -> Some RollForward.Feature
    | "minor" -> Some RollForward.Minor
    | "major" -> Some RollForward.Major
    | "latestpatch" -> Some RollForward.LatestPatch
    | "latestfeature" -> Some RollForward.LatestFeature
    | "latestminor" -> Some RollForward.LatestMinor
    | "latestmajor" -> Some RollForward.LatestMajor
    | _ -> None

  let describe (policy: RollForward) : string =
    match policy with
    | RollForward.Disable -> "disable"
    | RollForward.Patch -> "patch"
    | RollForward.Feature -> "feature"
    | RollForward.Minor -> "minor"
    | RollForward.Major -> "major"
    | RollForward.LatestPatch -> "latestPatch"
    | RollForward.LatestFeature -> "latestFeature"
    | RollForward.LatestMinor -> "latestMinor"
    | RollForward.LatestMajor -> "latestMajor"

/// An `sdk.version` found in a `global.json`, and where.
type GlobalJsonPin =
  { Path: string
    Version: SdkVersion
    RollForward: RollForward }

/// Whether the nearest `global.json` pins an SDK.
type PinState =
  | PinFound of GlobalJsonPin
  /// No `global.json`, or one that pins nothing (no `sdk.version`: it may only carry `msbuild-sdks` or `paths`).
  | NoPinFound

/// Why a `global.json` could not be read as a pin.
type GlobalJsonError =
  | NotJson of path: string * detail: string
  | VersionNotParsable of path: string * text: string
  | RollForwardUnknown of path: string * text: string

let describeGlobalJsonError (error: GlobalJsonError) : string =
  match error with
  | NotJson(path, detail) -> sprintf "%s is not valid JSON (%s)" path detail
  | VersionNotParsable(path, text) -> sprintf "sdk.version '%s' in %s is not a version number" text path
  | RollForwardUnknown(path, text) -> sprintf "sdk.rollForward '%s' in %s is not a rollForward policy" text path

/// The pin a `global.json`'s text carries.
let parseGlobalJson (path: string) (text: string) : Result<PinState, GlobalJsonError> =
  let parsed =
    Guarded.attempt (fun () ->
      let options = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)
      use document = JsonDocument.Parse(text, options)
      let sdk =
        match document.RootElement.ValueKind with
        | JsonValueKind.Object ->
          match document.RootElement.TryGetProperty "sdk" with
          | true, sdk when sdk.ValueKind = JsonValueKind.Object -> Some sdk
          | _ -> None
        | _ -> None
      let stringProperty (name: string) (element: JsonElement) : string option =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None
      sdk |> Option.bind (stringProperty "version"), sdk |> Option.bind (stringProperty "rollForward"))
  match parsed with
  | Guarded.Attempt.Threw ex -> Result.Error(NotJson(path, ex.Message))
  | Guarded.Attempt.Completed(None, _) -> Result.Ok NoPinFound
  | Guarded.Attempt.Completed(Some versionText, policyText) ->
    match SdkVersion.parse versionText with
    | None -> Result.Error(VersionNotParsable(path, versionText))
    | Some version ->
      match policyText with
      | None -> Result.Ok(PinFound { Path = path; Version = version; RollForward = RollForward.defaultPolicy })
      | Some text ->
        match RollForward.parse text with
        | Some policy -> Result.Ok(PinFound { Path = path; Version = version; RollForward = policy })
        | None -> Result.Error(RollForwardUnknown(path, text))

/// The installed SDK a pin selects, by the rules `dotnet` documents for `sdk.rollForward`. None when no installed
/// SDK satisfies it.
let resolvePin (pin: GlobalJsonPin) (installed: SdkVersion list) : SdkVersion option =
  let pinned = pin.Version
  let atLeast = installed |> List.filter (fun v -> SdkVersion.compareVersions v pinned >= 0)
  let sameMinor (v: SdkVersion) = v.Major = pinned.Major && v.Minor = pinned.Minor
  let sameBand (v: SdkVersion) = sameMinor v && SdkVersion.band v = SdkVersion.band pinned
  let sameMajor (v: SdkVersion) = v.Major = pinned.Major
  // The lowest group (by `key`) that has a candidate, then the newest SDK in it.
  let lowestGroupNewest (key: SdkVersion -> int * int * int) (candidates: SdkVersion list) : SdkVersion option =
    match candidates with
    | [] -> None
    | _ ->
      let lowest = candidates |> List.map key |> List.min
      candidates |> List.filter (fun v -> key v = lowest) |> SdkVersion.newest
  match pin.RollForward with
  | RollForward.Disable -> installed |> List.tryFind (fun v -> SdkVersion.compareVersions v pinned = 0)
  | RollForward.Patch ->
    match installed |> List.tryFind (fun v -> SdkVersion.compareVersions v pinned = 0) with
    | Some exact -> Some exact
    | None -> atLeast |> List.filter sameBand |> SdkVersion.newest
  | RollForward.LatestPatch -> atLeast |> List.filter sameBand |> SdkVersion.newest
  | RollForward.Feature -> atLeast |> List.filter sameMinor |> lowestGroupNewest (fun v -> v.Major, v.Minor, SdkVersion.band v)
  | RollForward.LatestFeature -> atLeast |> List.filter sameMinor |> SdkVersion.newest
  | RollForward.Minor -> atLeast |> List.filter sameMajor |> lowestGroupNewest (fun v -> v.Major, v.Minor, SdkVersion.band v)
  | RollForward.LatestMinor -> atLeast |> List.filter sameMajor |> SdkVersion.newest
  | RollForward.Major -> atLeast |> lowestGroupNewest (fun v -> v.Major, v.Minor, SdkVersion.band v)
  | RollForward.LatestMajor -> atLeast |> SdkVersion.newest

/// What the decision reads.
/// What the project needs of the SDK that builds it.
type SdkNeed =
  /// The highest major among the project's declared target frameworks (`net10.0` is 10): an older SDK cannot build it.
  | AtLeastMajor of int
  /// The project names no target framework in its own file (a Directory.Build.props may), so any SDK can build it.
  | AnySdk

type Facts =
  { Need: SdkNeed
    /// The major of the runtime SageFs's worker runs on. The worker loads the SDK's MSBuild into its own process,
    /// and a newer SDK's MSBuild cannot be loaded into an older runtime.
    HostMajor: int
    Installed: SdkVersion list
    Pin: PinState }

/// Why the SDK is the one it is.
type Source =
  /// No `global.json` pins one, so `dotnet` takes the newest installed.
  | Newest
  | PinnedBy of GlobalJsonPin

type Pick = { Sdk: SdkVersion; Source: Source }

/// What a `global.json` should pin instead of the SDK that was chosen.
type Remedy =
  /// An installed SDK: the newest release the worker can load that is still new enough to build the project.
  | PinInstalled of SdkVersion
  /// No installed SDK is both loadable by the worker and able to build the project.
  | NoInstalledFit

/// The chosen SDK is newer than the runtime the worker loads it into.
type TooNew =
  { Chosen: Pick
    HostMajor: int
    Need: SdkNeed
    Remedy: Remedy }

type Refusal =
  | TooNewForHost of TooNew
  | PinNotInstalled of pin: GlobalJsonPin * installed: SdkVersion list
  | NoSdkInstalled

type Decision =
  | Use of Pick
  | Refuse of Refusal

/// The installed SDK to pin when the chosen one is too new: majors the worker can load, no older than the project
/// needs, highest major first, a release over a pre-release, then the newest.
let remedyFor (facts: Facts) : Remedy =
  let oldestThatBuilds =
    match facts.Need with
    | AtLeastMajor major -> major
    | AnySdk -> 0
  let fitting =
    facts.Installed
    |> List.filter (fun v -> v.Major <= facts.HostMajor && v.Major >= oldestThatBuilds)
    |> List.sortWith (fun a b ->
      match compare b.Major a.Major with
      | 0 ->
        match compare (SdkVersion.isRelease b) (SdkVersion.isRelease a) with
        | 0 -> SdkVersion.compareVersions b a
        | other -> other
      | other -> other)
  match fitting with
  | best :: _ -> PinInstalled best
  | [] -> NoInstalledFit

let decide (facts: Facts) : Decision =
  let picked : Result<Pick, Refusal> =
    match facts.Pin with
    | PinFound pin ->
      match resolvePin pin facts.Installed with
      | Some sdk -> Result.Ok { Sdk = sdk; Source = PinnedBy pin }
      | None -> Result.Error(PinNotInstalled(pin, facts.Installed))
    | NoPinFound ->
      match SdkVersion.newest facts.Installed with
      | Some sdk -> Result.Ok { Sdk = sdk; Source = Newest }
      | None -> Result.Error NoSdkInstalled
  match picked with
  | Result.Error refusal -> Refuse refusal
  | Result.Ok pick when pick.Sdk.Major > facts.HostMajor ->
    Refuse(TooNewForHost { Chosen = pick; HostMajor = facts.HostMajor; Need = facts.Need; Remedy = remedyFor facts })
  | Result.Ok pick -> Use pick

/// The `global.json` text that pins `version`.
let pinSnippet (version: string) : string =
  sprintf "{\n  \"sdk\": {\n    \"version\": \"%s\"\n  }\n}" version

let describeSource (source: Source) : string =
  match source with
  | Newest -> "the newest installed, and no global.json pins one"
  | PinnedBy pin -> sprintf "pinned by %s" pin.Path

let describeInstalled (installed: SdkVersion list) : string =
  match installed with
  | [] -> "none"
  | _ -> installed |> List.map (fun v -> v.Text) |> String.concat ", "

/// The refusal as text: what was chosen and why, what that did, and the exact `global.json` that fixes it.
let describeRefusal (refusal: Refusal) : string =
  match refusal with
  | TooNewForHost t ->
    let project =
      match t.Need with
      | AtLeastMajor major -> sprintf "This project targets net%d.0. " major
      | AnySdk -> ""
    let consequence =
      sprintf
        "It was given .NET SDK %s (%s). SageFs's worker runs on .NET %d and cannot load the MSBuild of SDK %d.x into its own process, so the project was read by hand from its .fsproj: that gives source files and no references or build output, and a session started that way can fail in warmup."
        t.Chosen.Sdk.Text
        (describeSource t.Chosen.Source)
        t.HostMajor
        t.Chosen.Sdk.Major
    let fix =
      match t.Remedy, t.Chosen.Source with
      | PinInstalled suggested, Newest ->
        sprintf
          "SDK %s is installed and fits. Pin it with a global.json in the project's folder or any folder above it:\n%s\nThen run hard_reset_fsi_session."
          suggested.Text
          (pinSnippet suggested.Text)
      | PinInstalled suggested, PinnedBy pin ->
        sprintf
          "SDK %s is installed and fits. Change sdk.version in %s to it:\n%s\nThen run hard_reset_fsi_session."
          suggested.Text
          pin.Path
          (pinSnippet suggested.Text)
      | NoInstalledFit, _ ->
        match t.Need with
        | AtLeastMajor needed when needed > t.HostMajor ->
          sprintf
            "The project needs .NET SDK %d or newer and the worker can only load SDK %d or older, so no global.json can fix this. Run SageFs on .NET %d or newer."
            needed
            t.HostMajor
            needed
        | AtLeastMajor needed ->
          sprintf
            "No installed SDK is one the worker can load (major %d through %d). Install a .NET %d SDK from https://dotnet.microsoft.com/download and pin it with a global.json:\n{\n  \"sdk\": {\n    \"version\": \"%d.0.100\",\n    \"rollForward\": \"latestFeature\"\n  }\n}\nThen run hard_reset_fsi_session."
            needed
            t.HostMajor
            needed
            needed
        | AnySdk ->
          sprintf
            "No installed SDK is one the worker can load (major %d or older). Install a .NET %d SDK from https://dotnet.microsoft.com/download and pin it with a global.json:\n{\n  \"sdk\": {\n    \"version\": \"%d.0.100\",\n    \"rollForward\": \"latestFeature\"\n  }\n}\nThen run hard_reset_fsi_session."
            t.HostMajor
            t.HostMajor
            t.HostMajor
    sprintf "%s%s\n%s" project consequence fix
  | PinNotInstalled(pin, installed) ->
    sprintf
      "%s pins .NET SDK %s with rollForward %s, and no installed SDK matches it. Installed: %s. Change sdk.version to an installed SDK, or install %s from https://dotnet.microsoft.com/download."
      pin.Path
      pin.Version.Text
      (RollForward.describe pin.RollForward)
      (describeInstalled installed)
      pin.Version.Text
  | NoSdkInstalled ->
    "`dotnet --list-sdks` lists no .NET SDK. Install one from https://dotnet.microsoft.com/download."

// ---- the edge: what this machine has ----

/// The versions in `dotnet --list-sdks` output ("10.0.402 [/path/sdk]" per line). Lines that are not a version are dropped.
let parseSdkList (output: string) : SdkVersion list =
  output.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
  |> Array.choose (fun line ->
    match line.Trim().Split(' ') with
    | [||] -> None
    | parts -> SdkVersion.parse parts.[0])
  |> Array.toList

/// The pin the nearest `global.json` at or above `startDir` carries. `dotnet` uses the first one it finds, even
/// when that file pins nothing, so the walk stops there. A file that cannot be read or parsed reads as no pin.
let findPin (startDir: string) : PinState =
  let read (candidate: string) : PinState =
    match Guarded.attempt (fun () -> File.ReadAllText candidate) with
    | Guarded.Attempt.Completed text ->
      match parseGlobalJson candidate text with
      | Result.Ok state -> state
      | Result.Error _ -> NoPinFound
    | Guarded.Attempt.Threw _ -> NoPinFound
  let rec up (directory: DirectoryInfo) : PinState =
    match directory with
    | null -> NoPinFound
    | dir ->
      let candidate = Path.Combine(dir.FullName, "global.json")
      match File.Exists candidate with
      | true -> read candidate
      | false -> up dir.Parent
  match Guarded.attempt (fun () -> up (DirectoryInfo startDir)) with
  | Guarded.Attempt.Completed state -> state
  | Guarded.Attempt.Threw _ -> NoPinFound

/// The installed SDKs, from `dotnet --list-sdks` run in `workingDir`. Empty when it cannot be run or does not answer.
let listSdks (workingDir: string) : SdkVersion list =
  let ask () : SdkVersion list =
    let psi = ProcessStartInfo("dotnet", "--list-sdks")
    psi.WorkingDirectory <- workingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    ProcessEnvironment.applyTo psi []
    use proc = Process.Start psi
    let output = proc.StandardOutput.ReadToEndAsync()
    match proc.WaitForExit(int Timeouts.dotnetSdkQuery.TotalMilliseconds) with
    | false ->
      Guarded.call ignore (fun () -> proc.Kill true)
      []
    | true when proc.ExitCode = 0 -> parseSdkList output.Result
    | true -> []
  match Guarded.attempt ask with
  | Guarded.Attempt.Completed versions -> versions
  | Guarded.Attempt.Threw _ -> []

/// What the projects need of the SDK: the highest major among the target frameworks they declare in their .fsproj
/// text. A project whose file names none (a Directory.Build.props sets it) or cannot be read adds nothing.
let sdkNeedOf (projects: string list) : SdkNeed =
  let majorsOf (project: string) : int list =
    match Guarded.attempt (fun () -> File.ReadAllText project) with
    | Guarded.Attempt.Threw _ -> []
    | Guarded.Attempt.Completed xml ->
      match ProjectCompatibility.readTargetFrameworks xml with
      | Result.Ok tfms ->
        tfms
        |> List.choose (fun tfm ->
          match ProjectCompatibility.classifyTfm tfm with
          | ProjectCompatibility.TfmVerdict.Supported(_, major) -> Some major
          | ProjectCompatibility.TfmVerdict.Unsupported _
          | ProjectCompatibility.TfmVerdict.Unknown _ -> None)
      | Result.Error _ -> []
  match projects |> List.collect majorsOf with
  | [] -> AnySdk
  | majors -> AtLeastMajor(List.max majors)

/// The refusal for this layout, or None when the SDK is usable (or the machine could not be asked). Never throws.
let refusalFor (hostMajor: int) (workingDir: string) (projects: string list) : Refusal option =
  let ask () : Refusal option =
    match listSdks workingDir with
    | [] -> None // `dotnet --list-sdks` could not be asked: there is nothing to decide from
    | installed ->
      match decide { Need = sdkNeedOf projects; HostMajor = hostMajor; Installed = installed; Pin = findPin workingDir } with
      | Refuse refusal -> Some refusal
      | Use _ -> None
  match Guarded.attempt ask with
  | Guarded.Attempt.Completed refusal -> refusal
  | Guarded.Attempt.Threw _ -> None

/// The fault message with what is known about how the project was loaded under it, when something is.
let faultMessage (message: string) (cause: string option) : string =
  match cause with
  | Some text -> sprintf "%s\nProject load: %s" message text
  | None -> message

/// What a warmup fault should add to its message: why the project was read by hand, when it was. A project skipped
/// because its SDK is newer than the worker's runtime gets the refusal for this machine (`ask`, normally
/// `refusalFor`, called only then), which names the SDK and the global.json that pins a loadable one; if that
/// cannot be worked out, or the project fell back for another reason, the cause's own description. A project the
/// loader evaluated adds nothing.
let faultAdvice (ask: unit -> Refusal option) (mode: ProjectLoading.LoadMode) : string option =
  match mode with
  | ProjectLoading.LoadMode.ManualFallback(ProjectLoading.FallbackCause.SkippedNewerSdk _ as cause) ->
    match ask () with
    | Some refusal -> Some(describeRefusal refusal)
    | None -> Some(ProjectLoading.FallbackCause.describe cause)
  | ProjectLoading.LoadMode.ManualFallback cause -> Some(ProjectLoading.FallbackCause.describe cause)
  | ProjectLoading.LoadMode.EvaluatedWithConcerns _
  | ProjectLoading.LoadMode.Evaluated -> None

/// The message a failed warmup is published with: the failure's own text, then why the project was read by hand
/// if it was. `dotnet` picks the SDK from the project's folder upward, so that is where the machine is asked from.
/// `hostMajor` is the runtime this process runs on, which is the worker's.
let warmupFailureMessage (hostMajor: int) (message: string) (mode: ProjectLoading.LoadMode) (projectFiles: string list) : string =
  let searchFrom =
    match projectFiles with
    | first :: _ ->
      match Path.GetDirectoryName(first: string) with
      | null -> "."
      | directory -> directory
    | [] -> "."
  faultMessage message (faultAdvice (fun () -> refusalFor hostMajor searchFrom projectFiles) mode)
