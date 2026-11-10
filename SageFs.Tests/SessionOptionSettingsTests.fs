module SageFs.Tests.SessionOptionSettingsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Tests.TestInfrastructure

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-optset-" + Guid.NewGuid().ToString "N")
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally try Directory.Delete(dir, true) with :? IOException -> ()

let private pathsIn (root: string) : ConfigPaths =
  let globalDir = Path.Combine(root, "global")
  Directory.CreateDirectory globalDir |> ignore
  { GlobalDir = globalDir; Repo = RepoRootAt root }

let private options (edit: SessionOptions.Options -> SessionOptions.Options) : SessionOptions.Options = edit SessionOptions.none

let private genPlainName : Gen<string> =
  Gen.elements [ 'A' .. 'Z' ] |> Gen.listOfLength 6 |> Gen.map (fun letters -> "USER_" + String(List.toArray letters))

let private genOptions : Gen<SessionOptions.Options> =
  gen {
    let! gc = Gen.elements GcMode.all
    let! names = Gen.listOf genPlainName
    let! artifacts = Gen.oneof [ Gen.constant ProjectChooses; genPlainName |> Gen.map (fun n -> ArtifactsIn("/tmp/" + n)) ]
    return { SessionOptions.none with Gc = gc; Env = names |> List.distinct |> List.map (fun n -> EnvEntry(n, "v")); Artifacts = artifacts }
  }

let private render (node: XmlNode) : string = Net.WebUtility.HtmlDecode(renderNode node)

