# SageFs Documentation

## For New Users
- **[First 5 Minutes](../Readme.md#-first-5-minutes)** — Install → Evaluate → See results
- **[Workflow Modes Guide](workflow-modes.md)** — REPL vs Live mode, when to use which, and why live testing isn't a mode
- **[Choosing a Client](../Readme.md#what-you-get-in-each-editor)** — VS Code, Neovim, Visual Studio, the web dashboard, and MCP clients
- **[Understanding the Gutter](../Readme.md#-understanding-the-gutter-icons)** — What the colored markers mean
- **[Language Migration Guides](../Readme.md#welcome-traveler----pick-your-home-language)** — Coming from Python, Jupyter, C#, Java, JS, or Rust

## Feature Deep Dives
- **[Live Testing As You Type](live-testing-as-you-type.md)** — Three-speed feedback pipeline
- **[Session Isolation](session-isolation.md)** — Multi-project, multi-session design
- **[Why F#?](why-fsharp.md)** — Language philosophy and design rationale
- **[Hot Reload](internal/HOT_RELOAD_STATUS.md)** — Method patching + browser refresh architecture

## Technical Reference
- **[Feature Matrix](FEATURE_MATRIX.md)** — Cross-editor feature comparison + architecture notes
- **[Binary Format Spec](binary-format-spec.md)** — `.sagefs` and `.sagetc` persistence format
- **[Binary Format Benchmarks](binary-format-benchmarks.md)** — Serialization performance data
- **[System Architecture](architecture-graph.html)** — Component interaction diagram
- **[SSE Events Reference](../Readme.md)** — Wire format for SSE events and connected-client behavior
- **[MCP Tools Reference](../Readme.md)** — Full tool catalog and transport guidance
- **[Troubleshooting](TROUBLESHOOTING.md)** — Common issues and fixes

## For Contributors
- **[Architecture Decision Records](architecture-decisions.md)** — Historical decisions, including superseded frontend decisions, plus persistence, typed errors, MCP, and module composition
- **[Code Reference](internal/CODE_REFERENCE.md)** — Key design patterns with examples
- **[Contributing Guide](../CONTRIBUTING.md)** — Development workflow, testing, PRs
- **[Live Testing Guide](LIVE_TESTING_GUIDE.md)** — Implementation details of the test pipeline
- **[Features Survey](FEATURES_SURVEY.md)** — Complete module inventory
