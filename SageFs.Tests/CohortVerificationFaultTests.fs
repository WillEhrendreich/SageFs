/// A landing whose build fails, told as the one of two kinds it is, and the rebuild's order (P16, roast priority 7 and
/// improvements 6 and 7).
///
/// Every `ComputeAffected` error used to become "inconclusive" and the requester was told "Fix it in your commit" for an
/// offline restore (NU1301) or a locked file. A compiler error in the landing's own source is the member's to fix. Anything
/// else says nothing about the commit. The rebuild also trusted that the worktree was at the rebased commit, and recorded the
/// build only when it succeeded, so a refused build's half-written DLLs were invisible to the next landing.
module SageFs.Tests.CohortVerificationFaultTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger

let private diagnostic (file: string option) (code: string option) (message: string) : BuildDiagnostic =
  { File = file
    Line = file |> Option.map (fun _ -> 3)
    Column = file |> Option.map (fun _ -> 5)
    Severity = BuildDiagnosticSeverity.Blocking
    Code = code
    Message = message }

let private compilerError =
  SageFsError.BuildFailed(1, [ diagnostic (Some "/w/Lab.Core/Greeting.fs") (Some "FS0001") "This expression was expected to have type 'string'" ])

let private offlineRestore =
  SageFsError.BuildFailed(1, [ diagnostic (Some "/w/Lab.Core/Lab.Core.fsproj") (Some "NU1301") "Unable to load the service index for source https://api.nuget.org/v3/index.json." ])

let private lockedFile =
  SageFsError.BuildFailed(1, [ diagnostic (Some "/w/Lab.Core/Lab.Core.fsproj") (Some "MSB3027") "Could not copy \"obj/Lab.Core.dll\" to \"bin/Lab.Core.dll\". The file is locked by: \"dotnet\"" ])

[<Tests>]
let classifyTests =
  testList "CohortVerificationFault (P16): a refused build is the member's or it is not" [

    test "a compiler error in the landing is the member's, and it names the code and the file" {
      match CohortLandingPlan.classifyBuildFailure compilerError with
      | CohortOwner.VerificationFault.CompileErrorInLanding text ->
        text |> Expect.stringContains "the code" "FS0001"
        text |> Expect.stringContains "the file" "Greeting.fs"
        text |> Expect.stringContains "tells the member what to do" "Fix it in your commit"
      | other -> failtestf "expected CompileErrorInLanding, got %A" other
    }

    test "a C# compiler error is the member's too" {
      let error = SageFsError.BuildFailed(1, [ diagnostic (Some "/w/Lab.Cs/Greeting.cs") (Some "CS0103") "The name 'x' does not exist" ])
      match CohortLandingPlan.classifyBuildFailure error with
      | CohortOwner.VerificationFault.CompileErrorInLanding _ -> ()
      | other -> failtestf "expected CompileErrorInLanding, got %A" other
    }

    test "an offline restore (NU1301) is the environment, and does not tell the member to fix their commit" {
      match CohortLandingPlan.classifyBuildFailure offlineRestore with
      | CohortOwner.VerificationFault.Environment reason ->
        reason |> Expect.stringContains "quotes the first error" "NU1301"
        reason.Contains "Fix it in your commit" |> Expect.isFalse "it is not the commit's fault"
        reason |> Expect.stringContains "says to request again" "request the landing again"
      | other -> failtestf "expected Environment, got %A" other
    }

    test "a locked file (MSB3027) is the environment" {
      match CohortLandingPlan.classifyBuildFailure lockedFile with
      | CohortOwner.VerificationFault.Environment _ -> ()
      | other -> failtestf "expected Environment, got %A" other
    }

    test "a failed restore beside a compiler error is the environment: the compile ran against a restore that failed" {
      let error =
        SageFsError.BuildFailed(1, [
          diagnostic (Some "/w/Lab.Core/Greeting.fs") (Some "FS0001") "type mismatch"
          diagnostic (Some "/w/Lab.Core/Lab.Core.fsproj") (Some "NU1301") "Unable to load the service index" ])
      match CohortLandingPlan.classifyBuildFailure error with
      | CohortOwner.VerificationFault.Environment _ -> ()
      | other -> failtestf "expected Environment, got %A" other
    }

    test "a failure that is not a build failure is the environment" {
      match CohortLandingPlan.classifyBuildFailure (SageFsError.SessionNotFound "s1") with
      | CohortOwner.VerificationFault.Environment _ -> ()
      | other -> failtestf "expected Environment, got %A" other
    }

    test "a bare build line with no file or code is the environment" {
      match CohortLandingPlan.classifyBuildFailure (SageFsError.BuildFailed(1, [ diagnostic None None "Build FAILED." ])) with
      | CohortOwner.VerificationFault.Environment _ -> ()
      | other -> failtestf "expected Environment, got %A" other
    }
  ]

