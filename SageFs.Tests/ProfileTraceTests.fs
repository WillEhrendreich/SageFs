module SageFs.Tests.ProfileTraceTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features

/// Reading the Speedscope file `dotnet-trace convert` writes, and the command line that makes it. The fixture has the shape of a
/// real file written for an FSI host on 2026-11-10: the process and thread frames, a managed stack that ends in `CPU_TIME`, and a
/// thread in native code that ends in `UNMANAGED_CODE_TIME`.

let private frames =
  [ "Process64 dotnet (1470156) Args: FsiHost.dll"       // 0
    "(Non-Activities)"                                   // 1
    "Threads"                                            // 2
    "Thread (1470156)"                                   // 3
    "FsiHost!SageFs.FsiHost.Program.main(class System.String[])" // 4
    "FSI-ASSEMBLY!FSI_0008.hotFib(int32)"               // 5
    "CPU_TIME"                                           // 6
    "UNMANAGED_CODE_TIME"                                // 7
    "System.Private.CoreLib!System.String.Concat(class System.String,class System.String)" // 8
    "BLOCKED_TIME" ]                                     // 9

let private speedscope (events: (string * int * float) list) : string =
  let frameJson = frames |> List.map (fun n -> sprintf "{ \"name\": \"%s\" }" n) |> String.concat ", "
  let eventJson = events |> List.map (fun (t, f, at) -> sprintf "{ \"type\": \"%s\", \"frame\": %d, \"at\": %s }" t f (at.ToString("R", Globalization.CultureInfo.InvariantCulture))) |> String.concat ", "
  sprintf
    "{ \"exporter\": \"Microsoft.Diagnostics.Tracing.TraceEvent@3.1.23.0\", \"shared\": { \"frames\": [ %s ] }, \"profiles\": [ { \"type\": \"evented\", \"name\": \"Thread (1470156)\", \"unit\": \"milliseconds\", \"startValue\": 0, \"endValue\": 540, \"events\": [ %s ] } ] }"
    frameJson
    eventJson

let private opens (at: float) (ids: int list) = ids |> List.map (fun i -> "O", i, at)
let private closes (at: float) (ids: int list) = ids |> List.map (fun i -> "C", i, at)

/// 30 ms in hotFib on managed CPU, 10 ms in String.Concat on managed CPU, 500 ms in native code.
let private goodEvents =
  opens 0.0 [ 0; 1; 2; 3; 4; 5; 6 ]
  @ closes 30.0 [ 6; 5 ]
  @ opens 30.0 [ 8; 6 ]
  @ closes 40.0 [ 6; 8 ]
  @ opens 40.0 [ 7 ]
  @ closes 540.0 [ 7; 4; 3; 2; 1; 0 ]

let private cpuOf (events: (string * int * float) list) =
  SpeedscopeTrace.cpuReport Set.empty 10 (speedscope events)

