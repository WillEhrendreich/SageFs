/// The edge of `SourceState`: real files, real write times. A throwaway project directory with a project file, two
/// sources (one in a sub directory, named with a Windows separator the way an .fsproj often is) and a build output, whose
/// write times each test sets, and a warmup context that says which assembly the worker loaded and when.
///
/// The claims: what the build was made from is read from the project file's Compile items; a file that cannot be
/// read, a project that cannot be listed, an assembly the worker did not load and a worker that did not report are each
/// Unknown with the reason, never a quiet InSync; and a file that does not exist is not a file written in 1601.
module SageFs.Tests.SourceStateProbeTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

let private t0 = DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
let private minutesAfter (m: int) = t0.AddMinutes(float m)

let private fsproj (compileItems: string list) (extra: string) =
  let items = compileItems |> List.map (fun c -> sprintf "    <Compile Include=\"%s\" />" c) |> String.concat "\n"
  sprintf "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>%s\n  </PropertyGroup>\n  <ItemGroup>\n%s\n  </ItemGroup>\n</Project>\n" extra items

/// A project on disk with every file written 60 minutes before t0, the build output 30 minutes before, and the worker
/// loaded 10 minutes before: in sync until a test moves something.
type private Fixture =
  { Dir: string
    Project: string
    A: string
    B: string
    Dll: string }

let private buildFixture (compileItems: string list) (extra: string) : Fixture =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sourcestate-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory(Path.Combine(dir, "sub")) |> ignore
  Directory.CreateDirectory(Path.Combine(dir, "bin")) |> ignore
  let project = Path.Combine(dir, "Lib.fsproj")
  let a = Path.Combine(dir, "A.fs")
  let b = Path.Combine(dir, "sub", "B.fs")
  let dll = Path.Combine(dir, "bin", "Lib.dll")
  File.WriteAllText(project, fsproj compileItems extra)
  File.WriteAllText(a, "module A\nlet x = 1\n")
  File.WriteAllText(b, "module B\nlet y = 2\n")
  File.WriteAllText(dll, "not really an assembly")
  for source in [ project; a; b ] do File.SetLastWriteTimeUtc(source, minutesAfter -60)
  File.SetLastWriteTimeUtc(dll, minutesAfter -30)
  { Dir = dir; Project = project; A = a; B = b; Dll = dll }

let private standard () = buildFixture [ "A.fs"; "sub\\B.fs" ] ""

let private cleanUp (f: Fixture) =
  try
    // a test may have taken the execute bit off a directory
    for d in Directory.GetDirectories f.Dir do
      try File.SetUnixFileMode(d, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) with _ -> ()
    Directory.Delete(f.Dir, true)
  with _ -> ()

let private warmupFor (f: Fixture) (assemblyName: string) (startedMinutes: int) : WarmupContext option =
  Some
    { WarmupContext.empty with
        StartedAt = DateTimeOffset(minutesAfter startedMinutes)
        AssembliesLoaded = [ { Name = assemblyName; Path = f.Dll; NamespaceCount = 0; ModuleCount = 0 } ] }

let private probeIn (f: Fixture) (rebuild: LastRebuild) (warmup: WarmupContext option) : SourceState =
  SourceStateProbe.probe rebuild [ f.Project ] warmup

let private withFixture (make: unit -> Fixture) (body: Fixture -> unit) : unit =
  let f = make ()
  try body f finally cleanUp f

