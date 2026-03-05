namespace SageFs

/// Machine-readable feature parity matrix across editor integrations.
/// Used for dashboards, CLI queries, and automated parity testing.
module FeatureParity =

  /// Editor/client integration targets.
  type Editor =
    | VsCode
    | Neovim
    | VisualStudio
    | Tui
    | RaylibGui
    | McpAgent

  /// Support status for a feature in an editor.
  type Status =
    | Supported
    | Partial of reason: string
    | NotSupported
    | NotApplicable

  /// A single feature capability.
  type Feature = {
    name: string
    category: string
    description: string
  }

  /// Feature support entry: which feature, in which editor, at what status.
  type ParityEntry = {
    feature: Feature
    editor: Editor
    status: Status
  }

  module Editor =
    let all = [ VsCode; Neovim; VisualStudio; Tui; RaylibGui; McpAgent ]

    let label = function
      | VsCode -> "VS Code"
      | Neovim -> "Neovim"
      | VisualStudio -> "Visual Studio"
      | Tui -> "TUI"
      | RaylibGui -> "Raylib GUI"
      | McpAgent -> "MCP Agent"

  module Feature =
    let evalCode = { name = "eval-code"; category = "Execution"; description = "Execute F# code blocks" }
    let evalFile = { name = "eval-file"; category = "Execution"; description = "Execute entire F# file" }
    let cancelEval = { name = "cancel-eval"; category = "Execution"; description = "Cancel running evaluation" }
    let inlineResults = { name = "inline-results"; category = "Display"; description = "Show eval results inline in editor" }
    let liveDiagnostics = { name = "live-diagnostics"; category = "Display"; description = "Real-time diagnostics via SSE" }
    let codeCompletion = { name = "code-completion"; category = "Intelligence"; description = "FSI-powered code completions" }
    let codeLens = { name = "code-lens"; category = "Intelligence"; description = "CodeLens annotations on code blocks" }
    let sessionCreate = { name = "session-create"; category = "Session"; description = "Create new FSI sessions" }
    let sessionSwitch = { name = "session-switch"; category = "Session"; description = "Switch between sessions" }
    let sessionStop = { name = "session-stop"; category = "Session"; description = "Stop a session" }
    let sessionReset = { name = "session-reset"; category = "Session"; description = "Soft-reset FSI session" }
    let sessionHardReset = { name = "session-hard-reset"; category = "Session"; description = "Hard-reset with rebuild" }
    let liveTesting = { name = "live-testing"; category = "Testing"; description = "Enable/disable live test execution" }
    let testGutters = { name = "test-gutters"; category = "Testing"; description = "Pass/fail gutter markers" }
    let coverageGutters = { name = "coverage-gutters"; category = "Testing"; description = "Code coverage gutter markers" }
    let testPanel = { name = "test-panel"; category = "Testing"; description = "Dedicated test results panel" }
    let testPolicy = { name = "test-policy"; category = "Testing"; description = "Run policy (every/save/demand)" }
    let testTrace = { name = "test-trace"; category = "Testing"; description = "Test cycle timing trace" }
    let typeExplorer = { name = "type-explorer"; category = "Exploration"; description = "Browse .NET types interactively" }
    let namespaceExplorer = { name = "namespace-explorer"; category = "Exploration"; description = "Browse .NET namespaces" }
    let historyBrowser = { name = "history-browser"; category = "Exploration"; description = "Browse eval history" }
    let hotReload = { name = "hot-reload"; category = "Workflow"; description = "File-save triggers re-eval" }
    let dashboard = { name = "dashboard"; category = "Workflow"; description = "Web dashboard for session overview" }

    let all = [
      evalCode; evalFile; cancelEval
      inlineResults; liveDiagnostics
      codeCompletion; codeLens
      sessionCreate; sessionSwitch; sessionStop; sessionReset; sessionHardReset
      liveTesting; testGutters; coverageGutters; testPanel; testPolicy; testTrace
      typeExplorer; namespaceExplorer; historyBrowser
      hotReload; dashboard
    ]

  /// The canonical parity matrix. Update this when features are added to any editor.
  let matrix : ParityEntry list = [
    // === Execution ===
    { feature = Feature.evalCode; editor = VsCode; status = Supported }
    { feature = Feature.evalCode; editor = Neovim; status = Supported }
    { feature = Feature.evalCode; editor = VisualStudio; status = Supported }
    { feature = Feature.evalCode; editor = Tui; status = Supported }
    { feature = Feature.evalCode; editor = RaylibGui; status = Supported }
    { feature = Feature.evalCode; editor = McpAgent; status = Supported }

    { feature = Feature.evalFile; editor = VsCode; status = Supported }
    { feature = Feature.evalFile; editor = Neovim; status = Supported }
    { feature = Feature.evalFile; editor = VisualStudio; status = Supported }
    { feature = Feature.evalFile; editor = Tui; status = NotApplicable }
    { feature = Feature.evalFile; editor = RaylibGui; status = NotApplicable }
    { feature = Feature.evalFile; editor = McpAgent; status = Supported }

    { feature = Feature.cancelEval; editor = VsCode; status = Supported }
    { feature = Feature.cancelEval; editor = Neovim; status = Supported }
    { feature = Feature.cancelEval; editor = VisualStudio; status = Supported }
    { feature = Feature.cancelEval; editor = Tui; status = Supported }
    { feature = Feature.cancelEval; editor = RaylibGui; status = Supported }
    { feature = Feature.cancelEval; editor = McpAgent; status = Supported }

    // === Display ===
    { feature = Feature.inlineResults; editor = VsCode; status = Supported }
    { feature = Feature.inlineResults; editor = Neovim; status = Supported }
    { feature = Feature.inlineResults; editor = VisualStudio; status = Supported }
    { feature = Feature.inlineResults; editor = Tui; status = Supported }
    { feature = Feature.inlineResults; editor = RaylibGui; status = Supported }
    { feature = Feature.inlineResults; editor = McpAgent; status = NotApplicable }

    { feature = Feature.liveDiagnostics; editor = VsCode; status = Supported }
    { feature = Feature.liveDiagnostics; editor = Neovim; status = Supported }
    { feature = Feature.liveDiagnostics; editor = VisualStudio; status = Supported }
    { feature = Feature.liveDiagnostics; editor = Tui; status = Supported }
    { feature = Feature.liveDiagnostics; editor = RaylibGui; status = Supported }
    { feature = Feature.liveDiagnostics; editor = McpAgent; status = Supported }

    // === Intelligence ===
    { feature = Feature.codeCompletion; editor = VsCode; status = Supported }
    { feature = Feature.codeCompletion; editor = Neovim; status = Supported }
    { feature = Feature.codeCompletion; editor = VisualStudio; status = Partial "completion provider registered" }
    { feature = Feature.codeCompletion; editor = Tui; status = Supported }
    { feature = Feature.codeCompletion; editor = RaylibGui; status = Supported }
    { feature = Feature.codeCompletion; editor = McpAgent; status = Supported }

    { feature = Feature.codeLens; editor = VsCode; status = Supported }
    { feature = Feature.codeLens; editor = Neovim; status = Supported }
    { feature = Feature.codeLens; editor = VisualStudio; status = Supported }
    { feature = Feature.codeLens; editor = Tui; status = NotApplicable }
    { feature = Feature.codeLens; editor = RaylibGui; status = NotApplicable }
    { feature = Feature.codeLens; editor = McpAgent; status = NotApplicable }

    // === Session Management ===
    { feature = Feature.sessionCreate; editor = VsCode; status = Supported }
    { feature = Feature.sessionCreate; editor = Neovim; status = Supported }
    { feature = Feature.sessionCreate; editor = VisualStudio; status = Supported }
    { feature = Feature.sessionCreate; editor = Tui; status = Supported }
    { feature = Feature.sessionCreate; editor = RaylibGui; status = Supported }
    { feature = Feature.sessionCreate; editor = McpAgent; status = Supported }

    { feature = Feature.sessionSwitch; editor = VsCode; status = Supported }
    { feature = Feature.sessionSwitch; editor = Neovim; status = Supported }
    { feature = Feature.sessionSwitch; editor = VisualStudio; status = Supported }
    { feature = Feature.sessionSwitch; editor = Tui; status = Supported }
    { feature = Feature.sessionSwitch; editor = RaylibGui; status = Supported }
    { feature = Feature.sessionSwitch; editor = McpAgent; status = Supported }

    { feature = Feature.sessionStop; editor = VsCode; status = Supported }
    { feature = Feature.sessionStop; editor = Neovim; status = Supported }
    { feature = Feature.sessionStop; editor = VisualStudio; status = Supported }
    { feature = Feature.sessionStop; editor = Tui; status = Supported }
    { feature = Feature.sessionStop; editor = RaylibGui; status = Supported }
    { feature = Feature.sessionStop; editor = McpAgent; status = Supported }

    { feature = Feature.sessionReset; editor = VsCode; status = Supported }
    { feature = Feature.sessionReset; editor = Neovim; status = Supported }
    { feature = Feature.sessionReset; editor = VisualStudio; status = Supported }
    { feature = Feature.sessionReset; editor = Tui; status = Supported }
    { feature = Feature.sessionReset; editor = RaylibGui; status = Supported }
    { feature = Feature.sessionReset; editor = McpAgent; status = Supported }

    { feature = Feature.sessionHardReset; editor = VsCode; status = Supported }
    { feature = Feature.sessionHardReset; editor = Neovim; status = Supported }
    { feature = Feature.sessionHardReset; editor = VisualStudio; status = Supported }
    { feature = Feature.sessionHardReset; editor = Tui; status = Supported }
    { feature = Feature.sessionHardReset; editor = RaylibGui; status = Supported }
    { feature = Feature.sessionHardReset; editor = McpAgent; status = Supported }

    // === Testing ===
    { feature = Feature.liveTesting; editor = VsCode; status = Supported }
    { feature = Feature.liveTesting; editor = Neovim; status = Supported }
    { feature = Feature.liveTesting; editor = VisualStudio; status = Partial "server-side ready" }
    { feature = Feature.liveTesting; editor = Tui; status = Supported }
    { feature = Feature.liveTesting; editor = RaylibGui; status = Partial "server-side ready" }
    { feature = Feature.liveTesting; editor = McpAgent; status = Supported }

    { feature = Feature.testGutters; editor = VsCode; status = Supported }
    { feature = Feature.testGutters; editor = Neovim; status = Supported }
    { feature = Feature.testGutters; editor = VisualStudio; status = Partial "server-side ready" }
    { feature = Feature.testGutters; editor = Tui; status = Supported }
    { feature = Feature.testGutters; editor = RaylibGui; status = Partial "server-side ready" }
    { feature = Feature.testGutters; editor = McpAgent; status = NotApplicable }

    { feature = Feature.coverageGutters; editor = VsCode; status = Partial "server-side ready" }
    { feature = Feature.coverageGutters; editor = Neovim; status = Supported }
    { feature = Feature.coverageGutters; editor = VisualStudio; status = Partial "server-side ready" }
    { feature = Feature.coverageGutters; editor = Tui; status = Supported }
    { feature = Feature.coverageGutters; editor = RaylibGui; status = Partial "server-side ready" }
    { feature = Feature.coverageGutters; editor = McpAgent; status = NotApplicable }

    { feature = Feature.testPanel; editor = VsCode; status = Supported }
    { feature = Feature.testPanel; editor = Neovim; status = Supported }
    { feature = Feature.testPanel; editor = VisualStudio; status = NotSupported }
    { feature = Feature.testPanel; editor = Tui; status = NotApplicable }
    { feature = Feature.testPanel; editor = RaylibGui; status = NotApplicable }
    { feature = Feature.testPanel; editor = McpAgent; status = NotApplicable }

    { feature = Feature.testPolicy; editor = VsCode; status = Supported }
    { feature = Feature.testPolicy; editor = Neovim; status = Supported }
    { feature = Feature.testPolicy; editor = VisualStudio; status = NotSupported }
    { feature = Feature.testPolicy; editor = Tui; status = NotSupported }
    { feature = Feature.testPolicy; editor = RaylibGui; status = NotSupported }
    { feature = Feature.testPolicy; editor = McpAgent; status = Supported }

    { feature = Feature.testTrace; editor = VsCode; status = Supported }
    { feature = Feature.testTrace; editor = Neovim; status = Supported }
    { feature = Feature.testTrace; editor = VisualStudio; status = NotSupported }
    { feature = Feature.testTrace; editor = Tui; status = NotSupported }
    { feature = Feature.testTrace; editor = RaylibGui; status = NotSupported }
    { feature = Feature.testTrace; editor = McpAgent; status = Supported }

    // === Exploration ===
    { feature = Feature.typeExplorer; editor = VsCode; status = Supported }
    { feature = Feature.typeExplorer; editor = Neovim; status = Supported }
    { feature = Feature.typeExplorer; editor = VisualStudio; status = NotSupported }
    { feature = Feature.typeExplorer; editor = Tui; status = NotSupported }
    { feature = Feature.typeExplorer; editor = RaylibGui; status = NotSupported }
    { feature = Feature.typeExplorer; editor = McpAgent; status = Supported }

    { feature = Feature.namespaceExplorer; editor = VsCode; status = Supported }
    { feature = Feature.namespaceExplorer; editor = Neovim; status = Supported }
    { feature = Feature.namespaceExplorer; editor = VisualStudio; status = NotSupported }
    { feature = Feature.namespaceExplorer; editor = Tui; status = NotSupported }
    { feature = Feature.namespaceExplorer; editor = RaylibGui; status = NotSupported }
    { feature = Feature.namespaceExplorer; editor = McpAgent; status = Supported }

    { feature = Feature.historyBrowser; editor = VsCode; status = Supported }
    { feature = Feature.historyBrowser; editor = Neovim; status = Supported }
    { feature = Feature.historyBrowser; editor = VisualStudio; status = NotSupported }
    { feature = Feature.historyBrowser; editor = Tui; status = NotSupported }
    { feature = Feature.historyBrowser; editor = RaylibGui; status = NotSupported }
    { feature = Feature.historyBrowser; editor = McpAgent; status = NotApplicable }

    // === Workflow ===
    { feature = Feature.hotReload; editor = VsCode; status = Supported }
    { feature = Feature.hotReload; editor = Neovim; status = Supported }
    { feature = Feature.hotReload; editor = VisualStudio; status = Supported }
    { feature = Feature.hotReload; editor = Tui; status = Supported }
    { feature = Feature.hotReload; editor = RaylibGui; status = Supported }
    { feature = Feature.hotReload; editor = McpAgent; status = NotApplicable }

    { feature = Feature.dashboard; editor = VsCode; status = Supported }
    { feature = Feature.dashboard; editor = Neovim; status = NotSupported }
    { feature = Feature.dashboard; editor = VisualStudio; status = NotSupported }
    { feature = Feature.dashboard; editor = Tui; status = NotApplicable }
    { feature = Feature.dashboard; editor = RaylibGui; status = NotApplicable }
    { feature = Feature.dashboard; editor = McpAgent; status = NotApplicable }
  ]

  /// Query: all entries for a specific editor.
  let forEditor (editor: Editor) =
    matrix |> List.filter (fun e -> e.editor = editor)

  /// Query: all entries for a specific feature.
  let forFeature (feature: Feature) =
    matrix |> List.filter (fun e -> e.feature = feature)

  /// Query: features that are Supported in one editor but NotSupported in another.
  let gaps (reference: Editor) (target: Editor) =
    let refSupported =
      forEditor reference
      |> List.choose (fun e ->
        match e.status with
        | Supported -> Some e.feature.name
        | _ -> None)
      |> Set.ofList
    forEditor target
    |> List.filter (fun e ->
      match e.status with
      | NotSupported -> refSupported |> Set.contains e.feature.name
      | Partial _ -> refSupported |> Set.contains e.feature.name
      | _ -> false)

  /// Query: features with Partial status across any editor.
  let partialFeatures () =
    matrix
    |> List.choose (fun e ->
      match e.status with
      | Partial reason -> Some (e.feature.name, Editor.label e.editor, reason)
      | _ -> None)

  /// Summary: count of Supported/Partial/NotSupported/NotApplicable per editor.
  let summary () =
    Editor.all
    |> List.map (fun ed ->
      let entries = forEditor ed
      let supported = entries |> List.filter (fun e -> match e.status with Supported -> true | _ -> false) |> List.length
      let partial = entries |> List.filter (fun e -> match e.status with Partial _ -> true | _ -> false) |> List.length
      let notSupported = entries |> List.filter (fun e -> match e.status with NotSupported -> true | _ -> false) |> List.length
      let na = entries |> List.filter (fun e -> match e.status with NotApplicable -> true | _ -> false) |> List.length
      Editor.label ed, supported, partial, notSupported, na)
