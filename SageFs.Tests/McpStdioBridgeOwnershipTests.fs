/// `sagefs mcp` starts the daemon for an agent that connects over stdio. The code said
/// the default port is shared and exempt from owner tracking ("never tie its lifetime to
/// ours: other clients share it") and then passed `--owner-pid` anyway, so the daemon a
/// new user got from `claude mcp add sagefs -- sagefs mcp` was killed a few seconds after
/// their agent exited, along with its dashboard, its sessions, and any second agent that
/// had connected to it. The daemon already refuses a custom port with no owner or TTL, so
/// the bridge names itself as the owner only when it was asked for a port of its own.
module SageFs.Tests.McpStdioBridgeOwnershipTests

open Expecto
open Expecto.Flip
open SageFs.Server

[<Tests>]
let tests =
  testList "The daemon a stdio bridge starts" [
    testCase "WHY — on the default port it names no owner, because that daemon is shared and has to outlive the agent that happened to start it" <| fun _ ->
      McpStdioBridge.ownerArguments 37749 37749 4242
      |> Expect.isEmpty "nothing ties the daemon's life to the bridge"

    testCase "WHY — on a port of its own the bridge is the owner, because the daemon refuses a custom port with no owner or ttl" <| fun _ ->
      McpStdioBridge.ownerArguments 37749 38500 4242
      |> Expect.equal "the daemon dies with its spawner" [ "--owner-pid"; "4242" ]

    testCase "WHY — 'default' is whatever port the environment makes the daemon's home, so a user who moves it still gets a daemon that outlives the agent" <| fun _ ->
      McpStdioBridge.ownerArguments 38999 38999 4242
      |> Expect.isEmpty "the configured home port is exempt too"

    testProperty "WHY — a custom port always gets the owner and the default port never does, for any port and pid" <| fun (home: uint16) (asked: uint16) (pid: uint16) ->
      let args = McpStdioBridge.ownerArguments (int home) (int asked) (int pid)
      match home = asked with
      | true -> List.isEmpty args
      | false -> args = [ "--owner-pid"; string (int pid) ]
  ]