[<Tests>]
let rowTests =
  testList "Session options as settings rows" [

    testCase "WHY - the garbage collector row takes exactly the collectors' names, ignoring case, and refuses anything else naming the choices" <| fun _ ->
      let d = SessionOptionSettings.gcSetting
      for choice in GcMode.all do
        for spelling in [ GcMode.name choice; (GcMode.name choice).ToUpperInvariant() ] do
          match d.Parse spelling with
          | Ok value -> d.Render value |> Expect.equal "renders as the one spelling" (GcMode.name choice)
          | Error why -> failtestf "%s should parse: %s" spelling (ConfigError.describe why)
      match d.Parse "Turbo" with
      | Ok value -> failtestf "'Turbo' is not a collector, got %A" value
      | Error why ->
        let text = ConfigError.describe why
        for choice in GcMode.all do
          text |> Expect.stringContains "lists the valid names" (GcMode.name choice)

    testProperty "WHY - an environment row reads back what it wrote, in order, so the page never shows a different list than was saved"
      (Prop.forAll (Arb.fromGen genOptions) (fun generated ->
        let d = SessionOptionSettings.environmentSetting
        let text = SessionOptions.renderEnvText generated.Env
        match d.Parse text with
        | Ok value -> d.Render value = text
        | Error _ -> false))

    testCase "WHY - an environment row refuses a poisoned name, a missing equals sign and an empty name, naming every one with its next step" <| fun _ ->
      match SessionOptionSettings.environmentSetting.Parse "OK=1;DOTNET_ROOT=/x;oops;=y" with
      | Ok value -> failtestf "should be refused, got %A" value
      | Error why ->
        let text = ConfigError.describe why
        text |> Expect.stringContains "names the poisoned variable" "DOTNET_ROOT"
        text |> Expect.stringContains "names the entry with no equals sign" "'oops'"
        text |> Expect.stringContains "says the empty name" "empty name"
        text |> Expect.stringContains "gives a next step" "→"

    testCase "WHY - the artifacts row takes an empty value as the project's own choice, an absolute folder as itself, and refuses a relative one with its reason" <| fun _ ->
      let d = SessionOptionSettings.artifactsSetting
      let absolute = Path.Combine(Path.GetTempPath(), "optset-art")
      d.Parse "" |> Result.map d.Render |> Expect.equal "empty stays empty" (Ok "")
      d.Parse absolute |> Result.map d.Render |> Expect.equal "absolute stays" (Ok absolute)
      match d.Parse "out/art" with
      | Ok value -> failtestf "a relative path must be refused, got %A" value
      | Error why ->
        ConfigError.describe why |> Expect.stringContains "says why" "not absolute"
        ConfigError.describe why |> Expect.stringContains "says what to do" "→"

    testCase "WHY - every option row is a next-session row of the repo layer, so a save never claims to change a running session" <| fun _ ->
      for d in SessionOptionSettings.all do
        d.Applicability |> Expect.equal (sprintf "%s applies to the next session" d.Key) NextSession
        d.Scope |> Expect.equal (sprintf "%s may be set per repo" d.Key) RepoOverridable
        d.Category |> Expect.equal (sprintf "%s is in the Session group" d.Key) SessionSettings
      SessionOptionSettings.all |> List.map (fun d -> d.Key) |> List.distinct |> List.length |> Expect.equal "keys are distinct" 3

    testCase "WHY - a repo's saved options reach a new session over the global ones, and an unset option stays the default" <| fun _ ->
      withTempDir (fun root ->
        let paths = pathsIn root
        let saved layer (descriptor: SettingDescriptor) raw =
          match SettingsCatalog.edit paths layer raw descriptor with
          | Ok _ -> ()
          | Error why -> failtestf "%s should save: %s" descriptor.Key (ConfigError.describe why)
        saved LGlobal SessionOptionSettings.gcSetting "Server"
        saved LGlobal SessionOptionSettings.environmentSetting "FROM_GLOBAL=1"
        saved LRepo SessionOptionSettings.environmentSetting "FROM_REPO=2"
        let read, refusals = SessionOptionSettings.optionsAt paths
        refusals |> Expect.isEmpty "nothing refused"
        read.Gc |> Expect.equal "the global collector" ServerGc
        read.Env |> Expect.equal "the repo's environment wins over the global" [ EnvEntry("FROM_REPO", "2") ]
        read.Artifacts |> Expect.equal "unset stays the project's" ProjectChooses)

    testCase "WHY - a refused edit saves nothing and says why where the person is looking" <| fun _ ->
      withTempDir (fun root ->
        let paths = pathsIn root
        SettingsCatalog.edit paths LRepo "Turbo" SessionOptionSettings.gcSetting |> Expect.isError "refused"
        SettingsCatalog.edit paths LRepo "DOTNET_ROOT=/x" SessionOptionSettings.environmentSetting |> Expect.isError "refused"
        (SessionOptionSettings.optionsAt paths |> fst) |> Expect.equal "nothing was saved" SessionOptions.none)

    testCase "WHY - a saved value that cannot be read back is named and the default stands, so a damaged file does not stop a session" <| fun _ ->
      withTempDir (fun root ->
        let paths = pathsIn root
        SettingsStore.setKey (SettingsStore.repoPath root) SessionOptionSettings.GcKey "Turbo" |> ignore
        let read, refusals = SessionOptionSettings.optionsAt paths
        read.Gc |> Expect.equal "the default collector" GcMode.standard
        match refusals with
        | [ SessionOptions.SettingUnreadable(SessionOptions.GcOption, reason) ] ->
          SessionOptions.describe (SessionOptions.SettingUnreadable(SessionOptions.GcOption, reason)) |> Expect.stringContains "says what to do" "→"
        | other -> failtestf "expected one unreadable-setting refusal, got %A" other)
  ]

