/// The built-in tabs of the live bindings pane (docs/plans/live-bindings-watch-view.md, step 7): All, Pinned and Changes. A tab is a
/// question put to each top-level binding, "is this one listed here?", and the answer is a pure function of three facts about the
/// binding: whether it is pinned, whether it holds a tweak or a value the program changed, and which tab is open. The pane asks it
/// for every row, so the tab bar and the rows cannot disagree.
module SageFs.Features.Tweak.WatchTabs

open SageFs
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.ChangesStrip

/// Whether a binding is listed on a tab. (The tab type is in `ViewSettings`, beside the workflow a view's settings are keyed by.)
let lists (tab: WatchTab) (pinned: bool) (changed: bool) : bool =
  match tab with
  | WatchTab.All -> true
  | WatchTab.Pinned -> pinned
  | WatchTab.Changes -> changed
  // the Watch tab lists expressions (`WatchRows`), not bindings
  | WatchTab.Watch -> false

/// `lists` as an expression the page evaluates: the same three cases, with the pin and the change given as the page's own expressions.
/// A test puts `true` and `false` where the expressions go and holds the answer to `lists`, so the page cannot list a binding the
/// pane's model does not.
let listsJs (tab: WatchTab) (pinnedJs: string) (changedJs: string) : string =
  match tab with
  | WatchTab.All -> "true"
  | WatchTab.Pinned -> pinnedJs
  | WatchTab.Changes -> changedJs
  | WatchTab.Watch -> "false"

/// The names of the top-level bindings that hold a tweak or a value the program changed, whether the row is the binding itself or one
/// of its record fields.
let changedBindings (changes: Changes) : Set<string> =
  let tweaked = changes.Tweaks |> List.map (fun (t: Tweak) -> t.Row.Binding)
  let evolved = changes.Evolved |> List.map (fun (e: Evolution) -> e.Row.Binding)
  Set.ofList (tweaked @ evolved)
