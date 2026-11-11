/// An actor that keeps itself alive by eating a handler's exception must still answer the caller that waits on the message.
/// `ResilientActor.wrapLoop` did not: the reply died with the handler, and an untimed `PostAndAsyncReply` waited forever, so
/// join_cohort, acquire_claim and request_landing hung with no response. These tests pin the contract per owner: a step that
/// throws answers the caller with a typed failure, and an owner that never answers hits a ceiling named in `Timeouts`.
module SageFs.Tests.ActorReplyContractTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger
open SageFs.Features.TrunkFollow

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"
let private noOutcomes (_: string) : CohortOwner.SessionTestOutcomes = ([], [], [], 0L)
let private joinAlice = CohortCommand.Join(alice, JoinableRole.Implementer, None, machine)

/// A message that carries a reply, the shape every owner's reply-carrying command has.
type private Ask = { Fault: bool; Reply: AsyncReplyChannel<Result<int, string>> }

let private answering (state: int) (ask: Ask) : Async<int> =
  async {
    match ask.Fault with
    | true -> return failwith "step blew up"
    | false ->
      ask.Reply.Reply(Ok state)
      return state
  }


let private startCohort (ledger: LedgerPort<MemberId>) (outcomes: string -> CohortOwner.SessionTestOutcomes) (ceiling: TimeSpan) =
  CohortOwner.startCoreWithCeiling ceiling silentLogger machine ledger (fun () -> epoch) (fun () -> [| 1uy |]) outcomes CohortOwner.LandingPerformer.stub

let private ownerFaultMessage (error: CohortError<MemberId>) : string =
  match error with
  | CohortError.OwnerStepFailed reason -> reason
  | other -> failtestf "expected OwnerStepFailed, got %A" other