[<Tests>]
let resolutionTests =
  testList "Session options: which source wins, and the project's own ArtifactsPath" [

    testProperty "WHY - config.fsx wins option by option and the settings fill the rest, so an option is never lost between the two"
      (Prop.forAll (Arb.fromGen (Gen.zip genOptions genOptions)) (fun (settings, file) ->
        let resolved = SessionOptions.overlay settings file []
        let expectedGc = if file.Gc <> WorkstationGc then file.Gc else settings.Gc
        let expectedEnv = if not file.Env.IsEmpty then file.Env else settings.Env
        let expectedArtifacts = if file.Artifacts <> ProjectChooses then file.Artifacts else settings.Artifacts
        resolved.Options.Gc = expectedGc && resolved.Options.Env = expectedEnv && resolved.Options.Artifacts = expectedArtifacts))

    testCase "WHY - each option names where it came from, so a row can say the file won" <| fun _ ->
      let settings = options (fun o -> { o with Gc = ServerGc; Env = [ EnvEntry("S", "1") ] })
      let file = options (fun o -> { o with Env = [ EnvEntry("F", "2") ] })
      let resolved = SessionOptions.overlay settings file []
      SessionOptions.sourceOf resolved SessionOptions.GcOption |> Expect.equal "gc from the settings" SessionOptions.FromSettings
      SessionOptions.sourceOf resolved SessionOptions.EnvOption |> Expect.equal "env from the file" SessionOptions.FromConfigFile
      SessionOptions.sourceOf resolved SessionOptions.ArtifactsOption |> Expect.equal "artifacts from neither" SessionOptions.FromNothing

    testCase "WHY - a project that sets ArtifactsPath keeps it, the session's request is dropped, and the reason names both folders and what to do" <| fun _ ->
      let asked = options (fun o -> { o with Artifacts = ArtifactsIn "/tmp/mine" })
      let kept, refusals = SessionOptions.reconcileArtifacts (SessionOptions.ProjectSetsArtifacts "/repo/artifacts") asked
      kept.Artifacts |> Expect.equal "the build and the load both go where the project says" ProjectChooses
      match refusals with
      | [ SessionOptions.ArtifactsClaimedByProject("/tmp/mine", "/repo/artifacts") as refusal ] ->
        let text = SessionOptions.describe refusal
        text |> Expect.stringContains "names the session's folder" "/tmp/mine"
        text |> Expect.stringContains "names the project's folder" "/repo/artifacts"
        text |> Expect.stringContains "says why the project wins" "shared with your team and CI"
        text |> Expect.stringContains "gives the next step" "→"
        SessionOptions.optionOf refusal |> Expect.equal "about the artifacts option" SessionOptions.ArtifactsOption
      | other -> failtestf "expected the claim refusal, got %A" other

    testCase "WHY - the same folder is no conflict, and a project that leaves it alone keeps the session's request" <| fun _ ->
      let asked = options (fun o -> { o with Artifacts = ArtifactsIn "/tmp/same" })
      SessionOptions.reconcileArtifacts (SessionOptions.ProjectSetsArtifacts "/tmp/same/") asked |> Expect.equal "equal paths" (asked, [])
      SessionOptions.reconcileArtifacts SessionOptions.ProjectLeavesArtifacts asked |> Expect.equal "project leaves it" (asked, [])
      SessionOptions.reconcileArtifacts (SessionOptions.ProjectSetsArtifacts "/x") SessionOptions.none |> Expect.equal "no request, nothing to refuse" (SessionOptions.none, [])

    testCase "WHY - MSBuild's answer about ArtifactsPath is read as set or left alone, and a blank line is left alone" <| fun _ ->
      ProjectArtifactsProbe.parseAnswer "\n" |> Expect.equal "blank" SessionOptions.ProjectLeavesArtifacts
      ProjectArtifactsProbe.parseAnswer "" |> Expect.equal "empty" SessionOptions.ProjectLeavesArtifacts
      ProjectArtifactsProbe.parseAnswer "/repo/artifacts\n" |> Expect.equal "a path" (SessionOptions.ProjectSetsArtifacts "/repo/artifacts")

    testCase "WHY - an answer MSBuild could not give is reported and leaves the choice alone, and only a project file is asked" <| fun _ ->
      let claim, notes = ProjectArtifactsProbe.claimOf (fun _ -> Error(ProbeFailure "no SDK")) [ "/r/App.fsproj" ]
      claim |> Expect.equal "no claim" SessionOptions.ProjectLeavesArtifacts
      notes |> Expect.hasLength "one note" 1
      notes.Head |> Expect.stringContains "names the project" "/r/App.fsproj"
      let asked = ref 0
      let neverAsked (_: string) = asked.Value <- asked.Value + 1; Ok SessionOptions.ProjectLeavesArtifacts
      ProjectArtifactsProbe.claimOf neverAsked [ "/r/App.slnx" ] |> ignore
      asked.Value |> Expect.equal "a solution is not asked" 0

    testCase "WHY - preparing a session with an artifacts setting and a project that sets its own drops the request, keeps the reason, and the row says so" <| fun _ ->
      withTempDir (fun root ->
        let paths = pathsIn root
        SettingsCatalog.edit paths LRepo (Path.Combine(root, "mine")) SessionOptionSettings.artifactsSetting |> Expect.isOk "saved"
        let claimed _ = SessionOptions.ProjectSetsArtifacts "/repo/artifacts", []
        DirectoryConfig.prepareSession (SettingsCatalog.pathsFor paths.GlobalDir root) claimed [ Path.Combine(root, "App.fsproj") ] root |> Expect.isTrue "auto-open stays on"
        try
          SessionOptionsRegistry.optionsFor root |> Expect.equal "the registry holds the project's choice" SessionOptions.none
          let resolution = SessionOptionsRegistry.resolutionFor root
          let notes = SessionOptionSettings.notesFor resolution SessionOptionSettings.ArtifactsKey
          notes |> Expect.hasLength "one sentence under the artifacts row" 1
          notes.Head |> Expect.stringContains "names the project's folder" "/repo/artifacts"
          SessionOptionSettings.notesFor resolution SessionOptionSettings.GcKey |> Expect.isEmpty "nothing under the collector row"
          let row = SettingsPanel.rowFor ExternalInputs.none paths SessionOptionSettings.artifactsSetting |> SettingsPanel.withNotes notes
          render (SettingsPanel.renderPanel SettingsPanel.Quiet paths.Repo [ row ]) |> Expect.stringContains "the page shows the reason" "shared with your team and CI"
        finally
          SessionOptionsRegistry.remember root SessionOptions.none)

    testCase "WHY - preparing a session asks about the project only when an artifacts path was requested" <| fun _ ->
      withTempDir (fun root ->
        let paths = pathsIn root
        let asked = ref 0
        let counting _ = asked.Value <- asked.Value + 1; SessionOptions.ProjectLeavesArtifacts, []
        DirectoryConfig.prepareSession (SettingsCatalog.pathsFor paths.GlobalDir root) counting [ "App.fsproj" ] root |> ignore
        asked.Value |> Expect.equal "nothing requested, nothing asked" 0
        SettingsCatalog.edit paths LGlobal (Path.Combine(root, "art")) SessionOptionSettings.artifactsSetting |> Expect.isOk "saved"
        try
          DirectoryConfig.prepareSession (SettingsCatalog.pathsFor paths.GlobalDir root) counting [ "App.fsproj" ] root |> ignore
          asked.Value |> Expect.equal "requested, asked once" 1
        finally
          SessionOptionsRegistry.remember root SessionOptions.none)

    testCase "WHY - a row says what config.fsx set last and nothing for a directory with no session yet" <| fun _ ->
      let file = options (fun o -> { o with Gc = ServerGc })
      let resolved = SessionOptions.overlay SessionOptions.none file []
      SessionOptionSettings.notesFor (Some resolved) SessionOptionSettings.GcKey |> Expect.hasLength "gc came from the file" 1
      SessionOptionSettings.notesFor (Some resolved) SessionOptionSettings.EnvironmentKey |> Expect.isEmpty "env came from nothing"
      SessionOptionSettings.notesFor None SessionOptionSettings.GcKey |> Expect.isEmpty "no session yet"
      SessionOptionSettings.notesFor (Some resolved) "livetest.perTestTimeoutSeconds" |> Expect.isEmpty "not an option row"
  ]

