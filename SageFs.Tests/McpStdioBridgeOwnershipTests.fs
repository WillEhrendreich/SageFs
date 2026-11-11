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

/// The bridge (and the watchdog) started the daemon by running this process's own executable again. Under a local tool
/// (`dotnet sagefs mcp`), `dnx` or `dotnet SageFs.dll`, that executable is the `dotnet` muxer, so the start ran
/// `dotnet --mcp-port 37749`, no daemon came up, and the bridge gave up 30 probes later blaming the environment.
[<Tests>]
let spawnCommandTests =
  testList "McpBridge.daemonSpawnCommand" [
    testCase "WHY — the tool's own shim is started directly" <| fun _ ->
      SageFs.McpBridge.daemonSpawnCommand { ProcessPath = "/home/u/.dotnet/tools/sagefs"; EntryAssembly = SageFs.McpBridge.EntryAssembly.At "/home/u/.dotnet/tools/.store/sagefs/x/SageFs.dll" }
      |> Expect.equal "the shim" (SageFs.McpBridge.DaemonSpawn.Run("/home/u/.dotnet/tools/sagefs", []))

    testCase "WHY — under the dotnet muxer the entry assembly is handed to it, so the daemon is SageFs and not dotnet" <| fun _ ->
      SageFs.McpBridge.daemonSpawnCommand { ProcessPath = "/usr/share/dotnet/dotnet"; EntryAssembly = SageFs.McpBridge.EntryAssembly.At "/x/tools/net11.0/any/SageFs.dll" }
      |> Expect.equal "dotnet <dll>" (SageFs.McpBridge.DaemonSpawn.Run("/usr/share/dotnet/dotnet", [ "/x/tools/net11.0/any/SageFs.dll" ]))
      SageFs.McpBridge.daemonSpawnCommand { ProcessPath = @"C:\Program Files\dotnet\dotnet.exe"; EntryAssembly = SageFs.McpBridge.EntryAssembly.At @"C:\t\SageFs.dll" }
      |> Expect.equal "dotnet.exe <dll>" (SageFs.McpBridge.DaemonSpawn.Run(@"C:\Program Files\dotnet\dotnet.exe", [ @"C:\t\SageFs.dll" ]))

    testCase "WHY — a muxer with no entry assembly to give it is refused with a reason, which the bridge reports at once" <| fun _ ->
      match SageFs.McpBridge.daemonSpawnCommand { ProcessPath = "/usr/bin/dotnet"; EntryAssembly = SageFs.McpBridge.EntryAssembly.Unknown } with
      | SageFs.McpBridge.DaemonSpawn.MuxerWithoutEntry _ as refused ->
        SageFs.McpBridge.DaemonSpawn.describe refused |> Expect.stringContains "says the muxer is the problem" "dotnet muxer"
      | spawn -> failtestf "spawning %A would start dotnet, not the daemon" spawn
  ]
