/// What a browser page chose for how the Live Bindings pane looks: the tab it has open and how it shows each row. This is the one owner
/// of those choices, atomic per change. A choice is a convenience of the page that made it and nothing about a session's truth lives here: a
/// page that has chosen nothing, a page whose entry was evicted and a page with no id all read the default, so a reader never needs to
/// know whether an entry exists. A page that reloads has a new client id and starts at the default.
module SageFs.Server.PageState

open System.Collections.Generic
open SageFs
open SageFs.Features.Tweak.RowFormatting

/// What a page chose beside what the workflow gives: the tab it has open and how each of its rows is shown, by row.
type PageLook = { Tab: WatchTab; Formats: Map<string, FormatChoice> }

module PageLook =
  /// A page that has chosen nothing: the All tab and every row as it is.
  let onAll : PageLook = { Tab = WatchTab.All; Formats = Map.empty }

/// How many pages' choices are kept at once. The page seen longest ago goes first.
let [<Literal>] PageCapacity = 256

/// The choices of the pages that have made any, bounded.
type PageStore(capacity: int) =
  // The bounded, evicting, never-removing store of a page's choice is `PageChoices`, so this type keeps one copy of that rule
  // (and of the refusal of a capacity that could hold nothing) and adds only what a page's look is.
  let looks = PageChoices<PageLook>(capacity)

  /// Change what a page chose. A page with no id has nowhere to keep it, so the change is dropped. A page seen for the first time joins the
  /// eviction order, and past `capacity` the page seen longest ago goes. Two changes arriving at once are both applied, one after the other.
  let change (clientId: string) (apply: PageLook -> PageLook) : unit =
    match clientId with
    | null
    | "" -> ()
    | id -> looks.Update(id, PageLook.onAll, apply) |> ignore

  /// What the page chose, or the default.
  member _.Look(clientId: string) : PageLook =
    match clientId with
    | null
    | "" -> PageLook.onAll
    | id -> looks.Find(id, PageLook.onAll)

  /// The page opened this tab.
  member _.SetTab(clientId: string, tab: WatchTab) : unit = change clientId (fun look -> { look with Tab = tab })

  /// The page chose this format for this row.
  member _.SetFormat(clientId: string, row: string, choice: FormatChoice) : unit =
    change clientId (fun look -> { look with Formats = Map.add row choice look.Formats })

  /// How many pages have a choice held.
  member _.Count : int = looks.Count
