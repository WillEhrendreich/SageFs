/// The one home for the CI memory-admission variable name. A BCL-only constant, shared by the
/// standalone ci-pipeline.fsx (which #loads this file) and SageFs.Core (which compiles it), so the
/// gate that sets the flag and the daemon that reads it cannot drift. Lives inside Core so the
/// self-host fixture, which copies Core's sources, carries it. No types, no DU — just the name.
module SageFs.MemoryAdmissionEnv

  /// SAGEFS_MEMORY_ADMISSION=off lets a CI test daemon admit sessions under external memory
  /// pressure; a user's daemon leaves it unset and keeps the protected default.
  let environmentVariable = "SAGEFS_MEMORY_ADMISSION"
