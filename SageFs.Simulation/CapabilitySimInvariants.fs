namespace SageFs.Simulation

open System
open SageFs
open SageFs.Cohort
open SageFs.Capability
open SageFs.Simulation.CapabilitySim

/// Named invariants over a `CapabilitySim` trace. They check the REAL
/// reducer's recorded outcomes against independent arithmetic (a stack walk
/// for paths, an index into the preset chain for roles) and never call the
/// function under test to decide what it should have said.
module CapabilitySimInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: State list -> Outcome }

  let private firstViolation (messages: string seq) : Outcome =
    match Seq.tryHead messages with
    | Some message -> Outcome.Violated message
    | None -> Outcome.Holds

  /// The independent path arithmetic: the segments of a canonical path, or None when the
  /// path is rooted or climbs out of the repo.
  let private segmentsOf (raw: string) : string list option =
    let slashed = raw.Replace('\\', '/')
    if slashed.StartsWith "/" || (slashed.Length >= 2 && Char.IsLetter slashed.[0] && slashed.[1] = ':') then None
    else
      slashed.Split('/')
      |> Array.filter (fun s -> s <> "" && s <> ".")
      |> Array.fold
        (fun acc segment ->
          match acc, segment with
          | None, _ -> None
          | Some stack, ".." ->
            match stack with
            | [] -> None
            | _ :: rest -> Some rest
          | Some stack, name -> Some(name :: stack))
        (Some [])
      |> Option.map List.rev

  let private prefixSegments (prefix: ScopePrefix) : string list =
    (ScopePrefix.value prefix).Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

  let private isPrefixOf (shorter: string list) (longer: string list) =
    List.truncate shorter.Length longer = shorter

  /// The directory segments a claim covers: a file's own path, a project's directory.
  let private coveredSegments (scope: ClaimScope) : string list option =
    match scope with
    | ClaimScope.File path -> segmentsOf path
    | ClaimScope.Project path -> segmentsOf path |> Option.map (fun segments -> List.truncate (max 0 (segments.Length - 1)) segments)

  let private rankOf (preset: RolePreset) : int = RolePreset.all |> List.findIndex (fun p -> p = preset)

  /// NO-RAW-TOKEN-IN-LEDGER: a token is shown once, and no row the cohort ledger would persist
  /// holds it, in any spelling, or holds the entropy it was made from. The ledger keeps every
  /// command's entropy so that replay is deterministic, so a token minted from that entropy
  /// is a token written to disk.
  let noRawTokenInLedger : Invariant =
    { Id = "NO-RAW-TOKEN-IN-LEDGER"
      Description = "No persisted ledger row contains a raw token or the entropy it was made from."
      Check = fun states ->
        let final = List.last states
        firstViolation
          (seq {
            for KeyValue(token, raw) in final.RawTokens do
              let entropy = final.TokenEntropy.[token]
              for row in final.Ledger do
                let haystack = rowSpellings row
                match spellingsOf raw entropy |> List.tryFind (fun spelling -> haystack.Contains(spelling, StringComparison.Ordinal)) with
                | Some spelling ->
                  yield sprintf "ledger row at step %d holds token %d (as %s...)" row.Step token (spelling.Substring(0, min 8 spelling.Length))
                | None -> ()
          }) }

  /// SCOPE-NEVER-WIDENS: a token never holds more than the minter held, and never claims outside
  /// the prefix it was minted for, whatever spelling of the path it presents.
  let scopeNeverWidens : Invariant =
    { Id = "SCOPE-NEVER-WIDENS"
      Description = "An accepted mint is within its minter's grant on role, scope and expiry; an allowed claim is inside the grant's prefix."
      Check = fun states ->
        let final = List.last states
        firstViolation
          (seq {
            for mint in final.Mints do
              match mint.Outcome with
              | Ok record ->
                let granted = record.Grant
                let minter = mint.MinterGrant
                if rankOf granted.Preset > rankOf minter.Preset then
                  yield sprintf "step %d: token %d was minted as %A by a minter holding only %A" mint.AtStep mint.Token granted.Preset minter.Preset
                if not (isPrefixOf (prefixSegments minter.Scope) (prefixSegments granted.Scope)) then
                  yield sprintf "step %d: token %d was minted for '%s', outside its minter's '%s'" mint.AtStep mint.Token (ScopePrefix.value granted.Scope) (ScopePrefix.value minter.Scope)
                if granted.NotAfter > minter.NotAfter then
                  yield sprintf "step %d: token %d outlives its minter" mint.AtStep mint.Token
              | Error _ -> ()
            for claim in final.Claims do
              if claim.Allowed then
                match coveredSegments claim.Scope with
                | None -> yield sprintf "step %d: token %d was allowed to claim %A, which is not a repo-relative path" claim.AtStep claim.Token claim.Scope
                | Some covered ->
                  if not (isPrefixOf (prefixSegments claim.Grant.Scope) covered) then
                    yield sprintf "step %d: token %d (scope '%s') was allowed to claim %A" claim.AtStep claim.Token (ScopePrefix.value claim.Grant.Scope) claim.Scope
          }) }

  /// MINT-NEVER-SUBSTITUTES: only the conductor mints, and what is minted is exactly what was asked
  /// for. A request that is too wide is refused, never quietly narrowed into something else.
  let mintNeverSubstitutes : Invariant =
    { Id = "MINT-NEVER-SUBSTITUTES"
      Description = "An accepted mint is by a conductor and records exactly the grant requested."
      Check = fun states ->
        let final = List.last states
        firstViolation
          (seq {
            for mint in final.Mints do
              match mint.Outcome with
              | Ok record ->
                match mint.Minter with
                | MinterKind.PlainMember
                | MinterKind.Anonymous -> yield sprintf "step %d: %A minted token %d" mint.AtStep mint.Minter mint.Token
                | MinterKind.ConductorTop
                | MinterKind.ConductorNarrow -> ()
                if record.Grant <> mint.Requested then
                  yield sprintf "step %d: token %d was asked for %A and minted as %A" mint.AtStep mint.Token mint.Requested record.Grant
              | Error _ -> ()
          }) }

  /// REVOKED-STAYS-REVOKED: after a revoke is accepted, the token is refused for ever, and its
  /// hash cannot be minted again to bring it back.
  let revokedStaysRevoked : Invariant =
    { Id = "REVOKED-STAYS-REVOKED"
      Description = "After an accepted revoke, every presentation of the token is refused as revoked and no mint of it is accepted."
      Check = fun states ->
        let final = List.last states
        firstViolation
          (seq {
            for KeyValue(token, revokedAt) in final.RevokedAtStep do
              for present in final.Presents do
                if present.Token = Some token && present.AtStep > revokedAt then
                  match present.Resolution with
                  | Resolution.Refused(PresentRefusal.Revoked _) -> ()
                  | other -> yield sprintf "step %d: token %d was revoked at step %d and then presented as %A" present.AtStep token revokedAt other
              for mint in final.Mints do
                if mint.Token = token && mint.AtStep > revokedAt && Result.isOk mint.Outcome then
                  yield sprintf "step %d: token %d was revoked at step %d and then minted again" mint.AtStep token revokedAt
          }) }

  /// EXPIRY-IS-HONORED: a presentation that resolves is before the token's own expiry and within a
  /// cohort lease window of the last time the token was accepted.
  let expiryIsHonored : Invariant =
    { Id = "EXPIRY-IS-HONORED"
      Description = "A token resolves only before its NotAfter and within a lease window of its last use."
      Check = fun states ->
        let final = List.last states
        firstViolation
          (seq {
            for present in final.Presents do
              match present.Resolution with
              | Resolution.AsToken record ->
                if present.At >= record.Grant.NotAfter then
                  yield sprintf "step %d: a token that expired at %O resolved at %O" present.AtStep record.Grant.NotAfter present.At
                match present.LastAcceptedBefore with
                | Some last when present.At - last >= Cohort.leaseWindow ->
                  yield sprintf "step %d: a token last used at %O resolved at %O, a whole lease window later" present.AtStep last present.At
                | Some _
                | None -> ()
              | Resolution.Refused _
              | Resolution.AsConnection -> ()
          }) }

  /// UNKNOWN-TOKEN-NEVER-RESOLVES: a token nobody minted is refused. It never becomes the
  /// connection's own identity, which would turn a bad token into a silent downgrade.
  let unknownTokenNeverResolves : Invariant =
    { Id = "UNKNOWN-TOKEN-NEVER-RESOLVES"
      Description = "A presented token nobody minted is Refused Unknown, never a token identity and never the connection."
      Check = fun states ->
        let final = List.last states
        firstViolation
          (seq {
            for present in final.Presents do
              if present.Token.IsNone then
                match present.Resolution with
                | Resolution.Refused PresentRefusal.Unknown -> ()
                | other -> yield sprintf "step %d: an unknown token resolved as %A" present.AtStep other
          }) }

  let all : Invariant list =
    [ noRawTokenInLedger
      scopeNeverWidens
      mintNeverSubstitutes
      revokedStaysRevoked
      expiryIsHonored
      unknownTokenNeverResolves ]

  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun invariant ->
      match invariant.Check states with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some(invariant.Id, message))
