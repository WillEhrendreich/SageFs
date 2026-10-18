/// Issue #152: a destructive tool asks a person to confirm, and a client that advertises elicitation but cannot
/// answer (a headless agent, or a UI whose prompt never takes an answer) used to wait the full minute and then read
/// "stop_session was not confirmed", which sounds as if the stop failed. The session was still running and nothing
/// had been attempted. These cases drive the real `confirmWithin` with a fake that asks nothing of a person, so the
/// outcome is a typed value, the wait is bounded by a budget the test chooses, and the sentence says what it must.
module SageFs.Tests.ConfirmationUnansweredTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Protocol
open SageFs.Server.McpServer

let private canAsk = ClientCapabilities(Elicitation = ElicitationCapability())

/// A client that advertised elicitation and then never answers: the task only ends when the request is cancelled.
let private neverAnswers : ElicitRequestParams -> CancellationToken -> Task<ElicitResult> =
  fun _ token ->
    let pending = TaskCompletionSource<ElicitResult>()
    token.Register(Action(fun () -> pending.TrySetCanceled token |> ignore)) |> ignore
    pending.Task

let private answers (action: string) : ElicitRequestParams -> CancellationToken -> Task<ElicitResult> =
  fun _ _ -> Task.FromResult(ElicitResult(Action = action))

let private throws (message: string) : ElicitRequestParams -> CancellationToken -> Task<ElicitResult> =
  fun _ _ -> Task.FromException<ElicitResult>(InvalidOperationException message)

let private confirmTier = SageFs.Affordances.confirmationLosses |> Map.toList |> List.map (fst >> SageFs.Affordances.ToolName.toString)

[<Tests>]
let tests =
  testList "Confirmation that is never answered" [

    testTask "WHY — a client that never answers is refused inside the budget it was given, as NoAnswerInTime, not parked for a minute" {
      let work = confirmWithin TestTimeouts.confirmationBudget canAsk neverAnswers "stop_session" CancellationToken.None
      let! finished = Task.WhenAny(work, Task.Delay TestTimeouts.patienceBrief)
      Object.ReferenceEquals(finished, work) |> Expect.isTrue "the refusal arrived before the test's own ceiling"
      let! outcome = work
      outcome |> Expect.equal "the silence is a typed refusal that carries the wait" (Result.Error(NoAnswerInTime TestTimeouts.confirmationBudget))
    }

    testTask "WHY — the sentence for an unanswered prompt says nothing was done and the session is still running, which the old text never did" {
      let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk neverAnswers "stop_session" CancellationToken.None
      match outcome with
      | Ok() -> failtest "silence must never become permission"
      | Result.Error refusal ->
        let text = ConfirmationRefusal.describe "stop_session" refusal
        text |> Expect.stringContains "it says nothing was done" "Nothing was done"
        text |> Expect.stringContains "it says the session is still running" "still running"
        text |> Expect.stringContains "it names the way out that asks nobody" "POST /api/sessions/stop"
    }

    testCase "WHY — every refusal says nothing was done, so no outcome can be read as a stop that failed halfway" <| fun _ ->
      [ Declined
        Cancelled
        AnsweredOther "maybe"
        NoAnswerInTime TestTimeouts.confirmationBudget
        CouldNotBeAsked "the transport closed" ]
      |> List.iter (fun refusal ->
        let text = ConfirmationRefusal.describe "stop_session" refusal
        text |> Expect.stringContains (sprintf "%A names the tool" refusal) "stop_session"
        text |> Expect.stringContains (sprintf "%A says nothing was done" refusal) "Nothing was done"
        text |> Expect.stringContains (sprintf "%A says the session is still running" refusal) "still running")

    testCase "WHY — when nobody can answer, each confirm-tier tool names the daemon route that does the same job without asking" <| fun _ ->
      confirmTier
      |> List.iter (fun tool ->
        let text = ConfirmationRefusal.describe tool (NoAnswerInTime TestTimeouts.confirmationBudget)
        text |> Expect.stringContains (sprintf "%s names a route a script can call" tool) "POST /"
        text |> Expect.stringContains (sprintf "%s says there is no switch for the prompt" tool) "no setting"
        text |> Expect.stringContains (sprintf "%s says how long it waited" tool) (string TestTimeouts.confirmationBudget))

    testTask "WHY — a client whose request throws is CouldNotBeAsked with the reason, so a broken transport is not reported as a person saying no" {
      let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk (throws "transport closed") "stop_session" CancellationToken.None
      outcome |> Expect.equal "the failure is typed and carries its reason" (Result.Error(CouldNotBeAsked "transport closed"))
      match outcome with
      | Ok() -> failtest "a prompt that could not be asked is not an approval"
      | Result.Error refusal ->
        ConfirmationRefusal.describe "stop_session" refusal |> Expect.stringContains "the reason reaches the caller" "transport closed"
    }

    testTask "WHY — a decline is Declined and not a timeout, so a person's no is told apart from silence" {
      let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk (answers "decline") "stop_session" CancellationToken.None
      outcome |> Expect.equal "decline is its own case" (Result.Error Declined)
    }

    testTask "WHY — a cancelled prompt is Cancelled, kept apart from a decline" {
      let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk (answers "cancel") "stop_session" CancellationToken.None
      outcome |> Expect.equal "cancel is its own case" (Result.Error Cancelled)
    }

    testTask "WHY — an accepted prompt lets the call run, so the bound did not turn the prompt into a refusal" {
      let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk (answers "accept") "stop_session" CancellationToken.None
      outcome |> Expect.isOk "accept proceeds"
    }

    testTask "WHY — a client that cannot be asked is never asked, so the fake is not even called" {
      let mutable asked = 0
      let counting _ _ =
        asked <- asked + 1
        Task.FromResult(ElicitResult(Action = "accept"))
      let! outcome = confirmWithin TestTimeouts.confirmationBudget (ClientCapabilities()) counting "stop_session" CancellationToken.None
      outcome |> Expect.isOk "a client without elicitation is skipped, as before"
      asked |> Expect.equal "nothing was asked" 0
    }

    testTask "WHY — a caller who cancels the whole call is not told its confirmation timed out, because the wait did not run out" {
      let alreadyCancelled = CancellationToken(true)
      let! caught =
        task {
          try
            let! _ = confirmWithin TestTimeouts.confirmationBudget canAsk neverAnswers "stop_session" alreadyCancelled
            return false
          with :? OperationCanceledException -> return true
        }
      caught |> Expect.isTrue "the caller's own cancellation surfaces as a cancellation"
    }
  ]
