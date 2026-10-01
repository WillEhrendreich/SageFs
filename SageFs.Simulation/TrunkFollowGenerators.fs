namespace SageFs.Simulation

open System
open SageFs.Simulation.TrunkFollowSim

/// Seeded, dependency-free generators for trunk-follow scenarios. Chaos is data: `fromSeed n` replays identically forever.
module TrunkFollowGenerators =

  /// A general scenario: 4 to 28 ops. Same seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 29)
    let moveEnds () =
      match rnd.Next 10 with
      | 0 | 1 | 2 | 3 | 4 | 5 -> MoveEnds.Moves Sessions.Serving
      | 6 | 7 -> MoveEnds.Moves Sessions.NoApp
      | 8 -> MoveEnds.Moves Sessions.NoSession
      | _ -> MoveEnds.Refuses
    let workerDoes () =
      match rnd.Next 10 with
      | 0 | 1 | 2 | 3 | 4 | 5 -> WorkerDoes.Patches
      | 6 | 7 | 8 -> WorkerDoes.Restarts
      | _ -> WorkerDoes.GoesSilent
    let ops =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 16) with
          | 0 | 1 | 2 | 3 -> Op.Land
          | 4 -> Op.Verify
          | 5 -> Op.Refuse
          | 6 | 7 | 8 -> Op.MoveDone(rnd.Next(0, 4), moveEnds ())
          | 9 | 10 | 11 -> Op.WorkerRuns(rnd.Next(0, 4), workerDoes ())
          | 12 | 13 -> Op.AnswerArrives(rnd.Next(0, 4))
          | _ -> Op.BodyRuns(rnd.Next(0, 4)) ]
    { Seed = seed; Ops = ops }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// The whole path for one landing: it lands, the checkout moves under a session running an app, the worker patches, the answer
  /// arrives, and a request runs the new body.
  let oneLanding : Scenario =
    { Seed = 400
      Ops = [ Op.Land; Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Patches); Op.AnswerArrives 0; Op.BodyRuns 0 ] }

  /// Three landings land before the first is followed: the other two wait, and are followed in the order they landed.
  let threeQueued : Scenario =
    { Seed = 401
      Ops =
        [ Op.Land; Op.Land; Op.Land
          Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Patches); Op.AnswerArrives 0
          Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Patches); Op.AnswerArrives 0 ] }

  /// A landing is verified and refused, then another lands: only the one that landed reaches the trunk.
  let refusedNeverFollowed : Scenario =
    { Seed = 402
      Ops = [ Op.Verify; Op.Refuse; Op.Land; Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Patches); Op.AnswerArrives 0 ] }

  /// A landing is still being verified when the one before it is followed.
  let verifyingNeverFollowed : Scenario =
    { Seed = 403
      Ops = [ Op.Land; Op.Verify; Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Patches); Op.AnswerArrives 0 ] }

  /// The worker restarts the app for a landing, naming why.
  let restarts : Scenario =
    { Seed = 404
      Ops = [ Op.Land; Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Restarts); Op.AnswerArrives 0 ] }

  /// A request runs the patched body before the answer that carries the patch has reached the daemon.
  let earlyReport : Scenario =
    { Seed = 405
      Ops = [ Op.Land; Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Patches); Op.BodyRuns 0; Op.AnswerArrives 0 ] }

  /// A landing lands while another is mid-delivery, and one more while the move is in flight.
  let landDuringDelivery : Scenario =
    { Seed = 406
      Ops =
        [ Op.Land; Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.Land
          Op.WorkerRuns(0, WorkerDoes.Patches); Op.Land; Op.AnswerArrives 0
          Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Restarts); Op.AnswerArrives 0
          Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Patches); Op.AnswerArrives 0 ] }

  /// The trunk session runs no app.
  let noRunningApp : Scenario =
    { Seed = 407
      Ops = [ Op.Land; Op.MoveDone(0, MoveEnds.Moves Sessions.NoApp) ] }

  /// No session works in the trunk checkout.
  let noTrunkSession : Scenario =
    { Seed = 408
      Ops = [ Op.Land; Op.MoveDone(0, MoveEnds.Moves Sessions.NoSession) ] }

  /// The checkout cannot be moved, and the next landing is followed all the same.
  let moveRefused : Scenario =
    { Seed = 409
      Ops = [ Op.Land; Op.Land; Op.MoveDone(0, MoveEnds.Refuses); Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.Patches); Op.AnswerArrives 0 ] }

  /// The worker never answers.
  let silentWorker : Scenario =
    { Seed = 410
      Ops = [ Op.Land; Op.MoveDone(0, MoveEnds.Moves Sessions.Serving); Op.WorkerRuns(0, WorkerDoes.GoesSilent); Op.AnswerArrives 0 ] }
