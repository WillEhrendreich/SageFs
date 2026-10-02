module SageFsLike.Consumer.Api

/// Uses a function only the fixture's SageFs.Core has.
let probe () : string = "consumer sees " + SageFs.FixtureOnly.stamp ()
