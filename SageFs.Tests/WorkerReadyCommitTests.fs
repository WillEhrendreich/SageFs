module SageFs.Tests.WorkerReadyCommitTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.WorkerProtocol
open SageFs.WorkerReadyCommit

/// A stand-in session: the plan is generic over the session type, so it is
/// proven here without a process, a mailbox or a proxy.
type private FakeSession = { Pid: int option; Url: string; Replaced: WorkerContinuity }

let private fakeOps : SessionOps<FakeSession> =
  { WorkerPid = fun s -> s.Pid
    Install =
      fun transport s ->
        { s with Pid = Some transport.WorkerPid; Url = transport.BaseUrl; Replaced = transport.Continuity } }

let private liveProxy : SessionProxy =
  fun _ -> async { return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unused") }

let private goodUrl = "http://localhost:4123"

let private serving pid = { Pid = Some pid; Url = "http://old"; Replaced = WorkerContinuity.SameWorker }

[<Tests>]
let workerReadyCommitTests =
  testList "WorkerReadyCommit.plan" [
    testCase "a ready from the retiring old worker is stale, whatever its transport says" <| fun _ ->
      let old = serving 10
      let current = { old with Pid = Some 10 }
      match plan fakeOps current (ParkedSwap.Parked old) "" liveProxy 10 with
      | Plan.IgnoreStale -> ()
      | other -> failtestf "expected IgnoreStale, got %A" (other.GetType().Name)

    testCase "the pid guard decides before the transport check, so a stale ready with a bad transport is still ignored" <| fun _ ->
      let old = serving 10
      match plan fakeOps old (ParkedSwap.Parked old) "" liveProxy 10 with
      | Plan.IgnoreStale -> ()
      | _ -> failtest "a stale pid must never reach the transport check"

    testCase "a ready from the current worker with no swap installs its transport and retires nothing" <| fun _ ->
      let current = { Pid = Some 20; Url = ""; Replaced = WorkerContinuity.SameWorker }
      match plan fakeOps current ParkedSwap.NoSwap goodUrl liveProxy 20 with
      | Plan.Commit committed ->
        committed.Updated.Url |> Expect.equal "the new base URL is installed" goodUrl
        committed.Updated.Replaced |> Expect.equal "no swap means the same worker continues" WorkerContinuity.SameWorker
        match committed.RetireOld with
        | RetireOld.NothingParked -> ()
        | RetireOld.Retire _ -> failtest "nothing was parked, so nothing may be retired"
      | _ -> failtest "expected Commit"

    testCase "a ready from the replacement commits the swap and names the parked worker for retirement" <| fun _ ->
      let old = serving 10
      let restarting = { old with Pid = Some 10 }
      match plan fakeOps restarting (ParkedSwap.Parked old) goodUrl liveProxy 20 with
      | Plan.Commit committed ->
        committed.Updated.Pid |> Expect.equal "the registry points at the replacement" (Some 20)
        committed.Updated.Replaced |> Expect.equal "a committed swap replaces the worker" WorkerContinuity.ReplacesWorker
        match committed.RetireOld with
        | RetireOld.Retire retired -> retired |> Expect.equal "the parked old worker is the one retired" old
        | RetireOld.NothingParked -> failtest "the parked old worker must be retired"
      | _ -> failtest "expected Commit"

    testCase "an unusable transport during a swap restores the old worker and never retires it" <| fun _ ->
      let old = serving 10
      match plan fakeOps old (ParkedSwap.Parked old) "" liveProxy 20 with
      | Plan.RejectTransport(message, Rejected.RestoreOld restored) ->
        restored |> Expect.equal "the parked old worker is what comes back" old
        System.String.IsNullOrWhiteSpace message |> Expect.isFalse "the rejection says what was wrong"
      | _ -> failtest "expected RejectTransport with RestoreOld"

    testCase "an unusable transport with no swap has nothing to restore" <| fun _ ->
      let current = { Pid = Some 20; Url = ""; Replaced = WorkerContinuity.SameWorker }
      match plan fakeOps current ParkedSwap.NoSwap "  " liveProxy 20 with
      | Plan.RejectTransport(_, Rejected.NoSwapParked) -> ()
      | _ -> failtest "expected RejectTransport with NoSwapParked"

    testCase "a missing proxy is an unusable transport even with a good URL" <| fun _ ->
      let current = { Pid = Some 20; Url = ""; Replaced = WorkerContinuity.SameWorker }
      match plan fakeOps current ParkedSwap.NoSwap goodUrl Unchecked.defaultof<SessionProxy> 20 with
      | Plan.RejectTransport _ -> ()
      | _ -> failtest "expected RejectTransport"

    testCase "the port comes from the base URL when it carries one" <| fun _ ->
      let current = { Pid = Some 20; Url = ""; Replaced = WorkerContinuity.SameWorker }
      let seen = ref None
      let ops = { fakeOps with Install = fun transport s -> seen.Value <- Some transport.Port; fakeOps.Install transport s }
      plan ops current ParkedSwap.NoSwap goodUrl liveProxy 20 |> ignore
      seen.Value |> Expect.equal "port 4123 is read off the URL" (Some (Some 4123))

    testProperty "plan commits exactly when the pid guard commits and the transport is valid" <| fun (currentPid: PositiveInt) (parkedPid: PositiveInt) (eventPid: PositiveInt) (swap: bool) (validTransport: bool) ->
      let current = { Pid = Some currentPid.Get; Url = ""; Replaced = WorkerContinuity.SameWorker }
      let parked = if swap then ParkedSwap.Parked (serving parkedPid.Get) else ParkedSwap.NoSwap
      let url = if validTransport then goodUrl else ""
      let pendingPid = if swap then Some parkedPid.Get else None
      let guardCommits =
        match WorkerEventGuard.classifyReady (Some currentPid.Get) pendingPid eventPid.Get with
        | WorkerEventGuard.ReadyDecision.Commit -> true
        | WorkerEventGuard.ReadyDecision.IgnoreStale -> false
      let committed =
        match plan fakeOps current parked url liveProxy eventPid.Get with
        | Plan.Commit _ -> true
        | Plan.IgnoreStale | Plan.RejectTransport _ -> false
      committed = (guardCommits && validTransport)
  ]
