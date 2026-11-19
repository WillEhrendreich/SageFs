/// DST for the edit door. The decisions are the real pure functions in
/// WriteBackRequest and EvalWriteBack; this suite only supplies the world and
/// the scheduler. A failure prints the seed and the op list for replay.
module SageFs.Tests.WriteBackSimTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.Tweak
open SageFs.Simulation
open SageFs.Simulation.WriteBackSim

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 400 }

let private initialFiles = Map.ofList [ ownedFile, sourceBefore ]

let private genOp =
  Gen.elements
    [ Op.ValidEdit; Op.ValidEdit; Op.ValidEdit
      Op.RepeatedEdit
      Op.BareExpression
      Op.ForeignFile
      Op.WrongLine
      Op.NoFile
      Op.NoLine ]

let private genScenario =
  gen {
    let! seed = Gen.choose (0, 10000)
    let! count = Gen.choose (1, 12)
    let! ops = Gen.listOfLength count genOp
    return { Seed = seed; Ops = ops }
  }

let private replay (t: Trace) = sprintf "seed=%d ops=%A" t.Scenario.Seed t.Scenario.Ops

let private check (label: string) (violations: string list) (t: Trace) =
  match violations with
  | [] -> ()
  | _ -> failtestf "INVARIANT VIOLATION (%s) — replay: %s\n%A" label (replay t) violations

let private effectsOf (t: Trace) = t.Steps |> List.collect (fun s -> s.Effects)

let private writes (t: Trace) =
  effectsOf t
  |> List.choose (function Effect.Written(file, line, replacement) -> Some(file, line, replacement) | _ -> None)

/// No step may change the world unless its own effect says a declaration was
/// written. The previous step's files are the baseline, because a successful
/// write legitimately changes them for every step that follows.
let private changedWithoutWrite (t: Trace) =
  t.Steps
  |> List.fold (fun (previous, bad) step ->
    let stepBad =
      match step.Effects with
      | [ Effect.Written _ ] -> false
      | _ -> step.Files <> previous
    step.Files, bad || stepBad) (initialFiles, false)
  |> snd

[<Tests>]
let writeBackSimTests =
  testList "DST write back" [

    testPropertyWithConfig simConfig "seeded scenarios hold every invariant of the edit door" <|
      Prop.forAll (Arb.fromGen genScenario) (fun scenario ->
        let t = run scenario

        // A request the front door refused never reached a write.
        let refusedThenWrote =
          t.Steps
          |> List.exists (fun s ->
            match s.Effects with
            | [ Effect.Refused _ ] -> s.Op <> Op.NoFile && s.Op <> Op.NoLine
            | _ -> false)

        let foreignWrites = writes t |> List.filter (fun (f, _, _) -> f <> ownedFile)
        let wrongText = writes t |> List.filter (fun (_, _, replacement) -> replacement <> "let answer = 2")

        check "the edit door"
          [ if refusedThenWrote then "a front-door refusal produced a write"
            if not (List.isEmpty foreignWrites) then sprintf "wrote a foreign file: %A" foreignWrites
            if not (List.isEmpty wrongText) then sprintf "wrong replacement: %A" wrongText
            if changedWithoutWrite t then "the world changed without a Written effect" ]
          t)

    testCase "WHY — a valid edit writes, and the same edit again is refused as unchanged" <| fun _ ->
      let t = run { Seed = 1; Ops = [ Op.ValidEdit; Op.RepeatedEdit ] }
      match t.Steps.[0].Effects with
      | [ Effect.Written(file, line, _) ] ->
        file |> Expect.equal "the owned file" ownedFile
        line |> Expect.equal "the declaration line" declarationLine
      | other -> failtestf "expected a write, got %A" other
      match t.Steps.[1].Effects with
      | [ Effect.NotPersisted reason ] -> reason |> Expect.stringContains "unchanged" "unchanged"
      | other -> failtestf "expected unchanged, got %A" other

    testCase "WHY — an expression and a wrong line are refused without touching the file" <| fun _ ->
      for op in [ Op.BareExpression; Op.WrongLine ] do
        let t = run { Seed = 2; Ops = [ op ] }
        match t.Steps.[0].Effects with
        | [ Effect.NotPersisted _ ] -> ()
        | other -> failtestf "%A should have been refused, got %A" op other
        t.Steps.[0].Files |> Expect.equal "the file is byte-identical" initialFiles

    testCase "WHY — a missing file or line is refused by the front door before any eval" <| fun _ ->
      for op in [ Op.NoFile; Op.NoLine ] do
        let t = run { Seed = 3; Ops = [ op ] }
        match t.Steps.[0].Effects with
        | [ Effect.Refused _ ] -> ()
        | other -> failtestf "%A should have been refused before IO, got %A" op other
  ]
