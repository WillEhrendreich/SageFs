/// What a page chose for the Live Bindings pane has one owner and one lock, and nothing about a session's truth is in it: an absent entry,
/// an evicted one and a page with no id all read the default.
module SageFs.Tests.PageStateTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.Tweak.RowFormatting
open SageFs.Server.PageState

/// One thing a page does.
type private Choice =
  | OpenTab of WatchTab
  | Pick of row: string * FormatChoice

let private apply (store: PageStore) (page: string) (choice: Choice) : unit =
  match choice with
  | OpenTab tab -> store.SetTab(page, tab)
  | Pick(row, format) -> store.SetFormat(page, row, format)

/// The pure model of what a run of choices leaves.
let private model (choices: Choice list) : PageLook =
  choices
  |> List.fold
    (fun look choice ->
      match choice with
      | OpenTab tab -> { look with Tab = tab }
      | Pick(row, format) -> { look with Formats = Map.add row format look.Formats })
    PageLook.onAll

let private tabs = Array.ofList WatchTab.all

let private formats = Array.ofList FormatChoice.all

let private choiceOf (kind: byte, which: byte) : Choice =
  match int kind % 2 with
  | 0 -> OpenTab tabs.[int which % tabs.Length]
  | _ -> Pick(sprintf "row-%d" (int which % 5), formats.[int which % formats.Length])

[<Tests>]
let tests =
  testList "page state" [

    testCase "WHY — a page that chose nothing, a page with no id and a page the store never saw all read the default" <| fun _ ->
      let store = PageStore(8)
      store.Look "never-seen" |> Expect.equal "never seen" PageLook.onAll
      store.Look "" |> Expect.equal "no id" PageLook.onAll
      store.Look null |> Expect.equal "null id" PageLook.onAll
      store.SetTab("", WatchTab.Watch)
      store.SetTab(null, WatchTab.Watch)
      store.Count |> Expect.equal "a choice with no page to keep it for is dropped" 0
      store.Look "" |> Expect.equal "and still reads the default" PageLook.onAll

    testCase "a tab and a row's format are kept side by side, each page its own, and a page that reloads (a new id) starts over" <| fun _ ->
      let store = PageStore(8)
      store.SetTab("a", WatchTab.Changes)
      store.SetFormat("a", "flags", FormatChoice.Hex)
      store.SetFormat("b", "flags", FormatChoice.Binary)
      store.Look "a" |> Expect.equal "a keeps both" { Tab = WatchTab.Changes; Formats = Map.ofList [ "flags", FormatChoice.Hex ] }
      store.Look "b" |> Expect.equal "b has its own" { Tab = WatchTab.All; Formats = Map.ofList [ "flags", FormatChoice.Binary ] }
      store.Look "a-after-reload" |> Expect.equal "a new id starts at the default" PageLook.onAll

    testProperty "WHY — over any run of choices one page makes, the store holds what the model holds, tab and rows together" <| fun (steps: (byte * byte) list) ->
      let store = PageStore(8)
      let choices = steps |> List.map choiceOf
      choices |> List.iter (apply store "page")
      store.Look "page" = model choices

    testCase "WHY — two choices that arrive at once are both kept: a tab change cannot lose a format change, which two caches written separately could" <| fun _ ->
      let store = PageStore(8)
      let rows = [ 0 .. 199 ]
      let setTabs = Task.Run(fun () -> for i in rows do store.SetTab("page", tabs.[i % tabs.Length]))
      let setFormats = Task.Run(fun () -> for i in rows do store.SetFormat("page", sprintf "row-%d" i, FormatChoice.Hex))
      Task.WaitAll(setTabs, setFormats)
      let look = store.Look "page"
      look.Formats.Count |> Expect.equal "every row's format is there" rows.Length
      look.Tab |> Expect.equal "and the last tab" tabs.[(rows.Length - 1) % tabs.Length]

    testCase "WHY — the store is bounded: past its capacity the page seen longest ago goes, and reads the default afterwards" <| fun _ ->
      let store = PageStore(3)
      for page in [ "p1"; "p2"; "p3"; "p4"; "p5" ] do
        store.SetTab(page, WatchTab.Pinned)
      store.Count |> Expect.equal "never more than its capacity" 3
      store.Look "p1" |> Expect.equal "the oldest was evicted and reads the default" PageLook.onAll
      store.Look "p2" |> Expect.equal "so was the next" PageLook.onAll
      store.Look "p5" |> Expect.equal "the newest is kept" { PageLook.onAll with Tab = WatchTab.Pinned }
      store.SetFormat("p5", "r", FormatChoice.Hex)
      store.Count |> Expect.equal "a page that is already held does not push another out" 3
  ]
