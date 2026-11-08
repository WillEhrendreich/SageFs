/// What a browser page chose for how the Live Bindings pane looks: the tab it has open and how it shows each row. This is the one owner
/// of those choices, with one lock. A choice is a convenience of the page that made it and nothing about a session's truth lives here: a
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
  let gate = obj ()
  let looks = Dictionary<string, PageLook>()
  let arrival = Queue<string>()

  /// What the page chose, or the default.
  member _.Look(clientId: string) : PageLook = PageLook.onAll

  /// The page opened this tab.
  member _.SetTab(clientId: string, tab: WatchTab) : unit = ()

  /// The page chose this format for this row.
  member _.SetFormat(clientId: string, row: string, choice: FormatChoice) : unit = ()

  /// How many pages have a choice held.
  member _.Count : int = 0
