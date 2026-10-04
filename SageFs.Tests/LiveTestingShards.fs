/// The `--integration-lt` tier as shards, each on a copy of the sample of its own. (The stub: the cases in
/// LiveTestingShardsTests are red against it.)
module SageFs.Tests.LiveTestingShards

open Expecto
open SageFs.Build

let tree () : Test = failwith "LiveTestingShards.tree: not built yet"

let caseNames (_tree: Test) : string list = failwith "LiveTestingShards.caseNames: not built yet"

let share (_shard: TierPlan.Shard option) (_durations: Map<string, float>) (_tree: Test) : Test =
  failwith "LiveTestingShards.share: not built yet"

let sampleCopy (_repoRoot: string) : string = failwith "LiveTestingShards.sampleCopy: not built yet"
