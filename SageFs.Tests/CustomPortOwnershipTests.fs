module SageFs.Tests.CustomPortOwnershipTests

open System
open Expecto
open Expecto.Flip

// A daemon on a non-default port is, by construction, somebody's temporary
// daemon — a test, an agent, a demo. The user's own long-lived daemon always
// runs on the default port. Tonight a custom-port daemon (47001) outlived
// whatever spawned it and nothing required it to say who owned it or when
// to give up. `Program.decideCustomPortOwnership` is the pure gate: a
// non-default port needs --owner-pid or --ttl; the default port is never
// gated, no matter what. Nothing here starts a real daemon.

let private defaultPort = 37749
let private customPort = 47001

[<Tests>]
let customPortOwnershipTests =
  testList "Program.decideCustomPortOwnership" [

    test "the default port is always allowed, with no owner and no ttl" {
      Program.decideCustomPortOwnership defaultPort defaultPort None None
      |> Expect.equal "default port needs nothing" Program.CustomPortOwnershipDecision.Allowed
    }

    test "WHY — decideCustomPortOwnership — a custom port with neither --owner-pid nor --ttl is refused because an unowned custom-port daemon is exactly the kind that outlives its spawner" {
      match Program.decideCustomPortOwnership defaultPort customPort None None with
      | Program.CustomPortOwnershipDecision.Refused message ->
        message |> Expect.stringContains "names --owner-pid" "--owner-pid"
        message |> Expect.stringContains "names --ttl" "--ttl"
        message |> Expect.stringContains "names the offending port" (string customPort)
      | Program.CustomPortOwnershipDecision.Allowed ->
        failtest "a custom port with no owner and no ttl must be refused"
    }

    test "a custom port with --owner-pid is allowed" {
      Program.decideCustomPortOwnership defaultPort customPort (Some 12345) None
      |> Expect.equal "owner-pid is enough" Program.CustomPortOwnershipDecision.Allowed
    }

    test "a custom port with --ttl is allowed" {
      Program.decideCustomPortOwnership defaultPort customPort None (Some (TimeSpan.FromMinutes 30.0))
      |> Expect.equal "a ttl is enough" Program.CustomPortOwnershipDecision.Allowed
    }

    test "a custom port with both is allowed" {
      Program.decideCustomPortOwnership defaultPort customPort (Some 12345) (Some (TimeSpan.FromMinutes 30.0))
      |> Expect.equal "either is enough, both is fine" Program.CustomPortOwnershipDecision.Allowed
    }
  ]
