/// Regression tests for a warmup bug reported live, verbatim:
///
///   FS0039 unknown — The namespace or module 'WaitForGraph' is not defined.
///   ✖ Failed to open WaitForGraph (namespace) — Operation could not be completed due to earlier error
///   FS0039 unknown — The namespace or module 'MushroomKingdom' is not defined.
///   ✖ Failed to open MushroomKingdom (namespace) — Operation could not be completed due to earlier error
///
/// Root cause: `extractOpensFromLines` (AppState.fs) scrapes `open X` lines
/// out of project source with no awareness of scope, and warmup replayed
/// them verbatim into the FSI session. Both names are opens that are legal
/// exactly where they're written and cannot possibly resolve from a
/// separately loaded FSI session:
///   - samples/from-koans/SageFs.Samples.Koans/AboutModules.fs:21
///     `open MushroomKingdom` — a PUBLIC module NESTED inside
///     `module SageFs.Samples.Koans.AboutModules`, opened by its bare name.
///   - SageFs.Tests/ArchitectureTests.fs:764 `open WaitForGraph` — a
///     PRIVATE module NESTED inside `module SageFs.Tests.ArchitectureTests`.
///
/// The pre-existing mitigation (`internalTopLevelModuleFullNames`, roast-7
/// F7) only excludes INTERNAL TOP-LEVEL modules — it filters on
/// `not t.IsNested`, so it never even looks at either shape above. This file
/// pins the fix: `resolveWarmupOpens` folds F7's rule and the nested-module
/// rule into one decision, using real reflection (`ArchitectureTests`'s own
/// `WaitForGraph` lives in this very test assembly) wherever possible so the
/// test exercises the real bug, not a hand-built stand-in.
module SageFs.Tests.WarmupOpenReplayTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.WarmUp
open SageFs.OpenReplay
open SageFs.Tests.SharedGenerators

// ---------------------------------------------------------------------------
// A same-assembly stand-in for AboutModules.fs's shape: a PUBLIC module
// nested inside another module, opened by its bare name. The real
// samples/from-koans project isn't referenced by SageFs.Tests, so this
// mirrors its exact structure (public nested-inside-public) instead.
// ---------------------------------------------------------------------------
module PublicNestedSample =
  module MushroomKingdom =
    let mario = "Mario"

let private thisAssemblyTypes () =
  System.Reflection.Assembly.GetExecutingAssembly().GetTypes()

/// The REAL `WaitForGraph` fact — reflected out of this very test assembly,
/// off `SageFs.Tests.ArchitectureTests`'s own `module private WaitForGraph`
/// (ArchitectureTests.fs:669), the exact module the live bug report named.
let private realWaitForGraphFact () =
  thisAssemblyTypes ()
  |> reflectedModuleFacts
  |> List.tryFind (fun f -> f.BareName = "WaitForGraph")
  |> Option.defaultWith (fun () ->
    failwith "expected reflection to find SageFs.Tests.ArchitectureTests's own WaitForGraph nested module")

/// The stand-in `MushroomKingdom` fact — reflected out of `PublicNestedSample` above.
let private sampleMushroomKingdomFact () =
  thisAssemblyTypes ()
  |> reflectedModuleFacts
  |> List.tryFind (fun f -> f.BareName = "MushroomKingdom")
  |> Option.defaultWith (fun () ->
    failwith "expected reflection to find PublicNestedSample.MushroomKingdom")

[<Tests>]
let extractOpensStillCapturesBothShapesTests =
  testList "extractOpensFromLines (roast-8 shapes are scraped verbatim — dropping happens later, not here)" [
    test "WHY — a bare open of a nested PUBLIC module is captured verbatim, same as any other open" {
      [| "module SageFs.Samples.Koans.AboutModules"; ""; "module MushroomKingdom ="; "  let x = 1"; ""; "open MushroomKingdom" |]
      |> extractOpensFromLines
      |> Array.toList
      |> Expect.equal "captures the bare nested open verbatim" [ "MushroomKingdom" ]
    }

    test "WHY — a bare open of a nested PRIVATE module is captured verbatim, same as any other open" {
      [| "module SageFs.Tests.ArchitectureTests"; ""; "module private WaitForGraph ="; "  let x = 1"; ""; "open WaitForGraph" |]
      |> extractOpensFromLines
      |> Array.toList
      |> Expect.equal "captures the bare nested open verbatim" [ "WaitForGraph" ]
    }
  ]

