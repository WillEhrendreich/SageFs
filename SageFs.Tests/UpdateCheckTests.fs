module SageFs.Tests.UpdateCheckTests

open System
open Expecto
open Expecto.Flip
open SageFs

let private v (text: string) = System.Version.Parse text

[<Tests>]
let tests =
  testList "UpdateCheck" [

    testList "InstallKind.classify" [
      testCase "an executable under the dotnet tools dir is a tool install" <| fun _ ->
        InstallKind.classify (Some "/home/will/.dotnet/tools/sagefs") "/home/will/.dotnet/tools"
        |> Expect.equal "tool install" InstallKind.ToolInstall

      testCase "is case-insensitive (Windows paths)" <| fun _ ->
        InstallKind.classify (Some @"C:\Users\will\.dotnet\tools\sagefs.exe") @"C:\Users\will\.dotnet\tools"
        |> Expect.equal "tool install" InstallKind.ToolInstall

      testCase "an executable anywhere else is a local build" <| fun _ ->
        InstallKind.classify (Some "/home/will/Work/SageFs/SageFs/bin/Release/net10.0/SageFs") "/home/will/.dotnet/tools"
        |> Expect.equal "local build" InstallKind.LocalBuild

      testCase "no known executable path at all is a local build (fail toward silence, never a false tool-install claim)" <| fun _ ->
        InstallKind.classify None "/home/will/.dotnet/tools"
        |> Expect.equal "local build" InstallKind.LocalBuild

      testProperty "never misclassifies a path that isn't under the tools dir as a tool install" <| fun (suffix: string) ->
        let safeSuffix = (suffix |> Option.ofObj |> Option.defaultValue "").Replace("\000", "")
        let path = "/some/other/place/" + safeSuffix
        InstallKind.classify (Some path) "/home/will/.dotnet/tools" = InstallKind.LocalBuild
    ]

    testList "shouldCheck" [
      testCase "never checked before is always due" <| fun _ ->
        UpdateCheck.shouldCheck DateTimeOffset.UtcNow None (TimeSpan.FromHours 6.0)
        |> Expect.isTrue "due"

      testCase "checked well inside the interval is not due" <| fun _ ->
        let now = DateTimeOffset.UtcNow
        UpdateCheck.shouldCheck now (Some(now - TimeSpan.FromMinutes 5.0)) (TimeSpan.FromHours 6.0)
        |> Expect.isFalse "not due"

      testCase "checked exactly at the interval boundary is due" <| fun _ ->
        let now = DateTimeOffset.UtcNow
        UpdateCheck.shouldCheck now (Some(now - TimeSpan.FromHours 6.0)) (TimeSpan.FromHours 6.0)
        |> Expect.isTrue "due at boundary"

      testCase "checked past the interval is due" <| fun _ ->
        let now = DateTimeOffset.UtcNow
        UpdateCheck.shouldCheck now (Some(now - TimeSpan.FromHours 7.0)) (TimeSpan.FromHours 6.0)
        |> Expect.isTrue "due"
    ]

    testList "normalize" [
      testCase "a three-component version gains no phantom distance against its own four-component form" <| fun _ ->
        // The exact bug this function exists to prevent: Version "0.6.823" has
        // Revision = -1 and would otherwise compare as LESS than "0.6.823.0".
        UpdateCheck.versionsBehind (v "0.6.823") (System.Version(0, 6, 823, 0))
        |> Expect.equal "no phantom distance" 0

      testCase "drops the revision component" <| fun _ ->
        UpdateCheck.normalize (System.Version(0, 6, 823, 7))
        |> Expect.equal "revision dropped" (System.Version(0, 6, 823))
    ]

    testList "tryParseVersion" [
      testCase "parses a clean release version" <| fun _ ->
        UpdateCheck.tryParseVersion "0.6.823"
        |> Expect.equal "parsed" (Some(v "0.6.823"))

      testCase "strips a prerelease suffix" <| fun _ ->
        UpdateCheck.tryParseVersion "0.6.823-rc.1"
        |> Expect.equal "parsed" (Some(v "0.6.823"))

      testCase "strips a build-metadata suffix (AssemblyInformationalVersion's +sourcerevisionid)" <| fun _ ->
        UpdateCheck.tryParseVersion "0.6.823+abcdef1234567890"
        |> Expect.equal "parsed" (Some(v "0.6.823"))

      testCase "an unparseable string is None, never an exception" <| fun _ ->
        UpdateCheck.tryParseVersion "not-a-version-at-all"
        |> Expect.equal "none" None

      testCase "empty string is None" <| fun _ ->
        UpdateCheck.tryParseVersion ""
        |> Expect.equal "none" None
    ]

    testList "versionsBehind" [
      testCase "identical versions are zero behind" <| fun _ ->
        UpdateCheck.versionsBehind (v "0.6.823") (v "0.6.823")
        |> Expect.equal "zero" 0

      testCase "a current version ahead of latest (unreleased local commit) is zero behind, never negative" <| fun _ ->
        UpdateCheck.versionsBehind (v "0.6.900") (v "0.6.823")
        |> Expect.equal "zero, not negative" 0

      testCase "same major.minor: the exact patch distance, matching the 43-behind incident" <| fun _ ->
        UpdateCheck.versionsBehind (v "0.6.647") (v "0.6.690")
        |> Expect.equal "43 behind" 43

      testCase "a minor/major bump is reported as at least 1, never a fabricated large number" <| fun _ ->
        UpdateCheck.versionsBehind (v "0.6.823") (v "0.7.0")
        |> Expect.equal "at least one" 1

      testProperty "never negative" <| fun (a: uint16) (b: uint16) ->
        let current = System.Version(0, 6, int a)
        let latest = System.Version(0, 6, int b)
        UpdateCheck.versionsBehind current latest >= 0

      testProperty "zero if and only if latest <= current (same major.minor)" <| fun (a: uint16) (b: uint16) ->
        let current = System.Version(0, 6, int a)
        let latest = System.Version(0, 6, int b)
        (UpdateCheck.versionsBehind current latest = 0) = (latest <= current)
    ]

    testList "bandUrgency" [
      testCase "under the mild ceiling is Mild" <| fun _ ->
        UpdateCheck.bandUrgency 1 |> Expect.equal "mild" Urgency.Mild

      testCase "at the mild ceiling rolls into Notable" <| fun _ ->
        UpdateCheck.bandUrgency UpdateCheck.mildCeiling |> Expect.equal "notable" Urgency.Notable

      testCase "at the notable ceiling rolls into Severe" <| fun _ ->
        UpdateCheck.bandUrgency UpdateCheck.notableCeiling |> Expect.equal "severe" Urgency.Severe

      testCase "the motivating incident (43 behind) is Severe" <| fun _ ->
        UpdateCheck.bandUrgency 43 |> Expect.equal "severe" Urgency.Severe

      testProperty "monotone: a larger distance is never a milder band" <| fun (small: uint8) (extra: uint8) ->
        let a = int small
        let b = a + int extra
        let rank = function Urgency.Mild -> 0 | Urgency.Notable -> 1 | Urgency.Severe -> 2
        rank (UpdateCheck.bandUrgency a) <= rank (UpdateCheck.bandUrgency b)
    ]

    testList "decide — the false-positive-avoidance contract" [
      testCase "a local build NEVER renders as anything but CheckSkipped NotAToolInstall, even when wildly behind" <| fun _ ->
        UpdateCheck.decide InstallKind.LocalBuild false (v "0.1.0") (Ok(v "99.0.0"))
        |> Expect.equal "skipped, not a warning" (UpdateOutcome.CheckSkipped SkipReason.NotAToolInstall)

      testCase "opting out NEVER renders as anything but CheckSkipped OptedOut, even when wildly behind" <| fun _ ->
        UpdateCheck.decide InstallKind.ToolInstall true (v "0.1.0") (Ok(v "99.0.0"))
        |> Expect.equal "skipped, not a warning" (UpdateOutcome.CheckSkipped SkipReason.OptedOut)

      testCase "a failed fetch NEVER renders as UpToDate — silence is correct, a false all-clear is not" <| fun _ ->
        match UpdateCheck.decide InstallKind.ToolInstall false (v "0.6.823") (Error "timed out") with
        | UpdateOutcome.CheckFailed "timed out" -> ()
        | other -> failtestf "expected CheckFailed, got %A" other

      testCase "current == latest is UpToDate" <| fun _ ->
        UpdateCheck.decide InstallKind.ToolInstall false (v "0.6.823") (Ok(v "0.6.823"))
        |> Expect.equal "up to date" (UpdateOutcome.UpToDate(v "0.6.823"))

      testCase "current behind latest is UpdateAvailable with the exact distance" <| fun _ ->
        UpdateCheck.decide InstallKind.ToolInstall false (v "0.6.647") (Ok(v "0.6.690"))
        |> Expect.equal "43 behind" (UpdateOutcome.UpdateAvailable(v "0.6.647", v "0.6.690", 43))

      testCase "current ahead of latest (unreleased local commit, but still an installed tool build) is UpToDate, not an error" <| fun _ ->
        UpdateCheck.decide InstallKind.ToolInstall false (v "0.6.900") (Ok(v "0.6.823"))
        |> Expect.equal "up to date" (UpdateOutcome.UpToDate(v "0.6.900"))

      testProperty "NEVER produces UpdateAvailable for a local build or an opted-out install, for ANY inputs"
      <| fun (localBuild: bool) (optedOut: bool) (currentRaw: uint16) (latestRaw: uint16) (fetchFailed: bool) (failureMsg: string) ->
        let installKind = if localBuild then InstallKind.LocalBuild else InstallKind.ToolInstall
        let current = System.Version(0, 6, int currentRaw)
        let latest = System.Version(0, 6, int latestRaw)
        let fetch = if fetchFailed then Error(failureMsg |> Option.ofObj |> Option.defaultValue "") else Ok latest
        let outcome = UpdateCheck.decide installKind optedOut current fetch
        match localBuild, optedOut with
        | true, _ | _, true ->
          match outcome with
          | UpdateOutcome.UpdateAvailable _ -> false
          | _ -> true
        | false, false -> true // no constraint here — covered by the exact-case tests above

      testProperty "describe never returns Some for CheckSkipped or CheckFailed — a caller can never render 'you are stale' from ignorance or failure"
      <| fun (behind: uint16) (reasonIdx: byte) (failureMsg: string) ->
        let skip = [| SkipReason.TooSoonSinceLastCheck; SkipReason.OptedOut; SkipReason.NotAToolInstall |].[int reasonIdx % 3]
        UpdateCheck.describe (UpdateOutcome.CheckSkipped skip) = None
        && UpdateCheck.describe (UpdateOutcome.CheckFailed (failureMsg |> Option.ofObj |> Option.defaultValue "")) = None
        && UpdateCheck.describe (UpdateOutcome.UpToDate(System.Version(0, 6, int behind))) = None
    ]

    testList "describe" [
      testCase "names the exact upgrade command, including the --version escape hatch" <| fun _ ->
        let text =
          UpdateCheck.describe (UpdateOutcome.UpdateAvailable(v "0.6.647", v "0.6.690", 43))
          |> Option.defaultValue ""
        text |> Expect.stringContains "names dotnet tool update" "dotnet tool update -g sagefs"
        text |> Expect.stringContains "names the --version escape hatch" "--version 0.6.690"
        text |> Expect.stringContains "names how far behind" "43"

      testCase "singular wording for exactly one version behind" <| fun _ ->
        UpdateCheck.describe (UpdateOutcome.UpdateAvailable(v "0.6.822", v "0.6.823", 1))
        |> Option.defaultValue ""
        |> Expect.stringContains "singular" "1 version behind"
    ]
  ]