[<Tests>]
let perSessionTests =
  testList "Session options: a session keeps the options it started with" [

    testCase "WHY - a later create in the same directory does not change what a running session reads for its restart, its worker or its rebuild" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "optset-held-" + Guid.NewGuid().ToString "N")
      let projectOne = Path.Combine(dir, "One.fsproj")
      let projectTwo = Path.Combine(dir, "Two.fsproj")
      let heldOne = WorkerProtocol.SessionId.newId ()
      let heldTwo = WorkerProtocol.SessionId.newId ()
      let first = options (fun o -> { o with Gc = ServerGc; Artifacts = ArtifactsIn "/tmp/optset-first" })
      let second = options (fun o -> { o with Env = [ EnvEntry("SECOND", "1") ]; Artifacts = ArtifactsIn "/tmp/optset-second" })
      try
        SessionOptionsRegistry.remember dir first
        // The first session's worker is spawned: its options are fixed here.
        SessionOptionsRegistry.optionsOfSession heldOne [ projectOne ] dir |> Expect.equal "first spawn" first
        SessionOptionsRegistry.remember dir second
        SessionOptionsRegistry.optionsOfSession heldTwo [ projectTwo ] dir |> Expect.equal "the second session starts with the second" second
        SessionOptionsRegistry.optionsOfSession heldOne [ projectOne ] dir |> Expect.equal "the first session still has the first, on its restart" first
        SessionOptionsRegistry.workerEnvironmentOf heldOne [ projectOne ] dir
        |> Expect.equal "the respawned worker gets the first session's carrier" (SessionOptions.workerEnvironment first)
        SessionOptionsRegistry.forBuild [ projectOne ] dir |> Expect.equal "the first session's rebuild" first
        SessionOptionsRegistry.forBuild [ projectTwo ] dir |> Expect.equal "the second session's rebuild" second
        let argumentsOf (project: string) =
          (SessionBuild.buildStartInfoFor (fun _ -> null) [ project ] [ "build" ] dir).ArgumentList |> Seq.toList
        argumentsOf projectOne |> Expect.contains "the first session's build goes to its own folder" "-p:ArtifactsPath=/tmp/optset-first"
        argumentsOf projectTwo |> Expect.contains "the second session's build goes to its own folder" "-p:ArtifactsPath=/tmp/optset-second"
      finally
        SessionOptionsRegistry.release heldOne
        SessionOptionsRegistry.release heldTwo
        SessionOptionsRegistry.remember dir SessionOptions.none

    testProperty "WHY - however many sessions are created in one directory, each reads its own options on every later ask"
      (Prop.forAll (Arb.fromGen (Gen.nonEmptyListOf genOptions)) (fun created ->
        let dir = Path.Combine(Path.GetTempPath(), "optset-prop-" + Guid.NewGuid().ToString "N")
        let ids = created |> List.map (fun _ -> WorkerProtocol.SessionId.newId ())
        try
          for id, requested in List.zip ids created do
            SessionOptionsRegistry.remember dir requested
            SessionOptionsRegistry.optionsOfSession id [ WorkerProtocol.SessionId.value id + ".fsproj" ] dir |> ignore
          List.zip ids created
          |> List.forall (fun (id, requested) -> SessionOptionsRegistry.optionsOfSession id [ WorkerProtocol.SessionId.value id + ".fsproj" ] dir = requested)
        finally
          ids |> List.iter SessionOptionsRegistry.release
          SessionOptionsRegistry.remember dir SessionOptions.none))

    testCase "WHY - a stopped session's options are released, so a build with that session's projects falls back to what is pending" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "optset-release-" + Guid.NewGuid().ToString "N")
      let project = Path.Combine(dir, "App.fsproj")
      let releaseOne = WorkerProtocol.SessionId.newId ()
      let held = options (fun o -> { o with Gc = ServerGc })
      try
        SessionOptionsRegistry.remember dir held
        SessionOptionsRegistry.optionsOfSession releaseOne [ project ] dir |> ignore
        SessionOptionsRegistry.remember dir SessionOptions.none
        SessionOptionsRegistry.forBuild [ project ] dir |> Expect.equal "held while it runs" held
        SessionOptionsRegistry.release releaseOne
        SessionOptionsRegistry.forBuild [ project ] dir |> Expect.equal "pending after it stops" SessionOptions.none
      finally
        SessionOptionsRegistry.release releaseOne

    testCase "WHY - a project path is the same key whether it is written relative or absolute, so a rebuild finds its session" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "optset-key-" + Guid.NewGuid().ToString "N")
      let keyOne = WorkerProtocol.SessionId.newId ()
      let held = options (fun o -> { o with Gc = ServerGc })
      try
        SessionOptionsRegistry.remember dir held
        SessionOptionsRegistry.optionsOfSession keyOne [ "App.fsproj" ] dir |> ignore
        SessionOptionsRegistry.remember dir SessionOptions.none
        SessionOptionsRegistry.forBuild [ Path.Combine(dir, "App.fsproj") ] dir |> Expect.equal "found by the absolute path" held
      finally
        SessionOptionsRegistry.release keyOne
  ]

