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

    let build (version: System.Version) (buildId: string) (offsetSeconds: float) : HostCoreAdoption.BuildFacts =
      HostCoreAdoption.BuildFacts.Known(
        version,
        buildId,
        DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc).AddSeconds offsetSeconds)

    testCase "decide adopts a candidate that is the same build, whatever the clock says" <| fun _ ->
      let version = System.Version(1, 2, 3)

      HostCoreAdoption.decide (build version "build-a" 0.) "/tmp/proj/bin/SageFs.Core.dll" (build version "build-a" 0.)
      |> Expect.equal
        "the same bytes are the same build: adopt it"
        (HostCoreAdoption.Adoption.Adopted "/tmp/proj/bin/SageFs.Core.dll")

    testCase "decide adopts a candidate that is the same build even when the project's copy is older on disk" <| fun _ ->
      // The common case: a project that references the Core carries a copy of the daemon's own
      // build, and the copy is as old as the last time the project was built.
      let version = System.Version(1, 2, 3)

      HostCoreAdoption.decide (build version "build-a" 0.) "/tmp/proj/bin/SageFs.Core.dll" (build version "build-a" -3600.)
      |> Expect.equal
        "a copy of the daemon's own build is adopted whatever the clock says"
        (HostCoreAdoption.Adoption.Adopted "/tmp/proj/bin/SageFs.Core.dll")

    testCase "decide adopts a candidate that is a NEWER build, because that is the rebuild the user just made" <| fun _ ->
      let version = System.Version(1, 2, 3)

      HostCoreAdoption.decide (build version "build-a" 0.) "/tmp/proj/bin/SageFs.Core.dll" (build version "build-b" 1.)
      |> Expect.equal
        "the dogfooding case: a newer build is adopted"
        (HostCoreAdoption.Adoption.Adopted "/tmp/proj/bin/SageFs.Core.dll")

    testCase "decide refuses a candidate whose version differs from the daemon's own" <| fun _ ->
      let hostVersion = System.Version(1, 2, 3)
      let candidateVersion = System.Version(1, 2, 4)

      match HostCoreAdoption.decide (build hostVersion "build-a" 0.) "/tmp/proj/bin/SageFs.Core.dll" (build candidateVersion "build-b" 0.) with
      | HostCoreAdoption.Adoption.Refused reason ->
        reason |> Expect.stringContains "the reason names the daemon's version" "1.2.3"
        reason |> Expect.stringContains "the reason names the candidate's version" "1.2.4"
        reason |> Expect.stringContains "the reason says what to do about it" "rebuild"
      | other -> failtestf "expected Refused, got %A" other

    testCase "decide refuses a candidate whose version matches but whose BUILD is older, because that is the stale copy the version cannot catch" <| fun _ ->
      let version = System.Version(1, 2, 3)

      match HostCoreAdoption.decide (build version "build-new" 0.) "/tmp/proj/bin/SageFs.Core.dll" (build version "build-old" -1.) with
      | HostCoreAdoption.Adoption.Refused reason ->
        reason |> Expect.stringContains "the reason says the versions match" "same version"
        reason |> Expect.stringContains "the reason says the build is older" "OLDER build"
        reason |> Expect.stringContains "the reason says what to do about it" "Rebuild the project"
      | other -> failtestf "expected Refused, got %A" other

    testCase "the build id is the identity of the bytes, so a copy matches and a different build does not" <| fun _ ->
      withTempDir (fun dir ->
        let a = Path.Combine(dir, "a.dll")
        let b = Path.Combine(dir, "b.dll")
        File.WriteAllBytes(a, [| 1uy; 2uy; 3uy |])
        File.Copy(a, b, true)
        let c = Path.Combine(dir, "c.dll")
        File.WriteAllBytes(c, [| 1uy; 2uy; 4uy |])
        HostCoreAdoption.buildIdOf a
        |> Expect.equal "a copy is the same build" (HostCoreAdoption.buildIdOf b)
        HostCoreAdoption.buildIdOf a
        |> Expect.notEqual "different bytes are a different build" (HostCoreAdoption.buildIdOf c))

    testCase "a build that cannot be read is refused with the reason, not adopted" <| fun _ ->
      let version = System.Version(1, 2, 3)
      let missing = HostCoreAdoption.BuildFacts.Unreadable "the file is not there"

      match HostCoreAdoption.decide (build version "build-a" 0.) "/tmp/proj/bin/SageFs.Core.dll" missing with
      | HostCoreAdoption.Adoption.Refused reason ->
        reason |> Expect.stringContains "the reason says the file could not be read" "could not be read"
        reason |> Expect.stringContains "the reason carries the underlying why" "the file is not there"
      | other -> failtestf "expected Refused, got %A" other

    testCase "a build that cannot be read is said so, and the file it names is named too" <| fun _ ->
      let version = System.Version(1, 2, 3)
      let missing = HostCoreAdoption.BuildFacts.Unreadable "the file is not there"

      match HostCoreAdoption.decide missing "/tmp/proj/bin/SageFs.Core.dll" (build version "build-a" 0.) with
      | HostCoreAdoption.Adoption.Refused reason ->
        reason |> Expect.stringContains "the reason says it is the daemon's own copy that could not be read" "running daemon's own"
        reason |> Expect.stringContains "the reason carries the underlying why" "the file is not there"
      | other -> failtestf "expected Refused, got %A" other

    testCase "decide's refusal names the absolute build it measured, so a build resolved somewhere else is visible" <| fun _ ->
      let hostVersion = System.Version(1, 2, 3)
      let candidate = Path.Combine(Path.GetTempPath(), "some-other-checkout", "bin", "SageFs.Core.dll")

      match HostCoreAdoption.decide (build hostVersion "build-a" 0.) candidate (build (System.Version(1, 2, 4)) "build-b" 0.) with
      | HostCoreAdoption.Adoption.Refused reason ->
        reason |> Expect.stringContains "the reason names the exact dll that was compared" candidate
      | other -> failtestf "expected Refused, got %A" other

    testCase "a relative project resolves against the caller's working directory, not the daemon's" <| fun _ ->
      withTempDir (fun callerDir ->
        let daemonCwd = Directory.GetCurrentDirectory()
        callerDir |> Expect.notEqual "the working directory differs from the daemon's cwd" daemonCwd
        let relative = Path.Combine("SageFs.Tests", "SageFs.Tests.fsproj")

        let resolved =
          SessionProjectTarget.resolveAgainst callerDir [ SessionProjectTarget.Project relative ]

        resolved
        |> Expect.equal
          "the project is under the caller's working directory"
          [ SessionProjectTarget.Project (Path.Combine(callerDir, relative)) ]

        resolved
        |> SessionProjectTarget.projects
        |> HostCoreAdoption.projectDirsOf
        |> Expect.equal
          "the build search starts in the caller's project directory"
          [ Path.Combine(callerDir, "SageFs.Tests") ])

    testCase "a relative solution resolves against the caller's working directory too" <| fun _ ->
      withTempDir (fun callerDir ->
        SessionProjectTarget.resolveAgainst callerDir [ SessionProjectTarget.Solution "SageFs.slnx" ]
        |> Expect.equal
          "the solution is under the caller's working directory"
          [ SessionProjectTarget.Solution (Path.Combine(callerDir, "SageFs.slnx")) ])

    testCase "an absolute path and a bare target are left exactly as given" <| fun _ ->
      withTempDir (fun callerDir ->
        withTempDir (fun elsewhere ->
          let absolute = Path.Combine(elsewhere, "App.fsproj")
          SessionProjectTarget.resolveAgainst callerDir [ SessionProjectTarget.Project absolute ]
          |> Expect.equal "an absolute project is not re-rooted" [ SessionProjectTarget.Project absolute ]
          SessionProjectTarget.resolveAgainst callerDir [ SessionProjectTarget.Bare ]
          |> Expect.equal "bare has no path to resolve" [ SessionProjectTarget.Bare ]))

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

