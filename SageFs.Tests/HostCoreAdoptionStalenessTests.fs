module SageFs.Tests.HostCoreAdoptionStalenessTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// F5b Phase 1 core: a pure decision for whether the loaded SageFs.Core
/// build is stale relative to the newest build on disk. This is the
/// foundation of the "never silently run stale self-host code" signal —
/// see f5b-self-hosting-design.md.
[<Tests>]
let tests =
  testList "HostCoreAdoption self-host freshness" [

    testCase "WHY — HostCoreAdoption.selfHostFreshness — equal version and write time is Current because a build compared against itself is never stale" <| fun _ ->
      let now = DateTime.UtcNow
      HostCoreAdoption.selfHostFreshness (Some("1.2.3", now)) (Some("1.2.3", now))
      |> Expect.equal "identical loaded/newest is Current" HostCoreAdoption.SelfHostFreshness.Current

    testCase "WHY — HostCoreAdoption.selfHostFreshness — a different newest version is Stale because a version bump on disk means the loaded code is no longer current" <| fun _ ->
      let now = DateTime.UtcNow
      HostCoreAdoption.selfHostFreshness (Some("1.2.3", now)) (Some("1.3.0", now))
      |> Expect.equal
        "differing versions is Stale naming both"
        (HostCoreAdoption.SelfHostFreshness.Stale("1.2.3", "1.3.0"))

    testCase "WHY — HostCoreAdoption.selfHostFreshness — a higher newest version is Stale because the daemon must not claim currency against a build it has not adopted" <| fun _ ->
      let now = DateTime.UtcNow
      HostCoreAdoption.selfHostFreshness (Some("1.0.0", now)) (Some("2.0.0", now))
      |> Expect.equal
        "a higher on-disk version is Stale"
        (HostCoreAdoption.SelfHostFreshness.Stale("1.0.0", "2.0.0"))

    testCase "WHY — HostCoreAdoption.selfHostFreshness — a newer write time at the same version is Stale because a rebuild can overwrite bytes without bumping the assembly version" <| fun _ ->
      let loadedTime = DateTime.UtcNow
      let newestTime = loadedTime.AddMinutes 5.0
      HostCoreAdoption.selfHostFreshness (Some("1.2.3", loadedTime)) (Some("1.2.3", newestTime))
      |> Expect.equal
        "same version but newer write time is Stale"
        (HostCoreAdoption.SelfHostFreshness.Stale("1.2.3", "1.2.3"))

    testCase "WHY — HostCoreAdoption.selfHostFreshness — a write-time difference within epsilon at the same version is Current because filesystem timestamp jitter is not a rebuild" <| fun _ ->
      let loadedTime = DateTime.UtcNow
      let newestTime = loadedTime.AddMilliseconds 500.0
      HostCoreAdoption.selfHostFreshness (Some("1.2.3", loadedTime)) (Some("1.2.3", newestTime))
      |> Expect.equal "sub-epsilon jitter stays Current" HostCoreAdoption.SelfHostFreshness.Current

    testCase "WHY — HostCoreAdoption.selfHostFreshness — no candidate on disk is Indeterminate because absence of evidence must not be reported as freshness" <| fun _ ->
      let now = DateTime.UtcNow
      match HostCoreAdoption.selfHostFreshness (Some("1.2.3", now)) None with
      | HostCoreAdoption.SelfHostFreshness.Indeterminate _ -> ()
      | other -> failwithf "expected Indeterminate, got %A" other

    testCase "WHY — HostCoreAdoption.selfHostFreshness — no loaded build is Indeterminate because there is nothing yet to call stale" <| fun _ ->
      let now = DateTime.UtcNow
      match HostCoreAdoption.selfHostFreshness None (Some("1.2.3", now)) with
      | HostCoreAdoption.SelfHostFreshness.Indeterminate _ -> ()
      | other -> failwithf "expected Indeterminate, got %A" other

    testCase "WHY — HostCoreAdoption.selfHostFreshness — both sides absent is Indeterminate because neither the loaded nor the on-disk build is known" <| fun _ ->
      match HostCoreAdoption.selfHostFreshness None None with
      | HostCoreAdoption.SelfHostFreshness.Indeterminate _ -> ()
      | other -> failwithf "expected Indeterminate, got %A" other

    // formatFreshnessAffordance: the surfaced, actionable one-liner. Silent
    // unless genuinely Stale, so normal sessions never see a nag.

    testCase "WHY — HostCoreAdoption.formatFreshnessAffordance — Current yields no line because an up-to-date session must show nothing" <| fun _ ->
      HostCoreAdoption.formatFreshnessAffordance HostCoreAdoption.SelfHostFreshness.Current
      |> Expect.isNone "Current surfaces no affordance"

    testCase "WHY — HostCoreAdoption.formatFreshnessAffordance — Indeterminate yields no line because a non-self-hosting session must not be nagged" <| fun _ ->
      HostCoreAdoption.formatFreshnessAffordance (HostCoreAdoption.SelfHostFreshness.Indeterminate "no build loaded")
      |> Expect.isNone "Indeterminate surfaces no affordance"

    testCase "WHY — HostCoreAdoption.formatFreshnessAffordance — Stale yields a line naming both builds and the exact remediation because the agent must know what to run" <| fun _ ->
      match HostCoreAdoption.formatFreshnessAffordance (HostCoreAdoption.SelfHostFreshness.Stale("0.6.500", "0.6.501")) with
      | Some line ->
        line |> Expect.stringContains "names the loaded build" "0.6.500"
        line |> Expect.stringContains "names the newer on-disk build" "0.6.501"
        line |> Expect.stringContains "gives the exact remediation" "hard_reset_fsi_session"
        line |> Expect.stringContains "spells out the rebuild flag" "rebuild=true"
      | None -> failwith "expected a Stale affordance line, got None"

    testCase "WHY — HostCoreAdoption.newestCandidateIdentity — no self-host candidate on disk is None because a project that does not ship SageFs.Core has nothing to compare" <| fun _ ->
      // A directory with no SageFs.Core.dll under bin — a normal project.
      let tmp = Path.Combine(Path.GetTempPath(), "sagefs-newest-none-" + Guid.NewGuid().ToString("N").[..7])
      Directory.CreateDirectory(Path.Combine(tmp, "bin")) |> ignore
      try
        HostCoreAdoption.newestCandidateIdentity [ Path.Combine(tmp, "Some.fsproj") ]
        |> Expect.isNone "no SageFs.Core on disk yields None"
      finally
        try Directory.Delete(tmp, true) with _ -> ()
  ]
