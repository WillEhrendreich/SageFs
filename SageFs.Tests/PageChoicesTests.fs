/// A dashboard page makes choices when it loads (`?panels=friction`, a sort
/// order) that the server keeps for it. They used to be dropped when the page's
/// stream connection closed. Datastar reconnects to the same URL after any drop,
/// and a reconnect can start before the old connection's cleanup runs, so the
/// cleanup deleted the choice the new connection needed. In the gate that made
/// the friction panel vanish from a page that had asked for it. These pin that a
/// choice is never dropped because a connection went away.
module SageFs.Tests.PageChoicesTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs

[<Tests>]
let pageChoicesTests =
  testList "PageChoices" [

    testCase "WHY — a choice a page made is found again, because the stream reads it on every push" <| fun _ ->
      let choices = PageChoices<string>(8)
      choices.Set("page-a", "friction")
      choices.Find("page-a", "default") |> Expect.equal "the page's own choice" "friction"

    testCase "WHY — a page that never chose gets the caller's default, not an error, because most pages ask for nothing" <| fun _ ->
      let choices = PageChoices<string>(8)
      choices.Find("never-seen", "default") |> Expect.equal "the default" "default"

    testCase "WHY — choosing again replaces the earlier choice, because the newest is what the page asked for" <| fun _ ->
      let choices = PageChoices<string>(8)
      choices.Set("page-a", "recent")
      choices.Set("page-a", "oldest")
      choices.Find("page-a", "default") |> Expect.equal "the latest choice wins" "oldest"

    testCase "WHY — there is no way to remove a page's choice, because a closing connection must not be able to delete what its replacement needs" <| fun _ ->
      // The type has no Remove. Asserted on the members, so adding one is a
      // deliberate act that has to change this test.
      typeof<PageChoices<string>>.GetMethods()
      |> Array.map _.Name
      |> Array.filter (fun name -> name.StartsWith "Remove" || name.StartsWith "TryRemove" || name.StartsWith "Clear" || name.StartsWith "Delete")
      |> Expect.isEmpty "no member removes a choice"

    testCase "WHY — past its capacity the OLDEST page's choice is evicted first, because the store must stay bounded for a daemon that runs for weeks" <| fun _ ->
      let choices = PageChoices<int>(3)
      for i in 1 .. 5 do choices.Set(sprintf "page-%d" i, i)
      choices.Count |> Expect.equal "never more than the capacity" 3
      choices.Find("page-1", -1) |> Expect.equal "the oldest is gone" -1
      choices.Find("page-2", -1) |> Expect.equal "the next oldest is gone" -1
      choices.Find("page-5", -1) |> Expect.equal "the newest is kept" 5

    testCase "WHY — a capacity that could hold nothing is refused at construction, because a store that evicts every choice as it is made would look like it works" <| fun _ ->
      Expect.throws "zero" (fun () -> PageChoices<int>(0) |> ignore)
      Expect.throws "negative" (fun () -> PageChoices<int>(-4) |> ignore)

    testProperty "WHY — however many pages load, the store never holds more than its capacity and always holds the most recent one" <| fun (pages: NonEmptyArray<NonEmptyString>) ->
      let capacity = 5
      let choices = PageChoices<int>(capacity)
      let ids = pages.Get |> Array.map _.Get |> Array.distinct
      ids |> Array.iteri (fun i id -> choices.Set(id, i))
      let last = Array.last ids
      choices.Count <= capacity && choices.Find(last, -1) = (ids.Length - 1)
  ]
