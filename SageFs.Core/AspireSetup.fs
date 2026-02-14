module SageFs.AspireSetup

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open SageFs.Utils

type LaunchProfile = {
  CommandName: string
  LaunchBrowser: bool
  EnvironmentVariables: Map<string, string>
  ApplicationUrl: string option
}

type LaunchSettings = {
  Profiles: Map<string, LaunchProfile>
}

let private findLatestVersion (packagePath: string) =
  if Directory.Exists(packagePath) then
    Directory.GetDirectories(packagePath)
    |> Array.map Path.GetFileName
    |> Array.filter (fun v -> v.Contains("."))
    |> Array.sortDescending
    |> Array.tryHead
  else
    None

let private getRidSuffix () =
  if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "win-x64"
  elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then "linux-x64"
  elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then "osx-x64"
  else "win-x64" // default

let private getDcpExecutableName () =
  if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "dcp.exe"
  else "dcp"

let private loadLaunchSettings (projectDir: string) : LaunchSettings option =
  try
    let launchSettingsPath = Path.Combine(projectDir, "Properties", "launchSettings.json")
    if File.Exists(launchSettingsPath) then
      let json = File.ReadAllText(launchSettingsPath)
      let doc = JsonDocument.Parse(json)
      
      let profiles = 
        if doc.RootElement.TryGetProperty("profiles") |> fst then
          let profilesElement = doc.RootElement.GetProperty("profiles")
          profilesElement.EnumerateObject()
          |> Seq.map (fun prop ->
            let profileName = prop.Name
            let profile = prop.Value
            
            let envVars = 
              if profile.TryGetProperty("environmentVariables") |> fst then
                let envElement = profile.GetProperty("environmentVariables")
                envElement.EnumerateObject()
                |> Seq.map (fun envProp -> envProp.Name, envProp.Value.GetString())
                |> Map.ofSeq
              else
                Map.empty
            
            let appUrl = 
              if profile.TryGetProperty("applicationUrl") |> fst then
                Some (profile.GetProperty("applicationUrl").GetString())
              else
                None
            
            let commandName = 
              if profile.TryGetProperty("commandName") |> fst then
                profile.GetProperty("commandName").GetString()
              else
                "Project"
            
            let launchBrowser =
              if profile.TryGetProperty("launchBrowser") |> fst then
                profile.GetProperty("launchBrowser").GetBoolean()
              else
                false
            
            profileName, {
              CommandName = commandName
              LaunchBrowser = launchBrowser
              EnvironmentVariables = envVars
              ApplicationUrl = appUrl
            })
          |> Map.ofSeq
        else
          Map.empty
      
      Some { Profiles = profiles }
    else
      None
  with ex ->
    None

let private setupAspirePaths (logger: ILogger) =
  try
    let nugetPackages = 
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
    
    let rid = getRidSuffix()
    let dcpExeName = getDcpExecutableName()
    
    // Find DCP (Distributed Control Plane) path
    let dcpPackagePath = Path.Combine(nugetPackages, $"aspire.hosting.orchestration.{rid}")
    match findLatestVersion dcpPackagePath with
    | Some version ->
        let dcpExePath = Path.Combine(dcpPackagePath, version, "tools", dcpExeName)
        if File.Exists(dcpExePath) then
          Environment.SetEnvironmentVariable("DcpPublisherSettings__CliPath", dcpExePath)
          Environment.SetEnvironmentVariable("SageFs_ASPIRE_DCP_PATH", dcpExePath)
          logger.LogInfo $"Aspire DCP: {dcpExePath}"
        else
          logger.LogDebug $"DCP executable not found at: {dcpExePath}"
    | None ->
        logger.LogDebug $"Aspire DCP package not found at: {dcpPackagePath}"
    
    // Find Dashboard path
    let dashboardPackagePath = Path.Combine(nugetPackages, $"aspire.dashboard.sdk.{rid}")
    match findLatestVersion dashboardPackagePath with
    | Some version ->
        // Set the path to the actual DLL, not just the tools directory
        let dashboardDllPath = Path.Combine(dashboardPackagePath, version, "tools", "Aspire.Dashboard.dll")
        if File.Exists(dashboardDllPath) then
          Environment.SetEnvironmentVariable("DcpPublisherSettings__DashboardPath", dashboardDllPath)
          Environment.SetEnvironmentVariable("SageFs_ASPIRE_DASHBOARD_PATH", dashboardDllPath)
          logger.LogInfo $"Aspire Dashboard: {dashboardDllPath}"
        else
          logger.LogDebug $"Dashboard DLL not found at: {dashboardDllPath}"
    | None ->
        logger.LogDebug $"Aspire Dashboard package not found at: {dashboardPackagePath}"
    
    // Prevent duplicate endpoints by disabling launchSettings URL loading for child projects
    Environment.SetEnvironmentVariable("ASPNETCORE_SUPPRESS_LAUNCH_PROFILE_URLS", "true")
    logger.LogDebug "Suppressed launchSettings applicationUrl to prevent endpoint conflicts"
    
    // Enable hot reload for Aspire-launched projects via .NET Hot Reload
    Environment.SetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES", "debug")
    logger.LogDebug "Enabled .NET Hot Reload for Aspire project resources"
    
  with ex ->
    logger.LogDebug $"Error setting up Aspire paths: {ex.Message}"

