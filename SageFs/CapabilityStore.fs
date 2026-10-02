namespace SageFs

open System
open SageFs.Capability

/// A token that resolved, as the MCP edge carries it for the length of one call.
/// The grant is what the call may do; the id is the member's public name.
type ResolvedCapability = {
  Id: CapabilityId
  Hash: TokenHash
  Grant: Grant
}

/// RED STUB. The daemon's one capability table and identity policy.
type CapabilityStore(initialPolicy: IdentityPolicy) =
  let notImplemented () : 'a = raise (NotImplementedException "RED: CapabilityStore is not implemented yet")

  member _.Policy : IdentityPolicy = notImplemented ()
  member _.SetPolicy(policy: IdentityPolicy) : unit = notImplemented ()
  member _.Mint(now: DateTime, minter: Minter, requested: Grant, hash: TokenHash) : Result<CapabilityRecord, MintRefusal> = notImplemented ()
  member _.Present(now: DateTime, hash: TokenHash) : Result<ResolvedCapability, PresentRefusal> = notImplemented ()
  member _.Revoke(now: DateTime, by: Cohort.Authority<MemberTable.MemberId>, target: CapabilityId) : Result<unit, RevokeRefusal> = notImplemented ()
  member _.Records : CapabilityRecord list = notImplemented ()
