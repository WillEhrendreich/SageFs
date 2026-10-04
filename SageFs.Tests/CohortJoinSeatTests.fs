module SageFs.Tests.CohortJoinSeatTests

// An agent that joined a repository's cohort could not tell which conductor seat it was in. The reply only
// spoke about the seat when THAT join created it, so a member who joined an existing cohort (after a daemon
// restart its connection is a new identity, and its old self may still hold the seat) was told nothing
// about who the conductor was, or whether the seat had lapsed. Every join now says which seat the caller holds.

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.McpTools
open SageFs.MemberTable
open SageFs.Tests.ToolAuthorityGateTests

let me = MemberId.Minted "molina-agent"
let someoneElse = MemberId.Minted "someone-else"
let seatEmptiedAt = DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)

/// Every way a seat can have come to be empty. A new case is a compile error in `vacancyWhy`.
let everyVacancyReason =
  [ VacancyReason.ConductorLeft; VacancyReason.LeaseLapsed; VacancyReason.Revoked; VacancyReason.NeverBound ]

let sentence (binding: ConductorBinding<MemberId>) = McpCohortTools.seatSentence me binding

[<Tests>]
let seatSentenceTests =
  testList "seatSentence" [

    testCase "the seat's holder is told it is the conductor" <| fun _ ->
      sentence (ConductorBinding.Bound me)
      |> Expect.stringContains "you hold it" "You are the conductor"

    testCase "a member is told WHO the conductor is, and is not told it is the conductor" <| fun _ ->
      let text = sentence (ConductorBinding.Bound someoneElse)
      text |> Expect.stringContains "names the holder" (MemberId.display someoneElse)
      Expect.isFalse "it must not claim a seat it does not hold" (text.Contains "You are the conductor")

    testCase "a vacant seat says it is vacant and that nobody holds authority, for every reason" <| fun _ ->
      for why in everyVacancyReason do
        let text = sentence (ConductorBinding.Vacant(someoneElse, seatEmptiedAt, why))
        text |> Expect.stringContains (sprintf "%A: says the seat is empty" why) "vacant"
        text |> Expect.stringContains (sprintf "%A: says what that means" why) "Nobody holds conductor authority"

    testCase "each reason a seat is empty reads differently, so the explanation is never collapsed" <| fun _ ->
      let sentences = everyVacancyReason |> List.map (fun why -> sentence (ConductorBinding.Vacant(someoneElse, seatEmptiedAt, why)))
      sentences |> List.distinct |> List.length
      |> Expect.equal "four reasons, four different sentences" (List.length everyVacancyReason)

    testCase "a cohort that never had a conductor says so" <| fun _ ->
      sentence ConductorBinding.NeverBound
      |> Expect.stringContains "says there is none" "no conductor"
  ]

let joinText (ctx: McpContext) (agent: string) (repo: string) = task {
  match! McpCohortTools.joinCohort ctx agent "Implementer" (Some repo) with
  | Result.Ok text -> return text
  | Result.Error err -> return failtestf "%s could not join %s: %s" agent repo (SageFsError.describeForAgent err)
}

[<Tests>]
let joinReplyTests =
  testList "the reply to join_cohort" [

    testTask "the first joiner is told it created the seat" {
      let ctx = mkContext ()
      let! first = joinText ctx "molina-conductor" molinaRepo
      first |> Expect.stringContains "created the seat" "You are the conductor (first to join in this scope)"
    }

    testTask "a newcomer to an existing cohort is told who the conductor is" {
      // The case from the report: the cohort already has a conductor, and the new member must be able to read
      // that from the reply instead of guessing.
      let ctx = mkContext ()
      let! _ = joinText ctx "molina-conductor" molinaRepo
      let! newcomer = joinText ctx "molina-newcomer" molinaRepo
      newcomer |> Expect.stringContains "names the conductor" "The conductor is molina-conductor"
      Expect.isFalse "and does not claim the seat" (newcomer.Contains "You are the conductor")
    }

    testTask "a newcomer to a cohort whose conductor left is told the seat is vacant and why" {
      let ctx = mkContext ()
      let! _ = joinText ctx "molina-conductor" molinaRepo
      match! McpCohortTools.leaveCohort ctx "molina-conductor" (Some molinaRepo) with
      | Result.Ok _ -> ()
      | Result.Error err -> failtestf "the conductor could not leave: %s" (SageFsError.describeForAgent err)
      let! newcomer = joinText ctx "molina-newcomer" molinaRepo
      newcomer |> Expect.stringContains "says the seat is vacant" "vacant"
      newcomer |> Expect.stringContains "says why" (McpCohortTools.vacancyWhy VacancyReason.ConductorLeft)
    }
  ]