[<Tests>]
let traceTests =
  testList "Profile trace" [

    testList "frame names" [
      testCase "WHY - a code frame splits at the bang and loses its parameter list" <| fun _ ->
        SpeedscopeTrace.classify "FSI-ASSEMBLY!FSI_0008.hotFib(int32)"
        |> Expect.equal "module and method" (SpeedscopeTrace.FrameEntry.Code { Module = "FSI-ASSEMBLY"; Method = "FSI_0008.hotFib" })
      testCase "WHY - a generic method keeps its type arguments and loses its parameters" <| fun _ ->
        SpeedscopeTrace.classify "System.Private.CoreLib!System.Collections.Generic.List`1[System.__Canon].Add(!0)"
        |> Expect.equal "name" (SpeedscopeTrace.FrameEntry.Code { Module = "System.Private.CoreLib"; Method = "System.Collections.Generic.List`1[System.__Canon].Add" })
      testCase "WHY - the process, thread and activity frames are structure, not code" <| fun _ ->
        [ "Process64 dotnet (1) Args: x"; "(Non-Activities)"; "Threads"; "Thread (7)" ]
        |> List.map SpeedscopeTrace.classify
        |> List.distinct
        |> Expect.equal "all structure" [ SpeedscopeTrace.FrameEntry.Structure ]
      testCase "WHY - the three marker frames say what the thread was doing" <| fun _ ->
        [ "CPU_TIME"; "UNMANAGED_CODE_TIME"; "BLOCKED_TIME" ]
        |> List.map SpeedscopeTrace.classify
        |> Expect.equal
          "markers"
          [ SpeedscopeTrace.FrameEntry.Marker SampleKind.ManagedCpu
            SpeedscopeTrace.FrameEntry.Marker SampleKind.NativeCode
            SpeedscopeTrace.FrameEntry.Marker SampleKind.Blocked ]
    ]

    testList "samples" [
      testCase "WHY - time between events goes to the stack that was open, and the kinds are told apart" <| fun _ ->
        match SpeedscopeTrace.parse (speedscope goodEvents) with
        | Result.Error failure -> failtestf "should read: %A" failure
        | Result.Ok samples ->
          let ms kind = samples |> List.filter (fun s -> s.Kind = kind) |> List.sumBy (fun s -> s.Ms)
          ms SampleKind.ManagedCpu |> Expect.equal "40 ms of managed CPU" 40.0
          ms SampleKind.NativeCode |> Expect.equal "500 ms in native code" 500.0

      testCase "WHY - the CPU report of the fixture names the hot method first with its share" <| fun _ ->
        match cpuOf goodEvents with
        | Result.Error failure -> failtestf "should report: %A" failure
        | Result.Ok report ->
          let first = report.SelfRows |> List.head
          first.Label |> Expect.equal "hotFib is first" "FSI-ASSEMBLY!FSI_0008.hotFib"
          first.Percent |> Expect.equal "30 of 40 ms" 75.0
          (report.UserRows |> List.map (fun r -> r.Label)) |> Expect.equal "and it is the user's frame" [ "FSI-ASSEMBLY!FSI_0008.hotFib" ]
          report.NativeMs |> Expect.equal "the idle time is counted apart" 500.0

      testCase "WHY - samples with the same stack are one sample" <| fun _ ->
        let twice =
          opens 0.0 [ 3; 5; 6 ] @ closes 10.0 [ 6 ] @ opens 10.0 [ 6 ] @ closes 20.0 [ 6; 5; 3 ]
        match SpeedscopeTrace.parse (speedscope twice) with
        | Result.Ok [ one ] -> one.Ms |> Expect.equal "10 and 10" 20.0
        | other -> failtestf "expected one merged sample, got %A" other

      testCase "WHY - a blocked leaf is blocked time and is not managed CPU" <| fun _ ->
        let blocked = opens 0.0 [ 3; 4; 9 ] @ closes 50.0 [ 9; 4; 3 ]
        match SpeedscopeTrace.parse (speedscope blocked) with
        | Result.Ok [ s ] -> s.Kind |> Expect.equal "blocked" SampleKind.Blocked
        | other -> failtestf "got %A" other
    ]

    testList "named failures" [
      testCase "WHY - a trace with no managed CPU is NoSamples, never an empty table" <| fun _ ->
        let idle = opens 0.0 [ 3; 4; 7 ] @ closes 500.0 [ 7; 4; 3 ]
        cpuOf idle |> Expect.equal "refused by name" (Result.Error ProfileFailure.NoSamples)

      testCase "WHY - text that is not JSON is TraceUnreadable" <| fun _ ->
        match SpeedscopeTrace.parse "this is not a trace" with
        | Result.Error(ProfileFailure.TraceUnreadable _) -> ()
        | other -> failtestf "got %A" other

      testCase "WHY - JSON of another shape is TraceUnreadable and not an exception" <| fun _ ->
        match SpeedscopeTrace.parse "{ \"hello\": 1 }" with
        | Result.Error(ProfileFailure.TraceUnreadable _) -> ()
        | other -> failtestf "got %A" other

      testCase "WHY - an event that closes a frame that is not the innermost is refused" <| fun _ ->
        let crossed = opens 0.0 [ 3; 4; 5 ] @ closes 10.0 [ 4 ]
        match SpeedscopeTrace.parse (speedscope crossed) with
        | Result.Error(ProfileFailure.TraceUnreadable detail) -> detail |> Expect.stringContains "says which frame" "innermost"
        | other -> failtestf "got %A" other

      testCase "WHY - an event that names a frame the file does not list is refused" <| fun _ ->
        match SpeedscopeTrace.parse (speedscope (opens 0.0 [ 3; 99 ])) with
        | Result.Error(ProfileFailure.TraceUnreadable _) -> ()
        | other -> failtestf "got %A" other

      testCase "WHY - a trace that stops mid-record still reads what was complete and says nothing false" <| fun _ ->
        // Frames opened and never closed: the open stack gets no time after its last event, so only the closed span counts.
        let truncated = opens 0.0 [ 3; 5; 6 ] @ closes 20.0 [ 6; 5 ] @ opens 20.0 [ 8; 6 ]
        match SpeedscopeTrace.parse (speedscope truncated) with
        | Result.Ok samples -> (samples |> List.sumBy (fun s -> s.Ms)) |> Expect.equal "20 ms were complete" 20.0
        | other -> failtestf "got %A" other
    ]

    testList "the recorder command" [
      testCase "WHY - the cpu command attaches by pid, asks for Speedscope and names its provider set" <| fun _ ->
        let args = DotnetTrace.arguments { HostPid = 4242; Mode = ProfileMode.Cpu; TraceBase = "/tmp/t" }
        args |> Expect.contains "attach by pid" "4242"
        args |> Expect.contains "convert to Speedscope" "Speedscope"
        (args |> List.skipWhile (fun a -> a <> "--providers") |> List.item 1) |> Expect.equal "providers" DotnetTrace.providers
        args |> List.last |> Expect.equal "output base last" "/tmp/t"

      testCase "WHY - the allocation command keeps the nettrace file and asks for the GC events at verbose level" <| fun _ ->
        let args = DotnetTrace.arguments { HostPid = 4242; Mode = ProfileMode.Alloc; TraceBase = "/tmp/t" }
        args |> Expect.contains "attach by pid" "4242"
        args |> Expect.contains "keep the nettrace file" "NetTrace"
        (args |> List.skipWhile (fun a -> a <> "--providers") |> List.item 1) |> Expect.equal "providers" DotnetTrace.allocationProviders
        args |> List.last |> Expect.equal "the nettrace path last" "/tmp/t.nettrace"
        DotnetTrace.allocationProviders |> Expect.stringContains "GC keyword at verbose level" ":0x19:5"

      testCase "WHY - every file a recording may write is named, so the cleanup cannot miss one" <| fun _ ->
        DotnetTrace.filesOf { HostPid = 1; Mode = ProfileMode.Cpu; TraceBase = "/tmp/t" }
        |> Expect.equal "the three paths" [ "/tmp/t"; "/tmp/t.speedscope.json"; "/tmp/t.nettrace" ]

      testCase "WHY - the tool's output-open line is the attach signal and no other line is" <| fun _ ->
        DotnetTrace.isAttachedLine "Output File    : /tmp/x" |> Expect.isTrue "attached"
        DotnetTrace.isAttachedLine "Process        : /usr/bin/dotnet" |> Expect.isFalse "not yet"
        DotnetTrace.isAttachedLine null |> Expect.isFalse "a null line is not a signal"

      testCase "WHY - the tool is searched on PATH first and then in the dotnet global tools directory" <| fun _ ->
        let found =
          DotnetTrace.candidates { IsWindows = false; PathVariable = "/a:/b"; Home = "/home/x" }
        found |> Expect.equal "order" [ "/a/dotnet-trace"; "/b/dotnet-trace"; "/home/x/.dotnet/tools/dotnet-trace" ]

      testCase "WHY - on Windows the tool is the .exe" <| fun _ ->
        DotnetTrace.candidates { IsWindows = true; PathVariable = null; Home = "" } |> Expect.equal "no path, no home, no candidate" []
        (DotnetTrace.candidates { IsWindows = true; PathVariable = "C:\\t"; Home = "" } |> List.head) |> Expect.stringEnds "exe" "dotnet-trace.exe"

      testCase "WHY - what the tool said is bounded before it goes into a refusal" <| fun _ ->
        let long = [ for i in 1 .. 50 -> String('x', 200) ]
        (DotnetTrace.tailOf long).Length |> fun n -> (n <= 403) |> Expect.isTrue "bounded"
    ]
  ]
