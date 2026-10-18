/// A save to a `[<RequireQualifiedAccess>]` module used to fail as a compile error on a line the user never wrote.
///
/// A patch is compiled against the COMPILED module: `renderPatch` writes `open global.<module>` so the re-emitted
/// functions bind to the running app's types and to the members the save did not touch. F# refuses that open for a
/// module marked `[<RequireQualifiedAccess>]` (FS0892, an error, so `#nowarn` cannot silence it), and the save was
/// reported as `CompileFailed` with "This declaration opens the module ..., which is marked as
/// 'RequireQualifiedAccess'". Measured on 0.6.908 against `module Repro.Qualified`.
///
/// The emitter now refuses to write that patch, and the worker routes the refusal: a restart for an app run_app
/// owns, a whole-file re-evaluation judged by what reached the running app, or, when that re-evaluation would reset
/// a `let mutable`, a restart that leaves the live state alone.
module SageFs.Tests.QualifiedAccessPatchTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning
open SageFs.Middleware.CompilationContext

let private declsOf (source: string) : FileDecls =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

let private fn (decls: FileDecls) (name: string) : SourceDecl = decls.Decls |> List.find (fun d -> d.Name = name)

let private qualifiedSource = """[<RequireQualifiedAccess>]
module Repro.Qualified

let greet () = "qualified v2"
"""

let private plainSource = """module Repro.Plain

let greet () = "plain v2"
"""

let private nestedSource = """[<RequireQualifiedAccess>]
module Repro.Outer

module Inner =
  let greet () = "inner v2"

[<RequireQualifiedAccess>]
module Strict =
  let greet () = "strict v2"
"""

let private emit (decls: FileDecls) (name: string) =
  emitPatchCarrying "/p/File.fs" decls [ fn decls name ] []

let private openLines (code: string) =
  code.Split('\n') |> Array.map _.Trim() |> Array.filter _.StartsWith("open global.") |> Array.toList

[<Tests>]
let parserTests =
  testList "which modules a file marks [<RequireQualifiedAccess>]" [
    testCase "WHY — the file's own module is recorded by its full path, the path a patch would open" <| fun _ ->
      (declsOf qualifiedSource).QualifiedAccessModules
      |> Expect.equal "Repro.Qualified" [ [ "Repro"; "Qualified" ] ]

    testCase "WHY — a module without the attribute is recorded as nothing, so its patch is written as it always was" <| fun _ ->
      (declsOf plainSource).QualifiedAccessModules |> Expect.isEmpty "no attribute"

    testCase "WHY — a nested module is recorded under its parent, by the same names a declaration's Container uses" <| fun _ ->
      (declsOf nestedSource).QualifiedAccessModules
      |> Expect.equal "the file module and Strict, not Inner" [ [ "Repro"; "Outer" ]; [ "Repro"; "Outer"; "Strict" ] ]

    testCase "WHY — the attribute counts however it is spelled: qualified, with its suffix, or beside another attribute" <| fun _ ->
      let spelled attribute =
        (declsOf (sprintf "[<%s>]\nmodule A.B\n\nlet f () = 1\n" attribute)).QualifiedAccessModules
      [ "Microsoft.FSharp.Core.RequireQualifiedAccess"
        "RequireQualifiedAccessAttribute"
        "RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)" ]
      |> List.iter (fun attribute -> spelled attribute |> Expect.equal attribute [ [ "A"; "B" ] ])

    testCase "WHY — a module inside a namespace is recorded under the namespace, which is part of its compiled name" <| fun _ ->
      (declsOf "namespace Shop\n\n[<RequireQualifiedAccess>]\nmodule Cart =\n  let total () = 0\n").QualifiedAccessModules
      |> Expect.equal "Shop.Cart" [ [ "Shop"; "Cart" ] ]
  ]

