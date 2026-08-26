module SageFs.Tests.HostManifestTests

open Expecto
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
let hostDir =
  Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Host", "bin", "Debug", "net10.0")
  |> Path.GetFullPath

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

      Expect.isEmpty
        falcoDlls
        (sprintf "host dir must not contain dashboard deps, but found: %A" falcoDlls)

    testCase "host dir must not contain OpenTelemetry assemblies" <| fun _ ->
      let otelDlls =
        Directory.Exists hostDir
        |> function
          | false -> []
          | true ->
            Directory.GetFiles(hostDir, "OpenTelemetry*.dll")
            |> Array.toList

      Expect.isEmpty
        otelDlls
        (sprintf "host dir must not contain OpenTelemetry deps, but found: %A" otelDlls)

    testCase "host-manifest.json exists and verifies the host dir" <| fun _ ->
      let manifestPath = Path.Combine(hostDir, HostManifest.manifestFileName)

      Expect.isTrue
        (File.Exists manifestPath)
        "host-manifest.json must exist in the host dir"

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
          Expect.stringContains msg "sneaky.dll" "error should name the offending file"
      finally
        try Directory.Delete(tmp, true) with _ -> ()
  ]
