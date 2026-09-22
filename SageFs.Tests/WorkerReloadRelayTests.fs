/// The pure half of the daemon's reload-stream relay: given what it's
/// listening to and where the session's worker is, what does it do? The
/// outcome half (an open dashboard sees a save's kept-state notice without a
/// reload) is the Reset journey in HotReloadBrowserTests.
module SageFs.Tests.WorkerReloadRelayTests

open Expecto
open Expecto.Flip
open SageFs.Server.WorkerReloadRelay

[<Tests>]
let tests =
  testList "WorkerReloadRelay.decide" [
    testCase "nothing held and no worker means nothing to do" <| fun _ ->
      decide Held.NotListening Worker.NoWorker
      |> Expect.equal "no worker, nothing to listen to" Step.Nothing

    testProperty "a worker nobody's listening to gets listened to" <| fun (url: string) ->
      decide Held.NotListening (Worker.At url)
      |> Expect.equal "start listening to the worker the snapshot names" (Step.Listen url)

    testProperty "the same worker is kept, not reconnected" <| fun (url: string) ->
      decide (Held.ListeningTo url) (Worker.At url)
      |> Expect.equal "reconnecting would drop events in between" Step.Keep

    testProperty "a session with a new worker moves to it" <| fun (old: string) (fresh: string) ->
      match old = fresh with
      | true -> ()
      | false ->
        decide (Held.ListeningTo old) (Worker.At fresh)
        |> Expect.equal "a restart or hard reset gives the session a new worker URL, and the old stream is dead weight" (Step.Move fresh)

    testProperty "a worker that went away stops the listening" <| fun (url: string) ->
      decide (Held.ListeningTo url) Worker.NoWorker
      |> Expect.equal "nothing left to listen to" Step.Stop

    testProperty "the snapshot's option turns into a worker or no worker, nothing else" <| fun (url: string option) ->
      let expected =
        match url with
        | Some u -> Worker.At u
        | None -> Worker.NoWorker
      Worker.ofLookup url |> Expect.equal "the lookup means exactly what it says" expected
  ]