[<Tests>]
let preflightTests =
  testList "Session options: the build preflight looks where the session builds" [

    testCase "WHY - with an artifacts path the files that prove a build are looked for under it, and the project's own obj is not asked for" <| fun _ ->
      let project = Path.Combine(Path.GetTempPath(), "optset-pre", "App.fsproj")
      let artifacts = Path.Combine(Path.GetTempPath(), "optset-pre-art")
      let present = set [ project; Path.Combine(artifacts, "obj", "App", "project.assets.json"); Path.Combine(artifacts, "obj", "App", "App.fsproj.nuget.g.props") ]
      let exists (path: string) = present.Contains path
      let read (_: string) = "<Project />"
      BuildPreflight.checkUnder (ArtifactsIn artifacts) exists read (SessionProjectTarget.Project project)
      |> Expect.equal "built under the artifacts path is ready" BuildPreflight.Ready
      BuildPreflight.checkUnder ProjectChooses exists read (SessionProjectTarget.Project project)
      |> function
        | BuildPreflight.NeedsRebuild missing -> missing |> Expect.isNonEmpty "the project's own obj is what is missing"
        | other -> failtestf "without the setting the project's obj is checked, got %A" other

    testCase "WHY - a project not yet built under the artifacts path needs a rebuild, and the missing files are named under it" <| fun _ ->
      let project = Path.Combine(Path.GetTempPath(), "optset-pre2", "App.fsproj")
      let artifacts = Path.Combine(Path.GetTempPath(), "optset-pre2-art")
      let exists (path: string) = path = project
      match BuildPreflight.checkUnder (ArtifactsIn artifacts) exists (fun _ -> "<Project />") (SessionProjectTarget.Project project) with
      | BuildPreflight.NeedsRebuild missing ->
        missing |> Expect.equal "both restore outputs, under the artifacts path"
          [ Path.Combine(artifacts, "obj", "App", "project.assets.json"); Path.Combine(artifacts, "obj", "App", "App.fsproj.nuget.g.props") ]
      | other -> failtestf "expected NeedsRebuild, got %A" other
  ]

