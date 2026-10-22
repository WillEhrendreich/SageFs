module SageFs.Tests.CustomPortOwnershipTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

// A daemon on a port that is NOT the configured one is, by construction, somebody's
// temporary daemon — a test, an agent, a demo. The configured port is the environment,
// then the port the Settings panel persisted, then the default: whatever plain `sagefs`
// binds, and the one the user's own long-lived daemon is on. Tonight a custom-port daemon
// (47001) outlived whatever spawned it and nothing required it to say who owned it or when
// to give up. `Program.decideCustomPortOwnership` is the pure gate: a port other than the
// configured one needs --owner-pid or --ttl; the configured port is never gated, whatever
// set it. Nothing here starts a real daemon.

let private defaultPort = 37749
let private customPort = 47001

/// A ttl that satisfies the gate. The decision looks at whether a ttl is present, not how long it is.
let private anyTtl = FixtureDurations.ttlLong

/// An owner that satisfies the gate. The decision looks at whether an owner is present, not who it is.
let private anyOwner : DaemonOwnership.DaemonOwner = { Pid = 12345; Fence = DaemonOwnership.OwnerFence.Unfenced }

[<Tests>]
let customPortOwnershipTests =
  testList "Program.decideCustomPortOwnership" [

    test "the default port is always allowed, with no owner and no ttl" {
      Program.decideCustomPortOwnership defaultPort defaultPort DaemonOwnership.Ownership.Unowned
      |> Expect.equal "default port needs nothing" Program.CustomPortOwnershipDecision.Allowed
    }

    test "WHY — decideCustomPortOwnership — a custom port with neither --owner-pid nor --ttl is refused because an unowned custom-port daemon is exactly the kind that outlives its spawner" {
      match Program.decideCustomPortOwnership defaultPort customPort DaemonOwnership.Ownership.Unowned with
      | Program.CustomPortOwnershipDecision.Refused message ->
        message |> Expect.stringContains "names --owner-pid" "--owner-pid"
        message |> Expect.stringContains "names --ttl" "--ttl"
        message |> Expect.stringContains "names the offending port" (string customPort)
      | Program.CustomPortOwnershipDecision.Allowed ->
        failtest "a custom port with no owner and no ttl must be refused"
    }

    test "a custom port with --owner-pid is allowed" {
      Program.decideCustomPortOwnership defaultPort customPort (DaemonOwnership.Ownership.OwnedByPid anyOwner)
      |> Expect.equal "owner-pid is enough" Program.CustomPortOwnershipDecision.Allowed
    }

    test "a custom port with --ttl is allowed" {
      Program.decideCustomPortOwnership defaultPort customPort (DaemonOwnership.Ownership.ExpiresAfter (anyTtl, DaemonOwnership.TtlSource.Explicit))
      |> Expect.equal "a ttl is enough" Program.CustomPortOwnershipDecision.Allowed
    }

    test "a custom port with both is allowed" {
      Program.decideCustomPortOwnership defaultPort customPort (DaemonOwnership.Ownership.OwnedAndExpiring (anyOwner, anyTtl))
      |> Expect.equal "either is enough, both is fine" Program.CustomPortOwnershipDecision.Allowed
    }
  ]

let private repoRoot = RepoPaths.repoPathFull [||]
let private readSource (project: string) (file: string) = File.ReadAllText(Path.Combine(repoRoot, project, file))

/// The port the configured one differs by, so a test can ask for "some other port" without
/// depending on what the machine has the panel set to.
let private someOtherPort () =
  let configured = DaemonPort.configured ()
  if configured = 65535 then configured - 1 else configured + 1

