module SageFs.Tests.CallerPatchEmitTests

open Expecto
open Expecto.Flip
open SageFs.Features.CallerState
open SageFs.Features.CallerCheck
open SageFs.Features.ReloadPlanning
open SageFs.Middleware.CompilationContext

/// A caller saved in its OWN save has to compile against the function another file re-signed. A patch is compiled in FSI
/// as `module <path> = ...`, and each patch's module shadows the one before it, so the caller's patch cannot see the new
/// `stamp` an earlier patch defined: it binds to the compiled one, with the old signature, and does not compile (seen in a
/// real app: "This value is not a function and cannot be applied"). The caller's patch carries the definitions it depends
/// on, in the same submission, so they resolve to each other.

let private declsOf (source: string) : FileDecls =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

let private stateSource = """module Shop.State

let stamp (n: int) (suffix: string) : string = "S2-" + string n + suffix

let other (n: int) : string = "O" + string n
"""

let private pagesSource = """module Shop.Pages

open System

let stamped () : string =
  State.stamp 7 "!"

let unrelated () : string = "x"
"""

let private state = declsOf stateSource
let private pages = declsOf pagesSource

let private fn (decls: FileDecls) (name: string) : SourceDecl = decls.Decls |> List.find (fun d -> d.Name = name)

let private stampEdit : SignatureEdit =
  { Declaration = "Shop.State.stamp"; Cause = SignatureCause.ReSigned; File = "/p/State.fs" }

let private site (caller: string) : CallSite =
  { File = "/p/Pages.fs"; Line = 6; Caller = caller; Evidence = SiteEvidence.ResolvedByCompiler }

let private pendingFor (edit: SignatureEdit) (sites: CallSite list) : CallersState =
  match sites with
  | first :: rest ->
    CallerLedger.empty
    |> CallerLedger.apply (LedgerEvent.Checked [ edit, CallersCheck.Callers(first, rest) ])
    |> CallerLedger.stateOf
  | [] -> CallersState.CallersCurrent

[<Tests>]
let tests =
  testList "a caller saved on its own carries what it depends on" [

    testList "which definitions a caller's patch needs" [
      testCase "WHY — a patched declaration that holds a pending call needs the re-signed function it calls" <| fun _ ->
        dependenciesOf (pendingFor stampEdit [ site "Shop.Pages.stamped" ]) "/p/Pages.fs" [ "Shop.Pages.stamped" ]
        |> Expect.equal "stamp" [ stampEdit ]

      testCase "WHY — a pending call in a declaration this save did not patch needs nothing: it stays pending" <| fun _ ->
        dependenciesOf (pendingFor stampEdit [ site "Shop.Pages.stamped" ]) "/p/Pages.fs" [ "Shop.Pages.unrelated" ]
        |> Expect.isEmpty "the call is not in the patch"

      testCase "WHY — a pending call in another file needs nothing from this save" <| fun _ ->
        dependenciesOf (pendingFor stampEdit [ site "Shop.Pages.stamped" ]) "/p/Admin.fs" [ "Shop.Pages.stamped" ]
        |> Expect.isEmpty "a different file"

      testCase "WHY — a removed function has nothing to define, so it needs no definition" <| fun _ ->
        let removed = { stampEdit with Cause = SignatureCause.Removed }
        dependenciesOf (pendingFor removed [ site "Shop.Pages.stamped" ]) "/p/Pages.fs" [ "Shop.Pages.stamped" ]
        |> Expect.isEmpty "nothing to carry"

      testCase "WHY — a site outside any declaration is carried when its file is patched at all" <| fun _ ->
        dependenciesOf (pendingFor stampEdit [ site "" ]) "/p/Pages.fs" [ "Shop.Pages.anything" ]
        |> Expect.equal "stamp" [ stampEdit ]

      testCase "WHY — a state with nothing pending needs nothing" <| fun _ ->
        dependenciesOf CallersState.CallersCurrent "/p/Pages.fs" [ "Shop.Pages.stamped" ] |> Expect.isEmpty "nothing"

      testCase "WHY — the definition is the function's current text in its own file, found by its qualified name" <| fun _ ->
        match definitionOf state stampEdit with
        | Some d -> d.Name |> Expect.equal "stamp" "stamp"
        | None -> failtest "stamp is in State.fs"
        definitionOf state { stampEdit with Declaration = "Shop.State.gone" } |> Expect.isNone "a name the file no longer has"
    ]

    testList "the patch that carries them" [
      let emitted =
        match
          emitPatchWithDefinitions
            "/p/Pages.fs"
            pages
            [ fn pages "stamped" ]
            []
            [ { FilePath = "/p/State.fs"; Decls = state; Functions = [ fn state "stamp" ] } ]
        with
        | Ok result -> result.Code
        | Error(d, reason) -> failtestf "should emit: %s %A" d.Name reason

      testCase "WHY — both files' declarations are in ONE submission, definitions first so the caller resolves to them" <| fun _ ->
        let stampAt = emitted.IndexOf "let stamp (n: int) (suffix: string)"
        let stampedAt = emitted.IndexOf "let stamped ()"
        (stampAt >= 0 && stampedAt > stampAt) |> Expect.isTrue (sprintf "stamp before stamped in:\n%s" emitted)

      testCase "WHY — a module path the two files share is declared once: a second `module Shop =` in a submission is a duplicate" <| fun _ ->
        let headers = emitted.Split('\n') |> Array.filter (fun l -> l.Trim() = "module Shop =")
        headers.Length |> Expect.equal (sprintf "one header in:\n%s" emitted) 1

      testCase "WHY — each file's own module is opened onto the compiled one, so the rest of it still resolves" <| fun _ ->
        emitted |> Expect.stringContains "State" "open global.Shop.State"
        emitted |> Expect.stringContains "Pages" "open global.Shop.Pages"

      testCase "WHY — each declaration keeps the line directive of its own file, so a diagnostic points at the right source" <| fun _ ->
        emitted |> Expect.stringContains "State.fs" "\"/p/State.fs\""
        emitted |> Expect.stringContains "Pages.fs" "\"/p/Pages.fs\""

      testCase "WHY — the caller file's own opens are kept, and only the declarations asked for are emitted" <| fun _ ->
        emitted |> Expect.stringContains "its opens" "open System"
        (emitted.Contains "let unrelated" || emitted.Contains "let other") |> Expect.isFalse "nothing that was not asked for"

      testCase "WHY — with no definitions the patch is exactly what it always was" <| fun _ ->
        let plain = emitPatchCarrying "/p/Pages.fs" pages [ fn pages "stamped" ] []
        let same = emitPatchWithDefinitions "/p/Pages.fs" pages [ fn pages "stamped" ] [] []
        (match plain, same with
         | Ok a, Ok b -> a.Code = b.Code
         | _ -> false)
        |> Expect.isTrue "one emitter, one text"
    ]
  ]