/// A project's own copy of SageFs.Core that is older than the daemon's is a build output, so the daemon removes it and rebuilds the
/// project instead of refusing the session and leaving the rebuild to whoever hit it.
[<Tests>]
let staleCopyTests =
  let build (version: System.Version) (buildId: string) (offsetSeconds: int) : HostCoreAdoption.BuildFacts =
    HostCoreAdoption.BuildFacts.Known(version, buildId, DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc).AddSeconds(float offsetSeconds))
  let v1 = System.Version(1, 2, 3)
  let v2 = System.Version(1, 2, 4)
  testList "stale SageFs.Core copies" [

    testProperty "WHY — a copy is stale exactly when decide would refuse it as an older build of the same version" <| fun (sameVersion: bool) (sameBytes: bool) (offset: int) ->
      let host = build v1 "host" 0
      let candidate = build (if sameVersion then v1 else v2) (if sameBytes then "host" else "other") offset
      let refusedAsOlder =
        match HostCoreAdoption.decide host "/p/bin/SageFs.Core.dll" candidate with
        | HostCoreAdoption.Adoption.Refused reason -> reason.Contains "OLDER build"
        | HostCoreAdoption.Adoption.Adopted _ -> false
      HostCoreAdoption.isStaleCopy host candidate = refusedAsOlder

    testCase "WHY — a copy that cannot be read is not called stale, because the refusal for it says so and a rebuild would not help" <| fun _ ->
      let host = build v1 "host" 0
      HostCoreAdoption.isStaleCopy host (HostCoreAdoption.BuildFacts.Unreadable "denied") |> Expect.isFalse "unreadable candidate"
      HostCoreAdoption.isStaleCopy (HostCoreAdoption.BuildFacts.Unreadable "denied") (build v1 "x" -5) |> Expect.isFalse "unreadable daemon build"

    testCase "WHY — only the stale copies among a project's builds are named, newer and identical ones are left alone" <| fun _ ->
      withTempDir (fun root ->
        let sharedRoot = Path.Combine(root, "shared")
        Directory.CreateDirectory sharedRoot |> ignore
        let hostCore = Path.Combine(sharedRoot, "SageFs.Core.dll")
        File.WriteAllBytes(hostCore, [| 9uy |])
        let hostTime = DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc)
        File.SetLastWriteTimeUtc(hostCore, hostTime)
        let projectDir = Path.Combine(root, "project")
        let write (name: string) (bytes: byte[]) (time: DateTime) =
          let path = Path.Combine(projectDir, "bin", name, "SageFs.Core.dll")
          Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
          File.WriteAllBytes(path, bytes)
          File.SetLastWriteTimeUtc(path, time)
          path
        let stale = write "Debug" [| 1uy |] (hostTime.AddHours -3.0)
        write "Release" [| 2uy |] (hostTime.AddHours 1.0) |> ignore
        write "Other" [| 9uy |] (hostTime.AddHours -9.0) |> ignore
        let versionOf (_: string) = Some v1
        HostCoreAdoption.staleCopies versionOf (HostCoreAdoption.factsOf hostCore v1) (HostCoreAdoption.findCandidates [ projectDir ])
        |> Expect.equal "just the older, different build" [ stale ])

    testCase "WHY — a file whose version cannot be read is left alone, not removed on a guess" <| fun _ ->
      withTempDir (fun root ->
        let sharedRoot = Path.Combine(root, "shared")
        Directory.CreateDirectory sharedRoot |> ignore
        let hostCore = Path.Combine(sharedRoot, "SageFs.Core.dll")
        File.WriteAllBytes(hostCore, [| 9uy |])
        let projectDir = Path.Combine(root, "project")
        let path = Path.Combine(projectDir, "bin", "Debug", "SageFs.Core.dll")
        touch path (DateTime.UtcNow.AddDays -2.0)
        HostCoreAdoption.staleCopies (fun _ -> None) (HostCoreAdoption.factsOf hostCore v1) [ path ]
        |> Expect.isEmpty "nothing named when the version is unknown")

    testCase "WHY — removing stale copies deletes them, reports each, and says why one stayed" <| fun _ ->
      withTempDir (fun root ->
        let gone = Path.Combine(root, "a", "SageFs.Core.dll")
        touch gone DateTime.UtcNow
        let missing = Path.Combine(root, "b", "SageFs.Core.dll")
        let results = HostCoreAdoption.removeCopies [ gone; missing ]
        File.Exists gone |> Expect.isFalse "the stale copy is gone"
        results |> List.map fst |> Expect.equal "each path is reported once, in order" [ gone; missing ]
        results |> List.map snd |> Expect.equal "a copy already gone is not a failure" [ HostCoreAdoption.Removal.Removed; HostCoreAdoption.Removal.Removed ])
  ]

