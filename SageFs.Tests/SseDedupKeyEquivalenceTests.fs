module SageFs.Tests.SseDedupKeyEquivalenceTests

/// WHY — the SSE dedup key runs once per model change, and every app output
/// line changes the model. The first version formatted each session status and
/// run phase with F#'s `string` on a union (reflection): about 130 microseconds
/// and 54 KB per session per call, so 100 sessions cost 13 ms and 5.5 MB per
/// output line. The replacement writes plain appends. A dedup key has exactly
/// one contract, and it must not move: two models get the same key exactly
/// when the pushed view is the same. The old key is kept here as the oracle.
open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting

/// The previous implementation, verbatim, as the reference for the contract.
let private oldKey (model: SageFsModel) : string =
  let sb = System.Text.StringBuilder(128)
  sb.Append(model.RecentOutput.Version).Append('|') |> ignore
  let diagCount =
    model.Diagnostics |> Map.values |> Seq.sumBy List.length
  sb.Append(diagCount).Append('|') |> ignore
  sb.Append(model.Sessions.Sessions.Length).Append('|') |> ignore
  let activeSessionId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value |> Option.defaultValue ""
  sb.Append(activeSessionId).Append('|') |> ignore
  for s in model.Sessions.Sessions do
    sb.Append(s.Id).Append(':').Append(string s.Status).Append(';') |> ignore
  sb.Append('|') |> ignore
  let lt = model.LiveTesting.TestState
  let ts = lt.Cached.TestSummary
  sb.Append(ts.Total).Append(',')
    .Append(ts.Passed).Append(',')
    .Append(ts.Failed).Append(',')
    .Append(ts.Running).Append(',')
    .Append(ts.Stale).Append('|') |> ignore
  sb.Append(lt.Cached.StateVersion).Append('|') |> ignore
  let (RunGeneration gen) = lt.LastGeneration
  sb.Append(gen).Append('|') |> ignore
  for kvp in lt.RunPhases do
    sb.Append(kvp.Key).Append(':').Append(string kvp.Value).Append(';') |> ignore
  sb.Append('|') |> ignore
  match lt.Activation = LiveTestingActivation.Active with
  | true -> sb.Append('1') |> ignore
  | false -> sb.Append('0') |> ignore
  sb.ToString()

/// Small closed domains, so two independently generated specs collide often
/// and equal-key pairs are common rather than vanishingly rare.
type private Spec =
  { OutputLines: int
    DiagLists: (string * int) list
    Sessions: (int * int * int) list
    Active: int option
    Summary: int * int * int * int * int
    StateVersion: int
    Generation: int
    Phases: (string * int * int) list
    Active01: int }

let private ids = [| "aaaaaaaa"; "bbbbbbbb"; "cccccccc" |]
let private faultReasons = [| ""; "boom"; "out of memory"; "a b c" |]

let private statusOf (i: int) : SessionDisplayStatus =
  match i with
  | 0 -> SessionDisplayStatus.Running
  | 1 -> SessionDisplayStatus.Starting
  | 2 -> SessionDisplayStatus.Restarting
  | 3 -> SessionDisplayStatus.Lost
  | 4 -> SessionDisplayStatus.Stopped
  | 5 -> SessionDisplayStatus.Idle
  | n -> SessionDisplayStatus.Faulted faultReasons.[n % faultReasons.Length]

let private phaseOf (kind: int) (gen: int) : TestRunPhase =
  match kind with
  | 0 -> TestRunPhase.Idle
  | 1 -> TestRunPhase.Running (RunGeneration gen)
  | _ -> TestRunPhase.RunningButEdited (RunGeneration gen)

