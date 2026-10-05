/// Which panes the dashboard's bottom dock shows, decided in one place.
///
/// The bottom region used to be the Evaluate box. Will's call was that the live bindings belong there and Evaluate is a small
/// button that pops up when someone wants a quick check. So the dock's first pane is the live bindings. This module is the
/// rule for when it is shown: when the session has bindings. When it is not shown it collapses to ONE line that says why (never
/// a blank), and the user can pin it open anyway.
///
/// Pure: no Falco, no HTML. The renderer (`LiveBindingsDock`) takes the decision's answer and draws it, and every case of the
/// answer is a case of a closed union, so a new reason cannot be added without deciding how it reads.
module SageFs.Server.DockPanes

/// The panes the dock can show. One today; the dock is where the redesign's other bottom panes (app output, live testing) go.
[<RequireQualifiedAccess>]
type DockPane =
  | LiveBindings

module DockPane =
  /// Every pane, in the order the dock lists them.
  let all : DockPane list = [ DockPane.LiveBindings ]

/// Whether a session is being viewed at all.
type SessionInView =
  | NoSessionInView
  | SessionInView

/// What the session has bound, as far as the dock cares.
type BindingsPresence =
  | NoBindingsYet
  | HasBindings of count: int

module BindingsPresence =
  /// A count of zero (or a count that makes no sense) is "none yet", never a `HasBindings 0`.
  let ofCount (count: int) : BindingsPresence =
    match count > 0 with
    | true -> HasBindings count
    | false -> NoBindingsYet

/// Whether the user asked for the pane to stay open. A client signal on the page (`DockSignals.LivePanePinned`); the server
/// renders both answers and the signal picks, so GET and stream can never disagree about it.
type PanePin =
  | Unpinned
  | Pinned

type PaneFacts =
  { Session: SessionInView
    Bindings: BindingsPresence
    Pin: PanePin }

/// Why an open pane is open.
type OpenBecause =
  | SessionHasBindings of count: int
  | PinnedByUser

/// Why a collapsed pane is collapsed.
type CollapsedBecause =
  | NoSessionOpen
  | NothingBoundYet

/// Whether a collapsed pane can be pinned open. With no session there is nothing to open it on.
type PinOffer =
  | PinOffered
  | PinNotOffered

type PaneState =
  | PaneOpen of OpenBecause
  | PaneCollapsed of CollapsedBecause

module CollapsedBecause =
  /// The one line a collapsed pane shows. It says what is missing and what to do about it.
  let text (why: CollapsedBecause) : string =
    match why with
    | NoSessionOpen -> "no session is open: create or resume a session to see its bindings"
    | NothingBoundYet -> "no bindings yet: evaluate something (the evaluate button, or key e) and its values show here"

  let pin (why: CollapsedBecause) : PinOffer =
    match why with
    | NoSessionOpen -> PinNotOffered
    | NothingBoundYet -> PinOffered

module OpenBecause =
  /// What the open pane's header says about itself.
  let text (because: OpenBecause) : string =
    match because with
    | SessionHasBindings 1 -> "1 binding"
    | SessionHasBindings count -> sprintf "%d bindings" count
    | PinnedByUser -> "pinned open"

module PaneState =
  let decide (facts: PaneFacts) (pane: DockPane) : PaneState =
    match pane with
    | DockPane.LiveBindings ->
      match facts.Session, facts.Bindings, facts.Pin with
      | NoSessionInView, _, _ -> PaneCollapsed NoSessionOpen
      | SessionInView, HasBindings count, _ -> PaneOpen(SessionHasBindings count)
      | SessionInView, NoBindingsYet, Pinned -> PaneOpen PinnedByUser
      | SessionInView, NoBindingsYet, Unpinned -> PaneCollapsed NothingBoundYet

  /// The decision for every pane, in the dock's order.
  let all (facts: PaneFacts) : (DockPane * PaneState) list =
    DockPane.all |> List.map (fun pane -> pane, decide facts pane)
