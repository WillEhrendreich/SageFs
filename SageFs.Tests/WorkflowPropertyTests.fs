/// Property-based tests for the SessionWorkflow domain model.
///
/// These tests document WHY the model is correct — design invariants
/// that the type system alone cannot fully guarantee. Each property
/// answers: "What must ALWAYS be true about the relationship between
/// workflows, feedback strategies, REPL capabilities, and FSI flags?"
///
/// Muratori's filter applied: we do NOT test things the compiler already
/// guarantees (e.g., exhaustive match). We test design invariants that
/// could be accidentally violated in the update/rendering functions.
module SageFs.Tests.WorkflowPropertyTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.WorkflowTypes
open SageFs.Tests.SharedGenerators

// ── Generators ──────────────────────────────────────────────

let private genBrowserRefreshConfig =
  Gen.elements [
    BrowserRefreshConfig.defaults
    { WatchPatterns = [ "*.fs" ] }
    { WatchPatterns = [ "*.fsx"; "*.fs"; "*.html" ] }
    { WatchPatterns = [] }
  ]

let private genSessionWorkflow =
  Gen.oneof [
    Gen.constant SessionWorkflow.Interactive
    Gen.constant SessionWorkflow.LiveTesting
    genBrowserRefreshConfig |> Gen.map SessionWorkflow.HotReload
  ]

type WorkflowGenerators =
  static member SessionWorkflow () =
    Arb.fromGen genSessionWorkflow
  static member BrowserRefreshConfig () =
    Arb.fromGen genBrowserRefreshConfig

let private workflowConfig = {
  propConfig with
    arbitrary = [
      typeof<WorkflowGenerators>
    ]
}

// ── Property tests ──────────────────────────────────────────