let private applyLaunchProfile (logger: ILogger) (profile: LaunchProfile) (projectDir: string) =
  logger.LogInfo "Applying Aspire launch profile configuration..."
  
  // Apply all environment variables from the profile
  for KeyValue(key, value) in profile.EnvironmentVariables do
    Environment.SetEnvironmentVariable(key, value)
    logger.LogDebug $"  {key}={value}"
  
  // Set application URL if present
  match profile.ApplicationUrl with
  | Some url ->
      Environment.SetEnvironmentVariable("ASPNETCORE_URLS", url)
      logger.LogInfo $"Application URL: {url}"
  | None -> ()

let private hasAspireReferences (projects: ProjectLoading.Solution) =
  // Check both command-line references and project package references
  let hasInCommandLineRefs =
    projects.References
    |> List.exists (fun ref -> ref.Contains("Aspire.Hosting", StringComparison.OrdinalIgnoreCase))
  
  let hasInProjectRefs =
    projects.Projects
    |> Seq.exists (fun proj -> 
      proj.PackageReferences
      |> Seq.exists (fun pkgRef -> pkgRef.Name.Contains("Aspire.Hosting", StringComparison.OrdinalIgnoreCase)))
  
  hasInCommandLineRefs || hasInProjectRefs

let configureAspireIfNeeded (logger: ILogger) (solution: ProjectLoading.Solution) =
  if hasAspireReferences solution then
    logger.LogWarning "⚠️  Aspire AppHost project detected"
    logger.LogWarning "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    logger.LogWarning "Hot reload will NOT work for Aspire-orchestrated services!"
    logger.LogWarning ""
    logger.LogInfo "Aspire services run as separate processes, not in the FSI session."
    logger.LogInfo "For hot reload, load your F# web project directly instead:"
    logger.LogInfo "  ✅ SageFs --proj YourWebProject.fsproj"
    logger.LogInfo "  ❌ SageFs --proj AppHost.fsproj (limited functionality)"
    logger.LogWarning "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    logger.LogInfo ""
    logger.LogInfo "🚀 Configuring Aspire AppHost (non-blocking execution enabled)..."
    
    // Setup DCP and Dashboard paths
    setupAspirePaths logger
    
    // Try to load and apply launch settings from the first project
    match solution.Projects |> List.tryHead with
    | Some primaryProject ->
        let projectDir = Path.GetDirectoryName(primaryProject.ProjectFileName)
        match loadLaunchSettings projectDir with
        | Some launchSettings ->
            // Prefer "http" profile for development, fallback to first profile
            let profile = 
              launchSettings.Profiles 
              |> Map.tryFind "http"
              |> Option.orElseWith (fun () -> launchSettings.Profiles |> Map.toSeq |> Seq.tryHead |> Option.map snd)
            
            match profile with
            | Some p ->
                applyLaunchProfile logger p projectDir
                logger.LogInfo "✓ Aspire configured successfully"
            | None ->
                logger.LogWarning "No launch profiles found in launchSettings.json"
        | None ->
            logger.LogDebug "No launchSettings.json found, using basic Aspire configuration"
    | None -> ()
  else
    ()
