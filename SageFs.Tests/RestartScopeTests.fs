module SageFs.Tests.RestartScopeTests

/// WHY — `type-migration-direction.md` step 1's whole payoff. Today
/// `RestartReason.TypeShapeChanged typeName` carries no scope, so
/// `RestartReason.remedy` can only ever produce the generic "Restart the app"
/// (ReloadOutcome.fs:138-141) — the same sentence for every type change, no
/// matter how little was actually affected.
///
/// The contract: a type change whose only possible holder is one known unit
/// gets a remedy scoped to that unit; anything we cannot attribute falls back
/// to restarting everything. Failing toward "everything" is the safe default:
/// a too-narrow restart would leave old instances alive somewhere in the
/// process, which is precisely the bug this work exists to prevent.
open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.GranularRestart

/// A unit that is known to hold values of a given type. The pair is the whole
/// attribution input: we never guess beyond what is declared here.
let private units =
  [ { Name = "order-store"; DeclaresType = "UserRecord" }
    { Name = "session-cache"; DeclaresType = "Session" } ]

[<Tests>]
let restartScopeTests = testList "restart scope" [

  testCase "WHY — a type change only one unit can hold gets a remedy scoped to that unit" <| fun _ ->
    RestartReason.TypeShapeChanged("UserRecord", RestartScope.Scoped "order-store")
    |> RestartReason.remedy
    |> Expect.stringContains "the remedy must name the unit that will be restarted" "order-store"

  testCase "WHY — an unattributable type change still restarts everything, because a narrow guess would strand old instances" <| fun _ ->
    RestartReason.TypeShapeChanged("Mystery", RestartScope.Everything)
    |> RestartReason.remedy
    |> Expect.stringContains "the fallback must still say restart the app" "Restart the app"

  testCase "WHY — the scope a reason is given must be the scope its remedy reports" <| fun _ ->
    // If these ever disagree the user is told to do one thing and the product
    // does another, which is worse than either being consistently coarse.
    let scoped = RestartReason.TypeShapeChanged("UserRecord", RestartScope.Scoped "order-store") |> RestartReason.remedy
    let coarse = RestartReason.TypeShapeChanged("UserRecord", RestartScope.Everything) |> RestartReason.remedy

    (scoped = coarse)
    |> Expect.isFalse "a scoped remedy must differ from the coarse one for the same type"

  testCase "WHY — a scoped remedy never tells the user to restart the whole app" <| fun _ ->
    let remedy =
      RestartReason.TypeShapeChanged("UserRecord", RestartScope.Scoped "order-store")
      |> RestartReason.remedy

    remedy.Contains "Restart the app"
    |> Expect.isFalse
      "a unit-scoped remedy that still says 'Restart the app' is the generic sentence wearing a new hat"

  testCase "WHY — an unattributed type change is stated as unattributed, not guessed at" <| fun _ ->
    // A type nobody declared a unit for must resolve to Everything. This is
    // the direction the inference must fail: a too-narrow restart strands
    // old instances, a too-wide one is merely slow.
    RestartScope.infer units "Mystery"
    |> Expect.equal "an unknown type must never be scoped to a guessed unit" RestartScope.Everything

  testCase "WHY — a declared unit is the only thing that can narrow a restart" <| fun _ ->
    RestartScope.infer units "UserRecord"
    |> Expect.equal "a known unit must scope the restart" (RestartScope.Scoped "order-store")
  ]
