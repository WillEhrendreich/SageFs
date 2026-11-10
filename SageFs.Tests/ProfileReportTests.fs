module SageFs.Tests.ProfileReportTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features

/// The report half of the profile tool: stack samples in, ranked bounded rows out. The rows are held to what a reader can check
/// on their own: they plus the "(other)" row sum to the total, whatever `top` is, and the user's frames are found by one rule.

let private config = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private runtimeFrame (i: int) : Frame = { Module = "System.Private.CoreLib"; Method = sprintf "System.Thing%d.Run" i }

let private userFrame (i: int) : Frame = { Module = Frame.evalAssembly; Method = sprintf "FSI_%04d.work%d" i i }

/// Samples from a seed: random stacks over a small set of frames, random kinds, random times. Some have no frame at all.
let private samplesOfSeed (seed: int) : StackSample list =
  let random = Random seed
  let frameOf () =
    match random.Next 2 with
    | 0 -> runtimeFrame (random.Next 6)
    | _ -> userFrame (random.Next 4)
  let kinds = [| SampleKind.ManagedCpu; SampleKind.ManagedCpu; SampleKind.ManagedCpu; SampleKind.NativeCode; SampleKind.Blocked; SampleKind.Unmarked |]
  [ for _ in 1 .. random.Next(1, 40) ->
      { Kind = kinds.[random.Next kinds.Length]
        Frames = [ for _ in 1 .. random.Next(0, 6) -> frameOf () ]
        Ms = float (random.Next(1, 500)) / 7.0 } ]

let private allocSamplesOfSeed (seed: int) : AllocSample list =
  let random = Random seed
  [ for _ in 1 .. random.Next(1, 40) ->
      { TypeName = sprintf "Type%d" (random.Next 8)
        Bytes = int64 (random.Next(1, 100000))
        Frames = [ for _ in 1 .. random.Next(0, 5) -> (match random.Next 2 with | 0 -> runtimeFrame (random.Next 4) | _ -> userFrame (random.Next 3)) ] } ]

let private near (a: float) (b: float) = abs (a - b) < 1e-6

