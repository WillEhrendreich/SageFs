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
