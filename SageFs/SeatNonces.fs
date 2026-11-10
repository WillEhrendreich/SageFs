namespace SageFs.Server

open System
open System.Collections.Generic
open SageFs

/// The one-use tickets the dashboard's Seat button carries.
///
/// The Seat route is open to any process that can reach the dashboard's port, because the dashboard has no credential
/// (the operator credential is for `/api/cohort/seat`). So the route cannot know a person clicked, and a header the browser
/// sets proves nothing against a process that sets the same header. What a ticket proves is smaller and true: the request
/// names a button THIS daemon rendered, for that member, not long ago, and nobody has used that button yet. A request that
/// is forged blind, replayed, or aimed at another member's button is refused. A process that can read the page can read a
/// ticket out of it, and the docs say so.
///
/// A ticket is held here, server-side, with the member it was issued for and when it stops being good. It is removed the
/// first time anyone presents it, whether or not the presentation was right, so a guess costs the guesser the ticket.
module SeatNonces =

  /// Why a presented ticket was not good. Closed, with one `describe`.
  [<RequireQualifiedAccess>]
  type NonceRefusal =
    /// No ticket was presented.
    | Missing
    /// Nobody issued it, or it was already used.
    | NotIssuedOrUsed
    /// It was issued for another member's button.
    | ForAnotherMember
    /// It was good once and its time is up.
    | Expired

  module NonceRefusal =
    let describe (refusal: NonceRefusal) : string =
      match refusal with
      | NonceRefusal.Missing -> "the request carries no ticket from the page's Seat button."
      | NonceRefusal.NotIssuedOrUsed -> "the ticket is not one this daemon issued, or it has been used already."
      | NonceRefusal.ForAnotherMember -> "the ticket was issued for another member's Seat button."
      | NonceRefusal.Expired -> "the ticket has expired; the page was open too long."

  /// The pair a Replace button is for: the holder it replaces and the member who would sit.
  type ReplacePair = { HolderName: string; TargetName: string }

  /// What a Replace button's ticket is issued for: the pair of the holder it replaces and the member who would sit. The holder's
  /// length leads, so no two different pairs make the same text whatever characters the names hold, and the prefix keeps it
  /// apart from a Seat button's subject (the member's own name).
  let replaceSubject (pair: ReplacePair) : string =
    sprintf "replace:%d:%s%s" pair.HolderName.Length pair.HolderName pair.TargetName

  /// One issued ticket: whose button it is for, when it was issued, when it stops being good, and its place in the order
  /// they were issued (the oldest goes first when the store is full).
  type Ticket = { Member: string; Issued: DateTime; Expires: DateTime; Order: int64 }

  /// The tickets, behind one lock. `clock` is the daemon's clock and a test's own; `lifetime` is how long a ticket is good;
  /// `capacity` bounds how many are held.
  type Store(clock: unit -> DateTime, lifetime: TimeSpan, capacity: int) =
    let gate = obj ()
    let tickets = Dictionary<string, Ticket>()
    let mutable issuedSoFar = 0L

    let fresh () : string =
      Security.Cryptography.RandomNumberGenerator.GetBytes 16
      |> Convert.ToBase64String
      |> fun text -> text.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    /// The ticket to show for `memberName`: the one already issued while it has more than half its life left, otherwise a
    /// new one (the old stays good until its own end). Expired tickets are cleared first, and the oldest go when full.
    member _.Issue(memberName: string) : string =
      lock gate (fun () ->
        let now = clock ()
        for expired in tickets |> Seq.filter (fun kv -> kv.Value.Expires <= now) |> Seq.map (fun kv -> kv.Key) |> List.ofSeq do
          tickets.Remove expired |> ignore
        let halfLife = lifetime / 2.0
        let reusable =
          tickets
          |> Seq.filter (fun kv -> kv.Value.Member = memberName && now - kv.Value.Issued < halfLife)
          |> Seq.sortByDescending (fun kv -> kv.Value.Order)
          |> Seq.tryHead
        match reusable with
        | Some kv -> kv.Key
        | None ->
          let nonce = fresh ()
          issuedSoFar <- issuedSoFar + 1L
          tickets.[nonce] <- { Member = memberName; Issued = now; Expires = now + lifetime; Order = issuedSoFar }
          while tickets.Count > capacity do
            let oldest = tickets |> Seq.minBy (fun kv -> kv.Value.Order)
            tickets.Remove oldest.Key |> ignore
          nonce)

    /// Spend a ticket. The presenting spends it whether or not it was right, so a guess costs the guesser the ticket.
    member _.Redeem(nonce: string, memberName: string) : Result<unit, NonceRefusal> =
      lock gate (fun () ->
        match String.IsNullOrWhiteSpace nonce with
        | true -> Result.Error NonceRefusal.Missing
        | false ->
          match tickets.TryGetValue nonce with
          | false, _ -> Result.Error NonceRefusal.NotIssuedOrUsed
          | true, ticket ->
            tickets.Remove nonce |> ignore
            match clock () >= ticket.Expires, ticket.Member = memberName with
            | true, _ -> Result.Error NonceRefusal.Expired
            | false, false -> Result.Error NonceRefusal.ForAnotherMember
            | false, true -> Result.Ok())

    /// How many tickets are held.
    member _.Outstanding : int = lock gate (fun () -> tickets.Count)

  /// How many tickets may be outstanding at once. A render issues one per present member and reuses a good one, so this is
  /// a bound on memory and not a number a page reaches.
  let capacity = 256

  /// The daemon's one store.
  let shared = Store((fun () -> DateTime.UtcNow), Timeouts.dashboardSeatNonceLifetime, capacity)
