/// Hot-reload-reach-options.md (repo root) §1.4/§1.7 established that
/// `-p:Optimize=false` is not a style choice for the session build — it is
/// the one lever that closes two of the four hot-reload barriers
/// structurally: FSC's own Release inliner baking closures into the IL on
/// disk, and the CoreCLR JIT folding `initonly` static reads into constants
/// once the assembly is built optimized. `SessionBuild.buildArguments` used
/// to disable optimizations only by accident (no `-c` flag ⇒ default Debug
/// config ⇒ incidentally `Optimize=false`) — nothing recorded that this
/// mattered, and nothing would have failed if a future change added
/// `-c Release` for a perfectly sensible-sounding reason.
///
/// This suite is that "nothing" turned into something: a fast unit gate on
/// the argument list `SessionBuild.runBuildAsync` actually invokes `dotnet`
/// with, plus a real-build integration test proving the guarantee is
/// structural — a project's own `Directory.Build.props` cannot silently win.
module SageFs.Tests.SessionBuildOptimizationGateTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

[<Tests>]
let fastGateTests =
  testList "SessionBuild optimization gate" [

    testCase "WHY — buildArguments (fast, no-restore path) always disables optimizations — this is the argument list every cold-restart/hard-reset build actually runs, so a regression here silently degrades hot reload for every user" <| fun _ ->
      SessionBuild.buildArguments false "/src/Web/Web.fsproj"
      |> List.contains SessionBuild.optimizationDisablingProperty
      |> Expect.isTrue "the fast (--no-restore) rebuild path must carry -p:Optimize=false"

    testCase "WHY — buildArguments (restore path) also always disables optimizations — the self-heal retry after a failed no-restore build is not exempt from the same guarantee" <| fun _ ->
      SessionBuild.buildArguments true "/src/Web/Web.fsproj"
      |> List.contains SessionBuild.optimizationDisablingProperty
      |> Expect.isTrue "the restore rebuild path must carry -p:Optimize=false"

    testCase "WHY — the disabling property is spelled EXACTLY -p:Optimize=false — a typo or a different form (e.g. /p:Optimize:false) would pass MSBuild's CLI parser differently or not at all, silently defeating the whole guarantee" <| fun _ ->
      SessionBuild.optimizationDisablingProperty |> Expect.equal "exact MSBuild global-property syntax" "-p:Optimize=false"

    testCase "WHY — both build-argument shapes carry the SAME disabling-property value, not two independently-spelled copies that could drift apart" <| fun _ ->
      let fast = SessionBuild.buildArguments false "/x.fsproj"
      let restore = SessionBuild.buildArguments true "/x.fsproj"
      (fast |> List.filter ((=) SessionBuild.optimizationDisablingProperty),
       restore |> List.filter ((=) SessionBuild.optimizationDisablingProperty))
      |> Expect.equal "one property, used in both places" ([ SessionBuild.optimizationDisablingProperty ], [ SessionBuild.optimizationDisablingProperty ])
  ]

