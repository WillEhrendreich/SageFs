module SageFs.Tests.WarmupInitBoundaryTests

open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open FsCheck
open FSharp.Compiler.Interactive.Shell
open Mono.Cecil
open Mono.Cecil.Cil
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

/// A reference assembly, like a NuGet package's `ref/` folder holds: FSI compiles against it, and the runtime refuses
/// to load it. `RefOnlyFixture.Marker.Answer()` returns 42 in a file that could run.
let private writeReferenceAssembly (path: string) =
  use assembly =
    AssemblyDefinition.CreateAssembly(AssemblyNameDefinition("RefOnlyFixture", System.Version(1, 0, 0, 0)), "RefOnlyFixture", ModuleKind.Dll)
  let main = assembly.MainModule
  let marker =
    TypeDefinition(
      "RefOnlyFixture", "Marker",
      TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed ||| TypeAttributes.Class,
      main.ImportReference(typeof<obj>))
  let answer = MethodDefinition("Answer", MethodAttributes.Public ||| MethodAttributes.Static, main.ImportReference(typeof<int>))
  let il = answer.Body.GetILProcessor()
  il.Append(il.Create(OpCodes.Ldc_I4, 42))
  il.Append(il.Create(OpCodes.Ret))
  marker.Methods.Add answer
  main.Types.Add marker
  let referenceAssembly = typeof<System.Runtime.CompilerServices.ReferenceAssemblyAttribute>.GetConstructor(Type.EmptyTypes)
  assembly.CustomAttributes.Add(CustomAttribute(main.ImportReference referenceAssembly))
  assembly.Write path

/// Run `contents` as the working directory's init.fsx in a real session, the way a session's warmup does.
let private applyInSession (site: string) (contents: string) =
  let path = Path.Combine(site, ".SageFs", "init.fsx")
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  File.WriteAllText(path, contents)
  let session = newInProcess ()
  try
    let eval code = evalOrThrow session code CancellationToken.None
    StartupProfile.applyIfPresent site [ site ] eval ignore
  finally
    session.Dispose()

let private diagnostic severity errorNumber (startLine, startColumn, endLine, endColumn) message : Features.Diagnostics.Diagnostic =
  { Message = message
    Subcategory = ""
    Range = { StartLine = startLine; StartColumn = startColumn; EndLine = endLine; EndColumn = endColumn }
    Severity = severity
    ErrorNumber = errorNumber }

let private fsiWrapper = "Operation could not be completed due to earlier error"

