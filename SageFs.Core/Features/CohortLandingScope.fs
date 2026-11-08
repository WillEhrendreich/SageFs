namespace SageFs.Features

open System
open SageFs.Features.LiveTesting

/// Which of a landing's affected tests the landing gate can run in the integration session, and why the others are not run
/// there (finding C-failing of the cohort run).
///
/// Two landings of pure additions were each Blocked by the same hundred tests. Nearly all were `[Integration]` tests, suites that
/// start real processes, daemons and ports (`ProcessEnvironmentTests.realSpawnTests`, `SessionBuildColdTreeTests.coldBuildTests`,
/// `CohortDogfoodIntegrationTests`). Inside a session each ended as `Skipped "Timed out after 5s"`, the per-test timeout, and
/// `CohortLandingVerify.failingOf` is fail-closed on purpose: anything but a confirmed Passed counts as failing. The rule is right
/// for a gate and the input was wrong. The affected set held tests that cannot pass inside a session, so every landing that
/// touched code they cover was Blocked by tests that never ran.
///
/// The release gate runs those tiers itself (`--integration-host`, and the dedicated browser and editor entry points). So the
/// landing gate does not ask a session to run them, says so, and NAMES each one it left out in the landing's report, so
/// nothing is hidden. `failingOf` stays fail-closed for everything the gate does run.
///
/// The tag is the structural one: `TestInfrastructure.Integration.register` prefixes every registered suite's name with
/// "[Integration] ", and the default suite refuses to run when a tagged test bypassed the registry, so a test carries the tag
/// exactly when the registry owns it. A session discovers full names, so it can see the tag. Pure.
module CohortLandingScope =

  /// The prefix `TestInfrastructure.Integration.register` puts on a registered suite's name.
  [<Literal>]
  let integrationTag = "[Integration]"

  /// Why a test is not run by the landing gate, in words for the landing's report.
  [<Literal>]
  let notRunReason =
    "an [Integration] test: it starts real processes, daemons and ports, which the integration session cannot, and the release gate's --integration-host tier runs it"

  /// Does the test name carry the registry's tag? A full name has it somewhere along its list path.
  let isRegisteredIntegration (fullName: string) : bool =
    fullName.Contains(integrationTag, StringComparison.Ordinal)

  /// A test the landing gate leaves to the release gate.
  type Excluded = {
    Id: string
    Name: string
  }

  /// What the gate runs and what it names as left to the release gate.
  type Scope = {
    /// The ids to run in the session, in the order given.
    Run: string list
    /// The tests left out, by name.
    NotRun: Excluded list
  }

  /// Split `ids` by whether the session can run them. `fullNameOf` answers with a test's full name, or nothing when the session
  /// does not know the id: an id it cannot name is run, never skipped, so the gate fails closed on what it cannot read.
  let scope (fullNameOf: string -> string option) (ids: string list) : Scope =
    let named = ids |> List.map (fun id -> id, fullNameOf id)
    let isLeftOut (_, name) =
      match name with
      | Some fullName -> isRegisteredIntegration fullName
      | None -> false
    let left, kept = named |> List.partition isLeftOut
    { Run = kept |> List.map fst
      NotRun =
        left
        |> List.choose (fun (id, name) -> name |> Option.map (fun fullName -> { Id = id; Name = fullName }))
        |> List.sortBy (fun excluded -> excluded.Name, excluded.Id) }

  // -- A test that did not pass, and why --

  /// Why a test that was in the run did not come back Passed, when it did not FAIL: it could not run. A test that failed its
  /// assertions has no reason here and is a failing test. A per-test timeout ends as `Skipped "Timed out after 5s"`, which is the
  /// case that blocked the two landings: it is "could not run", not a failure of the landing's code.
  let couldNotRun (result: TestResult option) : string option =
    let lower (text: string) = match text with "" -> text | _ -> string (Char.ToLowerInvariant text.[0]) + text.Substring 1
    match result with
    | Some (TestResult.Passed _) -> None
    | Some (TestResult.Failed (TestFailure.AssertionFailed _, _)) -> None
    | Some (TestResult.Failed (TestFailure.ExceptionThrown _, _)) -> None
    | Some (TestResult.Failed (TestFailure.TimedOut after, _)) -> Some (sprintf "timed out after %gs" after.TotalSeconds)
    | Some (TestResult.Skipped reason) -> Some (lower reason)
    | Some TestResult.NotRun -> Some "it was not run"
    | Some (TestResult.NoResult why) -> Some (NoResultReason.describe why)
    | None -> Some "no result was reported for it"