[<Tests>]
let workflowPropertyTests =
  testList "Workflow domain model properties" [

    // --- Orthogonality invariants ---

    testList "feedback strategy is determined solely by workflow shape" [

      testPropertyWithConfig workflowConfig
        "Interactive always produces ReplDriven feedback" <|
        fun () ->
          SessionWorkflow.Interactive
          |> SessionWorkflow.feedbackStrategy
          |> (=) FeedbackStrategy.ReplDriven

      testPropertyWithConfig workflowConfig
        "HotReload always produces SaveDriven feedback" <|
        fun (cfg: BrowserRefreshConfig) ->
          SessionWorkflow.HotReload cfg
          |> SessionWorkflow.feedbackStrategy
          |> (=) (FeedbackStrategy.SaveDriven cfg)
    ]

    // --- FSI flag consistency ---

    testList "FSI flags are consistent with workflow" [

      testPropertyWithConfig workflowConfig
        "Interactive never emits --multiemit-" <|
        fun () ->
          SessionWorkflow.Interactive
          |> SessionWorkflow.fsiArgs
          |> List.contains "--multiemit-"
          |> not

      testPropertyWithConfig workflowConfig
        "HotReload always emits --multiemit-" <|
        fun (cfg: BrowserRefreshConfig) ->
          SessionWorkflow.HotReload cfg
          |> SessionWorkflow.fsiArgs
          |> List.contains "--multiemit-"

      testPropertyWithConfig workflowConfig
        "no workflow emits contradictory multiemit flags" <|
        fun (workflow: SessionWorkflow) ->
          let flags = SessionWorkflow.fsiArgs workflow
          not (
            List.contains "--multiemit-" flags
            && List.contains "--multiemit" flags
          )
    ]

    // --- REPL capability consistency ---

    testList "REPL capability is consistent with feedback strategy" [

      testPropertyWithConfig workflowConfig
        "ReplDriven feedback always yields Full REPL capability" <|
        fun (workflow: SessionWorkflow) ->
          match SessionWorkflow.feedbackStrategy workflow with
          | FeedbackStrategy.ReplDriven ->
            SessionWorkflow.replCapability workflow = ReplCapability.Full
          | FeedbackStrategy.SaveDriven _ -> true // not this case

      testPropertyWithConfig workflowConfig
        "SaveDriven feedback always yields ExpressionOnly REPL capability" <|
        fun (workflow: SessionWorkflow) ->
          match SessionWorkflow.feedbackStrategy workflow with
          | FeedbackStrategy.SaveDriven _ ->
            SessionWorkflow.replCapability workflow = ReplCapability.ExpressionOnly
          | FeedbackStrategy.ReplDriven -> true // not this case

      testPropertyWithConfig workflowConfig
        "Full REPL capability implies no --multiemit- in FSI args" <|
        fun (workflow: SessionWorkflow) ->
          match SessionWorkflow.replCapability workflow with
          | ReplCapability.Full ->
            SessionWorkflow.fsiArgs workflow
            |> List.contains "--multiemit-"
            |> not
          | ReplCapability.ExpressionOnly -> true // not this case
    ]

    // --- Label uniqueness and completeness ---

    testList "workflow labels are well-behaved" [

      testPropertyWithConfig workflowConfig
        "every workflow produces a non-empty label" <|
        fun (workflow: SessionWorkflow) ->
          SessionWorkflow.label workflow
          |> System.String.IsNullOrWhiteSpace
          |> not

      testPropertyWithConfig workflowConfig
        "different workflow shapes produce different labels" <|
        fun (cfg: BrowserRefreshConfig) ->
          let interactiveLabel =
            SessionWorkflow.label SessionWorkflow.Interactive
          let webLiveLabel =
            SessionWorkflow.label (SessionWorkflow.HotReload cfg)
          interactiveLabel <> webLiveLabel
    ]

    // --- Hot reload consistency ---

    testList "hot reload active is consistent with workflow" [

      testPropertyWithConfig workflowConfig
        "isHotReloadActive matches presence of --multiemit- flag" <|
        fun (workflow: SessionWorkflow) ->
          let hasMultiemitMinus =
            SessionWorkflow.fsiArgs workflow
            |> List.contains "--multiemit-"
          SessionWorkflow.isHotReloadActive workflow = hasMultiemitMinus
    ]

    // --- WorkflowDetection properties ---

    testList "workflow detection" [

      testCase "non-web packages produce no suggestion" <| fun _ ->
        [ "Newtonsoft.Json"; "FSharp.Core"; "Expecto" ]
        |> WorkflowDetection.suggest
        |> Expect.isNone "no suggestion for non-web project"

      testCase "empty package list produces no suggestion" <| fun _ ->
        []
        |> WorkflowDetection.suggest
        |> Expect.isNone "no suggestion for empty packages"

      testCase "Falco.Datastar triggers suggestion" <| fun _ ->
        [ "Falco"; "Falco.Datastar"; "FSharp.Core" ]
        |> WorkflowDetection.suggest
        |> Expect.isSome "should suggest for Datastar"

      testCase "Falco without Datastar triggers suggestion" <| fun _ ->
        [ "Falco"; "FSharp.Core" ]
        |> WorkflowDetection.suggest
        |> Expect.isSome "should suggest for web framework"

      testCase "Datastar suggestion takes priority over generic web" <| fun _ ->
        let result =
          [ "Falco"; "Falco.Datastar"; "Giraffe" ]
          |> WorkflowDetection.suggest
        result
        |> Expect.isSome "should produce suggestion"
        result.Value.Reason
        |> Expect.stringContains "should mention Datastar" "Datastar"

      testCase "suggested workflow is always HotReload" <| fun _ ->
        let result =
          [ "Saturn"; "FSharp.Core" ]
          |> WorkflowDetection.suggest
        match result with
        | Some s ->
          match s.SuggestedWorkflow with
          | SessionWorkflow.HotReload _ -> ()
          | SessionWorkflow.Interactive
          | SessionWorkflow.LiveTesting ->
            failtest "should suggest HotReload, not a non-hot-reload workflow"
        | None -> failtest "should produce suggestion for Saturn"

      testCase "detected packages list is accurate" <| fun _ ->
        let result =
          [ "Falco"; "Newtonsoft.Json"; "Giraffe" ]
          |> WorkflowDetection.suggest
        result
        |> Expect.isSome "should suggest"
        result.Value.DetectedPackages
        |> Expect.hasLength "should find both web packages" 2
    ]
  ]

