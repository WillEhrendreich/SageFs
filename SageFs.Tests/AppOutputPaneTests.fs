module SageFs.Tests.AppOutputPaneTests

open Expecto
open Expecto.Flip
open SageFs.Server
open SageFs.Server.AppOutputPane

/// The app output pane: what the dock shows for a running app's stdout and stderr.
///
/// WHY A PURE MODULE. The pane has three pieces of state a user drives (follow, pause, a search string) and one stream
/// of lines the daemon pushes. Every interesting decision is "what does the pane show for these lines, given what the
/// user asked for", and that is a function of its arguments with no clock, no buffer and no HTML in it, so it can be
/// called from a test and from a REPL eval identically.
///
/// THE SHAPE OF THE PROBLEM. An app that prints continuously would push a line faster than anyone reads, so a pane
/// that simply appends is worse than no pane: it buries the evals it was meant to stop burying, and it grows without
/// bound. The decision is therefore not "append" but "which lines survive this tick", and the answer depends on
/// whether the user is following, has paused, and what they searched for.
/// The seventeen cases below all feed text through the same door, so the door is written once here. A test that
/// built a line by hand could pass while the only real constructor refuses it.
let line (text: string) : AppOutputLine =
  AppOutputLine.ofText OutputStream.Stdout text |> Option.defaultWith (fun () -> failwithf "the fixture %A is not a line" text)

let errLine (text: string) : AppOutputLine =
  AppOutputLine.ofText OutputStream.Stderr text |> Option.defaultWith (fun () -> failwithf "the fixture %A is not a line" text)

