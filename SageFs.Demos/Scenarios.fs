/// The Phase-1 smoke scenario (demo-gif-plan.md §10 Phase 1): dashboard only,
/// click Quick Start, observe a session card appear. This is the smallest
/// possible real `Scenario` value — exactly the acceptance criterion asks
/// for — built the same way the worked hero example in §6.1 is: a plain F#
/// value, no strings for closed sets, one step with a caption/action/
/// expectation/dwell.
module SageFs.Demos.Scenarios

open SageFs.Demos.Domain

let helloDashboard: Scenario =
  { Id = ScenarioId.ofRaw "hello-dashboard"
    Capability = Capability.Sessions
    Client = Client.Dashboard
    App = AppKind.NoApp
    Sample = Sample.WebappDatastar
    Layout = LayoutTemplate.DashboardOnly
    Steps =
      [ { Caption = Caption.mk "1/1 · Press Quick Start"
          Action = Action.Click(Target.DashboardElement DashboardId.QuickStart)
          Expect = Expectation.PageShows(DashboardId.SessionCard, Text.mk "")
          Dwell = Dwell.medium } ]
    Cost = CostClass.web
    Masks = [] }
