module SageFs.Tests.HostManifestTests

open Expecto
open Expecto.Flip
open SageFs
open System.IO

/// Phase 0 RED → Phase 1 GREEN.
///
/// RED: the worker shared the daemon's output dir containing Falco*.dll and
/// OpenTelemetry — the 0x80131040 collision source.
///
/// GREEN: the SageFs.Host project's output dir must contain ONLY the vetted
/// manifest closure (no Falco, no OpenTelemetry), and a host-manifest.json
/// must exist and verify clean (fail-closed).
///
/// The host dir is derived from the TEST assembly's own output location
/// (SageFs.Tests/bin/<cfg>/<tfm>/ -> ../../SageFs.Host/bin/<cfg>/<tfm>/),
/// so it matches whatever configuration the tests run under, on any OS.
let hostDir =
  let testDir = System.AppContext.BaseDirectory
  // AppContext.BaseDirectory is repo/SageFs.Tests/bin/<cfg>/<tfm>/
  // Up 4 = the repo root; then into SageFs.Host/bin. The config/tfm subdirs
  // are discovered below (newest wins) so Debug/Release both work.
  Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "SageFs.Host", "bin"))
  |> fun root ->
    match Directory.Exists root with
    | false -> root
    | true ->
      Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
      |> Array.collect (fun cfg ->
        Directory.GetDirectories(cfg, "*", SearchOption.TopDirectoryOnly)
        |> Array.map (fun tfm -> tfm))
      |> Array.sortDescending
      |> Array.tryHead
      |> Option.defaultValue root

[<Tests>]
let tests =
  testList "Host manifest" [

    testCase "host dir must not contain Falco.dll" <| fun _ ->
      let falcoDlls =
        Directory.Exists hostDir
        |> function
          | false -> []
          | true ->
            Directory.GetFiles(hostDir, "Falco*.dll")
            |> Array.toList

      falcoDlls |> Expect.isEmpty (sprintf "host dir must not contain dashboard deps, but found: %A" falcoDlls)

    testCase "host dir must not contain OpenTelemetry assemblies" <| fun _ ->
      let otelDlls =
        Directory.Exists hostDir
        |> function
          | false -> []
          | true ->
            Directory.GetFiles(hostDir, "OpenTelemetry*.dll")
            |> Array.toList

      otelDlls |> Expect.isEmpty (sprintf "host dir must not contain OpenTelemetry deps, but found: %A" otelDlls)

    testCase "host-manifest.json exists and verifies the host dir" <| fun _ ->
      let manifestPath = Path.Combine(hostDir, HostManifest.manifestFileName)

      (File.Exists manifestPath) |> Expect.isTrue (sprintf "host-manifest.json must exist in the host dir (%s)" hostDir)

      match HostManifest.check hostDir with
      | Ok () -> ()
      | Error msg -> failtestf "host dir failed manifest verification: %s" msg

    testCase "manifest verification is fail-closed on an unexpected file" <| fun _ ->
      // A directory containing a file outside the allowed set must fail.
      let tmp = Path.Combine(Path.GetTempPath(), sprintf "sagefs-manifest-test-%s" (System.Guid.NewGuid().ToString("N")))
      Directory.CreateDirectory tmp |> ignore
      try
        File.WriteAllText(Path.Combine(tmp, HostManifest.manifestFileName), """{"version":"t","targetFramework":"net10.0","allowedFiles":["ok.dll"]}""")
        File.WriteAllText(Path.Combine(tmp, "ok.dll"), "")
        File.WriteAllText(Path.Combine(tmp, "sneaky.dll"), "")

        match HostManifest.check tmp with
        | Ok () -> failtest "unexpected file must fail the check"
        | Error msg ->
          msg |> Expect.stringContains "error should name the offending file" "sneaky.dll"
      finally
        try Directory.Delete(tmp, true) with _ -> ()
  ]

