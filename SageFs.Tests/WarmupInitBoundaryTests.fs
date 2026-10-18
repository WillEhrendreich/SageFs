module SageFs.Tests.WarmupInitBoundaryTests

open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open FSharp.Compiler.Interactive.Shell
open SageFs
open SageFs.FsiSession

let private warmupStartedAt =
  DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)

let private withTempDir run =
  let dir =
    Path.Combine(Path.GetTempPath(), $"sagefs-warmup-init-{Guid.NewGuid():N}")

  Directory.CreateDirectory(dir) |> ignore

  try
    run dir
  finally
    if Directory.Exists dir then
      Directory.Delete(dir, true)

[<Tests>]
let warmupInitBoundaryTests =
  testList "WarmupInitBoundary" [
    testCase "completeWarmup stops total timing at the namespace-open boundary" <| fun _ ->
      let boundaryMs = FixtureDurations.warmupBoundaryMs
      let ctx : WarmupContext =
        WarmupContext.completeWarmup
          warmupStartedAt
          3
          []
          []
          []
          25L
          80L
          boundaryMs

      ctx.StartedAt
      |> Expect.equal "warmup should keep the original start timestamp" warmupStartedAt

      ctx.PhaseTiming.ScanSourceFilesMs
      |> Expect.equal "scan timing should come from the first warmup milestone" 25L

      ctx.PhaseTiming.ScanAssembliesMs
      |> Expect.equal "assembly timing should stop at the assembly milestone" 55L

      ctx.PhaseTiming.OpenNamespacesMs
      |> Expect.equal "open timing should stop at the open milestone" 40L

      ctx.PhaseTiming.TotalMs
      |> Expect.equal
        "post-warmup startup work should not inflate warmup total timing"
        boundaryMs

      WarmupContext.completionDuration ctx
      |> Expect.equal
        "completion duration should stay anchored at the warmup boundary"
        (TimeSpan.FromMilliseconds (float boundaryMs))

    testCase "applyIfPresent reports missing startup profile without pretending it ran" <| fun _ ->
      let evalCalls = ResizeArray<string>()
      let logCalls = ResizeArray<string>()

      let outcome =
        StartupProfile.applyIfPresent
          @"C:\Code\Repos\SageFs\definitely-not-a-real-startup-profile-dir"
          [ @"C:\Code\Repos\SageFs\definitely-not-a-real-startup-profile-dir" ]
          evalCalls.Add
          logCalls.Add

      outcome
      |> Expect.equal
        "missing init script should stay distinct from a failed or loaded profile"
        StartupProfile.NotFound

      evalCalls
      |> Seq.toList
      |> Expect.isEmpty "missing init script should not evaluate anything"

      logCalls
      |> Seq.toList
      |> Expect.isEmpty "missing init script should not log a fake load"

    testCase "applyIfPresent preserves startup profile failures after warmup" <| fun _ ->
      withTempDir <| fun workingDir ->
        let scriptPath = Path.Combine(workingDir, ".SageFsrc")
        File.WriteAllText(scriptPath, "let warmupBoundary = 42")

        let outcome =
          StartupProfile.applyIfPresent
            workingDir
            [ workingDir ]
            (fun _ -> failwith "boom")
            ignore

        match outcome with
        | StartupProfile.Failed(path, message) ->
          path
          |> Expect.equal "failure should keep the init script path" scriptPath

          message
          |> Expect.stringContains "failure should preserve the evaluation error" "boom"

          StartupProfile.loadedPath outcome
          |> Expect.isNone "failed startup profiles should not surface as loaded"
        | other ->
          failtestf "expected Failed outcome, got %A" other

    testCase "loadedPath preserves the existing loaded-profile contract" <| fun _ ->
      let path = @"C:\Code\Repos\SageFs\.SageFsrc"

      StartupProfile.loadedPath (StartupProfile.Loaded path)
      |> Expect.equal "loaded startup profiles should still expose their path" (Some path)
  ]

/// The two startup script files a folder can hold.
type private InitFile =
  | InitFsx
  | SageFsrc

let private writeInit (dir: string) (file: InitFile) =
  let path =
    match file with
    | InitFsx -> Path.Combine(dir, ".SageFs", "init.fsx")
    | SageFsrc -> Path.Combine(dir, ".SageFsrc")
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  File.WriteAllText(path, "()")
  path

/// What a session in `site/` with `Dev/Dev.fsproj` as its primary project searches.
let private siteAndProject (site: string) =
  let project = Path.Combine(site, "Dev")
  Directory.CreateDirectory project |> ignore
  project, StartupProfile.searchDirectories (Some project) site

