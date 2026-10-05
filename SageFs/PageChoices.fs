namespace SageFs

open System
open System.Collections.Concurrent

/// The choices a browser page made when it loaded (`?panels=friction`, a sort
/// order), kept for it by its page id.
///
/// They used to live in a dictionary that the page's stream connection cleared
/// when it closed. Datastar reconnects to the same URL after any drop, and the
/// new connection can start before the old one's cleanup runs, so the cleanup
/// deleted the choice the new connection needed, and the page lost the friction
/// panel it had asked for. A page's choices belong to the PAGE, not to whichever
/// connection happens to be open, so this type has no way to remove one.
///
/// It is bounded instead: past `capacity` pages, the oldest page's choice is
/// evicted, so a daemon that runs for weeks cannot grow it without limit.
type PageChoices<'v>(capacity: int) =
  do
    match capacity > 0 with
    | true -> ()
    | false -> invalidArg (nameof capacity) "a store that can hold nothing evicts every choice as it is made"

  let values = ConcurrentDictionary<string, 'v>()
  let arrival = ConcurrentQueue<string>()

  /// A page seen for the first time joins the eviction order; past `capacity` the oldest page is evicted.
  let admit (page: string) =
    arrival.Enqueue page
    while values.Count > capacity do
      match arrival.TryDequeue() with
      | true, oldest -> values.TryRemove oldest |> ignore
      | false, _ -> ()

  /// How many pages currently have a choice held.
  member _.Count = values.Count

  /// Record `page`'s choice, replacing an earlier one. A page seen for the first
  /// time joins the eviction order; past `capacity` the oldest page is evicted.
  member _.Set(page: string, value: 'v) : unit =
    match values.TryAdd(page, value) with
    | true -> admit page
    | false -> values.[page] <- value

  /// Change `page`'s choice with `change`, starting from `whenAbsent` for a page that never chose, and give back
  /// what it became. Atomic: two changes arriving on two threads are both applied, one after the other, so a
  /// handler that reads a choice and writes the next one cannot lose another handler's write. `change` runs
  /// again if another thread got in first, so it must be pure.
  member _.Update(page: string, whenAbsent: 'v, change: 'v -> 'v) : 'v =
    let rec attempt () =
      match values.TryGetValue page with
      | true, current ->
        let next = change current
        match values.TryUpdate(page, next, current) with
        | true -> next
        | false -> attempt ()
      | false, _ ->
        let next = change whenAbsent
        match values.TryAdd(page, next) with
        | true ->
          admit page
          next
        | false -> attempt ()
    attempt ()

  /// The page's choice, or `whenAbsent` for a page that never made one.
  member _.Find(page: string, whenAbsent: 'v) : 'v =
    match values.TryGetValue page with
    | true, value -> value
    | false, _ -> whenAbsent
