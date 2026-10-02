/// A handler one agent edits. The line a landing changes is the message, so two agents that change it conflict.
///
/// Each handler's function has a name of its own. The verifying session re-evaluates a landed file and re-points the compiled
/// function with a matching name, and a name two modules share (`message` in both) would be matched in both.
module CohortTrunkFixture.Alice

let aliceMessage () : string = "alice:v1"
