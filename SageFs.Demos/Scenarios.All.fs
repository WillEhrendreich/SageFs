/// The scenario registry (Island F, demo-actors-plan.md §1.3): aggregates
/// every client's scenario list into the one `all` value `Program.fs`
/// resolves `record <scenario-id>` against. Each actor island owns and fills
/// only its own `Scenarios.<X>.scenarios` stub; after Island F, no actor
/// island ever touches this file again.
module SageFs.Demos.Scenarios.All

let all: SageFs.Demos.Domain.Scenario list =
  SageFs.Demos.Scenarios.Dashboard.dashboardScenarios
  @ SageFs.Demos.Scenarios.VsCode.scenarios
  @ SageFs.Demos.Scenarios.Neovim.scenarios
  @ SageFs.Demos.Scenarios.Agent.scenarios
  @ SageFs.Demos.Scenarios.Cohort.scenarios