[<Tests>]
let projectKindTests =
  testList "ProjectKind classification" [
    testCase "web frameworks classify as Web" <| fun _ ->
      ProjectKind.classify [ "Falco.Datastar"; "FSharp.Core" ] |> ProjectKind.label
      |> Expect.equal "Falco is web" "web"
      ProjectKind.classify [ "Microsoft.AspNetCore.App" ] |> ProjectKind.label
      |> Expect.equal "AspNetCore is web" "web"
      ProjectKind.classify [ "Giraffe" ] |> ProjectKind.label
      |> Expect.equal "Giraffe is web" "web"

    testCase "native game AND desktop-UI libraries classify as NativeGui" <| fun _ ->
      ProjectKind.classify [ "Raylib-cs" ] |> ProjectKind.label
      |> Expect.equal "Raylib is native-gui" "native-gui"
      ProjectKind.classify [ "SDL2-CS" ] |> ProjectKind.label
      |> Expect.equal "SDL2 is native-gui" "native-gui"
      // Desktop UI frameworks are native-GUI too (no WebApplication).
      ProjectKind.classify [ "Avalonia"; "Avalonia.Desktop"; "SkiaSharp" ] |> ProjectKind.label
      |> Expect.equal "Avalonia is native-gui" "native-gui"
      ProjectKind.classify [ "Microsoft.Maui.Controls" ] |> ProjectKind.label
      |> Expect.equal "MAUI is native-gui" "native-gui"
      ProjectKind.classify [ "Microsoft.WindowsAppSDK" ] |> ProjectKind.label
      |> Expect.equal "WinUI is native-gui" "native-gui"
      ProjectKind.classify [ "Uno.WinUI" ] |> ProjectKind.label
      |> Expect.equal "Uno is native-gui" "native-gui"
      // WPF/WinForms have NO package — they are MSBuild properties, surfaced as
      // markers by classifyProject. WPF detection must survive a Windows checkout.
      ProjectKind.classify [ "FSharp.Core"; "UseWPF" ] |> ProjectKind.label
      |> Expect.equal "WPF (UseWPF marker) is native-gui" "native-gui"
      ProjectKind.classify [ "UseWindowsForms" ] |> ProjectKind.label
      |> Expect.equal "WinForms (UseWindowsForms marker) is native-gui" "native-gui"

    testCase "everything else is Console" <| fun _ ->
      ProjectKind.classify [ "Expecto"; "FSharp.Core" ] |> ProjectKind.label
      |> Expect.equal "plain is console" "console"
      ProjectKind.classify [] |> ProjectKind.label
      |> Expect.equal "empty is console" "console"

    testCase "NativeGui wins over Web when both are present" <| fun _ ->
      // A native game that also references a web lib is still a game — native
      // windowing dominates the reload strategy.
      ProjectKind.classify [ "Raylib-cs"; "Microsoft.AspNetCore.App" ] |> ProjectKind.label
      |> Expect.equal "native-gui precedence" "native-gui"

    testCase "a Web project structurally carries a browser config" <| fun _ ->
      match ProjectKind.classify [ "Falco" ] with
      | ProjectKind.Web cfg -> cfg.WatchPatterns |> Expect.isNonEmpty "web carries a config"
      | other -> failtestf "expected Web, got %A" other
  ]

[<Tests>]
let reloadStrategyTests =
  testList "ReloadStrategy derivation" [
    testCase "Interactive never reloads, whatever the project kind" <| fun _ ->
      for kind in [ ProjectKind.Web BrowserRefreshConfig.defaults; ProjectKind.Console; ProjectKind.NativeGui ] do
        SessionWorkflow.reloadStrategy SessionWorkflow.Interactive kind
        |> Expect.equal "interactive = no reload" ReloadStrategy.NoReload

    testCase "hot-reload + web -> WebReload carrying the workflow's config" <| fun _ ->
      let cfg = { WatchPatterns = [ "*.fs" ] }
      SessionWorkflow.reloadStrategy (SessionWorkflow.HotReload cfg) (ProjectKind.Web BrowserRefreshConfig.defaults)
      |> Expect.equal "web reload keeps the workflow cfg" (ReloadStrategy.WebReload cfg)

    testCase "hot-reload + console -> method-detour only (no web machinery)" <| fun _ ->
      SessionWorkflow.reloadStrategy (SessionWorkflow.HotReload BrowserRefreshConfig.defaults) ProjectKind.Console
      |> Expect.equal "console = detour only" ReloadStrategy.MethodDetourOnly

    testCase "hot-reload + game -> game-loop reload (no WebApplication patches)" <| fun _ ->
      SessionWorkflow.reloadStrategy (SessionWorkflow.HotReload BrowserRefreshConfig.defaults) ProjectKind.NativeGui
      |> Expect.equal "game = loop reload" ReloadStrategy.NativeGuiReload
  ]

[<Tests>]
let devReloadGateTests =
  testList "web DevReload install gate" [
    testCase "a web app installs the web patch" <| fun _ ->
      ReloadStrategy.installsWebDevReload (ReloadStrategy.WebReload BrowserRefreshConfig.defaults)
      |> Expect.isTrue "web installs"

    testCase "console/method-detour installs defensively — a framework-ref web app is indistinguishable here and the patch is inert for a true console" <| fun _ ->
      ReloadStrategy.installsWebDevReload ReloadStrategy.MethodDetourOnly
      |> Expect.isTrue "console installs defensively (no regression for plain ASP.NET)"

    testCase "a native game never installs the web patch" <| fun _ ->
      ReloadStrategy.installsWebDevReload ReloadStrategy.NativeGuiReload
      |> Expect.isFalse "game skips the web patch"

    testCase "no reload installs nothing" <| fun _ ->
      ReloadStrategy.installsWebDevReload ReloadStrategy.NoReload
      |> Expect.isFalse "interactive skips"
  ]
