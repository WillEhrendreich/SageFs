/// A project on disk for tests that read a session's source state: a project file, one source and a build output, with write
/// times a test sets, and the warmup report a worker would give for it. Real files, because the claim under test is about what
/// is on disk.
module SageFs.Tests.SourceStateFixtures

open System
open System.IO
open System.Threading.Tasks
open SageFs

type Project =
  { Dir: string
    ProjectFile: string
    Source: string
    Dll: string }

let t0 = DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
let at (minutes: int) : DateTime = t0.AddMinutes(float minutes)

/// The assembly name every fixture project builds.
let assemblyName = "Lib"

/// A project whose files were all written 60 minutes before `t0`, whose build output was written 30 minutes before, and whose
/// worker loaded (see `warmup`) 10 minutes before: in sync until a test writes to something.
let create () : Project =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "source-state-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory(Path.Combine(dir, "bin")) |> ignore
  let projectFile = Path.Combine(dir, sprintf "%s.fsproj" assemblyName)
  let source = Path.Combine(dir, "A.fs")
  let dll = Path.Combine(dir, "bin", sprintf "%s.dll" assemblyName)
  File.WriteAllText(projectFile, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"A.fs\" /></ItemGroup></Project>")
  File.WriteAllText(source, "module A")
  File.WriteAllText(dll, "assembly")
  for file in [ projectFile; source ] do File.SetLastWriteTimeUtc(file, at -60)
  File.SetLastWriteTimeUtc(dll, at -30)
  { Dir = dir; ProjectFile = projectFile; Source = source; Dll = dll }

let drop (project: Project) : unit =
  try Directory.Delete(project.Dir, true) with _ -> ()

/// The source is written after the build: the project is now stale.
let editSource (project: Project) : unit = File.SetLastWriteTimeUtc(project.Source, at -2)

/// The project as the session registry lists it.
let classified (project: Project) : ProjectLoading.ClassifiedProject =
  { Path = project.ProjectFile
    Role = ProjectLoading.ProjectRole.Library
    PackageRefs = []
    LoadMode = ProjectLoading.LoadMode.Evaluated
    Build = BuildOptimization.Unoptimized }

/// What the worker reports for it: it loaded the build 10 minutes before `t0`.
let warmup (project: Project) : WarmupContext =
  { WarmupContext.empty with
      StartedAt = DateTimeOffset(at -10)
      AssembliesLoaded = [ { Name = assemblyName; Path = project.Dll; NamespaceCount = 0; ModuleCount = 0 } ] }

/// A body over a fresh project, and the project cleaned up whatever the body did. The outcome is observed through a
/// continuation, because an `await` inside a try/finally in a computation expression is the shape a Release build has
/// miscompiled here before.
let using (body: Project -> Task<unit>) : Task<unit> =
  task {
    let project = create ()
    let! outcome =
      (body project).ContinueWith(fun (t: Task<unit>) ->
        match t.IsFaulted with
        | true -> Error (t.Exception :> exn)
        | false -> Ok ())
    drop project
    match outcome with
    | Error e -> raise e
    | Ok () -> ()
  }
