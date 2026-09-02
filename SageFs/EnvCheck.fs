/// `sagefs check` — environment pre-flight checks.
/// All pure helper functions are testable without I/O side-effects.
module EnvCheck

open System
open System.IO
open System.Net.Http
open System.Net
open System.Net.Sockets
open System.Text.Json
open SageFs.Server

[<RequireQualifiedAccess>]
type Status = | Pass | Warn | Fail

type SessionAuthoritySession = {
  Id: string
  WorkingDirectory: string
  Status: string
}

type SessionAuthority =
  | NoMatchingSession
  | ExactMatch of SessionAuthoritySession
  | Ambiguous of SessionAuthoritySession list

type CheckResult = {
  Icon:   string
  Label:  string
  Status: Status
  Detail: string
  Hint:   string option
}

let private pass label detail   = { Icon = "✓"; Label = label; Status = Status.Pass; Detail = detail; Hint = None }
let private warn label detail h = { Icon = "⚠"; Label = label; Status = Status.Warn; Detail = detail; Hint = Some h }
let private fail label detail h = { Icon = "✗"; Label = label; Status = Status.Fail; Detail = detail; Hint = Some h }

let private normalizeWorkingDirectory (dir: string) =
  let fullPath =
    try Path.GetFullPath dir
    with _ -> dir
  let trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
  match OperatingSystem.IsWindows() with
  | true -> trimmed.Replace('/', '\\').ToLowerInvariant()
  | false -> trimmed.Replace('\\', '/')

/// True when the given TCP port is not in use on loopback.
let isPortFree (port: int) =
  try
    use l = new TcpListener(IPAddress.Loopback, port)
    l.Start()
    l.Stop()
    true
  with _ -> false

/// Non-recursive .fsproj discovery in a directory.
let findFsproj (dir: string) =
  try Directory.GetFiles(dir, "*.fsproj", SearchOption.TopDirectoryOnly) |> Array.toList
  with _ -> []

/// The .NET SDK selection required by a global.json `sdk` section.
type SdkRequirement = {
  Version: string
  RollForward: string
  AllowPrerelease: bool
}

/// Outcome of probing `dotnet fsi` availability.
type FsiProbeOutcome =
  | FsiStartedAndExited
  | FsiTimedOutAndKilled
  | FsiFailedToStart of error: string

/// Parse one `dotnet --list-sdks` line ("<version> [<install path>]") into its version.
let tryParseSdkListLine (line: string) =
  let bracket = line.IndexOf('[')
  let raw = if bracket > 0 then line.Substring(0, bracket) else line
  let trimmed = raw.Trim()
  if String.IsNullOrEmpty trimmed then None else Some trimmed

/// Parse a global.json document into the sdk requirement it pins. Returns
/// Ok None when the document has no sdk.version (no pin). Follows the CLI
/// resolver defaults: rollForward "patch", allowPrerelease true.
let parseGlobalJsonSdkRequirement (json: string) =
  try
    use doc = JsonDocument.Parse(json)
    let root = doc.RootElement
    match root.TryGetProperty("sdk") with
    | true, sdk when sdk.ValueKind = JsonValueKind.Object ->
      let version =
        match sdk.TryGetProperty("version") with
        | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
        | _ -> None
      let rollForward =
        match sdk.TryGetProperty("rollForward") with
        | true, rf when rf.ValueKind = JsonValueKind.String -> rf.GetString()
        | _ -> "patch"
      let allowPrerelease =
        match sdk.TryGetProperty("allowPrerelease") with
        | true, ap when ap.ValueKind = JsonValueKind.False -> false
        | _ -> true
      match version with
      | Some version -> Ok (Some { Version = version; RollForward = rollForward; AllowPrerelease = allowPrerelease })
      | None -> Ok None
    | _ -> Ok None
  with ex -> Error ex.Message

let private tryParseSdkVersion (v: string) =
  let dash = v.IndexOf('-')
  let releasePart = if dash >= 0 then v.Substring(0, dash) else v
  let prerelease = if dash >= 0 then Some (v.Substring(dash + 1)) else None
  let releaseNums =
    releasePart.Split('.')
    |> Array.toList
    |> List.map (fun s -> match Int32.TryParse(s.Trim()) with true, n -> n | _ -> -1)
  match releaseNums |> List.forall (fun n -> n >= 0) with
  | false -> None
  | true -> Some (releaseNums, prerelease)