/// WHY — SageFs's own assembly has to reach a user's project for the holder API
/// (`Holder` / `HolderRegistry` / `RegisteredHolder`) to be usable at all, and
/// nothing about a user's .fsproj mentions it. The decision is a DU because the
/// cheap-direction mistake is invisible: inject a `Reference` to a file that is
/// not there and MSBuild blames a path the user never wrote.
[<Tests>]
let coreReferencePolicyTests =
  testList "SageFs injects its own assembly as a build reference" [

    testCase "WHY — a KNOWN assembly is Available, which is the only case that may inject a Reference" <| fun _ ->
      SessionBuild.decideCoreReference (Some "/opt/tools/net10.0/SageFs.Core.dll")
      |> Expect.equal "the exact path, carried through" (SessionBuild.CoreReference.Available "/opt/tools/net10.0/SageFs.Core.dll")

    testCase "WHY — a BLANK path is a refusal, not a path — MSBuild reads '' as the current directory and fails with a missing-assembly error at a location the user never wrote" <| fun _ ->
      match SessionBuild.decideCoreReference (Some "   ") with
      | SessionBuild.CoreReference.Available p -> failtestf "a blank path must never be Available, got '%s'" p
      | SessionBuild.CoreReference.Absent why -> (System.String.IsNullOrWhiteSpace why) |> Expect.isFalse "a refusal must say WHY, not just refuse"
      | SessionBuild.CoreReference.NotChecked -> failtest "it was asked, so this is Absent, not NotChecked"

    testCase "WHY — 'never looked' is NOT the same claim as 'looked and absent' — the second is evidence and the first is not, exactly like LiveCount" <| fun _ ->
      let notChecked = SessionBuild.decideCoreReference None
      let absent = SessionBuild.decideCoreReference (Some "")
      (match notChecked with
       | SessionBuild.CoreReference.NotChecked -> true
       | _ -> false)
      |> Expect.isTrue "None means nobody checked"
      // The two must be distinguishable, which a bool could never do.
      (absent = notChecked) |> Expect.isFalse "and they are not the same value"

    testCase "WHY — the property is CustomAfterMicrosoftCommonTargets, NOT ReferencePath — measured: ReferencePath only tells MSBuild where to SEARCH for references that already exist, so it can never ADD one" <| fun _ ->
      let property = SessionBuild.coreReferenceProperty "/tmp/sagefs-inject.targets"
      (property)
      |> Expect.equal "the exact MSBuild spelling that was verified to build a project with no Reference" "-p:CustomAfterMicrosoftCommonTargets=/tmp/sagefs-inject.targets"
      (property.Contains "ReferencePath")
      |> Expect.isFalse "the search-only property must never be used for this"

    testCase "WHY — a build that injects Core also says so, in a property NAMED for it, because a project that builds Core itself (the ConsoleTicker sample) has to yield to the injection and cannot tell from CustomAfterMicrosoftCommonTargets: the SDK gives that property a default of its own, so it is never empty" <| fun _ ->
      let args = SessionBuild.injectionArguments "/tmp/sagefs-inject.targets"
      args |> Expect.contains "the reference injection itself" (SessionBuild.coreReferenceProperty "/tmp/sagefs-inject.targets")
      args |> Expect.contains "and the marker that says SageFs is the one building" SessionBuild.managedBuildProperty
      SessionBuild.managedBuildProperty
      |> Expect.equal "exact MSBuild global-property syntax, the spelling a project's Condition reads" "-p:SageFsManagedBuild=true"

    testCase "WHY — every build argument list still carries the optimization flag — the reference injection must not displace it, since -p:Optimize=false is structurally load-bearing for hot reload" <| fun _ ->
      for restore in [ false; true ] do
        SessionBuild.buildArguments restore "/x.fsproj"
        |> List.contains SessionBuild.optimizationDisablingProperty
        |> Expect.isTrue (sprintf "restore=%b keeps -p:Optimize=false" restore)

    testCase "WHY — the generated .targets file is WELL-FORMED XML, because MSBuild rejects a malformed one with a parse error that names no recognisable cause" <| fun _ ->
      // Parsed for real, not string-matched. A path containing XML metacharacters
      // is the case that would break: & and < are legal in a filesystem path.
      for path in [ "/opt/tools/SageFs.Core.dll"; "/a&b/<c>/SageFs.Core.dll"; "C:\\Program Files\\SageFs\\SageFs.Core.dll" ] do
        let doc = System.Xml.XmlDocument()
        try
          doc.LoadXml (SessionBuild.coreReferenceTargetsContent path)
        with ex -> failtestf "the generated file must parse for path '%s': %s" path ex.Message
        doc.DocumentElement.Name |> Expect.equal "and it is a Project" "Project"
        // The escaped path must survive the round trip as the ORIGINAL text,
        // not the escaped one — a double-escaped path would reference a file
        // that does not exist.
        let hint = doc.DocumentElement.InnerXml
        (hint.Contains "&amp;amp;") |> Expect.isFalse "the path must not be double-escaped"

    testCase "WHY — Private=true is present, because without it the reference is compile-time only and the app dies at RUN time with a missing assembly" <| fun _ ->
      (SessionBuild.coreReferenceTargetsContent "/opt/SageFs.Core.dll").Contains "<Private>true</Private>"
      |> Expect.isTrue "the generated file must mark the reference Private"

    testCase "WHY — injecting the reference is ADDITIVE and cannot displace the optimization flag" <| fun _ ->
      let plain = SessionBuild.buildArguments false "/x.fsproj"
      let injected =
        plain @ [ SessionBuild.coreReferenceProperty "/tmp/sagefs-inject.targets" ]
      (injected |> List.filter ((=) SessionBuild.optimizationDisablingProperty))
      |> Expect.equal "the optimization flag survives" [ SessionBuild.optimizationDisablingProperty ]
      (injected |> List.contains "-p:CustomAfterMicrosoftCommonTargets=/tmp/sagefs-inject.targets")
      |> Expect.isTrue "and the injection is present"
  ]