let private newInProcess () : IFsiSession =
  let session =
    FsiEvaluationSession.Create(
      FsiEvaluationSession.GetDefaultConfiguration(),
      [| "fsi"; "--noninteractive"; "--nologo"; "--readline-" |],
      new StreamReader(Stream.Null),
      TextWriter.Null,
      TextWriter.Null,
      collectible = true
    )
  new InProcessFsiSession(session, { Projects = []; ResolveFrom = []; ValueReads = SageFs.Middleware.ValueReadTracking.ValueReadWatch.IgnoreValueReads; WebDevReload = SageFs.HostAgent.WebDevReload.Off }) :> IFsiSession

[<Tests>]
let startupProfileLocationTests =
  testList "Where the startup profile is read from" [
    testCase "WHY — an init.fsx kept in the project folder still runs when the working directory has one too, so a monorepo root's init.fsx cannot silently replace each project's own" <| fun _ ->
      withTempDir <| fun site ->
        let project, dirs = siteAndProject site
        let expected = writeInit project InitFsx
        writeInit site InitFsx |> ignore
        let evaluated = ResizeArray<string>()
        StartupProfile.applyIfPresent project dirs evaluated.Add ignore
        |> Expect.equal "the project folder's script loads" (StartupProfile.Loaded expected)
        evaluated |> Seq.length |> Expect.equal "one script is evaluated" 1

    testCase "WHY — a session's init.fsx is read from its working directory, where its config.fsx is, when the project folder has none" <| fun _ ->
      withTempDir <| fun site ->
        let _, dirs = siteAndProject site
        let expected = writeInit site InitFsx
        StartupProfile.discoverInitScript dirs
        |> Expect.equal "site/.SageFs/init.fsx is found" (Some expected)

    testCase "WHY — the folder decides before the file name, so the project's .SageFsrc beats the working directory's init.fsx" <| fun _ ->
      withTempDir <| fun site ->
        let project, dirs = siteAndProject site
        let expected = writeInit project SageFsrc
        writeInit site InitFsx |> ignore
        StartupProfile.discoverInitScript dirs
        |> Expect.equal "site/Dev/.SageFsrc is found" (Some expected)

    testCase "WHY — a project-folder script is evaluated exactly as written, because FSI already resolves its relative paths there" <| fun _ ->
      withTempDir <| fun site ->
        let project, _ = siteAndProject site
        let script : StartupProfile.InitScript = { Folder = project; Path = Path.Combine(project, ".SageFs", "init.fsx") }
        StartupProfile.source project script "let x = 1"
        |> Expect.equal "no line directive" "let x = 1"

    testCase "WHY — a working-directory script's relative #load finds files in that folder, though FSI started in the project folder" <| fun _ ->
      withTempDir <| fun site ->
        File.WriteAllText(Path.Combine(site, "lib.fsx"), "let answer = 42")
        let contents =
          String.concat "\n" [
            "#load \"lib.fsx\""
            "if Lib.answer <> 42 then failwith \"the wrong lib.fsx loaded\"" ]
        Directory.CreateDirectory(Path.Combine(site, ".SageFs")) |> ignore
        File.WriteAllText(Path.Combine(site, ".SageFs", "init.fsx"), contents)
        // FSI resolves relative paths against the folder it was started in: the test process's.
        let fsiBaseDir = Environment.CurrentDirectory
        let session = newInProcess ()
        try
          let eval code = SageFs.FsiSession.evalOrThrow session code CancellationToken.None
          (fun () -> eval contents)
          |> Expect.throws "the same text evaluated as written looks for lib.fsx in FSI's own folder"
          StartupProfile.applyIfPresent fsiBaseDir [ site ] eval ignore
          |> Expect.equal "the script loads with its own folder's lib.fsx" (StartupProfile.Loaded (Path.Combine(site, ".SageFs", "init.fsx")))
        finally
          session.Dispose()

    testCase "a bare session, or one whose project sits in its working directory, searches that one folder" <| fun _ ->
      StartupProfile.searchDirectories None "/work/site"
      |> Expect.equal "no project" [ "/work/site" ]
      StartupProfile.searchDirectories (Some "/work/site") "/work/site"
      |> Expect.equal "project in the working directory" [ "/work/site" ]

    testProperty "the project folder is searched first, and the working directory after it only when it differs" <| fun (projectDir: string option) (sessionDir: string) ->
      let dirs = StartupProfile.searchDirectories projectDir sessionDir
      dirs = List.distinct dirs
      && (match projectDir with
          | Some dir when dir <> sessionDir -> dirs = [ dir; sessionDir ]
          | Some dir -> dirs = [ dir ]
          | None -> dirs = [ sessionDir ])
  ]
