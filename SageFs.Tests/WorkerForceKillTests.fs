/// Issue #154: the force kill after a failed graceful shutdown was gated on an inverted
/// liveness check, so a live worker was left running. The decision is now a pure function
/// over a `hasExited` probe, tested here without a real process.
module SageFs.Tests.WorkerForceKillTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.WorkerStop

/// How the probe answers: the three things a process handle can do when asked.
type ProbeAnswer =
  | LiveProcess
  | ExitedProcess
  | ProbeThrows

let probeOf (answer: ProbeAnswer) : unit -> bool =
  match answer with
  | LiveProcess -> fun () -> false
  | ExitedProcess -> fun () -> true
  | ProbeThrows -> fun () -> failwith "the handle is disposed"

/// The expression SessionManager.fs had before the fix, verbatim except that the
/// handle is the probe. `|> not` binds to the handler only.
let buggyProcAlive (hasExited: unit -> bool) : bool =
  try hasExited () with _ -> true |> not

[<Tests>]
let workerForceKillTests = testList "worker force kill after a failed graceful shutdown" [

  testCase "WHY — a live worker is force killed — otherwise a failed graceful shutdown leaks it (issue 154)" <| fun _ ->
    needsForceKill (probeOf LiveProcess)
    |> Expect.equal "live process is killed" ForceKill

  testCase "WHY — an exited worker is left alone — there is nothing to kill" <| fun _ ->
    needsForceKill (probeOf ExitedProcess)
    |> Expect.equal "exited process is not killed" AlreadyExited

  testCase "WHY — a probe that throws is treated as alive — fail closed, a leaked worker costs more than a kill on a dead one" <| fun _ ->
    needsForceKill (probeOf ProbeThrows)
    |> Expect.equal "unknown liveness is killed" ForceKill

  testCase "WHY — the old expression disagrees on a live process — this is the RED proof that the inversion was real" <| fun _ ->
    // procAlive = true means "kill". For a live process the old code said false.
    buggyProcAlive (probeOf LiveProcess)
    |> Expect.isFalse "the old expression skipped the kill for a live worker"
    needsForceKill (probeOf LiveProcess)
    |> Expect.notEqual "the fixed decision differs from the old one" AlreadyExited

  testCase "WHY — the old expression was wrong on all three outcomes — it was inverted on the process and on the handler" <| fun _ ->
    let oldSaysKill answer = buggyProcAlive (probeOf answer)
    let newSaysKill answer = (needsForceKill (probeOf answer) = ForceKill)
    [ LiveProcess; ExitedProcess; ProbeThrows ]
    |> List.map (fun a -> oldSaysKill a <> newSaysKill a)
    |> Expect.allEqual "every outcome differs" true

  testProperty "WHY — a worker is spared only when the probe positively says it exited" <| fun (answer: ProbeAnswer) ->
    let spared = (needsForceKill (probeOf answer) = AlreadyExited)
    spared = (answer = ExitedProcess)
]