// ── The cohort records each kind as what it is ────────────────────────────────────────────────────────────────

let private silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine
let private alice = MemberId.Minted "alice"

let private counterEntropy () : unit -> byte[] =
  let mutable n = 0
  fun () ->
    let bytes = BitConverter.GetBytes n
    n <- n + 1
    bytes

let private performerFailing (fault: CohortOwner.VerificationFault) : CohortOwner.LandingPerformer<MemberId> =
  { Rebase = fun _ onto _ -> async { return Ok(onto + "-rebased") }
    ComputeAffected = fun _ _ _ -> async { return Error fault }
    RunTests = fun _ _ -> async { return Ok [] }
    FastForward = fun _ toSha -> async { return Ok toSha }
    Notify = fun _ _ -> () }

let private blockedStateOf (fault: CohortOwner.VerificationFault) : Task<LandingState<MemberId>> =
  task {
    let ledger = InMemory.create<MemberId> ()
    use owner =
      CohortOwner.startWithPerformer silentLogger machine ledger (fun () -> epoch) (counterEntropy ()) (fun _ -> ([], [], [], 0L)) (performerFailing fault)
    let! _ = owner.Commit(CohortCommand.Join(alice, JoinableRole.Implementer, None, machine))
    let! requested = owner.Commit(CohortCommand.RequestLanding(alice, [], [ "c1" ], "land it", machine))
    let id =
      match requested with
      | Ok(events, _) ->
        events |> List.pick (function CohortEvent.LandingQueued(id, _) -> Some id | _ -> None)
      | Error err -> failtestf "refused: %A" err
    let deadline = DateTime.UtcNow.Add TestTimeouts.patience
    let rec wait () =
      task {
        do! owner.Flush()
        match (owner.ReadCohortState().Landings |> Map.find id).State with
        | LandingState.Blocked _ as blocked -> return blocked
        | _ when DateTime.UtcNow > deadline -> return failtest "the landing never blocked"
        | _ ->
          do! Task.Delay(TestTimeouts.asMs TestTimeouts.inProcessPoll)
          return! wait ()
      }
    let! state = wait ()
    Expect.isTrue "the queue is not jammed" (List.isEmpty (owner.ReadCohortState().Queue))
    return state
  }

[<Tests>]
let ownerTests =
  testList "CohortVerificationFault (P16): the cohort records each kind as what it is" [

    testTask "WHY - a compile error in the landing blocks it as the member's to fix, with the next action to fix the build" {
      let! state = blockedStateOf (CohortOwner.VerificationFault.CompileErrorInLanding "Greeting.fs(3,5): error FS0001: type mismatch")
      match state with
      | LandingState.Blocked(LandingBlocker.CompileErrorInLanding text, NextAction.FixTheBuild) -> text |> Expect.stringContains "carries the first error" "FS0001"
      | other -> failtestf "expected Blocked(CompileErrorInLanding, FixTheBuild), got %A" other
    }

    testTask "WHY - an environment fault blocks it as inconclusive, which says to request the same landing again" {
      let! state = blockedStateOf (CohortOwner.VerificationFault.Environment "NU1301: unable to load the service index")
      match state with
      | LandingState.Blocked(LandingBlocker.Inconclusive reason, NextAction.RebaseAndResubmit) -> reason |> Expect.stringContains "carries the reason" "NU1301"
      | other -> failtestf "expected Blocked(Inconclusive, RebaseAndResubmit), got %A" other
    }
  ]

[<Tests>]
let textTests =
  testList "CohortVerificationFault (P16): the status says which kind it is" [
    test "a compile error says the landing does not compile, quotes the error and says to fix the commit" {
      let said =
        CohortStatusText.landingStateText (LandingState.Blocked(LandingBlocker.CompileErrorInLanding "Greeting.fs(3,5): error FS0001: type mismatch", NextAction.FixTheBuild))
      said |> Expect.stringContains "kind" "does not compile"
      said |> Expect.stringContains "the error" "FS0001"
      said |> Expect.stringContains "the action" "fix it in your commit"
    }

    test "an inconclusive verification says it is inconclusive, quotes the reason and does not blame the commit" {
      let said = CohortStatusText.landingStateText (LandingState.Blocked(LandingBlocker.Inconclusive "NU1301: unable to load the service index", NextAction.RebaseAndResubmit))
      said |> Expect.stringContains "kind" "inconclusive"
      said |> Expect.stringContains "the reason" "NU1301"
      said.Contains "fix it in your commit" |> Expect.isFalse "no blame"
      said |> Expect.stringContains "the action" "request the landing again"
    }
  ]

// ── The rebuild's order ───────────────────────────────────────────────────────────────────────────────────────

