module SageFs.Tests.CheckProjectCompilerTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Features.ProjectCheckCore
open SageFs.Features

/// The compiler half of `check_project`, in this process: a project of files on disk, checked with and without overlays.
/// The arguments are built by hand with the references a script gets (FSharp.Core and the framework), because the MSBuild run
/// that reads them from a real project is what `CheckProjectMsBuildTests` covers. What these cases pin is what the user
/// sees: an overlay changes the answer, the diagnostic names the project's own file at the right place, and the files on
/// disk are never touched.

let private notes : RunNotes = { Arguments = ArgumentsSource.Kept; Built = [] }

let private withProject (files: (string * string) list) (body: MsBuildFacts -> string -> Task<unit>) : Task<unit> =
  task {
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-project-check-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
      for name, text in files do File.WriteAllText(Path.Combine(dir, name), text)
      let checker = FSharp.Compiler.CodeAnalysis.FSharpChecker.Create()
      let! script, _ =
        checker.GetProjectOptionsFromScript(Path.Combine(dir, "probe.fsx"), FSharp.Compiler.Text.SourceText.ofString "", assumeDotNetFramework = false)
      let projectFile = Path.Combine(dir, "Fixture.fsproj")
      let facts : MsBuildFacts =
        { Arguments =
            { ProjectFile = projectFile
              Options = script.OtherOptions |> Array.filter (fun o -> o.StartsWith "-") |> Array.toList
              Sources = files |> List.map (fun (name, _) -> Path.Combine(dir, name)) }
          Configuration = "Debug"
          References = []
          Inputs = [ projectFile ] }
      do! body facts dir
    finally
      Directory.Delete(dir, true)
  }

let private check (facts: MsBuildFacts) (overlays: Overlay list) : Task<CheckOutcome> =
  CheckProject.checkWithFacts facts overlays notes CancellationToken.None

let private errorsOf (outcome: CheckOutcome) : CheckDiagnostic list =
  match outcome with
  | CheckOutcome.Checked(diagnostics, _) -> diagnostics |> List.filter (fun d -> d.Severity = Severity.Errored)
  | other -> failtestf "expected a completed check, got %A" other

let private libraryText = "module Lib\n\nlet value : int = 1\n"
let private callerText = "module Caller\n\nlet answer : int = Lib.value + 1\n"

[<Tests>]
let tests =
  testList "check_project against the real compiler" [
    testCaseTask "WHY — a project whose files agree checks clean, with no overlay at all" <| fun () ->
      withProject [ "Lib.fs", libraryText; "Caller.fs", callerText ] (fun facts _ ->
        task {
          let! outcome = check facts []
          outcome |> errorsOf |> Expect.isEmpty "no errors"
        })

    testCaseTask "WHY — an overlay that changes a type in the first file breaks the file that uses it, and the error names that file's own line and column" <| fun () ->
      withProject [ "Lib.fs", libraryText; "Caller.fs", callerText ] (fun facts dir ->
        task {
          let overlay = { Path = "Lib.fs"; Text = "module Lib\n\nlet value : string = \"one\"\n" }
          let! outcome = check facts [ overlay ]
          match errorsOf outcome with
          | [] -> failtest "the overlay should break Caller.fs"
          | first :: _ ->
            first.Path |> Expect.equal "the project's own caller file" (Path.Combine(dir, "Caller.fs"))
            first.Line |> Expect.equal "the line of the use" 3
            first.Code |> Expect.isNotEmpty "a code such as FS0001"
        })

    testCaseTask "WHY — an overlay on the file with the error reports the project's path for it, not the overlay directory's" <| fun () ->
      withProject [ "Lib.fs", libraryText; "Caller.fs", callerText ] (fun facts dir ->
        task {
          let overlay = { Path = Path.Combine(dir, "Caller.fs"); Text = "module Caller\n\nlet answer : int = Lib.value + \"x\"\n" }
          let! outcome = check facts [ overlay ]
          match errorsOf outcome with
          | [] -> failtest "the overlay has a type error"
          | errors ->
            errors |> List.map (fun d -> d.Path) |> List.distinct |> Expect.equal "only the project path" [ Path.Combine(dir, "Caller.fs") ]
        })

    testCaseTask "WHY — an overlay that fixes the file makes the project clean again, and the files on disk are never rewritten" <| fun () ->
      withProject [ "Lib.fs", libraryText; "Caller.fs", "module Caller\n\nlet answer : int = Lib.value + \"x\"\n" ] (fun facts dir ->
        task {
          let brokenOnDisk = File.ReadAllText(Path.Combine(dir, "Caller.fs"))
          let! broken = check facts []
          broken |> errorsOf |> List.isEmpty |> Expect.isFalse "broken on disk"
          let! fixedByOverlay = check facts [ { Path = "Caller.fs"; Text = callerText } ]
          fixedByOverlay |> errorsOf |> Expect.isEmpty "clean with the overlay"
          File.ReadAllText(Path.Combine(dir, "Caller.fs")) |> Expect.equal "the file on disk is as it was" brokenOnDisk
        })

    testCaseTask "WHY — an overlay for a file the project does not compile is refused before the compiler runs" <| fun () ->
      withProject [ "Lib.fs", libraryText ] (fun facts _ ->
        task {
          match! check facts [ { Path = "Nope.fs"; Text = "module Nope" } ] with
          | CheckOutcome.Refused(CheckRefusal.Overlay(OverlayRefusal.NotInProject _)) -> ()
          | other -> failtestf "expected an overlay refusal, got %A" other
        })

    testCaseTask "WHY — a check whose caller has already cancelled says it was cancelled at the compiler, and concludes nothing" <| fun () ->
      withProject [ "Lib.fs", libraryText ] (fun facts _ ->
        task {
          use source = new CancellationTokenSource()
          source.Cancel()
          match! CheckProject.checkWithFacts facts [] notes source.Token with
          | CheckOutcome.Cancelled Stopped.Compiler -> ()
          | other -> failtestf "expected Cancelled Compiler, got %A" other
        })
  ]
