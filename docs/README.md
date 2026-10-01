# SageFs Documentation

Here's where everything lives. If you're just trying to get running, start at the top and work down.
Everything else is reference material you can come back to when you need it.

## Start here
- **[What SageFs has become](progress.md)**: what you had, what you have now, and why it matters, stretch by stretch since February, each change linked to its code. The newest window is 2026-10-01 and says which entries are in a release and which are only merged
- **[Get Started](../Readme.md#get-started)**: install, check your environment, start the daemon, connect an editor
- **[Using SageFs with AI agents](agents.md)**: install the SageFs skill, so your agent uses the REPL instead of rebuilding, and pull it back when it drifts
- **[Workflow Modes](workflow-modes.md)**: REPL, Live Testing, and Hot Reload: when to use which, and how the Live Testing *workflow* differs from the live-testing *toggle*
- **[What you get in each editor](../Readme.md#what-you-get-in-each-editor)**: VS Code, Neovim, the web dashboard, and MCP
- **[Gutter icons](../Readme.md#-gutter-icons)**: what the colored markers mean
- **[Coming from another language](../Readme.md#coming-from-another-language)**: Python, Jupyter, C#, Java, JS/TS, Rust, or F# koans

## Feature deep dives
- **[How hot reload works](how-hot-reload-works.md)**: the ideas it borrows (Erlang, Clojure, Smalltalk), what F# and .NET lack by default, and a row-by-row comparison with .NET's hot reload that says where it is level, where it is behind and what the plan is
- **[How live testing works](how-live-testing-works.md)**: one keystroke, in the order the data moves, and a row-by-row comparison with Visual Studio Live Unit Testing, including the measured latency and what is still rough
- **[How SageFs opens projects plain FSI can't](how-isolation-works.md)**: why FSI fails on name-based assembly resolution, the process and closure design that avoids it, and how a project's pinned versions are adapted to or refused
- **[Hot Reload](hot-reload.md)**: file watch → FSI eval → Harmony patch → browser refresh, and its current limits
- **[Live Testing As You Type](live-testing-as-you-type.md)**: the three-speed feedback pipeline
- **[Multi-Session](multi-session.md)**: one daemon, many isolated worker processes
- **[Session Isolation](session-isolation.md)**: how sessions stay out of each other's way
- **[Why F#?](why-fsharp.md)**: language rationale, from the person who had to live with the decision

## Reference
- **[Can I use SageFs with…?](ecosystem-compatibility.md)**: Falco, Giraffe, Saturn, Oxpecker, plain ASP.NET, Fable/SAFE, React/Vue/Angular, Native AOT, .NET Framework
- **[Feature Matrix](FEATURE_MATRIX.md)**: capabilities across VS Code, Neovim, the web dashboard, and MCP
- **[MCP Tools](mcp-tools.md)**: the 63 affordance-gated tools and per-client config
- **[SSE Events](sse-events.md)**: wire format for the events editors consume
- **[Binary Format Spec](binary-format-spec.md)**: the `.sagefs` and `.sagetc` persistence formats
- **[Binary Format Benchmarks](binary-format-benchmarks.md)**: serialization performance data
- **[System Architecture](architecture.md)**: daemon, workers, dashboard, and the MCP surface
- **[Configuration](configuration.md)**: every `SAGEFS_*` environment variable, with its default and what it is for
- **[Troubleshooting](TROUBLESHOOTING.md)**: first-run issues, runtime problems, where the logs are, what Degraded means, platform fixes

## For contributors
- **[Contributing Guide](../CONTRIBUTING.md)**: development workflow, testing, PRs
- **[Decisions](decisions.md)**: things I looked at and said no to, and the design calls behind hot reload, live testing and live values, each with its evidence and what would reopen it
- **[Architecture Decision Records](architecture-decisions.md)**: persistence, typed errors, MCP, module composition, and superseded frontend decisions
- **[Live Testing Guide](LIVE_TESTING_GUIDE.md)**: implementation details of the test pipeline
- **[Features Survey](FEATURES_SURVEY.md)**: module inventory

Finding something here that's wrong or out of date? Open an issue, or a PR. I'd rather know.