/// Reads the assembly-level `System.Diagnostics.DebuggableAttribute` off a
/// built DLL via Mono.Cecil (already a SageFs.Core dependency — see
/// FsiHostBuildTests for the same pattern) rather than `Assembly.LoadFrom`,
/// so inspecting the fixture's output never loads it into this test
/// process. Returns the raw `DebuggingModes` int: `3` = optimized
/// (`Default|IgnoreSymbolStoreSequencePoints`), `259` = optimizations
/// disabled (adds the `DisableOptimizations` bit, 256) — matching the
/// measurements in hot-reload-reach-options.md §1.4.
let private debuggingModes (dllPath: string) : int option =
  use assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(dllPath)
  assembly.CustomAttributes
  |> Seq.tryFind (fun a -> a.AttributeType.FullName = "System.Diagnostics.DebuggableAttribute")
  |> Option.bind (fun a ->
    match a.ConstructorArguments.Count > 0 with
    | true -> Some (a.ConstructorArguments.[0].Value :?> int)
    | false -> None)

let private disableOptimizationsBit = 256

[<Tests>]
let structuralOverrideGuaranteeTests =
  Integration.hostList "SessionBuild optimization gate (real build)" [
    testAsync "WHY — a project's own Directory.Build.props setting <Optimize>true</Optimize> CANNOT override -p:Optimize=false — verified against a REAL dotnet build, because this is the exact silent-degradation scenario item 4 of the task asked to rule out, not merely assert" {
      let workDir = Directory.CreateTempSubdirectory("sagefs-optgate-").FullName
      try
        File.WriteAllText(
          Path.Combine(workDir, "Directory.Build.props"),
          """<Project><PropertyGroup><Optimize>true</Optimize></PropertyGroup></Project>""")
        let projName = "OptGateFixture.fsproj"
        File.WriteAllText(
          Path.Combine(workDir, projName),
          """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
</Project>""")
        // A non-constant-foldable top-level `let` — matches the exact shape
        // (an `initonly` static field with a real cctor) that §1.7 measured,
        // rather than a literal FSC would fold away at compile time anyway.
        //
        // NOTE: built with explicit "\n" joins, not a third triple-quoted
        // literal whose closing `"""` would sit alone at column 1 — that
        // combination trips the offside-rule checker inside a surrounding
        // `try/finally` (reproduced independently; the closing delimiter's
        // own line position, not its string content, is what matters).
        File.WriteAllText(
          Path.Combine(workDir, "Lib.fs"),
          "module OptGateFixture.Lib\nlet greeting : string = System.String([| 'O'; 'K' |])\nlet readIt () = greeting\n")

        let! result = SessionBuild.runBuildAsync [ projName ] workDir

        match result with
        | Error err -> failtestf "expected the fixture project to build; got %A" err
        | Ok _ ->
          let dllPath = Path.Combine(workDir, "bin", "Debug", "net10.0", "OptGateFixture.dll")
          File.Exists dllPath |> Expect.isTrue "the build produced the expected output assembly"
          match debuggingModes dllPath with
          | None -> failtest "a built assembly always carries a DebuggableAttribute — none found"
          | Some modes ->
            (modes &&& disableOptimizationsBit)
            |> Expect.equal
              "DisableOptimizations bit is set — the project's own Directory.Build.props (Optimize=true) did NOT win, because -p:Optimize=false arrives as an MSBuild global property the project cannot reassign"
              disableOptimizationsBit
      finally
        try Directory.Delete(workDir, true) with _ -> ()
    }
  ]