[<Tests>]
let reflectedModuleFactsTests =
  testList "AppState.reflectedModuleFacts (roast-8)" [
    test "WHY — a PUBLIC nested module IS reported nested, and IS visible (its whole chain is public)" {
      let fact = sampleMushroomKingdomFact ()
      fact.IsNested |> Expect.isTrue "MushroomKingdom must be reported nested"
      fact.IsVisibleOutsideAssembly
      |> Expect.isTrue "public + all-public-ancestors means genuinely reachable fully-qualified"
    }

    test "WHY — the REAL ArchitectureTests.WaitForGraph is reported nested AND not visible (it's declared private)" {
      let fact = realWaitForGraphFact ()
      fact.IsNested |> Expect.isTrue "WaitForGraph must be reported nested"
      fact.IsVisibleOutsideAssembly
      |> Expect.isFalse "a private nested module is not visible outside its assembly even fully qualified"
    }
  ]

[<Tests>]
let resolveWarmupOpensRegressionTests =
  testList "AppState.resolveWarmupOpens (roast-8: warmup must not replay opens that cannot resolve)" [

    test "WHY — RED/GREEN pin: 'open MushroomKingdom' (bare, nested, public) is dropped — this is the exact live bug, samples/from-koans/.../AboutModules.fs:21" {
      let result = resolveWarmupOpens [ "MushroomKingdom" ] [ sampleMushroomKingdomFact () ]
      result.Replayable |> Expect.isEmpty "must not be replayed into FSI"
      match result.Dropped with
      | [ (name, NestedModuleBareReference _) ] -> name |> Expect.equal "dropped name" "MushroomKingdom"
      | other -> failtestf "expected exactly one NestedModuleBareReference drop, got %A" other
    }

    test "WHY — RED/GREEN pin: 'open WaitForGraph' (bare, nested, private) is dropped — this is the exact live bug, ArchitectureTests.fs:764" {
      let result = resolveWarmupOpens [ "WaitForGraph" ] [ realWaitForGraphFact () ]
      result.Replayable |> Expect.isEmpty "must not be replayed into FSI"
      match result.Dropped with
      | [ (name, _) ] -> name |> Expect.equal "dropped name" "WaitForGraph"
      | other -> failtestf "expected exactly one drop, got %A" other
    }

    test "WHY — both real modules together drop exactly the two reported names and nothing else" {
      let facts = [ sampleMushroomKingdomFact (); realWaitForGraphFact () ]
      let result = resolveWarmupOpens [ "MushroomKingdom"; "WaitForGraph"; "System.Collections.Generic" ] facts
      result.Replayable |> Expect.equal "the unrelated namespace is kept" [ "System.Collections.Generic" ]
      result.Dropped |> List.map fst |> List.sort
      |> Expect.equal "both reported names dropped" [ "MushroomKingdom"; "WaitForGraph" ]
    }

    test "WHY — a name reflection has no evidence against (e.g. a BCL/NuGet namespace) is kept — dropping is only ever a positive finding, never a default" {
      let result = resolveWarmupOpens [ "System.Collections.Generic"; "TotallyUnknownNamespace" ] [ realWaitForGraphFact () ]
      result.Dropped |> Expect.isEmpty "nothing to drop — no evidence either way"
      result.Replayable
      |> Expect.equal "both kept" [ "System.Collections.Generic"; "TotallyUnknownNamespace" ]
    }

    test "WHY — a public top-level module is still replayed (must not regress the case F7 was careful never to break)" {
      let publicTopLevel: ReflectedModuleFact =
        { BareName = "Cohort"; DottedFullName = "SageFs.Cohort"; IsNested = false; IsVisibleOutsideAssembly = true }
      let result = resolveWarmupOpens [ "SageFs.Cohort" ] [ publicTopLevel ]
      result.Replayable |> Expect.equal "kept" [ "SageFs.Cohort" ]
      result.Dropped |> Expect.isEmpty "nothing dropped"
    }

    test "WHY — an internal top-level module (roast-7 F7) is still dropped — the original rule, folded into this one decision" {
      let internalTopLevel: ReflectedModuleFact =
        { BareName = "WarmupReplayCache"; DottedFullName = "SageFs.WarmupReplayCache"; IsNested = false; IsVisibleOutsideAssembly = false }
      let result = resolveWarmupOpens [ "SageFs.WarmupReplayCache" ] [ internalTopLevel ]
      result.Replayable |> Expect.isEmpty "must not be replayed"
      match result.Dropped with
      | [ (name, InternalTopLevelModule) ] -> name |> Expect.equal "dropped name" "SageFs.WarmupReplayCache"
      | other -> failtestf "expected InternalTopLevelModule drop, got %A" other
    }

    test "WHY — a FULLY QUALIFIED reference to a PUBLIC nested module IS kept — it genuinely resolves from FSI, unlike the bare form" {
      let publicNested: ReflectedModuleFact =
        { BareName = "MushroomKingdom"
          DottedFullName = "SageFs.Samples.Koans.AboutModules.MushroomKingdom"
          IsNested = true
          IsVisibleOutsideAssembly = true }
      let result = resolveWarmupOpens [ "SageFs.Samples.Koans.AboutModules.MushroomKingdom" ] [ publicNested ]
      result.Replayable
      |> Expect.equal "kept — fully-qualified public nested opens resolve fine" [ "SageFs.Samples.Koans.AboutModules.MushroomKingdom" ]
      result.Dropped |> Expect.isEmpty "nothing dropped"
    }

    test "WHY — a fully qualified reference to a nested module that is NOT visible is still dropped, even qualified" {
      let notVisibleNested: ReflectedModuleFact =
        { BareName = "WaitForGraph"
          DottedFullName = "SageFs.Tests.ArchitectureTests.WaitForGraph"
          IsNested = true
          IsVisibleOutsideAssembly = false }
      let result = resolveWarmupOpens [ "SageFs.Tests.ArchitectureTests.WaitForGraph" ] [ notVisibleNested ]
      result.Replayable |> Expect.isEmpty "must not be replayed — genuinely inaccessible even fully qualified"
      match result.Dropped with
      | [ (name, NestedModuleNotVisible _) ] -> name |> Expect.equal "dropped name" "SageFs.Tests.ArchitectureTests.WaitForGraph"
      | other -> failtestf "expected NestedModuleNotVisible drop, got %A" other
    }
  ]