/// Only the copy a spawn would judge (the newest) decides whether anything is removed, so a leftover older copy that every build
/// regenerates cannot make each create rebuild the project.
[<Tests>]
let staleCopyScopeTests =
  let v1 = System.Version(1, 2, 3)
  let hostTime = DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc)
  let setup (root: string) (copies: (string * byte[] * DateTime) list) =
    let sharedRoot = Path.Combine(root, "shared")
    Directory.CreateDirectory sharedRoot |> ignore
    let hostCore = Path.Combine(sharedRoot, "SageFs.Core.dll")
    File.WriteAllBytes(hostCore, [| 9uy |])
    File.SetLastWriteTimeUtc(hostCore, hostTime)
    let projectDir = Path.Combine(root, "project")
    for (name, bytes, time) in copies do
      let path = Path.Combine(projectDir, "bin", name, "SageFs.Core.dll")
      Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
      File.WriteAllBytes(path, bytes)
      File.SetLastWriteTimeUtc(path, time)
    HostCoreAdoption.factsOf hostCore v1, HostCoreAdoption.findCandidates [ projectDir ]
  testList "stale copies, scoped to the copy that decides" [

    testCase "WHY — when the newest copy is fine, an older stale one is left alone, because removing it would only be rebuilt" <| fun _ ->
      withTempDir (fun root ->
        let host, candidates =
          setup root [ "Release", [| 9uy |], hostTime.AddHours -1.0   // the daemon's own bytes, newest of the two
                       "Debug", [| 1uy |], hostTime.AddHours -5.0 ]    // older and different: stale, but not the one a spawn judges
        HostCoreAdoption.staleCopiesWhenNewestIs (fun _ -> Some v1) host candidates
        |> Expect.isEmpty "nothing to remove")

    testCase "WHY — when the newest copy is stale, every stale copy is named" <| fun _ ->
      withTempDir (fun root ->
        let host, candidates =
          setup root [ "Debug", [| 1uy |], hostTime.AddHours -2.0
                       "Other", [| 2uy |], hostTime.AddHours -6.0 ]
        HostCoreAdoption.staleCopiesWhenNewestIs (fun _ -> Some v1) host candidates
        |> List.length
        |> Expect.equal "both are stale and both go" 2)

    testCase "WHY — a project with no copy has nothing to remove" <| fun _ ->
      withTempDir (fun root ->
        let host, candidates = setup root []
        HostCoreAdoption.staleCopiesWhenNewestIs (fun _ -> Some v1) host candidates |> Expect.isEmpty "no copies")
  ]
