// WHY — a `test_results_batch` frame carries a tally and no list, so the editor fetches the list and
// delivers the same frame again with it. A reconnect can replay several such frames in a row, each
// firing its own fetch, and the responses land in any order. The one that landed last decided the
// generation, freshness and completion the panel showed, which could be an older frame's.
// The gate gives each frame that owes a fetch a ticket per session, and only the newest ticket's
// response may deliver.
//
// Runs under plain `dotnet fsi` (no Fable), like the other contract tests here.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/HydrationGatePure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.HydrationGatePure

let tests =
  testList "VS Code hydration - which fetch answer may deliver" [

    testCase "a frame's own answer delivers when no newer frame has asked since" <| fun _ ->
      let ticket, gate = take "s1" empty
      isCurrent "s1" ticket gate |> Expect.isTrue "the only ticket is the newest"

    testCase "WHY - an older frame's answer is dropped once a newer frame has asked" <| fun _ ->
      let first, gate = take "s1" empty
      let _, gate = take "s1" gate
      isCurrent "s1" first gate |> Expect.isFalse "the second ask supersedes the first"

    testCase "the newest of several asks is the one that delivers" <| fun _ ->
      let _, gate = take "s1" empty
      let _, gate = take "s1" gate
      let newest, gate = take "s1" gate
      isCurrent "s1" newest gate |> Expect.isTrue "the last ticket taken"

    testCase "WHY - another session's ask does not supersede this session's" <| fun _ ->
      let mine, gate = take "s1" empty
      let _, gate = take "s2" gate
      isCurrent "s1" mine gate |> Expect.isTrue "tickets are per session"

    testCase "a ticket for a session that never asked is not current" <| fun _ ->
      isCurrent "s1" 1 empty |> Expect.isFalse "nothing was taken"

    testCase "tickets only grow, so an answer from before a reset can never match a later ask" <| fun _ ->
      let old, gate = take "s1" empty
      let later, gate = take "s1" gate
      Expect.isTrue "later is greater" (later > old)
      isCurrent "s1" old gate |> Expect.isFalse "old stays dead"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
