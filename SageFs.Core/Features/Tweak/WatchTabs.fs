/// The built-in tabs of the live bindings pane (docs/plans/live-bindings-watch-view.md, step 7): All, Pinned and Changes. A tab is a
/// question put to each top-level binding, "is this one listed here?", and the answer is a pure function of three facts about the
/// binding: whether it is pinned, whether it holds a tweak or a value the program changed, and which tab is open. The pane asks it
/// for every row, so the tab bar and the rows cannot disagree.
module SageFs.Features.Tweak.WatchTabs

open SageFs
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.ChangesStrip

[<RequireQualifiedAccess>]
type WatchTab =
  /// Every binding.
  | All
  /// The bindings the person pinned.
  | Pinned
  /// The bindings that hold a tweak, or a value the program changed.
  | Changes

module WatchTab =
  let all : WatchTab list = [ WatchTab.All; WatchTab.Pinned; WatchTab.Changes ]

  /// The word a page stores for the open tab, and the one place it is read back.
  let token (tab: WatchTab) : string =
    match tab with
    | WatchTab.All -> "all"
    | WatchTab.Pinned -> "pinned"
    | WatchTab.Changes -> "changes"

  let label (tab: WatchTab) : string =
    match tab with
    | WatchTab.All -> "All"
    | WatchTab.Pinned -> "Pinned"
    | WatchTab.Changes -> "Changes"

  let ofToken (text: string) : Result<WatchTab, ConfigError> =
    match all |> List.tryFind (fun tab -> token tab = text) with
    | Some tab -> Ok tab
    | None -> Error(NotAMember(text, all |> List.map token))

  /// What the tab says when nothing is listed on it, so an empty tab is never a blank pane.
  let emptyWords (tab: WatchTab) : string =
    match tab with
    | WatchTab.All -> "No values are bound in this session yet."
    | WatchTab.Pinned -> "Nothing is pinned. Pin a value with the pin at the end of its row."
    | WatchTab.Changes -> "Nothing has changed: no value was tried and not saved, and the program has not changed one."

/// Whether a binding is listed on a tab.
let lists (tab: WatchTab) (pinned: bool) (changed: bool) : bool =
  match tab with
  | WatchTab.All -> true
  | WatchTab.Pinned -> pinned
  | WatchTab.Changes -> changed

/// `lists` as an expression the page evaluates: the same three cases, with the pin and the change given as the page's own expressions.
/// A test puts `true` and `false` where the expressions go and holds the answer to `lists`, so the page cannot list a binding the
/// pane's model does not.
let listsJs (tab: WatchTab) (pinnedJs: string) (changedJs: string) : string =
  match tab with
  | WatchTab.All -> "true"
  | WatchTab.Pinned -> pinnedJs
  | WatchTab.Changes -> changedJs

/// The names of the top-level bindings that hold a tweak or a value the program changed, whether the row is the binding itself or one
/// of its record fields.
let changedBindings (changes: Changes) : Set<string> =
  let tweaked = changes.Tweaks |> List.map (fun (t: Tweak) -> t.Row.Binding)
  let evolved = changes.Evolved |> List.map (fun (e: Evolution) -> e.Row.Binding)
  Set.ofList (tweaked @ evolved)
