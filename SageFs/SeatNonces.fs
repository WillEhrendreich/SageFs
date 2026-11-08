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

  /// RED stub: issues nothing real and accepts everything.
  type Store(clock: unit -> DateTime, lifetime: TimeSpan, capacity: int) =
    member _.Issue(memberName: string) : string = ""
    member _.Redeem(nonce: string, memberName: string) : Result<unit, NonceRefusal> = Result.Ok()
    member _.Outstanding : int = 0

  /// How many tickets may be outstanding at once. A render issues one per present member and reuses a good one, so this is
  /// a bound on memory and not a number a page reaches.
  let capacity = 256

  /// The daemon's one store.
  let shared = Store((fun () -> DateTime.UtcNow), Timeouts.dashboardSeatNonceLifetime, capacity)