[<Tests>]
let resolveWarmupOpensPropertyTests =
  testList "AppState.resolveWarmupOpens properties" [

    testPropertyWithConfig propConfig
      "every scraped name lands in exactly one of Replayable/Dropped — never both, never neither" <|
      fun (facts: ReflectedModuleFact list) (names: string list) ->
        let result = resolveWarmupOpens names facts
        let inputNames = names |> Set.ofList
        let replayable = result.Replayable |> Set.ofList
        let dropped = result.Dropped |> List.map fst |> Set.ofList
        Set.isEmpty (Set.intersect replayable dropped)
        && Set.union replayable dropped = inputNames

    testPropertyWithConfig propConfig
      "a name is never dropped without a reflected module fact naming it — dropping is only ever a positive finding" <|
      fun (facts: ReflectedModuleFact list) (names: string list) ->
        let result = resolveWarmupOpens names facts
        result.Dropped
        |> List.forall (fun (name, _) ->
          facts |> List.exists (fun f -> f.BareName = name || f.DottedFullName = name))

    testPropertyWithConfig propConfig
      "a bare (undotted), non-empty name matching a nested module's bare name is always dropped, regardless of visibility" <|
      fun (facts: ReflectedModuleFact list) (extraNames: string list) ->
        facts
        |> List.filter (fun f -> f.IsNested && f.BareName <> "" && not (f.BareName.Contains '.'))
        |> List.forall (fun f ->
          let result = resolveWarmupOpens (f.BareName :: extraNames) facts
          not (List.contains f.BareName result.Replayable))
  ]

