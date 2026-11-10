namespace SageFs.Features

open System
open System.IO
open System.Security.Cryptography
open System.Text
open SageFs

/// Where a host's diagnostics socket lives, and keeping it where `dotnet-trace` can find it.
///
/// The runtime creates its diagnostics socket at `<TMPDIR>/dotnet-diagnostic-<pid>-<key>-socket` (`/tmp` when TMPDIR is unset),
/// and a Unix socket path holds 107 characters. A longer path is cut short by the runtime, so the socket is not named the way
/// `dotnet-trace` looks for it, and the profile tool cannot attach: `dotnet-trace` answers "The total length of the diagnostic
/// socket path may exceed 108 characters". Found 2026-11-10: the release gate gives each tier a TMPDIR of 85 characters
/// (`~/.local/share/sagefs-gate/checkout-<id>.tiers/integration-host-4of5.tmp`), so every host in it had a truncated socket name,
/// where a 59 character directory worked (measured: a 75 character TMPDIR made the socket name end after 107 characters).
///
/// A host is started with its TMPDIR as it was, spelled through a short link when the real spelling is too long. The link is
/// the same directory, so a file the user's code writes to its temp directory lands where it did before, and only the path the
/// code reads from `Path.GetTempPath()` is shorter.
module DiagnosticsSocket =
  /// What a Unix socket path may hold, not counting its terminating NUL (`sun_path` is 108 bytes).
  let maxPathLength = 107

  /// The longest name the runtime gives its socket: a pid of up to seven digits (the Linux maximum is 4194304) and a key of up to
  /// ten digits (the process start time).
  let longestName = "dotnet-diagnostic-4194304-4294967295-socket"

  /// The longest directory that still holds that socket name.
  let longestTemp = maxPathLength - longestName.Length - 1

  /// Whether a socket in `tmp` keeps its whole name.
  let fits (tmp: string) : bool = tmp.TrimEnd('/').Length <= longestTemp

  [<RequireQualifiedAccess>]
  type TempPlan =
    /// The directory is short enough as it is.
    | KeepTemp
    /// Spell it as `link`, a short path to `target`.
    | UseShortLink of link: string * target: string
    /// It is too long and no place for a short link has room. `longest` is the most a directory may be.
    | TempTooLong of tmp: string * longest: int

  /// The name of the link for a directory: the same directory always gets the same name.
  let linkName (target: string) : string =
    let hash = SHA256.HashData(Encoding.UTF8.GetBytes target) |> Convert.ToHexString
    "sf-" + hash.Substring(0, 8).ToLowerInvariant()

  /// What to do about a host's temp directory, given the directories a short link could be made in.
  let plan (tmp: string) (bases: string list) : TempPlan =
    match fits tmp with
    | true -> TempPlan.KeepTemp
    | false ->
      let linkIn (parent: string) = parent.TrimEnd('/') + "/" + linkName tmp
      match bases |> List.map linkIn |> List.tryFind fits with
      | Some link -> TempPlan.UseShortLink(link, tmp)
      | None -> TempPlan.TempTooLong(tmp, longestTemp)

  /// A short path and the directory it stands for.
  type ShortLink = { Link: string; Target: string }

  /// Make `Link` a link to `Target`, or find that it already is one. False when something else is there or it cannot be made.
  let makeLink (shortLink: ShortLink) : bool =
    let isLinkToTarget () =
      match Guarded.attempt (fun () -> Directory.ResolveLinkTarget(shortLink.Link, false)) with
      | Guarded.Attempt.Completed null -> false
      | Guarded.Attempt.Completed existing -> existing.FullName.TrimEnd('/') = shortLink.Target.TrimEnd('/')
      | Guarded.Attempt.Threw _ -> false
    match isLinkToTarget () with
    | true -> true
    | false ->
      match Guarded.attempt (fun () -> Directory.CreateSymbolicLink(shortLink.Link, shortLink.Target) |> ignore) with
      | Guarded.Attempt.Completed() -> true
      // Another host started at the same moment may have made it first: that is as good.
      | Guarded.Attempt.Threw _ -> isLinkToTarget ()

  /// The environment a host needs so its socket keeps its name: TMPDIR spelled through a short link when `tmp` is too long, and
  /// nothing when it fits or when no link can be made (the profile tool then refuses by name).
  let environmentFor (bases: string list) (tmp: string) : (string * string) list =
    match plan tmp bases with
    | TempPlan.KeepTemp -> []
    | TempPlan.TempTooLong _ -> []
    | TempPlan.UseShortLink(link, target) ->
      match makeLink { Link = link; Target = target } with
      | true -> [ "TMPDIR", link ]
      | false -> []

  /// The directory a process uses, from its NUL separated environment: TMPDIR, or `/tmp` when it is unset or empty.
  let tempOfEnvironment (environment: string) : string =
    environment.Split('\000')
    |> Array.tryPick (fun entry ->
      match entry.StartsWith("TMPDIR=", StringComparison.Ordinal) with
      | true -> Some(entry.Substring 7)
      | false -> None)
    |> Option.filter (fun value -> value <> "")
    |> Option.defaultValue "/tmp"

  /// The temp directory of a running host, read the way `dotnet-trace` reads it. None where the environment cannot be read.
  let hostTempOf (pid: int) : string option =
    match Guarded.attempt (fun () -> File.ReadAllText(sprintf "/proc/%d/environ" pid)) with
    | Guarded.Attempt.Completed text -> Some(tempOfEnvironment text)
    | Guarded.Attempt.Threw _ -> None

  /// Why a host that is already running cannot be attached to, when its directory is too long for its socket.
  let refusalFor (pid: int) : ProfileFailure option =
    match OperatingSystem.IsLinux(), hostTempOf pid with
    | true, Some tmp when not (fits tmp) -> Some(ProfileFailure.SocketPathTooLong(tmp, longestTemp))
    | _ -> None

  /// Where a short link may be made, shortest-named places first. Only those that exist.
  let defaultBases () : string list =
    [ "/tmp"; "/dev/shm"; Environment.GetEnvironmentVariable "XDG_RUNTIME_DIR"; "/var/tmp" ]
    |> List.filter (fun parent -> not (String.IsNullOrEmpty parent) && Directory.Exists parent)

  /// The environment overrides for a host about to start, from the directory this process has. Linux only: that is where the
  /// socket path is a file path.
  let hostEnvironment () : (string * string) list =
    match OperatingSystem.IsLinux() with
    | false -> []
    | true ->
      let tmp =
        match Environment.GetEnvironmentVariable "TMPDIR" with
        | null
        | "" -> "/tmp"
        | value -> value
      environmentFor (defaultBases ()) tmp
