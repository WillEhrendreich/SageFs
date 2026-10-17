// Two copies of the same test, the way an FSI session holds one after the file is evaluated twice: each evaluation is
// wrapped in its own incrementing `FSI_NNNN` container, and the wrapper is stripped from the test's name, so both copies
// have the same name and the same id. The later copy is the one that was evaluated last.

namespace FSI_0001

open System

[<AttributeUsage(AttributeTargets.Method)>]
type RedefinedFactAttribute() =
  inherit Attribute()

type RedefinedAcrossEvals() =
  /// The earlier evaluation: this version of the test passes.
  [<RedefinedFact>]
  member _.SameTest() = ()

namespace FSI_0002

open System

type RedefinedAcrossEvals() =
  /// The later evaluation: this version of the test fails, so which copy a run uses can be seen.
  [<FSI_0001.RedefinedFact>]
  member _.SameTest() = failwith "the later evaluation"