[<Tests>]
let actorReplyContractTests =
  testList "actor reply contract: a failing step answers its caller, a silent owner hits a named ceiling" [

    testList "ResilientActor" [
      let loopWith (step: int -> Ask -> Async<int>) =
        MailboxProcessor<Ask>.Start(fun inbox ->
          let rec loop state = async {
            let! ask = inbox.Receive()
            let! next = step state ask
            return! loop next
          }
          loop 0)
      let ask (fault: bool) (ceiling: TimeSpan) (mailbox: MailboxProcessor<Ask>) =
        Started.asTask (ResilientActor.awaitReply mailbox ceiling (fun ch -> { Fault = fault; Reply = ch }))

      testTask "wrapLoopAnswering lets a faulting handler answer the caller that waits on the message" {
        let onFault (ask: Ask) (ex: exn) = ask.Reply.Reply(Error ex.Message)
        let mailbox = loopWith (ResilientActor.wrapLoopAnswering silentLogger "contract-test" onFault answering)
        let! answer = ask true TestTimeouts.patienceInProcess mailbox
        answer |> Expect.equal "the caller got the fault as its answer" (Some(Error "step blew up"))
      }

      testTask "wrapLoop, which has no fault hook, leaves the caller unanswered: the ceiling is what ends the wait" {
        let mailbox = loopWith (ResilientActor.wrapLoop silentLogger "contract-test" answering)
        let! answer = ask true TestTimeouts.ownerReplyWithheld mailbox
        answer |> Expect.isNone "no answer came inside the ceiling"
      }

      testTask "a fault hook that throws does not stop the loop" {
        let onFault (_: Ask) (_: exn) : unit = failwith "the hook faults too"
        let mailbox = loopWith (ResilientActor.wrapLoopAnswering silentLogger "contract-test" onFault answering)
        let! _ = ask true TestTimeouts.ownerReplyWithheld mailbox
        let! second = ask false TestTimeouts.patienceInProcess mailbox
        second |> Expect.equal "the loop survived the hook and answers the next message" (Some(Ok 0))
      }

      test "replyOnce runs the reply a single time" {
        let seen = ResizeArray<int>()
        let once = ResilientActor.replyOnce seen.Add
        once 1
        once 2
        seen |> List.ofSeq |> Expect.equal "only the first answer is delivered" [ 1 ]
      }
    ]

    testList "CohortOwner" [
      testTask "a ledger that throws on append answers Commit with OwnerStepFailed, and the owner serves the next command" {
        let inner = InMemory.create<MemberId> ()
        let appends = ref 0
        let ledger =
          { inner with
              Append =
                fun entry ->
                  match Interlocked.Increment appends with
                  | 1 -> failwith "disk full"
                  | _ -> inner.Append entry }
        use owner = startCohort ledger noOutcomes Timeouts.ownerReply
        let! first = owner.Commit(joinAlice).WaitAsync TestTimeouts.patienceInProcess
        match first with
        | Error error -> ownerFaultMessage error |> Expect.stringContains "the reason names the failed step" "disk full"
        | Ok _ -> failtest "the append threw, so the join cannot have been accepted"
        inner.ReadAll machine |> Expect.isEmpty "nothing was recorded for the command that failed"
        let! second = owner.Commit(joinAlice).WaitAsync TestTimeouts.patienceInProcess
        second |> Result.isOk |> Expect.isTrue "the owner is alive and applies the retry"
        (inner.ReadAll machine |> List.map (fun e -> e.Seq))
        |> Expect.equal "the retry takes sequence 0, because the failed command never advanced it" [ 0L<Measures.ledgerSeq> ]
      }

      testTask "a frame projection that throws answers Commit and leaves the ledger without the entry" {
        let ledger = InMemory.create<MemberId> ()
        let outcomes (_: string) : CohortOwner.SessionTestOutcomes = failwith "outcome lookup failed"
        use owner = startCohort ledger outcomes Timeouts.ownerReply
        let join = CohortCommand.Join(alice, JoinableRole.Implementer, Some "session-1", machine)
        let! result = owner.Commit(join).WaitAsync TestTimeouts.patienceInProcess
        match result with
        | Error error -> ownerFaultMessage error |> Expect.stringContains "the reason names the failed step" "outcome lookup failed"
        | Ok _ -> failtest "the projection threw, so the join cannot have been accepted"
        ledger.ReadAll machine
        |> Expect.isEmpty "the frame is projected before the append, so a throw there records nothing (no duplicate Seq on the next command)"
      }

      testTask "an event subscriber that throws does not cost the caller its answer or the entry its place" {
        let ledger = InMemory.create<MemberId> ()
        use owner = startCohort ledger noOutcomes Timeouts.ownerReply
        owner.Events.Add(fun _ -> failwith "subscriber failed")
        let! result = owner.Commit(joinAlice).WaitAsync TestTimeouts.patienceInProcess
        result |> Result.isOk |> Expect.isTrue "the join is answered Ok"
        ledger.ReadAll machine |> List.length |> Expect.equal "the entry is on the ledger once" 1
        let! next = owner.Commit(CohortCommand.Join(bob, JoinableRole.Verifier, None, machine)).WaitAsync TestTimeouts.patienceInProcess
        next |> Result.isOk |> Expect.isTrue "the next command is applied"
        (ledger.ReadAll machine |> List.map (fun e -> e.Seq))
        |> Expect.equal "sequence numbers stay dense" [ 0L<Measures.ledgerSeq>; 1L<Measures.ledgerSeq> ]
      }

      testTask "an owner stuck in a step that never returns answers OwnerUnresponsive at the ceiling" {
        let inner = InMemory.create<MemberId> ()
        let release = new ManualResetEventSlim(false)
        // The owner's append is synchronous, so a step that never returns holds its thread by nature: this is that step.
        let ledger : LedgerPort<MemberId> = { inner with Append = fun _ -> release.WaitHandle.WaitOne() |> ignore }
        use owner = startCohort ledger noOutcomes TestTimeouts.ownerReplyWithheld
        try
          let! result = owner.Commit(joinAlice).WaitAsync TestTimeouts.patienceInProcess
          result |> Expect.equal "the caller is told the owner did not answer, with the ceiling it waited" (Error(CohortError.OwnerUnresponsive TestTimeouts.ownerReplyWithheld))
        finally
          release.Set()
      }
    ]

    testList "ManifestOwner" [
      let faulting (_: ManifestOwner.OwnerState) (_: ManifestOwner.Command) : Async<ManifestOwner.OwnerState> =
        async { return failwith "manifest step blew up" }
      let withheld (owner: ManifestOwner.OwnerState) (_: ManifestOwner.Command) : Async<ManifestOwner.OwnerState> =
        async {
          do! Async.AwaitTask(TaskCompletionSource<unit>().Task)
          return owner
        }

      testTask "a throwing step answers Commit, Read, QuarantineCorrupt and Flush" {
        use owner = ManifestOwner.startWithHandler silentLogger Timeouts.ownerReply faulting
        let! committed =
          owner.Commit(DaemonManifest.ManifestMutation.Remove "ghost").WaitAsync TestTimeouts.patienceInProcess
        match committed with
        | Error(ManifestOwner.CommitError.WriteFailed reason) ->
          reason |> Expect.stringContains "the reason names the failed step" "manifest step blew up"
        | other -> failtestf "expected WriteFailed, got %A" other
        let! read = owner.Read().WaitAsync TestTimeouts.patienceInProcess
        match read with
        | Error(ManifestTypes.ManifestLoadError.IoError reason) ->
          reason |> Expect.stringContains "the read names the failed step" "manifest step blew up"
        | other -> failtestf "expected IoError, got %A" other
        let! quarantined = owner.QuarantineCorrupt().WaitAsync TestTimeouts.patienceInProcess
        quarantined |> Expect.isFalse "nothing was renamed"
        do! owner.Flush().WaitAsync TestTimeouts.patienceInProcess
      }

      testTask "an owner that never answers hits the ceiling: Commit and Read fail, Flush raises a TimeoutException" {
        use owner = ManifestOwner.startWithHandler silentLogger TestTimeouts.ownerReplyWithheld withheld
        let! committed =
          owner.Commit(DaemonManifest.ManifestMutation.Remove "ghost").WaitAsync TestTimeouts.patienceInProcess
        match committed with
        | Error(ManifestOwner.CommitError.WriteFailed reason) -> reason |> Expect.stringContains "the reason says the owner did not answer" "did not answer"
        | other -> failtestf "expected WriteFailed, got %A" other
        let! flushed =
          owner.Flush().WaitAsync TestTimeouts.patienceInProcess
          |> Async.AwaitTask
          |> Async.Catch
          |> Started.asTask
        match flushed with
        | Choice2Of2 (ex: exn) -> (ex.GetBaseException() :? TimeoutException) |> Expect.isTrue "a flush that gets no answer raises a TimeoutException"
        | Choice1Of2 () -> failtest "a flush the owner never answered cannot complete"
      }
    ]

    testList "TrunkFollowOwner" [
      let event = TrunkEvent.Moved(LandingId "l-1", TrunkMove.NotMoved "x")

      testTask "a throwing step still lets Flush complete, and the loop keeps serving" {
        let faulting (_: TrunkMachine -> unit) (_: TrunkFollowOwner.Command -> unit) (_: TrunkMachine) (_: TrunkFollowOwner.Command) : Async<TrunkMachine> =
          async { return failwith "trunk step blew up" }
        use owner = TrunkFollowOwner.startWithHandler silentLogger Timeouts.ownerReply faulting
        owner.Post event
        do! owner.Flush().WaitAsync TestTimeouts.patienceInProcess
        do! owner.Flush().WaitAsync TestTimeouts.patienceInProcess
      }

      testTask "an owner that never answers a flush raises a TimeoutException at the ceiling" {
        let withheld (_: TrunkMachine -> unit) (_: TrunkFollowOwner.Command -> unit) (machine: TrunkMachine) (_: TrunkFollowOwner.Command) : Async<TrunkMachine> =
          async {
            do! Async.AwaitTask(TaskCompletionSource<unit>().Task)
            return machine
          }
        use owner = TrunkFollowOwner.startWithHandler silentLogger TestTimeouts.ownerReplyWithheld withheld
        let! flushed =
          owner.Flush().WaitAsync TestTimeouts.patienceInProcess
          |> Async.AwaitTask
          |> Async.Catch
          |> Started.asTask
        match flushed with
        | Choice2Of2 (ex: exn) -> (ex.GetBaseException() :? TimeoutException) |> Expect.isTrue "a flush that gets no answer raises a TimeoutException"
        | Choice1Of2 () -> failtest "a flush the owner never answered cannot complete"
      }
    ]

    testList "LandingCacheOwner" [
      testTask "a step that throws outside the handler's own try answers Verify with a typed Error" {
        let faulting (_: LiveTesting.TestResultCache) (_: CohortOwner.LandingCacheOwner.Command) : Async<LiveTesting.TestResultCache> =
          async { return failwith "cache step blew up" }
        use owner = CohortOwner.LandingCacheOwner.startWithHandler silentLogger faulting
        let! result =
          owner.Verify (fun _ -> None) "session-1" [] (fun _ -> async { return Ok [] })
          |> fun task -> task.WaitAsync TestTimeouts.patienceInProcess
        match result with
        | Error reason -> reason |> Expect.stringContains "the reason names the failed step" "cache step blew up"
        | Ok _ -> failtest "the step threw, so there is no verdict"
      }
    ]

    testList "the other renderers and mappings" [
      test "sagefs play counts present members, so a departed member is listed but not counted" {
        let clock = epoch
        let apply (state: CohortState<MemberId>) (command: CohortCommand<MemberId>) =
          match decide clock [||] state command with
          | Ok(next, _, _) -> next
          | Error e -> failtestf "refused: %A" e
        let state =
          CohortState.empty ()
          |> fun s -> apply s (CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
          |> fun s -> apply s (CohortCommand.Join(bob, JoinableRole.Verifier, None, machine))
          |> fun s -> apply s (CohortCommand.Depart(bob, machine))
        let head : LedgerHead<MemberId> = { Seq = 2L<Measures.ledgerSeq>; State = state }
        let frame = project head [||]
        frame.MemberIds.Length |> Expect.equal "the frame still holds both ids (a departed member keeps its row)" 2
        let summary = CohortPlay.renderPlaySummary 3 head frame
        summary |> Expect.stringContains "one member is present" "Members (1):"
        summary |> Expect.stringContains "the departed member is still listed, marked departed" "bob  role=Verifier  departed since"
      }

      test "the vacant-conductor refusal is a sentence: no union syntax reaches agent text" {
        let since = epoch
        let texts =
          [ CohortError.ConductorVacant(Some alice, since, VacancyReason.LeaseLapsed)
            CohortError.ConductorVacant(Some alice, since, VacancyReason.ConductorLeft)
            CohortError.ConductorVacant(None, since, VacancyReason.NeverBound) ]
          |> List.map CohortErrorMapping.toSageFsError
          |> List.map (fun error ->
            match error with
            | SageFsError.CohortRefused(CohortFailureKind.ConductorVacant, reason, suggestion) -> reason, suggestion
            | other -> failtestf "expected CohortRefused(ConductorVacant), got %A" other)
        for reason, suggestion in texts do
          let text = reason + " " + suggestion
          for shape in [ "Some"; "None"; "["; "{"; "\"alice\""; "LeaseLapsed"; "ConductorLeft"; "NeverBound" ] do
            text.Contains shape |> Expect.isFalse (sprintf "agent text has no %s shape: %s" shape text)
        let reason, _ = texts.Head
        reason |> Expect.stringContains "the sentence names who held the seat" "alice held it until"
        reason |> Expect.stringContains "the sentence says why it is empty" "the conductor's lease lapsed"
      }

      test "the owner-fault refusals say what happened and the next action" {
        for error, expectedKind in
          [ CohortError.OwnerStepFailed "disk full", CohortFailureKind.OwnerStepFailed
            CohortError.OwnerUnresponsive Timeouts.ownerReply, CohortFailureKind.OwnerUnresponsive ] do
          match CohortErrorMapping.toSageFsError error with
          | SageFsError.CohortRefused(kind, reason, suggestion) ->
            kind |> Expect.equal "an agent can tell a failed step from a silent owner" expectedKind
            reason |> Expect.isNotEmpty "there is a reason"
            suggestion |> Expect.stringContains "the next action reads the cohort first" "get_cohort_status"
          | other -> failtestf "expected CohortRefused, got %A" other
      }
    ]

    testList "every actor loop that carries a reply uses the answering wrapper" [
      // `ResilientActor.wrapLoop` eats the reply a caller waits on. These are the call sites that still use it, by file:
      // actors whose messages carry a reply (AppState's eval and query actors, behind the worker's /eval) belong to the
      // answering wrapper too, and each conversion lowers a budget here. A file not in the table has a budget of zero.
      let budgets = [ "SageFs.Core/AppState.fs", 3; "SageFs/DaemonMode.fs", 1 ]
      let uses =
        let root = RepoPaths.repoPathFull [||]
        let pattern = Regex(@"ResilientActor\.wrapLoop logger", RegexOptions.Compiled)
        [ for project in [ "SageFs.Core"; "SageFs"; "SageFs.Host" ] do
            let dir = Path.Combine(root, project)
            for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
              let relative = Path.GetRelativePath(root, file).Replace('\\', '/')
              match relative.Contains "/obj/" || relative.Contains "/bin/" with
              | true -> ()
              | false ->
                let count =
                  File.ReadAllLines file
                  |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//") && pattern.IsMatch line)
                  |> Array.length
                match count with
                | 0 -> ()
                | n -> yield relative, n ]
      test "the call sites of the non-answering wrapper only go down" {
        uses |> Expect.equal "wrapLoop call sites by file" budgets
      }
    ]
  ]
