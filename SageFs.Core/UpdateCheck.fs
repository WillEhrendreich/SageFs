namespace SageFs

open System

/// Where SageFs is running from — decides whether a staleness verdict is
/// even meaningful. A local source build's version means nothing next to
/// NuGet's release cadence (mid-work on a branch, an unpushed commit, a
/// worktree still on yesterday's `master`), and "dotnet tool update -g
/// sagefs" is the wrong advice for it, so it must never be compared at all.
[<RequireQualifiedAccess>]
type InstallKind =
  | ToolInstall
  | LocalBuild

[<RequireQualifiedAccess>]
module InstallKind =

  /// `dotnet tool install -g` always lays the launcher under
  /// `<dotnetToolsDir>` (`~/.dotnet/tools` by convention). A Release build
  /// run straight from the repo, `dotnet run`, or a CI checkout is anything
  /// else. Pure: both paths are parameters, so this is testable without
  /// touching `Environment.ProcessPath` or the real filesystem.
  let classify (executablePath: string option) (dotnetToolsDir: string) : InstallKind =
    match executablePath with
    | Some path when not (String.IsNullOrEmpty dotnetToolsDir) && path.StartsWith(dotnetToolsDir, StringComparison.OrdinalIgnoreCase) ->
      InstallKind.ToolInstall
    | _ -> InstallKind.LocalBuild

/// Why an update check produced no verdict at all. Distinct from
/// `UpdateOutcome.UpToDate` on purpose — see that type's doc comment: "we
/// don't know" must never render as "you're current".
[<RequireQualifiedAccess>]
type SkipReason =
  | TooSoonSinceLastCheck
  | OptedOut
  | NotAToolInstall

/// A presentation band for a known distance-behind. Purely cosmetic (how
/// loud a banner/log line should be) — never itself a reason to withhold or
/// alter the real numbers in `UpdateOutcome.UpdateAvailable`.
[<RequireQualifiedAccess>]
type Urgency =
  | Mild
  | Notable
  | Severe

/// The one honest verdict every surface renders from. `CheckSkipped` and
/// `CheckFailed` are distinct from `UpToDate` and from each other so that
/// ignorance (opted out, too soon, not a tool install) and failure (offline,
/// timeout, a garbled feed) can never be mistaken for "you are current" —
/// silence on failure is correct, a false all-clear is not.
[<RequireQualifiedAccess>]
type UpdateOutcome =
  | UpToDate of current: Version
  | UpdateAvailable of current: Version * latest: Version * behind: int
  | CheckSkipped of SkipReason
  | CheckFailed of reason: string