let private toModel (spec: Spec) : SageFsModel =
  let m = SageFsModel.initial ()
  let lines =
    [ for i in 1..spec.OutputLines ->
        { Kind = OutputKind.Info
          Text = sprintf "line %d" i
          Timestamp = DateTime.UnixEpoch
          SessionId = "" } ]
  let snapshots =
    spec.Sessions
    |> List.map (fun (idIx, statusIx, evals) ->
      { Id = (match SessionId.validate ids.[idIx % ids.Length] with
              | Ok sid -> sid
              | Result.Error e -> failwithf "bad fixture id: %A" e)
        Name = None
        Projects = []
        Status = statusOf statusIx
        LastActivity = DateTime.UnixEpoch
        EvalCount = evals
        UpSince = DateTime.UnixEpoch
        WorkingDirectory = "/w" })
  let active =
    match spec.Active with
    | Some ix ->
      (match SessionId.validate ids.[ix % ids.Length] with
       | Ok sid -> ActiveSession.Viewing sid
       | Result.Error e -> failwithf "bad fixture id: %A" e)
    | None -> ActiveSession.AwaitingSession
  let (total, passed, failed, running, stale) = spec.Summary
  let summary : TestSummary =
    { Total = total; Passed = passed; Failed = failed; Stale = stale
      Running = running; Disabled = 0; Enabled = true }
  let ts =
    { m.LiveTesting.TestState with
        Cached = { m.LiveTesting.TestState.Cached with
                    TestSummary = summary
                    StateVersion = int64 spec.StateVersion }
        LastGeneration = RunGeneration spec.Generation
        RunPhases = spec.Phases |> List.map (fun (k, kind, gen) -> k, phaseOf kind gen) |> Map.ofList
        Activation = (match spec.Active01 with 0 -> LiveTestingActivation.Inactive | _ -> LiveTestingActivation.Active) }
  { m with
      RecentOutput = SessionOutputStore.ofLines lines
      Diagnostics =
        spec.DiagLists
        |> List.map (fun (file, n) -> file, List.replicate n Unchecked.defaultof<SageFs.Features.Diagnostics.Diagnostic>)
        |> Map.ofList
      Sessions = { m.Sessions with Sessions = snapshots; ActiveSessionId = active }
      LiveTesting = { m.LiveTesting with TestState = ts } }

let private genSpec : Gen<Spec> =
  gen {
    let! outputLines = Gen.choose (0, 2)
    let! diagLists = Gen.listOfLength 2 (Gen.zip (Gen.elements [ "f1"; "f2" ]) (Gen.choose (0, 2)))
    let! diagCount = Gen.choose (0, 2)
    let! sessions =
      Gen.listOfLength 2 (Gen.zip3 (Gen.choose (0, 2)) (Gen.choose (0, 9)) (Gen.choose (0, 2)))
    let! sessionCount = Gen.choose (0, 2)
    let! active = Gen.oneof [ Gen.constant None; Gen.map Some (Gen.choose (0, 2)) ]
    let! summary = Gen.zip3 (Gen.choose (0, 2)) (Gen.choose (0, 2)) (Gen.choose (0, 2))
    let! (running, stale) = Gen.zip (Gen.choose (0, 1)) (Gen.choose (0, 1))
    let! stateVersion = Gen.choose (0, 2)
    let! generation = Gen.choose (0, 2)
    let! phases =
      Gen.listOfLength 2 (Gen.zip3 (Gen.elements [ "p1"; "p2" ]) (Gen.choose (0, 2)) (Gen.choose (0, 2)))
    let! phaseCount = Gen.choose (0, 2)
    let! active01 = Gen.choose (0, 1)
    let (total, passed, failed) = summary
    return
      { OutputLines = outputLines
        DiagLists = diagLists |> List.truncate diagCount
        Sessions = sessions |> List.truncate sessionCount
        Active = active
        Summary = (total, passed, failed, running, stale)
        StateVersion = stateVersion
        Generation = generation
        Phases = phases |> List.truncate phaseCount
        Active01 = active01 }
  }

