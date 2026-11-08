module SageFs.Tests.CoreSurfaceTests

open System
open System.IO
open Expecto
open Expecto.Flip
open Mono.Cecil
open Mono.Cecil.Cil
open SageFs

// A worktree whose SageFs.Core differs from the daemon's has the same assembly version, because the version moves on a
// release push and not on a commit. The worker the daemon spawns for such a session used to be handed that Core next to
// the daemon's own SageFs.Host.dll, which was compiled against a different shape of it. A union whose cases moved
// has new tags, and the host's `match` on the old tags cast a message to the wrong case: every route answered 500 and
// the worker log said nothing. These tests hold the check that keeps the two from being paired.

let private withTempDir (run: string -> 'a) : 'a =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-core-surface-test-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally
    try Directory.Delete(dir, true) with _ -> ()

/// The real SageFs.Core this test process runs, as a file.
let private realCorePath = typeof<SageFs.SageFsError>.Assembly.Location

let private writeModule (path: string) (module_: ModuleDefinition) =
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  use stream = File.Create path
  module_.Write stream
  module_.Dispose()

/// A small library with a class `Fixture.Outer` whose nested public classes are `cases`, in that order (the shape of an
/// F# union: one nested class per case, tags by position), and one public static method per name in `publicMethods` and
/// one non-public static method per name in `hiddenMethods`.
let private writeSurfaceFixture (path: string) (cases: string list) (publicMethods: string list) (hiddenMethods: string list) =
  let module_ = ModuleDefinition.CreateModule("SurfaceFixture", ModuleKind.Dll)
  let classAttrs = TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.BeforeFieldInit
  let outer = TypeDefinition("Fixture", "Outer", classAttrs, module_.TypeSystem.Object)
  module_.Types.Add outer
  for name in cases do
    outer.NestedTypes.Add(TypeDefinition("", name, TypeAttributes.NestedPublic ||| TypeAttributes.Class ||| TypeAttributes.BeforeFieldInit, module_.TypeSystem.Object))
  let addMethod (name: string) (visibility: MethodAttributes) =
    let m = MethodDefinition(name, visibility ||| MethodAttributes.Static ||| MethodAttributes.HideBySig, module_.TypeSystem.Void)
    m.Body.GetILProcessor().Emit(OpCodes.Ret)
    outer.Methods.Add m
  for name in publicMethods do addMethod name MethodAttributes.Public
  for name in hiddenMethods do addMethod name MethodAttributes.Assembly
  writeModule path module_

let private surfaceOfFixture (cases: string list) (publicMethods: string list) (hiddenMethods: string list) : CoreSurface.Surface =
  withTempDir (fun dir ->
    let path = Path.Combine(dir, "SurfaceFixture.dll")
    writeSurfaceFixture path cases publicMethods hiddenMethods
    match CoreSurface.surfaceOfFile path with
    | CoreSurface.SurfaceReading.Read read -> read
    | CoreSurface.SurfaceReading.Unreadable reason -> failtestf "the fixture could not be read: %s" reason)

/// A copy of the real SageFs.Core with two adjacent cases of `WorkerMessage` swapped: the shape of the worktree that broke.
let private writeCoreWithSwappedCases (dest: string) =
  let module_ = ModuleDefinition.ReadModule realCorePath
  let message = module_.GetType "SageFs.WorkerProtocol/WorkerMessage"
  let nested = message.NestedTypes
  let first = nested |> Seq.find (fun t -> t.Name = "LevelRepl")
  let second = nested |> Seq.find (fun t -> t.Name = "GetStatus")
  let low = min (nested.IndexOf first) (nested.IndexOf second)
  let high = max (nested.IndexOf first) (nested.IndexOf second)
  let atLow = nested.[low]
  let atHigh = nested.[high]
  nested.RemoveAt high
  nested.RemoveAt low
  nested.Insert(low, atHigh)
  nested.Insert(high, atLow)
  writeModule dest module_

/// A copy of the real SageFs.Core read and written back with nothing changed: different bytes, the same surface.
let private writeCoreUnchanged (dest: string) =
  writeModule dest (ModuleDefinition.ReadModule realCorePath)

[<Tests>]
let coreSurfaceTests =
  testList "CoreSurface" [

    testCase "WHY — the surface of a real build names its exported types, and reading it twice gives the same answer" <| fun _ ->
      match CoreSurface.surfaceOfFile realCorePath, CoreSurface.surfaceOfFile realCorePath with
      | CoreSurface.SurfaceReading.Read first, CoreSurface.SurfaceReading.Read second ->
        first |> Expect.equal "the same file gives the same surface" second
        first |> Map.containsKey "SageFs.WorkerProtocol+WorkerMessage" |> Expect.isTrue "the wire's message type is in it"
        (first.Count > 100) |> Expect.isTrue "a build of the Core has hundreds of exported types"
      | CoreSurface.SurfaceReading.Unreadable reason, _
      | _, CoreSurface.SurfaceReading.Unreadable reason -> failtestf "the Core could not be read: %s" reason

    testCase "WHY — the cases of a union in another order are a different surface, because every later case has a new tag" <| fun _ ->
      let before = surfaceOfFixture [ "A"; "B"; "C" ] [] []
      let after = surfaceOfFixture [ "A"; "C"; "B" ] [] []
      CoreSurface.compareSurfaces before after
      |> Expect.equal "the type whose cases moved is named" (CoreSurface.SurfaceChange.Changed "Fixture.Outer")

    testCase "a public method added to a type is a different surface" <| fun _ ->
      let before = surfaceOfFixture [ "A" ] [ "One" ] []
      let after = surfaceOfFixture [ "A" ] [ "One"; "Two" ] []
      CoreSurface.compareSurfaces before after
      |> Expect.equal "the type that grew a method is named" (CoreSurface.SurfaceChange.Changed "Fixture.Outer")

    testCase "WHY — a change to what is not public leaves the surface alone, because a program compiled against the build cannot see it" <| fun _ ->
      let before = surfaceOfFixture [ "A" ] [ "One" ] []
      let after = surfaceOfFixture [ "A" ] [ "One" ] [ "Hidden" ]
      CoreSurface.compareSurfaces before after
      |> Expect.equal "unchanged" CoreSurface.SurfaceChange.Unchanged

    testCase "an added type and a removed type are named" <| fun _ ->
      let one = Map.ofList [ "T.One", "a" ]
      let two = Map.ofList [ "T.One", "a"; "T.Two", "b" ]
      CoreSurface.compareSurfaces one two |> Expect.equal "added" (CoreSurface.SurfaceChange.Added "T.Two")
      CoreSurface.compareSurfaces two one |> Expect.equal "removed" (CoreSurface.SurfaceChange.Removed "T.Two")

    testCase "WHY — the real Core with two WorkerMessage cases swapped is a different surface, named WorkerMessage (the worktree that answered 500 to everything)" <| fun _ ->
      withTempDir (fun dir ->
        let swapped = Path.Combine(dir, "SageFs.Core.dll")
        writeCoreWithSwappedCases swapped
        match CoreSurface.surfaceOfFile realCorePath, CoreSurface.surfaceOfFile swapped with
        | CoreSurface.SurfaceReading.Read real, CoreSurface.SurfaceReading.Read changed ->
          CoreSurface.compareSurfaces real changed
          |> Expect.equal "the message type is the one named" (CoreSurface.SurfaceChange.Changed "SageFs.WorkerProtocol+WorkerMessage")
        | CoreSurface.SurfaceReading.Unreadable reason, _
        | _, CoreSurface.SurfaceReading.Unreadable reason -> failtestf "a surface could not be read: %s" reason)
  ]

/// The spawn decision: a project's own Core is adopted into the worker when the worker's host can run on it, and left
/// alone, with the reason, when it cannot.
[<Tests>]
let adoptionTests =
  let realVersion = Reflection.AssemblyName.GetAssemblyName(realCorePath).Version
  /// A daemon base dir with the real Core as both the daemon's own copy and the host directory's copy, and a project whose
  /// newest build of the Core is the one `writeCandidate` writes.
  let withWorld (writeCandidate: string -> unit) (run: string -> string -> unit) =
    withTempDir (fun root ->
      let sharedRoot = Path.Combine(root, "shared")
      let hostDir = Path.Combine(sharedRoot, "host")
      Directory.CreateDirectory hostDir |> ignore
      File.Copy(realCorePath, Path.Combine(sharedRoot, "SageFs.Core.dll"))
      File.Copy(realCorePath, Path.Combine(hostDir, "SageFs.Core.dll"))
      File.WriteAllText(Path.Combine(hostDir, "SageFs.Host.dll"), "host")
      let projectDir = Path.Combine(root, "project")
      let candidate = Path.Combine(projectDir, "bin", "Debug", "net11.0", "SageFs.Core.dll")
      writeCandidate candidate
      File.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddMinutes 5.0)
      run sharedRoot (Path.Combine(projectDir, "P.fsproj")))
  testList "Host core adoption, by surface" [

    testCase "WHY — a project's Core whose surface differs is not adopted, and the plan says why, because the daemon's host was compiled against the other shape and answered 500 to every route" <| fun _ ->
      withWorld writeCoreWithSwappedCases (fun sharedRoot project ->
        match HostCoreAdoption.resolveLaunchRoot sharedRoot "surfacetest" [ project ] realVersion with
        | Ok plan ->
          plan.Cleanup |> Option.iter (fun cleanup -> cleanup ())
          plan.LaunchRoot |> Expect.equal "the worker launches from the daemon's own host directory" sharedRoot
          plan.AdoptedCore |> Expect.isNone "nothing was adopted"
          match plan.Note with
          | HostCoreAdoption.AdoptionNote.NotAdopted reason ->
            reason |> Expect.stringContains "the reason names the type that differs" "SageFs.WorkerProtocol+WorkerMessage"
          | HostCoreAdoption.AdoptionNote.NothingToSay -> failtest "the plan must carry the reason"
        | Error reason -> failtestf "expected a plan, got the refusal: %s" reason)

    testCase "WHY — the twin: a project's Core with the same surface and different bytes is adopted, the dogfood case the adoption exists for" <| fun _ ->
      withWorld writeCoreUnchanged (fun sharedRoot project ->
        match HostCoreAdoption.resolveLaunchRoot sharedRoot "surfacetwin" [ project ] realVersion with
        | Ok plan ->
          plan.Cleanup |> Option.iter (fun cleanup -> cleanup ())
          plan.AdoptedCore |> Expect.isSome "the build was adopted"
          plan.Note |> Expect.equal "and there is no reason to give" HostCoreAdoption.AdoptionNote.NothingToSay
          (plan.LaunchRoot <> sharedRoot) |> Expect.isTrue "into a private root"
        | Error reason -> failtestf "expected a plan, got the refusal: %s" reason)

    testCase "WHY — previewNote says what a spawn would say, without spawning, so the create reply can name a Core that will not be adopted" <| fun _ ->
      withWorld writeCoreWithSwappedCases (fun sharedRoot project ->
        match HostCoreAdoption.previewNote sharedRoot [ project ] realVersion with
        | HostCoreAdoption.AdoptionNote.NotAdopted reason ->
          reason |> Expect.stringContains "names the type that differs" "SageFs.WorkerProtocol+WorkerMessage"
        | HostCoreAdoption.AdoptionNote.NothingToSay -> failtest "a Core of another shape is not adopted, and the preview must say so")

    testCase "the twin of previewNote: a Core of the same shape has nothing to say" <| fun _ ->
      withWorld writeCoreUnchanged (fun sharedRoot project ->
        HostCoreAdoption.previewNote sharedRoot [ project ] realVersion
        |> Expect.equal "adopted, so silent" HostCoreAdoption.AdoptionNote.NothingToSay)
  ]

