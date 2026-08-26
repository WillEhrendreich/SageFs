module SageFs.Tests.HostManifestTests

open Expecto
open System.IO

/// Phase 0 RED: prove the current worker packaging is NOT manifest-vetted.
///
/// The plan requires: the FSI host process's directory contains ONLY the
/// vetted minimal closure (no Falco, no OpenTelemetry, no dashboard deps),
/// enforced by a host-manifest.json + startup check.
///
/// Today: the worker shares the daemon's output directory, which contains
/// Falco*.dll and other dashboard-only deps — the source of the 0x80131040
/// assembly collisions.
[<Tests>]
let tests =
  testList "Host manifest (RED)" [

    testCase "current worker dir must not contain Falco.dll" <| fun _ ->
      // The current worker runs from the SageFs output dir (same dir as the
      // daemon). The plan says the host's dir must be free of dashboard deps.
      let workerDir =
        Path.Combine(
          __SOURCE_DIRECTORY__, "..", "SageFs", "bin", "Debug", "net10.0")
        |> Path.GetFullPath

      let falcoDlls =
        Directory.Exists workerDir
        |> function
          | false -> []
          | true ->
            Directory.GetFiles(workerDir, "Falco*.dll")
            |> Array.toList

      Expect.isEmpty
        falcoDlls
        (sprintf "worker dir must not contain dashboard deps, but found: %A" falcoDlls)

    testCase "current worker dir must not contain OpenTelemetry assemblies" <| fun _ ->
      let workerDir =
        Path.Combine(
          __SOURCE_DIRECTORY__, "..", "SageFs", "bin", "Debug", "net10.0")
        |> Path.GetFullPath

      let otelDlls =
        Directory.Exists workerDir
        |> function
          | false -> []
          | true ->
            Directory.GetFiles(workerDir, "OpenTelemetry*.dll")
            |> Array.toList

      Expect.isEmpty
        otelDlls
        (sprintf "worker dir must not contain OpenTelemetry deps, but found: %A" otelDlls)

    testCase "a host-manifest.json must exist in the worker dir" <| fun _ ->
      let workerDir =
        Path.Combine(
          __SOURCE_DIRECTORY__, "..", "SageFs", "bin", "Debug", "net10.0")
        |> Path.GetFullPath

      let manifest = Path.Combine(workerDir, "host-manifest.json")

      Expect.isTrue
        (File.Exists manifest)
        "host-manifest.json must exist in the host dir (currently missing)"
  ]
