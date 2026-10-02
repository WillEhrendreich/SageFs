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

/// The daemon's one capability table and identity policy: the shell around the
/// pure reducer in `SageFs.Core/Capability.fs`. It owns the only mutable state
/// capabilities have, behind one lock, and every transition is `Capability.decide`.
///
/// In memory by design. Tokens do not survive a daemon restart, so a stolen
/// ledger or data directory holds nothing to steal, and the orchestrator mints
/// new tokens when it starts the daemon again. (The cohort ledger replays the
/// seats of old members; they lapse by the cohort's own lease.)
type CapabilityStore(initialPolicy: IdentityPolicy) =
  let gate = obj ()
  let mutable state = CapabilityState.empty
  let mutable policy = initialPolicy

  let apply (now: DateTime) (command: CapabilityCommand) =
    lock gate (fun () ->
      match decide now state command with
      | Ok(next, events) ->
        state <- next
        Ok events
      | Error refusal -> Error refusal)

  member _.Policy : IdentityPolicy = lock gate (fun () -> policy)

  member _.SetPolicy(newPolicy: IdentityPolicy) : unit = lock gate (fun () -> policy <- newPolicy)

  /// Mint under `minter`'s authority. Only the hash is passed in; the table keeps only the hash.
  member _.Mint(now: DateTime, minter: Minter, requested: Grant, hash: TokenHash) : Result<CapabilityRecord, MintRefusal> =
    lock gate (fun () ->
      match apply now (CapabilityCommand.Mint(minter, requested, hash)) with
      | Ok _ -> Ok state.Records.[hash]
      | Error(CapabilityRefusal.Mint refusal) -> Error refusal
      | Error(CapabilityRefusal.Revoke _) -> failwith "Capability.decide answered a Mint with a Revoke refusal")

  /// A call presented this token: resolve it and, if it is good, keep it alive.
  member _.Present(now: DateTime, hash: TokenHash) : Result<ResolvedCapability, PresentRefusal> =
    lock gate (fun () ->
      match resolve now state hash with
      | Error refusal -> Error refusal
      | Ok record ->
        apply now (CapabilityCommand.Touch hash) |> ignore
        Ok { Id = record.Id; Hash = hash; Grant = record.Grant })

  member _.Revoke(now: DateTime, by: Cohort.Authority<MemberTable.MemberId>, target: CapabilityId) : Result<unit, RevokeRefusal> =
    match apply now (CapabilityCommand.Revoke(by, target)) with
    | Ok _ -> Ok()
    | Error(CapabilityRefusal.Revoke refusal) -> Error refusal
    | Error(CapabilityRefusal.Mint _) -> failwith "Capability.decide answered a Revoke with a Mint refusal"

  /// Every record, for the conductor's own view. Hashes are keys inside the table and are not returned.
  member _.Records : CapabilityRecord list = lock gate (fun () -> state.Records |> Map.toList |> List.map snd)