[<Tests>]
let probeTests =
  testList "SourceStateProbe" [

    testCase "WHY — files all older than the build, and the build older than the worker's load, are in sync, and the project file counts among what is checked" <| fun _ ->
      withFixture standard (fun f ->
        probeIn f LastRebuild.NeverRebuilt (warmupFor f "Lib" -10)
        |> Expect.equal "in sync: the oldest build, three files" (SourceState.InSync (minutesAfter -30, 3)))

    testCase "WHY — the Nehemiah case: a source edited after the build, with no rebuild, is Stale and named by its full path" <| fun _ ->
      withFixture standard (fun f ->
        File.SetLastWriteTimeUtc(f.B, minutesAfter -5)
        probeIn f LastRebuild.NeverRebuilt (warmupFor f "Lib" -10)
        |> Expect.equal "stale, naming the edited file" (SourceState.Stale [ { Path = f.B; Because = StaleBecause.EditedAfterBuild (minutesAfter -5, minutesAfter -30) } ]))

    testCase "WHY — a project file edited after the build is a changed input too" <| fun _ ->
      withFixture standard (fun f ->
        File.SetLastWriteTimeUtc(f.Project, minutesAfter -2)
        match probeIn f LastRebuild.NeverRebuilt (warmupFor f "Lib" -10) with
        | SourceState.Stale [ file ] -> file.Path |> Expect.equal "the project file" f.Project
        | other -> failtestf "expected Stale naming the project file, got %A" other)

    testCase "WHY — a build on disk newer than the one the worker loaded means the session runs an older build" <| fun _ ->
      withFixture standard (fun f ->
        File.SetLastWriteTimeUtc(f.Dll, minutesAfter -5)
        probeIn f LastRebuild.NeverRebuilt (warmupFor f "Lib" -10)
        |> Expect.equal "stale, naming the output" (SourceState.Stale [ { Path = f.Dll; Because = StaleBecause.RebuiltAfterLoad (minutesAfter -5, minutesAfter -10) } ]))

    testCase "WHY — a rebuild in progress is Rebuilding before any file is looked at" <| fun _ ->
      withFixture standard (fun f ->
        File.SetLastWriteTimeUtc(f.A, minutesAfter -1)
        probeIn f (LastRebuild.Latest (RebuildOutcome.InProgress (minutesAfter -2))) (warmupFor f "Lib" -10)
        |> Expect.equal "rebuilding" (SourceState.Rebuilding (minutesAfter -2)))

    testCase "WHY — a source listed in the project that is not on disk is Unknown and names the file, not a file written in the year 1601" <| fun _ ->
      withFixture standard (fun f ->
        File.Delete f.A
        match probeIn f LastRebuild.NeverRebuilt (warmupFor f "Lib" -10) with
        | SourceState.Unknown (UnknownReason.Unreadable (path, reason)) ->
          path |> Expect.equal "the missing file" f.A
          reason |> Expect.isNotEmpty "says why"
        | other -> failtestf "expected Unknown, got %A" other)

    testCase "WHY — a source in a directory that cannot be entered is Unknown with the operating system's reason" <| fun _ ->
      match OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess with
      | true -> skiptest "a directory's execute bit is a Unix permission, and a privileged process is not held back by it"
      | false -> ()
      withFixture standard (fun f ->
        File.SetUnixFileMode(Path.Combine(f.Dir, "sub"), UnixFileMode.None)
        match probeIn f LastRebuild.NeverRebuilt (warmupFor f "Lib" -10) with
        | SourceState.Unknown (UnknownReason.Unreadable (path, reason)) ->
          path |> Expect.equal "the file behind the closed directory" f.B
          reason |> Expect.isNotEmpty "says why"
        | other -> failtestf "expected Unknown, got %A" other)

    testCase "WHY — a worker that did not report its warmup leaves the answer Unknown" <| fun _ ->
      withFixture standard (fun f ->
        match probeIn f LastRebuild.NeverRebuilt None with
        | SourceState.Unknown (UnknownReason.LoadTimeNotReported reason) -> reason |> Expect.isNotEmpty "says why"
        | other -> failtestf "expected Unknown LoadTimeNotReported, got %A" other)

    testCase "WHY — a warmup that loaded no assembly for the project leaves the answer Unknown and names the project" <| fun _ ->
      withFixture standard (fun f ->
        match probeIn f LastRebuild.NeverRebuilt (warmupFor f "SomethingElse" -10) with
        | SourceState.Unknown (UnknownReason.NotInspectable (project, reason)) ->
          project |> Expect.equal "the project" f.Project
          reason |> Expect.stringContains "names the assembly it looked for" "Lib"
        | other -> failtestf "expected Unknown NotInspectable, got %A" other)

    testCase "WHY — an AssemblyName in the project file is the name the loaded assembly is matched by" <| fun _ ->
      withFixture (fun () -> buildFixture [ "A.fs"; "sub\\B.fs" ] "\n    <AssemblyName>Renamed</AssemblyName>") (fun f ->
        probeIn f LastRebuild.NeverRebuilt (warmupFor f "Renamed" -10)
        |> Expect.equal "matched by AssemblyName" (SourceState.InSync (minutesAfter -30, 3)))

    testCase "WHY — a project file that is not there is Unknown and names the project" <| fun _ ->
      withFixture standard (fun f ->
        File.Delete f.Project
        match probeIn f LastRebuild.NeverRebuilt (warmupFor f "Lib" -10) with
        | SourceState.Unknown (UnknownReason.NotInspectable (project, _)) -> project |> Expect.equal "the project" f.Project
        | other -> failtestf "expected Unknown NotInspectable, got %A" other)

    testCase "WHY — a wildcard Compile item cannot be listed, so it is Unknown rather than a guess at what the build contained" <| fun _ ->
      withFixture (fun () -> buildFixture [ "A.fs"; "**/*.fs" ] "") (fun f ->
        match probeIn f LastRebuild.NeverRebuilt (warmupFor f "Lib" -10) with
        | SourceState.Unknown (UnknownReason.NotInspectable (_, reason)) -> reason |> Expect.stringContains "says wildcard" "wildcard"
        | other -> failtestf "expected Unknown NotInspectable, got %A" other)

    testCase "WHY — a session with no project loaded has no build to be behind, and says so" <| fun _ ->
      SourceStateProbe.probe LastRebuild.NeverRebuilt [] None
      |> Expect.equal "no project loaded" (SourceState.Unknown UnknownReason.NoProjectLoaded)

    testCase "WHY — an edit after the build is stale on every project that has one, and a clean project beside it does not hide it" <| fun _ ->
      let first = standard ()
      let second = standard ()
      try
        File.SetLastWriteTimeUtc(second.A, minutesAfter -3)
        let warmup =
          Some
            { WarmupContext.empty with
                StartedAt = DateTimeOffset(minutesAfter -10)
                AssembliesLoaded =
                  [ { Name = "Lib"; Path = first.Dll; NamespaceCount = 0; ModuleCount = 0 } ] }
        // two projects with the same assembly name would collide: give the second its own name
        File.WriteAllText(second.Project, fsproj [ "A.fs"; "sub\\B.fs" ] "\n    <AssemblyName>Other</AssemblyName>")
        File.SetLastWriteTimeUtc(second.Project, minutesAfter -60)
        let warmupTwo =
          warmup |> Option.map (fun w -> { w with AssembliesLoaded = w.AssembliesLoaded @ [ { Name = "Other"; Path = second.Dll; NamespaceCount = 0; ModuleCount = 0 } ] })
        match SourceStateProbe.probe LastRebuild.NeverRebuilt [ first.Project; second.Project ] warmupTwo with
        | SourceState.Stale [ file ] -> file.Path |> Expect.equal "the edited file in the second project" second.A
        | other -> failtestf "expected Stale naming one file, got %A" other
      finally
        cleanUp first
        cleanUp second
  ]
