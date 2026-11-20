/// The CI switch for the supervisor's session-admission refusal. Pure: parse the env spelling and
/// filter the decision's actions. Nothing here reads a real environment or touches a daemon.
module SageFs.Tests.MemoryAdmissionTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.MemorySupervisor

let private decision (actions: MemorySupervisor.ShedAction list) =
  { Level = MemoryPressure.Critical
    Actions = actions
    Reason = Some "machine low on memory" }

let private reap = MemorySupervisor.ShedAction.ReapDeadSessions [ "dead" ]
let private stop = MemorySupervisor.ShedAction.StopIdleSessions [ "idle" ]
let private refuse = MemorySupervisor.ShedAction.RefuseNewSessions "refused"

[<Tests>]
let memoryAdmissionTests =
  testList "MemoryAdmission — the CI admission switch" [

    testList "parse — env spellings" [
      testCase "WHY — unset is protected, so a user's daemon is never silently opened" <| fun _ ->
        MemoryAdmission.parse null |> Expect.equal "default" MemoryAdmission.Mode.Protected

      testCase "WHY — off/0/false open admission, the spellings the gate could use" <| fun _ ->
        for value in [ "off"; "0"; "false"; " OFF "; "False" ] do
          MemoryAdmission.parse value |> Expect.equal (sprintf "%s opens" value) MemoryAdmission.Mode.Unrestricted

      testCase "WHY — on/1/true keep the protected default" <| fun _ ->
        for value in [ "on"; "1"; "true"; " ON " ] do
          MemoryAdmission.parse value |> Expect.equal (sprintf "%s protects" value) MemoryAdmission.Mode.Protected

      testCase "WHY — an unknown spelling falls back to protected, never opens on a typo" <| fun _ ->
        for value in [ "wat"; ""; "yes"; "disable" ] do
          MemoryAdmission.parse value |> Expect.equal (sprintf "%s is a typo" value) MemoryAdmission.Mode.Protected

      testCase "WHY — the gate's literal matches the module's name, so the CI switch actually reaches test daemons" <| fun _ ->
        // ci-pipeline.fsx runs standalone and cannot reference SageFs.Core, so it spells the variable
        // literally. This test is the one place that sees both and fails if they drift apart.
        let root = RepoPaths.repoPath [| "ci-pipeline.fsx" |]
        let script = System.IO.File.ReadAllText root
        script.Contains(sprintf "\"%s\", \"off\"" MemoryAdmission.environmentVariable)
        |> Expect.isTrue "the gate sets the module's variable to off for every tier"
    ]

    testList "apply — filter the decision's actions only" [
      testCase "WHY — protected leaves every action as decided" <| fun _ ->
        let d = decision [ reap; stop; refuse ]
        MemoryAdmission.apply MemoryAdmission.Mode.Protected d
        |> Expect.equal "unchanged" d

      testCase "WHY — unrestricted drops only the refusal, keeping the memory-freeing actions" <| fun _ ->
        MemoryAdmission.apply MemoryAdmission.Mode.Unrestricted (decision [ reap; stop; refuse ])
        |> fun d -> d.Actions
        |> Expect.equal "reap and stop survive" [ reap; stop ]

      testCase "WHY — unrestricted with no refusal changes nothing" <| fun _ ->
        let d = decision [ reap; stop ]
        MemoryAdmission.apply MemoryAdmission.Mode.Unrestricted d
        |> Expect.equal "unchanged" d

      testCase "WHY — the pressure level and reason survive: this is an action filter, not a new decision" <| fun _ ->
        let d = decision [ refuse ]
        let applied = MemoryAdmission.apply MemoryAdmission.Mode.Unrestricted d
        applied.Level |> Expect.equal "level is still the machine's" MemoryPressure.Critical
        applied.Reason |> Expect.equal "reason is still the evidence" (Some "machine low on memory")
    ]
  ]