[<Tests>]
let warmupFcsDiagnosticFormattingTests =
  testList "WarmupFcsDiagnostic (roast-8: no fake 'unknown' placeholder, no cascade-message masking)" [

    test "WHY — a diagnostic with no file omits the location entirely instead of printing a fake 'unknown' placeholder" {
      let d: WarmupFcsDiagnostic = {
        Message = "The namespace or module 'WaitForGraph' is not defined."
        Severity = "error"; ErrorNumber = 39; FileName = None
        StartLine = 0; EndLine = 0; StartColumn = 0; EndColumn = 0
      }
      let line = WarmupFcsDiagnostic.formatLine d
      line |> Expect.equal "no location segment at all" "FS0039 — The namespace or module 'WaitForGraph' is not defined."
      line.Contains("unknown") |> Expect.isFalse "no placeholder text at all"
    }

    test "WHY — a diagnostic WITH a file still renders its location" {
      let d: WarmupFcsDiagnostic = {
        Message = "boom"; Severity = "error"; ErrorNumber = 39
        FileName = Some "Foo.fs"; StartLine = 3; EndLine = 3; StartColumn = 5; EndColumn = 9
      }
      WarmupFcsDiagnostic.formatLine d
      |> Expect.equal "location rendered" "FS0039 Foo.fs:3:5 — boom"
    }

    test "WHY — the real first-error diagnostic message is preferred over FSI's generic cascade wrapper" {
      let diagnostics : WarmupFcsDiagnostic list = [
        { Message = "The namespace or module 'WaitForGraph' is not defined."
          Severity = "error"; ErrorNumber = 39; FileName = None
          StartLine = 0; EndLine = 0; StartColumn = 0; EndColumn = 0 }
      ]
      WarmupFcsDiagnostic.pickErrorMessage "Operation could not be completed due to earlier error(s)" diagnostics
      |> Expect.equal "the real diagnostic wins" "The namespace or module 'WaitForGraph' is not defined."
    }

    test "WHY — the cascade wrapper is the honest fallback when FSI genuinely captured no diagnostics" {
      WarmupFcsDiagnostic.pickErrorMessage "Operation could not be completed due to earlier error(s)" []
      |> Expect.equal "falls back to the exception message" "Operation could not be completed due to earlier error(s)"
    }
  ]

[<Tests>]
let warmupOpenFailureSuggestedActionTests =
  testList "WarmupOpenFailure.suggestedAction (roast-8: actionable remediation)" [

    test "WHY — a discovery-time warning (missing/partial project assembly) already IS the actionable explanation, so it gets no separate suggestion" {
      let f: WarmupOpenFailure = {
        Name = WarmupOpenFailure.DiscoveryWarningName
        Kind = OpenableKind.Namespace
        ErrorMessage = "Project assembly not found: /x.dll — run 'dotnet build' first."
        Diagnostics = []; RetryCount = 1; DurationMs = 0.0
      }
      WarmupOpenFailure.suggestedAction f |> Expect.isNone "no separate suggestion needed"
    }

    test "WHY — a genuine per-name open failure gets a concrete, actionable next step naming the actual name" {
      let f: WarmupOpenFailure = {
        Name = "WaitForGraph"; Kind = OpenableKind.Namespace
        ErrorMessage = "The namespace or module 'WaitForGraph' is not defined."
        Diagnostics = []; RetryCount = 1; DurationMs = 0.0
      }
      match WarmupOpenFailure.suggestedAction f with
      | Some action ->
        action |> Expect.stringContains "names the failing open" "WaitForGraph"
        action |> Expect.stringContains "mentions the mechanical fix" "dotnet build"
      | None -> failtest "expected a concrete suggestion"
    }
  ]
