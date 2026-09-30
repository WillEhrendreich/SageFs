/// A hot-reload patch re-points a function, and a caller the JIT inlined it into
/// keeps running the old body. SageFs builds sessions unoptimized to prevent that;
/// an assembly someone built by hand, optimized, brings it back and nothing said so.
/// These pin that the daemon can tell from the assembly itself, and says so in the
/// session's health.
module SageFs.Tests.BuildOptimizationTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Reflection.Emit
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.ProjectLoading

type private Modes = DebuggableAttribute.DebuggingModes

/// Emit a real assembly file, stamped the way a build stamps it (or not at all).
let private emitWith (attribute: (ConstructorInfo * obj[]) option) : string =
  let dir = Directory.CreateTempSubdirectory("sagefs-buildopt-").FullName
  let path = Path.Combine(dir, "Probe.dll")
  let name = AssemblyName "Probe"
  let builder = PersistedAssemblyBuilder(name, typeof<obj>.Assembly)
  match attribute with
  | Some (ctor, args) -> builder.SetCustomAttribute(CustomAttributeBuilder(ctor, args))
  | None -> ()
  let moduleBuilder = builder.DefineDynamicModule "Probe"
  moduleBuilder.DefineType("T", TypeAttributes.Public).CreateType() |> ignore
  builder.Save path
  path

let private byModes (modes: Modes) : string =
  emitWith (Some (typeof<DebuggableAttribute>.GetConstructor [| typeof<Modes> |], [| box modes |]))

let private byFlags (tracking: bool) (optimizerDisabled: bool) : string =
  emitWith (Some (typeof<DebuggableAttribute>.GetConstructor [| typeof<bool>; typeof<bool> |], [| box tracking; box optimizerDisabled |]))

/// Probe, then remove the temp directory, so a test leaves nothing behind.
let private classified (path: string) : BuildOptimization =
  try BuildOptimization.ofAssemblyFile path
  finally
    match File.Exists path with
    | true -> Directory.Delete(Path.GetDirectoryName path, true)
    | false -> ()

let private isUnknown (build: BuildOptimization) : bool =
  match build with
  | BuildOptimization.Unknown _ -> true
  | BuildOptimization.Unoptimized | BuildOptimization.Optimized -> false

let private handle : WorkerHandle = { Pid = 4242; Port = Some 5000 }

let private projectBuilt (build: BuildOptimization) (name: string) : ClassifiedProject =
  { Path = sprintf "/repo/%s/%s.fsproj" name name
    Role = ProjectRole.Executable
    PackageRefs = []
    LoadMode = LoadMode.Evaluated
    Build = build }

let private health (projects: ClassifiedProject list) : SessionHealth =
  SessionHealth.classify (SessionLifecycleStatus.Ready handle) projects None

[<Tests>]
let tests =
  testList "Build optimization of a project's assembly" [
    testCase "WHY — an assembly built with the optimizer disabled is Unoptimized, because that is the build a patch reaches" <| fun _ ->
      byModes (Modes.DisableOptimizations ||| Modes.Default)
      |> classified
      |> Expect.equal "DisableOptimizations set" BuildOptimization.Unoptimized

    testCase "WHY — an assembly whose modes lack DisableOptimizations is Optimized, because the JIT may inline into its callers" <| fun _ ->
      byModes Modes.IgnoreSymbolStoreSequencePoints
      |> classified
      |> Expect.equal "the Release-style stamp" BuildOptimization.Optimized

    testCase "WHY — an assembly with no DebuggableAttribute at all is Optimized, since that is the compiler's default" <| fun _ ->
      emitWith None
      |> classified
      |> Expect.equal "no attribute" BuildOptimization.Optimized

    testCase "WHY — the two-bool DebuggableAttribute shape is read too, so an older stamp is not misjudged" <| fun _ ->
      byFlags true true |> classified |> Expect.equal "optimizer disabled" BuildOptimization.Unoptimized
      byFlags true false |> classified |> Expect.equal "optimizer enabled" BuildOptimization.Optimized

    testCase "WHY — a file that is not there is Unknown and says so, never silently called optimized" <| fun _ ->
      BuildOptimization.ofAssemblyFile (Path.Combine(Path.GetTempPath(), "sagefs-no-such-dir", "Missing.dll"))
      |> isUnknown
      |> Expect.isTrue "missing output is Unknown"

    testCase "WHY — a file that is not an assembly is Unknown, and the probe does not throw" <| fun _ ->
      let path = Path.Combine(Directory.CreateTempSubdirectory("sagefs-buildopt-").FullName, "notes.txt")
      File.WriteAllText(path, "not a dll")
      try
        BuildOptimization.ofAssemblyFile path
        |> isUnknown
        |> Expect.isTrue "an unreadable file is Unknown"
      finally
        Directory.Delete(Path.GetDirectoryName path, true)

    testCase "WHY — the framework's own assemblies read as Optimized, so the probe agrees with a build nobody stamped" <| fun _ ->
      BuildOptimization.ofAssemblyFile typeof<obj>.Assembly.Location
      |> Expect.equal "System.Private.CoreLib is a Release build" BuildOptimization.Optimized
  ]

[<Tests>]
let healthTests =
  testList "Session health when a project's build is optimized" [
    testCase "WHY — an optimized project makes a Ready session Degraded and names the project, because a patch to it can be bypassed" <| fun _ ->
      match health [ projectBuilt BuildOptimization.Optimized "MyApp" ] with
      | SessionHealth.Degraded reason -> reason |> Expect.stringContains "the project is named" "MyApp.fsproj"
      | other -> failtestf "expected Degraded, got %A" other

    testCase "WHY — an unoptimized project leaves the session Healthy, so the warning is not noise on the normal build" <| fun _ ->
      health [ projectBuilt BuildOptimization.Unoptimized "MyApp" ]
      |> Expect.equal "nothing to warn about" SessionHealth.Healthy

    testCase "WHY — a project whose build could not be read is not a warning by itself, because a missing build already has its own verdict" <| fun _ ->
      health [ projectBuilt (BuildOptimization.Unknown "no build output") "MyApp" ]
      |> Expect.equal "Unknown is silent" SessionHealth.Healthy

    testCase "WHY — only the optimized project is named when a solution mixes builds" <| fun _ ->
      match health [ projectBuilt BuildOptimization.Unoptimized "Lib"; projectBuilt BuildOptimization.Optimized "Web" ] with
      | SessionHealth.Degraded reason ->
        reason |> Expect.stringContains "the optimized one is named" "Web.fsproj"
        reason.Contains "Lib.fsproj" |> Expect.isFalse "the unoptimized one is not"
      | other -> failtestf "expected Degraded, got %A" other

    testCase "WHY — the warning says what to do, so the reader is not left to work out the fix" <| fun _ ->
      BuildOptimization.warning "MyApp.fsproj" BuildOptimization.Optimized
      |> Option.map (fun text -> text.Contains "rebuild=true")
      |> Expect.equal "the remedy is in the words" (Some true)
  ]