[<Tests>]
let emitterTests =
  testList "a patch into a [<RequireQualifiedAccess>] module" [
    testCase "WHY — the emitter refuses rather than write an open F# rejects, and names the module" <| fun _ ->
      match emit (declsOf qualifiedSource) "greet" with
      | Error (PatchRefusal.QualifiedAccess modulePath) -> modulePath |> Expect.equal "the module" [ "Repro"; "Qualified" ]
      | other -> failtestf "expected a QualifiedAccess refusal, got %A" other

    testCase "WHY — a module without the attribute is still opened onto the compiled one" <| fun _ ->
      match emit (declsOf plainSource) "greet" with
      | Ok patch -> openLines patch.Code |> Expect.equal "the compiled module" [ "open global.Repro.Plain" ]
      | Error refusal -> failtestf "should emit: %A" refusal

    testCase "WHY — only the module a declaration lives in is opened, so a plain module inside a strict one still patches" <| fun _ ->
      // Measured with `dotnet fsi` against a compiled fixture: `open global.Repro.Outer.Inner` compiles when Outer is
      // [<RequireQualifiedAccess>] and Inner is not. F# checks the module it opens, not the path to it.
      match emit (declsOf nestedSource) "greet" with
      | Ok patch -> openLines patch.Code |> Expect.equal "Inner alone" [ "open global.Repro.Outer.Inner" ]
      | Error refusal -> failtestf "should emit: %A" refusal

    testCase "WHY — a strict module nested in another is refused by its own path" <| fun _ ->
      let decls = declsOf nestedSource
      let strict = decls.Decls |> List.find (fun d -> d.Container = [ "Strict" ])
      match emitPatchCarrying "/p/File.fs" decls [ strict ] [] with
      | Error (PatchRefusal.QualifiedAccess modulePath) -> modulePath |> Expect.equal "Strict" [ "Repro"; "Outer"; "Strict" ]
      | other -> failtestf "expected a QualifiedAccess refusal, got %A" other

    testCase "WHY — a definition another file contributes is opened too, so its module is checked by its own file's attributes" <| fun _ ->
      let plain = declsOf plainSource
      let strict = declsOf qualifiedSource
      let definition = { FilePath = "/p/Qualified.fs"; Decls = strict; Functions = [ fn strict "greet" ] }
      match emitPatchWithDefinitions "/p/Plain.fs" plain [ fn plain "greet" ] [] [ definition ] with
      | Error (PatchRefusal.QualifiedAccess modulePath) -> modulePath |> Expect.equal "the definition's module" [ "Repro"; "Qualified" ]
      | other -> failtestf "expected a QualifiedAccess refusal, got %A" other

    testCase "WHY — taking the attribute off in the editor does not take it off the running module" <| fun _ ->
      // The draft's second case: the loaded assembly has the attribute and the source no longer does. The patch is
      // compiled against the assembly, so the assembly's attribute decides.
      let edited = declsOf (qualifiedSource.Replace("[<RequireQualifiedAccess>]\n", ""))
      edited.QualifiedAccessModules |> Expect.isEmpty "the edit has no attribute"
      let compiled = (declsOf qualifiedSource).QualifiedAccessModules
      let againstRunning = againstRunningModules compiled edited edited
      match emit againstRunning "greet" with
      | Error (PatchRefusal.QualifiedAccess modulePath) -> modulePath |> Expect.equal "the compiled module" [ "Repro"; "Qualified" ]
      | other -> failtestf "expected a QualifiedAccess refusal, got %A" other

    testCase "WHY — the refusal is a restart that names the module and says it is unimplemented, not impossible" <| fun _ ->
      match ReloadChange.restartReason (ReloadChange.RequiresQualifiedAccess "Repro.Qualified") with
      | RestartReason.NotYetSupported shape -> shape |> Expect.stringContains "names the module" "'Repro.Qualified'"
      | other -> failtestf "expected NotYetSupported, got %A" other
  ]

[<Tests>]
let routeTests =
  testList "where a refused save goes" [
    testCase "WHY — an app run_app started is restarted by SageFs, because a re-evaluation in the FSI host never reaches it" <| fun _ ->
      qualifiedAccessRoute AppPlacement.InWorkerProcess (declsOf qualifiedSource)
      |> Expect.equal "restart" QualifiedAccessRoute.RestartApp

    testCase "WHY — a file with no live state is re-evaluated whole, which compiles without the open and can land" <| fun _ ->
      qualifiedAccessRoute AppPlacement.InAgentProcess (declsOf qualifiedSource)
      |> Expect.equal "whole file" QualifiedAccessRoute.ReevaluateWholeFile

    testCase "WHY — a file with a let mutable is not re-evaluated, because that would replace the live value with the initializer" <| fun _ ->
      let withState = declsOf (qualifiedSource + "\nlet mutable count = 0\n")
      qualifiedAccessRoute AppPlacement.InAgentProcess withState
      |> Expect.equal "state untouched" QualifiedAccessRoute.LeaveStateUntouched
  ]

/// A file module and up to three nested modules, each with or without the attribute, and one function in the deepest.
let private genNesting : Gen<bool * bool list> =
  gen {
    let! top = Gen.elements [ true; false ]
    let! depth = Gen.choose (0, 3)
    let! nested = Gen.listOfLength depth (Gen.elements [ true; false ])
    return top, nested
  }

let private sourceOf (top: bool, nested: bool list) =
  let attribute strict indent =
    match strict with
    | true -> [ indent + "[<RequireQualifiedAccess>]" ]
    | false -> []
  let modules =
    nested
    |> List.mapi (fun i strict ->
      let indent = String.replicate i "  "
      attribute strict indent @ [ sprintf "%smodule N%d =" indent (i + 1) ])
    |> List.concat
  let body = sprintf "%slet greet () = \"v2\"" (String.replicate nested.Length "  ")
  attribute top "" @ [ "module Repro.Top"; "" ] @ modules @ [ body; "" ] |> String.concat "\n"

let private strictPaths (top: bool, nested: bool list) =
  let paths = [ "Repro"; "Top" ] :: (nested |> List.mapi (fun i _ -> [ "Repro"; "Top" ] @ [ for n in 1 .. i + 1 -> sprintf "N%d" n ]))
  List.zip (top :: nested) paths |> List.filter fst |> List.map snd

[<Tests>]
let propertyTests =
  let config = { FsCheckConfig.defaultConfig with maxTest = 200 }
  testList "no patch opens a module F# will not let it open" [
    testPropertyWithConfig config "WHY — the parser records exactly the modules carrying the attribute"
    <| Prop.forAll (Arb.fromGen genNesting) (fun nesting ->
      (declsOf (sourceOf nesting)).QualifiedAccessModules = strictPaths nesting)

    testPropertyWithConfig config "WHY — a patch is refused exactly when the function's own module is strict, and otherwise opens no strict module"
    <| Prop.forAll (Arb.fromGen genNesting) (fun ((top, nested) as nesting) ->
      let decls = declsOf (sourceOf nesting)
      let ownIsStrict = List.last (top :: nested)
      match emit decls "greet", ownIsStrict with
      | Error (PatchRefusal.QualifiedAccess _), true -> true
      | Ok patch, false ->
        let strict = strictPaths nesting |> List.map (fun p -> "open global." + String.concat "." p)
        openLines patch.Code |> List.forall (fun line -> not (List.contains line strict))
      | _ -> false)
  ]
