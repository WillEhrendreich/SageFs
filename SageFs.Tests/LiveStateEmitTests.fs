module SageFs.Tests.LiveStateEmitTests

open Expecto
open Expecto.Flip
open SageFs.Features.ReloadPlanning
open SageFs.Features.LiveStateEmit

let private declOf (source: string) (name: string) =
  match extractDecls source with
  | Ok decls -> decls.Decls |> List.find (fun d -> d.Name = name)
  | Error reason -> failtestf "extractDecls failed: %s" reason

[<Tests>]
let liveStateEmitTests =
  testList "LiveStateEmit" [
    testCase "WHY — LiveStateEmit.typeNameCandidates — lists the module both as a namespace-qualified type and as a nested type, because FileDecls doesn't record which path segments are namespaces" <| fun _ ->
      let names = typeNameCandidates [ "StateFixture"; "State" ]
      names |> Expect.contains "module StateFixture.State" "StateFixture.State"
      names |> Expect.contains "a module nested in a module" "StateFixture+State"
      names |> Expect.contains "the Module suffix the compiler adds beside a same-named type" "StateFixture.StateModule"

    testProperty "WHY — LiveStateEmit.typeNameCandidates — every candidate ends in the module's own name, because a lookup that finds some other type would bind the patch to the wrong storage" <| fun (segments: FsCheck.NonEmptyArray<FsCheck.NonWhiteSpaceString>) ->
      let parts = segments.Get |> Array.map (fun s -> s.Get.Replace(".", "").Replace("+", "")) |> Array.filter (fun s -> s <> "") |> Array.toList
      match List.tryLast parts with
      | None -> true
      | Some last ->
        let names = typeNameCandidates parts
        names |> List.forall (fun n -> n.EndsWith last || n.EndsWith(last + "Module"))
        // Linear, so a deep path can't blow up (2^n once took 33 GB).
        && names.Length <= 2 * parts.Length

    testCase "WHY — LiveStateEmit.initializerOf — reads the right-hand side of the binding, because the stand-in types itself from it without running it" <| fun _ ->
      declOf "module M\n\nlet mutable private hits = 40 + 2\n" "hits"
      |> initializerOf
      |> Expect.equal "the text after =" (Ok "40 + 2")

    testCase "WHY — LiveStateEmit.carriedStandIn — never re-declares the binding, because a re-declaration is a fresh field holding the initializer and the live value would be gone" <| fun _ ->
      let decl = declOf "module M\n\nlet mutable private hits = 0\n" "hits"
      match carriedStandIn "  " [ "M" ] decl with
      | Error reason -> failtestf "expected a stand-in, got %s" reason
      | Ok lines ->
        let text = String.concat "\n" lines
        text.Contains "let mutable" |> Expect.isFalse "the stand-in must not declare its own storage"
        text |> Expect.stringContains "the patch resolves the name through the stand-in" "open type SageFsLive_hits"
        text |> Expect.stringContains "the property carries the binding's own name" "static member hits"

    testCase "WHY — LiveStateEmit.carriedStandIn — uses a declared annotation as the property type, because then the initializer doesn't have to appear in the patch at all" <| fun _ ->
      let decl = declOf "module M\n\nlet mutable private hits : int = startCounting ()\n" "hits"
      match carriedStandIn "" [ "M" ] decl with
      | Error reason -> failtestf "expected a stand-in, got %s" reason
      | Ok lines ->
        let text = String.concat "\n" lines
        text |> Expect.stringContains "the getter is typed by the annotation" "with get () : int ="
        text.Contains "startCounting" |> Expect.isFalse "the initializer is left out entirely"
  ]