[<Tests>]
let switchTests =
  testList "Session options: the artifacts path as an MSBuild switch" [

    testProperty "WHY - a path survives the switch's escaping, so a folder with a comma, a semicolon or a percent sign is read back whole"
      (Prop.forAll (Arb.fromGen (Gen.elements [ "/tmp/a b"; "/tmp/a;b"; "/tmp/a,b"; "/tmp/100%"; "/tmp/%3B"; "/tmp/a;b,c%d" ])) (fun path ->
        let argument = SessionOptions.buildArguments (options (fun o -> { o with Artifacts = ArtifactsIn path })) |> List.exactlyOne
        let value = argument.Substring("-p:ArtifactsPath=".Length)
        // What MSBuild does with the switch value: %XX is a byte, and ';' and ',' split properties, so none may be left raw.
        not (value.Contains ";") && not (value.Contains ",") && Uri.UnescapeDataString value = path))

    testCase "WHY - the build arguments name no property when the options ask for none" <| fun _ ->
      SessionOptions.buildArguments SessionOptions.none |> Expect.isEmpty "none"
      SessionOptions.argumentsFromEnvironment (fun _ -> null) |> Expect.isEmpty "no carrier, no arguments"

    testCase "WHY - the new settings kind and chip are wired: a next-session row draws its chip word, its effect line and the new-session promise" <| fun _ ->
      withTempDir (fun root ->
        let paths = pathsIn root
        let html = render (SettingsPanel.renderPanel SettingsPanel.Quiet paths.Repo (SessionOptionSettings.all |> List.map (SettingsPanel.rowFor ExternalInputs.none paths)))
        html |> Expect.stringContains "chip word" ">next session<"
        html |> Expect.stringContains "chip class" "settings-chip-next-session"
        html |> Expect.stringContains "effect line" "a running session keeps what it started with"
        for d in SessionOptionSettings.all do
          html |> Expect.stringContains (sprintf "%s has a row" d.Key) d.Name)
  ]

