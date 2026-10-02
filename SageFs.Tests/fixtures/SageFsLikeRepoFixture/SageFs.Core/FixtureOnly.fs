namespace SageFs

/// An API the daemon's SageFs.Core has never had.
module FixtureOnly =
  /// What the Consumer's probe prints. If a session answers with this, it compiled against and loaded
  /// the Core this repo builds.
  let stamp () : string = "from-fixture-core"