/// The host's big assemblies are compiled ahead of time, because the worker's start is the cost of every session a user opens and of
/// every host case the suite runs: a cold JIT of the F# compiler is seconds of CPU. The build step is SageFs.Host/ReadyToRun.targets.
///
/// Two rules, one for each side of the package boundary:
///
/// * The daemon's own `host/` directory (what the tests and a dev daemon run) holds ReadyToRun images for the assemblies the step names,
///   or the build's own report says why it could not make them. It is speed only, so a machine without crossgen2 still builds.
/// * The package's copy (SageFs.Host/bin, which `PackHostForDaemon` packs) stays plain IL. The tool package is one payload for every OS and
///   CPU, and a ReadyToRun image is built for ONE of them: the runtime refuses a foreign one with a BadImageFormatException instead of
///   falling back to the IL, so a Linux image in the package would take the host down on Windows and macOS.
module HostReadyToRun =

  /// The configuration the tests run under, read from this assembly's own output path (repo/SageFs.Tests/bin/<cfg>/<tfm>/).
  let configuration = DirectoryInfo(System.AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent.Name

  /// The frameworks the shipped tool is built for (Directory.Build.props, SageFsTargetFrameworks).
  let targetFrameworks = [ "net10.0"; "net11.0" ]

  let repoRoot = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", ".."))

  /// The daemon's host directory for a framework, where the build step applies the images.
  let daemonHostDir (tfm: string) = Path.Combine(repoRoot, "SageFs", "bin", configuration, tfm, "host")

  /// The package's host directory for a framework: SageFs.Host's own output.
  let packagedHostDir (tfm: string) = Path.Combine(repoRoot, "SageFs.Host", "bin", configuration, tfm)

  /// What the build step wrote down about the daemon's host directory.
  let reportFile (tfm: string) = Path.Combine(repoRoot, "SageFs", "obj", configuration, tfm, "sagefs-host-r2r.status")

  /// The assemblies the step compiles. Mirrors `SageFsReadyToRunAssemblies` in ReadyToRun.targets, which the test below reads back from the report.
  [<RequireQualifiedAccess>]
  type Report =
    | Applied of assemblies: string list
    | Skipped of reason: string

  let parseReport (text: string) : Result<Report, string> =
    let line = text.Split('\n') |> Array.map (fun l -> l.Trim()) |> Array.tryFind (fun l -> l.StartsWith "r2r:")
    match line with
    | None -> Error(sprintf "no `r2r:` line in the report: %s" text)
    | Some l when l.StartsWith "r2r: skipped" -> Ok(Report.Skipped(l.Substring("r2r: skipped".Length).Trim()))
    | Some l when l.StartsWith "r2r: applied" ->
      let assemblies =
        l.Split(' ')
        |> Array.tryFind (fun token -> token.StartsWith "assemblies=")
        |> Option.map (fun token -> token.Substring("assemblies=".Length).Split(',', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList)
      match assemblies with
      | Some names -> Ok(Report.Applied names)
      | None -> Error(sprintf "the applied report names no assemblies: %s" l)
    | Some l -> Error(sprintf "unrecognised report line: %s" l)

  /// Whether a managed assembly carries precompiled code (a ManagedNativeHeader) as well as its IL.
  let isReadyToRun (path: string) : bool =
    use stream = File.OpenRead path
    use reader = new System.Reflection.PortableExecutable.PEReader(stream)
    match reader.PEHeaders.CorHeader with
    | null -> false
    | cor -> cor.ManagedNativeHeaderDirectory.Size > 0

  let builtFrameworks (dirOf: string -> string) =
    targetFrameworks |> List.filter (fun tfm -> File.Exists(Path.Combine(dirOf tfm, "SageFs.Host.dll")))

  [<Tests>]
  let tests =
    testList "Host ReadyToRun" [

      testCase "the report parser reads an applied step and a skipped one" <| fun _ ->
        parseReport "r2r: applied rid=linux-x64 crossgen2=11.0.0 compiled=1 reused=2 assemblies=FSharp.Core,SageFs.Core"
        |> Expect.equal "the applied line names its assemblies" (Ok(Report.Applied [ "FSharp.Core"; "SageFs.Core" ]))
        parseReport "r2r: skipped crossgen2 pack not found"
        |> Expect.equal "the skipped line keeps its reason" (Ok(Report.Skipped "crossgen2 pack not found"))
        parseReport "nothing here" |> Expect.isError "text with no r2r line is not a report"

      testCase "the daemon's host directory still verifies against its vetted manifest after the step" <| fun _ ->
        for tfm in builtFrameworks daemonHostDir do
          match HostManifest.check (daemonHostDir tfm) with
          | Ok () -> ()
          | Error msg -> failtestf "the %s host directory failed manifest verification: %s" tfm msg

      testCase "the daemon's host directory holds ReadyToRun images, or the build says why not" <| fun _ ->
        match configuration with
        | "Release" ->
          let frameworks = builtFrameworks daemonHostDir
          frameworks |> Expect.isNonEmpty "the tests run against a built daemon, so at least one host directory exists"
          for tfm in frameworks do
            let report =
              match File.Exists(reportFile tfm) with
              | false -> failtestf "the Release build wrote no ReadyToRun report for %s at %s: the step in SageFs.Host/ReadyToRun.targets did not run" tfm (reportFile tfm)
              | true -> File.ReadLines(reportFile tfm) |> String.concat "\n"
            match parseReport report with
            | Error msg -> failtestf "%s: %s" tfm msg
            | Ok(Report.Skipped _) -> ()
            | Ok(Report.Applied assemblies) ->
              assemblies |> Expect.isNonEmpty (sprintf "%s: an applied step names the assemblies it compiled" tfm)
              for name in assemblies do
                isReadyToRun (Path.Combine(daemonHostDir tfm, name + ".dll"))
                |> Expect.isTrue (sprintf "%s: %s.dll in the daemon's host directory must be a ReadyToRun image" tfm name)
        | other -> skiptest (sprintf "the step runs in Release builds only, and these tests run in %s" other)

      testCase "Harmony stays plain IL because the FSI host build rewrites its assembly name with Cecil" <| fun _ ->
        for tfm in builtFrameworks daemonHostDir do
          isReadyToRun (Path.Combine(daemonHostDir tfm, "0Harmony.dll"))
          |> Expect.isFalse (sprintf "%s: Cecil cannot write a ReadyToRun image back (FsiHostBuild.renameAssembly), so 0Harmony.dll must stay IL" tfm)

      testCase "the packaged host stays plain IL: a ReadyToRun image is built for one OS and the runtime refuses it on another" <| fun _ ->
        let frameworks = builtFrameworks packagedHostDir
        for tfm in frameworks do
          let compiled =
            DirectoryInfo(packagedHostDir tfm).GetFileSystemInfos("*.dll")
            |> Array.filter (fun f -> isReadyToRun f.FullName)
            |> Array.map (fun f -> f.Name)
          compiled |> Expect.isEmpty (sprintf "%s: the package carries SageFs.Host/bin to every OS, so no assembly there may be ReadyToRun" tfm)
    ]