/// The pieces that need a real MSBuild: what it answers about a project's own ArtifactsPath, how it reads the switch, and that
/// a project's own file and the session's request end in one folder.
[<Tests>]
let realMsBuildTests =
  let smallProject (directory: string) =
    Directory.CreateDirectory directory |> ignore
    File.WriteAllText(Path.Combine(directory, "Small.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Lib.fs\" /></ItemGroup></Project>")
    File.WriteAllText(Path.Combine(directory, "Lib.fs"), "module Lib\nlet answer = 42\n")
    Path.Combine(directory, "Small.fsproj")
  let runBuild (project: string) (directory: string) =
    let psi = SessionBuild.buildStartInfo [ "build"; project; "-nodeReuse:false" ] directory
    ProcessEnvironment.applyTo psi []
    use proc = Diagnostics.Process.Start psi
    let output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode |> Expect.equal (sprintf "the build succeeds: %s" output) 0
  Integration.hostList "Session options with real MSBuild" [

    testCase "WHY - a project with no ArtifactsPath of its own is asked and leaves the choice, and one whose Directory.Build.props sets it is asked and claims it" <| fun _ ->
      withTempDir (fun root ->
        let plain = smallProject (Path.Combine(root, "plain"))
        ProjectArtifactsProbe.ask plain |> Expect.equal "plain project" (Ok SessionOptions.ProjectLeavesArtifacts)
        let claiming = smallProject (Path.Combine(root, "claiming"))
        let propsFolder = Path.Combine(root, "claiming", "propsart")
        File.WriteAllText(Path.Combine(root, "claiming", "Directory.Build.props"), sprintf "<Project><PropertyGroup><ArtifactsPath>%s</ArtifactsPath></PropertyGroup></Project>" propsFolder)
        ProjectArtifactsProbe.ask claiming |> Expect.equal "claiming project" (Ok(SessionOptions.ProjectSetsArtifacts propsFolder))
        let useOutput = smallProject (Path.Combine(root, "useoutput"))
        File.WriteAllText(Path.Combine(root, "useoutput", "Directory.Build.props"), "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>")
        match ProjectArtifactsProbe.ask useOutput with
        | Ok(SessionOptions.ProjectSetsArtifacts path) -> path |> Expect.stringContains "UseArtifactsOutput counts as the project's choice" "artifacts"
        | other -> failtestf "UseArtifactsOutput should claim the default artifacts folder, got %A" other)

    testCase "WHY - a project that sets ArtifactsPath in its own props keeps it: the session's request is refused with the reason, and the build writes where the project says" <| fun _ ->
      withTempDir (fun root ->
        let directory = Path.Combine(root, "app")
        let project = smallProject directory
        let propsFolder = Path.Combine(directory, "propsart")
        let sessionFolder = Path.Combine(root, "sessionart")
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), sprintf "<Project><PropertyGroup><ArtifactsPath>%s</ArtifactsPath></PropertyGroup></Project>" propsFolder)
        let paths = pathsIn root
        SettingsCatalog.edit { paths with Repo = RepoRootAt directory } LRepo sessionFolder SessionOptionSettings.artifactsSetting |> Expect.isOk "saved"
        try
          DirectoryConfig.prepareSession (SettingsCatalog.pathsFor paths.GlobalDir directory) (ProjectArtifactsProbe.claimOf ProjectArtifactsProbe.ask) [ project ] directory |> ignore
          let resolution = SessionOptionsRegistry.resolutionFor directory |> Option.get
          resolution.Refusals |> List.map SessionOptions.optionOf |> Expect.equal "the artifacts request was refused" [ SessionOptions.ArtifactsOption ]
          runBuild project directory
          Directory.GetFiles(propsFolder, "Small.dll", SearchOption.AllDirectories) |> Expect.isNonEmpty "the output is where the project says"
          Directory.Exists sessionFolder |> Expect.isFalse "nothing was written under the session's folder"
        finally
          SessionOptionsRegistry.remember directory SessionOptions.none)

    testCase "WHY - a session's artifacts path with a comma and a semicolon in it is read by MSBuild as one folder" <| fun _ ->
      withTempDir (fun root ->
        let directory = Path.Combine(root, "app")
        let project = smallProject directory
        let odd = Path.Combine(root, "art;with,odd 100%")
        try
          SessionOptionsRegistry.remember directory (options (fun o -> { o with Artifacts = ArtifactsIn odd }))
          runBuild project directory
          Directory.GetFiles(odd, "Small.dll", SearchOption.AllDirectories) |> Expect.isNonEmpty "the output is under the odd folder"
        finally
          SessionOptionsRegistry.remember directory SessionOptions.none)
  ]

