module SageFs.Tests.MigrationWorthTests

/// WHY — `RestartAction` had two cases, and neither is the one a live value
/// deserves. `RespawnOnly` is right when NOTHING holds the old shape;
/// `RebuildProject` is right when a live value cannot be carried. Neither can
/// express "a live value exists, and every field of it can be carried" — which
/// needs no build AND must not lose the value.
///
/// The compiler enforced the new case at its one production site
/// (`AppRunOrchestration.fs`), which is the point of a DU: adding a possibility
/// makes every place that must handle it say so.

open Expecto
open Expecto.Flip
open SageFs
// NOT `open SageFs.RestartCost`: that module is `[<RequireQualifiedAccess>]`,
// and opening it makes every unqualified name here ambiguous with its own (the
// compiler says so directly, FS0892). Qualified names are the price of the
// module stating its intent, and paying it here is cheaper than a file where
// `decide` could mean two things.

let private held = LiveCount.HeldBy [ "order-store" ]
let private empty = LiveCount.HeldByNothing
let private unchecked = LiveCount.Unconsulted "no registry in this process"
let private failed = LiveCount.SourceFailed "the probe threw"

let private decide liveness worth =
  RestartCost.decideFromLivenessAndMigration liveness worth

let private rebuilds action = RestartCost.rebuilds action

let private legacy liveness = RestartCost.decideFromLiveCount liveness

[<Tests>]
let migrationWorthTests =
  testList "whether a restart may carry a live value" [

    testCase "WHY — a live value whose fields all carry needs NO build, and that is a THIRD outcome rather than a cheaper rebuild" <| fun _ ->
      let action = decide held (MigrationWorth.WorthCarrying 3)
      (match action with
       | RestartAction.MigrateAndRespawn _ -> ()
       | other -> failtestf "expected MigrateAndRespawn, got %A" other)
      (rebuilds action)
      |> Expect.isFalse "and it must not rebuild, or the value is discarded anyway"

    testCase "WHY — a live value that CANNOT be carried pays the build, and the message names the field that blocked it" <| fun _ ->
      let action = decide held (MigrationWorth.NotWorthCarrying "'Payload': no carry rule")
      (rebuilds action)
      |> Expect.isTrue "a value that cannot be carried must not be silently dropped"
      let why =
        match action with
        | RestartAction.RebuildProject w -> w
        | other -> failtestf "expected RebuildProject, got %A" other
      (why.Contains "Payload")
      |> Expect.isTrue "and the reason must reach the user, not just 'something'"

    testCase "WHY — a live value with NO verdict about carrying pays the build: absence of evidence is not evidence" <| fun _ ->
      // The two-sided invariant. A rule that only refuses when it is TOLD to
      // refuse is satisfied by an implementation that never carries anything,
      // so the un-answered case must also pay.
      let action = decide held (MigrationWorth.NoValueToMigrate "no live value")
      rebuilds action
      |> Expect.isTrue "a live value nobody has assessed must be rebuilt, not assumed carryable"

    testCase "WHY — 'nothing is live' wins over ANY migration verdict, because a verdict about a value that does not exist is vacuous" <| fun _ ->
      // A caller asking for the cheaper answer does not get it: the liveness
      // answer is the authority on WHETHER there is a value to carry.
      for worth in
        [ MigrationWorth.WorthCarrying 3
          MigrationWorth.NotWorthCarrying "cannot"
          MigrationWorth.NoValueToMigrate "no live value" ] do
        let action = decide empty worth
        (match action with
         | RestartAction.RespawnOnly _ -> ()
         | other -> failtestf "nothing live must respawn, got %A" other)

    testCase "WHY — liveness that was never established, or could not answer, pays the build REGARDLESS of a carryable verdict" <| fun _ ->
      // The safety property that a caller cannot route around: it cannot report
      // "worth carrying" and have the uncertainty in liveness ignored.
      for liveness in [ unchecked; failed ] do
        let action = decide liveness (MigrationWorth.WorthCarrying 3)
        (rebuilds action)
        |> Expect.isTrue "uncertain liveness pays the build even with a carryable verdict"

    testCase "WHY — MigrateAndRespawn is NOT foldable into RespawnOnly, because they differ in whether a live value survives" <| fun _ ->
      // If they were the same case, a caller could not tell a restart that keeps
      // live state from one that discards it — and the executor would have to
      // guess which it was doing.
      let carry = decide held (MigrationWorth.WorthCarrying 3)
      let respawn = decide empty (MigrationWorth.NoValueToMigrate "nothing live")
      ((match carry with
        | RestartAction.MigrateAndRespawn _ -> "carry"
        | _ -> "other"),
       (match respawn with
        | RestartAction.RespawnOnly _ -> "respawn"
        | _ -> "other"))
      |> Expect.equal "two distinct cases, because the value survives one" ("carry", "respawn")

    testCase "WHY — the pre-existing two-case decision is UNCHANGED, so no shipped behaviour moved" <| fun _ ->
      // decideFromLiveCount is still the two-case path the live path calls. It
      // must keep answering exactly what it did, or this addition would be a
      // silent behaviour change wearing a new function's clothes.
      (match legacy held with
       | RestartAction.RebuildProject _ -> ()
       | other -> failtestf "a held value still rebuilds, got %A" other)
      (match legacy empty with
       | RestartAction.RespawnOnly _ -> ()
       | other -> failtestf "an empty registry still respawns, got %A" other)
      (rebuilds (legacy unchecked))
      |> Expect.isTrue "and unconsulted still pays the build"
  ]
