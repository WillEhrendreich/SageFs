/// What a lease decision looks like on the wire, for the MCP lease tools and the HTTP lease endpoint.
///
/// The decision tokens the guard hook and `scripts/local-gate` already read keep their meaning:
/// `granted`, `wait` and `refused`. A holder that asks again for the lease it holds is `granted`
/// too (it does hold it), with `grant: already_held` and the same lease id, so a caller that
/// proceeds on `granted` keeps working. Everything an agent needs to act on a wait or a refusal is
/// in the JSON as data and in `reason` as words: who holds the pool, what kind of work, when it
/// was granted and when it lapses, the caller's place in line, and what to do.
module SageFs.McpLeaseWire

open System
open SageFs.ExpensiveWorkLease

let private holderJson (holder: Holder) =
  {| agentName = Holder.agentLabel holder
     connection = holder.Connection
     workingDirectory = Holder.directoryLabel holder |}

/// A lease as a stranger sees it: who, what, when. No lease id, which is the capability to release it.
let private blockerJson (now: DateTimeOffset) (lease: ActiveLease) =
  {| agentName = Holder.agentLabel lease.Holder
     connection = lease.Holder.Connection
     expiresAt = lease.ExpiresAt
     expiresInSeconds = int (Math.Ceiling (lease.ExpiresAt - now).TotalSeconds)
     grantedAt = lease.GrantedAt
     kind = Kind.toToken lease.Kind
     workingDirectory = Holder.directoryLabel lease.Holder |}

let private askJson (request: QueuedRequest) =
  {| agentName = Holder.agentLabel request.Holder
     askedAt = request.FirstAskedAt
     connection = request.Holder.Connection
     kind = Kind.toToken request.Kind
     workingDirectory = Holder.directoryLabel request.Holder |}

/// The three answers the guard hook and `scripts/local-gate` read.
[<RequireQualifiedAccess>]
type private Verdict =
  | Granted
  | Wait
  | Refused

let private decisionToken =
  function
  | Verdict.Granted -> "granted"
  | Verdict.Wait -> "wait"
  | Verdict.Refused -> "refused"

/// How a `granted` came about: a lease taken now, or the one the holder already had.
[<RequireQualifiedAccess>]
type private Grant =
  | New
  | AlreadyHeld

let private grantToken =
  function
  | Grant.New -> "new"
  | Grant.AlreadyHeld -> "already_held"

/// The decision `holder` got for `kind`, at `now`, as JSON text.
let decisionJson (now: DateTimeOffset) (holder: Holder) (kind: Kind) (decision: Decision) : string =
  let kindName = Kind.toToken kind
  let reason = explain now decision
  match decision with
  | Decision.Granted(leaseId, expiresAt) ->
    SageFs.Json.serialize SageFs.Json.standard
      {| decision = decisionToken Verdict.Granted
         expiresAt = expiresAt
         grant = grantToken Grant.New
         heldBy = holderJson holder
         kind = kindName
         leaseId = LeaseId.value leaseId |}
  | Decision.AlreadyHeld lease ->
    SageFs.Json.serialize SageFs.Json.standard
      {| decision = decisionToken Verdict.Granted
         expiresAt = lease.ExpiresAt
         grant = grantToken Grant.AlreadyHeld
         grantedAt = lease.GrantedAt
         heldBy = holderJson lease.Holder
         kind = kindName
         leaseId = LeaseId.value lease.Id
         reason = reason |}
  | Decision.Queued waiting ->
    SageFs.Json.serialize SageFs.Json.standard
      {| ahead = waiting.Ahead |> List.map askJson
         capacity = waiting.Cap
         decision = decisionToken Verdict.Wait
         heldBy = waiting.Holding |> List.map (blockerJson now)
         kind = kindName
         position = waiting.Position
         pressure = SageFs.MemoryPressure.describe waiting.Pressure
         reason = reason
         retryAfterSeconds = waiting.RetryAfter.TotalSeconds |}
  | Decision.Refused(Refusal.HoldsOtherKind(held, _)) ->
    SageFs.Json.serialize SageFs.Json.standard
      {| decision = decisionToken Verdict.Refused
         heldLease =
           {| expiresAt = held.ExpiresAt
              expiresInSeconds = int (Math.Ceiling (held.ExpiresAt - now).TotalSeconds)
              grantedAt = held.GrantedAt
              heldBy = holderJson held.Holder
              kind = Kind.toToken held.Kind
              leaseId = LeaseId.value held.Id |}
         kind = kindName
         reason = reason |}
  | Decision.Refused(Refusal.OtherKindQueued(queued, _)) ->
    SageFs.Json.serialize SageFs.Json.standard
      {| decision = decisionToken Verdict.Refused
         kind = kindName
         queuedKind = Kind.toToken queued
         reason = reason |}
