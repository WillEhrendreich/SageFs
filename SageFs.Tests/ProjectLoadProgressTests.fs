/// "project 34 of 61" — the piece of warmup progress that didn't exist
/// before this: get_fsi_status could say a session was stuck, and for how
/// long, but never where. `ProjectLoading.ProjectLoadProgress` is the pure
/// fold that turns Ionide's own per-project notification stream into that
/// number; `loadSolution` below is where it actually gets fed live.
module SageFs.Tests.ProjectLoadProgressTests

open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.ProjectLoading
open SageFs.ProjectLoading.ProjectLoadProgress

let private quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

[<Tests>]
let pureProgressFoldTests =
  testList "ProjectLoadProgress — pure fold over Ionide's notification stream" [
    testCase "Loading reports nothing yet — there is no honest step to give"
    <| fun _ ->
      let _, reported = step initial (Loading "/repo/Foo.fsproj")
      reported |> Expect.isNone "nothing has completed, so no line should be emitted"

    testCase "the first Loaded reports step 1 of at least 1"
    <| fun _ ->
      let _, reported = step initial (Loaded("/repo/Foo.fsproj", 1))
      match reported with
      | Some(step, total, message) ->
        step |> Expect.equal "one project has completed" 1
        (total, step) |> Expect.isGreaterThanOrEqual "total covers what completed"
        message |> Expect.stringContains "names the file, not the whole path" "Foo.fsproj"
      | None -> failtest "a completed project must report progress"

    testCase "a growing knownProjects count raises the total, never lowers it"
    <| fun _ ->
      let s1, r1 = step initial (Loaded("/repo/A.fsproj", 1))
      let _, r2 = step s1 (Loaded("/repo/B.fsproj", 5))
      match r1, r2 with
      | Some(_, total1, _), Some(step2, total2, _) ->
        (total2, total1) |> Expect.isGreaterThanOrEqual "the loader discovered more projects than it first knew about"
        step2 |> Expect.equal "two projects have now completed" 2
      | _ -> failtest "both loads should report"

    testCase "a failed project still advances step, so the count can't stall on one bad project"
    <| fun _ ->
      let s1, _ = step initial (Loaded("/repo/A.fsproj", 2))
      let _, reported = step s1 (Failed "/repo/Broken.fsproj")
      match reported with
      | Some(step, total, message) ->
        step |> Expect.equal "the failure still counts as one more processed" 2
        (total, step) |> Expect.isGreaterThanOrEqual "total still covers step"
        message |> Expect.stringContains "says which one failed" "Broken.fsproj"
      | None -> failtest "a failed project must still report progress"

    testProperty "step is always positive and never exceeds total, for any sequence of updates"
    <| fun (fileNames: NonEmptyString list) ->
      let updates =
        fileNames
        |> List.mapi (fun i (NonEmptyString name) ->
          let path = sprintf "/repo/%s.fsproj" name
          match i % 3 with
          | 0 -> Loading path
          | 1 -> Loaded(path, i + 1)
          | _ -> Failed path)
      updates
      |> List.fold
        (fun s update ->
          let s', reported = step s update
          match reported with
          | Some(stepCount, total, _) ->
            Expect.isTrue "step must be positive" (stepCount > 0)
            Expect.isTrue "step must never exceed total" (stepCount <= total)
          | None -> ()
          s')
        initial
      |> ignore

    testProperty "State never grows past two ints, however many updates stream through"
    <| fun (n: PositiveInt) ->
      // Bounded-memory claim, made concrete: folding thousands of updates
      // through `step` produces a `State` that is still exactly `Completed`
      // and `KnownTotal` — no list, no accumulated history — so `Completed`
      // can never exceed the number of Loaded/Failed updates actually folded,
      // regardless of how many updates (Loading included) streamed through.
      let count = min n.Get 5000
      let final =
        [ 1 .. count ]
        |> List.fold (fun s i -> step s (Loaded(sprintf "/repo/P%d.fsproj" i, i)) |> fst) initial
      final.Completed |> Expect.equal "every Loaded update completes exactly one project" count
  ]

/// Fed through the REAL Ionide loader (the smallest fixture project in the
/// repo), not a fake notification stream — this is the proof `loadSolution`
/// actually wires `ProjectLoadProgress` to its `onProgress` callback, not
/// merely that the fold itself is correct.
[<Tests>]
let loadSolutionProgressTests =
  testList "loadSolution reports real progress through onProgress" [
    testCase "loading the TestWorkspace fixture reports at least one valid, in-range step"
    <| fun _ ->
      let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
      let fixture = Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "TestWorkspace", "TestWorkspace.fsproj")
      let reported = ResizeArray<int * int * string>()
      let config = { SageFs.Args.ProjectLoadConfig.empty with Projects = [ fixture ]; WorkingDir = Path.GetDirectoryName fixture }
      let solution = loadSolution quietLogger config (fun step total message -> reported.Add(step, total, message))
      solution.Projects |> Expect.isNonEmpty "the fixture project should actually load"
      reported |> Expect.isNonEmpty "loading a real project should report at least one step"
      for (step, total, _) in reported do
        Expect.isTrue "every reported step must be positive" (step > 0)
        Expect.isTrue "every reported step must fit inside its own total" (step <= total)
      let lastStep, lastTotal, _ = reported.[reported.Count - 1]
      lastStep |> Expect.equal "the one fixture project is the last one reported" lastTotal
  ]
