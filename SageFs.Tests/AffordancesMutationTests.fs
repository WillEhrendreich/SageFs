/// ## Affordances Mutation Tests
///
/// Proves the test suite catches mutations in the per-state MCP tool gating
/// model — the mechanism meant to stop an agent from calling
/// `send_fsharp_code` on a session that isn't Ready. A dropped tool name, a
/// swapped ToolGate classification, or a fail-open default here would defeat
/// the whole gate silently. Each case asserts EXACT equality/membership
/// against the correct value so a mutant that returns any other wrong value
/// is killed too.
module AffordancesMutationTests

open System
open Expecto
open Expecto.Flip
open SageFs

let affordancesMutationTests = testList "Affordances mutations" [

  // ── availableTools: state-specific membership ───────────────────────────

  testCase "WHY — uninitialized_cannot_send_code — Uninitialized must not expose send_fsharp_code" <| fun () ->
    Affordances.availableTools Uninitialized
    |> List.contains "send_fsharp_code"
    |> Expect.isFalse "a session with no worker yet must never advertise send_fsharp_code"

  testCase "WHY — ready_can_send_code — Ready must expose send_fsharp_code" <| fun () ->
    Affordances.availableTools Ready
    |> List.contains "send_fsharp_code"
    |> Expect.isTrue "a Ready session must advertise send_fsharp_code"

  testCase "WHY — warmingUp_cannot_send_code — a warming-up worker cannot yet accept eval" <| fun () ->
    Affordances.availableTools WarmingUp
    |> List.contains "send_fsharp_code"
    |> Expect.isFalse "WarmingUp must not advertise send_fsharp_code"

  testCase "WHY — evaluating_cannot_send_code_but_can_cancel — mid-eval, only cancel_eval is meaningful, not a second send" <| fun () ->
    let tools = Affordances.availableTools Evaluating
    (tools |> List.contains "send_fsharp_code", tools |> List.contains "cancel_eval")
    |> Expect.equal "Evaluating must NOT advertise send_fsharp_code but MUST advertise cancel_eval"
      (false, true)

  testCase "WHY — faulted_can_reset_but_not_send — a faulted session's only forward paths are reset/hard-reset/create, never eval" <| fun () ->
    let tools = Affordances.availableTools Faulted
    (tools |> List.contains "send_fsharp_code",
     tools |> List.contains "reset_fsi_session",
     tools |> List.contains "hard_reset_fsi_session",
     tools |> List.contains "create_session")
    |> Expect.equal "Faulted must offer reset/hard-reset/create but never send_fsharp_code"
      (false, true, true, true)

  testCase "WHY — every_state_can_list_sessions_and_check_fsi_status — the baseline monitoring tools must never disappear from any state" <| fun () ->
    [ Uninitialized; WarmingUp; Ready; Evaluating; Faulted ]
    |> List.forall (fun s ->
      let tools = Affordances.availableTools s
      List.contains "list_sessions" tools && List.contains "get_fsi_status" tools)
    |> Expect.isTrue "list_sessions and get_fsi_status must be present in every SessionState"

  // ── checkToolAvailability ────────────────────────────────────────────────

  testCase "WHY — checkToolAvailability_ok_when_listed" <| fun () ->
    Affordances.checkToolAvailability Ready "send_fsharp_code"
    |> Expect.equal "a listed tool in a matching state must be Ok ()" (Ok ())

  testCase "WHY — checkToolAvailability_error_when_not_listed_carries_state_and_available_tools" <| fun () ->
    match Affordances.checkToolAvailability Uninitialized "send_fsharp_code" with
    | Error (SageFsError.ToolNotAvailable (tool, state, available)) ->
      (tool, state, List.contains "send_fsharp_code" available)
      |> Expect.equal "the error must name the requested tool, the actual state, and the REAL available list (which excludes the tool)"
        ("send_fsharp_code", Uninitialized, false)
    | other -> failwithf "expected ToolNotAvailable, got %A" other

  // ── gatingDomain / toolGate / checkToolCallAllowed ──────────────────────

  testCase "WHY — alwaysAvailable_tool_bypasses_state — get_fsi_status must be callable even when Faulted" <| fun () ->
    Affordances.checkToolCallAllowed Faulted "get_fsi_status"
    |> Expect.equal "an AlwaysAvailable tool must succeed regardless of session state" (Ok ())

  testCase "WHY — stateGated_tool_respects_state — send_fsharp_code must be gated by the ACTUAL current state" <| fun () ->
    (Affordances.checkToolCallAllowed Ready "send_fsharp_code",
     Affordances.checkToolCallAllowed Faulted "send_fsharp_code" |> Result.isOk)
    |> Expect.equal "send_fsharp_code must succeed when Ready and fail when Faulted"
      (Ok (), false)

  testCase "WHY — undeclared_tool_fails_closed_in_every_state — an unknown tool name must NEVER bypass the gate" <| fun () ->
    [ Uninitialized; WarmingUp; Ready; Evaluating; Faulted ]
    |> List.forall (fun s -> Affordances.checkToolCallAllowed s "totally_unknown_tool_xyz" |> Result.isError)
    |> Expect.isTrue "an undeclared tool must fail closed in every single session state"

  testCase "WHY — toolGate_stop_session_is_always_available" <| fun () ->
    Affordances.toolGate "stop_session"
    |> Expect.equal "stop_session must be declared AlwaysAvailable (you must be able to stop a session in any state)"
      (Some Affordances.ToolGate.AlwaysAvailable)

  testCase "WHY — toolGate_send_fsharp_code_is_stateGated" <| fun () ->
    Affordances.toolGate "send_fsharp_code"
    |> Expect.equal "send_fsharp_code must be declared StateGated" (Some Affordances.ToolGate.StateGated)

  testCase "WHY — toolGate_unknown_tool_is_none" <| fun () ->
    Affordances.toolGate "not_a_real_tool"
    |> Expect.equal "an undeclared tool name must resolve to None, not a default gate" None

  testCase "WHY — declaredGateTools_contains_every_state_gated_and_always_available_tool" <| fun () ->
    let declared = Affordances.declaredGateTools
    (List.contains "send_fsharp_code" declared, List.contains "stop_session" declared)
    |> Expect.equal "both a representative StateGated and AlwaysAvailable tool must appear in the declared set" (true, true)

  // ── EvalStats ─────────────────────────────────────────────────────────────

  testCase "WHY — evalStats_first_record_sets_min_max_equal_to_duration" <| fun () ->
    let stats = Affordances.EvalStats.record (TimeSpan.FromSeconds 3.0) Affordances.EvalStats.empty
    (stats.EvalCount, stats.TotalDuration, stats.MinDuration, stats.MaxDuration)
    |> Expect.equal "the FIRST recorded duration must set count=1 and Total=Min=Max=that duration"
      (1, TimeSpan.FromSeconds 3.0, TimeSpan.FromSeconds 3.0, TimeSpan.FromSeconds 3.0)

  testCase "WHY — evalStats_second_record_tracks_true_min_and_max — min/max must not just track the latest value" <| fun () ->
    let stats =
      Affordances.EvalStats.empty
      |> Affordances.EvalStats.record (TimeSpan.FromSeconds 5.0)
      |> Affordances.EvalStats.record (TimeSpan.FromSeconds 1.0)
    (stats.EvalCount, stats.TotalDuration, stats.MinDuration, stats.MaxDuration)
    |> Expect.equal "after 5s then 1s: count=2, total=6s, min=1s (not 5s), max=5s (not 1s)"
      (2, TimeSpan.FromSeconds 6.0, TimeSpan.FromSeconds 1.0, TimeSpan.FromSeconds 5.0)

  testCase "WHY — evalStats_averageDuration_divides_total_by_count" <| fun () ->
    let stats =
      Affordances.EvalStats.empty
      |> Affordances.EvalStats.record (TimeSpan.FromSeconds 2.0)
      |> Affordances.EvalStats.record (TimeSpan.FromSeconds 4.0)
    Affordances.EvalStats.averageDuration stats
    |> Expect.equal "the average of 2s and 4s must be exactly 3s" (TimeSpan.FromSeconds 3.0)

  testCase "WHY — evalStats_averageDuration_of_empty_is_zero_not_a_divide_by_zero_crash" <| fun () ->
    Affordances.EvalStats.averageDuration Affordances.EvalStats.empty
    |> Expect.equal "averaging zero evals must be TimeSpan.Zero, never throw" TimeSpan.Zero
]