/// One rule for the port, three places that have to reach it: the daemon, the watchdog's jitter
/// seed and the stdio bridge's ownership decision. Two of them resolving it differently is a
/// client that cannot find the daemon it is talking to, which is what a second reading of the
/// rule always produces.
[<Tests>]
let daemonPortTests =
  testList "DaemonPort — one rule for the port" [

    testCase "WHY — a flag names a port only when its value parses, so a broken flag cannot hijack the configured port" <| fun _ ->
      DaemonPort.fromFlag [ "--mcp-port"; "4242"; "--foo" ] |> Expect.equal "the flag" (Some 4242)
      DaemonPort.fromFlag [ "--foo" ] |> Expect.equal "no flag" None
      DaemonPort.fromFlag [ "--mcp-port"; "nope" ] |> Expect.equal "a value that is not a number" None
      DaemonPort.fromFlag [ "--mcp-port" ] |> Expect.equal "a flag with nothing after it" None

    testCase "WHY — the command line owns the port only when it names something other than the configured one" <| fun _ ->
      DaemonPort.hasOwnPortWith 37749 [] |> Expect.isFalse "no flag means the daemon everybody shares"
      DaemonPort.hasOwnPortWith 37749 [ "--mcp-port"; "37749" ] |> Expect.isFalse "naming what is configured changes nothing"
      DaemonPort.hasOwnPortWith 37749 [ "--mcp-port"; "47001" ] |> Expect.isTrue "naming another port is a daemon of one's own"
      DaemonPort.hasOwnPortWith 40000 [ "--mcp-port"; "37749" ] |> Expect.isTrue "the hard default is not the configured port"
      DaemonPort.hasOwnPortWith 40000 [] |> Expect.isFalse "an unset flag never makes a daemon of one's own"
      DaemonPort.hasOwnPortWith 37749 [ "--mcp-port"; "nope" ] |> Expect.isFalse "a value that does not parse names nothing"

    testCase "WHY — a plain invocation is never a port of one's own, whatever the machine has configured" <| fun _ ->
      Program.explicitDaemonInvocationUsesAlternatePort Array.empty
      |> Expect.isFalse "no flag, so the running daemon is the one to attach to"
      Program.explicitDaemonInvocationUsesAlternatePort [| "--mcp-port"; string (DaemonPort.configured ()) |]
      |> Expect.isFalse "naming exactly the configured port is still the shared daemon"
      Program.explicitDaemonInvocationUsesAlternatePort [| "--mcp-port"; string (someOtherPort ()) |]
      |> Expect.isTrue "only a flag naming something else starts one of the caller's own"

    testCase "WHY — the ownership gate compares against the configured port, not the hard default, because the panel can set it" <| fun _ ->
      // The gate's baseline is what plain `sagefs` binds. Before this it was McpPortFromEnv, so a port
      // persisted in the Settings panel read as a stranger's port and `sagefs` refused to start at all.
      let configured = DaemonPort.configured ()
      Program.decideCustomPortOwnership configured configured DaemonOwnership.Ownership.Unowned
      |> Expect.equal "the configured port needs no owner" Program.CustomPortOwnershipDecision.Allowed
      let other = someOtherPort ()
      match Program.decideCustomPortOwnership configured other DaemonOwnership.Ownership.Unowned with
      | Program.CustomPortOwnershipDecision.Refused message ->
        message |> Expect.stringContains "the refusal names the port that needs an owner" (string other)
      | Program.CustomPortOwnershipDecision.Allowed ->
        failtest "a port other than the configured one must still need an owner"

    testCase "WHY — the call sites read the one rule: Program gates on it and the bridge calls its port home" <| fun _ ->
      let program = readSource "SageFs" "Program.fs"
      program
      |> Expect.stringContains "the gate is fed the configured port, not McpPortFromEnv" "decideCustomPortOwnership (DaemonPort.configured ())"
      program
      |> Expect.stringContains "the CLI resolves the port through DaemonPort" "DaemonPort.resolve (Array.toList args)"
      let bridge = readSource "SageFs" "McpStdioBridge.fs"
      bridge
      |> Expect.stringContains "the bridge's home port is the configured one, so a panel port is shared" "ownerArguments (DaemonPort.configured ())"
      let watchdog = readSource "SageFs" "WatchdogRunner.fs"
      watchdog
      |> Expect.stringContains "the watchdog reads the daemon's port from the same rule" "DaemonPort.resolve daemonArgs"
      watchdog.Contains "SageFsConfig.McpPortFromEnv"
      |> Expect.isFalse "the watchdog keeps no second reading of the rule"
  ]
