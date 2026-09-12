/// Pure content fingerprinting and freshness (demo-gif-plan.md §4.10, §1).
/// The same module runs in the recorder and in CI's `check`, so a Wave-2 test
/// must pin that CI's digest equals the recorder's for a fixed tree.
module SageFs.Demos.Fingerprint

open SageFs.Demos.Domain

/// Hashes every category in `inputs` (via git blob hashes) into one digest.
/// Coarse-but-safe: whole trees, not a curated file list, so a missing
/// category can only ever cause an extra re-record, never a false `Fresh`
/// (§4.10).
let ofInputs (inputs: Inputs) : Digest =
  failwith "TODO: Fingerprint — Wave 2"

/// Compares `inputs`' current digest against what the manifest last recorded
/// for this scenario.
let check (recorded: RecordedDigest) (inputs: Inputs) : Freshness =
  failwith "TODO: Fingerprint — Wave 2"