/// WHY — the sample the hot-reload docs point people at has to survive the
/// rebuild SageFs runs itself. That rebuild hands MSBuild a global property
/// (`CustomAfterMicrosoftCommonTargets`) that injects SageFs.Core as a
/// reference. The ticker's own `BuildSageFsCoreFirst` target builds SageFs.Core
/// through a nested MSBuild task, which INHERITS that property, so Core was
/// compiled with a reference to itself and every type in it became ambiguous.
/// The rebuild failed, and nothing said so: the old worker just kept serving.
/// Run through `SessionBuild.runBuildAsync`, because that is the exact function
/// the daemon's hard reset calls.
[<Tests>]
let referenceSampleRebuildTests =
  Integration.hostList "SessionBuild rebuilds the reference sample (real build)" [
    testAsync "WHY — the ConsoleTicker sample builds through SageFs's own rebuild path, because a hard reset on the documented hot-reload demo used to fail silently and leave the old worker serving" {
      let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
      let sampleDir = Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.ConsoleTicker")
      let project = Path.Combine(sampleDir, "SageFs.Samples.ConsoleTicker.fsproj")

      let! result = SessionBuild.runBuildAsync [ project ] sampleDir

      match result with
      | Error err ->
        failtestf "the reference sample must build through SageFs's rebuild path, got: %s" (SageFsError.describe err)
      | Ok _ ->
        Directory.EnumerateFiles(Path.Combine(sampleDir, "bin", "Debug"), "SageFs.Samples.ConsoleTicker.dll", SearchOption.AllDirectories)
        |> Seq.isEmpty
        |> Expect.isFalse "the rebuild must produce the sample's assembly"
    }
  ]

/// What MSBuild really evaluates for a project called `assemblyName` that imports the
/// generated targets file: the include of every Reference item. A bare project file
/// that imports the targets is enough, because the file only adds an item.
let private referencesFor (assemblyName: string) : System.Threading.Tasks.Task<string list> =
  task {
    let dir = Directory.CreateTempSubdirectory("sagefs-targets-").FullName
    try
      let targets = Path.Combine(dir, "inject.targets")
      let proj = Path.Combine(dir, "p.proj")
      File.WriteAllText(targets, SessionBuild.coreReferenceTargetsContent "/opt/SageFs.Core.dll")
      File.WriteAllText(proj, sprintf "<Project><Import Project=\"%s\" /></Project>" targets)
      let psi = System.Diagnostics.ProcessStartInfo("dotnet")
      for arg in [ "msbuild"; proj; "-getItem:Reference"; sprintf "-p:AssemblyName=%s" assemblyName ] do
        psi.ArgumentList.Add arg
      psi.RedirectStandardOutput <- true
      psi.RedirectStandardError <- true
      use p = System.Diagnostics.Process.Start psi
      let! output = p.StandardOutput.ReadToEndAsync()
      let! error = p.StandardError.ReadToEndAsync()
      do! p.WaitForExitAsync()
      match p.ExitCode with
      | 0 ->
        use doc = System.Text.Json.JsonDocument.Parse output
        match doc.RootElement.GetProperty("Items").TryGetProperty "Reference" with
        | true, items -> return [ for item in items.EnumerateArray() -> item.GetProperty("Identity").GetString() ]
        | false, _ -> return []
      | code -> return failtestf "msbuild exited %d evaluating the injected targets: %s%s" code output error
    finally
      Directory.Delete(dir, true)
  }

[<Tests>]
let injectedReferenceGuardTests =
  testList "The injected SageFs.Core reference" [
    testTask "WHY — a project that is not SageFs.Core gets the reference, because that is the whole point of injecting it" {
      let! references = referencesFor "MyApp"
      references |> Expect.contains "MyApp compiles against the daemon's SageFs.Core" "SageFs.Core"
    }

    testTask "WHY — the project whose AssemblyName IS SageFs.Core gets none, because a session on SageFs itself compiled Core against a copy of itself and failed with impossible type errors while dotnet build passed" {
      let! references = referencesFor "SageFs.Core"
      references |> Expect.isEmpty "SageFs.Core does not reference itself"
    }
  ]