[<Tests>]
let reportTests =
  testList "Profile report" [

    testPropertyWithConfig config
      "WHY - the CPU rows plus the other row sum to the managed total and to 100 percent, whatever top is"
      <| fun (seed: int) (topSeed: byte) ->
        let top = int topSeed % 12
        let report = ProfileReport.cpu Set.empty top (samplesOfSeed seed)
        (near (report.SelfRows |> List.sumBy (fun r -> r.Weight)) report.ManagedMs)
        |> Expect.isTrue (sprintf "rows weigh %A, the total is %A" (report.SelfRows |> List.sumBy (fun r -> r.Weight)) report.ManagedMs)
        match report.ManagedMs > 0.0 with
        | true -> (near (report.SelfRows |> List.sumBy (fun r -> r.Percent)) 100.0) |> Expect.isTrue "percentages sum to 100"
        | false -> report.SelfRows |> Expect.isEmpty "no managed time, no rows"

    testPropertyWithConfig config
      "WHY - a table never has more than top rows plus the other row, and the rows run heaviest first"
      <| fun (seed: int) (topSeed: byte) ->
        let top = int topSeed % 12
        let report = ProfileReport.cpu Set.empty top (samplesOfSeed seed)
        let named = report.SelfRows |> List.filter (fun r -> r.Label <> ProfileReport.otherLabel)
        (List.length named <= top) |> Expect.isTrue "no more named rows than top"
        (List.length report.UserRows <= top) |> Expect.isTrue "the user table is cut to top too"
        (named |> List.map (fun r -> r.Weight)) |> Expect.equal "heaviest first" (named |> List.map (fun r -> r.Weight) |> List.sortDescending)

    testPropertyWithConfig config
      "WHY - time in native code and blocked time are counted apart from managed CPU and never inside it"
      <| fun (seed: int) ->
        let samples = samplesOfSeed seed
        let report = ProfileReport.cpu Set.empty 5 samples
        let msOf kind = samples |> List.filter (fun s -> s.Kind = kind) |> List.sumBy (fun s -> s.Ms)
        (near report.ManagedMs (msOf SampleKind.ManagedCpu)) |> Expect.isTrue "managed is the managed samples"
        (near report.NativeMs (msOf SampleKind.NativeCode)) |> Expect.isTrue "native is its own total"
        (near report.BlockedMs (msOf SampleKind.Blocked)) |> Expect.isTrue "blocked is its own total"

    testPropertyWithConfig config
      "WHY - time spent waiting for a garbage collection is part of the managed time, never more than it"
      <| fun (seed: int) ->
        let report = ProfileReport.cpu Set.empty 5 (samplesOfSeed seed)
        (report.GcWaitMs <= report.ManagedMs + 1e-9) |> Expect.isTrue "a part of the managed time is not more than the managed time"

    testCase "WHY - a thread seen waiting for a garbage collection is labelled, with its share of the managed time" <| fun _ ->
      let waiting = { Module = "System.Private.CoreLib"; Method = "System.Threading.Thread.<PollGC>g__PollGCWorker|71_0" }
      let report =
        ProfileReport.cpu
          Set.empty
          5
          [ { Kind = SampleKind.ManagedCpu; Frames = [ waiting; userFrame 1 ]; Ms = 25.0 }
            { Kind = SampleKind.ManagedCpu; Frames = [ userFrame 1 ]; Ms = 75.0 } ]
      report.GcWaitMs |> Expect.equal "the waiting sample" 25.0
      report.ManagedMs |> Expect.equal "and it is still managed time" 100.0
      ProfileReport.renderCpu ProfileReport.defaultBounds report
      |> Expect.stringContains "the share is said" "25 ms (25.0% of the managed time)"

    testCase "WHY - a managed sample with no stack is named in the table, never dropped" <| fun _ ->
      let report = ProfileReport.cpu Set.empty 5 [ { Kind = SampleKind.ManagedCpu; Frames = []; Ms = 12.0 } ]
      (report.SelfRows |> List.map (fun r -> r.Label)) |> Expect.equal "the row says what it is" [ ProfileReport.noStackLabel ]
      report.NoStackMs |> Expect.equal "and is counted" 12.0

    testCase "WHY - a recursive frame counts once per sample in the user table" <| fun _ ->
      let hot = userFrame 1
      let report = ProfileReport.cpu Set.empty 5 [ { Kind = SampleKind.ManagedCpu; Frames = [ hot; hot; hot; runtimeFrame 1 ]; Ms = 40.0 } ]
      match report.UserRows with
      | [ row ] ->
        row.Weight |> Expect.equal "40 ms, not 120" 40.0
        row.Percent |> Expect.equal "all of the sampled time" 100.0
      | other -> failtestf "expected one user row, got %A" other

    testCase "WHY - the heaviest user frame is the first row of the user table" <| fun _ ->
      let report =
        ProfileReport.cpu
          Set.empty
          5
          [ { Kind = SampleKind.ManagedCpu; Frames = [ userFrame 1; runtimeFrame 1 ]; Ms = 10.0 }
            { Kind = SampleKind.ManagedCpu; Frames = [ userFrame 2; runtimeFrame 1 ]; Ms = 90.0 } ]
      (report.UserRows |> List.head |> fun r -> r.Label) |> Expect.equal "the 90 ms frame leads" (Frame.label (userFrame 2))

    testList "who owns a frame" [
      testCase "WHY - a frame in the eval assembly is the user's" <| fun _ ->
        Frame.ownerOf Set.empty (userFrame 3) |> Expect.equal "typed into the session" FrameOwner.UserCode
      testCase "WHY - a startup stub of an eval is the user's" <| fun _ ->
        Frame.ownerOf Set.empty { Module = "x"; Method = "<StartupCode$FSI_0014>.$FSI_0014.main@" } |> Expect.equal "startup code of an FSI submission" FrameOwner.UserCode
      testCase "WHY - a frame from a project the session owns is the user's" <| fun _ ->
        Frame.ownerOf (Set.ofList [ "MyProject" ]) { Module = "MyProject"; Method = "Lib.compute" } |> Expect.equal "project assembly" FrameOwner.UserCode
      testCase "WHY - the runtime, the libraries and the host are not the user's" <| fun _ ->
        [ runtimeFrame 1
          { Module = "FsiHost"; Method = "SageFs.FsiHost.Program.main" }
          { Module = "FSharp.Compiler.Service"; Method = "FSharp.Compiler.Interactive.Shell.EvalInteraction" } ]
        |> List.map (Frame.ownerOf (Set.ofList [ "MyProject" ]))
        |> List.distinct
        |> Expect.equal "one owner for all of them" [ FrameOwner.RuntimeCode ]
      testPropertyWithConfig config "WHY - the owner rule is total: a frame of any name has one owner" <| fun (m: string) (n: string) ->
        let frame = { Module = (match isNull m with | true -> "" | false -> m); Method = (match isNull n with | true -> "" | false -> n) }
        match Frame.ownerOf Set.empty frame with
        | FrameOwner.UserCode
        | FrameOwner.RuntimeCode -> ()
    ]

    testList "allocation" [
      testPropertyWithConfig config
        "WHY - allocation rows plus the other row sum to the sampled bytes, by type and by site"
        <| fun (seed: int) (topSeed: byte) ->
          let report = ProfileReport.alloc Set.empty (int topSeed % 8) (allocSamplesOfSeed seed)
          let total = float report.TotalBytes
          (near (report.ByType |> List.sumBy (fun r -> r.Weight)) total) |> Expect.isTrue "by type sums to the total"
          (near (report.BySite |> List.sumBy (fun r -> r.Weight)) total) |> Expect.isTrue "by site sums to the total"

      testCase "WHY - the nearest frame of yours is the first of yours walking from the leaf" <| fun _ ->
        let frames = [ runtimeFrame 1; userFrame 7; userFrame 2; runtimeFrame 2 ]
        ProfileReport.nearestUserFrame Set.empty frames |> Expect.equal "leaf side first" (Some(userFrame 7))

      testCase "WHY - a stack with no frame of yours says so in the site row" <| fun _ ->
        let report = ProfileReport.alloc Set.empty 5 [ { TypeName = "System.String"; Bytes = 100L; Frames = [ runtimeFrame 1 ] } ]
        (report.BySite |> List.head |> fun r -> r.Label) |> Expect.stringContains "named, not hidden" "no frame of yours"
    ]

    testList "bounds" [
      testPropertyWithConfig config
        "WHY - a rendered report never runs past its bound by more than the one line that says lines were left out"
        <| fun (seed: int) (limit: byte) ->
          let maxChars = 200 + int limit * 4
          let report = ProfileReport.cpu Set.empty 15 (samplesOfSeed seed)
          let text = ProfileReport.renderCpu { Top = 15; MaxChars = maxChars } report
          let lastLine = text.Split('\n') |> Array.last
          let body = text.Substring(0, text.Length - lastLine.Length)
          match lastLine.Contains "left out" with
          | true -> (body.Length <= maxChars) |> Expect.isTrue "the kept lines fit"
          | false -> (text.Length <= maxChars) |> Expect.isTrue "an uncut report fits"

      testCase "WHY - the default bounds keep a full report under the reply ceiling" <| fun _ ->
        let report = ProfileReport.cpu Set.empty ProfileReport.defaultBounds.Top (samplesOfSeed 3)
        (ProfileReport.renderCpu ProfileReport.defaultBounds report).Length
        |> fun n -> (n <= ProfileReport.defaultBounds.MaxChars + 100) |> Expect.isTrue "bounded"
    ]

    testList "mode and refusals" [
      testCase "WHY - every mode reads back from its wire spelling" <| fun _ ->
        for mode in ProfileMode.all do
          ProfileMode.parse (ProfileMode.toWire mode) |> Expect.equal "round trip" (Result.Ok mode)
      testCase "WHY - an empty mode means cpu and an unknown one is refused by name" <| fun _ ->
        ProfileMode.parse "" |> Expect.equal "default" (Result.Ok ProfileMode.Cpu)
        ProfileMode.parse "memory" |> Expect.equal "refused" (Result.Error(ProfileFailure.UnknownMode "memory"))
      testCase "WHY - a missing tool is refused with the command that installs it" <| fun _ ->
        ProfileFailure.describe (ProfileFailure.ToolMissing("dotnet-trace", "dotnet tool install -g dotnet-trace"))
        |> Expect.stringContains "the install command" "dotnet tool install -g dotnet-trace"
      testCase "WHY - every refusal says what to do next" <| fun _ ->
        let all =
          [ ProfileFailure.UnknownMode "x"
            ProfileFailure.NotRecordable "in-process"
            ProfileFailure.ToolMissing("t", "install t")
            ProfileFailure.CouldNotAttach "d"
            ProfileFailure.HostGone "d"
            ProfileFailure.StopTimedOut SageFs.Timeouts.profileRecorderWait
            ProfileFailure.TraceUnreadable "d"
            ProfileFailure.NoSamples
            ProfileFailure.TraceTooLarge(10L, 5L) ]
        for failure in all do
          let text = ProfileFailure.describe failure
          (text.StartsWith "profile: " && text.EndsWith ".") |> Expect.isTrue (sprintf "%A reads as a sentence that starts with the tool" failure)
          (text.Length > 40) |> Expect.isTrue "and says more than the name"
    ]
  ]
