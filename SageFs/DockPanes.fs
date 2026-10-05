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

/// The panes the dock can show. The dock is where the redesign's other bottom panes go, so this is a list
/// rather than a single case: live bindings first, the running app's stdout/stderr second.
[<RequireQualifiedAccess>]
type DockPane =
  | LiveBindings
  | AppOutput

module DockPane =
  /// Every pane, in the order the dock lists them.
  let all : DockPane list = [ DockPane.LiveBindings; DockPane.AppOutput ]

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

/// What the running app has written, as far as the dock cares. The pane's own buffer is the authority on the
/// lines; this is only the dock's question of whether the pane is worth opening.
type AppOutputPresence =
  | NoAppOutput
  | HasAppOutput of count: int

module AppOutputPresence =
  /// Zero is "nothing yet" rather than a pane holding an empty list of lines.
  let ofCount (count: int) : AppOutputPresence =
    match count > 0 with
    | true -> HasAppOutput count
    | false -> NoAppOutput

/// Whether the user asked for the pane to stay open. A client signal on the page (`DockSignals.LivePanePinned`); the server
/// renders both answers and the signal picks, so GET and stream can never disagree about it.
type PanePin =
  | Unpinned
  | Pinned

type PaneFacts =
  { Session: SessionInView
    Bindings: BindingsPresence
    AppOutput: AppOutputPresence
    Pin: PanePin }

/// Why an open pane is open.
type OpenBecause =
  | SessionHasBindings of count: int
  | SessionProducedOutput of count: int
  | PinnedByUser

/// Why a collapsed pane is collapsed.
type CollapsedBecause =
  | NoSessionOpen
  | NothingBoundYet
  | NoAppOutputYet

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
    | NoAppOutputYet -> "no app output yet: start an app (run_app) and its stdout and stderr show here"

  let pin (why: CollapsedBecause) : PinOffer =
    match why with
    | NoSessionOpen -> PinNotOffered
    | NothingBoundYet -> PinOffered
    | NoAppOutputYet -> PinOffered

module OpenBecause =
  /// What the open pane's header says about itself.
  let text (because: OpenBecause) : string =
    match because with
    | SessionHasBindings 1 -> "1 binding"
    | SessionHasBindings count -> sprintf "%d bindings" count
    | SessionProducedOutput 1 -> "1 line of app output"
    | SessionProducedOutput count -> sprintf "%d lines of app output" count
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
    | DockPane.AppOutput ->
      // Same shape as the bindings rule above it, on its own fact: open when the app has actually
      // written something, otherwise collapsed to one line that says how to make it write, and
      // pinnable so a pane you are about to use is not something you have to feed first. The
      // presence case is `NoAppOutput`, not `NoAppOutputYet` — that name belongs to
      // `CollapsedBecause` and two cases with one name resolved to whichever was defined last.
      match facts.Session, facts.AppOutput, facts.Pin with
      | NoSessionInView, _, _ -> PaneCollapsed NoSessionOpen
      | SessionInView, HasAppOutput count, _ -> PaneOpen(SessionProducedOutput count)
      | SessionInView, NoAppOutput, Pinned -> PaneOpen PinnedByUser
      | SessionInView, NoAppOutput, Unpinned -> PaneCollapsed NoAppOutputYet

  /// The decision for every pane, in the dock's order.
  let all (facts: PaneFacts) : (DockPane * PaneState) list =
    DockPane.all |> List.map (fun pane -> pane, decide facts pane)
