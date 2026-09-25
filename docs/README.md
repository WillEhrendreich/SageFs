# SageFs Documentation

Here's where everything lives. If you're just trying to get running, start at the top and work down.
Everything else is reference material you can come back to when you need it.

## Start here
- **[Get Started](../Readme.md#get-started)**: install, check your environment, start the daemon, connect an editor
- **[Using SageFs with AI agents](agents.md)**: install the SageFs skill, so your agent uses the REPL instead of rebuilding, and pull it back when it drifts
- **[Workflow Modes](workflow-modes.md)**: REPL, Live Testing, and Hot Reload: when to use which, and how the Live Testing *workflow* differs from the live-testing *toggle*
- **[What you get in each editor](../Readme.md#what-you-get-in-each-editor)**: VS Code, Neovim, the web dashboard, and MCP
- **[Gutter icons](../Readme.md#-gutter-icons)**: what the colored markers mean
- **[Coming from another language](../Readme.md#coming-from-another-language)**: Python, Jupyter, C#, Java, JS/TS, Rust, or F# koans

## Feature deep dives
- **[Hot Reload](hot-reload.md)**: file watch → FSI eval → Harmony patch → browser refresh, and its current limits
- **[Live Testing As You Type](live-testing-as-you-type.md)**: the three-speed feedback pipeline
- **[Multi-Session](multi-session.md)**: one daemon, many isolated worker processes
- **[Session Isolation](session-isolation.md)**: how sessions stay out of each other's way
- **[Why F#?](why-fsharp.md)**: language rationale, from the person who had to live with the decision

## Reference
- **[Can I use SageFs with…?](ecosystem-compatibility.md)**: Falco, Giraffe, Saturn, Oxpecker, plain ASP.NET, Fable/SAFE, React/Vue/Angular, Native AOT, .NET Framework
- **[Feature Matrix](FEATURE_MATRIX.md)**: capabilities across VS Code, Neovim, the web dashboard, and MCP
- **[MCP Tools](mcp-tools.md)**: the 60 affordance-gated tools and per-client config
- **[SSE Events](sse-events.md)**: wire format for the events editors consume
- **[Binary Format Spec](binary-format-spec.md)**: the `.sagefs` and `.sagetc` persistence formats
- **[Binary Format Benchmarks](binary-format-benchmarks.md)**: serialization performance data
- **[System Architecture](architecture.md)**: daemon, workers, dashboard, and the MCP surface
- **[Troubleshooting](TROUBLESHOOTING.md)**: first-run issues, runtime problems, platform fixes

## For contributors
- **[Contributing Guide](../CONTRIBUTING.md)**: development workflow, testing, PRs
- **[Architecture Decision Records](architecture-decisions.md)**: persistence, typed errors, MCP, module composition, and superseded frontend decisions
- **[Live Testing Guide](LIVE_TESTING_GUIDE.md)**: implementation details of the test pipeline
- **[Features Survey](FEATURES_SURVEY.md)**: module inventory

Finding something here that's wrong or out of date? Open an issue, or a PR. I'd rather know.
