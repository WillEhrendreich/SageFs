/// Compiling stub for the Neovim client's scenarios (demo-actors-plan.md
/// §1.3/§2.2: `repl-neovim`, `lt-neovim`, `hr-neovim-neovim-{web,raylib,
/// console}`). `Scenarios.All.fs` already references `scenarios` below —
/// Island F leaves it empty; the Neovim island fills it in.
module SageFs.Demos.Scenarios.Neovim

let scenarios: SageFs.Demos.Domain.Scenario list = []