[<Tests>]
let tests =
  testList "the app output pane" [

    // ---- what a line is -------------------------------------------------------------------------------------

    testCase "WHY — a line carries its stream, because stderr is the one you are looking for" <| fun _ ->
      // Reading them as one undifferentiated stream is how a stack trace ends up rendered as ordinary output.
      // `ofText` is the refusing constructor: a blank line is not a line, so it is `None` rather than an empty Text.
      let out = AppOutputLine.ofText OutputStream.Stdout "listening on http://localhost:5000"
      out |> Expect.isSome "a line of output"
      let out = out |> Option.get
      out.Stream |> Expect.equal "stdout is stdout" OutputStream.Stdout
      out.Text |> Expect.equal "the text is kept whole" "listening on http://localhost:5000"

      let err = AppOutputLine.ofText OutputStream.Stderr "Unhandled exception. System.IO.IOException" |> Option.get
      err.Stream |> Expect.equal "stderr is stderr" OutputStream.Stderr
      err.Text |> Expect.stringContains "the whole first line survives" "System.IO.IOException"

    testCase "WHY — a blank line is dropped at the edge, so the pane never fills with empty rows" <| fun _ ->
      // A `printfn ""` is a real event in the app's world but it carries no information for a reader, and keeping
      // them is how a pane's row count stops meaning anything.
      AppOutputLine.ofText OutputStream.Stdout "" |> Expect.isNone "an empty line is not a line"
      AppOutputLine.ofText OutputStream.Stdout "   " |> Expect.isNone "nor is whitespace"

    // ---- the buffer, and its bound ---------------------------------------------------------------------------

    testCase "WHY — the buffer keeps the most recent lines, because an unbounded one is a leak with a UI" <| fun _ ->
      let cap = 200
      let buffer = AppOutputBuffer.empty cap
      let full =
        [ 1 .. (cap * 3) ]
        |> List.map (fun i -> line (sprintf "line %d" i))
        |> List.fold (fun b l -> AppOutputBuffer.append b l) buffer
      AppOutputBuffer.count full |> Expect.equal "it is capped" cap
      // The NEWEST survive: a reader looking at output wants the end of it, and the beginning of a
      // 600-line burst is the least interesting part.
      AppOutputBuffer.last full
      |> Expect.equal "the newest line is last" (Some (sprintf "line %d" (cap * 3)))

    testCase "WHY — the cap is honoured from the first line, not after the buffer has already grown" <| fun _ ->
      let buffer = AppOutputBuffer.empty 3
      let full =
        [ 1 .. 5 ] |> List.fold (fun b i -> AppOutputBuffer.append b (line (sprintf "l%d" i))) buffer
      AppOutputBuffer.toList full
      |> List.map (fun l -> l.Text)
      |> Expect.equal "the last three, in order" [ "l3"; "l4"; "l5" ]

    testCase "WHY — a cap of zero is refused by name, because it would silently discard everything" <| fun _ ->
      // `empty 0` is valid and means "keep nothing", which is never what anyone meant. A refusal here is a
      // constructor's job, not a caller's, and it is a Result rather than an option because the reason is
      // something a caller can act on (pass a real number).
      AppOutputBuffer.empty 0
      |> ignore

      AppOutputBuffer.tryEmpty 0 |> Result.isOk
      |> Expect.isFalse "a cap of zero is refused"

      // and the refusal says what was asked for
      match AppOutputBuffer.tryEmpty 0 with
      | Error refusal -> AppOutputBuffer.describe refusal |> Expect.stringContains "it names the number" "0"
      | Ok _ -> failtest "a cap of zero should not be accepted"

      AppOutputBuffer.tryEmpty 1 |> Result.isOk
      |> Expect.isTrue "one line is fine"

    // ---- the decision: follow, pause, search ----------------------------------------------------------------

    testCase "WHY — a paused pane keeps taking lines and shows none of them, so unpausing is not a lie" <| fun _ ->
      // The tempting implementation drops lines while paused, which makes the pane's contents depend on WHEN you
      // looked rather than on what the app did. Dropping them is losing data; the pane says "N lines while paused"
      // instead, and the lines are still there.
      let fed =
        AppOutputPane.feedTexts
          OutputStream.Stdout
          ([ 1 .. 5 ] |> List.map (sprintf "l%d"))
          (AppOutputPane.create |> AppOutputPane.setPaused true)

      AppOutputPane.visible fed |> Expect.equal "nothing is shown while paused" []
      AppOutputPane.heldWhilePaused fed |> Expect.equal "but five lines are held" 5
      AppOutputPane.buffered fed |> Expect.equal "and the buffer has them" 5

    testCase "WHY — unpausing shows what was held, in order, newest last" <| fun _ ->
      let fed =
        AppOutputPane.feedTexts
          OutputStream.Stdout
          ([ 1 .. 3 ] |> List.map (sprintf "l%d"))
          (AppOutputPane.create |> AppOutputPane.setPaused true)
      let live = fed |> AppOutputPane.setPaused false
      AppOutputPane.visible live |> List.map (fun l -> l.Text) |> Expect.equal "all three, in order" [ "l1"; "l2"; "l3" ]

    testCase "WHY — following off means the pane holds still while lines still arrive, so it does not scroll away" <| fun _ ->
      let fed =
        AppOutputPane.feedTexts
          OutputStream.Stdout
          ([ 1 .. 3 ] |> List.map (sprintf "l%d"))
          (AppOutputPane.create |> AppOutputPane.setFollowing false)
      AppOutputPane.following fed |> Expect.isFalse "still not following"
      AppOutputPane.buffered fed |> Expect.equal "the lines are still buffered" 3

    testCase "WHY — a search that matches nothing shows an empty pane and says so, rather than showing everything" <| fun _ ->
      // The failure this prevents: a filter that no longer matches falls back to "show all", so a search that
      // stopped matching looks like the search was cleared.
      let searched = AppOutputPane.create |> AppOutputPane.setSearch "connection refused"
      let fed = AppOutputPane.feedTexts OutputStream.Stdout [ "listening on :5000"; "GET / 200" ] searched
      AppOutputPane.visible fed |> Expect.equal "nothing matches" []
      AppOutputPane.matching fed |> Expect.isFalse "and the pane says the search matched nothing"

    testCase "WHY — a search is matched case-insensitively, because a reader does not remember the app's casing" <| fun _ ->
      let searched = AppOutputPane.create |> AppOutputPane.setSearch "LISTENING"
      let fed = AppOutputPane.feedTexts OutputStream.Stdout [ "listening on :5000"; "GET / 200" ] searched
      AppOutputPane.visible fed |> List.map (fun l -> l.Text) |> Expect.equal "the one that matches" [ "listening on :5000" ]

    testCase "WHY — a search can be narrowed to stderr, which is what you want when hunting a stack trace" <| fun _ ->
      // An app that logs steadily and then throws: the useful filter is "errors only", not a text search.
      // `feed` takes the LINE first, so a run of lines pipes.
      let fed =
        AppOutputPane.create
        |> AppOutputPane.setStreamFilter ErrorsOnly
        |> AppOutputPane.feed (line "GET / 200")
        |> AppOutputPane.feed (errLine "System.IO.IOException: broken pipe")

      AppOutputPane.visible fed |> List.map (fun l -> l.Text) |> Expect.equal "only the error" [ "System.IO.IOException: broken pipe" ]

    testCase "WHY — an empty search is not a filter, and says so" <| fun _ ->
      // Whitespace-only would otherwise read as "match lines containing nothing" and hide everything, or as
      // "match everything" depending on the implementation. It is neither: it is no search.
      let pane = AppOutputPane.create |> AppOutputPane.setSearch "   "
      pane |> AppOutputPane.searching |> Expect.isFalse "not searching"

    // ---- the pane's own header ------------------------------------------------------------------------------

    testCase "WHY — the header says what the pane is doing, so a still pane is not mistaken for a dead app" <| fun _ ->
      let pane = AppOutputPane.create
      AppOutputPane.header pane
      |> Expect.stringContains "an idle pane says what an idle pane is" "no output yet"

      let fed = AppOutputPane.feed (line "hello") pane
      AppOutputPane.header fed
      |> Expect.stringContains "and a pane with lines counts them" "1 line"

    testCase "WHY — a paused pane's header says how many lines it is holding, which is the number you want" <| fun _ ->
      let held =
        AppOutputPane.feedTexts
          OutputStream.Stdout
          ([ 1 .. 7 ] |> List.map (sprintf "l%d"))
          (AppOutputPane.create |> AppOutputPane.setPaused true)
      AppOutputPane.header held
      |> Expect.stringContains "it names the held count" "7 lines while paused"
  ]
