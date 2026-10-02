module LemScore.Tests.SlimTests

open System
open System.IO
open Expecto
open Expecto.Flip
open LemScore

let private scratchRoot () : string =
  let dir = Path.Combine(Path.GetTempPath(), "lem-slim-" + Guid.NewGuid().ToString("N").Substring(0, 8), "lem")
  Directory.CreateDirectory dir |> ignore
  dir

let private cleanup (root: string) =
  match Path.GetDirectoryName root with
  | null -> ()
  | parent -> try Directory.Delete(parent, true) with _ -> ()

let private bytes (n: int) = String('x', n)

/// A run as it is at the end of a lemming: a working copy, scratch, a NuGet folder, and its evidence.
let private makeRun (root: string) (id: string) (marker: string option) : string =
  let run = Path.Combine(root, id)
  for dir in [ "w/src"; "dotnethome/nuget/packages"; "cmdchome"; "bin"; "out/sbx"; "out/shots"; "out/ui/ts/parser" ] do Directory.CreateDirectory(Path.Combine(run, dir)) |> ignore
  File.WriteAllText(Path.Combine(run, "w/src/a.fs"), bytes 5000)
  File.WriteAllText(Path.Combine(run, "dotnethome/nuget/packages/x.nupkg"), bytes 20000)
  File.WriteAllText(Path.Combine(run, "out/shots/001.png"), bytes 3000)
  File.WriteAllText(Path.Combine(run, "out/timeline.ndjson"), "{}")
  File.WriteAllText(Path.Combine(run, "out/ui/ts/parser/fsharp.so"), bytes 40000)
  File.WriteAllText(Path.Combine(run, "out/ui/init.lua"), "-- init")
  marker |> Option.iter (fun m -> File.WriteAllText(Path.Combine(run, "out", m), "{}"))
  run

[<Tests>]
let slimming =
  testList "a finished run keeps only its evidence" [
    testCase "everything except out/ goes, and out/ keeps its summary, timeline and shots" <| fun _ ->
      let root = scratchRoot ()
      try
        let run = makeRun root "done-01" (Some "summary.json")
        match Prune.slim run with
        | Ok freed ->
          Directory.GetFileSystemEntries run |> Array.map Path.GetFileName |> Expect.equal "only out" [| "out" |]
          File.Exists(Path.Combine(run, "out", "summary.json")) |> Expect.isTrue "summary"
          File.Exists(Path.Combine(run, "out", "timeline.ndjson")) |> Expect.isTrue "timeline"
          File.Exists(Path.Combine(run, "out", "shots", "001.png")) |> Expect.isTrue "shots"
          File.Exists(Path.Combine(run, "out", "ui", "init.lua")) |> Expect.isTrue "the editor's small files stay"
          Directory.Exists(Path.Combine(run, "out", "ui", "ts")) |> Expect.isFalse "the copy of the parser is staging, not evidence"
          (freed >= 25000L) |> Expect.isTrue "freed what was outside out/"
        | Error e -> failtest e
      finally cleanup root

    testCase "a run still going (no summary.json, no tour.json) is never touched" <| fun _ ->
      let root = scratchRoot ()
      try
        let run = makeRun root "live-01" None
        Prune.slim run |> Result.isError |> Expect.isTrue "refused"
        File.Exists(Path.Combine(run, "w/src/a.fs")) |> Expect.isTrue "still there"
      finally cleanup root

    testCase "a tour has no score, and its tour.json says it is over" <| fun _ ->
      let root = scratchRoot ()
      try
        let run = makeRun root "tour-x-01" (Some "tour.json")
        Prune.slim run |> Result.isOk |> Expect.isTrue "slimmed"
        Directory.Exists(Path.Combine(run, "w")) |> Expect.isFalse "working copy gone"
      finally cleanup root

    testCase "a path that is not a run directory is refused: a root can never be slimmed" <| fun _ ->
      Prune.slim "/" |> Result.isError |> Expect.isTrue "/"
      Prune.slim "/tmp" |> Result.isError |> Expect.isTrue "/tmp"
      Prune.slim "/tmp/lem" |> Result.isError |> Expect.isTrue "the run root"
      Prune.slim "relative/dir/x" |> Result.isError |> Expect.isTrue "relative"

    testCase "read-only trees in a run (a build left them) do not stop it" <| fun _ ->
      let root = scratchRoot ()
      try
        let run = makeRun root "ro-01" (Some "summary.json")
        let file = Path.Combine(run, "w/src/a.fs")
        File.SetUnixFileMode(file, UnixFileMode.UserRead)
        File.SetUnixFileMode(Path.Combine(run, "w/src"), UnixFileMode.UserRead ||| UnixFileMode.UserExecute)
        Prune.slim run |> Result.isOk |> Expect.isTrue "slimmed"
        Directory.Exists(Path.Combine(run, "w")) |> Expect.isFalse "gone"
      finally cleanup root

    testCase "the plan frees only what is outside out/ by default, and everything with --whole" <| fun _ ->
      let root = scratchRoot ()
      try
        makeRun root "a-01" (Some "summary.json") |> ignore
        match Prune.plan root DateTime.UtcNow [ "a-01" ] None with
        | Ok plan ->
          let run = plan.Delete.Head
          (Prune.freedBytes Prune.KeepEvidence run < Prune.freedBytes Prune.DeleteWhole run) |> Expect.isTrue "evidence is not counted as freed"
          (Prune.freedBytes Prune.KeepEvidence run >= 25000L) |> Expect.isTrue "the working copy and scratch are"
        | Error e -> failtest e
      finally cleanup root

    testCase "a tour counts as finished for pruning by age" <| fun _ ->
      let root = scratchRoot ()
      try
        makeRun root "tour-y-01" (Some "tour.json") |> ignore
        match Prune.plan root DateTime.UtcNow [] (Some 0.0) with
        | Ok plan -> plan.Delete |> List.map _.Id |> Expect.equal "selected" [ "tour-y-01" ]
        | Error e -> failtest e
      finally cleanup root

    testCase "the supervisor's own process is scaffolding, not something the lemming left behind" <| fun _ ->
      let ps =
        String.concat "\n"
          [ "    1       0       0 bwrap --die-with-parent --unshare-pid"
            "    2       1       0 /home/u/.dotnet/dotnet /repo/scripts/lemmings/LemRun/bin/Release/net11.0/LemRun.dll supervise --ps-dir /tmp/x -- timeout --kill-after=30 1500 cmdc -p x"
            "   85       2      17 /home/u/.dotnet/sdk/11/MSBuild /nologo /nodemode:8"
            "  257       2       0 ps -eo pid,ppid,etimes,args --no-headers" ]
      LemScore.Classify.leftoverProcesses ps |> Expect.equal "only the MSBuild node" [ "/home/u/.dotnet/sdk/11/MSBuild /nologo /nodemode:8" ]
  ]