/// A second spec that is the first, or the first with ONE component swapped
/// (some of those components are part of the key, some are not: an eval count
/// is shown nowhere in the key), so equal and unequal pairs both occur.
let private genPair : Gen<Spec * Spec> =
  gen {
    let! a = genSpec
    let! c = genSpec
    let! otherStatus = Gen.choose (0, 9)
    let! otherPhaseGen = Gen.choose (0, 2)
    let! b =
      Gen.elements
        [ a
          { a with OutputLines = c.OutputLines }
          { a with DiagLists = c.DiagLists }
          { a with Sessions = c.Sessions }
          { a with Sessions = a.Sessions |> List.map (fun (i, s, e) -> i, s, e + 1) }
          // One session's status alone, then one run phase's generation alone.
          { a with Sessions = a.Sessions |> List.mapi (fun n (i, s, e) -> (match n with 0 -> i, otherStatus, e | _ -> i, s, e)) }
          { a with Phases = a.Phases |> List.mapi (fun n (k, kind, g) -> (match n with 0 -> k, kind, otherPhaseGen | _ -> k, kind, g)) }
          { a with Active = c.Active }
          { a with Summary = c.Summary }
          { a with StateVersion = c.StateVersion }
          { a with Generation = c.Generation }
          { a with Phases = c.Phases }
          { a with Active01 = c.Active01 }
          c ]
    return a, b
  }

[<Tests>]
let tests = testList "SseDedupKey equivalence" [
  testProperty "WHY — two models share a key exactly when they shared one before the rewrite"
    (Prop.forAll (Arb.fromGen genPair) (fun (a, b) ->
      let ma = toModel a
      let mb = toModel b
      let before = (oldKey ma = oldKey mb)
      let after = (SseDedupKey.fromModel ma = SseDedupKey.fromModel mb)
      before = after))

  testCase "WHY — a change nothing in the key shows (an eval count) leaves the key equal" <| fun _ ->
    let spec =
      { OutputLines = 1; DiagLists = [ "f1", 1 ]; Sessions = [ 0, 0, 1 ]; Active = Some 0
        Summary = (3, 1, 1, 0, 0); StateVersion = 4; Generation = 2
        Phases = [ "p1", 1, 2 ]; Active01 = 1 }
    let bumped = { spec with Sessions = spec.Sessions |> List.map (fun (i, s, e) -> i, s, e + 5) }
    SseDedupKey.fromModel (toModel bumped)
    |> Expect.equal "an eval count alone must not wake the SSE stream" (SseDedupKey.fromModel (toModel spec))

  testProperty "WHY — fault text made of the key's own delimiters cannot make two different fault lists share a key"
    (let delimiterText = Gen.elements [ ';'; ':'; '~'; 'f'; '1'; 'b' ] |> Gen.listOf |> Gen.map (fun cs -> String(Array.ofList cs))
     let pairs = Gen.zip delimiterText delimiterText
     Prop.forAll (Arb.fromGen (Gen.zip pairs pairs)) (fun ((r1, r2), (s1, s2)) ->
       let build (a: string) (b: string) =
         let m = toModel { OutputLines = 0; DiagLists = []; Sessions = []; Active = None
                           Summary = (0, 0, 0, 0, 0); StateVersion = 0; Generation = 0
                           Phases = []; Active01 = 0 }
         let snaps =
           [ a; b ]
           |> List.mapi (fun i reason ->
             { Id = (match SessionId.validate ids.[i % ids.Length] with
                     | Ok sid -> sid
                     | Result.Error e -> failwithf "bad fixture id: %A" e)
               Name = None; Projects = []; Status = SessionDisplayStatus.Faulted reason
               LastActivity = DateTime.UnixEpoch; EvalCount = 0; UpSince = DateTime.UnixEpoch
               WorkingDirectory = "/w" })
         SseDedupKey.fromModel { m with Sessions = { m.Sessions with Sessions = snaps } }
       (build r1 r2 = build s1 s2) = ((r1, r2) = (s1, s2))))
]