let private padTo (n: int) (xs: int list) = xs @ List.replicate (max 0 (n - xs.Length)) 0

let rec private compareNumList (xs: int list) (ys: int list) =
  match xs, ys with
  | [], [] -> 0
  | x :: xr, y :: yr ->
    let c = compare x y
    if c <> 0 then c else compareNumList xr yr
  | [], _ -> -1
  | _, [] -> 1

/// Compare prerelease suffixes ("preview.7" vs "preview.7.26381.103"):
/// numeric tokens compare numerically, other tokens ordinally; a shorter
/// suffix that is an exact prefix of a longer one sorts below it.
let private comparePreParts (a: string) (b: string) =
  let rec loop (xs: string list) (ys: string list) =
    match xs, ys with
    | [], [] -> 0
    | [], _ -> -1
    | _, [] -> 1
    | x :: xr, y :: yr ->
      let c =
        match Int32.TryParse x, Int32.TryParse y with
        | (true, nx), (true, ny) -> compare nx ny
        | _ -> String.CompareOrdinal(x, y)
      if c <> 0 then c else loop xr yr
  loop (a.Split('.') |> Array.toList) (b.Split('.') |> Array.toList)

/// True when candidate >= pin under SDK ordering (stable > prerelease at the
/// same release tuple; a prerelease extends the pin's prerelease prefix).
let private sdkVersionAtLeast (pin: string) (cand: string) =
  match tryParseSdkVersion pin, tryParseSdkVersion cand with
  | Some (pinRel, pinPre), Some (candRel, candPre) ->
    let relCmp = compareNumList (padTo 4 pinRel) (padTo 4 candRel)
    match relCmp with
    | c when c > 0 -> false
    | c when c < 0 -> true
    | _ ->
      match pinPre, candPre with
      | None, None -> true
      | Some _, None -> true
      | None, Some _ -> false
      | Some p, Some c -> comparePreParts p c <= 0
  | _ -> false

let private sdkVersionEquals (a: string) (b: string) =
  match tryParseSdkVersion a, tryParseSdkVersion b with
  | Some (aRel, aPre), Some (bRel, bPre) ->
    compareNumList (padTo 4 aRel) (padTo 4 bRel) = 0
    && match aPre, bPre with
       | None, None -> true
       | Some p, Some q -> comparePreParts p q = 0
       | _ -> false
  | _ -> false

/// True when the first n release components of a and b are identical
/// (e.g. n=3: same feature band 11.0.1xx; n=1: same major).
let private sameReleasePrefix (n: int) (a: string) (b: string) =
  match tryParseSdkVersion a, tryParseSdkVersion b with
  | Some (aRel, _), Some (bRel, _) ->
    compareNumList (padTo n (List.truncate n aRel)) (padTo n (List.truncate n bRel)) = 0
  | _ -> false

/// global.json sdk.version must be a full version such as 11.0.100 (with an
/// optional prerelease suffix); "11" or "11.0" are not valid pins.
let private isValidSdkPin (pin: string) =
  Text.RegularExpressions.Regex.IsMatch(pin, @"^\d+(\.\d+){2,}(-[0-9A-Za-z.]+)?$")

/// Whether the installed SDK version satisfies the global.json pin under the
/// configured rollForward policy (mirrors `dotnet` SDK resolution semantics).
let private satisfiesRequirement (req: SdkRequirement) (installedVersion: string) =
  match isValidSdkPin req.Version with
  | false -> false
  | true ->
    match tryParseSdkVersion installedVersion with
    | None -> false
    | Some (_, candPre) ->
      let prereleaseOk = req.AllowPrerelease || Option.isNone candPre
      prereleaseOk
      && sdkVersionAtLeast req.Version installedVersion
      && match req.RollForward with
         | "disable" -> sdkVersionEquals req.Version installedVersion
         | "patch" | "latestPatch" -> sameReleasePrefix 3 req.Version installedVersion
         | "feature" | "latestFeature" -> sameReleasePrefix 2 req.Version installedVersion
         | "minor" | "latestMinor" -> sameReleasePrefix 1 req.Version installedVersion
         | "major" | "latestMajor" -> true
         | _ -> true

let private sdkDownloadUrlMajorMinor (pin: string) =
  match tryParseSdkVersion pin with
  | Some (major :: minor :: _, _) -> sprintf "%d.%d" major minor
  | Some (major :: _, _) -> sprintf "%d" major
  | _ -> "latest"