let private gatePorts
  (calls: List<string>)
  (head: Result<string, string>)
  (restart: Result<unit, SageFsError>)
  (settle: Result<unit, string>)
  : CohortRebuildGate.Ports =
  { WorktreeHead = fun () -> async { calls.Add "head"; return head }
    NoteAttempt = fun sha -> calls.Add(sprintf "note %s" sha)
    Restart = fun () -> async { calls.Add "restart"; return restart }
    AwaitSettled = fun () -> async { calls.Add "settle"; return settle } }

[<Tests>]
let gateTests =
  testList "CohortRebuildGate (P16, roast improvements 6 and 7): the rebuild's order and what it trusts" [

    testAsync "a worktree that is not at the rebased commit is refused before anything is built or recorded" {
      let calls = List<string>()
      let! result = CohortRebuildGate.restartAt (gatePorts calls (Ok "bbbb") (Ok ()) (Ok ())) "aaaa"
      match result with
      | Error(CohortOwner.VerificationFault.Environment reason) ->
        reason |> Expect.stringContains "names the rebased commit" "aaaa"
        reason |> Expect.stringContains "names where the worktree is" "bbbb"
      | other -> failtestf "expected Environment, got %A" other
      Seq.toList calls |> Expect.equal "only the head was read" [ "head" ]
    }

    testAsync "a worktree whose head cannot be read is refused: fail closed" {
      let calls = List<string>()
      let! result = CohortRebuildGate.restartAt (gatePorts calls (Error "not a git repository") (Ok ()) (Ok ())) "aaaa"
      match result with
      | Error(CohortOwner.VerificationFault.Environment reason) -> reason |> Expect.stringContains "says why" "not a git repository"
      | other -> failtestf "expected Environment, got %A" other
      Seq.toList calls |> Expect.equal "nothing was built" [ "head" ]
    }

    testAsync "the attempt is recorded BEFORE the build, so a build that fails still counts against the session" {
      let calls = List<string>()
      let! result = CohortRebuildGate.restartAt (gatePorts calls (Ok "aaaa") (Error compilerError) (Ok ())) "aaaa"
      match result with
      | Error(CohortOwner.VerificationFault.CompileErrorInLanding _) -> ()
      | other -> failtestf "expected CompileErrorInLanding, got %A" other
      Seq.toList calls |> Expect.equal "head, note, restart" [ "head"; "note aaaa"; "restart" ]
    }

    testAsync "an offline restore is recorded as attempted and is the environment" {
      let calls = List<string>()
      let! result = CohortRebuildGate.restartAt (gatePorts calls (Ok "aaaa") (Error offlineRestore) (Ok ())) "aaaa"
      match result with
      | Error(CohortOwner.VerificationFault.Environment _) -> ()
      | other -> failtestf "expected Environment, got %A" other
      Seq.toList calls |> Expect.contains "the attempt was noted" "note aaaa"
    }

    testAsync "a success reads the head, notes, restarts and waits for the session, in that order" {
      let calls = List<string>()
      let! result = CohortRebuildGate.restartAt (gatePorts calls (Ok "aaaa") (Ok ()) (Ok ())) "aaaa"
      result |> Expect.equal "built" (Ok ())
      Seq.toList calls |> Expect.equal "the order" [ "head"; "note aaaa"; "restart"; "settle" ]
    }

    testAsync "a session that does not settle is the environment, and the attempt stays recorded" {
      let calls = List<string>()
      let! result = CohortRebuildGate.restartAt (gatePorts calls (Ok "aaaa") (Ok ()) (Error "still warming up")) "aaaa"
      match result with
      | Error(CohortOwner.VerificationFault.Environment reason) -> reason |> Expect.stringContains "says why" "warming up"
      | other -> failtestf "expected Environment, got %A" other
      Seq.toList calls |> Expect.contains "noted" "note aaaa"
    }

    testAsync "whatever the build and the settle answer, a rebuild notes exactly one attempt and notes it before it restarts" {
      for restartFails in [ true; false ] do
        for settleFails in [ true; false ] do
          let calls = List<string>()
          let restart = match restartFails with | true -> Error offlineRestore | false -> Ok ()
          let settle = match settleFails with | true -> Error "x" | false -> Ok ()
          let! _ = CohortRebuildGate.restartAt (gatePorts calls (Ok "aaaa") restart settle) "aaaa"
          let log = Seq.toList calls
          let notes = log |> List.filter (fun c -> c.StartsWith "note")
          let noteAt = log |> List.findIndex (fun c -> c.StartsWith "note")
          let restartAt = log |> List.findIndex (fun c -> c = "restart")
          notes |> List.length |> Expect.equal (sprintf "one note (restartFails=%b settleFails=%b)" restartFails settleFails) 1
          noteAt |> Expect.isLessThan "the note comes before the restart" restartAt
    }
  ]
