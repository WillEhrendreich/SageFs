/// Compiling stub for the VS Code client's scenarios (demo-actors-plan.md
/// §1.3/§2.1: `lt-vscode`, `repl-vscode`, `hr-dashboard-vscode-{web,raylib,
/// console}`). `Scenarios.All.fs` already references `scenarios` below —
/// Island F leaves it empty; the VsCode island fills it in.
module SageFs.Demos.Scenarios.VsCode

let scenarios: SageFs.Demos.Domain.Scenario list = []
