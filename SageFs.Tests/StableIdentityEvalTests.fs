module SageFs.Tests.StableIdentityEvalTests

open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs.AppState
open SageFs.Features.ReloadPlanning
open SageFs.Middleware.CompilationContext
open SageFs.Tests.TestInfrastructure

let private fixturePath = Path.Combine(__SOURCE_DIRECTORY__, "StableIdentityProbeFixture.fs")

/// The fixture in LF: a Windows checkout has CRLF, and the tests' "\n"-based
/// edits would otherwise silently leave the source unchanged.
let private fixtureSource () = File.ReadAllText(fixturePath).Replace("\r\n", "\n")

let private decls (source: string) =
  match extractDecls source with
  | Ok d -> d
  | Error e -> failtestf "fixture does not extract: %s" e

let private eval (code: string) =
  globalActorResult.Value.Actor.PostAndAsyncReply(fun rc -> Eval({ Code = code; Args = Map.empty }, CancellationToken.None, rc))

/// Emit the patch a save of `edited` needs, after referencing the compiled fixture.
let private patchWith (edited: string) =
  task {
    let assembly = typeof<StableIdentityProbe.Fixture.Item>.Assembly.Location
    let! referenced = eval (sprintf "#r @\"%s\"" assembly)
    match referenced.EvaluationResult with
    | Error ex -> failtestf "could not reference the test assembly: %s" ex.Message
    | Ok _ -> ()
    let source = fixtureSource ()
    let editedDecls = decls edited
    match planReload (decls source) editedDecls with
    | ReloadPlan.PatchFunctions [ f ] -> return! eval (emitStableIdentity fixturePath editedDecls [ f ]).Code
    | other -> return failtestf "expected one function to patch, got %A" other
  }

/// Emit one named function of `edited` without consulting the planner, to pin what FSI itself does.
let private patchFunction (edited: string) (name: string) =
  task {
    let assembly = typeof<StableIdentityProbe.Fixture.Item>.Assembly.Location
    let! referenced = eval (sprintf "#r @\"%s\"" assembly)
    match referenced.EvaluationResult with
    | Error ex -> failtestf "could not reference the test assembly: %s" ex.Message
    | Ok _ -> ()
    let editedDecls = decls edited
    let f = editedDecls.Decls |> List.find (fun d -> d.Name = name)
    return! eval (emitStableIdentity fixturePath editedDecls [ f ]).Code
  }

[<Tests>]
let stableIdentityEvalTests =
  testSequenced <| testList "Stable-identity reload in FSI" [
    testTask "WHY — stable-identity reload — a patched function takes the compiled type and returns its new result because the running app's own values flow through it" {
      let source = fixtureSource ()
      let! patched = patchWith (source.Replace("  xs.Length\n", "  xs.Length + 100\n"))
      match patched.EvaluationResult with
      | Error ex -> failtestf "patch did not compile: %s" ex.Message
      | Ok _ -> ()
      let! called = eval "StableIdentityProbe.Fixture.count global.StableIdentityProbe.Fixture.items"
      match called.EvaluationResult with
      | Ok output -> output |> Expect.stringContains "the patched body ran on the compiled list" "101"
      | Error ex -> failtestf "the patch does not accept the compiled type: %s" ex.Message
    }

    testTask "WHY — stable-identity reload — a patch's compile error is reported on the source line because the overlay must point at the user's file" {
      let source = fixtureSource ()
      let! patched = patchWith (source.Replace("  xs.Length\n", "  xs.Length + \"oops\"\n"))
      patched.EvaluationResult |> Result.isError |> Expect.isTrue "the broken patch fails"
      let errorLine =
        source.Replace("\r\n", "\n").Split('\n') |> Array.findIndex (fun l -> l = "  xs.Length") |> (+) 1
      patched.Diagnostics
      |> Array.filter (fun d -> d.Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Blocking)
      |> Array.map _.Range.StartLine
      |> Array.distinct
      |> Expect.equal "errors sit on the fixture's line" [| errorLine |]
    }

    testTask "WHY — stable-identity reload — a patch that uses a private compiled member cannot compile in FSI, which is why the planner restarts instead" {
      let source = fixtureSource ()
      let! patched = patchFunction (source.Replace("  secret () + 1\n", "  secret () + 2\n")) "answer"
      patched.EvaluationResult |> Result.isError |> Expect.isTrue "the patch cannot reach the private member"
      let reported =
        patched.Diagnostics
        |> Array.filter (fun d -> d.Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Blocking)
        |> Array.map (fun d -> sprintf "%s %s" d.Subcategory d.Message)
      reported |> Array.exists (fun m -> m.Contains "secret") |> Expect.isTrue "the error names the private member"
    }
  ]