/// What a person is told: in the create reply, in the status payload, and for as long as the session lives.
[<Tests>]
let adoptionSurfaceTests =
  let notAdopted = HostCoreAdoption.AdoptionNote.NotAdopted "SageFs.WorkerProtocol+WorkerMessage has a different public shape"
  testList "Core adoption, as told" [

    testCase "WHY — a session's note is kept from its spawn until it is forgotten, because a Core that was not adopted went to the daemon log only" <| fun _ ->
      let sid = sprintf "note-%s" (Guid.NewGuid().ToString("N"))
      HostCoreAdoption.noteFor sid |> Expect.equal "nothing recorded says nothing" HostCoreAdoption.AdoptionNote.NothingToSay
      HostCoreAdoption.recordNote sid notAdopted
      HostCoreAdoption.noteFor sid |> Expect.equal "the reason is there" notAdopted
      HostCoreAdoption.forgetNote sid
      HostCoreAdoption.noteFor sid |> Expect.equal "and gone once the session's worker is" HostCoreAdoption.AdoptionNote.NothingToSay

    testCase "WHY — the create reply line names the reason and the next action, and a Core that was adopted adds no line" <| fun _ ->
      match CreateSessionUx.adoptionHint notAdopted with
      | Some line ->
        line |> Expect.stringContains "the reason" "has a different public shape"
        line |> Expect.stringContains "the next action" "install-local.fsx"
      | None -> failtest "a Core that is not adopted must be told"
      CreateSessionUx.adoptionHint HostCoreAdoption.AdoptionNote.NothingToSay |> Expect.isNone "silent when adopted"

    testCase "WHY — the surface cache keeps at most its capacity, because it was keyed by path and write time and never let an entry go" <| fun _ ->
      withTempDir (fun dir ->
        for i in 1 .. CoreSurface.cacheCapacity + 4 do
          let path = Path.Combine(dir, sprintf "Fixture%d.dll" i)
          writeSurfaceFixture path [ "A" ] [ sprintf "Method%d" i ] []
          CoreSurface.surfaceOfFileCached path |> ignore
        (CoreSurface.cachedCount () <= CoreSurface.cacheCapacity)
        |> Expect.isTrue "the cache is bounded")
  ]