let private sdkInstallGuidance (req: SdkRequirement) =
  let url = sprintf "https://dotnet.microsoft.com/download/dotnet/%s" (sdkDownloadUrlMajorMinor req.Version)
  sprintf
    "Install the .NET SDK %s from %s (or any newer SDK the global.json rollForward '%s' accepts, e.g. `dotnet --list-sdks` after installing)."
    req.Version url req.RollForward

/// Core SDK check over injected inputs (no I/O):
///   requirement        - the global.json pin (None: no pin anywhere up-tree)
///   targetFrameworkMajor - major of the project target framework to build (None: unknown)
///   installedLines     - raw `dotnet --list-sdks` output lines
let sdkCheckFromInputs
  (requirement: SdkRequirement option)
  (targetFrameworkMajor: int option)
  (installedLines: string list)
  =
  let installed = installedLines |> List.choose tryParseSdkListLine |> List.distinct
  match requirement with
  | None ->
    match installed with
    | [] ->
      fail ".NET SDK" "No .NET SDKs are installed"
            "Install the .NET SDK from https://dotnet.microsoft.com/download/dotnet"
    | versions ->
      let newest = versions |> List.sortWith (fun a b -> if sdkVersionAtLeast a b then -1 else 1) |> List.head
      pass ".NET SDK" (sprintf ".NET SDK %s installed" newest)
  | Some req ->
    match isValidSdkPin req.Version with
    | false ->
      fail ".NET SDK"
           (sprintf "global.json sdk.version '%s' is not a valid SDK version (expected e.g. 11.0.100)" req.Version)
           (sprintf "Fix the sdk.version in global.json to a full version such as 11.0.100, or remove it to use the latest installed SDK (%s)."
                    (match installed with v :: _ -> v | [] -> "none installed"))
    | true ->
      let eligible =
        installed
        |> List.filter (fun v ->
          match tryParseSdkVersion v with
          | Some (_, pre) -> req.AllowPrerelease || Option.isNone pre
          | None -> false)
      match eligible with
      | [] ->
        fail ".NET SDK"
             (sprintf "No installed .NET SDK satisfies global.json (pin %s, rollForward %s)" req.Version req.RollForward)
             (sdkInstallGuidance req)
      | candidates ->
        let selected = candidates |> List.sortWith (fun a b -> if sdkVersionAtLeast a b then -1 else 1) |> List.head
        let satisfied = satisfiesRequirement req selected
        match satisfied with
        | false ->
          fail ".NET SDK"
               (sprintf "Required .NET SDK %s (global.json, rollForward %s) is not installed; newest eligible is %s" req.Version req.RollForward selected)
               (sdkInstallGuidance req)
        | true ->
          match targetFrameworkMajor with
          | Some tfmMajor ->
            let selectedMajor =
              match tryParseSdkVersion selected with
              | Some (major :: _, _) -> major
              | _ -> 0
            match tfmMajor > selectedMajor with
            | true ->
              fail ".NET SDK"
                   (sprintf "Selected .NET SDK %s cannot build target framework net%d.0" selected tfmMajor)
                   (sprintf "Install a .NET %d SDK (https://dotnet.microsoft.com/download/dotnet/%d) or align the project's TargetFramework with the SDK your global.json selects." tfmMajor tfmMajor)
            | false ->
              pass ".NET SDK"
                   (sprintf ".NET SDK %s installed - global.json pin %s satisfied" selected req.Version)
          | None ->
            pass ".NET SDK"
                 (sprintf ".NET SDK %s installed - global.json pin %s satisfied" selected req.Version)

/// Locate the highest TargetFramework major (net<major>.<minor>) referenced by
/// the projects/build props reachable upward from the working directory.
let private targetFrameworkMajorAt (dir: string) =
  let netRx = Text.RegularExpressions.Regex("net(\\d+)\\.\\d+")
  let rec collect (dir: string) (acc: int list) =
    let read file =
      let path = Path.Combine(dir, file)
      if File.Exists path then
        try
          [ for m in netRx.Matches(File.ReadAllText path) do
              match Int32.TryParse m.Groups.[1].Value with
              | true, major -> yield major
              | _ -> () ]
        with _ -> []
      else []
    let here =
      read "Directory.Build.props"
      @ (findFsproj dir |> List.collect read)
    match here with
    | _ :: _ -> acc @ here
    | [] ->
      let parent = Directory.GetParent(dir)
      if isNull parent then acc else collect parent.FullName acc
  match (try collect dir [] with _ -> []) with
  | [] -> None
  | majors -> Some (List.max majors)

