module SageFs.Tests.StartEscalationTests

/// Starting a worker on a machine whose speed is not known in advance: the first attempt's patience
/// comes from the tier and from what the machine has taught, an attempt that runs out of patience is
/// retried with MORE, never the same and never less, and when it is given up on the failure says what
/// it was waiting for. The seeded model of the whole loop is in StartEscalationSimTests.fs.
open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.StartEscalationTimeouts

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private first = StartEscalation.firstBudget StageHistory.NeverSeen silenceAllowance absoluteBound

let private silentTimeout (budget: StartBudget) : StartTimeout =
  { Stage = StartStage.WorkerPort
    Budget = budget
    Waited = budget.Inactivity
    Progress = ProgressSeen.NoneYet }

let private learned : StageHistory =
  StageHistory.Seen
    { Stage = StartStage.WorkerPort
      SmoothedMs = learnedStart.TotalMilliseconds
      DeviationMs = learnedDeviation.TotalMilliseconds
      Samples = 9 }

/// Drive the real escalation until it gives up, collecting every budget it hands out.
let private schedule (history: StageHistory) (budget: StartBudget) : StartBudget list * StartFailure =
  let rec go (budget: StartBudget) (given: StartBudget list) =
    match StartEscalation.next MachineTier.Standard history (silentTimeout budget) with
    | EscalationStep.RetryWith bigger -> go bigger (given @ [ budget ])
    | EscalationStep.GiveUp failure -> given @ [ budget ], failure
  go budget []