/// Pure core for "is this SageFs stale?" (issue #136). No IO anywhere in
/// this module: the HTTP GET against NuGet, the on-disk cache, and the
/// clock all live in the (impure) adapter that calls these functions with
/// facts it already resolved.
[<RequireQualifiedAccess>]
module UpdateCheck =

  /// Thresholds for `bandUrgency`, named so the numbers explain themselves
  /// at the call site instead of living as bare literals in a match. Chosen
  /// against the incident that motivated this feature: 43 versions behind
  /// (Severe) after 14.5 hours; a handful behind (Mild) is normal drift
  /// between "I opened my laptop" and "I checked".
  let mildCeiling = 5
  let notableCeiling = 25

  /// Should the adapter even ask NuGet again? `None` (never checked, e.g.
  /// first run) is always due.
  let shouldCheck (now: DateTimeOffset) (lastChecked: DateTimeOffset option) (interval: TimeSpan) : bool =
    match lastChecked with
    | None -> true
    | Some last -> now - last >= interval

  /// Drop the fourth (Revision) component that a .NET assembly version
  /// carries but a NuGet package version never does. Left in, `Version
  /// "0.6.823"` (Revision = -1, unset) compares as LESS than `Version
  /// "0.6.823.0"` (Revision = 0) even though they name the same release —
  /// that would manufacture a phantom "1 version behind" on every daemon.
  let normalize (v: Version) : Version = Version(v.Major, v.Minor, max v.Build 0)

  /// Best-effort parse of a version string the way NuGet publishes it
  /// (`0.6.823`, possibly `-rc.1` prerelease) or the way .NET's own
  /// `AssemblyInformationalVersionAttribute` renders it (the SDK appends a
  /// `+<sourcerevisionid>` build-metadata suffix by default). Both suffix
  /// characters are stripped before parsing; an unparseable remainder is
  /// simply excluded rather than failing the whole comparison (see the
  /// adapter's use over a whole version list).
  let tryParseVersion (text: string) : Version option =
    let cut = text.IndexOfAny [| '-'; '+' |]
    let core = if cut = -1 then text else text.Substring(0, cut)
    match Version.TryParse core with
    | true, v -> Some(normalize v)
    | false, _ -> None

  /// How many releases `current` is behind `latest`, assuming this repo's
  /// own convention (`Directory.Build.props`): Major.Minor changes rarely
  /// and by hand; every ordinary release auto-bumps only the patch
  /// component. When Major or Minor differ, the patch component alone
  /// cannot express the true distance, so this is a lower bound — 1
  /// whenever `latest` is newer, never more precise than that. Always 0
  /// when `latest <= current` (current is up to date, or ahead — a local
  /// build one commit past the last release is not "behind" anything).
  /// Normalizes both inputs itself (see `normalize`) so a caller that
  /// forgets to strip the Revision component can never manufacture a
  /// phantom distance here.
  let versionsBehind (current: Version) (latest: Version) : int =
    let current = normalize current
    let latest = normalize latest
    match latest.CompareTo(current) with
    | c when c <= 0 -> 0
    | _ ->
      match latest.Major = current.Major && latest.Minor = current.Minor with
      | true -> max 1 (latest.Build - current.Build)
      | false -> 1

  /// Turn a raw distance into a presentation band. Never called for 0 — see
  /// `decide`, where `UpToDate` never carries a distance to band.
  let bandUrgency (behind: int) : Urgency =
    match behind with
    | n when n < mildCeiling -> Urgency.Mild
    | n when n < notableCeiling -> Urgency.Notable
    | _ -> Urgency.Severe

  /// The whole decision, pure: every fact the caller already resolved
  /// (which kind of install this is, whether the user opted out, and the
  /// result of the adapter's own attempt to learn the latest published
  /// version — fresh from NuGet, or reused from a cache still inside its
  /// interval; see `shouldCheck`) goes in as a parameter, and exactly one
  /// `UpdateOutcome` comes out. No clock, no IO, no exceptions: this
  /// function cannot itself go stale, hang, or lie.
  let decide
    (installKind: InstallKind)
    (optedOut: bool)
    (current: Version)
    (latestFetch: Result<Version, string>)
    : UpdateOutcome =
    let current = normalize current
    match installKind, optedOut with
    | InstallKind.LocalBuild, _ -> UpdateOutcome.CheckSkipped SkipReason.NotAToolInstall
    | InstallKind.ToolInstall, true -> UpdateOutcome.CheckSkipped SkipReason.OptedOut
    | InstallKind.ToolInstall, false ->
      match latestFetch with
      | Error reason -> UpdateOutcome.CheckFailed reason
      | Ok latestRaw ->
        let latest = normalize latestRaw
        match versionsBehind current latest with
        | 0 -> UpdateOutcome.UpToDate current
        | behind -> UpdateOutcome.UpdateAvailable(current, latest, behind)

  /// The exact remediation text for an install kind — the daemon-restart
  /// case names the one caveat that has already cost real time (`dotnet
  /// tool update` resolving through NuGet's search index, which lags the
  /// package store by minutes — issue #136's own author hit it shipping
  /// 0.6.822): `--version X.Y.Z` gets past that.
  let remediation (latest: Version) : string =
    sprintf
      "Restart the SageFs daemon. If it's installed as a global tool: dotnet tool update -g sagefs (or, if that reports \"already installed\" because the search index hasn't caught up yet, dotnet tool update -g sagefs --version %s)."
      (latest.ToString())

  /// A one-line, user- and agent-facing rendering. `None` for every outcome
  /// that must never nag — `UpToDate`, every `CheckSkipped` reason, and
  /// `CheckFailed` (a failed check is not evidence of staleness and must
  /// stay silent, not read as a warning).
  let describe (outcome: UpdateOutcome) : string option =
    match outcome with
    | UpdateOutcome.UpdateAvailable(current, latest, behind) ->
      let plural = if behind = 1 then "version" else "versions"
      Some(
        sprintf
          "This SageFs daemon is running %s, %d %s behind the latest published %s. %s"
          (current.ToString())
          behind
          plural
          (latest.ToString())
          (remediation latest))
    | UpdateOutcome.UpToDate _
    | UpdateOutcome.CheckSkipped _
    | UpdateOutcome.CheckFailed _ -> None