let checkDotnetSdk () =
  try
    // Like the .NET SDK resolver: search from the current directory upward.
    let requirement =
      let rec findUp (dir: string) =
        let candidate = Path.Combine(dir, "global.json")
        if File.Exists candidate then Some candidate
        else
          let parent = Directory.GetParent(dir)
          if isNull parent then None else findUp parent.FullName
      findUp Environment.CurrentDirectory
      |> Option.bind (fun path ->
        try
          match File.ReadAllText path |> parseGlobalJsonSdkRequirement with
          | Ok req -> req
          | Error _ -> None
        with _ -> None)
    let installedLines =
      let psi = Diagnostics.ProcessStartInfo("dotnet", "--list-sdks")
      psi.RedirectStandardOutput <- true
      psi.RedirectStandardError <- true
      psi.UseShellExecute <- false
      psi.CreateNoWindow <- true
      use proc = Diagnostics.Process.Start(psi)
      proc.StandardOutput.ReadToEnd()
      |> _.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.toList
    let tfmMajor = targetFrameworkMajorAt Environment.CurrentDirectory
    sdkCheckFromInputs requirement tfmMajor installedLines
  with ex ->
    fail ".NET SDK" "Could not determine installed .NET SDKs" (sprintf "Run `dotnet --list-sdks` to inspect your installation: %s" ex.Message)

/// Classify a `dotnet fsi` availability probe outcome into a check result.
/// A probe process that had to be killed on timeout is a FAILURE — an fsi that
/// hangs on startup is not usable, and silently passing would hide a broken
/// SDK install.
let checkFsiFromProbe (outcome: FsiProbeOutcome) =
  match outcome with
  | FsiStartedAndExited -> pass "F# Interactive" "dotnet fsi available"
  | FsiTimedOutAndKilled ->
    fail "F# Interactive" "dotnet fsi did not start within 3s (probe process killed)"
          "dotnet fsi hung on startup — repair or reinstall the .NET SDK, and make sure `dotnet` resolves to a healthy installation."
  | FsiFailedToStart error ->
    fail "F# Interactive" "dotnet fsi not found" (sprintf "Ensure the .NET SDK is installed and `dotnet` is on your PATH: %s" error)

let checkFsiAvailable () =
  try
    let psi = Diagnostics.ProcessStartInfo("dotnet", "fsi --nologo --exec")
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use proc = Diagnostics.Process.Start(psi)
    let exited = proc.WaitForExit(3000)
    match exited with
    | true -> checkFsiFromProbe FsiStartedAndExited
    | false ->
      try proc.Kill()
      with _ -> ()
      checkFsiFromProbe FsiTimedOutAndKilled
  with ex ->
    checkFsiFromProbe (FsiFailedToStart ex.Message)

let checkFsproj (dir: string) =
  match findFsproj dir with
  | [] ->
    warn ".fsproj files" (sprintf "None found in %s" dir)
          "Start `sagefs`, then create a session for the project you want to load from your client or the dashboard"
  | files ->
    let names = files |> List.map Path.GetFileName |> String.concat ", "
    pass ".fsproj files" (sprintf "%d found: %s" files.Length names)

let checkPort (label: string) (port: int) =
  match isPortFree port with
  | true  -> pass label (sprintf "Port %d available" port)
  | false ->
    fail label (sprintf "Port %d is already in use" port)
          (sprintf "Another process is using port %d. Use --mcp-port (or --dash-port) to choose a different port, or stop the conflicting process." port)

let checkDaemon (mcpPort: int) =
  match DaemonState.readOnPort mcpPort with
  | Some info ->
    warn "SageFs daemon"
         (sprintf "Already running (PID %d, port %d, started %s)" info.Pid info.Port (info.StartedAt.ToString("HH:mm:ss")))
         "A daemon is already running. The new start will be a no-op or conflict. Run `sagefs stop` first if you want a fresh start."
  | None ->
    pass "SageFs daemon" "No daemon running — ready to start"

let classifySessionAuthority (targetDir: string) (sessions: SessionAuthoritySession list) =
  let target = normalizeWorkingDirectory targetDir
  let matches =
    sessions
    |> List.filter (fun session -> normalizeWorkingDirectory session.WorkingDirectory = target)

  match matches with
  | [] -> NoMatchingSession
  | [ session ] -> ExactMatch session
  | many -> Ambiguous many