[<Tests>]
let hostOutputTests =
  testList "Session options: the host finds the build where the session wrote it" [

    testCase "WHY - with an artifacts path the project's output folder is looked for under it, newest build first, and not under the project's bin" <| fun _ ->
      let project = "/repo/App/App.fsproj"
      let artifacts = "/art"
      let looked = ResizeArray<string>()
      let matching (dir: string) (_: string) =
        looked.Add dir
        [ "/art/bin/App/debug/App.dll"; "/art/bin/App/release/App.dll" ]
      let writeTime (path: string) = if path.Contains "release" then DateTime(2026, 1, 2) else DateTime(2026, 1, 1)
      IsolatedFsiSession.primaryProjectOutputDirWithin (ArtifactsIn artifacts) (fun _ -> true) matching writeTime [ project ]
      |> Expect.equal "the newest build under the artifacts path" (Some "/art/bin/App/release")
      looked |> Seq.toList |> Expect.equal "only the artifacts path was searched" [ "/art/bin/App" ]

    testCase "WHY - without an artifacts path the project's own bin is searched, as before" <| fun _ ->
      let looked = ResizeArray<string>()
      let matching (dir: string) (_: string) =
        looked.Add dir
        [ "/repo/App/bin/Debug/net11.0/App.dll" ]
      IsolatedFsiSession.primaryProjectOutputDirWithin ProjectChooses (fun _ -> true) matching (fun _ -> DateTime.UtcNow) [ "/repo/App/App.fsproj" ]
      |> Expect.equal "the project's bin" (Some "/repo/App/bin/Debug/net11.0")
      looked |> Seq.toList |> Expect.equal "searched the project's bin" [ "/repo/App/bin" ]

    testCase "WHY - with an artifacts path a project's restore record is found under it, so a package's native library is still located" <| fun _ ->
      let record = "/art/obj/App/project.assets.json"
      IsolatedFsiSession.projectAssetsFilesWithin (ArtifactsIn "/art") (fun path -> path = record) [ "/repo/App/App.fsproj" ]
      |> Expect.equal "the record under the artifacts path" [ record ]
      IsolatedFsiSession.projectAssetsFilesWithin ProjectChooses (fun path -> path = record) [ "/repo/App/App.fsproj" ]
      |> Expect.isEmpty "without the setting that folder is not looked in"
  ]
