/// The one home for the CI memory-admission variable name. A BCL-only constant, shared by the
/// standalone ci-pipeline.fsx (which #loads this file) and SageFs.Core (which compiles it), so the
/// gate that sets the flag and the daemon that reads it cannot drift. No types, no DU — just the
/// name both processes must agree on.
module SageFs.MemoryAdmissionEnv

  /// SAGEFS_MEMORY_ADMISSION=off lets a CI test daemon admit sessions under external memory
  /// pressure; a user's daemon leaves it unset and keeps the protected default.
  let environmentVariable = "SAGEFS_MEMORY_ADMISSION"
