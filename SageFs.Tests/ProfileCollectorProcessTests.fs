module SageFs.Tests.ProfileCollectorProcessTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features

/// The real collector's process handling, against a stand-in for `dotnet-trace`: a shell script that prints what the tool prints,
/// stops on SIGTERM and writes what the tool writes. The real tool is proved by the outcome gate in `ProfileIntegrationTests`;
/// these cases hold the collector to what it does around any tool: when it counts the recorder as attached, how it stops it, what
/// it reads, and that it leaves no file behind. They run on Linux and macOS, where the collector runs.

let private onUnix = not (OperatingSystem.IsWindows())

let private fixtureJson = SageFs.Simulation.ProfileRecorderSim.goodTrace

/// Write an executable script named like the tool and return its path.
let private scriptWith (body: string) : string =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-profile-fake", Guid.NewGuid().ToString "N")
  Directory.CreateDirectory dir |> ignore
  let path = Path.Combine(dir, "dotnet-trace")
  let header = "#!/bin/sh\nbase=\"\"\nprev=\"\"\nfor a in \"$@\"; do\n  if [ \"$prev\" = \"-o\" ]; then base=\"$a\"; fi\n  prev=\"$a\"\ndone\n"
  File.WriteAllText(path, header + body)
  File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
  path

/// A stand-in that attaches, then waits for SIGTERM and writes `fixtureJson` the way the tool converts a trace.
let private wellBehaved () : string =
  scriptWith (
    ": > \"$base\"\necho \"Process        : /usr/bin/dotnet\"\necho \"Output File    : $base\"\n"
    + "stop() {\n  cat > \"$base.speedscope.json\" <<'EOF'\n" + fixtureJson + "\nEOF\n  kill $napper 2>/dev/null\n  exit 0\n}\n"
    + "trap stop TERM\nsleep 300 &\nnapper=$!\nwait $napper\n")

let private requestIn (name: string) : RecordingRequest =
  { HostPid = Environment.ProcessId; Mode = ProfileMode.Cpu; TraceBase = Path.Combine(Path.GetTempPath(), sprintf "sagefs-profile-test-%s-%s" name (Guid.NewGuid().ToString "N")) }

let private filesOf (request: RecordingRequest) : string list =
  DotnetTrace.filesOf request |> List.filter File.Exists

/// A stand-in that attaches by making the nettrace file the allocation command names, then writes a few bytes into it when stopped.
let private nettraceMaker () : string =
  scriptWith (
    ": > \"$base\"\necho \"Output File    : $base\"\n"
    + "stop() {\n  printf 'Nettrace-stand-in' > \"$base\"\n  kill $napper 2>/dev/null\n  exit 0\n}\n"
    + "trap stop TERM\nsleep 300 &\nnapper=$!\nwait $napper\n")

let private whenUnix (body: unit -> unit) : unit =
  match onUnix with
  | true -> body ()
  | false -> ()

