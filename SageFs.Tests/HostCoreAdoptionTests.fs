module SageFs.Tests.HostCoreAdoptionTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// roast-4 #0(a), real fix (not the ALC-based one that DogfoodReplTests.fs's
/// own comment documents as a dead end): SageFs.Core.dll is a
/// Trusted-Platform-Assembly for the SageFs.Host process (it is listed in
/// SageFs.Host.deps.json), so no in-process trick inside the spawned host
/// can make it resolve to a different build — the native hostfxr/hostpolicy
/// binder wins that race before any managed AssemblyLoadContext gets a say.
/// The fix has to happen BEFORE the process exists: give the daemon's
/// worker-spawn logic a directory whose SageFs.Core.dll already IS the
/// session project's own build.
///
/// These are the pure/impure-boundary tests for HostCoreAdoption. The
/// actual spawn integration (SessionManager.startWorkerProcess) and the
/// live Location assertion are exercised by DogfoodReplTests.fs.
let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-host-adoption-test-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally
    try Directory.Delete(dir, true) with _ -> ()

let private touch (path: string) (writeTime: DateTime) =
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  File.WriteAllBytes(path, [| 1uy; 2uy; 3uy |])
  File.SetLastWriteTimeUtc(path, writeTime)

[<Tests>]
let tests =
  testList "Host core adoption" [

    testCase "findCandidates returns empty when no project ships SageFs.Core" <| fun _ ->
      withTempDir (fun projectDir ->
        HostCoreAdoption.findCandidates [ projectDir ]
        |> Expect.isEmpty "no bin dir at all means no candidates")

    testCase "findCandidates finds a candidate under a project's bin tree" <| fun _ ->
      withTempDir (fun projectDir ->
        let dll = Path.Combine(projectDir, "bin", "Debug", "net10.0", "SageFs.Core.dll")
        touch dll DateTime.UtcNow

        HostCoreAdoption.findCandidates [ projectDir ]
        |> Expect.equal "the one candidate under bin/Debug/net10.0" [ dll ])

    testCase "findCandidates orders multiple candidates newest write time first" <| fun _ ->
      withTempDir (fun projectDir ->
        let older = Path.Combine(projectDir, "bin", "Debug", "net10.0", "SageFs.Core.dll")
        let newer = Path.Combine(projectDir, "bin", "Release", "net10.0", "SageFs.Core.dll")
        touch older (DateTime.UtcNow.AddMinutes -10.0)
        touch newer DateTime.UtcNow

        HostCoreAdoption.findCandidates [ projectDir ]
        |> Expect.equal "newest write time sorts first" [ newer; older ])

    testCase "findCandidates excludes host/ and ref/ paths" <| fun _ ->
      withTempDir (fun projectDir ->
        let hostCopy = Path.Combine(projectDir, "bin", "Debug", "net10.0", "host", "SageFs.Core.dll")
        let refCopy = Path.Combine(projectDir, "bin", "Debug", "net10.0", "ref", "SageFs.Core.dll")
        let real = Path.Combine(projectDir, "bin", "Debug", "net10.0", "SageFs.Core.dll")
        touch hostCopy DateTime.UtcNow
        touch refCopy DateTime.UtcNow
        touch real (DateTime.UtcNow.AddMinutes -1.0)

        HostCoreAdoption.findCandidates [ projectDir ]
        |> Expect.equal "only the real build, never host/ or ref/ copies" [ real ])

    testCase "decide adopts a candidate whose version equals the daemon's own" <| fun _ ->
      let version = System.Version(1, 2, 3)

      HostCoreAdoption.decide version "/tmp/proj/bin/SageFs.Core.dll" version
      |> Expect.equal
        "same version is the dogfooding case: adopt it"
        (HostCoreAdoption.Adoption.Adopted "/tmp/proj/bin/SageFs.Core.dll")

    testCase "decide refuses a candidate whose version differs from the daemon's own" <| fun _ ->
      let hostVersion = System.Version(1, 2, 3)
      let candidateVersion = System.Version(1, 2, 4)

      match HostCoreAdoption.decide hostVersion "/tmp/proj/bin/SageFs.Core.dll" candidateVersion with
      | HostCoreAdoption.Adoption.Refused reason ->
        reason |> Expect.stringContains "the reason names the daemon's version" "1.2.3"
        reason |> Expect.stringContains "the reason names the candidate's version" "1.2.4"
        reason |> Expect.stringContains "the reason says what to do about it" "rebuild"
      | other -> failtestf "expected Refused, got %A" other

    testCase "materialize copies the shared host dir and substitutes SageFs.Core.dll" <| fun _ ->
      withTempDir (fun root ->
        let sharedHostDir = Path.Combine(root, "shared", "host")
        Directory.CreateDirectory sharedHostDir |> ignore
        File.WriteAllBytes(Path.Combine(sharedHostDir, "SageFs.Core.dll"), [| 9uy |])
        File.WriteAllBytes(Path.Combine(sharedHostDir, "SageFs.Host.dll"), [| 8uy |])
        File.WriteAllText(Path.Combine(sharedHostDir, "host-manifest.json"), """{"version":"t","targetFramework":"net10.0","allowedFiles":["SageFs.Core.dll","SageFs.Host.dll"]}""")

        let candidateDir = Path.Combine(root, "candidate")
        Directory.CreateDirectory candidateDir |> ignore
        let candidatePath = Path.Combine(candidateDir, "SageFs.Core.dll")
        File.WriteAllBytes(candidatePath, [| 42uy; 42uy |])

        let privateRoot = Path.Combine(root, "private")
        HostCoreAdoption.materialize sharedHostDir privateRoot candidatePath

        let privateHostDir = Path.Combine(privateRoot, "host")
        let substituted = Path.Combine(privateHostDir, "SageFs.Core.dll")
        File.ReadAllBytes substituted
        |> Expect.equal "the private copy's SageFs.Core.dll is the candidate's bytes" [| 42uy; 42uy |]

        File.Exists (Path.Combine(privateHostDir, "SageFs.Host.dll"))
        |> Expect.isTrue "other host files are copied through unchanged"

        File.Exists (Path.Combine(privateHostDir, "host-manifest.json"))
        |> Expect.isTrue "the manifest is copied through so HostManifest.check still passes")

    testCase "cleanup removes the private root, and tolerates it already being gone" <| fun _ ->
      withTempDir (fun root ->
        let privateRoot = Path.Combine(root, "gone-soon")
        Directory.CreateDirectory privateRoot |> ignore
        File.WriteAllText(Path.Combine(privateRoot, "x.txt"), "x")

        HostCoreAdoption.cleanup privateRoot
        Directory.Exists privateRoot |> Expect.isFalse "the private root is gone after cleanup"

        // Idempotent: cleaning up an already-gone dir must not throw.
        HostCoreAdoption.cleanup privateRoot)

    testCase "resolveLaunchRoot returns the shared root unchanged when no project ships SageFs.Core" <| fun _ ->
      withTempDir (fun root ->
        let sharedRoot = Path.Combine(root, "shared")
        Directory.CreateDirectory sharedRoot |> ignore
        let projectDir = Path.Combine(root, "project-with-no-core-build")
        Directory.CreateDirectory projectDir |> ignore

        match HostCoreAdoption.resolveLaunchRoot sharedRoot "deadbeef" [ Path.Combine(projectDir, "P.fsproj") ] (System.Version(1, 0, 0)) with
        | Ok plan ->
          plan.LaunchRoot |> Expect.equal "unchanged shared root" sharedRoot
          plan.Cleanup |> Expect.isNone "no private dir was created, so nothing to clean up"
          plan.AdoptedCore |> Expect.isNone "no self-host adoption, so no adopted-core identity"
        | Error reason -> failtestf "expected Ok, got Error %s" reason)

    testCase "resolveLaunchRoot adopts a same-version candidate into a fresh private root" <| fun _ ->
      withTempDir (fun root ->
        let sharedRoot = Path.Combine(root, "shared")
        let sharedHostDir = Path.Combine(sharedRoot, "host")
        Directory.CreateDirectory sharedHostDir |> ignore
        File.WriteAllBytes(Path.Combine(sharedHostDir, "SageFs.Core.dll"), [| 1uy |])

        let projectDir = Path.Combine(root, "project")
        let candidatePath = Path.Combine(projectDir, "bin", "Debug", "net10.0", "SageFs.Core.dll")
        touch candidatePath DateTime.UtcNow

        // AssemblyName.GetAssemblyName requires a real PE image, so the pure
        // decide/materialize path is exercised directly by the tests above;
        // this test's candidate is a stub file and cannot carry a real
        // AssemblyName — that empirical, byte-for-byte case is exercised by
        // DogfoodReplTests.fs against real built assemblies. Here we assert
        // the plumbing: an unreadable (non-PE) candidate is a fail-closed
        // Error, never a silent NotShipped/Adopted fallback.
        match HostCoreAdoption.resolveLaunchRoot sharedRoot "deadbeef" [ Path.Combine(projectDir, "P.fsproj") ] (System.Version(1, 0, 0)) with
        | Error reason ->
          reason |> Expect.stringContains "an unreadable candidate is reported, not silently ignored" "SageFs.Core"
        | Ok _ -> failtest "a non-PE stub file must not be silently treated as a valid candidate")
  ]
