module SageFs.Tests.TestPortsTests

/// `TestPorts.reservePair` is the ONE allocator every real-daemon-spawning
/// harness must route through (SageFs.Tests.TestInfrastructure.TestPorts).
/// These tests prove its two modes directly: scanning inside an assigned
/// `SAGEFS_TEST_PORT_RANGE`, and falling back to the OS's ephemeral behaviour
/// when no range is assigned (a developer running one tier by hand).

open System.Net
open System.Net.Sockets
open Expecto
open Expecto.Flip

module TestPorts = SageFs.Tests.TestInfrastructure.TestPorts

let private withPortRange (range: string option) (body: unit -> 'T) : 'T =
  SageFs.Tests.TestInfrastructure.withEnvVar "SAGEFS_TEST_PORT_RANGE" range body

[<Tests>]
let testPortsTests =
  testList "TestPorts.reservePair" [

    testCase "with no assigned range, returns an adjacent free pair (the ephemeral fallback)" <| fun _ ->
      withPortRange None (fun () ->
        let mcpPort, dashboardPort = TestPorts.reservePair ()
        dashboardPort |> Expect.equal "dashboard is mcp + 1" (mcpPort + 1)
        (mcpPort, 0) |> Expect.isGreaterThan "a real port was assigned")

    testCase "scans only inside SAGEFS_TEST_PORT_RANGE when it is set" <| fun _ ->
      withPortRange (Some "25000-25100") (fun () ->
        let mcpPort, dashboardPort = TestPorts.reservePair ()
        (mcpPort, 25000) |> Expect.isGreaterThanOrEqual "the mcp port stays inside the assigned range"
        (dashboardPort, 25100) |> Expect.isLessThan "the dashboard neighbour stays inside the assigned range too")

    testCase "two calls under different assigned ranges never collide" <| fun _ ->
      // The property this whole module exists for: two "tiers" scanning
      // disjoint slices cannot land on the same pair.
      let a = withPortRange (Some "25200-25300") TestPorts.reservePair
      let b = withPortRange (Some "25300-25400") TestPorts.reservePair
      (fst a, 25300) |> Expect.isLessThan "the first tier's pair stays below the boundary"
      (fst b, 25300) |> Expect.isGreaterThanOrEqual "the second tier's pair stays at or above the boundary"

    testCase "fails with a clear message when the assigned range has no free pair" <| fun _ ->
      // A 2-port range whose only possible pair is already held.
      use occupied = new TcpListener(IPAddress.Loopback, 26001)
      occupied.Start()
      withPortRange (Some "26000-26002") (fun () ->
        Expect.throwsT<System.Exception> "no free pair in a fully-occupied range"
          (fun () -> TestPorts.reservePair () |> ignore))
  ]
