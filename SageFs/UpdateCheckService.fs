namespace SageFs

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

/// The impure adapter for issue #136 ("tell users and agents when they are
/// running a stale SageFs"). Everything decidable is in `SageFs.UpdateCheck`
/// (pure, property-tested); this module supplies the three facts that
/// module can't know on its own — the clock, the on-disk cache, and one
/// bounded GET against NuGet's flat container — and nothing else. Any
/// failure here (offline, timeout, garbled JSON, an unwritable cache dir)
/// is swallowed into `UpdateOutcome.CheckFailed`; this module must never
/// throw into a caller, never block daemon startup, and never slow a
/// session.
[<RequireQualifiedAccess>]
module UpdateCheckService =

  let private nugetIndexUrl = "https://api.nuget.org/v3-flatcontainer/sagefs/index.json"

  let private httpClient = new HttpClient(Timeout = Timeouts.updateCheckFetch)

  let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

  /// On-disk shape of `<SageFsDir>/update-check.json`. Plain primitives only
  /// (no `option`, no `DateTimeOffset`) so this round-trips through the
  /// default `JsonSerializer` with no F#-aware converter required — `0L` /
  /// `""` are the "nothing yet" values, read back via `toLastChecked`/
  /// `toLatest` below. NOT `private`: System.Text.Json's reflection-based
  /// deserializer only matches a PUBLIC constructor for an immutable record
  /// like this one — a `private` type's auto-generated constructor is
  /// private too, which `Deserialize<CacheFile>` would then silently fail
  /// to find, and `readCache`'s fail-safe `with _ -> emptyCache` would
  /// swallow that into "no cache exists" forever, on every read, after
  /// every write (caught by this file's own tests: dismissing a version
  /// appeared to work, but a follow-up read never saw it).
  type CacheFile = {
    LastCheckedUtcTicks: int64
    LastKnownLatest: string
    DismissedVersions: string[]
  }

  let private emptyCache : CacheFile =
    { LastCheckedUtcTicks = 0L; LastKnownLatest = ""; DismissedVersions = [||] }

  let private cachePath (sageFsDir: string) : string = Path.Combine(sageFsDir, "update-check.json")

  /// Fail-safe: a missing or malformed cache file resolves to `emptyCache`,
  /// never a throw — the same discipline `SettingsStore.readLayer` uses for
  /// its own layer files.
  let private readCache (sageFsDir: string) : CacheFile =
    try
      let path = cachePath sageFsDir
      match File.Exists path with
      | false -> emptyCache
      | true ->
        match JsonSerializer.Deserialize<CacheFile>(File.ReadAllText path, jsonOptions) with
        | cache when obj.ReferenceEquals(cache, null) -> emptyCache
        | cache -> cache
    with _ -> emptyCache

  /// Atomic (tmp + move) and best-effort: a failed write never blocks or
  /// throws — worst case, the daemon simply re-checks NuGet again sooner
  /// than `Timeouts.updateCheckInterval` intends.
  let private writeCache (sageFsDir: string) (cache: CacheFile) : unit =
    try
      Directory.CreateDirectory sageFsDir |> ignore
      let target = cachePath sageFsDir
      let tmp = target + ".tmp"
      File.WriteAllText(tmp, JsonSerializer.Serialize(cache, jsonOptions))
      File.Move(tmp, target, true)
    with _ -> ()

  let private toLastChecked (cache: CacheFile) : DateTimeOffset option =
    match cache.LastCheckedUtcTicks with
    | 0L -> None
    | ticks -> Some(DateTimeOffset(ticks, TimeSpan.Zero))

  let private toLatest (cache: CacheFile) : Version option = UpdateCheck.tryParseVersion cache.LastKnownLatest

  /// `dotnet tool install -g` always lays its launcher here.
  let private dotnetToolsDir =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools")

  /// Reads THIS process's own executable path. Correct for the daemon (it
  /// answers for itself); for the CLI's one-shot `evaluateForCli` below it
  /// is an assumption — that the same `sagefs` launcher that ran this CLI
  /// invocation is the one that would also run the daemon on this machine,
  /// true for every install path this repo ships (the tool package and a
  /// local build each produce exactly one `sagefs` entry point).
  let private installKind () : InstallKind =
    InstallKind.classify (Environment.ProcessPath |> Option.ofObj) dotnetToolsDir

  let private optOutEnvVar = "SAGEFS_UPDATE_CHECK_DISABLED"

  let private optedOut () : bool =
    match Environment.GetEnvironmentVariable optOutEnvVar with
    | null | "" -> false
    | v -> not (v = "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase))

  /// This daemon's own running version, read the same way the rest of the
  /// codebase already does (`SageFsVersion.current`, which prefers
  /// `AssemblyInformationalVersion`), then parsed for comparison.
  let private ownVersion () : Version option =
    SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current ()
    |> UpdateCheck.tryParseVersion

  /// One bounded GET against NuGet's flat container. Every failure mode —
  /// offline, DNS, a timeout (`Timeouts.updateCheckFetch`), a non-JSON body,
  /// a response with no parseable versions — becomes `Error`, never an
  /// exception that escapes this function.
  let private fetchLatestVersion () : Task<Result<Version, string>> =
    task {
      try
        let! json = httpClient.GetStringAsync nugetIndexUrl
        use doc = JsonDocument.Parse json
        match doc.RootElement.TryGetProperty "versions" with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
          let versions =
            arr.EnumerateArray()
            |> Seq.choose (fun el ->
              match el.ValueKind with
              | JsonValueKind.String -> UpdateCheck.tryParseVersion (el.GetString())
              | _ -> None)
            |> Seq.toList
          match versions with
          | [] -> return Error "NuGet's flat container returned no parseable version"
          | vs -> return Ok(vs |> List.max)
        | _ -> return Error "NuGet's flat container response had no 'versions' array"
      with
      | :? TaskCanceledException -> return Error "timed out contacting NuGet"
      | :? HttpRequestException as ex -> return Error(sprintf "could not reach NuGet: %s" ex.Message)
      | :? JsonException as ex -> return Error(sprintf "NuGet's response was not valid JSON: %s" ex.Message)
      | ex -> return Error(sprintf "update check failed: %s" ex.Message)
    }

  let private lockObj = obj ()
  let mutable private current : UpdateOutcome = UpdateOutcome.CheckSkipped SkipReason.TooSoonSinceLastCheck

  /// The last outcome `checkOnceAsync` computed, read with no IO — this is
  /// what `get_fsi_status` and `/health` call on every single request, so it
  /// must stay a cheap in-memory read (agents call `get_fsi_status`
  /// constantly; this must never itself become a reason a session is slow).
  let currentOutcome () : UpdateOutcome = lock lockObj (fun () -> current)

  let private setCurrent (outcome: UpdateOutcome) : UpdateOutcome =
    lock lockObj (fun () -> current <- outcome)
    outcome

  /// Runs one full check and updates `currentOutcome`. Reuses the on-disk
  /// cache when it is still inside `Timeouts.updateCheckInterval`
  /// (`UpdateCheck.shouldCheck`); otherwise asks NuGet once, bounded by
  /// `Timeouts.updateCheckFetch`. A local build or an opted-out install
  /// never touches the network or the cache at all — see
  /// `InstallKind`/`SkipReason` on why those must never even attempt a
  /// check, let alone report one. Safe to call from daemon startup (fire
  /// and forget) and from a periodic background loop; never throws.
  let checkOnceAsync (sageFsDir: string) : Task<UpdateOutcome> =
    task {
      match ownVersion () with
      | None ->
        return setCurrent (UpdateOutcome.CheckFailed "could not parse this daemon's own running version")
      | Some ver ->
        let kind = installKind ()
        let optOut = optedOut ()
        match kind, optOut with
        | InstallKind.LocalBuild, _ ->
          return setCurrent (UpdateCheck.decide kind optOut ver (Error "not checked: local build"))
        | InstallKind.ToolInstall, true ->
          return setCurrent (UpdateCheck.decide kind optOut ver (Error "not checked: opted out"))
        | InstallKind.ToolInstall, false ->
          let cache = readCache sageFsDir
          let now = DateTimeOffset.UtcNow
          let due = UpdateCheck.shouldCheck now (toLastChecked cache) Timeouts.updateCheckInterval
          let! fetchResult =
            match due with
            | true ->
              task {
                let! result = fetchLatestVersion ()
                let updated =
                  match result with
                  | Ok latest -> { cache with LastCheckedUtcTicks = now.UtcTicks; LastKnownLatest = latest.ToString() }
                  | Error _ -> { cache with LastCheckedUtcTicks = now.UtcTicks }
                writeCache sageFsDir updated
                return result
              }
            | false ->
              Task.FromResult(
                match toLatest cache with
                | Some latest -> Ok latest
                | None -> Error "no successful update check yet")
          return setCurrent (UpdateCheck.decide kind optOut ver fetchResult)
    }

  /// The CLI's one-shot equivalent: `sagefs status` runs as a separate
  /// process from the daemon it's reporting on, so it never hits the
  /// network itself — it just reads the cache the daemon's own periodic
  /// check already maintains and decides from that, given the version the
  /// daemon reported (over `/api/daemon-info`, already fetched by the
  /// caller). A cache that has never seen a successful check yet (e.g. the
  /// daemon started seconds ago) reports `CheckFailed`, never a fabricated
  /// "up to date".
  let evaluateForCli (sageFsDir: string) (daemonVersionText: string) : UpdateOutcome =
    match UpdateCheck.tryParseVersion daemonVersionText with
    | None -> UpdateOutcome.CheckFailed "could not parse the daemon's reported version"
    | Some ver ->
      let kind = installKind ()
      let optOut = optedOut ()
      match kind, optOut with
      | InstallKind.LocalBuild, _ -> UpdateCheck.decide kind optOut ver (Error "not checked: local build")
      | InstallKind.ToolInstall, true -> UpdateCheck.decide kind optOut ver (Error "not checked: opted out")
      | InstallKind.ToolInstall, false ->
        match toLatest (readCache sageFsDir) with
        | Some latest -> UpdateCheck.decide kind optOut ver (Ok latest)
        | None -> UpdateCheck.decide kind optOut ver (Error "no update check has completed yet")

  /// Per-version dismiss for the dashboard banner (issue #136: "Dismissible
  /// per version, so dismissing 0.6.690 does not silence 0.6.750"). Stored
  /// alongside the rest of the cache; best-effort like every other write
  /// here.
  let dismissVersion (sageFsDir: string) (version: Version) : unit =
    let text = version.ToString()
    let cache = readCache sageFsDir
    match cache.DismissedVersions |> Array.contains text with
    | true -> ()
    | false -> writeCache sageFsDir { cache with DismissedVersions = Array.append cache.DismissedVersions [| text |] }

  let isDismissed (sageFsDir: string) (version: Version) : bool =
    (readCache sageFsDir).DismissedVersions |> Array.contains (version.ToString())
