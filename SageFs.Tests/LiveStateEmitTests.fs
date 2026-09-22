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

    testCase "WHY — LiveStateEmit.parseProbe — reads the probe's answer out of FSI's echo, because that echo is the only thing that comes back from the isolated host" <| fun _ ->
      let echo (text: string) =
        sprintf "val it: string = \"SAGEFS_LIVE_STATE:%s\"" (System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes text))
      echo "keeps\n13" |> parseProbe |> Expect.equal "a kept value" (Ok(ProbeReading.Keeps "13"))
      echo "retyped\nInt32\nString" |> parseProbe |> Expect.equal "a retype" (Ok(ProbeReading.Retyped("Int32", "String")))
      "val it: unit = ()" |> parseProbe |> Result.isError |> Expect.isTrue "no marker is an error, never a guess"

    testCase "WHY — LiveStateEmit.probeCode — defines the new initializer as a function and never calls it, because a probe must not run it (rule 3)" <| fun _ ->
      let source = "module M\n\nlet mutable tuned = startCounting ()\n"
      let decls = match extractDecls source with Ok d -> d | Error e -> failtestf "%s" e
      match probeCode decls (declOf source "tuned") with
      | Error reason -> failtestf "expected probe code, got %s" reason
      | Ok code ->
        code |> Expect.stringContains "the initializer is wrapped in a function" "let __sagefsInit_tuned () ="
        code.Contains "__sagefsInit_tuned ()\n" |> Expect.isFalse "and nothing calls it"
        code.Contains "SetValue" |> Expect.isFalse "a probe never writes the app's field"
  ]

[<Tests>]
let keptOutcomeTests =
  let kept : SageFs.Features.ReloadOutcome.KeptValue =
    { Binding = "M.tuned"; KeptValue = "13"; NewInitializer = "25" }
  testList "ReloadOutcome kept live state" [
    testCase "WHY — ReloadOutcome.withKept — a save that only kept state changed nothing the app serves, so it never refreshes the page" <| fun _ ->
      let outcome =
        SageFs.Features.ReloadOutcome.ReloadOutcome.ofPatchCounts 0 0 []
        |> SageFs.Features.ReloadOutcome.ReloadOutcome.withKept [ kept ]
      outcome |> Expect.equal "kept, nothing patched" (SageFs.Features.ReloadOutcome.ReloadOutcome.KeptLiveState(0, 1, kept, []))
      SageFs.Features.ReloadOutcome.ReloadOutcome.shouldRefreshBrowser outcome |> Expect.isFalse "no refresh"
      SageFs.Features.ReloadBroadcast.eventOf outcome
      |> SageFs.DevReload.DevReloadEvent.payloadJson
      |> Expect.stringContains "the payload names what it kept" "\"kept\":[{\"binding\":\"M.tuned\",\"keptValue\":\"13\",\"newInitializer\":\"25\"}]"

    testCase "WHY — ReloadOutcome.withKept — a patch that landed next to a kept value still refreshes, and still says what it kept" <| fun _ ->
      let outcome =
        SageFs.Features.ReloadOutcome.ReloadOutcome.ofPatchCounts 1 1 []
        |> SageFs.Features.ReloadOutcome.ReloadOutcome.withKept [ kept ]
      outcome |> Expect.equal "patched and kept" (SageFs.Features.ReloadOutcome.ReloadOutcome.KeptLiveState(1, 2, kept, []))
      SageFs.Features.ReloadOutcome.ReloadOutcome.shouldRefreshBrowser outcome |> Expect.isTrue "the patch refreshes"
      SageFs.Features.ReloadOutcome.ReloadOutcome.describeForUser outcome
      |> Expect.stringContains "the notice carries the kept value and the pending initializer" "kept 'M.tuned' = 13 (your new initializer 25 applies when you reset it)"

    testCase "WHY — ReloadOutcome.withKept — a restart has nothing to keep, so kept values never dress up a restart as something gentler" <| fun _ ->
      let restart = SageFs.Features.ReloadOutcome.ReloadOutcome.RestartRequired [ SageFs.Features.ReloadOutcome.RestartReason.SignatureChanged "f" ]
      restart
      |> SageFs.Features.ReloadOutcome.ReloadOutcome.withKept [ kept ]
      |> Expect.equal "unchanged" restart
  ]