let checkSessionAuthority (targetDir: string) (sessions: SessionAuthoritySession list option) =
  let label = "Target session authority"
  match sessions with
  | None ->
    warn label
      (sprintf "Not checked for %s — no daemon running" targetDir)
      "Start SageFs, then rerun `sagefs check` to verify daemon session authority for this target."
  | Some daemonSessions ->
    match classifySessionAuthority targetDir daemonSessions with
    | NoMatchingSession ->
      warn label
        (sprintf "No matching session for %s on the running daemon" targetDir)
        "Create or switch to a session rooted at this target if you expect daemon-backed tooling here."
    | ExactMatch session when String.Equals(session.Status, "Ready", StringComparison.OrdinalIgnoreCase) ->
      pass label
        (sprintf "1 matching session for %s (%s, %s)" targetDir session.Id session.Status)
    | ExactMatch session ->
      warn label
        (sprintf "1 matching session for %s (%s, %s)" targetDir session.Id session.Status)
        "Session authority is unambiguous, but the matching session is not Ready yet."
    | Ambiguous matches ->
      let details =
        matches
        |> List.map (fun session -> sprintf "%s (%s)" session.Id session.Status)
        |> String.concat ", "
      warn label
        (sprintf "%d matching sessions for %s: %s" matches.Length targetDir details)
        "Session authority is ambiguous for this target. Specify the session explicitly or prune duplicates."

let private tryGetDaemonSessions (mcpPort: int) =
  try
    use client = new HttpClient(Timeout = TimeSpan.FromSeconds(3.0))
    let response = client.GetAsync(sprintf "http://localhost:%d/api/sessions" mcpPort).Result
    match response.IsSuccessStatusCode with
    | true ->
      let json = response.Content.ReadAsStringAsync().Result
      use doc = JsonDocument.Parse(json)
      let sessions =
        doc.RootElement.GetProperty("sessions").EnumerateArray()
        |> Seq.map (fun session ->
          { Id = session.GetProperty("id").GetString() |> Option.ofObj |> Option.defaultValue ""
            WorkingDirectory = session.GetProperty("workingDirectory").GetString() |> Option.ofObj |> Option.defaultValue ""
            Status = session.GetProperty("status").GetString() |> Option.ofObj |> Option.defaultValue "" })
        |> Seq.toList
      Ok sessions
    | false ->
      Error (sprintf "Daemon returned HTTP %d from /api/sessions" (int response.StatusCode))
  with ex ->
    Error ex.Message

let checkDaemonSessionAuthority (targetDir: string) (mcpPort: int) =
  match DaemonState.readOnPort mcpPort with
  | None ->
    checkSessionAuthority targetDir None
  | Some _ ->
    match tryGetDaemonSessions mcpPort with
    | Ok sessions ->
      checkSessionAuthority targetDir (Some sessions)
    | Error detail ->
      warn "Target session authority"
        (sprintf "Not checked for %s — could not inspect daemon sessions" targetDir)
        (sprintf "The daemon is running, but /api/sessions could not be read: %s" detail)

let runAll (dir: string) (mcpPort: int) (dashPort: int) =
  [ checkDotnetSdk ()
    checkFsiAvailable ()
    checkFsproj dir
    checkPort "MCP port"       mcpPort
    checkPort "Dashboard port" dashPort
    checkDaemon mcpPort
    checkDaemonSessionAuthority dir mcpPort ]

/// Print the check results to stdout. Returns the count of failures (for exit code).
let print (results: CheckResult list) =
  let w = 60
  printfn ""
  printfn "  SageFs Environment Check"
  printfn "  %s" (String.replicate w "═")
  for r in results do
    printfn "  %s  %s" r.Icon r.Detail
    match r.Hint with
    | Some h -> printfn "     └─ %s" h
    | None   -> ()
  printfn "  %s" (String.replicate w "─")
  let failures = results |> List.filter (fun r -> r.Status = Status.Fail)
  let warnings = results |> List.filter (fun r -> r.Status = Status.Warn)
  match failures, warnings with
  | [], [] ->
    printfn "  ✓  All requested checks passed for the checked target."
    printfn "     Local prerequisites look good, and daemon session authority is unambiguous and Ready."
  | [], ws ->
    printfn "  ⚠  %d warning(s) — local prerequisites may be fine, but review daemon/target authority details above." ws.Length
  | fs, _ ->
    printfn "  ✗  %d check(s) failed — fix the issues above before running SageFs." fs.Length
  printfn ""
  failures.Length
