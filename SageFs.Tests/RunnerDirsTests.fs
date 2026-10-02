/// The runners' temp dirs: each run dir records its owner, the runner removes its own on the way out, and the next
/// runner's start removes the ones whose owner is gone and that nobody has written to for a while. Only a dir with a
/// marker is ever swept.
module SageFs.Tests.RunnerDirsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Tests.RunnerDirs

/// A pid no process has: `Int32.MaxValue` is above any real pid_max.
let private deadPid = Int32.MaxValue

let private aged (dir: string) (by: TimeSpan) = Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - by)

let private runDir (temp: string) (name: string) (pid: int option) (age: TimeSpan) : string =
  let dir = Path.Combine(temp, familyName Family.HotReloadRuns, name)
  Directory.CreateDirectory dir |> ignore
  File.WriteAllText(Path.Combine(dir, "data.bin"), "x")
  pid |> Option.iter (fun p -> OrphanTempDirSweep.writeOwnerPid OwnerMarker dir p)
  aged dir age
  dir

[<Tests>]
let tests =
  testList "Runner temp dirs" [

    testCase "the sweep takes a run dir whose owner is gone and that is old, and nothing else" <| fun _ ->
      let temp = Directory.CreateTempSubdirectory("sagefs-runnerdirs-test-").FullName
      try
        let old = HygieneAges.ancient
        let deadAndOld = runDir temp "dead-old" (Some deadPid) old
        let deadAndYoung = runDir temp "dead-young" (Some deadPid) HygieneAges.justNow
        let aliveAndOld = runDir temp "alive-old" (Some Environment.ProcessId) old
        let unmarkedAndOld = runDir temp "unmarked-old" None old
        let removed = sweepStale temp Family.HotReloadRuns DateTime.UtcNow
        removed |> List.map fst |> Expect.equal "only the dead and old one" [ deadAndOld ]
        Directory.Exists deadAndYoung |> Expect.isTrue "a run that just crashed keeps its leftovers for a person to read"
        Directory.Exists aliveAndOld |> Expect.isTrue "a live owner is never swept, however old the dir"
        Directory.Exists unmarkedAndOld |> Expect.isTrue "a dir with no marker is not provably ours"
      finally Directory.Delete(temp, true)

    testCase "creating a run dir sweeps the family's stale ones first and records this process as the owner" <| fun _ ->
      let temp = Directory.CreateTempSubdirectory("sagefs-runnerdirs-test-").FullName
      try
        let stale = runDir temp "stale" (Some deadPid) HygieneAges.ancient
        let fresh = createIn temp Family.HotReloadRuns
        Directory.Exists stale |> Expect.isFalse "the stale one went"
        Directory.Exists fresh |> Expect.isTrue "the new one exists"
        OrphanTempDirSweep.readOwnerPid OwnerMarker fresh |> Expect.equal "owned by this process" (Some Environment.ProcessId)
        Path.GetDirectoryName fresh |> Expect.equal "under its family" (Path.Combine(temp, familyName Family.HotReloadRuns))
      finally Directory.Delete(temp, true)

    testCase "a runner removes its own dir on the way out, and a dir already gone is not an error" <| fun _ ->
      let temp = Directory.CreateTempSubdirectory("sagefs-runnerdirs-test-").FullName
      try
        let dir = createIn temp Family.BrowserRuns
        remove dir
        Directory.Exists dir |> Expect.isFalse "gone"
        remove dir
      finally Directory.Delete(temp, true)

    testCase "every family has its own name, and each name starts with sagefs- so the path gate and the sweep both know it" <| fun _ ->
      let names = [ Family.BrowserRuns; Family.HotReloadRuns; Family.LiveTestingRuns; Family.TestDatabases; Family.TestScratch ] |> List.map familyName
      names |> List.distinct |> List.length |> Expect.equal "distinct" (List.length names)
      names |> List.forall (fun n -> n.StartsWith "sagefs-") |> Expect.isTrue "prefixed"
  ]