[<Tests>]
let tests =
  testList "Profile collector against a stand-in tool" [

    testAsync "WHY - a recorder that opens its output is attached, a stop reads what it wrote, and no file is left" {
      match onUnix with
      | false -> ()
      | true ->
        let request = requestIn "ok"
        match! DotnetTraceCollector.beginAt (wellBehaved ()) request with
        | Result.Error failure -> failtestf "should attach: %s" (ProfileFailure.describe failure)
        | Result.Ok active ->
          // The stand-in made the trace file when it attached, which is when the real tool does.
          filesOf request |> Expect.contains "the recorder's file exists while it records" request.TraceBase
          match! active.Finish() with
          | Result.Ok(RecordedTrace.Speedscope text) -> text |> Expect.stringContains "the converted trace is what is read" "FSI_0008.hotFib"
          | other -> failtestf "should read the Speedscope text: %A" other
          filesOf request |> Expect.isEmpty "nothing is left behind"
    }

    testAsync "WHY - an allocation recording reads the nettrace file its command names, and no file is left" {
      match onUnix with
      | false -> ()
      | true ->
        // The stand-in writes the file the argument after -o names, which for an allocation request is `<base>.nettrace`.
        let request = { requestIn "alloc" with Mode = ProfileMode.Alloc }
        match! DotnetTraceCollector.beginAt (nettraceMaker ()) request with
        | Result.Error failure -> failtestf "should attach: %s" (ProfileFailure.describe failure)
        | Result.Ok active ->
          filesOf request |> Expect.contains "the nettrace file exists while it records" (DotnetTrace.nettracePathOf request.TraceBase)
          match! active.Finish() with
          | Result.Ok(RecordedTrace.NetTrace bytes) -> bytes.Length |> Expect.equal "the bytes the stand-in wrote" 17
          | other -> failtestf "should read the nettrace bytes: %A" other
          filesOf request |> Expect.isEmpty "nothing is left behind"
    }

    testAsync "WHY - a recorder that ends before it opens its output is CouldNotAttach with what it said" {
      match onUnix with
      | false -> ()
      | true ->
        let request = requestIn "noattach"
        let path = scriptWith "echo \"Unable to connect to the process\"\nexit 3\n"
        match! DotnetTraceCollector.beginAt path request with
        | Result.Error(ProfileFailure.CouldNotAttach detail) -> detail |> Expect.stringContains "its words are in the refusal" "Unable to connect"
        | other -> failtestf "got %A" other
        filesOf request |> Expect.isEmpty "nothing is left behind"
    }

    testAsync "WHY - a recorder that ended on its own means the process it recorded ended: HostGone, and nothing is left" {
      match onUnix with
      | false -> ()
      | true ->
        let request = requestIn "hostgone"
        let path = scriptWith ": > \"$base\"\necho \"Output File    : $base\"\nexit 0\n"
        match DotnetTraceCollector.start path request with
        | Result.Error failure -> failtestf "should start: %s" (ProfileFailure.describe failure)
        | Result.Ok running ->
          let! _ = running.Exited |> Async.AwaitTask
          match! DotnetTraceCollector.finish running request with
          | Result.Error(ProfileFailure.HostGone _) -> ()
          | other -> failtestf "got %A" other
        filesOf request |> Expect.isEmpty "nothing is left behind"
    }

    testAsync "WHY - a stop that leaves no Speedscope file is TraceUnreadable and says what the recorder said" {
      match onUnix with
      | false -> ()
      | true ->
        let request = requestIn "nofile"
        let path =
          scriptWith
            ": > \"$base\"\necho \"Output File    : $base\"\nstop() {\n  kill $napper 2>/dev/null\n  exit 0\n}\ntrap stop TERM\nsleep 300 &\nnapper=$!\nwait $napper\n"
        match! DotnetTraceCollector.beginAt path request with
        | Result.Error failure -> failtestf "should attach: %s" (ProfileFailure.describe failure)
        | Result.Ok active ->
          match! active.Finish() with
          | Result.Error(ProfileFailure.TraceUnreadable detail) -> detail |> Expect.stringContains "names what is missing" "no Speedscope file"
          | other -> failtestf "got %A" other
        filesOf request |> Expect.isEmpty "nothing is left behind, including the recorder's own file"
    }

    testCase "WHY - the tool is found where candidates says, and a path that is not a file is not found" <| fun _ ->
      whenUnix (fun () ->
        let path = wellBehaved ()
        let dir = Path.GetDirectoryName path
        DotnetTrace.candidates { IsWindows = false; PathVariable = dir; Home = "" }
        |> List.tryFind File.Exists
        |> Expect.equal "found on PATH" (Some path)
        DotnetTrace.candidates { IsWindows = false; PathVariable = "/nonexistent-dir"; Home = "" }
        |> List.tryFind File.Exists
        |> Expect.isNone "an empty search finds nothing")
  ]
