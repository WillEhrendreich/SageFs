/// The roadmap's items, in the order they read on the page. Edit here, then run scripts/gen-roadmap.fsx.
/// Status is never written here. An item that names a landmark becomes Built when the landmark is in the tree.
module SageFs.RoadmapItems

open SageFs.Roadmap

let items : Item list =
  [ { Id = "agent-landings-reach-the-running-app"
      Title = "An agent's landed work reaches your running app"
      Area = Agents
      Horizon = Now
      Summary = "When an agent's change lands in the shared trunk, the app running there picks it up live with its state kept, and the reload row says how it got there."
      Arrival = NoLandmarkYet
      Links = [ "docs/how-hot-reload-works.md" ] } ]