[<Tests>]
let tests =
  testList "Start escalation" [

    testList "the first attempt's patience" [

      testCase "WHY — with no history the first attempt gets the tier's silence allowance and the absolute bound" <| fun _ ->
        first.Attempt |> Expect.equal "first attempt" 1
        first.Inactivity |> Expect.equal "the tier's allowance" silenceAllowance
        first.Absolute |> Expect.equal "the absolute bound" absoluteBound
        first.Earlier |> Expect.isEmpty "nothing came before it"

      testCase "WHY — a machine that has shown starts take 90 s is never given less than that again, even if the tier says 30 s" <| fun _ ->
        let budget = StartEscalation.firstBudget learned silenceAllowance absoluteBound
        (budget.Inactivity >= learnedStart) |> Expect.isTrue "at least the learned start"
        (budget.Inactivity >= silenceAllowance) |> Expect.isTrue "at least the tier's allowance"

      testCase "WHY — a history of quick starts does not shorten the tier's allowance, which also covers a cold host build" <| fun _ ->
        let quick =
          StageHistory.Seen { Stage = StartStage.WorkerPort; SmoothedMs = quickStart.TotalMilliseconds; DeviationMs = 0.0; Samples = 20 }
        (StartEscalation.firstBudget quick silenceAllowance absoluteBound).Inactivity
        |> Expect.equal "the tier's allowance stands" silenceAllowance

      testPropertyWithConfig propConfig "the first attempt is never given more silence than the absolute bound allows" <|
        fun (PositiveInt staticSeconds) (PositiveInt absoluteSeconds) ->
          let absolute = TimeSpan.FromSeconds(float absoluteSeconds)
          let budget = StartEscalation.firstBudget learned (TimeSpan.FromSeconds(float staticSeconds)) absolute
          budget.Inactivity <= absolute
    ]

    testList "what a timeout leads to" [

      testCase "WHY — an attempt that ran out of patience is retried with twice as much, and the attempt before it is on record" <| fun _ ->
        match StartEscalation.next MachineTier.Fast StageHistory.NeverSeen (silentTimeout first) with
        | EscalationStep.RetryWith bigger ->
          bigger.Attempt |> Expect.equal "second attempt" 2
          bigger.Inactivity |> Expect.equal "doubled" (TimeSpan.FromTicks(first.Inactivity.Ticks * 2L))
          bigger.Earlier |> Expect.equal "the first attempt is recorded" [ { Budget = first.Inactivity; Waited = first.Inactivity } ]
        | EscalationStep.GiveUp failure -> failtestf "expected a retry, got %A" failure

      testCase "WHY — a start that needs 40 s, which the 30 s allowance cannot give, is rescued by the second attempt (the Phenom II's case)" <| fun _ ->
        let budgets, _ = schedule StageHistory.NeverSeen first
        (budgets |> List.exists (fun b -> b.Inactivity >= slowHealthyStart)) |> Expect.isTrue "some attempt is patient enough"

      testCase "WHY — attempts are limited, so a hung start is given up on instead of retried for ever" <| fun _ ->
        let budgets, failure = schedule StageHistory.NeverSeen first
        budgets |> List.length |> Expect.isLessThanOrEqual "within the limit" StartEscalation.MaxAttempts
        failure.Attempts |> Expect.equal "the failure counts the attempts made" (List.length budgets)

      testCase "WHY — once the patience is at the absolute bound there is no retry with the same patience, only the failure" <| fun _ ->
        let atBound = { first with Inactivity = absoluteBound }
        match StartEscalation.next MachineTier.Fast StageHistory.NeverSeen (silentTimeout atBound) with
        | EscalationStep.GiveUp _ -> ()
        | EscalationStep.RetryWith retry -> failtestf "expected a failure, got a retry: %A" retry

      testPropertyWithConfig propConfig "every retry has strictly more patience than the attempt before, from any starting allowance" <|
        fun (PositiveInt staticSeconds) ->
          let start = StartEscalation.firstBudget StageHistory.NeverSeen (TimeSpan.FromSeconds(float staticSeconds)) absoluteBound
          let budgets, _ = schedule StageHistory.NeverSeen start
          budgets |> List.map (fun b -> b.Inactivity) |> List.pairwise |> List.forall (fun (a, b) -> b > a)

      testPropertyWithConfig propConfig "the whole schedule ends, within the attempt limit, with every allowance inside the absolute bound" <|
        fun (PositiveInt staticSeconds) ->
          let start = StartEscalation.firstBudget learned (TimeSpan.FromSeconds(float staticSeconds)) absoluteBound
          let budgets, _ = schedule learned start
          List.length budgets <= StartEscalation.MaxAttempts
          && budgets |> List.forall (fun b -> b.Inactivity <= absoluteBound)
    ]

    testList "the failure" [

      testCase "WHY — a given-up start carries what it waited for, each allowance, the total, the attempts, the tier and what the machine usually does" <| fun _ ->
        let budgets, failure = schedule learned (StartEscalation.firstBudget learned silenceAllowance absoluteBound)
        failure.Stage |> Expect.equal "stage" StartStage.WorkerPort
        failure.Budgets |> Expect.equal "every allowance, in order" (budgets |> List.map (fun b -> b.Inactivity))
        failure.TotalWaited |> Expect.equal "the total is what was waited" (budgets |> List.sumBy (fun b -> b.Inactivity.Ticks) |> TimeSpan.FromTicks)
        failure.Tier |> Expect.equal "tier" MachineTier.Standard
        failure.Progress |> Expect.equal "no progress was ever reported" ProgressSeen.NoneYet
        failure.Expectation |> Expect.equal "what this machine usually does" (Expectation.Typically (learnedStart, 9))

      testCase "WHY — the text names what it waited for, how long, how many times, the tier, and what to do, so nobody has to guess" <| fun _ ->
        let _, failure = schedule StageHistory.NeverSeen first
        let text = StartEscalation.describe failure
        text |> Expect.stringContains "what it was waiting for" (StartStage.describe StartStage.WorkerPort)
        text |> Expect.stringContains "the tier" "Standard"
        text |> Expect.stringContains "the attempts" (sprintf "%d attempts" failure.Attempts)
        text |> Expect.stringContains "the next step on the tier ladder" "SAGEFS_MACHINE_TIER=Constrained"
        text |> Expect.stringContains "a way out" "hard_reset_fsi_session"
        text |> Expect.stringContains "the knob that was too small" "SAGEFS_WARMUP_INACTIVITY_SECONDS"
        for budget in failure.Budgets do
          text |> Expect.stringContains "each allowance" (sprintf "%.0f s" budget.TotalSeconds)

      testCase "WHY — the text is not the old vague 'abandoned after max retries' and is not blank" <| fun _ ->
        let _, failure = schedule StageHistory.NeverSeen first
        let text = StartEscalation.describe failure
        text.Contains("abandoned after max retries", StringComparison.Ordinal) |> Expect.isFalse "not the old message"
        text.Contains("exited with code", StringComparison.Ordinal) |> Expect.isFalse "not an exit code"

      testCase "WHY — on the slowest tier the advice does not point past the end of the ladder" <| fun _ ->
        let failure =
          { Stage = StartStage.WorkerPort
            Attempts = 1
            Budgets = [ silenceAllowance ]
            TotalWaited = silenceAllowance
            Progress = ProgressSeen.Last "2/4 loading"
            Tier = MachineTier.Minimal
            Expectation = Expectation.NoHistory }
        let text = StartEscalation.describe failure
        text |> Expect.stringContains "the last tier" "SAGEFS_MACHINE_TIER=Minimal"
        text |> Expect.stringContains "the last thing it said" "2/4 loading"
        text |> Expect.stringContains "one attempt is not pluralised" "1 attempt waiting"
    ]

    testList "telling a person the wait is expected" [

      testCase "WHY — a fast or standard machine is not told anything, because its starts are short" <| fun _ ->
        for tier in [ MachineTier.Fast; MachineTier.Standard ] do
          StartEscalation.notice tier StageHistory.NeverSeen |> Expect.equal "no notice" StartEscalation.SlowStartNotice.NotSlow
          StartEscalation.describeNotice (StartEscalation.notice tier StageHistory.NeverSeen) |> Expect.equal "no text" ""

      testCase "WHY — a constrained machine is told its first start can take a minute or more, and that this is normal" <| fun _ ->
        let text = StartEscalation.describeNotice (StartEscalation.notice MachineTier.Constrained StageHistory.NeverSeen)
        text |> Expect.stringContains "the tier" "Constrained"
        text |> Expect.stringContains "that it is normal" "normal here"

      testCase "WHY — once the machine has taught SageFs how long a start takes, the notice says so in seconds" <| fun _ ->
        let text = StartEscalation.describeNotice (StartEscalation.notice MachineTier.Constrained learned)
        text |> Expect.stringContains "the learned duration" (sprintf "%.0f s" learnedStart.TotalSeconds)
    ]
  ]