[<Tests>]
let startupProfileFailureTests =
  testList "What a failed startup profile says" [
    testCase "WHY — an init.fsx that runs to the end still fails when FSI could not load an assembly it uses, and the warning names the assembly" <| fun _ ->
      // FSI records the failed load as an error even though the script caught the exception, so the profile fails
      // and its bindings are lost. FSI's own message names no cause, so the warning has to carry the diagnostic.
      withTempDir <| fun site ->
        let fixture = Path.Combine(site, "RefOnlyFixture.dll")
        writeReferenceAssembly fixture
        let contents =
          String.concat "\n" [
            sprintf "#r @\"%s\"" fixture
            "[<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]"
            "let useFixture () = RefOnlyFixture.Marker.Answer()"
            "let caught = try useFixture () with _ -> -1" ]
        match applyInSession site contents with
        | StartupProfile.Failed(_, message) ->
          message |> Expect.stringContains "the warning names the FSI error" "FS0193 — "
          message |> Expect.stringContains "the warning says which assembly" "RefOnlyFixture"
          message |> Expect.stringContains "the warning says why it could not load" "Reference assemblies cannot be loaded for execution"
          message |> Expect.stringContains "FSI's own words are kept after the cause" fsiWrapper
        | other -> failtestf "expected the profile to fail, got %A" other

    testCase "WHY — a typo in a file init.fsx #loads names the identifier, and no line, since the line would read as init.fsx's" <| fun _ ->
      withTempDir <| fun site ->
        File.WriteAllText(Path.Combine(site, "helpers.fsx"), "let ok = 1\n\nlet x = undefinedThing")
        match applyInSession site (sprintf "#load @\"%s\"" (Path.Combine(site, "helpers.fsx"))) with
        | StartupProfile.Failed(_, message) ->
          message |> Expect.stringContains "the warning names the error, with no location" "FS0039 — "
          message |> Expect.stringContains "the warning names the identifier" "undefinedThing"
        | other -> failtestf "expected the profile to fail, got %A" other

    testCase "WHY — a failure with no error diagnostic is raised unchanged, because warnings name no cause" <| fun _ ->
      let warning = diagnostic Features.Diagnostics.DiagnosticSeverity.Warning 64 (3, 4, 3, 9) "a warning"
      let ex = Exception "the host went away"
      obj.ReferenceEquals(failureToRaise ex [| warning |], ex)
      |> Expect.isTrue "evalOrThrow raises FSI's exception unchanged"

    testCase "WHY — a cancellation or a SageFs error is raised as it is, even inside an AggregateException, so callers that match on the type still see it" <| fun _ ->
      // Error diagnostics are present, so anything not typed would be wrapped: only the type keeps these out.
      let error = diagnostic Features.Diagnostics.DiagnosticSeverity.Blocking 39 (1, 1, 1, 5) "undefinedThing"
      let typed : (string * exn) list = [
        "a cancellation", OperationCanceledException "cancelled"
        "a SageFs error", SageFsErrorException(SageFsError.EvalFailed "Session is resetting")
        "a cancellation in an AggregateException", AggregateException(OperationCanceledException "cancelled")
        "a SageFs error in an AggregateException", AggregateException(SageFsErrorException(SageFsError.EvalFailed "Session is resetting")) ]
      for name, ex in typed do
        obj.ReferenceEquals(failureToRaise ex [| error |], ex)
        |> Expect.isTrue (sprintf "%s is raised unwrapped" name)
      let untyped = AggregateException(InvalidOperationException "boom")
      (failureToRaise untyped [| error |]).InnerException
      |> Expect.equal "an AggregateException of anything else is wrapped with the diagnostics" (untyped :> exn)

    testProperty "WHY — the first three distinct errors are listed, the rest counted, warnings left out, and FSI's words kept last" <| fun (raw: (bool * int * int * int * int) list) ->
      // Each message is unique, so one containing another is no accident. The ranges are anything at all: the message
      // shows no location, because a diagnostic does not say which file it is in.
      let diagnostics =
        raw
        |> List.mapi (fun i (isError, a, b, c, d) ->
          match isError with
          | true -> diagnostic Features.Diagnostics.DiagnosticSeverity.Blocking 39 (a, b, c, d) (sprintf "<error %d>" i)
          | false -> diagnostic Features.Diagnostics.DiagnosticSeverity.Warning 64 (a, b, c, d) (sprintf "<warning %d>" i))
        |> List.toArray
      let errors = diagnostics |> Array.filter (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Blocking)
      match WarmUp.WarmupFcsDiagnostic.failureMessage fsiWrapper (warmupDiagnostics (Array.append diagnostics diagnostics)), errors with
      | None, [||] -> true
      | Some message, errors when errors.Length > 0 ->
        let listed, counted = errors |> Array.splitAt (min 3 errors.Length)
        let rest =
          match counted.Length with
          | 0 -> not (message.Contains " more error")
          | 1 -> message.Contains " (and 1 more error) "
          | n -> message.Contains(sprintf " (and %d more errors) " n)
        listed |> Array.forall (fun d -> message.Contains(sprintf "FS0039 — %s" d.Message))
        && counted |> Array.forall (fun d -> not (message.Contains d.Message))
        && diagnostics |> Array.forall (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Blocking || not (message.Contains d.Message))
        && rest
        && message.EndsWith(sprintf "(FSI: %s)" fsiWrapper)
      | _ -> false
  ]
